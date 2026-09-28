using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// "gather wild food" (added in Phase 5 build step 7): ripe wild plants people can eat (berry bushes, agave), marked
    /// for harvest with the same designation as vanilla's harvest tool. Food before the first crop, where no one can hunt.
    /// </summary>
    public static class WildFood
    {
        public const int MaxPlants = 25;

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            if (!scan.SomeoneCanDo(WorkTypeDefOf.PlantCutting))
                yield break;
            Map map = scan.map;
            var plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .Where(p => scan.WalkAt(p.Position) >= 0 && Check(p, map) == null)
                .OrderBy(p => scan.WalkAt(p.Position))
                .Take(MaxPlants)
                .ToList();
            if (plants.Count < 2)
                yield break;
            var products = plants.Select(p => p.def.plant.harvestedThingDef).Distinct().ToList();
            string food = products.Count == 1 ? products[0].label : "food";
            string kinds = string.Join(", ", plants.GroupBy(p => p.def).Select(g => $"{g.Key.label} ×{g.Count()}"));
            yield return new ChoreOption
            {
                kind = Chore.Kind.Gather,
                label = $"food: gather wild plants ({kinds}, ~{plants.Sum(p => p.YieldNow())} {food}, {ChoreScan.Near(plants.Max(p => scan.WalkAt(p.Position)))})",
                useful = 0.5f + ChoreOptions.FoodNeed(scan) * 1.25f,
                check = () => plants.Select(p => Check(p, map)).FirstOrDefault(r => r != null),
                apply = mind => Apply(mind.pawn, plants, kinds),
            };
        }

        /// <summary>The validator for one plant: null if it may be marked now.</summary>
        public static string Check(Plant p, Map map)
        {
            if (p.Destroyed || !p.Spawned || p.Map != map) return "gone";
            if (p.def.plant.IsTree) return "a tree";
            ThingDef product = p.def.plant.harvestedThingDef;
            if (product == null || !product.IsNutritionGivingIngestible || !product.ingestible.HumanEdible || product.ingestible.preferability <= FoodPreferability.DesperateOnly)
                return "not food";
            if (!p.HarvestableNow || p.YieldNow() <= 0) return "not ripe"; // food is ripe or not (the user); the yield rule is for trees
            if (p.Fogged()) return "not seen";
            if (map.zoneManager.ZoneAt(p.Position) is Zone_Growing) return "in a field";
            if (map.designationManager.DesignationOn(p) != null) return "already marked";
            return null;
        }

        private static string Apply(Pawn pawn, List<Plant> plants, string kinds)
        {
            var marked = plants.Where(p => Check(p, pawn.Map) == null).ToList();
            if (marked.Count == 0)
                return ChoreOptions.NoneLeft(plants.Select(p => Check(p, pawn.Map)), "Those plants can't be harvested now.");
            var chore = ChoreManager.Instance.Mark(pawn, Chore.Kind.Gather, "wild food", marked.Select(p => new LocalTargetInfo(p)));
            chore.Remember($"I marked wild plants to gather for food: {kinds}.", 2);
            ModLog.Message($"{pawn.LabelShort} marked wild food to gather: {kinds}.");
            return $"Marked wild food to gather: {kinds}.";
        }
    }
}
