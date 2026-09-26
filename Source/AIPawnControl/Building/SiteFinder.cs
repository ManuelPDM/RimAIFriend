using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Finds sites for a room near the base (PHASE4.md §2): one walk from the base centre, grids of blocked cells with
    /// summed-area tables so each rectangle check is 4 lookups, then every footprint is laid out and scored.
    /// </summary>
    public class SiteFinder
    {
        public static readonly int[] Footprints = { 7, 6 }; // 5×5 and 4×4 inside

        public readonly Map map;
        public readonly IntVec3 center;
        public readonly SiteWeights weights;
        public readonly List<(IntVec3 pos, float radius)> foci;
        private readonly int w, h;
        private readonly int[] walk;
        private readonly bool[] reusable;
        private readonly int[] satBlocked, satRingBad, satDoorTouch, satTrees, satItems, satFertility;
        private readonly Dictionary<Room, bool> outdoorsCache = new Dictionary<Room, bool>();
        private readonly Dictionary<Room, int> doorCountCache = new Dictionary<Room, int>();
        private CellRect scanArea;
        private readonly HashSet<IntVec3> interactionSpots;

        public SiteFinder(Map map, IntVec3 center)
        {
            this.map = map;
            this.center = center;
            weights = SiteWeights.Load();
            foci = NoBuildFoci(map);
            w = map.Size.x;
            h = map.Size.z;
            walk = Walk(map, center, weights.maxWalk);

            // Footprints lie within 8 cells of a reached cell (the door's outside cell is reached).
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int i = 0; i < walk.Length; i++)
                if (walk[i] >= 0)
                {
                    int x = i % w, z = i / w;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                }
            scanArea = minX > maxX ? CellRect.Empty : CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy(8).ClipInsideMap(map);

            var blocked = new bool[w * h];
            var ringBad = new bool[w * h];
            var doorTouch = new bool[w * h];
            var trees = new bool[w * h];
            var items = new bool[w * h];
            var fertilityTenths = new int[w * h];
            reusable = new bool[w * h];
            interactionSpots = InteractionSpots(map);
            for (int i = 0; i < blocked.Length; i++)
            {
                var c = new IntVec3(i % w, 0, i / w);
                if (!scanArea.Contains(c))
                {
                    blocked[i] = ringBad[i] = true;
                    continue;
                }
                reusable[i] = IsReusableWall(c, map);
                blocked[i] = IsBlocked(c, out trees[i], out items[i]);
                ringBad[i] = blocked[i] && !reusable[i];
                fertilityTenths[i] = Mathf.Max(0, Mathf.RoundToInt((c.GetTerrain(map).fertility - 1f) * 10f));
            }
            for (int i = 0; i < blocked.Length; i++)
            {
                if (reusable[i])
                    continue;
                var c = new IntVec3(i % w, 0, i / w);
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (n.InBounds(map) && n.GetEdifice(map) is Building_Door)
                        doorTouch[i] = true;
                }
            }
            satBlocked = Sat(blocked);
            satRingBad = Sat(ringBad);
            satDoorTouch = Sat(doorTouch);
            satTrees = Sat(trees);
            satItems = Sat(items);
            satFertility = Sat(fertilityTenths);
        }

        /// <summary>Things whose meditation focus is hurt by artificial structures nearby (the anima tree), with the radius from their def.</summary>
        public static List<(IntVec3 pos, float radius)> NoBuildFoci(Map map)
        {
            var result = new List<(IntVec3, float)>();
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                var focus = def.GetCompProperties<CompProperties_MeditationFocus>();
                if (focus == null)
                    continue;
                float radius = 0f;
                foreach (var offset in focus.offsets)
                    if (offset is FocusStrengthOffset_ArtificialBuildings artificial)
                        radius = Mathf.Max(radius, artificial.radius);
                if (radius <= 0f)
                    continue;
                foreach (var t in map.listerThings.ThingsOfDef(def))
                    result.Add((t.Position, radius));
            }
            return result;
        }

        public static bool InFocusRadius(IntVec3 c, List<(IntVec3 pos, float radius)> foci)
        {
            foreach (var (pos, radius) in foci)
                if (c.InHorDistOf(pos, radius))
                    return true;
            return false;
        }

        /// <summary>Interaction cells of every thing on the map (blueprints and frames too): a wall there fails vanilla's placement.</summary>
        private static HashSet<IntVec3> InteractionSpots(Map map)
        {
            var spots = new HashSet<IntVec3>();
            foreach (var t in map.listerThings.AllThings)
            {
                ThingDef def = t is Blueprint || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def;
                if (def != null && def.HasSingleOrMultipleInteractionCells)
                    foreach (var c in ThingUtility.InteractionCellsWhenAt(def, t.Position, t.Rotation, map))
                        spots.Add(c);
            }
            return spots;
        }

        public static bool UnderOverheadMountain(IntVec3 c, Map map) => map.roofGrid.RoofAt(c)?.isThickRoof == true;

        /// <summary>Average of the Home area, else of colony buildings, else of colonists; snapped to a standable cell.</summary>
        public static IntVec3 BaseCenter(Map map)
        {
            var cells = map.areaManager.Home.ActiveCells.ToList();
            if (cells.Count == 0)
                cells = map.listerBuildings.allBuildingsColonist.Select(b => b.Position).ToList();
            if (cells.Count == 0)
                cells = map.mapPawns.FreeColonistsSpawned.Select(p => p.Position).ToList();
            IntVec3 avg = cells.Count == 0 ? map.Center
                : new IntVec3((int)cells.Average(c => c.x), 0, (int)cells.Average(c => c.z));
            IntVec3 snapped = CellFinder.StandableCellNear(avg, map, 12);
            return snapped.IsValid ? snapped : avg;
        }

        /// <summary>BFS over walkable, unfogged cells (doors pass), 4 neighbours. -1 = not reached within maxSteps.</summary>
        public static int[] Walk(Map map, IntVec3 start, int maxSteps, Func<IntVec3, bool> blockedExtra = null)
        {
            int w = map.Size.x;
            var dist = new int[w * map.Size.z];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = -1;
            if (!start.InBounds(map) || !start.Walkable(map))
                return dist;
            var queue = new Queue<IntVec3>();
            dist[start.z * w + start.x] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                int d = dist[c.z * w + c.x];
                if (d >= maxSteps)
                    continue;
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (!n.InBounds(map) || dist[n.z * w + n.x] >= 0 || !n.Walkable(map) || n.Fogged(map) || (blockedExtra != null && blockedExtra(n)))
                        continue;
                    dist[n.z * w + n.x] = d + 1;
                    queue.Enqueue(n);
                }
            }
            return dist;
        }

        public int WalkAt(IntVec3 c) => c.InBounds(map) ? walk[c.z * w + c.x] : -1;

        public static bool IsReusableWall(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.IsWall && e.Faction == Faction.OfPlayer && !e.def.building.isNaturalRock;
        }

        /// <summary>The hard rules of §2 for one cell. Trees and items aren't blocks, just clearing work.</summary>
        private bool IsBlocked(IntVec3 c, out bool tree, out bool item)
        {
            tree = item = false;
            if (c.InNoBuildEdgeArea(map) || c.Fogged(map) || !c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy)
                || map.zoneManager.ZoneAt(c) != null || map.planManager.PlanAt(c) != null
                || UnderOverheadMountain(c, map) || InFocusRadius(c, foci) || interactionSpots.Contains(c))
                return true;
            foreach (var t in c.GetThingList(map))
            {
                if (t is Blueprint || t is Frame || t.def.category == ThingCategory.Building)
                    return true;
                if (t.def.category == ThingCategory.Plant && t.def.plant.IsTree)
                    tree = true;
                else if (t.def.category == ThingCategory.Item)
                    item = true;
            }
            return IsIndoors(c.GetRoom(map));
        }

        public bool IsIndoors(Room room) => room != null && !room.IsDoorway && !Outdoors(room);

        private bool Outdoors(Room room)
        {
            if (!outdoorsCache.TryGetValue(room, out bool v))
                outdoorsCache[room] = v = room.UsesOutdoorTemperature;
            return v;
        }

        /// <summary>No role: vanilla gives a proper room with nothing role-defining the generic "Room" role, and others None.</summary>
        public static bool NoRole(Room room) => room.Role == RoomRoleDefOf.None || room.Role.defName == "Room";

        /// <summary>Vanilla has no hallway role: a hallway is an indoor room with no role and at least 2 doors.</summary>
        public bool IsHallway(Room room)
        {
            if (!IsIndoors(room) || !NoRole(room))
                return false;
            if (!doorCountCache.TryGetValue(room, out int n))
                doorCountCache[room] = n = DoorCount(room, map);
            return n >= 2;
        }

        public static int DoorCount(Room room, Map map)
        {
            var doors = new HashSet<Building_Door>();
            foreach (var b in room.BorderCells)
                if (b.InBounds(map) && b.GetEdifice(map) is Building_Door d)
                    doors.Add(d);
            return doors.Count;
        }

        // ---- summed-area tables ----

        private int[] Sat(bool[] grid) => Sat(grid.Select(b => b ? 1 : 0).ToArray());

        private int[] Sat(int[] grid)
        {
            var sat = new int[(w + 1) * (h + 1)];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    sat[(z + 1) * (w + 1) + x + 1] = grid[z * w + x]
                        + sat[z * (w + 1) + x + 1] + sat[(z + 1) * (w + 1) + x] - sat[z * (w + 1) + x];
            return sat;
        }

        private int Sum(int[] sat, CellRect r) =>
            sat[(r.maxZ + 1) * (w + 1) + r.maxX + 1] - sat[r.minZ * (w + 1) + r.maxX + 1]
            - sat[(r.maxZ + 1) * (w + 1) + r.minX] + sat[r.minZ * (w + 1) + r.minX];

        // ---- candidates ----

        /// <summary>Every footprint that passes the hard rules and gets a layout, scored, best first.</summary>
        public List<RoomPlan> Candidates(RoomKind kind)
        {
            var result = new List<RoomPlan>();
            if (scanArea.IsEmpty)
                return result;
            foreach (int size in Footprints)
                for (int z = scanArea.minZ; z + size - 1 <= scanArea.maxZ; z++)
                    for (int x = scanArea.minX; x + size - 1 <= scanArea.maxX; x++)
                    {
                        var rect = new CellRect(x, z, size, size);
                        if (Sum(satBlocked, rect.ContractedBy(1)) > 0 || Sum(satRingBad, rect) > 0 || Sum(satDoorTouch, rect) > 0)
                            continue;
                        var plan = Layout(kind, rect);
                        if (plan != null)
                            result.Add(plan);
                    }
            result.Sort((a, b) => b.score.CompareTo(a.score));
            return result;
        }

        private RoomPlan Layout(RoomKind kind, CellRect rect)
        {
            var plan = new RoomPlan { kind = kind, map = map, footprint = rect };
            foreach (var c in rect.EdgeCells)
                if (reusable[c.z * w + c.x])
                    plan.reusedWalls.Add(c);

            // The door: best walk from the base, then centred on its wall (vanilla's TryGetBestRectExteriorCell idea).
            float bestKey = float.MaxValue;
            foreach (var c in rect.EdgeCells)
            {
                if (IsCorner(rect, c))
                    continue;
                Rot4 side = SideOf(rect, c);
                IntVec3 outside = c + side.FacingCell;
                int d = WalkAt(outside);
                if (d < 0 || outside.GetEdifice(map) is Building_Door)
                    continue;
                if (plan.reusedWalls.Contains(c) && c.GetEdifice(map).def != ThingDefOf.Wall)
                    continue; // only a real wall can be replaced by a door blueprint
                Room room = outside.GetRoom(map);
                if (room == null || (IsIndoors(room) && !IsHallway(room)))
                    continue;
                float offCentre = side.IsHorizontal ? Mathf.Abs(c.z - rect.CenterCell.z) : Mathf.Abs(c.x - rect.CenterCell.x);
                float key = d * 10 + offCentre;
                if (key < bestKey)
                {
                    bestKey = key;
                    plan.door = c;
                    plan.doorOutside = outside;
                    plan.doorInside = c - side.FacingCell;
                }
            }
            if (bestKey == float.MaxValue)
                return null;
            plan.reusedWalls.Remove(plan.door);

            foreach (var c in rect.EdgeCells)
                if (c == plan.door)
                    plan.entries.Add(new PlanEntry(ThingDefOf.Door, c, Rot4.North));
                else if (!plan.reusedWalls.Contains(c))
                    plan.entries.Add(new PlanEntry(ThingDefOf.Wall, c, Rot4.North));

            if (!RoomPlacer.Place(plan))
                return null;
            Score(plan);
            return plan;
        }

        public static bool IsCorner(CellRect r, IntVec3 c) => (c.x == r.minX || c.x == r.maxX) && (c.z == r.minZ || c.z == r.maxZ);

        /// <summary>Which side of the rect an edge cell is on (the direction pointing out of the room).</summary>
        public static Rot4 SideOf(CellRect r, IntVec3 c) =>
            c.z == r.maxZ ? Rot4.North : c.z == r.minZ ? Rot4.South : c.x == r.maxX ? Rot4.East : Rot4.West;

        private void Score(RoomPlan plan)
        {
            CellRect rect = plan.footprint;
            plan.walk = WalkAt(plan.doorOutside);
            plan.trees = Sum(satTrees, rect);
            plan.items = Sum(satItems, rect);
            plan.inHome = map.areaManager.Home[rect.CenterCell];
            var sides = new HashSet<Rot4>();
            foreach (var c in plan.reusedWalls)
                if (!IsCorner(rect, c))
                    sides.Add(SideOf(rect, c));
            plan.sharedSides = sides.Count;

            int aligned = 0;
            for (int r = 0; r < 4; r++)
                if (ContinuesWallLine(rect, new Rot4(r)))
                    aligned++;
            int slivers = 0;
            foreach (var c in rect.EdgeCells)
            {
                if (IsCorner(rect, c) || plan.reusedWalls.Contains(c))
                    continue;
                IntVec3 step = SideOf(rect, c).FacingCell;
                IntVec3 gap = c + step, beyond = c + step * 2;
                if (beyond.InBounds(map) && gap.GetEdifice(map) == null && IsReusableWall(beyond, map))
                    slivers++;
            }
            int siteEdge = Mathf.Min(Mathf.Min(rect.minX, rect.minZ), Mathf.Min(w - 1 - rect.maxX, h - 1 - rect.maxZ));
            int shelter = Mathf.Clamp(EdgeDistance(rect.CenterCell) - EdgeDistance(center), -15, 15);

            var t = plan.terms;
            t.Clear();
            t["walk"] = weights.walk * plan.walk;
            t["shared"] = weights.sharedWallCell * plan.reusedWalls.Count + weights.sharedSide * plan.sharedSides;
            t["aligned"] = weights.alignedSide * aligned;
            t["slivers"] = weights.sliverCell * slivers;
            t["farmland"] = weights.fertility * Sum(satFertility, rect);
            t["shelter"] = weights.shelter * shelter + weights.nearEdge * Mathf.Max(0, 15 - siteEdge);
            t["clearing"] = weights.tree * plan.trees + weights.item * plan.items;
            t["home"] = plan.inHome ? weights.home : 0f;
            t["size"] = rect.Width == 7 ? weights.bigRoom : 0f;
            plan.score = 0f;
            foreach (var v in t.Values)
                plan.score += v;
        }

        private int EdgeDistance(IntVec3 c) => Mathf.Min(Mathf.Min(c.x, c.z), Mathf.Min(w - 1 - c.x, h - 1 - c.z));

        /// <summary>
        /// Does this side's wall line continue an existing player wall line within 10 cells beyond the footprint? The wall
        /// must run along the line (a neighbour on the line is a wall too), so a wall that only crosses it doesn't count.
        /// </summary>
        private bool ContinuesWallLine(CellRect rect, Rot4 side)
        {
            bool vertical = side == Rot4.East || side == Rot4.West;
            int line = side == Rot4.North ? rect.maxZ : side == Rot4.South ? rect.minZ : side == Rot4.East ? rect.maxX : rect.minX;
            int lo = vertical ? rect.minZ : rect.minX, hi = vertical ? rect.maxZ : rect.maxX;
            IntVec3 At(int along) => vertical ? new IntVec3(line, 0, along) : new IntVec3(along, 0, line);
            bool Wall(IntVec3 c) => c.InBounds(map) && IsReusableWall(c, map);
            for (int d = 1; d <= 10; d++)
                foreach (int along in new[] { lo - d, hi + d })
                    if (Wall(At(along)) && (Wall(At(along - 1)) || Wall(At(along + 1))))
                        return true;
            return false;
        }

        /// <summary>The best n that don't overlap each other and pass the validator.</summary>
        public static List<RoomPlan> TopSites(List<RoomPlan> sorted, int n, RoomValidator validator, ThingDef material)
        {
            var top = new List<RoomPlan>();
            foreach (var plan in sorted)
            {
                if (top.Count >= n)
                    break;
                if (top.Any(t => t.footprint.Overlaps(plan.footprint)))
                    continue;
                var failures = validator.Check(plan, material);
                if (failures.Count > 0)
                {
                    ModLog.Warning($"Site dropped by the validator (a site-finder bug): {string.Join("; ", failures)}\n{TextMap.Draw(plan)}");
                    continue;
                }
                top.Add(plan);
            }
            return top;
        }

        // ---- materials and words ----

        /// <summary>Up to 3 wall materials, most in storage first; padded with vanilla's default so it's never empty.</summary>
        public static List<ThingDef> Materials(Map map)
        {
            map.resourceCounter.UpdateResourceCounts(); // vanilla refreshes every 204 ticks, so it's stale while paused
            var list = GenStuff.AllowedStuffsFor(ThingDefOf.Wall)
                .Select(s => (stuff: s, count: map.resourceCounter.GetCount(s)))
                .Where(p => p.count > 0)
                .OrderByDescending(p => p.count)
                .Take(3)
                .Select(p => p.stuff)
                .ToList();
            ThingDef fallback = GenStuff.DefaultStuffFor(ThingDefOf.Wall);
            if (list.Count < 3 && fallback != null && !list.Contains(fallback))
                list.Add(fallback);
            return list;
        }

        public static string StockLine(Map map, List<ThingDef> materials) =>
            "In storage: " + string.Join(", ", materials.Select(m => $"{m.label} {map.resourceCounter.GetCount(m)}"));

        /// <summary>One site in words, with no coordinates (PHASE4.md §2).</summary>
        public string Describe(RoomPlan plan, char letter, List<ThingDef> materials)
        {
            var parts = new List<string> { Where(plan), $"{plan.InteriorSize}×{plan.InteriorSize}" };
            if (plan.sharedSides > 0)
                parts.Add(plan.sharedSides == 1 ? "shares 1 wall" : $"shares {plan.sharedSides} walls");
            parts.Add($"{plan.walk} tiles from the centre");
            var clearing = new List<string>();
            if (plan.trees > 0)
                clearing.Add(plan.trees == 1 ? "1 tree to cut" : $"{plan.trees} trees to cut");
            if (plan.items > 0)
                clearing.Add(plan.items == 1 ? "1 item to move" : $"{plan.items} items to move");
            parts.Add(clearing.Count > 0 ? string.Join(", ", clearing) : "open ground");

            var needs = new List<string>();
            var extras = new Dictionary<ThingDef, int>();
            foreach (var m in materials)
            {
                var cost = plan.Cost(m);
                cost.TryGetValue(m, out int n);
                needs.Add($"{n} {m.label}");
                foreach (var kv in cost)
                    if (!materials.Contains(kv.Key))
                        extras[kv.Key] = kv.Value;
            }
            string extra = extras.Count > 0 ? " plus " + string.Join(", ", extras.Select(kv => $"{kv.Value} {kv.Key.label}")) : "";
            return $"{letter}: {string.Join(", ", parts)}. Needs {string.Join(" or ", needs)}{extra}.";
        }

        private string Where(RoomPlan plan)
        {
            foreach (var c in plan.reusedWalls)
            {
                if (IsCorner(plan.footprint, c))
                    continue;
                Rot4 side = SideOf(plan.footprint, c);
                Room room = (c + side.FacingCell).GetRoom(map);
                if (!IsIndoors(room))
                    continue;
                string label = NoRole(room) ? "a room" : "the " + room.Role.label;
                return $"against {label}'s {Compass(side.Opposite)} wall";
            }
            string where = $"{Direction(center, plan.footprint.CenterCell)} of the base";
            foreach (var c in plan.footprint.ExpandedBy(3).ClipInsideMap(map))
                if (c.GetTerrain(map).IsWater)
                    return where + ", by the water";
            return where;
        }

        private static string Compass(Rot4 r) => r == Rot4.North ? "north" : r == Rot4.South ? "south" : r == Rot4.East ? "east" : "west";

        private static string Direction(IntVec3 from, IntVec3 to)
        {
            if ((to - from).LengthHorizontalSquared < 25)
                return "right next to the middle";
            float angle = Mathf.Atan2(to.z - from.z, to.x - from.x) * Mathf.Rad2Deg; // 0 = east, 90 = north
            string[] names = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
            int i = Mathf.RoundToInt((angle + 360f) % 360f / 45f) % 8;
            return names[i];
        }
    }
}
