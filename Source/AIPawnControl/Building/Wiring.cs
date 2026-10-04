using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Conduits for what a mind places that draws power: the shortest line of conduit blueprints from the item to the
    /// colony's powered grid. Vanilla hooks a consumer to any transmitter within 6 cells of it (PowerConnectionMaker), and
    /// transmitters join by touching, so the line starts within 6 cells of the item and ends touching a powered
    /// transmitter. Every conduit cell is one vanilla takes a conduit blueprint on (walls included).
    /// </summary>
    public static class Wiring
    {
        private const int ConnectMaxDist = 6; // PowerConnectionMaker.ConnectMaxDist

        /// <summary>It runs on power from a net (a lamp, a bench), not one that makes or only carries it.</summary>
        public static bool DrawsPower(ThingDef def) =>
            def.GetCompProperties<CompProperties_Power>() is CompProperties_Power power && power.compClass == typeof(CompPowerTrader) && power.PowerConsumption > 0f;

        /// <summary>
        /// The conduit cells to lay for the item at pos: none when it draws no power or a powered transmitter is already in
        /// reach; null when no line can be laid. Blocked cells (a new room's footprint) take no conduit.
        /// </summary>
        public static List<IntVec3> Route(Map map, ThingDef def, IntVec3 pos, Rot4 rot, ICollection<IntVec3> blocked = null)
        {
            if (!DrawsPower(def))
                return new List<IntVec3>();
            if (!RoomKindDef.Buildable(ThingDefOf.PowerConduit, map))
                return null;
            var reach = CellRect.SingleCell(pos).ExpandedBy(ConnectMaxDist).ClipInsideMap(map);
            if (reach.Cells.Any(c => Powered(c, map)))
                return new List<IntVec3>();
            var occupied = GenAdj.OccupiedRect(pos, rot, def.size);
            var free = new Dictionary<IntVec3, bool>();
            bool Free(IntVec3 c)
            {
                if (!free.TryGetValue(c, out bool ok))
                    free[c] = ok = c.InBounds(map) && !occupied.Contains(c) && (blocked == null || !blocked.Contains(c))
                                   && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.PowerConduit, c, Rot4.North, map).Accepted;
                return ok;
            }
            // Outward from every cell in reach at once: the first cell touching the grid ends the shortest line.
            var parent = new Dictionary<IntVec3, IntVec3>();
            var queue = new Queue<IntVec3>();
            foreach (var c in reach.Cells.Where(Free))
            {
                parent[c] = c;
                queue.Enqueue(c);
            }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                if (GenAdj.CardinalDirections.Any(d => Powered(c + d, map)))
                {
                    var line = new List<IntVec3> { c };
                    while (parent[c] != c)
                        line.Add(c = parent[c]);
                    return line;
                }
                foreach (var d in GenAdj.CardinalDirections)
                {
                    var n = c + d;
                    if (!parent.ContainsKey(n) && Free(n))
                    {
                        parent[n] = c;
                        queue.Enqueue(n);
                    }
                }
            }
            return null;
        }

        /// <summary>A transmitter on a net that has power coming in, which a conduit or consumer can join.</summary>
        private static bool Powered(IntVec3 c, Map map) =>
            c.InBounds(map) && c.GetTransmitter(map) is Building b && b.TransmitsPowerNow && b.def.building?.allowWireConnection != false
            && b.PowerComp?.PowerNet is PowerNet net && net.HasActivePowerSource;

        /// <summary>The line as plan entries.</summary>
        public static List<PlanEntry> Entries(List<IntVec3> line) =>
            line.Select(c => new PlanEntry(ThingDefOf.PowerConduit, c, Rot4.North)).ToList();

        /// <summary>What the line costs (1 steel a conduit), for the cost checks and labels.</summary>
        public static List<ThingDefCountClass> Cost(int conduits) =>
            ThingDefOf.PowerConduit.CostList.Select(c => new ThingDefCountClass(c.thingDef, c.count * conduits)).ToList();
    }
}
