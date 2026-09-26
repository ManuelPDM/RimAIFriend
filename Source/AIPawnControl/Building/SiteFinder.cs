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
        private readonly bool[] reusable, realWall, noEdifice, wallRunH, wallRunV, doorOk, spotBlocked;
        private readonly Dictionary<(RoomKindDef, int, int, int, int), List<PlanEntry>> templates = new Dictionary<(RoomKindDef, int, int, int, int), List<PlanEntry>>();
        public readonly Dictionary<string, long> timings = new Dictionary<string, long>();
        private readonly int[] satBlocked, satRingBad, satDoorTouch, satTrees, satItems, satFertility;
        private readonly Dictionary<Room, bool> outdoorsCache = new Dictionary<Room, bool>();
        private readonly Dictionary<Room, int> doorCountCache = new Dictionary<Room, int>();
        private CellRect scanArea;
        private readonly HashSet<IntVec3> interactionSpots;

        public SiteFinder(Map map, IntVec3 center)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            this.map = map;
            this.center = center;
            weights = SiteWeights.Load();
            foci = NoBuildFoci(map);
            w = map.Size.x;
            h = map.Size.z;
            walk = Walk(map, center, weights.maxWalk);
            timings["walk"] = clock.ElapsedMilliseconds;

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
            realWall = new bool[w * h];
            noEdifice = new bool[w * h];
            wallRunH = new bool[w * h];
            wallRunV = new bool[w * h];
            doorOk = new bool[w * h];
            spotBlocked = new bool[w * h];
            interactionSpots = InteractionSpots(map);
            timings["spots"] = clock.ElapsedMilliseconds;
            // Wall grids reach 12 cells past the scan area: alignment looks up to 10 beyond a footprint, slivers 2.
            CellRect wallArea = scanArea.IsEmpty ? CellRect.Empty : scanArea.ExpandedBy(12).ClipInsideMap(map);
            foreach (var c in wallArea)
            {
                int i = c.z * w + c.x;
                Building edifice = c.GetEdifice(map);
                noEdifice[i] = edifice == null;
                reusable[i] = IsReusableWall(c, map);
                realWall[i] = reusable[i] && edifice.def == ThingDefOf.Wall;
            }
            foreach (var c in wallArea)
            {
                int i = c.z * w + c.x, x = c.x, z = c.z;
                wallRunH[i] = reusable[i] && ((x > 0 && reusable[i - 1]) || (x < w - 1 && reusable[i + 1]));
                wallRunV[i] = reusable[i] && ((z > 0 && reusable[i - w]) || (z < h - 1 && reusable[i + w]));
            }
            for (int i = 0; i < blocked.Length; i++)
            {
                var c = new IntVec3(i % w, 0, i / w);
                if (!scanArea.Contains(c))
                {
                    blocked[i] = ringBad[i] = true;
                    continue;
                }
                if (walk[i] >= 0 && !(c.GetEdifice(map) is Building_Door))
                {
                    Room room = c.GetRoom(map);
                    doorOk[i] = room != null && (!IsIndoors(room) || IsHallway(room));
                }
                blocked[i] = IsBlocked(c, out trees[i], out items[i]);
                foreach (var t in c.GetThingList(map))
                    spotBlocked[i] |= t.def.passability != Traversability.Standable; // a tree or chunk on a work spot fails vanilla's placement
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
            timings["cells"] = clock.ElapsedMilliseconds;
            satBlocked = Sat(blocked);
            satRingBad = Sat(ringBad);
            satDoorTouch = Sat(doorTouch);
            satTrees = Sat(trees);
            satItems = Sat(items);
            satFertility = Sat(fertilityTenths);
            timings["grids"] = clock.ElapsedMilliseconds;
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

        private int[] Sat(bool[] grid)
        {
            var ints = new int[grid.Length];
            for (int i = 0; i < grid.Length; i++)
                ints[i] = grid[i] ? 1 : 0;
            return Sat(ints);
        }

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

        /// <summary>A footprint with its door and grid-only score. Cheap: a full RoomPlan is built only for the best ones.</summary>
        public class Candidate
        {
            public CellRect rect;
            public IntVec3 door;
            public Rot4 side;
            public float score;
            public int Width => rect.Width - 2;
            public int Height => rect.Height - 2;
        }

        /// <summary>Every footprint of this interior size (width along x, height along z) that passes the hard rules and has a door, best first.</summary>
        public List<Candidate> Candidates(int width, int height)
        {
            var result = new List<Candidate>();
            if (scanArea.IsEmpty)
                return result;
            for (int z = scanArea.minZ; z + height + 1 <= scanArea.maxZ; z++)
                for (int x = scanArea.minX; x + width + 1 <= scanArea.maxX; x++)
                {
                    var c = TryCandidate(new CellRect(x, z, width + 2, height + 2));
                    if (c != null)
                        result.Add(c);
                }
            result.Sort((a, b) => b.score.CompareTo(a.score));
            return result;
        }

        private Candidate TryCandidate(CellRect rect)
        {
            if (!rect.InBounds(map) || Sum(satBlocked, rect.ContractedBy(1)) > 0 || Sum(satRingBad, rect) > 0 || Sum(satDoorTouch, rect) > 0)
                return null;
            if (!ChooseDoor(rect, out IntVec3 door, out Rot4 side))
                return null;
            return new Candidate { rect = rect, door = door, side = side, score = Score(rect, door, side, null) };
        }

        /// <summary>The door: best walk from the base, then centred on its wall (vanilla's TryGetBestRectExteriorCell idea).</summary>
        private bool ChooseDoor(CellRect rect, out IntVec3 door, out Rot4 doorSide)
        {
            door = IntVec3.Invalid;
            doorSide = Rot4.North;
            float bestKey = float.MaxValue;
            IntVec3 centre = rect.CenterCell;
            foreach (var c in rect.EdgeCells)
            {
                if (IsCorner(rect, c))
                    continue;
                Rot4 side = SideOf(rect, c);
                IntVec3 outside = c + side.FacingCell;
                if (!outside.InBounds(map))
                    continue;
                int o = outside.z * w + outside.x;
                if (!doorOk[o])
                    continue;
                int i = c.z * w + c.x;
                if (reusable[i] && !realWall[i])
                    continue; // only a real wall can be replaced by a door blueprint
                float offCentre = side.IsHorizontal ? Mathf.Abs(c.z - centre.z) : Mathf.Abs(c.x - centre.x);
                float key = walk[o] * 10 + offCentre;
                if (key < bestKey)
                {
                    bestKey = key;
                    door = c;
                    doorSide = side;
                }
            }
            return bestKey < float.MaxValue;
        }

        /// <summary>The weighted score from the grids alone. With a plan, also fills its terms and description inputs.</summary>
        private float Score(CellRect rect, IntVec3 door, Rot4 doorSide, RoomPlan plan)
        {
            IntVec3 outside = door + doorSide.FacingCell;
            int walkTiles = walk[outside.z * w + outside.x];
            int reused = 0, slivers = 0;
            var sides = 0; // bit per side with a reused wall
            foreach (var c in rect.EdgeCells)
            {
                int i = c.z * w + c.x;
                bool corner = IsCorner(rect, c);
                if (reusable[i] && c != door)
                {
                    reused++;
                    if (!corner)
                        sides |= 1 << SideOf(rect, c).AsInt;
                    continue;
                }
                if (corner)
                    continue;
                IntVec3 step = SideOf(rect, c).FacingCell;
                IntVec3 gap = c + step, beyond = c + step * 2;
                if (beyond.InBounds(map) && noEdifice[gap.z * w + gap.x] && reusable[beyond.z * w + beyond.x])
                    slivers++;
            }
            int sharedSides = 0;
            for (int r = 0; r < 4; r++)
                if ((sides & (1 << r)) != 0)
                    sharedSides++;
            int aligned = 0;
            for (int r = 0; r < 4; r++)
                if (ContinuesWallLine(rect, new Rot4(r)))
                    aligned++;
            int trees = Sum(satTrees, rect), items = Sum(satItems, rect);
            bool inHome = map.areaManager.Home[rect.CenterCell];
            int siteEdge = Mathf.Min(Mathf.Min(rect.minX, rect.minZ), Mathf.Min(w - 1 - rect.maxX, h - 1 - rect.maxZ));
            int shelter = Mathf.Clamp(EdgeDistance(rect.CenterCell) - EdgeDistance(center), -15, 15);
            bool standard = Mathf.Min(rect.Width, rect.Height) - 2 >= StandardSize;

            float walkTerm = weights.walk * walkTiles;
            float shared = weights.sharedWallCell * reused + weights.sharedSide * sharedSides;
            float alignedTerm = weights.alignedSide * aligned;
            float sliverTerm = weights.sliverCell * slivers;
            float farmland = weights.fertility * Sum(satFertility, rect);
            float shelterTerm = weights.shelter * shelter + weights.nearEdge * Mathf.Max(0, 15 - siteEdge);
            float clearing = weights.tree * trees + weights.item * items;
            float home = inHome ? weights.home : 0f;
            float size = standard ? weights.bigRoom : 0f;
            float score = walkTerm + shared + alignedTerm + sliverTerm + farmland + shelterTerm + clearing + home + size;
            if (plan != null)
            {
                plan.walk = walkTiles;
                plan.sharedSides = sharedSides;
                plan.trees = trees;
                plan.items = items;
                plan.inHome = inHome;
                plan.score = score;
                var t = plan.terms;
                t.Clear();
                t["walk"] = walkTerm;
                t["shared"] = shared;
                t["aligned"] = alignedTerm;
                t["slivers"] = sliverTerm;
                t["farmland"] = farmland;
                t["shelter"] = shelterTerm;
                t["clearing"] = clearing;
                t["home"] = home;
                t["size"] = size;
            }
            return score;
        }

        public static bool IsCorner(CellRect r, IntVec3 c) => (c.x == r.minX || c.x == r.maxX) && (c.z == r.minZ || c.z == r.maxZ);

        /// <summary>Which side of the rect an edge cell is on (the direction pointing out of the room).</summary>
        public static Rot4 SideOf(CellRect r, IntVec3 c) =>
            c.z == r.maxZ ? Rot4.North : c.z == r.minZ ? Rot4.South : c.x == r.maxX ? Rot4.East : Rot4.West;

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
            bool[] run = vertical ? wallRunV : wallRunH;
            for (int d = 1; d <= 10; d++)
                for (int end = 0; end < 2; end++)
                {
                    int along = end == 0 ? lo - d : hi + d;
                    int x = vertical ? line : along, z = vertical ? along : line;
                    if (x >= 0 && z >= 0 && x < w && z < h && run[z * w + x])
                        return true;
                }
            return false;
        }

        // ---- plans ----

        /// <summary>
        /// The full plan for a candidate and a kind: walls, door and the kind's furniture, scored with every term. Null if
        /// the kind's items don't fit. The furniture comes from a template: the interior is empty, so the layout depends only
        /// on the kind, the size and where the door is, and it's worked out once per scan and shifted into place.
        /// </summary>
        public RoomPlan Plan(Candidate c, RoomKindDef kind)
        {
            if (!kind.Fits(c.Width, c.Height))
                return null;
            var plan = NewPlan(c, kind);
            var furniture = Template(plan, c);
            if (furniture == null)
                return null;
            IntVec3 origin = new IntVec3(c.rect.minX, 0, c.rect.minZ);
            foreach (var e in furniture)
            {
                var moved = e.Moved(origin);
                if (moved.def.hasInteractionCell || !moved.def.multipleInteractionCellOffsets.NullOrEmpty())
                    foreach (var spot in ThingUtility.InteractionCellsWhenAt(moved.def, moved.cell, moved.rot, map))
                        if (spotBlocked[spot.z * w + spot.x])
                            return null;
                plan.entries.Add(moved);
            }
            foreach (var cell in c.rect.EdgeCells)
                if (cell == c.door)
                    plan.entries.Add(new PlanEntry(ThingDefOf.Door, cell, Rot4.North));
                else if (!plan.reusedWalls.Contains(cell))
                    plan.entries.Add(new PlanEntry(ThingDefOf.Wall, cell, Rot4.North));
            Score(c.rect, c.door, c.side, plan);
            return plan;
        }

        private RoomPlan NewPlan(Candidate c, RoomKindDef kind)
        {
            var plan = new RoomPlan
            {
                kind = kind, map = map, footprint = c.rect,
                door = c.door, doorOutside = c.door + c.side.FacingCell, doorInside = c.door - c.side.FacingCell,
            };
            foreach (var cell in c.rect.EdgeCells)
                if (reusable[cell.z * w + cell.x] && cell != c.door)
                    plan.reusedWalls.Add(cell);
            return plan;
        }

        /// <summary>The kind's furniture relative to the footprint's corner, or null if it doesn't fit (cached per scan).</summary>
        private List<PlanEntry> Template(RoomPlan plan, Candidate c)
        {
            int offset = c.side.IsHorizontal ? c.door.z - c.rect.minZ : c.door.x - c.rect.minX;
            var key = (plan.kind, c.Width, c.Height, c.side.AsInt, offset);
            if (templates.TryGetValue(key, out var cached))
                return cached;
            var scratch = NewPlan(c, plan.kind);
            List<PlanEntry> result = null;
            if (RoomPlacer.Place(scratch))
            {
                IntVec3 origin = new IntVec3(c.rect.minX, 0, c.rect.minZ);
                result = scratch.entries.Select(e => e.Moved(-origin)).ToList();
            }
            templates[key] = result;
            return result;
        }

        /// <summary>The best n that don't overlap each other and pass the validator, as plans of this kind.</summary>
        public List<RoomPlan> TopSites(List<Candidate> sorted, int n, RoomKindDef kind, RoomValidator validator, ThingDef material)
        {
            var top = new List<RoomPlan>();
            foreach (var c in sorted)
            {
                if (top.Count >= n)
                    break;
                if (top.Any(t => t.footprint.Overlaps(c.rect)))
                    continue;
                var plan = Plan(c, kind);
                if (plan == null)
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

        // ---- sites and sizes (PHASE4.md §2, §5) ----

        public const int StandardSize = 5;

        /// <summary>Every interior size she may choose, width along x and height along z, both orientations.</summary>
        public static readonly List<(int w, int h)> Shapes = Enumerable.Range(4, 4).SelectMany(a => Enumerable.Range(4, 4).Select(b => (a, b))).ToList();

        /// <summary>The top 3 sites as validated plain rooms at the standard 5×5, else 4×4 where 5×5 fits nowhere.</summary>
        public List<RoomPlan> Sites(RoomValidator validator, ThingDef material, out List<Candidate> candidates, int n = 3)
        {
            candidates = Candidates(StandardSize, StandardSize);
            var sites = TopSites(candidates, n, RoomKindDef.Plain, validator, material);
            if (sites.Count == 0)
            {
                candidates = Candidates(4, 4);
                sites = TopSites(candidates, n, RoomKindDef.Plain, validator, material);
            }
            return sites;
        }

        /// <summary>The candidates of one shape whose interior contains the cell, best first.</summary>
        private List<Candidate> Around(IntVec3 cell, int width, int height)
        {
            var result = new List<Candidate>();
            for (int x = cell.x - width; x <= cell.x - 1; x++)
                for (int z = cell.z - height; z <= cell.z - 1; z++)
                {
                    var c = TryCandidate(new CellRect(x, z, width + 2, height + 2));
                    if (c != null)
                        result.Add(c);
                }
            result.Sort((a, b) => b.score.CompareTo(a.score));
            return result;
        }

        /// <summary>The biggest interior (by area) that fits at a site: a footprint whose interior holds the site's centre.</summary>
        public (int w, int h) MaxFit(RoomPlan site)
        {
            IntVec3 cell = site.Interior.CenterCell;
            foreach (var (sw, sh) in Shapes.OrderByDescending(s => s.w * s.h).ThenByDescending(s => Mathf.Min(s.w, s.h)))
                if (Around(cell, sw, sh).Count > 0)
                    return (sw, sh);
            return (site.Width, site.Height);
        }

        /// <summary>
        /// Her chosen kind and size at a site: the best validated plan of that size whose interior holds the site's centre (either
        /// orientation). If it doesn't fit there, or is below the kind's minimum, the nearest size that fits (smaller first on a
        /// tie). The note says what changed, or is null.
        /// </summary>
        public RoomPlan Fit(RoomPlan site, RoomKindDef kind, int width, int height, RoomValidator validator, ThingDef material, out string note)
        {
            note = null;
            IntVec3 cell = site.Interior.CenterCell;
            int area = width * height;
            var order = Shapes.Where(s => kind.Fits(s.w, s.h))
                .OrderBy(s => Mathf.Min(s.w, s.h) == Mathf.Min(width, height) && Mathf.Max(s.w, s.h) == Mathf.Max(width, height) ? 0 : 1)
                .ThenBy(s => Mathf.Abs(s.w * s.h - area))
                .ThenBy(s => s.w * s.h)
                .ToList();
            foreach (var (sw, sh) in order)
            {
                int tries = 0;
                foreach (var c in Around(cell, sw, sh))
                {
                    var plan = Plan(c, kind);
                    if (plan == null)
                        continue;
                    var failures = validator.Check(plan, material);
                    if (failures.Count > 0)
                    {
                        ModLog.Warning($"Fit dropped by the validator (a site-finder bug): {string.Join("; ", failures)}\n{TextMap.Draw(plan)}");
                        if (++tries >= 5)
                            break;
                        continue;
                    }
                    if (Mathf.Min(sw, sh) != Mathf.Min(width, height) || Mathf.Max(sw, sh) != Mathf.Max(width, height))
                        note = kind.Fits(width, height) ? $"a {width}×{height} {kind.label} doesn't fit there, so it's {plan.SizeLabel}"
                            : $"a {kind.label} needs at least {kind.minSize.x}×{kind.minSize.z}, so it's {plan.SizeLabel}";
                    return plan;
                }
            }
            return null;
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

        /// <summary>One site in words, with no coordinates (PHASE4.md §2): where, walls shared, distance, clearing, room to grow, wall cost.</summary>
        public string Describe(RoomPlan plan, char letter, List<ThingDef> materials, (int w, int h) maxFit)
        {
            var parts = new List<string> { Where(plan) };
            if (plan.sharedSides > 0)
                parts.Add(plan.sharedSides == 1 ? "shares 1 wall" : $"shares {plan.sharedSides} walls");
            parts.Add($"{plan.walk} tiles from the centre");
            var clearing = new List<string>();
            if (plan.trees > 0)
                clearing.Add(plan.trees == 1 ? "1 tree to cut" : $"{plan.trees} trees to cut");
            if (plan.items > 0)
                clearing.Add(plan.items == 1 ? "1 item to move" : $"{plan.items} items to move");
            parts.Add(clearing.Count > 0 ? string.Join(", ", clearing) : "open ground");
            parts.Add($"fits up to {maxFit.w}×{maxFit.h}");
            return $"{letter}: {string.Join(", ", parts)}. Walls and door at {plan.SizeLabel}: {CostText(plan, materials)}.";
        }

        /// <summary>"80 wood or 90 granite blocks", plus anything that doesn't come in the listed materials.</summary>
        public static string CostText(RoomPlan plan, List<ThingDef> materials)
        {
            var needs = new List<string>();
            var extras = new Dictionary<ThingDef, int>();
            bool usesMaterial = false;
            foreach (var m in materials)
            {
                var cost = plan.Cost(m);
                if (cost.TryGetValue(m, out int n))
                {
                    usesMaterial = true;
                    needs.Add($"{n} {m.label}");
                }
                foreach (var kv in cost)
                    if (!materials.Contains(kv.Key))
                        extras[kv.Key] = kv.Value;
            }
            var fixedCost = extras.Select(kv => $"{kv.Value} {kv.Key.label}").ToList();
            if (!usesMaterial)
                return fixedCost.Count > 0 ? string.Join(" + ", fixedCost) : "nothing";
            return string.Join(" or ", needs) + (fixedCost.Count > 0 ? " plus " + string.Join(", ", fixedCost) : "");
        }

        /// <summary>Where a room is, in words: "against the kitchen's west wall", "north-east of the base, by the water".</summary>
        public string Where(RoomPlan plan)
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
