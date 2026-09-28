using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    /// <summary>
    /// The oracle (PHASE4.md §3): checks a planned room against the live map, independently of the site finder's grids,
    /// and never repairs. Each failure starts with its rule id. In testing, any failure is a site-finder bug.
    /// </summary>
    public class RoomValidator
    {
        /// <summary>Doors may take this much longer to reach with the room in place before it counts as cutting a path.</summary>
        private const int DetourAllowance = 30;

        private readonly Map map;
        private readonly IntVec3 center;
        private readonly int limit;
        private readonly List<IntVec3> reachableDoors = new List<IntVec3>();
        private readonly List<(IntVec3 pos, float radius)> foci;

        public RoomValidator(Map map, IntVec3 center, int maxWalk)
        {
            this.map = map;
            this.center = center;
            foci = SiteFinder.NoBuildFoci(map);
            limit = maxWalk + 20;
            int[] before = SiteFinder.Walk(map, center, limit);
            foreach (var b in map.listerBuildings.allBuildingsColonist)
                if (b is Building_Door && before[b.Position.z * map.Size.x + b.Position.x] >= 0)
                    reachableDoors.Add(b.Position);
        }

        public List<string> Check(RoomPlan plan, ThingDef material)
        {
            var fail = new List<string>();
            CellRect rect = plan.footprint, inner = plan.Interior;

            // V1 door
            var doors = plan.entries.FindAll(e => e.def == ThingDefOf.Door);
            if (doors.Count != 1 || doors[0].cell != plan.door)
                fail.Add($"V1: {doors.Count} doors planned");
            else if (!rect.IsOnEdge(plan.door) || SiteFinder.IsCorner(rect, plan.door))
                fail.Add("V1: door is in a corner or off the ring");
            else
            {
                Rot4 side = SiteFinder.SideOf(rect, plan.door);
                Rot4 along = side.Rotated(RotationDirection.Clockwise);
                if (!plan.IsRingSolid(plan.door + along.FacingCell) || !plan.IsRingSolid(plan.door - along.FacingCell))
                    fail.Add("V1: door lacks wall on both sides");
                if (plan.doorInside != plan.door - side.FacingCell || plan.doorOutside != plan.door + side.FacingCell)
                    fail.Add("V1: door inside/outside cells don't match the door's side");
            }

            // V2 enclosed: 4-neighbour flood fill from inside must stay inside and cover exactly the interior.
            var seen = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            IntVec3 start = inner.CenterCell;
            seen.Add(start);
            queue.Enqueue(start);
            bool escaped = false;
            while (queue.Count > 0 && !escaped)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (plan.IsRingSolid(n) || !seen.Add(n))
                        continue;
                    if (!inner.Contains(n))
                    {
                        escaped = true;
                        break;
                    }
                    queue.Enqueue(n);
                }
            }
            if (escaped)
                fail.Add("V2: the room isn't enclosed");
            else if (seen.Count != inner.Area)
                fail.Add($"V2: interior has {seen.Count} cells, expected {inner.Area}");

            // V3 door outside
            IntVec3 o = plan.doorOutside;
            if (!o.InBounds(map) || !o.Walkable(map))
                fail.Add("V3: the cell outside the door isn't walkable");
            else if (!map.reachability.CanReach(center, o, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                fail.Add("V3: the cell outside the door can't be reached from the base centre");
            else
            {
                Room room = o.GetRoom(map);
                bool outdoors = room != null && room.UsesOutdoorTemperature;
                bool walkThrough = !outdoors && Layout.WalkThrough(room);
                if (!outdoors && !walkThrough)
                    fail.Add($"V3: the door opens into {(room == null ? "nothing" : room.Role.label)}, not outdoors or a room people may walk through");
                else if (walkThrough && o.GetThingList(map).Any(t => t.def.category == ThingCategory.Building))
                    fail.Add("V3: something is built where the door opens");
            }

            // V4 door inside: empty, and every free interior cell reachable from it.
            var furnitureCells = new HashSet<IntVec3>();
            foreach (var e in plan.Furniture)
                foreach (var c in e.Rect)
                    if (!furnitureCells.Add(c))
                        fail.Add($"V4: furniture overlaps at {e.def.label}");
            if (furnitureCells.Contains(plan.doorInside))
                fail.Add("V4: furniture blocks the cell inside the door");
            else
            {
                var reach = new HashSet<IntVec3> { plan.doorInside };
                var q = new Queue<IntVec3>();
                q.Enqueue(plan.doorInside);
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    for (int r = 0; r < 4; r++)
                    {
                        var n = c + new Rot4(r).FacingCell;
                        if (inner.Contains(n) && !furnitureCells.Contains(n) && reach.Add(n))
                            q.Enqueue(n);
                    }
                }
                int free = 0;
                foreach (var c in inner)
                    if (!furnitureCells.Contains(c))
                        free++;
                if (reach.Count != free)
                    fail.Add($"V4: {free - reach.Count} free cells can't be reached from the door");
            }

            // V5 furniture: inside, beds' heads against a wall, interaction cells free, the kind's items there, links, access.
            var beds = new List<PlanEntry>();
            foreach (var e in plan.Furniture)
            {
                foreach (var c in e.Rect)
                    if (!inner.Contains(c))
                    {
                        fail.Add($"V5: {e.def.label} sticks out of the room");
                        break;
                    }
                if (e.def.IsBed)
                {
                    beds.Add(e);
                    if (BedUtility.GetSleepingSlotPos(0, e.cell, e.rot, e.def.size) != e.cell)
                        fail.Add("V5: bed head isn't at its Position");
                    IntVec3 behindHead = e.cell - e.rot.FacingCell;
                    if (!plan.IsRingSolid(behindHead) || behindHead == plan.door)
                        fail.Add("V5: bed head isn't against a wall");
                }
                if (e.def.hasInteractionCell || !e.def.multipleInteractionCellOffsets.NullOrEmpty())
                    foreach (var ic in ThingUtility.InteractionCellsWhenAt(e.def, e.cell, e.rot, map))
                        if (!inner.Contains(ic) || furnitureCells.Contains(ic))
                            fail.Add($"V5: {e.def.label}'s interaction cell is blocked");
                bool access = false;
                foreach (var n in RoomPlacer.Adjacent(e.Rect))
                    access |= inner.Contains(n) && !furnitureCells.Contains(n);
                if (!access)
                    fail.Add($"V5: {e.def.label} has no free cell next to it");
            }
            foreach (var e in plan.Furniture)
                if (!e.def.IsBed && beds.Count > 0 && e.def.GetCompProperties<CompProperties_Facility>() is CompProperties_Facility facility
                    && facility.mustBePlacedAdjacentCardinalToBedHead
                    && !beds.Exists(bed => CompAffectedByFacilities.CanPotentiallyLinkTo_Static(e.def, e.cell, e.rot, bed.def, bed.cell, bed.rot, map)))
                    fail.Add($"V5: {e.def.label} wouldn't link to a bed");
            foreach (var item in plan.kind.items)
            {
                if (item.optional)
                    continue;
                int count = plan.Furniture.Count(e => item.need != null ? Needs.Meets(item.need, e.def, null) : item.defs.Contains(e.def));
                if (count < Math.Min(item.min, item.repeat))
                    fail.Add($"V5: {count} of the kind's {item.need ?? item.defs[0].label} placed, needs {Math.Min(item.min, item.repeat)}");
            }

            // V6 footprint, cell by cell (vanilla checks fog only at a thing's centre; blueprints delete zone cells).
            foreach (var c in rect)
            {
                if (plan.reusedWalls.Contains(c) || (c == plan.door && SiteFinder.IsReusableWall(c, map)))
                    continue; // a door may replace a real player wall; V7 checks it's a Wall
                string why = !c.InBounds(map) ? "out of bounds"
                    : c.InNoBuildEdgeArea(map) ? "in the no-build edge"
                    : c.Fogged(map) ? "fogged"
                    : !c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy) ? "no heavy affordance"
                    : map.zoneManager.ZoneAt(c) != null ? "a zone"
                    : map.planManager.PlanAt(c) != null ? "a Plan"
                    : SiteFinder.UnderOverheadMountain(c, map) ? "overhead mountain"
                    : SiteFinder.InFocusRadius(c, foci) ? "the anima tree's radius"
                    : BlockingThing(c) is Thing t ? t.LabelShort
                    : Indoors(c) ? "inside an existing room"
                    : null;
                if (why != null)
                {
                    fail.Add($"V6: footprint cell on {why}");
                    break;
                }
            }

            // V7 neighbours: reused walls are real player walls; no new cell touches a door; no path gets cut.
            foreach (var c in plan.reusedWalls)
                if (!SiteFinder.IsReusableWall(c, map))
                    fail.Add("V7: a reused ring cell isn't a player wall");
            if (plan.door.GetEdifice(map) is Building doorBase && doorBase.def != ThingDefOf.Wall)
                fail.Add($"V7: door planned over {doorBase.LabelShort}");
            foreach (var c in rect)
            {
                if (plan.reusedWalls.Contains(c))
                    continue;
                bool touches = false;
                for (int r = 0; r < 4 && !touches; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    touches = !rect.Contains(n) && n.InBounds(map) && n.GetEdifice(map) is Building_Door;
                }
                if (touches)
                {
                    fail.Add("V7: the room touches an existing door");
                    break;
                }
            }
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
        /// counts doors that were reachable before but aren't now. Stops as soon as every door is found.
        /// </summary>
        private int DoorsCutOff(RoomPlan plan)
        {
            if (reachableDoors.Count == 0 || !plan.doorOutside.InBounds(map) || !plan.doorOutside.Walkable(map))
                return 0;
            var remaining = new HashSet<IntVec3>(reachableDoors);
            int maxSteps = limit + DetourAllowance + Math.Max(plan.walk, 0);
            var dist = new Dictionary<IntVec3, int> { [plan.doorOutside] = 0 };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(plan.doorOutside);
            remaining.Remove(plan.doorOutside);
            while (queue.Count > 0 && remaining.Count > 0)
            {
                var c = queue.Dequeue();
                int d = dist[c];
                if (d >= maxSteps)
                    continue;
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (!n.InBounds(map) || dist.ContainsKey(n) || plan.footprint.Contains(n) || !n.Walkable(map) || n.Fogged(map))
                        continue;
                    dist[n] = d + 1;
                    remaining.Remove(n);
                    queue.Enqueue(n);
                }
            }
            return remaining.Count;
        }

        /// <summary>Walkable cells around the room reached from beyond it now but not once it's built (its door passes).</summary>
        private int WalledIn(RoomPlan plan)
        {
            CellRect area = plan.footprint.ExpandedBy(10).ClipInsideMap(map);
            var before = Reach(area, null);
            var after = Reach(area, plan);
            int n = 0;
            foreach (var c in before)
                if (!plan.footprint.Contains(c) && !after.Contains(c))
                    n++;
            return n;
        }

        private HashSet<IntVec3> Reach(CellRect area, RoomPlan plan)
        {
            bool Open(IntVec3 c) => c.Walkable(map) && (plan == null || c == plan.door || !plan.IsRingSolid(c));
            var seen = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            foreach (var c in area.EdgeCells)
                if (Open(c) && seen.Add(c))
                    queue.Enqueue(c);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int r = 0; r < 4; r++)
                {
                    var n = c + new Rot4(r).FacingCell;
                    if (area.Contains(n) && !seen.Contains(n) && Open(n) && seen.Add(n))
                        queue.Enqueue(n);
                }
            }
            return seen;
        }

        private Thing BlockingThing(IntVec3 c)
        {
            foreach (var t in c.GetThingList(map))
                if (t is Blueprint || t is Frame || t.def.category == ThingCategory.Building)
                    return t;
            return null;
        }

        private bool Indoors(IntVec3 c)
        {
            Room room = c.GetRoom(map);
            return room != null && !room.IsDoorway && !room.UsesOutdoorTemperature;
        }
    }
}
