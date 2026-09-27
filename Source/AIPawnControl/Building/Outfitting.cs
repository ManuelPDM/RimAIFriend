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
            var tables = room.ContainedAndAdjacentThings.OfType<Building>()
                .Where(b => b is IBillGiver giver && giver.BillStack != null && room.ContainsCell(b.Position) && b.Faction == Faction.OfPlayer)
                .Distinct().ToList();
            foreach (var table in tables)
                if (DefaultBill(pawn, table, colonists) is string result)
                    results.Add(result);
            // Stockpiles only in storerooms (FURNISHING.md §3): food piled on a kitchen floor doesn't work.
            if (project.kindDef?.defName == "AIPC_Storeroom" && RoomStockpile(pawn, room, "everything", project.Kind) is string pile)
                results.Add(pile);
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
                        return WorkOrders.AddBill(pawn, table, recipe, meals, 0, $"until there are {meals}", ingredients: false);
                    case WorkOrders.Goal.Butcher:
                        return WorkOrders.AddBill(pawn, table, recipe, 0, 0, "whenever there are corpses", ingredients: false);
                    case WorkOrders.Goal.Blocks:
                        return WorkOrders.AddBill(pawn, table, recipe, 100, 0, "until there are 100", ingredients: false);
                    default:
                        return WorkOrders.AddBill(pawn, table, recipe, colonists, 0, $"until there are {colonists}", ingredients: false);
                }
            }
            return null;
        }

        /// <summary>A stockpile on the room's free floor: no furniture, no work spot, not the cell inside a door, no zone yet.</summary>
        private static string RoomStockpile(Pawn pawn, Room room, string holds, string kind)
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
            return Stockpiles.PlaceCells(pawn, cells, holds, $"in the {kind}", $"{cells.Count} cells");
        }
    }
}
