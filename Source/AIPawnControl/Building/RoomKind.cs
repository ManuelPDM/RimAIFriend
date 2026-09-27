using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>One item of a room kind and the rules for where it goes (PHASE4.md §1).</summary>
    public class RoomItem
    {
        /// <summary>Alternatives: the first one that's buildable is used (a fueled stove). Ignored when there's a need.</summary>
        public List<ThingDef> defs = new List<ThingDef>();
        /// <summary>What it does instead of named defs (FURNISHING.md §4): bed, seat, shelf, accessory (of the nextTo item).</summary>
        public string need;
        /// <summary>Against a wall (a bed: its head, Position, against the wall and rotated away). The default placement.</summary>
        public bool backToWall;
        /// <summary>As close to the middle as the other rules allow (a dining table).</summary>
        public bool centre;
        /// <summary>Index of an earlier item this one sits cardinally next to (facing it), or -1.</summary>
        public int nextTo = -1;
        /// <summary>Placed again while it fits, up to this many.</summary>
        public int repeat = 1;
        /// <summary>How many must fit for the room to count (ignored when optional).</summary>
        public int min = 1;
        /// <summary>Skipped when it isn't buildable (research, difficulty) or doesn't fit, instead of failing the room.</summary>
        public bool optional;
        /// <summary>Prefer a def the colony doesn't have yet (a second workshop gets a different bench).</summary>
        public bool preferNew;
        /// <summary>Beds: set to medical once built (a hospital without hospital beds).</summary>
        public bool medical;
        /// <summary>Free cardinal neighbours to keep around it (a chess table's players).</summary>
        public int clearAround;

        public ThingDef Resolve(Map map, ThingDef anchor = null)
        {
            if (need != null)
                return Needs.Best(need, map, anchor, repeat);
            ThingDef first = null;
            foreach (var def in defs)
            {
                if (def == null || !RoomKindDef.Buildable(def))
                    continue;
                if (!preferNew || map.listerBuildings.ColonistsHaveBuilding(def) == false)
                    return def;
                first = first ?? def;
            }
            return first;
        }
    }

    /// <summary>
    /// A room kind (PHASE4.md §1): the items that make vanilla give a room its role. Data in Defs/RoomKinds.xml, so a new
    /// kind needs no code. No role means the plain room, which vanilla names by whatever ends up in it.
    /// </summary>
    public class RoomKindDef : Def
    {
        public RoomRoleDef role;
        /// <summary>Hers (the bedroom): the bed is claimed for her and it's "my bedroom".</summary>
        public bool owned;
        /// <summary>The smallest interior: x = the short side, z = the long side.</summary>
        public IntVec2 minSize = new IntVec2(4, 4);
        /// <summary>The interior code builds it at (STREAMLINE.md §7); a barracks is sized to the beds missing instead.</summary>
        public IntVec2 size = new IntVec2(5, 5);
        public List<RoomItem> items = new List<RoomItem>();

        public static RoomKindDef Bedroom => DefDatabase<RoomKindDef>.GetNamed("AIPC_Bedroom");
        public static RoomKindDef Plain => DefDatabase<RoomKindDef>.GetNamed("AIPC_PlainRoom");

        public static bool Buildable(BuildableDef def) => BuildCopyCommandUtility.FindAllowedDesignator(def) != null;

        /// <summary>Walls and doors are buildable, and every required item has a buildable def.</summary>
        public bool BuildableNow(Map map) =>
            Buildable(ThingDefOf.Wall) && Buildable(ThingDefOf.Door) && items.All(i => i.optional || i.Resolve(map) != null);

        public bool Fits(int width, int height) =>
            Mathf.Min(width, height) >= minSize.x && Mathf.Max(width, height) >= minSize.z;
    }

    /// <summary>
    /// Rule-based furniture placer: each item takes the best cell its rules allow, one at a time. Interaction cells,
    /// watch cells and the cell inside the door stay clear, every item keeps a free neighbour, and every free cell stays
    /// reachable from the door. Returns false if a required item doesn't fit. Pure geometry: the interior is empty.
    /// </summary>
    public static class RoomPlacer
    {
        private class State
        {
            public RoomPlan plan;
            public CellRect inner;
            public readonly HashSet<IntVec3> taken = new HashSet<IntVec3>();    // furniture
            public readonly HashSet<IntVec3> reserved = new HashSet<IntVec3>(); // walkable, but no furniture
            public readonly List<PlanEntry> placed = new List<PlanEntry>();
            // A room that already has furniture (PlaceOne): one more item mustn't make it worse. Null for a new room.
            public HashSet<IntVec3> reachableBefore;              // free cells the door reaches now
            public HashSet<PlanEntry> boxedInBefore;             // items with no free neighbour already
        }

        public static bool Place(RoomPlan plan)
        {
            var s = new State { plan = plan, inner = plan.Interior };
            s.reserved.Add(plan.doorInside);
            var firstOf = new List<PlanEntry>();

            var kindItems = plan.kind.items;
            foreach (var item in kindItems)
            {
                ThingDef anchorDef = item.nextTo >= 0 && item.nextTo < kindItems.Count ? kindItems[item.nextTo].Resolve(plan.map) : null;
                ThingDef def = item.Resolve(plan.map, anchorDef);
                PlanEntry first = null;
                int count = 0;
                if (def != null)
                    while (count < item.repeat)
                    {
                        PlanEntry anchor = item.nextTo >= 0 && item.nextTo < firstOf.Count ? firstOf[item.nextTo] : null;
                        PlanEntry entry = item.nextTo >= 0 ? (anchor != null ? NextTo(def, anchor, s, item) : null)
                            : item.centre ? Centre(def, s, item)
                            : AgainstWall(def, s, item);
                        if (entry == null)
                            break;
                        Commit(entry, s, item);
                        first = first ?? entry;
                        count++;
                    }
                if (!item.optional && count < Mathf.Min(item.min, item.repeat))
                    return false;
                firstOf.Add(first);
            }
            plan.entries.AddRange(s.placed);
            return true;
        }

        /// <summary>
        /// One more item in a room that already has furniture (furnishing, §9): what's there is taken, its work spots stay
        /// clear, and it keeps a free neighbour. Next to the anchor (a seat by a table) if given, else against a wall.
        /// </summary>
        public static PlanEntry PlaceOne(RoomPlan plan, ThingDef def, List<PlanEntry> existing, PlanEntry nextTo)
        {
            var s = new State { plan = plan, inner = plan.Interior };
            s.reserved.Add(plan.doorInside);
            var none = new RoomItem();
            foreach (var e in existing)
                foreach (var c in e.Rect)
                    s.taken.Add(c);
            foreach (var e in existing)
            {
                var clear = KeepClear(e, s, none);
                if (clear != null)
                    foreach (var c in clear)
                        s.reserved.Add(c);
                s.placed.Add(e);
            }
            // Rooms aren't always laid out by these rules (the player's, older ones, a table ringed with chairs), so
            // what's already true isn't held against the new item: it only mustn't make things worse.
            s.reachableBefore = Reachable(s.inner, plan.doorInside, s.taken, CellRect.Empty);
            s.boxedInBefore = new HashSet<PlanEntry>(existing.Where(e => !HasFreeNeighbour(e.Rect, s, CellRect.Empty)));
            var item = new RoomItem { defs = { def } };
            return (nextTo != null ? NextTo(def, nextTo, s, item) : null) ?? AgainstWall(def, s, item);
        }

        private static void Commit(PlanEntry entry, State s, RoomItem item)
        {
            foreach (var c in entry.Rect)
                s.taken.Add(c);
            foreach (var c in KeepClear(entry, s, item))
                s.reserved.Add(c);
            if (item.medical)
                entry.medical = true;
            s.placed.Add(entry);
        }

        private static PlanEntry AgainstWall(ThingDef def, State s, RoomItem item)
        {
            RoomPlan plan = s.plan;
            PlanEntry best = null;
            float bestScore = float.MinValue;
            foreach (var cell in s.inner)
                foreach (var rot in Rotations(def))
                {
                    var entry = new PlanEntry(def, cell, rot);
                    int contacts = WallContacts(entry.Rect, s);
                    if (def.IsBed)
                    {
                        IntVec3 behindHead = cell - rot.FacingCell;
                        if (s.inner.Contains(behindHead) || behindHead == plan.door)
                            continue;
                    }
                    else if (contacts == 0)
                        continue;
                    if (!Fits(entry, s, item, awayFromDoor: true))
                        continue;
                    float score;
                    if (def.IsBed)
                    {
                        // Far from the door first; on a tie, the wall opposite the door, then off the door's line.
                        score = cell.DistanceToSquared(plan.doorInside);
                        if (rot.Opposite.FacingCell == plan.doorInside - plan.door)
                            score += 0.5f;
                        if (cell.x != plan.doorInside.x && cell.z != plan.doorInside.z)
                            score += 0.25f;
                    }
                    else
                    {
                        // Far from the door, the long side against the wall.
                        Vector3 centre = entry.Rect.CenterVector3;
                        score = (centre - plan.doorInside.ToVector3Shifted()).MagnitudeHorizontalSquared() + contacts * 0.5f;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = entry;
                    }
                }
            return best;
        }

        private static PlanEntry Centre(ThingDef def, State s, RoomItem item)
        {
            PlanEntry best = null;
            float bestDist = float.MaxValue;
            Vector3 middle = s.inner.CenterVector3;
            foreach (var cell in s.inner)
                foreach (var rot in Rotations(def))
                {
                    var entry = new PlanEntry(def, cell, rot);
                    float d = (entry.Rect.CenterVector3 - middle).MagnitudeHorizontalSquared();
                    if (d < bestDist && Fits(entry, s, item, awayFromDoor: false))
                    {
                        bestDist = d;
                        best = entry;
                    }
                }
            return best;
        }

        private static PlanEntry NextTo(ThingDef def, PlanEntry anchor, State s, RoomItem item)
        {
            bool facility = def.GetCompProperties<CompProperties_Facility>() != null;
            PlanEntry fallback = null;
            foreach (var cell in Adjacent(anchor.Rect))
            {
                Rot4 rot = facility ? anchor.rot : def.rotatable ? Facing(cell, anchor.Rect) : Rot4.North;
                var entry = new PlanEntry(def, cell, rot);
                if (!Fits(entry, s, item, awayFromDoor: false))
                    continue;
                if (facility && !CompAffectedByFacilities.CanPotentiallyLinkTo_Static(def, cell, rot, anchor.def, anchor.cell, anchor.rot, s.plan.map))
                    continue;
                if (WallContacts(entry.Rect, s) > 0)
                    return entry; // out of the way
                fallback = fallback ?? entry;
            }
            return fallback;
        }

        /// <summary>Cells cardinally next to the rect, outside it.</summary>
        public static IEnumerable<IntVec3> Adjacent(CellRect r)
        {
            for (int x = r.minX; x <= r.maxX; x++)
            {
                yield return new IntVec3(x, 0, r.minZ - 1);
                yield return new IntVec3(x, 0, r.maxZ + 1);
            }
            for (int z = r.minZ; z <= r.maxZ; z++)
            {
                yield return new IntVec3(r.minX - 1, 0, z);
                yield return new IntVec3(r.maxX + 1, 0, z);
            }
        }

        /// <summary>The rotation whose facing cell points from this cell into the anchor.</summary>
        private static Rot4 Facing(IntVec3 cell, CellRect anchor)
        {
            for (int r = 0; r < 4; r++)
                if (anchor.Contains(cell + new Rot4(r).FacingCell))
                    return new Rot4(r);
            return Rot4.North;
        }

        private static IEnumerable<Rot4> Rotations(ThingDef def)
        {
            if (!def.rotatable)
            {
                yield return Rot4.North;
                yield break;
            }
            for (int r = 0; r < 4; r++)
                yield return new Rot4(r);
        }

        /// <summary>Cells of the rect with a ring cell (not the door) cardinally next to them.</summary>
        private static int WallContacts(CellRect rect, State s)
        {
            int n = 0;
            foreach (var c in rect)
                for (int r = 0; r < 4; r++)
                {
                    IntVec3 w = c + new Rot4(r).FacingCell;
                    if (!s.inner.Contains(w) && w != s.plan.door)
                    {
                        n++;
                        break;
                    }
                }
            return n;
        }

        private static bool Fits(PlanEntry entry, State s, RoomItem item, bool awayFromDoor)
        {
            var rect = entry.Rect;
            foreach (var c in rect)
            {
                if (!s.inner.Contains(c) || s.taken.Contains(c) || s.reserved.Contains(c))
                    return false;
                if (awayFromDoor && c.AdjacentToCardinal(s.plan.doorInside))
                    return false;
            }
            var clear = KeepClear(entry, s, item);
            if (clear == null)
                return false;
            foreach (var c in clear)
                if (!s.inner.Contains(c) || s.taken.Contains(c) || rect.Contains(c))
                    return false;
            // Every placed item (this one too) keeps a free neighbour to be used and built from.
            if (!HasFreeNeighbour(rect, s, rect))
                return false;
            foreach (var p in s.placed)
                if (s.boxedInBefore?.Contains(p) != true && !HasFreeNeighbour(p.Rect, s, rect))
                    return false;
            if (s.reachableBefore == null)
                return AllFreeReachable(s.inner, s.plan.doorInside, s.taken, rect);
            var reachable = Reachable(s.inner, s.plan.doorInside, s.taken, rect);
            return s.reachableBefore.All(c => rect.Contains(c) || reachable.Contains(c));
        }

        private static bool HasFreeNeighbour(CellRect of, State s, CellRect extra)
        {
            foreach (var c in Adjacent(of))
                if (s.inner.Contains(c) && !s.taken.Contains(c) && !extra.Contains(c))
                    return true;
            return false;
        }

        /// <summary>Cells that must stay free for the item to work: interaction cells, watch cells, cells around it. Null = can't.</summary>
        private static List<IntVec3> KeepClear(PlanEntry entry, State s, RoomItem item)
        {
            var cells = new List<IntVec3>();
            ThingDef def = entry.def;
            if (def.hasInteractionCell || !def.multipleInteractionCellOffsets.NullOrEmpty())
                cells.AddRange(ThingUtility.InteractionCellsWhenAt(def, entry.cell, entry.rot, s.plan.map));
            if (IsWatchBuilding(def))
            {
                var watch = WatchCells(def, entry.cell, entry.rot).Where(c => s.inner.Contains(c) && !s.taken.Contains(c) && !entry.Rect.Contains(c)).ToList();
                if (watch.Count == 0)
                    return null;
                cells.AddRange(watch);
            }
            if (item.clearAround > 0)
            {
                var around = Adjacent(entry.Rect).Where(c => s.inner.Contains(c) && !s.taken.Contains(c)).ToList();
                if (around.Count < item.clearAround)
                    return null;
                cells.AddRange(around.Take(item.clearAround));
            }
            return cells;
        }

        public static bool IsWatchBuilding(ThingDef def) => def.building != null && def.PlaceWorkers != null && def.PlaceWorkers.Any(w => w is PlaceWorker_WatchArea);

        /// <summary>Vanilla's watch rect (WatchBuildingUtility.GetWatchCellRect) for each direction the building can be watched from.</summary>
        public static IEnumerable<IntVec3> WatchCells(ThingDef def, IntVec3 center, Rot4 rot)
        {
            var b = def.building;
            var dirs = def.rotatable ? new[] { rot.AsInt } : new[] { 0, 1, 2, 3 };
            foreach (int dir in dirs)
            {
                var r = new Rot4(dir);
                IntVec3 step = r.FacingCell;
                int half = b.watchBuildingStandRectWidth / 2;
                for (int d = b.watchBuildingStandDistanceRange.min; d <= b.watchBuildingStandDistanceRange.max; d++)
                    for (int w = -half; w <= half; w++)
                    {
                        if (b.watchBuildingStandRectWidth % 2 == 0 && w == (r == Rot4.West || r == Rot4.North ? -half : half))
                            continue;
                        IntVec3 side = r.IsHorizontal ? new IntVec3(0, 0, w) : new IntVec3(w, 0, 0);
                        yield return center + step * d + side;
                    }
            }
        }

        /// <summary>Every free interior cell can be reached from the cell inside the door (4 neighbours).</summary>
        public static bool AllFreeReachable(CellRect inner, IntVec3 start, HashSet<IntVec3> taken, CellRect extra)
        {
            int free = 0;
            foreach (var c in inner)
                if ((!taken.Contains(c) || c == start) && !extra.Contains(c))
                    free++;
            return Reachable(inner, start, taken, extra).Count == free;
        }

        /// <summary>The free interior cells reached from the cell inside the door (4 neighbours).</summary>
        private static HashSet<IntVec3> Reachable(CellRect inner, IntVec3 start, HashSet<IntVec3> taken, CellRect extra)
        {
            bool Blocked(IntVec3 c) => (taken.Contains(c) && c != start) || extra.Contains(c);
            var seen = new HashSet<IntVec3> { start };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (inner.Contains(n) && !Blocked(n) && seen.Add(n))
                        queue.Enqueue(n);
                }
            }
            return seen;
        }
    }
}
