using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>A common room shaped to the base (BASE_LAYOUT.md §5.3): its inside, its new walls and its one new door out.</summary>
    public class CommonPlan
    {
        public HashSet<IntVec3> cells = new HashSet<IntVec3>();
        public HashSet<IntVec3> walkway = new HashSet<IntVec3>(); // the paths joining its doors: kept clear of furniture
        public RoomKindDef kind;                                  // what it's furnished as, or null for a hall
        public List<PlanEntry> furniture = new List<PlanEntry>();
        public List<IntVec3> walls = new List<IntVec3>();     // new walls, the entrance excluded
        public List<IntVec3> entrances = new List<IntVec3>(); // new doors out; none when a ring door already leads out
        public List<Layout.WayOut> doors = new List<Layout.WayOut>(); // ways out it brings inside
        public float score;

        public CellRect Bounds => CellRect.FromCellList(cells).ExpandedBy(1);
        public int WaysOutSaved => doors.Count - entrances.Count;

        public string Label => $"{(kind != null ? "a " + kind.label + " as a common room" : "a common room")} the {Rooms} open into ({cells.Count} cells; {doors.Count} doors come inside"
                               + (entrances.Count == 1 ? ", 1 new door out" : entrances.Count > 1 ? $", {entrances.Count} new doors out" : "")
                               + $": {WaysOutSaved} fewer ways out; {walls.Count} walls"
                               + (entrances.Count == 1 ? " and a door" : entrances.Count > 1 ? $" and {entrances.Count} doors" : "") + ")";

        public string Rooms
        {
            get
            {
                var names = doors.Select(d => Layout.Name(d.room)).ToList();
                return names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
            }
        }

        /// <summary>The planned walls and door as a room plan, so BuildManager places and tracks it like any room.</summary>
        public RoomPlan ToPlan(Map map, RoomKindDef kind)
        {
            var plan = new RoomPlan { kind = kind, map = map, footprint = Bounds, door = IntVec3.Invalid, doorOutside = IntVec3.Invalid, doorInside = IntVec3.Invalid };
            foreach (var c in walls)
                plan.entries.Add(new PlanEntry(ThingDefOf.Wall, c, Rot4.North));
            foreach (var c in entrances)
                plan.entries.Add(new PlanEntry(ThingDefOf.Door, c, Rot4.North));
            plan.entries.AddRange(furniture);
            return plan;
        }
    }

    /// <summary>
    /// Finds common rooms (BASE_LAYOUT.md §5.3): the outside cells of ways out are joined by the shortest paths over free
    /// ground, pockets are filled where that costs no extra wall, and the ring gets new walls where there's none. Zones
    /// are never walled over or taken, so the shape bends around them.
    /// </summary>
    public class CommonRoomFinder
    {
        public const float RoofReach = 6.9f; // RoofCollapseUtility.RoofMaxSupportDistance
        private const int MaxCells = 320;   // AutoBuildRoofAreaSetter only roofs rooms up to this size

        private readonly Map map;
        private readonly int w;
        private readonly CellRect area;
        private readonly SiteWeights weights;
        private readonly bool[] solid, free, wallOk;
        private readonly int[] toTargets; // walking distance from the fields, stockpiles and the ground beyond the base
        private readonly HashSet<IntVec3> reachedBefore;
        public readonly List<Layout.WayOut> waysOut;
        public List<string> trace; // dev: why each try failed or what it scored

        public CommonRoomFinder(Map map)
        {
            this.map = map;
            w = map.Size.x;
            weights = SiteWeights.Load();
            waysOut = Layout.WaysOut(map);
            var buildings = map.listerBuildings.allBuildingsColonist;
            area = buildings.Count == 0 ? CellRect.Empty
                : CellRect.FromCellList(buildings.Select(b => b.Position)).ExpandedBy(12).ClipInsideMap(map);
            int n = w * map.Size.z;
            solid = new bool[n];
            free = new bool[n];
            wallOk = new bool[n];
            var spots = SiteFinder.InteractionSpots(map);
            var foci = SiteFinder.NoBuildFoci(map);
            var doorFronts = new HashSet<IntVec3>();
            foreach (var b in buildings)
                if (b is Building_Door)
                    for (int r = 0; r < 4; r++)
                        doorFronts.Add(b.Position + new Rot4(r).FacingCell);
            foreach (var c in area)
            {
                int i = c.z * w + c.x;
                solid[i] = c.Impassable(map) || c.GetEdifice(map) is Building_Door;
                if (solid[i] || !c.Walkable(map) || c.InNoBuildEdgeArea(map) || c.Fogged(map) || map.zoneManager.ZoneAt(c) != null
                    || map.planManager.PlanAt(c) != null || SiteFinder.UnderOverheadMountain(c, map) || SiteFinder.InFocusRadius(c, foci))
                    continue;
                Room room = c.GetRoom(map);
                if (room == null || room.IsDoorway || !room.UsesOutdoorTemperature)
                    continue;
                bool built = false;
                foreach (var t in c.GetThingList(map))
                    built |= t is Blueprint || t is Frame || t.def.category == ThingCategory.Building;
                if (built)
                    continue;
                free[i] = true;
                wallOk[i] = !spots.Contains(c) && !doorFronts.Contains(c) && c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy);
            }
            toTargets = TargetDistances();
            reachedBefore = Reach(null);
        }

        /// <summary>Walkable cells of the area reached from the ground beyond it (doors pass), with these cells walled.</summary>
        private HashSet<IntVec3> Reach(HashSet<IntVec3> walled)
        {
            var seen = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            foreach (var c in area.EdgeCells)
                if (c.Walkable(map) && seen.Add(c))
                    queue.Enqueue(c);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (In(n) && !seen.Contains(n) && n.Walkable(map) && (walled == null || !walled.Contains(n)) && seen.Add(n))
                        queue.Enqueue(n);
                }
            }
            return seen;
        }

        /// <summary>Whether the room would shut off something reached today (a door, a stretch of ground).</summary>
        private bool CutsOff(CommonPlan plan)
        {
            var walled = new HashSet<IntVec3>(plan.walls);
            var after = Reach(walled);
            foreach (var c in reachedBefore)
                if (!walled.Contains(c) && !after.Contains(c))
                    return true;
            return false;
        }

        private bool In(IntVec3 c) => area.Contains(c);
        private bool Solid(IntVec3 c) => In(c) && solid[c.z * w + c.x];
        private bool Free(IntVec3 c) => In(c) && free[c.z * w + c.x];
        private bool WallOk(IntVec3 c) => In(c) && wallOk[c.z * w + c.x];

        /// <summary>A cell the room may hold: free, and each neighbour is solid or free (a zone next to it would need a wall on the zone).</summary>
        private bool Roomable(IntVec3 c)
        {
            if (!Free(c))
                return false;
            for (int r = 0; r < 4; r++)
            {
                IntVec3 n = c + new Rot4(r).FacingCell;
                if (!Solid(n) && !Free(n))
                    return false;
            }
            return true;
        }

        /// <summary>BFS over walkable outdoor cells from the colony's outdoor zones and the ground outside the home area.</summary>
        private int[] TargetDistances()
        {
            var dist = new int[w * map.Size.z];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = -1;
            var queue = new Queue<IntVec3>();
            foreach (var c in area.ExpandedBy(1).ClipInsideMap(map))
            {
                bool target = !area.Contains(c) || (map.zoneManager.ZoneAt(c) != null && !c.Roofed(map));
                if (target && c.Walkable(map))
                {
                    dist[c.z * w + c.x] = 0;
                    queue.Enqueue(c);
                }
            }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (!In(n) || dist[n.z * w + n.x] >= 0 || !n.Walkable(map) || n.GetEdifice(map) is Building_Door)
                        continue;
                    Room room = n.GetRoom(map);
                    if (room == null || !room.UsesOutdoorTemperature)
                        continue;
                    dist[n.z * w + n.x] = dist[c.z * w + c.x] + 1;
                    queue.Enqueue(n);
                }
            }
            return dist;
        }

        /// <summary>The best common room per group of ways out that share free ground, best first.</summary>
        public List<CommonPlan> Find()
        {
            var result = new List<CommonPlan>();
            if (area.IsEmpty)
                return result;
            var seeds = waysOut.Where(d => Roomable(d.outside)).ToList();
            foreach (var group in Groups(seeds))
            {
                var members = group.ToList();
                CommonPlan best = Build(members);
                // Drop the door that helps least while it improves the score (§5.3 step 7).
                while (members.Count > 2)
                {
                    CommonPlan better = null;
                    Layout.WayOut drop = null;
                    foreach (var m in members)
                    {
                        var plan = Build(members.Where(x => x != m).ToList());
                        if (plan != null && (better == null || plan.score > better.score))
                        {
                            better = plan;
                            drop = m;
                        }
                    }
                    if (better == null || (best != null && better.score <= best.score))
                        break;
                    best = better;
                    members.Remove(drop);
                }
                if (best != null && best.doors.Count >= 2 && best.WaysOutSaved > 0 && best.score > 0)
                    result.Add(best);
            }
            result.Sort((a, b) => b.score.CompareTo(a.score));
            return result;
        }

        /// <summary>Seeds grouped by the free ground joining them.</summary>
        private List<List<Layout.WayOut>> Groups(List<Layout.WayOut> seeds)
        {
            var groups = new List<List<Layout.WayOut>>();
            var left = new List<Layout.WayOut>(seeds);
            while (left.Count > 0)
            {
                var start = left[0];
                var reach = Flood(new[] { start.outside });
                var group = left.Where(s => reach.Contains(s.outside)).ToList();
                left.RemoveAll(group.Contains);
                groups.Add(group);
            }
            return groups;
        }

        private HashSet<IntVec3> Flood(IEnumerable<IntVec3> from)
        {
            var seen = new HashSet<IntVec3>(from);
            var queue = new Queue<IntVec3>(seen);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (Roomable(n) && seen.Add(n))
                        queue.Enqueue(n);
                }
            }
            return seen;
        }

        /// <summary>The room joining these doors, or null if it can't be closed in, roofed or entered.</summary>
        public CommonPlan Build(List<Layout.WayOut> members)
        {
            var plan = TryBuild(members, out string why);
            trace?.Add($"{members.Count} doors [{string.Join(" ", members.Select(m => m.door.Position.x + "," + m.door.Position.z))}]: "
                       + (plan != null ? $"score {plan.score:0.0}, {plan.walls.Count} walls, {plan.cells.Count} cells" : why));
            return plan;
        }

        private CommonPlan TryBuild(List<Layout.WayOut> members, out string why)
        {
            why = null;
            if (members.Count < 2)
            {
                why = "fewer than 2 doors";
                return null;
            }
            var cells = new HashSet<IntVec3> { members[0].outside };
            // Spine: join the nearest unjoined door to what's there, by the shortest path over roomable cells.
            var todo = new HashSet<IntVec3>(members.Skip(1).Select(m => m.outside));
            while (todo.Count > 0)
            {
                todo.RemoveWhere(cells.Contains); // an earlier path ran through it
                if (todo.Count == 0)
                    break;
                var path = PathToNearest(cells, todo);
                if (path == null)
                {
                    why = "no path";
                    return null;
                }
                foreach (var c in path)
                    cells.Add(c);
                todo.Remove(path[path.Count - 1]);
            }
            return Close(cells, cells, out why);
        }

        /// <summary>
        /// Closes in these cells as a room (the walkway joins its doors): pockets filled where that costs no wall, then
        /// walls, roof, ways out and the cut-off check. Null with the reason if it can't be done.
        /// </summary>
        private CommonPlan Close(HashSet<IntVec3> start, HashSet<IntVec3> walkway, out string why)
        {
            why = null;
            var cells = new HashSet<IntVec3>(start);
            // Fill pockets: a neighbour that needs at most one new wall of its own costs nothing (it removes one).
            bool grew = true;
            while (grew && cells.Count <= MaxCells)
            {
                grew = false;
                foreach (var c in Border(cells).ToList())
                {
                    if (!Roomable(c))
                        continue;
                    int open = 0;
                    for (int r = 0; r < 4; r++)
                    {
                        IntVec3 n = c + new Rot4(r).FacingCell;
                        if (!cells.Contains(n) && !Solid(n))
                            open++;
                    }
                    if (open <= 1)
                    {
                        cells.Add(c);
                        grew = true;
                    }
                }
            }
            if (cells.Count > MaxCells)
            {
                why = "too big";
                return null;
            }
            var plan = new CommonPlan { cells = cells, walkway = new HashSet<IntVec3>(walkway) };
            foreach (var b in Border(cells))
            {
                if (Solid(b))
                    continue;
                if (!WallOk(b))
                {
                    why = $"no wall possible at {b.x},{b.z}";
                    return null;
                }
                plan.walls.Add(b);
            }
            // Roof: every cell within reach of a wall.
            var holders = Border(cells).ToList();
            foreach (var c in cells)
                if (!holders.Any(h => c.InHorDistOf(h, RoofReach)))
                {
                    why = "roof too wide";
                    return null;
                }
            plan.doors = waysOut.Where(d => cells.Contains(d.outside)).ToList();
            if (!PickEntrance(plan))
            {
                why = "no door out";
                return null;
            }
            if (CutsOff(plan))
            {
                why = "cuts something off";
                return null;
            }
            plan.score = plan.WaysOutSaved * weights.insideDoor - plan.walls.Count - plan.entrances.Count;
            return plan;
        }

        /// <summary>
        /// The hall furnished as a kind (BASE_LAYOUT.md §5.4): a block of the kind's size beside the walkway, the one
        /// costing the fewest new walls, with the kind's furniture placed in it and the walkway kept clear. Null if none fits.
        /// </summary>
        public CommonPlan WithKind(CommonPlan hall, RoomKindDef kind)
        {
            const int Tries = 40;
            int longSide = Mathf.Max(kind.size.x, kind.size.z);
            CellRect around = CellRect.FromCellList(hall.walkway).ExpandedBy(longSide + 1).ClipInsideMap(map);
            var blocks = new List<(CellRect block, int added)>();
            foreach (var (bw, bh) in new[] { (kind.size.x, kind.size.z), (kind.size.z, kind.size.x) }.Distinct())
                for (int x = around.minX; x + bw - 1 <= around.maxX; x++)
                    for (int z = around.minZ; z + bh - 1 <= around.maxZ; z++)
                    {
                        var block = new CellRect(x, z, bw, bh);
                        int added = 0;
                        bool ok = true;
                        foreach (var c in block)
                        {
                            if (hall.walkway.Contains(c) || (!hall.cells.Contains(c) && !Roomable(c)))
                            {
                                ok = false;
                                break;
                            }
                            if (!hall.cells.Contains(c))
                                added++;
                        }
                        if (ok && RoomPlacer.Adjacent(block).Any(hall.walkway.Contains))
                            blocks.Add((block, added));
                    }
            CommonPlan best = null;
            foreach (var (block, _) in blocks.OrderBy(b => b.added).Take(Tries))
            {
                var cells = new HashSet<IntVec3>(hall.cells);
                cells.UnionWith(block);
                var plan = Close(cells, hall.walkway, out string why);
                if (plan == null || (best != null && plan.score <= best.score))
                {
                    trace?.Add($"  {kind.label} block {block}: {why ?? "not better"}");
                    continue;
                }
                if (!Furnish(plan, kind, block))
                {
                    trace?.Add($"  {kind.label} block {block}: furniture doesn't fit");
                    continue;
                }
                trace?.Add($"  {kind.label} block {block}: score {plan.score:0.0}, {plan.walls.Count} walls");
                best = plan;
            }
            return best;
        }

        /// <summary>
        /// Places the kind's furniture in the block with the room placer: the block is its inside, and its "door" is the
        /// walkway cell next to it nearest its middle. Nothing may stand in front of a way out.
        /// </summary>
        private bool Furnish(CommonPlan plan, RoomKindDef kind, CellRect block)
        {
            CellRect footprint = block.ExpandedBy(1);
            IntVec3 door = footprint.EdgeCells.Where(c => !SiteFinder.IsCorner(footprint, c) && plan.walkway.Contains(c))
                .OrderBy(c => c.DistanceToSquared(block.CenterCell)).FirstOrDefault();
            if (!door.IsValid || !plan.walkway.Contains(door))
                return false;
            Rot4 side = SiteFinder.SideOf(footprint, door);
            var scratch = new RoomPlan { kind = kind, map = map, footprint = footprint, door = door, doorInside = door - side.FacingCell, doorOutside = door + side.FacingCell };
            var keepFree = plan.entrances.SelectMany(e => GenAdj.CardinalDirections.Select(d => e + d)).Where(plan.cells.Contains);
            if (!RoomPlacer.Place(scratch, keepFree))
                return false;
            plan.kind = kind;
            plan.furniture = scratch.Furniture.ToList();
            return true;
        }

        /// <summary>Cells next to the set, outside it (4 neighbours).</summary>
        private static IEnumerable<IntVec3> Border(HashSet<IntVec3> cells)
        {
            var seen = new HashSet<IntVec3>();
            foreach (var c in cells)
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (!cells.Contains(n) && seen.Add(n))
                        yield return n;
                }
        }

        private List<IntVec3> PathToNearest(HashSet<IntVec3> from, HashSet<IntVec3> targets)
        {
            var parent = new Dictionary<IntVec3, IntVec3>();
            var queue = new Queue<IntVec3>();
            foreach (var c in from)
            {
                parent[c] = IntVec3.Invalid;
                queue.Enqueue(c);
            }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                if (targets.Contains(c))
                {
                    var path = new List<IntVec3>();
                    for (var p = c; p.IsValid && !from.Contains(p); p = parent[p])
                        path.Add(p);
                    path.Reverse();
                    return path;
                }
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (!parent.ContainsKey(n) && Roomable(n))
                    {
                        parent[n] = c;
                        queue.Enqueue(n);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Its ways out: a door already on the ring that leads outdoors, else a new wall cell turned into a door, the room
        /// on one side and open ground on the other, where the walk to the fields, stockpiles and the ground beyond the
        /// base is shortest (a straight stretch of wall over a corner). While some door it brings inside is more than
        /// outdoorWalk tiles from a way out, another door goes in nearest that one.
        /// </summary>
        private bool PickEntrance(CommonPlan plan)
        {
            var wallSet = new HashSet<IntVec3>(plan.walls);
            var outs = new List<IntVec3>(); // inside cells next to a way out
            foreach (var b in Border(plan.cells))
                if (b.GetEdifice(map) is Building_Door d && !waysOut.Any(x => x.door == d) && Layout.Sides(d).Any(r => r.UsesOutdoorTemperature))
                    outs.AddRange(plan.cells.Where(c => c.AdjacentToCardinal(b)));
            const int CornerCost = 3;
            bool Enclosing(IntVec3 x) => wallSet.Contains(x) || Solid(x);
            var candidates = new List<(IntVec3 door, IntVec3 inside, int cost)>();
            foreach (var c in plan.walls)
                for (int r = 0; r < 2; r++)
                {
                    IntVec3 along = new Rot4(r).FacingCell, across = new Rot4(r + 1).FacingCell;
                    if (plan.cells.Contains(c + along) || plan.cells.Contains(c - along))
                        continue;
                    bool straight = Enclosing(c + along) && Enclosing(c - along);
                    foreach (var sign in new[] { 1, -1 })
                    {
                        IntVec3 inside = c - across * sign, outside = c + across * sign;
                        if (!plan.cells.Contains(inside) || plan.cells.Contains(outside) || wallSet.Contains(outside) || !In(outside) || !outside.Walkable(map))
                            continue;
                        int d = toTargets[outside.z * w + outside.x];
                        if (d >= 0)
                            candidates.Add((c, inside, d + (straight ? 0 : CornerCost)));
                    }
                }
            while (true)
            {
                // The door it brings inside that is farthest from a way out, walking inside the room.
                Dictionary<IntVec3, int> fromWorst = null;
                if (outs.Count > 0)
                {
                    var dist = Distances(plan.cells, outs);
                    IntVec3 worst = IntVec3.Invalid;
                    int far = -1;
                    foreach (var m in plan.doors)
                    {
                        int dm = dist.TryGetValue(m.outside, out int v) ? v : int.MaxValue;
                        if (dm > far)
                        {
                            far = dm;
                            worst = m.outside;
                        }
                    }
                    if (far <= weights.outdoorWalk)
                        return true;
                    fromWorst = Distances(plan.cells, new[] { worst });
                }
                var left = candidates.Where(k => !plan.entrances.Contains(k.door)).ToList();
                if (left.Count == 0 || plan.entrances.Count >= 3)
                    return false;
                var pick = left.OrderBy(k => (fromWorst != null && fromWorst.TryGetValue(k.inside, out int dw) ? dw : 0) + k.cost).First();
                plan.entrances.Add(pick.door);
                plan.walls.Remove(pick.door);
                wallSet.Remove(pick.door);
                outs.Add(pick.inside);
            }
        }

        /// <summary>Walking distance inside the cells from the sources (4 neighbours).</summary>
        private static Dictionary<IntVec3, int> Distances(HashSet<IntVec3> cells, IEnumerable<IntVec3> sources)
        {
            var dist = new Dictionary<IntVec3, int>();
            var queue = new Queue<IntVec3>();
            foreach (var src in sources)
                if (!dist.ContainsKey(src))
                {
                    dist[src] = 0;
                    queue.Enqueue(src);
                }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 n = c + new Rot4(r).FacingCell;
                    if (cells.Contains(n) && !dist.ContainsKey(n))
                    {
                        dist[n] = dist[c] + 1;
                        queue.Enqueue(n);
                    }
                }
            }
            return dist;
        }

        /// <summary>For the dev log: the room over the map around it. North is up.</summary>
        public string Draw(CommonPlan plan)
        {
            var walls = new HashSet<IntVec3>(plan.walls);
            var furniture = new HashSet<IntVec3>(plan.furniture.SelectMany(e => e.Rect.Cells));
            CellRect view = plan.Bounds.ExpandedBy(2).ClipInsideMap(map);
            var sb = new StringBuilder();
            for (int z = view.maxZ; z >= view.minZ; z--)
            {
                sb.Append(z.ToString().PadLeft(4)).Append(' ');
                for (int x = view.minX; x <= view.maxX; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    char ch = plan.entrances.Contains(c) ? 'E' : walls.Contains(c) ? 'N' : furniture.Contains(c) ? 'F'
                        : plan.walkway.Contains(c) ? '=' : plan.cells.Contains(c) ? ','
                        : c.GetEdifice(map) is Building_Door ? 'D' : c.GetEdifice(map)?.def == ThingDefOf.Wall ? '#' : Solid(c) ? 'X'
                        : map.zoneManager.ZoneAt(c) is Zone_Growing ? 'g' : map.zoneManager.ZoneAt(c) != null ? 's'
                        : Layout.Indoor(c.GetRoom(map)) ? '_' : Free(c) ? '.' : '?';
                    sb.Append(ch);
                }
                sb.AppendLine();
            }
            sb.Append($"     x from {view.minX}. E new door, N new wall, F furniture, = walkway, , inside, # wall, D door, X other solid, g field, s stockpile, _ indoors, . free, ? blocked");
            return sb.ToString();
        }
    }
}
