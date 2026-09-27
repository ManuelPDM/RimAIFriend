using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Cut-trees options (PHASE5.md §2): up to 50 grown trees near the base, the ones in the way first (in the Home area or
    /// next to a zone or a building). Never the anima tree or its radius, never trees protected from cutting, never tree
    /// farms, and a floor of trees always stays near the base.
    /// </summary>
    public static class TreeCutting
    {
        public const int MaxTrees = 50;
        private const int NearBase = 30; // steps: the trees whose count keeps a floor
        private const int MinNearTrees = 4;
        private const float MinGrowth = 0.9f; // young trees give little wood; they're only cut when in the way

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            if (!scan.SomeoneCanDo(WorkTypeDefOf.PlantCutting))
                yield break;
            Map map = scan.map;
            var foci = SiteFinder.NoBuildFoci(map);
            var all = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .Where(t => t.def.plant.IsTree && scan.WalkAt(t.Position) >= 0)
                .ToList();
            int near = all.Count(t => scan.WalkAt(t.Position) <= NearBase && map.designationManager.DesignationOn(t) == null);
            int floor = System.Math.Max(MinNearTrees, near / 3);
            var candidates = all.Where(t => Check(t, map, foci) == null && (t.Growth >= MinGrowth || InTheWay(t, map)))
                .OrderByDescending(t => InTheWay(t, map))
                .ThenBy(t => scan.WalkAt(t.Position))
                .ToList();
            var picked = new List<Plant>();
            foreach (var t in candidates)
            {
                if (picked.Count >= MaxTrees)
                    break;
                if (scan.WalkAt(t.Position) <= NearBase && !InTheWay(t, map))
                {
                    if (near - 1 < floor)
                        continue;
                    near--;
                }
                picked.Add(t);
            }
            if (picked.Count < 3)
                yield break;
            int woodStock = scan.Stock(ThingDefOf.WoodLog);
            yield return new ChoreOption
            {
                kind = Chore.Kind.Cut,
                label = $"cut trees for wood ({ChoreScan.Near(picked.Max(t => scan.WalkAt(t.Position)))}, the ones in the way first)",
                useful = 0.5f + (woodStock < 100 ? 2f : woodStock < 300 ? 1f : 0f) + scan.PassionFor(SkillDefOf.Plants) * 0.5f
                         + (picked.Count(t => InTheWay(t, map)) >= 3 ? 0.5f : 0f),
                needs = ChoreNeeds.Amount,
                counts = ChoreOptions.Counts(picked.Count, 10, 25, MaxTrees),
                describe = k => $"{k} trees, about {picked.Take(k).Sum(t => t.YieldNow())} wood",
                check = () => picked.Select(t => Check(t, map, foci)).FirstOrDefault(r => r != null),
                apply = (mind, choice) => Apply(mind.pawn, picked.Take(choice.count).ToList(), foci),
            };
        }

        /// <summary>The validator for one tree: null if it may be marked now.</summary>
        public static string Check(Plant t, Map map, List<(IntVec3 pos, float radius)> foci)
        {
            if (t.Destroyed || !t.Spawned || t.Map != map) return "gone";
            if (!t.def.plant.IsTree) return "not a tree";
            if (t.def.plant.harvestedThingDef != ThingDefOf.WoodLog || !t.HarvestableNow || t.YieldNow() <= 0) return "no wood yet";
            if (t.Fogged()) return "not seen";
            if (t.def.GetCompProperties<CompProperties_MeditationFocus>() != null) return "a meditation focus"; // anima, harbinger
            if (t.def.GetCompProperties<CompProperties_TreeConnection>() != null) return "a connection tree"; // Gauranlen
            if (SiteFinder.InFocusRadius(t.Position, foci)) return "in the anima tree's radius";
            if (t.TryGetComp<CompPlantPreventCutting>() is CompPlantPreventCutting prevent && prevent.PreventCutting) return "protected from cutting";
            if (map.zoneManager.ZoneAt(t.Position) is Zone_Growing zone && zone.GetPlantDefToGrow() == t.def) return "grown in a field";
            if (map.designationManager.DesignationOn(t) != null) return "already marked";
            return null;
        }

        /// <summary>In the Home area, or next to a zone, a building or a blueprint.</summary>
        public static bool InTheWay(Plant t, Map map)
        {
            if (map.areaManager.Home[t.Position])
                return true;
            foreach (var c in GenAdj.CellsAdjacent8Way(t))
            {
                if (!c.InBounds(map))
                    continue;
                if (map.zoneManager.ZoneAt(c) != null)
                    return true;
                foreach (var thing in c.GetThingList(map))
                    if (thing is Blueprint || thing is Frame || (thing.def.category == ThingCategory.Building && thing.Faction == Faction.OfPlayer))
                        return true;
            }
            return false;
        }

        private static string Apply(Pawn pawn, List<Plant> trees, List<(IntVec3 pos, float radius)> foci)
        {
            var marked = trees.Where(t => Check(t, pawn.Map, foci) == null).ToList();
            if (marked.Count == 0)
                return ChoreOptions.NoneLeft(trees.Select(t => Check(t, pawn.Map, foci)), "Those trees can't be cut now.");
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Cut, "trees");
            foreach (var t in marked)
            {
                pawn.Map.designationManager.AddDesignation(new Designation(t, DesignationDefOf.HarvestPlant));
                chore.things.Add(t);
            }
            int wood = marked.Sum(t => t.YieldNow());
            chore.Remember($"I marked trees to cut for wood: ×{marked.Count}, about {wood} wood.", 2);
            ModLog.Message($"{pawn.LabelShort} marked {marked.Count} trees to cut (about {wood} wood).");
            return $"Marked trees to cut: ×{marked.Count}, about {wood} wood.";
        }
    }
}
