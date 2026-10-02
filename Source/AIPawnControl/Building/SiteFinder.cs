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
    /// summed-area tables so each rectangle check is 4 lookups, then every footprint is laid out and scored. A hub
    /// (BASE_LAYOUT.md) is the same, with existing ways out allowed in its wall ring: the rooms behind them open into it.
    /// </summary>
    public class SiteFinder
    {
        public const int StandardSize = 5;

        /// <summary>Every interior size she may choose, width along x and height along z, both orientations.</summary>
        public static readonly List<(int w, int h)> Shapes = Enumerable.Range(4, 4).SelectMany(a => Enumerable.Range(4, 4).Select(b => (a, b))).ToList();

        /// <summary>The sizes Fit may use: up to 10 a side, for rooms that grow with the colony (a dining hall, BASE_GROWTH.md §6.1).</summary>
        public static readonly List<(int w, int h)> FitShapes = Enumerable.Range(4, 7).SelectMany(a => Enumerable.Range(4, 7).Select(b => (a, b))).ToList();

        /// <summary>FitShapes plus the 3-wide ones, which only a kind whose minimum allows them takes (a bathroom, HYGIENE.md §3).</summary>
        private static readonly List<(int w, int h)> FitShapesNarrow = Enumerable.Range(3, 8).SelectMany(a => Enumerable.Range(3, 8).Select(b => (a, b))).ToList();

        /// <summary>A hub is at most as wide as the widest room; its length is bounded by outdoorWalk (TryHub).</summary>
        public const int HubMaxWidth = 7;

        /// <summary>Hub plans validated per search at most: the validator's floods are the cost.</summary>
        private const int HubTries = 40;

        public readonly Map map;
        public readonly IntVec3 center;
        public readonly SiteWeights weights;
        public readonly List<(IntVec3 pos, float radius)> foci;
        private readonly int w, h;
        private readonly Flood walk;       // from the base centre
        private readonly Flood reach;      // from the base centre and the open ground along the base's outer walls: where sites are looked for
        private readonly Flood heartWalk;  // from the heart's doors, or null: what the walk term measures (BASE_GROWTH.md §6.1)
        private readonly string heartName;
        private readonly Flood toOutdoors; // tiles from the outdoors, walking through the base (doors pass)
        private readonly bool[] reusable, realWall, noEdifice, wallRunH, wallRunV, doorOk, doorInside, spotBlocked;
        private readonly Dictionary<Room, bool> walkThroughCache = new Dictionary<Room, bool>();
        private readonly Dictionary<Room, bool> openAir = new Dictionary<Room, bool>(); // Ground.OpenAir per room, for this scan
        // Keyed by the kind's items too: a room the game asks for gets its items per ask (AskedFor.Ask.Apply).
        private readonly Dictionary<(RoomKindDef, List<RoomItem>, int, int, int, int), List<PlanEntry>> templates = new Dictionary<(RoomKindDef, List<RoomItem>, int, int, int, int), List<PlanEntry>>();
        private readonly AreaSums blocked, ringBad, doorTouch, trees, items, fertility;
        private readonly Dictionary<IntVec3, IntVec3> wayOutFront = new Dictionary<IntVec3, IntVec3>(); // a way out a hub may take in → its outside cell
        private readonly bool[] wayOutDoor; // wayOutFront's doors as a grid, for the per-footprint door choice
        private readonly AreaSums wayOutDoors, wayOutFronts, otherDoorTouch;
        private readonly AreaSums reusableSums, built; // styles (BASE_GROWTH.md §6.4): walls a site can share; anything built or planned
        private readonly CellRect scanArea;
        private readonly HashSet<IntVec3> interactionSpots;

        // The kind being placed (For): rooms its goods flow to or from, walked from their doors, and the temperature it holds.
        private readonly List<(Flood walk, string name)> goodsLinks = new List<(Flood, string)>();
        private float kindTemp = float.NaN;
        private bool anyHeldTemp;
        private readonly Dictionary<Room, float> heldTemp = new Dictionary<Room, float>();

        public SiteFinder(Map map, IntVec3 center)
        {
            this.map = map;
            this.center = center;
            weights = SiteWeights.Load();
            foci = NoBuildFoci(map);
            w = map.Size.x;
            h = map.Size.z;
            walk = Walk(map, center, weights.maxWalk);
            // Ground within maxWalk of the base's outside, not only of its centre: a big block whose doors face one way still
            // has ground on its far side (BASE_GROWTH.md build checks).
            // Only the base's own rooms' walls: a far defensive line or outpost doesn't stretch the search.
            var edge = new HashSet<IntVec3>();
            foreach (var room in map.regionGrid.AllRooms.Where(Layout.OfBase))
                foreach (var b in room.BorderCells)
                {
                    if (!b.InBounds(map) || !(b.GetEdifice(map) is Building e) || !(e.def.IsWall || e is Building_Door))
                        continue;
                    foreach (var d in GenAdj.CardinalDirections)
                    {
                        IntVec3 n = b + d;
                        if (n.InBounds(map) && n.Walkable(map) && !n.Fogged(map) && Ground.OpenAir(n.GetRoom(map), openAir))
                            edge.Add(n);
                    }
                }
            reach = edge.Count == 0 ? walk
                : Flood.Run(Flood.All(map), walk.Reached.Count > 0 ? edge.Append(walk.Reached[0]) : edge, c => c.Walkable(map) && !c.Fogged(map), weights.maxWalk);
            if (Heart(map) is Room heart)
            {
                heartName = Layout.Name(heart);
                heartWalk = Flood.Run(Flood.All(map), Layout.Doors(heart).Select(d => d.Position), c => c.Walkable(map) && !c.Fogged(map), weights.maxWalk * 2);
            }

            // Footprints lie within 8 cells of a reached cell (the door's outside cell is reached).
            scanArea = reach.Reached.Count == 0 ? CellRect.Empty : CellRect.FromCellList(reach.Reached).ExpandedBy(8).ClipInsideMap(map);

            var blockedCells = new bool[w * h];
            var ringBadCells = new bool[w * h];
            var doorTouchCells = new bool[w * h];
            var treeCells = new bool[w * h];
            var itemCells = new bool[w * h];
            var fertilityTenths = new int[w * h];
            reusable = new bool[w * h];
            realWall = new bool[w * h];
            noEdifice = new bool[w * h];
            wallRunH = new bool[w * h];
            wallRunV = new bool[w * h];
            doorOk = new bool[w * h];
            doorInside = new bool[w * h];
            spotBlocked = new bool[w * h];
            interactionSpots = InteractionSpots(map);
            // Wall grids reach 12 cells past the scan area: alignment looks up to 10 beyond a footprint, slivers 2.
            CellRect wallArea = scanArea.IsEmpty ? CellRect.Empty : scanArea.ExpandedBy(12).ClipInsideMap(map);
            toOutdoors = ToOutdoors(wallArea);
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
            for (int i = 0; i < blockedCells.Length; i++)
            {
                var c = new IntVec3(i % w, 0, i / w);
                if (!scanArea.Contains(c))
                {
                    blockedCells[i] = ringBadCells[i] = true;
                    continue;
                }
                if (reach.Has(c) && !(c.GetEdifice(map) is Building_Door))
                {
                    Room room = c.GetRoom(map);
                    // Outdoors, or a room people may walk through (BASE_LAYOUT.md §5.6) with nothing built where the door opens,
                    // close enough to the outdoors (outdoorWalk).
                    doorOk[i] = room != null && (Ground.OpenAir(room, openAir) || (WalkThrough(room) && Layout.RealRoom(room) && !interactionSpots.Contains(c)
                        && c.GetThingList(map).All(t => t.def.category != ThingCategory.Building) && toOutdoors.Has(c) && toOutdoors[c] + 1 <= weights.outdoorWalk));
                    doorInside[i] = doorOk[i] && Ground.Indoor(room);
                }
                blockedCells[i] = IsBlocked(c, out treeCells[i], out itemCells[i]);
                foreach (var t in c.GetThingList(map))
                    spotBlocked[i] |= t.def.passability != Traversability.Standable; // a tree or chunk on a work spot fails vanilla's placement
                ringBadCells[i] = blockedCells[i] && !reusable[i];
                fertilityTenths[i] = Mathf.Max(0, Mathf.RoundToInt((c.GetTerrain(map).fertility - 1f) * 10f));
            }

            // Hubs may take in the ways out of real rooms (not pockets). Every other door keeps rooms at a distance, as before.
            foreach (var way in Layout.WaysOut(map))
                if (Layout.RealRoom(way.room))
                    wayOutFront[way.door.Position] = way.outside;
            wayOutDoor = new bool[w * h];
            var wayOutFrontCells = new bool[w * h];
            var otherTouchCells = new bool[w * h];
            foreach (var kv in wayOutFront)
            {
                wayOutDoor[kv.Key.z * w + kv.Key.x] = true;
                wayOutFrontCells[kv.Value.z * w + kv.Value.x] = true;
            }
            // The cells beside a door (4 neighbours), found from the doors instead of looking around every cell of the map.
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                if (!(thing is Building_Door door))
                    continue;
                foreach (var n in door.OccupiedRect())
                    foreach (var d in GenAdj.CardinalDirections)
                    {
                        var c = n + d;
                        int i = c.z * w + c.x;
                        if (!c.InBounds(map) || reusable[i])
                            continue;
                        doorTouchCells[i] = true;
                        if (!wayOutFront.ContainsKey(n) && !wayOutDoor[i])
                            otherTouchCells[i] = true;
                    }
            }
            var builtCells = new bool[w * h];
            foreach (var c in wallArea)
                builtCells[c.z * w + c.x] = !noEdifice[c.z * w + c.x] || c.GetThingList(map).Any(t => t is Blueprint || t is Frame);
            reusableSums = new AreaSums(map, reusable);
            built = new AreaSums(map, builtCells);
            blocked = new AreaSums(map, blockedCells);
            ringBad = new AreaSums(map, ringBadCells);
            doorTouch = new AreaSums(map, doorTouchCells);
            trees = new AreaSums(map, treeCells);
            items = new AreaSums(map, itemCells);
            fertility = new AreaSums(map, fertilityTenths);
            wayOutDoors = new AreaSums(map, wayOutDoor);
            wayOutFronts = new AreaSums(map, wayOutFrontCells);
            otherDoorTouch = new AreaSums(map, otherTouchCells);
        }

        /// <summary>
        /// Scores sites for this kind from now on (BASE_GROWTH.md §6.2): the walk to every room its goods flow to or from (a
        /// kitchen: the storeroom's raw food, the dining room's meals), and walls shared with rooms held at another
        /// temperature. Null: no kind, only the general terms.
        /// </summary>
        public void For(RoomKindDef kind)
        {
            goodsLinks.Clear();
            kindTemp = float.NaN;
            anyHeldTemp = false;
            if (kind == null)
                return;
            var mine = Goods.OfKind(kind, map);
            if (!mine.Empty)
                foreach (var room in map.regionGrid.AllRooms.Where(Layout.OfBase))
                    if (mine.LinkedTo(Goods.OfRoom(room)))
                        goodsLinks.Add((Flood.Run(Flood.All(map), Layout.Doors(room).Select(d => d.Position), c => c.Walkable(map) && !c.Fogged(map), weights.maxWalk),
                            Layout.Name(room)));
            if (!float.IsNaN(kind.holdTemperature) && kind.items.Any(i => i.Resolve(map)?.GetCompProperties<CompProperties_TempControl>() != null))
                kindTemp = kind.holdTemperature;
            anyHeldTemp = !float.IsNaN(kindTemp) || map.listerBuildings.allBuildingsColonist.Any(b => b.TryGetComp<CompTempControl>() != null);
        }

        /// <summary>The temperature a room is held at: its cooler's or heater's target, else a comfortable 21°C; NaN if none is set.</summary>
        private float HeldTemp(Room room)
        {
            if (!heldTemp.TryGetValue(room, out float t))
            {
                t = float.NaN;
                foreach (var thing in room.ContainedAndAdjacentThings)
                    if (thing.TryGetComp<CompTempControl>() is CompTempControl control && Needs.Controls(thing, room))
                    {
                        t = control.targetTemperature;
                        break;
                    }
                heldTemp[room] = t;
            }
            return t;
        }

        /// <summary>
        /// Walking distance from the outdoors into the base's rooms (doors pass). It never crosses a room that shouldn't be
        /// walked through (a bedroom, the kitchen).
        /// </summary>
        private Flood ToOutdoors(CellRect area) =>
            Flood.Run(area, area.Cells.Where(c => Ground.OpenAir(c.GetRoom(map), openAir) && c.Walkable(map)),
                n => n.Walkable(map) && (!(n.GetRoom(map) is Room room) || !Ground.Indoor(room) || WalkThrough(room)));

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
        public static HashSet<IntVec3> InteractionSpots(Map map)
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

        /// <summary>
        /// The heart (BASE_GROWTH.md §6.1): the indoor room with the most eating surfaces, where the whole colony meets, or
        /// null. Once there is one, every walk is measured from it.
        /// </summary>
        public static Room Heart(Map map) =>
            map.listerBuildings.allBuildingsColonist.Where(b => b.def.surfaceType == SurfaceType.Eat)
                .Select(b => b.GetRoom()).Where(Ground.Indoor)
                .GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.CellCount).FirstOrDefault()?.Key;

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

        /// <summary>Steps over walkable, unfogged cells (doors pass) from a start cell, up to maxSteps.</summary>
        public static Flood Walk(Map map, IntVec3 start, int maxSteps) =>
            Flood.Run(Flood.All(map), start.InBounds(map) && start.Walkable(map) ? new[] { start } : new IntVec3[0],
                c => c.Walkable(map) && !c.Fogged(map), maxSteps);

        public int WalkAt(IntVec3 c) => walk[c];

        /// <summary>Tiles to walk from the heart's doors once there's a heart, else from the base centre.</summary>
        private int WalkTiles(IntVec3 outside) =>
            heartWalk != null ? (heartWalk.Has(outside) ? heartWalk[outside] : weights.maxWalk * 2)
            : walk.Has(outside) ? walk[outside] : weights.maxWalk * 2;

        public static bool IsReusableWall(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.IsWall && e.Faction == Faction.OfPlayer && !e.def.building.isNaturalRock;
        }

        /// <summary>The hard rules of §2 for one cell. Trees and items aren't blocks, just clearing work.</summary>
        private bool IsBlocked(IntVec3 c, out bool tree, out bool item)
        {
            tree = item = false;
            foreach (var t in c.GetThingList(map))
            {
                if (t.def.category == ThingCategory.Plant && t.def.plant.IsTree)
                    tree = true;
                else if (t.def.category == ThingCategory.Item)
                    item = true;
            }
            return !Ground.Open(c, map) || !c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy) || UnderOverheadMountain(c, map)
                   || InFocusRadius(c, foci) || interactionSpots.Contains(c) || Ground.Indoor(c.GetRoom(map));
        }

        private bool WalkThrough(Room room)
        {
            if (!walkThroughCache.TryGetValue(room, out bool v))
                walkThroughCache[room] = v = Layout.WalkThrough(room);
            return v;
        }

        // ---- candidates ----

        /// <summary>A footprint with its door and grid-only score. Cheap: a full RoomPlan is built only for the best ones.</summary>
        public class Candidate
        {
            public CellRect rect;
            public IntVec3 door;
            public Rot4 side;
            public float score;
            public List<IntVec3> broughtIn = new List<IntVec3>(); // a hub's: the ways out on its ring
            public int waysOutSaved;
            public bool joined; // it shares a wall with the base (BASE_GROWTH.md §6.4)
            public bool apart;  // a building of its own: nothing built within apartGap of its walls
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

        /// <summary>For the self-test: why footprints of this size in the scan area fail, as counts.</summary>
        public string Rejections(int width, int height)
        {
            int total = 0, bounds = 0, inside = 0, ring = 0, door = 0, noDoor = 0, ok = 0;
            for (int z = scanArea.minZ; z + height + 1 <= scanArea.maxZ; z++)
                for (int x = scanArea.minX; x + width + 1 <= scanArea.maxX; x++)
                {
                    var rect = new CellRect(x, z, width + 2, height + 2);
                    total++;
                    if (!rect.InBounds(map)) bounds++;
                    else if (blocked.Sum(rect.ContractedBy(1)) > 0) inside++;
                    else if (ringBad.Sum(rect) > 0) ring++;
                    else if (doorTouch.Sum(rect) > 0) door++;
                    else if (!ChooseDoor(rect, hub: false, out _, out _)) noDoor++;
                    else ok++;
                }
            return $"{total} footprints {width}x{height} in {scanArea}: {bounds} off the map, {inside} blocked inside, {ring} blocked ring, {door} beside a door, {noDoor} no door spot, {ok} ok; reached {reach.Reached.Count} cells ({walk.Reached.Count} from the centre)";
        }

        private Candidate TryCandidate(CellRect rect)
        {
            if (!rect.InBounds(map) || blocked.Sum(rect.ContractedBy(1)) > 0 || ringBad.Sum(rect) > 0 || doorTouch.Sum(rect) > 0)
                return null;
            if (!ChooseDoor(rect, hub: false, out IntVec3 door, out Rot4 side))
                return null;
            CellRect around = rect.ExpandedBy(weights.apartGap).ClipInsideMap(map);
            return new Candidate
            {
                rect = rect, door = door, side = side, score = Score(rect, door, side, null),
                joined = reusableSums.Sum(rect) > 0, apart = built.Sum(around) == 0,
            };
        }

        /// <summary>
        /// The door: into a room people may walk through if the walk allows (insideDoor tiles are worth it), else the best
        /// walk from the base, then centred on its wall (vanilla's TryGetBestRectExteriorCell idea). A hub's door opens
        /// outdoors (the rooms behind it get out through it: into another room, two hubs could lead only into each other)
        /// and keeps off the doors it takes in.
        /// </summary>
        private bool ChooseDoor(CellRect rect, bool hub, out IntVec3 door, out Rot4 doorSide)
        {
            door = IntVec3.Invalid;
            doorSide = Rot4.North;
            float bestKey = float.MaxValue;
            IntVec3 centre = rect.CenterCell;
            foreach (var c in rect.EdgeCells)
            {
                if (IsCorner(rect, c) || wayOutDoor[c.z * w + c.x] || (hub && Ground.BesideDoor(c, map)))
                    continue;
                Rot4 side = SideOf(rect, c);
                IntVec3 outside = c + side.FacingCell;
                if (!outside.InBounds(map))
                    continue;
                int o = outside.z * w + outside.x;
                if (!doorOk[o] || (hub && doorInside[o]))
                    continue;
                int i = c.z * w + c.x;
                if (reusable[i] && !realWall[i])
                    continue; // only a real wall can be replaced by a door blueprint
                float offCentre = side.IsHorizontal ? Mathf.Abs(c.z - centre.z) : Mathf.Abs(c.x - centre.x);
                float key = WalkTiles(outside) * 10 + offCentre - (doorInside[o] ? weights.insideDoor * 10 : 0);
                if (key < bestKey)
                {
                    bestKey = key;
                    door = c;
                    doorSide = side;
                }
            }
            return bestKey < float.MaxValue;
        }

        /// <summary>The weighted score from the grids alone. With a plan, also fills its description inputs.</summary>
        private float Score(CellRect rect, IntVec3 door, Rot4 doorSide, RoomPlan plan)
        {
            IntVec3 outside = door + doorSide.FacingCell;
            int walkTiles = WalkTiles(outside);
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
            int treeCount = trees.Sum(rect), itemCount = items.Sum(rect);
            bool inHome = map.areaManager.Home[rect.CenterCell];
            int siteEdge = Mathf.Min(Mathf.Min(rect.minX, rect.minZ), Mathf.Min(w - 1 - rect.maxX, h - 1 - rect.maxZ));
            int shelter = Mathf.Clamp(EdgeDistance(rect.CenterCell) - EdgeDistance(center), -15, 15);
            bool standard = Mathf.Min(rect.Width, rect.Height) - 2 >= StandardSize;

            float goods = 0f;
            foreach (var link in goodsLinks)
                goods += link.walk.Has(outside) ? link.walk[outside] : weights.maxWalk;
            float tempGap = 0f;
            if (anyHeldTemp)
                foreach (var c in rect.EdgeCells)
                {
                    if (IsCorner(rect, c) || c == door || !reusable[c.z * w + c.x])
                        continue;
                    IntVec3 beyond = c + SideOf(rect, c).FacingCell;
                    Room other = beyond.InBounds(map) ? beyond.GetRoom(map) : null;
                    if (!Ground.Indoor(other))
                        continue;
                    float theirs = HeldTemp(other);
                    if (float.IsNaN(theirs) && float.IsNaN(kindTemp))
                        continue;
                    tempGap += Mathf.Abs((float.IsNaN(kindTemp) ? 21f : kindTemp) - (float.IsNaN(theirs) ? 21f : theirs)) / 10f;
                }

            float score = weights.walk * walkTiles
                          + weights.goodsWalk * goods + weights.wallTemp * tempGap
                          + weights.sharedWallCell * reused + weights.sharedSide * sharedSides
                          + weights.alignedSide * aligned
                          + weights.sliverCell * slivers
                          + weights.fertility * fertility.Sum(rect)
                          + weights.shelter * shelter + weights.nearEdge * Mathf.Max(0, 15 - siteEdge)
                          + weights.tree * treeCount + weights.item * itemCount
                          + (inHome ? weights.home : 0f)
                          + (standard ? weights.bigRoom : 0f)
                          + (doorInside[outside.z * w + outside.x] ? weights.insideDoor : 0f);
            if (plan != null)
            {
                plan.walk = walkTiles;
                plan.sharedSides = sharedSides;
                plan.trees = treeCount;
                plan.items = itemCount;
                plan.score = score;
                plan.near = goodsLinks.Select(l => (l.name, tiles: l.walk.Has(outside) ? l.walk[outside] : -1)).Where(l => l.tiles >= 0)
                    .OrderBy(l => l.tiles).Take(2).Select(l => $"the {l.name} ({l.tiles} tiles)").ToList();
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
        /// the kind's items don't fit. A plain room's furniture comes from a template: its interior is empty, so the layout
        /// depends only on the kind, the size and where the door is, and it's worked out once per scan. A hub's depends on
        /// the doors it takes in too, so it's placed each time.
        /// </summary>
        public RoomPlan Plan(Candidate c, RoomKindDef kind)
        {
            if (!kind.Fits(c.Width, c.Height))
                return null;
            var plan = NewPlan(c, kind);
            var furniture = c.broughtIn.Count > 0 ? Furnish(c, kind) : Template(plan, c);
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
                else if (!plan.reusedWalls.Contains(cell) && !plan.broughtIn.Contains(cell))
                    plan.entries.Add(new PlanEntry(ThingDefOf.Wall, cell, Rot4.North));
            if (!WallItems(plan, c))
                return null;
            Score(c.rect, c.door, c.side, plan);
            if (c.broughtIn.Count > 0)
                plan.score = c.score;
            plan.waysOutSaved = c.waysOutSaved;
            plan.joined = c.joined;
            plan.apart = c.apart;
            return plan;
        }

        /// <summary>
        /// The kind's items that go in the wall (a cooler, BASE_GROWTH.md §6.3): each takes a new wall's place, facing out onto
        /// open ground, with its inside cell free and off the door. False if a required one finds no spot.
        /// </summary>
        private bool WallItems(RoomPlan plan, Candidate c)
        {
            foreach (var item in plan.kind.items.Where(i => i.inWall))
            {
                ThingDef def = item.Resolve(map);
                PlanEntry spot = def == null ? null : plan.entries
                    .Where(e => e.def == ThingDefOf.Wall && !IsCorner(c.rect, e.cell) && !e.cell.AdjacentToCardinal(c.door))
                    .Select(e => new PlanEntry(def, e.cell, SideOf(c.rect, e.cell)))
                    .FirstOrDefault(e =>
                    {
                        IntVec3 outside = e.cell + e.rot.FacingCell, inside = e.cell - e.rot.FacingCell;
                        return outside.InBounds(map) && Ground.Open(outside, map) && outside.Standable(map) && Ground.OpenAir(outside.GetRoom(map), openAir)
                               && !plan.entries.Any(f => f.def != ThingDefOf.Wall && f.def != ThingDefOf.Door && f.Rect.Contains(inside))
                               && inside != plan.doorInside;
                    });
                if (spot == null)
                {
                    // An optional one that can be built (a cooler, with power) is wanted where it fits; Fit drops it only
                    // when no footprint around the site has a spot for it.
                    if (!item.optional || (def != null && wallItemsWanted))
                        return false;
                    continue;
                }
                plan.entries.RemoveAll(e => e.cell == spot.cell && e.def == ThingDefOf.Wall);
                plan.entries.Add(spot);
            }
            return true;
        }

        private bool wallItemsWanted = true;

        private RoomPlan NewPlan(Candidate c, RoomKindDef kind)
        {
            var plan = new RoomPlan
            {
                kind = kind, map = map, footprint = c.rect,
                door = c.door, doorOutside = c.door + c.side.FacingCell, doorInside = c.door - c.side.FacingCell,
            };
            plan.broughtIn.AddRange(c.broughtIn);
            foreach (var cell in c.rect.EdgeCells)
                if (reusable[cell.z * w + cell.x] && cell != c.door)
                    plan.reusedWalls.Add(cell);
            return plan;
        }

        /// <summary>The kind's furniture relative to the footprint's corner, or null if it doesn't fit (cached per scan).</summary>
        private List<PlanEntry> Template(RoomPlan plan, Candidate c)
        {
            int offset = c.side.IsHorizontal ? c.door.z - c.rect.minZ : c.door.x - c.rect.minX;
            var key = (plan.kind, plan.kind.items, c.Width, c.Height, c.side.AsInt, offset);
            if (!templates.TryGetValue(key, out var cached))
                templates[key] = cached = Furnish(c, plan.kind);
            return cached;
        }

        /// <summary>The kind's furniture placed in an empty copy of the plan, relative to the footprint's corner; null if it doesn't fit.</summary>
        private List<PlanEntry> Furnish(Candidate c, RoomKindDef kind)
        {
            var scratch = NewPlan(c, kind);
            if (!RoomPlacer.Place(scratch))
                return null;
            IntVec3 origin = new IntVec3(c.rect.minX, 0, c.rect.minZ);
            return scratch.entries.Select(e => e.Moved(-origin)).ToList();
        }

        /// <summary>The best n that don't overlap each other and pass the validator, as plans of this kind.</summary>
        /// <param name="maxTries">Plans validated at most (hubs: the search is wide).</param>
        public List<RoomPlan> TopSites(IEnumerable<Candidate> sorted, int n, RoomKindDef kind, RoomValidator validator, ThingDef material, int maxTries = int.MaxValue,
                                       List<RoomPlan> avoid = null)
        {
            var top = new List<RoomPlan>();
            int tries = 0;
            foreach (var c in sorted)
            {
                if (top.Count >= n || tries >= maxTries)
                    break;
                if (top.Any(t => t.footprint.Overlaps(c.rect)) || avoid?.Any(t => t.footprint.Overlaps(c.rect)) == true)
                    continue;
                var plan = Plan(c, kind);
                if (plan == null)
                    continue;
                tries++;
                if (Passes(plan, validator, material))
                    top.Add(plan);
            }
            return top;
        }

        /// <summary>
        /// The validator's verdict. A door cut off (V7) and walls in open ground (V9) are only visible to the validator; vanilla
        /// refusing a placement (V8) means the grids missed something: a finder bug worth a warning.
        /// </summary>
        private static bool Passes(RoomPlan plan, RoomValidator validator, ThingDef material)
        {
            var failures = validator.Check(plan, material);
            if (failures.Any(f => f.StartsWith("V8")))
                ModLog.Warning($"A plan failed the validator (a site-finder bug): {string.Join("; ", failures)}\n{TextMap.Draw(plan)}");
            return failures.Count == 0;
        }

        // ---- sites and sizes (PHASE4.md §2, §5) ----

        /// <summary>
        /// The top 3 sites as validated plain rooms at the standard 5×5, else 4×4 where 5×5 fits nowhere. One per style
        /// (BASE_GROWTH.md §6.4): A the best site joined to the base, B the best building apart, C the next best of any;
        /// a style with no site leaves its place to the next best.
        /// </summary>
        public List<RoomPlan> Sites(RoomValidator validator, ThingDef material, out List<Candidate> candidates, int n = 3)
        {
            candidates = Candidates(StandardSize, StandardSize);
            var sites = Styled(candidates, n, validator, material);
            if (sites.Count == 0)
            {
                candidates = Candidates(4, 4);
                sites = Styled(candidates, n, validator, material);
            }
            return sites;
        }

        private List<RoomPlan> Styled(List<Candidate> sorted, int n, RoomValidator validator, ThingDef material)
        {
            var sites = new List<RoomPlan>();
            sites.AddRange(TopSites(sorted.Where(c => c.joined), 1, RoomKindDef.Plain, validator, material));
            sites.AddRange(TopSites(sorted.Where(c => c.apart), 1, RoomKindDef.Plain, validator, material, avoid: sites));
            sites.AddRange(TopSites(sorted, n - sites.Count, RoomKindDef.Plain, validator, material, avoid: sites));
            return sites;
        }

        /// <summary>The candidates of one shape whose interior contains the cell, best first.</summary>
        private List<Candidate> Around(IntVec3 cell, int width, int height, Func<CellRect, Candidate> tryRect)
        {
            var result = new List<Candidate>();
            for (int x = cell.x - width; x <= cell.x - 1; x++)
                for (int z = cell.z - height; z <= cell.z - 1; z++)
                {
                    var c = tryRect(new CellRect(x, z, width + 2, height + 2));
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
                if (Around(cell, sw, sh, TryCandidate).Count > 0)
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
            var plan = FitOnce(site, kind, width, height, validator, material, out note);
            if (plan != null || !kind.items.Any(i => i.inWall && i.optional))
                return plan;
            wallItemsWanted = false; // no spot anywhere for the cooler: the room without it
            try
            {
                return FitOnce(site, kind, width, height, validator, material, out note);
            }
            finally
            {
                wallItemsWanted = true;
            }
        }

        private RoomPlan FitOnce(RoomPlan site, RoomKindDef kind, int width, int height, RoomValidator validator, ThingDef material, out string note)
        {
            note = null;
            IntVec3 cell = site.Interior.CenterCell;
            int area = width * height;
            var order = FitShapesNarrow.Where(s => kind.Fits(s.w, s.h))
                .OrderBy(s => Mathf.Min(s.w, s.h) == Mathf.Min(width, height) && Mathf.Max(s.w, s.h) == Mathf.Max(width, height) ? 0 : 1)
                .ThenBy(s => Mathf.Abs(s.w * s.h - area))
                .ThenBy(s => s.w * s.h)
                .ToList();
            foreach (var (sw, sh) in order)
            {
                // A building apart stays apart at its size (BASE_GROWTH.md §6.4), unless no footprint there is.
                var around = Around(cell, sw, sh, TryCandidate);
                var plan = (site.apart ? TopSites(around.Where(x => x.apart), 1, kind, validator, material).FirstOrDefault() : null)
                           ?? TopSites(around, 1, kind, validator, material).FirstOrDefault();
                if (plan == null)
                    continue;
                if (Mathf.Min(sw, sh) != Mathf.Min(width, height) || Mathf.Max(sw, sh) != Mathf.Max(width, height))
                    note = kind.Fits(width, height) ? $"a {width}×{height} {kind.label} doesn't fit there, so it's {plan.SizeLabel}"
                        : $"a {kind.label} needs at least {kind.minSize.x}×{kind.minSize.z}, so it's {plan.SizeLabel}";
                return plan;
            }
            return null;
        }

        // ---- hubs (BASE_LAYOUT.md, CLEANUP.md §5) ----

        /// <summary>
        /// Hubs of a walk-through kind, best first: a room whose wall ring takes in existing ways out, so the rooms behind
        /// them open into it instead of outdoors. Validated like any room, and apart from each other.
        /// </summary>
        public List<RoomPlan> Hubs(RoomKindDef kind, RoomValidator validator, ThingDef material, int n = 1)
        {
            For(kind);
            var shapes = new List<(int, int)>();
            // A furnished room is at least the size code builds it at (a great hall 7×7), not squeezed to save walls; a
            // plain hall takes whatever the gap allows.
            int minShort = kind.layout ? 1 : Mathf.Min(kind.size.x, kind.size.z), minLong = kind.layout ? 1 : Mathf.Max(kind.size.x, kind.size.z);
            for (int a = 1; a <= HubMaxWidth; a++)
                for (int b = a; b < weights.outdoorWalk; b++)
                    if (kind.Fits(a, b) && a >= minShort && b >= minLong)
                    {
                        shapes.Add((a, b));
                        if (a != b)
                            shapes.Add((b, a));
                    }
            var seen = new HashSet<CellRect>();
            var candidates = new List<Candidate>();
            foreach (var front in wayOutFront.Values)
                foreach (var (sw, sh) in shapes)
                    candidates.AddRange(Around(front, sw, sh, r => seen.Add(r) ? TryHub(r) : null));
            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            return TopSites(candidates, n, kind, validator, material, HubTries);
        }

        /// <summary>
        /// A hub footprint: the inside is free ground, and every blocked ring cell is a way out opening into it. Its own door
        /// opens outdoors, so it saves a way out when it takes in two or more.
        /// </summary>
        private Candidate TryHub(CellRect rect)
        {
            if (!rect.InBounds(map))
                return null;
            CellRect inner = rect.ContractedBy(1);
            int doors = wayOutDoors.Sum(rect); // the inside can't hold one: it's free ground
            // Each door's outside cell must be inside (checked per door below: two doors at a corner share one).
            if (doors == 0 || blocked.Sum(inner) > 0 || ringBad.Sum(rect) != doors || wayOutFronts.Sum(inner) == 0 || otherDoorTouch.Sum(rect) > 0)
                return null;
            if (!ChooseDoor(rect, hub: true, out IntVec3 door, out Rot4 side))
                return null;
            int saved = doors - 1;
            if (saved <= 0)
                return null;
            var c = new Candidate { rect = rect, door = door, side = side, waysOutSaved = saved };
            IntVec3 ownInside = door - side.FacingCell;
            int newWalls = 0;
            foreach (var cell in rect.EdgeCells)
            {
                if (wayOutFront.TryGetValue(cell, out IntVec3 front))
                {
                    if (!inner.Contains(front))
                        return null; // it opens away from the hub
                    // People still get out: across the empty hub to its own door, within outdoorWalk (the way-out rule, §9).
                    if (Math.Abs(front.x - ownInside.x) + Math.Abs(front.z - ownInside.z) + 2 > weights.outdoorWalk)
                        return null;
                    c.broughtIn.Add(cell);
                }
                else if (cell != door && !reusable[cell.z * w + cell.x])
                    newWalls++;
            }
            // As the common room scored before it: each way out saved is worth insideDoor, each new wall costs 1.
            c.score = Score(rect, door, side, null) + weights.insideDoor * saved - newWalls;
            return c;
        }

        // ---- materials and words ----

        /// <summary>One site in words, with no coordinates (PHASE4.md §2): where, walls shared, distance, clearing, room to grow, wall cost.</summary>
        public string Describe(RoomPlan plan, char letter, List<ThingDef> materials, (int w, int h) maxFit)
        {
            var parts = new List<string> { plan.apart ? "a building of its own " + Where(plan) : Where(plan) };
            Room into = plan.doorOutside.GetRoom(map);
            if (Ground.Indoor(into))
                parts.Add($"its door opens into the {Layout.Name(into)}");
            if (plan.sharedSides > 0)
                parts.Add(plan.sharedSides == 1 ? "shares 1 wall" : $"shares {plan.sharedSides} walls");
            if (plan.near.Count > 0)
                parts.Add("near " + string.Join(" and ", plan.near));
            parts.Add(heartName != null ? $"{plan.walk} tiles from the {heartName}" : $"{plan.walk} tiles from the centre");
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
                if (!Ground.Indoor(room))
                    continue;
                string label = Ground.NoRole(room) ? "a room" : "the " + room.Role.label;
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
