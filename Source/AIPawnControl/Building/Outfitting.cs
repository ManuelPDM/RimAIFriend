using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// A finished room comes working (STREAMLINE.md §7): each work table in it gets one default bill with code's count, and
    /// a storeroom gets a stockpile on its free floor. Runs once per project, as its designer's chores.
    /// </summary>
    public static class Outfitting
    {
        private static readonly WorkOrders.Goal[] BillOrder = { WorkOrders.Goal.Meal, WorkOrders.Goal.Butcher, WorkOrders.Goal.Blocks, WorkOrders.Goal.Clothes };

        public static void Outfit(BuildProject project)
        {
            Room room = project.Room;
            Pawn pawn = project.pawn;
            if (room == null || pawn == null || pawn.Map != project.map || ChoreManager.Instance == null)
                return;
            int colonists = Math.Max(1, project.map.mapPawns.FreeColonistsSpawnedCount);
            var results = new List<string>();
            // A heater is there for its heat: a campfire gets no bill (nobody cooks in the barracks).
            var tables = room.ContainedAndAdjacentThings.OfType<Building>()
                .Where(b => b is IBillGiver giver && giver.BillStack != null && room.ContainsCell(b.Position) && b.Faction == Faction.OfPlayer && !Needs.Meets(Needs.Heater, b.def, null))
                .Distinct().ToList();
            foreach (var table in tables)
                if (DefaultBill(pawn, table, colonists) is string result)
                    results.Add(result);
            // Stockpiles only in rooms for storage (FURNISHING.md §3; a storeroom, a food store): food piled on a kitchen floor
            // doesn't work. Its shelves take the same things at the same priority (BASE_GROWTH.md §6.3).
            if (project.kindDef?.stores != null)
            {
                var kind = project.kindDef;
                var filter = kind.StoreFilter();
                // An "everything" room keeps its shelves as vanilla made them (Preferred, above the floor stockpile).
                if (!kind.stores.Contains(ThingCategoryDefOf.Root))
                    foreach (var shelf in room.ContainedAndAdjacentThings.OfType<Building_Storage>().Where(s => room.ContainsCell(s.Position)).Distinct())
                    {
                        var settings = shelf.GetStoreSettings();
                        settings.filter.CopyAllowancesFrom(filter);
                        if (kind.storePriority > settings.Priority)
                            settings.Priority = kind.storePriority;
                    }
                string what = kind.stores.Contains(ThingCategoryDefOf.Root) ? "everything" : string.Join(" and ", kind.stores.Select(s => s.label));
                if (RoomStockpile(pawn, room, project.Kind, filter, kind.storePriority, what) is string pile)
                    results.Add(pile);
            }
            // A throne is its owner's (a title's throne room, BASE_GROWTH.md §6.6).
            if (project.occupant != null && room.ContainedAndAdjacentThings.OfType<Building_Throne>().FirstOrDefault() is Building_Throne throne
                && project.occupant.ownership?.AssignedThrone == null && project.occupant.ownership.ClaimThrone(throne))
                results.Add($"The throne is {project.occupant.LabelShort}'s.");
            // Coolers and heaters hold the kind's temperature (a freezer, BASE_GROWTH.md §6.3).
            if (project.kindDef != null && !float.IsNaN(project.kindDef.holdTemperature))
                foreach (var control in room.ContainedAndAdjacentThings.Where(t => Needs.Controls(t, room)).Select(t => t.TryGetComp<CompTempControl>()).Where(c => c != null).Distinct())
                {
                    control.targetTemperature = project.kindDef.holdTemperature;
                    results.Add($"Set the {control.parent.def.label} to {project.kindDef.holdTemperature.ToStringTemperature("F0")}.");
                }
            ModLog.Message($"Outfitted {pawn.LabelShort}'s {project.Kind}: {(results.Count > 0 ? string.Join(" ", results) : "nothing to add")}");
        }

        /// <summary>The table's first recipe by goal (meals, butchering, stone blocks, clothes) that has no bill yet; none for other tables.</summary>
        private static string DefaultBill(Pawn pawn, Building table, int colonists)
        {
            foreach (var goal in BillOrder)
            {
                var recipes = table.def.AllRecipes.Where(r => WorkOrders.GoalOf(r) == goal && r.AvailableNow).ToList();
                RecipeDef recipe = goal == WorkOrders.Goal.Meal
                    ? recipes.FirstOrDefault(r => r.defName == "CookMealSimple") ?? recipes.OrderBy(r => r.workAmount).FirstOrDefault()
                    : goal == WorkOrders.Goal.Clothes
                        ? recipes.Where(r => r.ProducedThingDef.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.Torso)).OrderBy(r => r.workAmount).FirstOrDefault()
                        : recipes.FirstOrDefault();
                if (recipe == null || WorkOrders.Check(table, recipe, ingredients: false) != null)
                    continue;
                switch (goal)
                {
                    case WorkOrders.Goal.Meal:
                        int meals = colonists * 4; // about two days: simple meals spoil in a few
                        return WorkOrders.AddBill(pawn, table, recipe, meals, $"until there are {meals}", ingredients: false);
                    case WorkOrders.Goal.Butcher:
                        return WorkOrders.AddBill(pawn, table, recipe, 0, "whenever there are corpses", ingredients: false);
                    case WorkOrders.Goal.Blocks:
                        return WorkOrders.AddBill(pawn, table, recipe, 100, "until there are 100", ingredients: false);
                    default:
                        return WorkOrders.AddBill(pawn, table, recipe, colonists, $"until there are {colonists}", ingredients: false);
                }
            }
            return null;
        }

        /// <summary>A stockpile on the room's free floor: no furniture, no work spot, not the cell inside a door, no zone yet.</summary>
        private static string RoomStockpile(Pawn pawn, Room room, string kind, ThingFilter filter, StoragePriority priority, string what)
        {
            Map map = room.Map;
            var blocked = new HashSet<IntVec3>();
            foreach (var b in room.ContainedAndAdjacentThings.OfType<Building>())
            {
                if (b.def.hasInteractionCell)
                    blocked.Add(b.InteractionCell);
                if (b is Building_Door)
                    foreach (var c in GenAdj.CellsAdjacentCardinal(b))
                        blocked.Add(c);
            }
            var cells = room.Cells.Where(c => !blocked.Contains(c) && c.Standable(map) && map.zoneManager.ZoneAt(c) == null
                                              && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Building || t is Blueprint || t is Frame))
                .ToList();
            if (cells.Count == 0)
                return null;
            return Stockpiles.PlaceCells(pawn, cells, $"in the {kind}", $"{cells.Count} cells", filter, priority, what);
        }
    }
}
