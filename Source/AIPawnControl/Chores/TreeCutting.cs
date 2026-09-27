using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Wood (STREAMLINE.md §5): grown trees near the base, the ones in the way first (in the Home area or next to a zone or a
    /// building), for a purpose-labelled amount: enough for another room, or a big stockpile. Never the anima tree or its
    /// radius, never trees protected from cutting, never tree farms, and a floor of trees always stays near the base.
    /// </summary>
    public static class TreeCutting
    {
        public const int MaxTrees = 60;
        private const int NearBase = 30; // steps: the trees whose count keeps a floor
        private const int MinNearTrees = 4;

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            if (!scan.SomeoneCanDo(WorkTypeDefOf.PlantCutting))
                yield break;
            Map map = scan.map;
            var foci = SiteFinder.NoBuildFoci(map);
            var picked = Candidates(scan, foci);
            int have = scan.Stock(ThingDefOf.WoodLog) + MarkedWood(map);
            if (map.resourceCounter.GetCount(ThingDefOf.WoodLog) >= ChoreOptions.StockCap)
                yield break;
            int lastCount = 0;
            foreach (var (purpose, target) in ChoreOptions.WoodTargets(scan.colonists))
            {
                var trees = Take(picked, target - have);
                if (trees.Count < 3 || trees.Count == lastCount)
                    continue;
                lastCount = trees.Count;
                int wood = trees.Sum(t => t.YieldNow());
                yield return new ChoreOption
                {
                    kind = Chore.Kind.Cut,
                    label = $"wood, {purpose}: ~{trees.Count} trees (+{wood} wood, {ChoreScan.Near(trees.Max(t => scan.WalkAt(t.Position)))})",
                    useful = 0.5f + (have < 150 ? 2f : have < 300 ? 1f : 0f) + scan.PassionFor(SkillDefOf.Plants) * 0.5f,
                    check = () => trees.Select(t => Check(t, map, foci)).FirstOrDefault(r => r != null),
                    apply = mind => Apply(mind.pawn, trees, foci),
                };
            }
        }

        /// <summary>The trees that may be cut, in the order to cut them, keeping the floor near the base. At most MaxTrees.</summary>
        public static List<Plant> Candidates(ChoreScan scan, List<(IntVec3 pos, float radius)> foci)
        {
            Map map = scan.map;
            var all = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .Where(t => t.def.plant.IsTree && scan.WalkAt(t.Position) >= 0)
                .ToList();
            int near = all.Count(t => scan.WalkAt(t.Position) <= NearBase && map.designationManager.DesignationOn(t) == null);
            int floor = System.Math.Max(MinNearTrees, near / 3);
            // Vanilla clears plants off blueprints by itself; for wood, only trees worth it (ChoreOptions.WorthHarvesting).
            var candidates = all.Where(t => Check(t, map, foci) == null && ChoreOptions.WorthHarvesting(t))
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
            return picked;
        }

        /// <summary>The first trees whose wood adds up to the amount (none for 0 or less).</summary>
        public static List<Plant> Take(List<Plant> trees, int wood)
        {
            var result = new List<Plant>();
            int sum = 0;
            foreach (var t in trees)
            {
                if (sum >= wood)
                    break;
                result.Add(t);
                sum += t.YieldNow();
            }
            return result;
        }

        /// <summary>Wood still standing in trees someone already marked.</summary>
        public static int MarkedWood(Map map) =>
            map.designationManager.AllDesignations
                .Where(d => (d.def == DesignationDefOf.HarvestPlant || d.def == DesignationDefOf.CutPlant) && d.target.Thing is Plant p && p.def.plant.IsTree
                            && p.def.plant.harvestedThingDef == ThingDefOf.WoodLog)
                .Sum(d => ((Plant)d.target.Thing).YieldNow());

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

        public static string Apply(Pawn pawn, List<Plant> trees, List<(IntVec3 pos, float radius)> foci)
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
