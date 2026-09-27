using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Sites for a new zone (a field or a stockpile, PHASE5.md §2): every square near the base whose cells all pass the
    /// kind's cell rule, scored by the kind's cell score and the walk from the base centre; the best few that don't overlap.
    /// Grids with summed-area tables, like SiteFinder, so each square costs 4 lookups. The chosen size is laid out around
    /// the site's centre, trimmed to the largest size that still fits.
    /// </summary>
    public class ZoneSites
    {
        public class Site
        {
            public char letter;
            public IntVec3 center;
            public int steps;   // walk from the base centre
            public int maxSize; // the largest offered size that fits here
            public float score;
            public CellRect Rect(int size) => ZoneSites.Square(center, size);
        }

        private readonly Map map;
        private readonly Func<IntVec3, bool> cellOk;
        private readonly int w, h;
        private readonly int[] satBad, satScore;
        private readonly ChoreScan scan;

        /// <param name="cellOk">The hard rule for one cell (the same rule the validator applies).</param>
        /// <param name="cellScore">0-100 per good cell, e.g. fertility for fields.</param>
        public ZoneSites(ChoreScan scan, Func<IntVec3, bool> cellOk, Func<IntVec3, int> cellScore)
        {
            this.scan = scan;
            map = scan.map;
            this.cellOk = cellOk;
            w = map.Size.x;
            h = map.Size.z;
            var bad = new int[w * h];
            var score = new int[w * h];
            for (int i = 0; i < bad.Length; i++)
            {
                var c = new IntVec3(i % w, 0, i / w);
                // Only cells within the walk count: nothing is laid out beyond where a colonist gets from the base.
                bool ok = scan.WalkAt(c) >= 0 && cellOk(c);
                bad[i] = ok ? 0 : 1;
                score[i] = ok ? cellScore(c) : 0;
            }
            satBad = Sat(bad);
            satScore = Sat(score);
        }

        public static CellRect Square(IntVec3 center, int size) => new CellRect(center.x - (size - 1) / 2, center.z - (size - 1) / 2, size, size);

        /// <summary>Top sites for the smallest size, apart from each other, best first; each knows the largest size that fits.</summary>
        public List<Site> Find(int[] sizes, int count = 3)
        {
            int small = sizes.Min();
            var candidates = new List<Site>();
            for (int z = 1; z + small < h - 1; z++)
                for (int x = 1; x + small < w - 1; x++)
                {
                    var rect = new CellRect(x, z, small, small);
                    if (Sum(satBad, rect) > 0)
                        continue;
                    var center = new IntVec3(x + (small - 1) / 2, 0, z + (small - 1) / 2);
                    int steps = scan.WalkAt(center);
                    if (steps < 0)
                        continue;
                    float quality = Sum(satScore, rect) / (float)(small * small); // 0-100
                    candidates.Add(new Site { center = center, steps = steps, score = quality / 25f - steps / 15f });
                }
            // Room to grow counts: each bigger size that fits around the site is worth as much as 15 steps of walking.
            var bySize = sizes.OrderBy(n => n).ToArray();
            foreach (var s in candidates)
            {
                s.maxSize = bySize.LastOrDefault(n => Fits(Square(s.center, n)));
                s.score += System.Array.IndexOf(bySize, s.maxSize);
            }
            var picked = new List<Site>();
            foreach (var s in candidates.OrderByDescending(s => s.score))
            {
                if (picked.Count >= count)
                    break;
                if (picked.Any(p => p.center.DistanceTo(s.center) < sizes.Max() + 2))
                    continue;
                s.letter = (char)('A' + picked.Count);
                picked.Add(s);
            }
            return picked;
        }

        public bool Fits(CellRect rect) => rect.minX >= 0 && rect.minZ >= 0 && rect.maxX < w && rect.maxZ < h && Sum(satBad, rect) == 0;

        /// <summary>The validator: every cell of the square passes the cell rule on the live map. Null if fine.</summary>
        public static string Check(CellRect rect, Map map, Func<IntVec3, bool> cellOk)
        {
            foreach (var c in rect)
                if (!c.InBounds(map) || !cellOk(c))
                    return $"a cell fails the rule ({c.GetTerrain(map)?.label ?? "off the map"})";
            return null;
        }

        /// <summary>"next to the kitchen, near the base" / "by the water, a walk from the base". No directions.</summary>
        public string Where(CellRect rect, int steps)
        {
            var words = new List<string>();
            foreach (var c in rect.ExpandedBy(2).ClipInsideMap(map).EdgeCells)
            {
                Room room = c.GetRoom(map);
                if (room != null && !room.PsychologicallyOutdoors && room.Role != null && room.Role != RoomRoleDefOf.None && !SiteFinder.NoRole(room))
                {
                    words.Add("next to the " + room.GetRoomRoleLabel());
                    break;
                }
            }
            foreach (var c in rect.ExpandedBy(3).ClipInsideMap(map))
                if (c.GetTerrain(map).IsWater)
                {
                    words.Add("by the water");
                    break;
                }
            words.Add(ChoreScan.Near(steps));
            return string.Join(", ", words);
        }

        /// <summary>The terrain most of the square is on, by the game's label: "soil", "rich soil".</summary>
        public string Ground(CellRect rect) =>
            rect.Cells.Select(c => c.GetTerrain(map)).GroupBy(t => t).OrderByDescending(g => g.Count()).First().Key.label;

        private int[] Sat(int[] grid)
        {
            var sat = new int[(w + 1) * (h + 1)];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    sat[(z + 1) * (w + 1) + x + 1] = grid[z * w + x] + sat[z * (w + 1) + x + 1] + sat[(z + 1) * (w + 1) + x] - sat[z * (w + 1) + x];
            return sat;
        }

        private int Sum(int[] sat, CellRect r) =>
            sat[(r.maxZ + 1) * (w + 1) + r.maxX + 1] - sat[r.minZ * (w + 1) + r.maxX + 1] - sat[(r.maxZ + 1) * (w + 1) + r.minX] + sat[r.minZ * (w + 1) + r.minX];
    }
}
