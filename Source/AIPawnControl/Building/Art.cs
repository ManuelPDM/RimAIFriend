using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Art for a room (a sculpture, or the ideoligion's styled one), the way vanilla makes it: a bill at the bench that makes
    /// it, then an install blueprint once it exists. The bill runs as her chore and blocks nothing; only the install is a
    /// project (furnishing). Offered only when a bench that makes it stands and someone can do art.
    /// </summary>
    public static class Art
    {
        private static List<ThingDef> pieces;
        private static Dictionary<ThingDef, RecipeDef> recipes;

        /// <summary>Art vanilla makes at a bench and installs, there only to be looked at: sculptures (not a void sculpture, which also links to buildings).</summary>
        public static List<ThingDef> Pieces => pieces ??= DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => IsArt(d) && RecipeFor(d) != null && d.GetCompProperties<CompProperties_Facility>() == null).ToList();

        public static bool IsArt(ThingDef def) => def != null && typeof(Building_Art).IsAssignableFrom(def.thingClass) && def.Minifiable;

        public static RecipeDef RecipeFor(ThingDef art)
        {
            recipes ??= DefDatabase<RecipeDef>.AllDefsListForReading.Where(r => r.ProducedThingDef != null).GroupBy(r => r.ProducedThingDef).ToDictionary(g => g.Key, g => g.First());
            return recipes.TryGetValue(art, out RecipeDef recipe) ? recipe : null;
        }

        /// <summary>The colony's bench that makes it, when its recipe is researched and someone can do art; null otherwise.</summary>
        public static Building Bench(Map map, ThingDef art)
        {
            RecipeDef recipe = RecipeFor(art);
            if (recipe == null || !recipe.AvailableNow || !map.mapPawns.FreeColonistsSpawned.Any(p => !p.WorkTypeIsDisabled(recipe.requiredGiverWorkType ?? WorkTypeDefOf.Crafting)
                                                                                                         && (p.skills == null || recipe.skillRequirements == null || recipe.skillRequirements.All(s => s.PawnSatisfies(p)))))
                return null;
            return map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b is IBillGiver && b.def.AllRecipes.Contains(recipe));
        }

        /// <summary>A piece is on order for this room (or for this ideoligion building), not yet installed.</summary>
        public static bool Ordered(Map map, Room room = null, Precept_Building precept = null) =>
            ChoreManager.Instance?.Active(map).Any(c => c.art != null && (room == null || c.artCell.GetRoom(map) == room) && (precept == null || c.artPrecept == precept)) == true;

        /// <summary>Orders one at the bench (in the given material, in the ideoligion's style if any) and tracks it as her chore.</summary>
        public static string Order(Pawn pawn, ThingDef art, ThingDef stuff, Precept_Building precept, PlanEntry at, string where)
        {
            Map map = pawn.Map;
            Building bench = Bench(map, art);
            if (bench == null)
                return $"Couldn't order a {art.label}: nobody here can make one now.";
            RecipeDef recipe = RecipeFor(art);
            var bill = (Bill_Production)recipe.MakeNewBill(precept); // vanilla's own bill class for it (art is carved over time: an unfinished thing)
            bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
            bill.repeatCount = 1;
            if (stuff != null)
            {
                bill.ingredientFilter.SetDisallowAll();
                bill.ingredientFilter.SetAllow(stuff, true);
            }
            ((IBillGiver)bench).BillStack.AddBill(bill);
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Bill, recipe.label);
            chore.bill = bill;
            chore.art = art;
            chore.artPrecept = precept;
            chore.artCell = at.cell;
            chore.artRot = at.rot;
            chore.artWhere = where;
            string name = precept != null ? precept.LabelCap.ToString() : art.label;
            chore.Remember($"I ordered a {name} at the {bench.def.label} for {where.Replace("in ", "")}.", 4);
            ModLog.Message($"{pawn.LabelShort} ordered a {name} ({stuff?.label ?? "any material"}) at the {bench.def.label}, to go {where}.");
            return $"Ordered a {name} at the {bench.def.label}; it goes {where} once it's made.";
        }

        /// <summary>
        /// Her order was made: the piece gets an install blueprint in its room (its planned spot, or the next free one) and
        /// becomes a furnishing project, and the spent bill goes. False while the piece isn't there yet.
        /// </summary>
        public static bool TryInstall(Chore chore)
        {
            Map map = chore.map;
            var piece = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).OfType<MinifiedThing>()
                .FirstOrDefault(m => m.InnerThing?.def == chore.art && InstallBlueprintUtility.ExistingBlueprintFor(m) == null
                                     && (chore.artPrecept == null || m.InnerThing.StyleSourcePrecept == chore.artPrecept));
            if (piece == null)
                return false;
            Room room = chore.artCell.GetRoom(map);
            if (room == null || !Ground.Indoor(room))
                return true; // the room is gone: the piece stays in storage for the player
            ThingDef def = piece.InnerThing.def;
            PlanEntry at = GenConstruct.CanPlaceBlueprintAt(def, chore.artCell, chore.artRot, map, thing: piece).Accepted
                ? new PlanEntry(def, chore.artCell, chore.artRot)
                : Upgrades.SlotIn(room, chore.pawn, def, piece.InnerThing.Stuff);
            if (at == null)
                return true;
            at.stuff = piece.InnerThing.Stuff;
            GenConstruct.PlaceBlueprintForInstall(piece, at.cell, map, at.rot, Faction.OfPlayer);
            var project = Upgrades.NewProject(chore.pawn, room, chore.artWhere, piece.InnerThing.Stuff);
            project.entries = new List<PlanEntry> { at };
            BuildManager.Instance.Add(project);
            chore.bill.billStack?.Delete(chore.bill);
            ModLog.Message($"{chore.pawn.LabelShort}'s {chore.label} is made: it goes {chore.artWhere}.");
            return true;
        }
    }
}
