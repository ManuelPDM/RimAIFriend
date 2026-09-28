using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The base's ways out and what can bring doors inside (BASE_LAYOUT.md §5): a way out is a colony door between an
    /// indoor room and the outdoors; a room people may walk through has no bed and no meal source (§3).
    /// </summary>
    public static class Layout
    {
        /// <summary>An indoor room of the base: proper, not a doorway, not outdoors.</summary>
        public static bool Indoor(Room room) => room != null && !room.IsDoorway && !room.PsychologicallyOutdoors && !room.UsesOutdoorTemperature;

        /// <summary>An indoor room with a colony door: a room of the base, not an ancient ruin.</summary>
        public static bool OfBase(Room room) => Indoor(room) && Doors(room).Any(d => d.Faction == Faction.OfPlayer);

        /// <summary>
        /// A room of the base that's for something: vanilla gives it a role, one of our projects built it (a common room has
        /// no role), or it joins 2+ doors (a hall). Not a pocket of ground the walls happen to close in.
        /// </summary>
        public static bool RealRoom(Room room) => OfBase(room)
            && (!SiteFinder.NoRole(room) || BuildManager.Instance?.BuilderOf(room) != null || Doors(room).Count() >= 2);

        /// <summary>
        /// Walking through doesn't hurt it (§3): no bed (movement noise disturbs sleep) and no meal source (tracked-in
        /// filth raises food poisoning). Vanilla ties no other harm to foot traffic.
        /// </summary>
        public static bool WalkThrough(Room room)
        {
            if (!Indoor(room))
                return false;
            foreach (var t in room.ContainedAndAdjacentThings)
                if (t is Building b && room.ContainsCell(b.Position) && (b is Building_Bed || b.def.building?.isMealSource == true))
                    return false;
            return true;
        }

        /// <summary>A kind whose items make a walk-through room: no bed and no meal source among them.</summary>
        public static bool WalkThroughKind(RoomKindDef kind, Map map) =>
            kind.role != null && kind.items.All(i => !(i.Resolve(map) is ThingDef d) || (!d.IsBed && d.building?.isMealSource != true));

        /// <summary>The rooms on either side of a door (its walkable neighbours), doorways left out.</summary>
        public static List<Room> Sides(Building_Door door)
        {
            var rooms = new List<Room>();
            for (int r = 0; r < 4; r++)
            {
                IntVec3 n = door.Position + new Rot4(r).FacingCell;
                if (!n.InBounds(door.Map) || !n.Walkable(door.Map))
                    continue;
                Room room = n.GetRoom(door.Map);
                if (room != null && !room.IsDoorway && !rooms.Contains(room))
                    rooms.Add(room);
            }
            return rooms;
        }

        public class WayOut
        {
            public Building_Door door;
            public Room room;      // the indoor room it serves
            public IntVec3 outside; // its cell on the outdoor side
        }

        /// <summary>Every colony door with an indoor room on one side and the outdoors on the other.</summary>
        public static List<WayOut> WaysOut(Map map)
        {
            var result = new List<WayOut>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_Door door))
                    continue;
                Room inside = null;
                IntVec3 outside = IntVec3.Invalid;
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = door.Position + new Rot4(r).FacingCell;
                    if (!n.InBounds(map) || !n.Walkable(map))
                        continue;
                    Room room = n.GetRoom(map);
                    if (Indoor(room))
                        inside = room;
                    else if (room != null && !room.IsDoorway)
                        outside = n;
                }
                if (inside != null && outside.IsValid)
                    result.Add(new WayOut { door = door, room = inside, outside = outside });
            }
            return result;
        }

        /// <summary>A room reaches inside when one of its doors opens into another indoor room.</summary>
        public static bool ReachesInside(Room room)
        {
            foreach (var door in Doors(room))
                if (Sides(door).Any(r => r != room && Indoor(r)))
                    return true;
            return false;
        }

        public static IEnumerable<Building_Door> Doors(Room room)
        {
            var seen = new HashSet<Building_Door>();
            foreach (var c in room.BorderCells)
                if (c.InBounds(room.Map) && c.GetEdifice(room.Map) is Building_Door d && seen.Add(d))
                    yield return d;
        }

        /// <summary>"dining room", "workshop", or "room" when vanilla gives it no role.</summary>
        public static string Name(Room room) => SiteFinder.NoRole(room) ? "room" : room.Role.label;

        /// <summary>For [Colony]: "6 ways out".</summary>
        public static string Line(Map map)
        {
            int n = WaysOut(map).Count;
            return n == 1 ? "1 way out" : $"{n} ways out";
        }

        // ---- doors between neighbours (§5.2a) ----

        public class NeighbourDoor
        {
            public IntVec3 cell;
            public Room from, into; // from: a room with no way inside yet; into: the room it opens into
            public string Label => $"connect the {Name(from)} to the {Name(into)} (a door in their shared wall)";
        }

        /// <summary>
        /// A door in a single player wall between two indoor rooms, at least one walk-through, where one of them doesn't
        /// reach inside yet. The door cell has walls on its other two sides and nothing built right in front of it.
        /// Doors never join two end rooms, so nobody walks through a bedroom or the kitchen.
        /// </summary>
        public static List<NeighbourDoor> NeighbourDoors(Map map)
        {
            var best = new Dictionary<(Room, Room), (NeighbourDoor door, float off)>();
            var spots = SiteFinder.InteractionSpots(map);
            foreach (var room in map.regionGrid.AllRooms)
            {
                if (!RealRoom(room) || ReachesInside(room))
                    continue;
                foreach (var w in room.BorderCells)
                {
                    if (!w.InBounds(map) || !(w.GetEdifice(map) is Building wall) || wall.def != ThingDefOf.Wall || wall.Faction != Faction.OfPlayer)
                        continue;
                    for (int r = 0; r < 4; r++)
                    {
                        IntVec3 dir = new Rot4(r).FacingCell, side = new Rot4(r).Rotated(RotationDirection.Clockwise).FacingCell;
                        IntVec3 a = w - dir, b = w + dir;
                        if (!a.InBounds(map) || !b.InBounds(map) || a.GetRoom(map) != room)
                            continue;
                        Room other = b.GetRoom(map);
                        // She'll walk through the other room once her outside door closes, so it must be walk-through.
                        if (other == room || !RealRoom(other) || !WalkThrough(other))
                            continue;
                        if (!(w + side).Impassable(map) || !(w - side).Impassable(map) || !Clear(a, map, spots) || !Clear(b, map, spots))
                            continue;
                        // Away from the corners: the most wall on its shorter side.
                        float off = -System.Math.Min(Run(w, side, map), Run(w, -side, map));
                        var key = room.ID < other.ID ? (room, other) : (other, room);
                        if (!best.TryGetValue(key, out var had) || off < had.off)
                            best[key] = (new NeighbourDoor { cell = w, from = room, into = other }, off);
                    }
                }
            }
            return best.Values.Select(v => v.door).ToList();
        }

        private static bool Clear(IntVec3 c, Map map, HashSet<IntVec3> spots)
        {
            if (!c.Walkable(map) || spots.Contains(c))
                return false;
            foreach (var t in c.GetThingList(map))
                if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame)
                    return false;
            return true;
        }

        /// <summary>How many wall cells continue from c along dir (up to 3).</summary>
        private static int Run(IntVec3 c, IntVec3 dir, Map map)
        {
            int n = 0;
            for (int i = 1; i <= 3 && (c + dir * i).InBounds(map) && (c + dir * i).Impassable(map); i++)
                n++;
            return n;
        }

        // ---- Base call lines (§5.7) ----

        public class Option
        {
            public string group, label;
            public Func<Pawn, ThingDef, string> apply; // her pick, with the wall material: places it and says what happened
        }

        /// <summary>
        /// What the Base call offers for the layout: the ladder's next room as a common room (when people may walk through
        /// it and it fits), else the best plain common room, a way out that can close, and a door between neighbours.
        /// Each is found again when she picks it: the map may have changed while she thought.
        /// </summary>
        public static List<Option> Options(Map map, Ladder.Rung rung)
        {
            var options = new List<Option>();
            var materials = SiteFinder.Materials(map);
            string Cost(IEnumerable<PlanEntry> entries)
            {
                var plan = new RoomPlan { map = map };
                plan.entries.AddRange(entries);
                return SiteFinder.CostText(plan, materials);
            }
            var finder = new CommonRoomFinder(map);
            var hall = finder.Find().FirstOrDefault();
            if (hall != null && rung?.kind != null && rung.waiting == null && WalkThroughKind(rung.kind, map) && finder.WithKind(hall, rung.kind) is CommonPlan asRung)
                options.Add(new Option { group = "Next for the base", label = $"{asRung.Label}: {Cost(asRung.ToPlan(map, rung.kind).entries)}",
                    apply = (p, m) => PlaceCommon(p, asRung.doors, rung.kind, m) });
            if (hall != null)
                options.Add(new Option { group = "Inside the base", label = $"{hall.Label}: {Cost(hall.ToPlan(map, null).entries)}", apply = (p, m) => PlaceCommon(p, hall.doors, null, m) });
            if (Surplus(map).FirstOrDefault() is WayOut way)
                options.Add(new Option { group = "Inside the base", label = $"{CloseLabel(way)}: {Cost(new[] { new PlanEntry(ThingDefOf.Wall, way.door.Position, Rot4.North) })}",
                    apply = (p, m) => Close(p, way.door.Position, m) });
            if (NeighbourDoors(map).FirstOrDefault() is NeighbourDoor door)
                options.Add(new Option { group = "Inside the base", label = $"{door.Label}: {Cost(new[] { new PlanEntry(ThingDefOf.Door, door.cell, Rot4.North) })}; then its outside door can be closed",
                    apply = (p, m) => Connect(p, door.cell, m) });
            return options;
        }

        private static RoomKindDef Kind(string defName) => DefDatabase<RoomKindDef>.GetNamed(defName);

        private static string PlaceCommon(Pawn pawn, List<WayOut> doors, RoomKindDef kind, ThingDef material)
        {
            Map map = pawn.Map;
            var finder = new CommonRoomFinder(map);
            var members = finder.waysOut.Where(w => doors.Any(d => d.door.Position == w.door.Position)).ToList();
            var plan = finder.Build(members);
            if (plan != null && kind != null)
                plan = finder.WithKind(plan, kind);
            if (plan == null)
                return $"Wanted a {kind?.label ?? "common room"} the others open into, but it doesn't fit there any more.";
            string where = "joining the " + plan.Rooms;
            ModLog.Message($"{pawn.LabelShort}: {plan.Label}\n{finder.Draw(plan)}");
            var project = BuildManager.Instance.PlaceLayout(pawn, plan.ToPlan(map, kind ?? Kind("AIPC_CommonRoom")), material, plan.walkway.First(), where);
            string marked = Supplies.MarkFor(pawn, project);
            return $"Laid out a {kind?.label ?? "common room"} {where} ({plan.WaysOutSaved} fewer ways out, {material.label})." + (marked.Length > 0 ? " " + marked : "");
        }

        private static string Close(Pawn pawn, IntVec3 cell, ThingDef material)
        {
            Map map = pawn.Map;
            var way = Surplus(map).Find(w => w.door.Position == cell);
            if (way == null)
                return "Wanted to close an outside door, but it's still needed.";
            var plan = new RoomPlan { kind = Kind("AIPC_ClosedDoor"), map = map, footprint = CellRect.SingleCell(cell) };
            plan.entries.Add(new PlanEntry(ThingDefOf.Wall, cell, Rot4.North));
            var project = BuildManager.Instance.PlaceLayout(pawn, plan, material, InsideCell(cell, way.room), $"in the {Name(way.room)}'s outside door", way.door);
            string marked = Supplies.MarkFor(pawn, project);
            return $"Marked the {Name(way.room)}'s outside door to come down and be walled up ({material.label})." + (marked.Length > 0 ? " " + marked : "");
        }

        private static string Connect(Pawn pawn, IntVec3 cell, ThingDef material)
        {
            Map map = pawn.Map;
            var door = NeighbourDoors(map).Find(d => d.cell == cell);
            if (door == null)
                return "Wanted a door between two rooms, but it isn't needed any more.";
            var plan = new RoomPlan { kind = Kind("AIPC_Doorway"), map = map, footprint = CellRect.SingleCell(cell) };
            plan.entries.Add(new PlanEntry(ThingDefOf.Door, cell, Rot4.North));
            string where = $"between the {Name(door.from)} and the {Name(door.into)}";
            var project = BuildManager.Instance.PlaceLayout(pawn, plan, material, InsideCell(cell, door.from), where);
            string marked = Supplies.MarkFor(pawn, project);
            return $"Laid out a door {where} ({material.label})." + (marked.Length > 0 ? " " + marked : "");
        }

        /// <summary>The room's cell next to a door or wall cell in its border.</summary>
        public static IntVec3 InsideCell(IntVec3 cell, Room room)
        {
            foreach (var d in GenAdj.CardinalDirections)
                if (room.ContainsCell(cell + d))
                    return cell + d;
            return cell;
        }

        // ---- closing ways out (§5.5) ----

        /// <summary>"close the kitchen's outside door (it reaches the outdoors through the dining room)".</summary>
        public static string CloseLabel(WayOut way) => $"close the {Name(way.room)}'s outside door (it's reached from inside now)";

        /// <summary>
        /// Ways out that can go: its room reaches inside another way, one of our projects built the door (never the
        /// player's), and with it walled every room of the base still reaches the outdoors within outdoorWalk tiles.
        /// </summary>
        public static List<WayOut> Surplus(Map map)
        {
            var result = new List<WayOut>();
            var ways = WaysOut(map);
            if (ways.Count < 2 || BuildManager.Instance == null)
                return result;
            int limit = SiteWeights.Load().outdoorWalk;
            var rooms = map.regionGrid.AllRooms.Where(OfBase).ToList();
            foreach (var way in ways)
                if (ReachesInside(way.room) && BuildManager.Instance.OurDoor(map, way.door.Position)
                    && rooms.All(r => OutdoorWalk(r, way.door.Position, map, limit) <= limit))
                    result.Add(way);
            return result;
        }

        /// <summary>
        /// Tiles from the room's doors to the outdoors with this door shut, up to limit + 1. The walk never crosses another
        /// room that shouldn't be walked through (a bedroom, the kitchen).
        /// </summary>
        private static int OutdoorWalk(Room room, IntVec3 shut, Map map, int limit)
        {
            var passable = new Dictionary<Room, bool>();
            bool Passable(Room r)
            {
                if (r == null || r == room || r.IsDoorway || !Indoor(r))
                    return true;
                if (!passable.TryGetValue(r, out bool v))
                    passable[r] = v = WalkThrough(r);
                return v;
            }
            var dist = new Dictionary<IntVec3, int>();
            var queue = new Queue<IntVec3>();
            foreach (var d in Doors(room))
                if (d.Position != shut)
                {
                    dist[d.Position] = 0;
                    queue.Enqueue(d.Position);
                }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                int n0 = dist[c];
                Room here = c.GetRoom(map);
                if (here != null && !here.IsDoorway && here.UsesOutdoorTemperature)
                    return n0;
                if (n0 > limit)
                    continue;
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (n == shut || !n.InBounds(map) || dist.ContainsKey(n) || !n.Walkable(map) || !Passable(n.GetRoom(map)))
                        continue;
                    dist[n] = n0 + 1;
                    queue.Enqueue(n);
                }
            }
            return limit + 1;
        }
    }
}
