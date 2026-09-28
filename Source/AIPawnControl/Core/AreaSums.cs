using System;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// A summed-area table over the map: the sum of a per-cell number over any rectangle in 4 lookups. The site finders
    /// build a few per scan ("blocked cells", "trees", "fertility") so every candidate rectangle is checked in constant time.
    /// </summary>
    public sealed class AreaSums
    {
        private readonly int w;
        private readonly int[] sat;

        public AreaSums(Map map, Func<int, int> valueAt)
        {
            w = map.Size.x;
            int h = map.Size.z;
            sat = new int[(w + 1) * (h + 1)];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    sat[(z + 1) * (w + 1) + x + 1] = valueAt(z * w + x)
                        + sat[z * (w + 1) + x + 1] + sat[(z + 1) * (w + 1) + x] - sat[z * (w + 1) + x];
        }

        public AreaSums(Map map, bool[] grid) : this(map, i => grid[i] ? 1 : 0) { }

        public AreaSums(Map map, int[] grid) : this(map, i => grid[i]) { }

        /// <summary>The sum over the rect, which must lie inside the map.</summary>
        public int Sum(CellRect r) =>
            sat[(r.maxZ + 1) * (w + 1) + r.maxX + 1] - sat[r.minZ * (w + 1) + r.maxX + 1]
            - sat[(r.maxZ + 1) * (w + 1) + r.minX] + sat[r.minZ * (w + 1) + r.minX];
    }
}
