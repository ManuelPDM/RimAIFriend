using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Mine options (STREAMLINE.md §5): per ore, the vein nearest the base that a miner can reach, up to 40 cells. Never under
    /// overhead mountain (infestations, deadly collapses), and every roofed cell nearby must keep a roof holder within
    /// vanilla's 6.9-cell support range once the vein is gone.
    /// </summary>
    public static class Mining
    {
        public const int MaxCells = 40;

        public static bool IsOre(ThingDef rock) =>
            rock?.building?.mineableThing != null && rock.building.isNaturalRock && !rock.building.mineableThing.IsWithinCategory(ThingCategoryDefOf.Chunks);

        /// <summary>"steel", "component", or "stone" for plain rock (it drops chunks).</summary>
        public static string ResourceLabel(ThingDef rock)
        {
            var thing = rock.building?.mineableThing;
            return thing == null || thing.IsWithinCategory(ThingCategoryDefOf.Chunks) ? "stone" : thing.label;
        }

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            foreach (var (rock, cells, steps) in Veins(scan))
            {
                ThingDef resource = rock.building.mineableThing;
                string label = ResourceLabel(rock);
                bool core = resource == ThingDefOf.Steel || resource == ThingDefOf.ComponentIndustrial || resource == ThingDefOf.Plasteel;
                bool short_ = (resource == ThingDefOf.Steel && scan.Stock(ThingDefOf.Steel) < 100)
                              || (resource == ThingDefOf.ComponentIndustrial && scan.Stock(ThingDefOf.ComponentIndustrial) < 5);
                int amount = (int)(cells.Count * PerCell(rock));
                yield return new ChoreOption
                {
                    kind = Chore.Kind.Mine,
                    label = $"{label}: the vein {ChoreScan.Near(steps)} (~{cells.Count} spots, +{amount} {label})",
                    useful = 0.3f + (core ? 0.5f : 0f) + (short_ ? 1.5f : 0f) + scan.PassionFor(SkillDefOf.Mining) * 0.5f,
                    check = () => cells.Select(c => Check(c, scan.map)).FirstOrDefault(r => r != null) ?? (RoofSafe(scan.map, cells) ? null : "a roof would collapse"),
                    apply = mind => Apply(mind.pawn, cells, label),
                };
            }
        }

        public static float PerCell(ThingDef rock) => rock.building.EffectiveMineableYield * rock.building.mineableDropChance;

        /// <summary>Per ore, the vein nearest the base that a miner can reach (roof-checked, up to MaxCells cells, nearest first).</summary>
        public static List<(ThingDef rock, List<IntVec3> cells, int steps)> Veins(ChoreScan scan)
        {
            var result = new List<(ThingDef, List<IntVec3>, int)>();
            if (!scan.SomeoneCanDo(WorkTypeDefOf.Mining))
                return result;
            Map map = scan.map;
            // The nearest exposed cell of each ore: next to a cell the walk from the base reached.
            var seeds = new Dictionary<ThingDef, (IntVec3 cell, int steps)>();
            int w = map.Size.x;
            for (int i = 0; i < w * map.Size.z; i++)
            {
                var c = new IntVec3(i % w, 0, i / w);
                int steps = scan.WalkAt(c);
                if (steps < 0)
                    continue;
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (!n.InBounds(map) || !(n.GetFirstMineable(map) is Mineable rock) || !IsOre(rock.def) || Check(n, map) != null)
                        continue;
                    if (!seeds.TryGetValue(rock.def, out var best) || steps < best.steps)
                        seeds[rock.def] = (n, steps);
                }
            }
            foreach (var kv in seeds)
            {
                var vein = Vein(kv.Value.cell, kv.Key, map);
                if (!RoofSafe(map, vein))
                {
                    ModLog.Message($"Mining finder: the {ResourceLabel(kv.Key)} vein at {kv.Value.cell} would bring a roof down; not offered.");
                    continue;
                }
                result.Add((kv.Key, vein, kv.Value.steps));
            }
            return result;
        }

        /// <summary>The same rock around the seed, nearest first, up to MaxCells, each passing Check.</summary>
        private static List<IntVec3> Vein(IntVec3 seed, ThingDef rock, Map map)
        {
            var vein = new List<IntVec3>();
            var seen = new HashSet<IntVec3> { seed };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(seed);
            while (queue.Count > 0 && vein.Count < MaxCells)
            {
                var c = queue.Dequeue();
                vein.Add(c);
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (n.InBounds(map) && seen.Add(n) && n.GetFirstMineable(map)?.def == rock && Check(n, map) == null)
                        queue.Enqueue(n);
                }
            }
            return vein;
        }

        /// <summary>The validator for one cell: null if it may be marked now.</summary>
        public static string Check(IntVec3 c, Map map)
        {
            if (!c.InBounds(map)) return "off the map";
            if (c.Fogged(map)) return "not seen";
            if (!(c.GetFirstMineable(map) is Mineable rock) || !rock.def.building.isNaturalRock) return "nothing to mine";
            if (SiteFinder.UnderOverheadMountain(c, map)) return "under overhead mountain";
            if (map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null || map.designationManager.DesignationAt(c, DesignationDefOf.MineVein) != null)
                return "already marked";
            return null;
        }

        /// <summary>
        /// Every roofed cell within reach of the vein keeps a roof holder within 6.9 cells that isn't being mined
        /// (RoofCollapseUtility's range; vanilla also needs the roof to connect them, which natural rock roofs do).
        /// </summary>
        public static bool RoofSafe(Map map, List<IntVec3> cells)
        {
            var mined = new HashSet<IntVec3>(cells);
            var roofed = new HashSet<IntVec3>();
            int radial = RoofCollapseUtility.RoofSupportRadialCellsCount;
            foreach (var c in cells)
                for (int i = 0; i < radial; i++)
                {
                    var n = c + GenRadial.RadialPattern[i];
                    if (n.InBounds(map) && n.Roofed(map))
                        roofed.Add(n);
                }
            foreach (var r in roofed)
            {
                if (!mined.Contains(r) && r.GetEdifice(map)?.def.holdsRoof == true)
                    continue; // holds itself up
                bool held = false;
                for (int i = 0; i < radial && !held; i++)
                {
                    var h = r + GenRadial.RadialPattern[i];
                    held = h.InBounds(map) && !mined.Contains(h) && h.GetEdifice(map)?.def.holdsRoof == true;
                }
                if (!held)
                    return false;
            }
            return true;
        }

        public static string Apply(Pawn pawn, List<IntVec3> cells, string label)
        {
            Map map = pawn.Map;
            var marked = cells.Where(c => Check(c, map) == null).ToList();
            if (marked.Count == 0)
                return ChoreOptions.NoneLeft(cells.Select(c => Check(c, map)), $"The {label} can't be mined now.");
            if (!RoofSafe(map, marked))
                return $"Mining the {label} there would bring the roof down; I left it.";
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Mine, label);
            foreach (var c in marked)
            {
                map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                chore.cells.Add(c);
            }
            chore.Remember($"I marked {label} to mine: ×{marked.Count}.", 2);
            ModLog.Message($"{pawn.LabelShort} marked {label} to mine: {marked.Count} cells.");
            return $"Marked {label} to mine: ×{marked.Count}.";
        }
    }
}
