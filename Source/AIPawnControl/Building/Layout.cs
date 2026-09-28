using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The base's ways out and what can bring doors inside (BASE_LAYOUT.md §5): a way out is a colony door between an
    /// indoor room and the outdoors; a room people may walk through has no bed and no meal source (§3). The lines the
    /// Base call offers: a hub the ways out open into (SiteFinder.Hubs), a door between neighbours, and closing a way out
    /// that isn't needed any more.
    /// </summary>
    public static class Layout
    {
        /// <summary>An indoor room with a colony door: a room of the base, not an ancient ruin.</summary>
        public static bool OfBase(Room room) => Ground.Indoor(room) && Doors(room).Any(d => d.Faction == Faction.OfPlayer);

        /// <summary>
        /// A room of the base that's for something: vanilla gives it a role, one of our projects built it (a hall has no
        /// role), or it joins 2+ doors. Not a pocket of ground the walls happen to close in.
        /// </summary>
        public static bool RealRoom(Room room) => OfBase(room)
            && (!Ground.NoRole(room) || BuildManager.Instance?.BuilderOf(room) != null || Doors(room).Count() >= 2);

        /// <summary>
        /// Walking through doesn't hurt it (§3): no bed (movement noise disturbs sleep) and no meal source (tracked-in
        /// filth raises food poisoning). Vanilla ties no other harm to foot traffic.
        /// </summary>
        public static bool WalkThrough(Room room)
        {
            if (!Ground.Indoor(room))
                return false;
            foreach (var t in room.ContainedAndAdjacentThings)
                if (t is Building b && room.ContainsCell(b.Position) && (b is Building_Bed || b.def.building?.isMealSource == true))
                    return false;
            return true;
        }

        /// <summary>A kind whose items make a walk-through room: no bed and no meal source among them.</summary>
        public static bool WalkThroughKind(RoomKindDef kind, Map map) =>
            kind.items.All(i => !(i.Resolve(map) is ThingDef d) || (!d.IsBed && d.building?.isMealSource != true));

        /// <summary>The rooms on either side of a door (its walkable neighbours), doorways left out.</summary>
        public static List<Room> Sides(Building_Door door)
        {
            var rooms = new List<Room>();
            foreach (var d in GenAdj.CardinalDirections)
            {
                IntVec3 n = door.Position + d;
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
            public Room room;       // the indoor room it serves
            public IntVec3 outside; // its cell on the outdoor side
        }

        /// <summary>Every colony door with an indoor room on one side and the outdoors that reach the map edge on the other.</summary>
        public static List<WayOut> WaysOut(Map map)
        {
            var result = new List<WayOut>();
            var openAir = new Dictionary<Room, bool>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_Door door))
                    continue;
                Room inside = null;
                IntVec3 outside = IntVec3.Invalid;
                foreach (var d in GenAdj.CardinalDirections)
                {
                    IntVec3 n = door.Position + d;
                    if (!n.InBounds(map) || !n.Walkable(map))
                        continue;
                    Room room = n.GetRoom(map);
                    if (Ground.Indoor(room))
                        inside = room;
                    else if (Ground.OpenAir(room, openAir))
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
                if (Sides(door).Any(r => r != room && Ground.Indoor(r)))
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
        public static string Name(Room room) => Ground.NoRole(room) ? "room" : room.Role.label;

        /// <summary>For [Colony]: "one block of 5 rooms, 2 buildings apart · 6 ways out".</summary>
        public static string Line(Map map)
        {
            int n = WaysOut(map).Count;
            string shape = Shape(map);
            return (shape != null ? shape + " · " : "") + (n == 1 ? "1 way out" : $"{n} ways out");
        }

        /// <summary>
        /// The base's shape (BASE_GROWTH.md §6.4): rooms that share a wall or a door make one building. "one block of 5
        /// rooms, 2 buildings apart", or null with no rooms.
        /// </summary>
        public static string Shape(Map map)
        {
            var rooms = map.regionGrid.AllRooms.Where(OfBase).ToList();
            if (rooms.Count == 0)
                return null;
            var group = rooms.ToDictionary(r => r, r => r);
            Room Find(Room r) => group[r] == r ? r : group[r] = Find(group[r]);
            foreach (var room in rooms)
                foreach (var c in room.BorderCells)
                {
                    if (!c.InBounds(map) || !(c.GetEdifice(map) is Building b) || !(b.def.IsWall || b is Building_Door))
                        continue;
                    foreach (var d in GenAdj.CardinalDirections)
                    {
                        IntVec3 n = c + d;
                        Room other = n.InBounds(map) ? n.GetRoom(map) : null;
                        if (other != null && other != room && group.ContainsKey(other))
                            group[Find(other)] = Find(room);
                    }
                }
            var sizes = rooms.GroupBy(Find).Select(g => g.Count()).OrderByDescending(s => s).ToList();
            string block = sizes[0] == 1 ? "one room" : $"one block of {sizes[0]} rooms";
            int apart = sizes.Count - 1;
            return apart == 0 ? block : $"{block}, {apart} {(apart == 1 ? "building" : "buildings")} apart";
        }

        // ---- doors between neighbours (§5.2a) ----

        public class NeighbourDoor
        {
            public IntVec3 cell;
            public Room from, into; // from: a room with no way inside yet; into: the room it opens into
            public string Label => $"connect the {Name(from)} to the {Name(into)} (a door in their shared wall)";
        }

        /// <summary>
        /// A door in a single player wall from a room that doesn't reach inside yet into a walk-through room. The door cell
        /// has walls on its other two sides and nothing built right in front of it. Doors never join two end rooms, so nobody
        /// walks through a bedroom or the kitchen.
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
                        float off = -Math.Min(Run(w, side, map), Run(w, -side, map));
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
        /// What the Base call offers for the layout: the ladder's next room as a hub the ways out open into (when people may
        /// walk through it and it fits), a plain hall, a way out that can close, and a door between neighbours. Each is
        /// found again when she picks it: the map may have changed while she thought.
        /// </summary>
        public static List<Option> Options(Map map, Ladder.Rung rung, SiteFinder finder, RoomValidator validator, List<ThingDef> materials)
        {
            var options = new List<Option>();
            string Cost(IEnumerable<PlanEntry> entries)
            {
                var plan = new RoomPlan { map = map };
                plan.entries.AddRange(entries);
                return SiteFinder.CostText(plan, materials);
            }
            if (rung?.kind != null && rung.waiting == null && WalkThroughKind(rung.kind, map)
                && finder.Hubs(rung.kind, validator, materials[0]).FirstOrDefault() is RoomPlan asRung)
                options.Add(HubOption("Next for the base", asRung, materials));
            if (finder.Hubs(RoomKindDef.Hall, validator, materials[0]).FirstOrDefault() is RoomPlan hall)
                options.Add(HubOption("Inside the base", hall, materials));
            if (Surplus(map).FirstOrDefault() is WayOut way)
                options.Add(new Option { group = "Inside the base", label = $"{CloseLabel(way)}: {Cost(new[] { new PlanEntry(ThingDefOf.Wall, way.door.Position, Rot4.North) })}",
                    apply = (p, m) => Close(p, way.door.Position, m) });
            if (NeighbourDoors(map).FirstOrDefault() is NeighbourDoor door)
                options.Add(new Option { group = "Inside the base", label = $"{door.Label}: {Cost(new[] { new PlanEntry(ThingDefOf.Door, door.cell, Rot4.North) })}; then its outside door can be closed",
                    apply = (p, m) => Connect(p, door.cell, m) });
            return options;
        }

        // ---- hubs ----

        /// <summary>"the barracks, kitchen and storeroom": the rooms whose doors the hub takes in.</summary>
        public static string HubRooms(RoomPlan hub)
        {
            var names = hub.broughtIn.Select(d => d.GetEdifice(hub.map) is Building_Door door && Sides(door).FirstOrDefault(Ground.Indoor) is Room room
                ? Name(room) : "room").ToList();
            return names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
        }

        /// <summary>"a storeroom the barracks and kitchen open into (4x6: 6 shelves; 2 doors come inside, 1 fewer way out): 80 wood".</summary>
        private static Option HubOption(string group, RoomPlan hub, List<ThingDef> materials)
        {
            var furniture = hub.Furniture.ToList();
            string items = furniture.Count == 0 ? "" : ": " + string.Join(", ", furniture.GroupBy(e => e.def)
                .Select(g => g.Count() > 1 ? $"{g.Count()} {Find.ActiveLanguageWorker.Pluralize(g.Key.label, g.Count())}" : g.Key.label));
            string saved = hub.waysOutSaved == 1 ? "1 fewer way out" : $"{hub.waysOutSaved} fewer ways out";
            string label = $"a {hub.kind.label} the {HubRooms(hub)} open into ({hub.SizeLabel.Replace('×', 'x')}{items}; "
                           + $"{hub.broughtIn.Count} doors come inside, {saved}): {SiteFinder.CostText(hub, materials)}";
            RoomKindDef kind = hub.kind;
            CellRect offered = hub.footprint;
            return new Option { group = group, label = label, apply = (p, m) => PlaceHub(p, kind, offered, m) };
        }

        /// <summary>The hub found again (the nearest to the one offered) and laid out like any room.</summary>
        private static string PlaceHub(Pawn pawn, RoomKindDef kind, CellRect offered, ThingDef material)
        {
            Map map = pawn.Map;
            var finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
            var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
            var plan = finder.Hubs(kind, validator, material, 3).OrderBy(h => h.footprint.CenterCell.DistanceToSquared(offered.CenterCell)).FirstOrDefault();
            if (plan == null)
                return $"Wanted a {kind.label} the others open into, but it doesn't fit there any more.";
            string where = "joining the " + HubRooms(plan);
            var project = BuildManager.Instance.Place(pawn, plan, material, validator, where);
            if (project == null)
                return $"Wanted a {kind.label} the others open into, but it doesn't fit there any more.";
            string marked = Supplies.MarkFor(pawn, project);
            return $"Laid out a {kind.label} {where} ({plan.waysOutSaved} fewer ways out, {material.label})." + (marked.Length > 0 ? " " + marked : "");
        }

        private static RoomKindDef Kind(string defName) => DefDatabase<RoomKindDef>.GetNamed(defName);

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

        /// <summary>"close the kitchen's outside door (it's reached from inside now)".</summary>
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
            var openAir = new Dictionary<Room, bool>();
            foreach (var way in ways)
                if (ReachesInside(way.room) && BuildManager.Instance.OurDoor(map, way.door.Position)
                    && rooms.All(r => OutdoorWalk(r, way.door.Position, map, limit, openAir) <= limit))
                    result.Add(way);
            return result;
        }

        /// <summary>
        /// Tiles from the room's doors to the outdoors with this door shut, up to limit + 1. The walk never crosses another
        /// room that shouldn't be walked through (a bedroom, the kitchen).
        /// </summary>
        private static int OutdoorWalk(Room room, IntVec3 shut, Map map, int limit, Dictionary<Room, bool> openAir)
        {
            var passable = new Dictionary<Room, bool>();
            bool Passable(Room r)
            {
                if (r == null || r == room || r.IsDoorway || !Ground.Indoor(r))
                    return true;
                if (!passable.TryGetValue(r, out bool v))
                    passable[r] = v = WalkThrough(r);
                return v;
            }
            IntVec3 hit = IntVec3.Invalid;
            var flood = Flood.Run(room.ExtentsClose.ExpandedBy(limit + 2).ClipInsideMap(map),
                Doors(room).Where(d => d.Position != shut).Select(d => d.Position),
                n => n != shut && n.Walkable(map) && Passable(n.GetRoom(map)),
                limit + 1,
                stop: c =>
                {
                    if (!Ground.OpenAir(c.GetRoom(map), openAir))
                        return false;
                    hit = c;
                    return true;
                });
            return hit.IsValid ? flood[hit] : limit + 1;
        }
    }
}
