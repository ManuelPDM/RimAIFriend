using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Checks a planned room against the live map for what the site finder's grids can't see (PHASE4.md §3, CLEANUP.md §6):
    /// V7 no existing door cut off, V8 vanilla's own placement check, V9 no open ground walled in. Each failure starts with
    /// its rule id. The finder guarantees the rest (door, enclosure, furniture, footprint cells) by how it builds a plan;
    /// the "Every kind builds" self-test has vanilla judge the finished room.
    /// </summary>
    public class RoomValidator
    {
        /// <summary>Doors may take this much longer to reach with the room in place before it counts as cutting a path.</summary>
        private const int DetourAllowance = 30;

        private readonly Map map;
        private readonly int limit;
        private readonly List<IntVec3> reachableDoors;

        public RoomValidator(Map map, IntVec3 center, int maxWalk)
        {
            this.map = map;
            limit = maxWalk + 20;
            var before = SiteFinder.Walk(map, center, limit);
            reachableDoors = map.listerBuildings.allBuildingsColonist.Where(b => b is Building_Door && before.Has(b.Position)).Select(b => b.Position).ToList();
        }

        public List<string> Check(RoomPlan plan, ThingDef material)
        {
            var fail = new List<string>();
            int cut = DoorsCutOff(plan);
            if (cut > 0)
                fail.Add($"V7: {cut} existing doors can't be reached (or need a long detour) with the room in place");

            // V9 no pockets (BASE_LAYOUT.md): open ground reached from outside today stays reached, or the walls close in a
            // pointless little room (the ground in front of a door, between three other rooms).
            int walledIn = WalledIn(plan);
            if (walledIn > 0)
                fail.Add($"V9: walls in {walledIn} cells of open ground");

            // V8 vanilla: every entry passes CanPlaceBlueprintAt with its material.
            foreach (var e in plan.entries)
            {
                var report = GenConstruct.CanPlaceBlueprintAt(e.def, e.cell, e.rot, map, stuffDef: RoomPlan.StuffFor(e.def, material, map));
                if (!report.Accepted)
                {
                    fail.Add($"V8: {e.def.label}: {report.Reason}");
                    break;
                }
            }
            return fail;
        }

        /// <summary>
        /// Walks from the door's outside cell (the base centre may lie inside the footprint) with the room in place, and
        /// counts doors that were reachable before but aren't now. Stops as soon as every door is found. Its own door is
        /// open even where a wall stands today: a hub is walked through to the doors it takes in.
        /// </summary>
        private int DoorsCutOff(RoomPlan plan)
        {
            if (reachableDoors.Count == 0 || !plan.doorOutside.InBounds(map) || !plan.doorOutside.Walkable(map))
                return 0;
            var remaining = new HashSet<IntVec3>(reachableDoors);
            Flood.Run(Flood.All(map), new[] { plan.doorOutside },
                n => n == plan.door || (n.Walkable(map) && !n.Fogged(map) && !plan.IsWall(n)),
                limit + DetourAllowance + Math.Max(plan.walk, 0),
                stop: c => remaining.Remove(c) && remaining.Count == 0);
            return remaining.Count;
        }

        /// <summary>Walkable cells around the room reached from beyond it now but not once it's built (its doors pass).</summary>
        private int WalledIn(RoomPlan plan)
        {
            CellRect area = plan.footprint.ExpandedBy(10).ClipInsideMap(map);
            var before = Reach(area, null);
            var after = Reach(area, plan);
            return before.Reached.Count(c => !plan.footprint.Contains(c) && !after.Has(c));
        }

        private Flood Reach(CellRect area, RoomPlan plan)
        {
            bool Open(IntVec3 c) => plan == null ? c.Walkable(map) : c == plan.door || (c.Walkable(map) && !plan.IsWall(c));
            return Flood.Run(area, area.EdgeCells.Where(Open), Open);
        }
    }
}
