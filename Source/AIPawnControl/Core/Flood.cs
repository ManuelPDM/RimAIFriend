using System;
using System.Collections.Generic;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Breadth-first walk over map cells (4 neighbours) inside an area: how many steps each cell is from the sources, or -1
    /// if it wasn't reached. Every flood in the mod goes through here; callers only say what may be walked on.
    /// The array is sized to the area, so small floods (inside one room) stay cheap.
    /// </summary>
    public sealed class Flood
    {
        public readonly CellRect area;
        private readonly int[] dist;

        /// <summary>Cells reached, in the order they were reached (nearest first).</summary>
        public readonly List<IntVec3> Reached = new List<IntVec3>();

        private Flood(CellRect area)
        {
            this.area = area;
            dist = new int[area.Area];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = -1;
        }

        /// <summary>Steps from the nearest source, or -1 (not reached, or outside the area).</summary>
        public int this[IntVec3 c] => area.Contains(c) ? dist[Index(c)] : -1;

        public bool Has(IntVec3 c) => this[c] >= 0;

        private int Index(IntVec3 c) => (c.z - area.minZ) * area.Width + (c.x - area.minX);

        /// <param name="sources">Where the walk starts (step 0). They're taken as given: passable isn't asked.</param>
        /// <param name="passable">Whether a cell may be stepped onto.</param>
        /// <param name="maxSteps">Cells this many steps away are reached but not walked on from.</param>
        /// <param name="stop">Asked for each cell as it's reached (sources too); true ends the walk there.</param>
        public static Flood Run(CellRect area, IEnumerable<IntVec3> sources, Func<IntVec3, bool> passable,
                                int maxSteps = int.MaxValue, Func<IntVec3, bool> stop = null)
        {
            var flood = new Flood(area);
            var queue = new Queue<IntVec3>();
            foreach (var s in sources)
            {
                if (!area.Contains(s) || flood.dist[flood.Index(s)] >= 0)
                    continue;
                flood.dist[flood.Index(s)] = 0;
                flood.Reached.Add(s);
                if (stop != null && stop(s))
                    return flood;
                queue.Enqueue(s);
            }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                int d = flood.dist[flood.Index(c)];
                if (d >= maxSteps)
                    continue;
                foreach (var dir in GenAdj.CardinalDirections)
                {
                    var n = c + dir;
                    if (!area.Contains(n) || flood.dist[flood.Index(n)] >= 0 || !passable(n))
                        continue;
                    flood.dist[flood.Index(n)] = d + 1;
                    flood.Reached.Add(n);
                    if (stop != null && stop(n))
                        return flood;
                    queue.Enqueue(n);
                }
            }
            return flood;
        }

        /// <summary>A shortest walk from one cell to another over passable cells (both ends included), or an empty list.</summary>
        public static List<IntVec3> Path(CellRect area, IntVec3 from, IntVec3 to, Func<IntVec3, bool> passable)
        {
            var flood = Run(area, new[] { to }, passable, stop: c => c == from);
            var path = new List<IntVec3>();
            if (!flood.Has(from))
                return path;
            // Walk back downhill from `from` to `to` (the flood started at `to`).
            for (var c = from; ; )
            {
                path.Add(c);
                int d = flood[c];
                if (d == 0)
                    return path;
                foreach (var dir in GenAdj.CardinalDirections)
                    if (flood[c + dir] == d - 1)
                    {
                        c += dir;
                        break;
                    }
            }
        }

        /// <summary>The whole map as an area.</summary>
        public static CellRect All(Map map) => new CellRect(0, 0, map.Size.x, map.Size.z);
    }
}
