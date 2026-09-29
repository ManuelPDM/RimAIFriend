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
        private readonly Dictionary<Room, bool> openAir = new Dictionary<Room, bool>(); // Ground.OpenAir per room: the map doesn't change while it checks

        public RoomValidator(Map map, IntVec3 center, int maxWalk)
        {
            this.map = map;
            limit = maxWalk + 20;
            var before = Flood.Run(Flood.All(map), center.InBounds(map) && Walkable(center) ? new[] { center } : new IntVec3[0],
                c => Walkable(c) && !c.Fogged(map), limit);
            reachableDoors = map.listerBuildings.allBuildingsColonist.Where(b => b is Building_Door && before.Has(b.Position)).Select(b => b.Position).ToList();
        }

        /// <summary>
        /// Walkable once the base is built: other projects' blueprints and frames count as what they'll be. Two rooms
        /// under construction can each leave a gap open that the pair closes (a dining hall and a food store sealing the
        /// ground in front of the storeroom's door).
        /// </summary>
        private bool Walkable(IntVec3 c) => c.Walkable(map) && !(Planned(c) is ThingDef d && d.passability == Traversability.Impassable);

        /// <summary>A door already there or planned there.</summary>
        private bool Door(IntVec3 c) => c.GetEdifice(map) is Building_Door || (Planned(c) is ThingDef d && d.IsDoor);

        private ThingDef Planned(IntVec3 c)
        {
            foreach (var t in c.GetThingList(map))
                if ((t is Blueprint || t is Frame) && t.def.entityDefToBuild is ThingDef d)
                    return d;
            return null;
        }

        public List<string> Check(RoomPlan plan, ThingDef material)
        {
            var fail = new List<string>();
            if (!plan.doorOutside.InBounds(map) || !Walkable(plan.doorOutside))
                fail.Add("V7: its door opens onto a wall or other blocked ground, built or planned");
            int cut = DoorsCutOff(plan);
            if (cut > 0)
                fail.Add($"V7: {cut} existing doors can't be reached (or need a long detour) with the room in place");

            // V9 no pockets (BASE_LAYOUT.md): open ground reached from outside today stays reached, or the walls close in a
            // pointless little room (the ground in front of a door, between three other rooms).
            int walledIn = WalledIn(plan);
            if (walledIn > 0)
                fail.Add($"V9: walls in {walledIn} cells of open ground");
            else if (SealsOff(plan))
                fail.Add("V10: closes off open ground from the map edge (a courtyard against rock or water)");

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
            if (reachableDoors.Count == 0 || !plan.doorOutside.InBounds(map) || !Walkable(plan.doorOutside))
                return 0;
            var remaining = new HashSet<IntVec3>(reachableDoors);
            Flood.Run(Flood.All(map), new[] { plan.doorOutside },
                n => n == plan.door || (Walkable(n) && !n.Fogged(map) && !plan.IsWall(n)),
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

        /// <summary>
        /// V10: the open ground around the room, with its walls up, still all reaches the map edge. V9 only looks 10 cells
        /// out, so a courtyard its walls close against rock or other rooms slips past it. The open cells just outside the
        /// footprint are grouped by a local walk; only when they fall apart is each group walked to the map edge. A walk also
        /// ends on a cell an earlier group's walk reached: that group reaches the edge, so this one does too.
        /// </summary>
        private bool SealsOff(RoomPlan plan)
        {
            // Open ground only: not through the new room, nor through doors (a pocket reached through a room is still no way out).
            bool Open(IntVec3 c) => !plan.footprint.Contains(c) && Walkable(c) && !Door(c);
            var around = plan.footprint.ExpandedBy(1).EdgeCells.Where(c => c.InBounds(map) && Open(c) && Ground.OpenAir(c.GetRoom(map), openAir)).ToList();
            if (around.Count < 2)
                return false;
            CellRect local = plan.footprint.ExpandedBy(25).ClipInsideMap(map);
            var groups = new List<IntVec3>();
            var left = new HashSet<IntVec3>(around);
            while (left.Count > 0)
            {
                IntVec3 seed = left.First();
                groups.Add(seed);
                var reached = Flood.Run(local, new[] { seed }, Open);
                left.RemoveWhere(reached.Has);
                left.Remove(seed);
            }
            if (groups.Count < 2)
                return false;
            var reachesEdge = new bool[map.Size.x * map.Size.z];
            foreach (var seed in groups)
            {
                bool edge = false;
                var walk = Flood.Run(Flood.All(map), new[] { seed }, Open,
                    stop: c => edge = reachesEdge[c.z * map.Size.x + c.x] || c.x == 0 || c.z == 0 || c.x == map.Size.x - 1 || c.z == map.Size.z - 1);
                if (!edge)
                    return true;
                foreach (var c in walk.Reached)
                    reachesEdge[c.z * map.Size.x + c.x] = true;
            }
            return false;
        }

        private Flood Reach(CellRect area, RoomPlan plan)
        {
            bool Open(IntVec3 c) => plan == null ? Walkable(c) : c == plan.door || (Walkable(c) && !plan.IsWall(c));
            // From the open air only: the area's edge can run through a room, and a pocket is reached through its doors.
            return Flood.Run(area, area.EdgeCells.Where(c => Open(c) && Ground.OpenAir(c.GetRoom(map), openAir)), Open);
        }
    }
}
