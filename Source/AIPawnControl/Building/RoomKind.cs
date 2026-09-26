using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>One item of a room kind and the rules for where it goes (PHASE4.md §1).</summary>
    public class RoomItem
    {
        public ThingDef def;
        /// <summary>Against a wall, facing away from it (for a bed: its head, Position, against the wall).</summary>
        public bool backToWall;
        /// <summary>Index of an earlier item this one must sit cardinally next to (its Position: a bed's head), or -1.</summary>
        public int nextTo = -1;
        /// <summary>Skipped when it isn't buildable yet (research) or doesn't fit, instead of failing the room.</summary>
        public bool optional;
    }

    /// <summary>A room template: a list of items the placer puts in one at a time. Later kinds are just other lists.</summary>
    public class RoomKind
    {
        public string label;
        public List<RoomItem> items = new List<RoomItem>();

        private static RoomKind bedroom;

        public static RoomKind Bedroom => bedroom ?? (bedroom = new RoomKind
        {
            label = "bedroom",
            items =
            {
                new RoomItem { def = ThingDefOf.Bed, backToWall = true },
                new RoomItem { def = DefDatabase<ThingDef>.GetNamed("EndTable"), nextTo = 0, optional = true },
            },
        });

        public static bool Buildable(ThingDef def) => BuildCopyCommandUtility.FindAllowedDesignator(def) != null;
    }

    /// <summary>
    /// Rule-based furniture placer: each item takes the best cell its rules allow, and the cell inside the door and
    /// every free cell's path from it stay clear. Returns false if a required item doesn't fit.
    /// </summary>
    public static class RoomPlacer
    {
        public static bool Place(RoomPlan plan)
        {
            CellRect inner = plan.Interior;
            var taken = new HashSet<IntVec3> { plan.doorInside };
            var placed = new List<PlanEntry>();

            foreach (var item in plan.kind.items)
            {
                PlanEntry entry = null;
                if (RoomKind.Buildable(item.def))
                    entry = item.backToWall ? BestAgainstWall(item.def, plan, inner, taken)
                        : item.nextTo >= 0 && item.nextTo < placed.Count && placed[item.nextTo] != null ? NextTo(item.def, placed[item.nextTo], plan, inner, taken)
                        : null;
                if (entry == null && !item.optional)
                    return false;
                placed.Add(entry);
                if (entry == null)
                    continue;
                foreach (var c in entry.Rect)
                    taken.Add(c);
                plan.entries.Add(entry);
            }
            return true;
        }

        private static PlanEntry BestAgainstWall(ThingDef def, RoomPlan plan, CellRect inner, HashSet<IntVec3> taken)
        {
            PlanEntry best = null;
            float bestScore = float.MinValue;
            foreach (var cell in inner)
                for (int r = 0; r < 4; r++)
                {
                    var toWall = new Rot4(r);
                    IntVec3 wall = cell + toWall.FacingCell;
                    if (inner.Contains(wall) || wall == plan.door)
                        continue;
                    var entry = new PlanEntry(def, cell, toWall.Opposite);
                    if (!Fits(entry, plan, inner, taken, awayFromDoor: true))
                        continue;
                    // Far from the door first; on a tie, the wall opposite the door, then off the door's line.
                    float score = cell.DistanceToSquared(plan.doorInside);
                    if (toWall.FacingCell == plan.doorInside - plan.door)
                        score += 0.5f;
                    if (cell.x != plan.doorInside.x && cell.z != plan.doorInside.z)
                        score += 0.25f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = entry;
                    }
                }
            return best;
        }

        private static PlanEntry NextTo(ThingDef def, PlanEntry anchor, RoomPlan plan, CellRect inner, HashSet<IntVec3> taken)
        {
            PlanEntry fallback = null;
            for (int r = 0; r < 4; r++)
            {
                IntVec3 cell = anchor.cell + new Rot4(r).FacingCell;
                var entry = new PlanEntry(def, cell, anchor.rot);
                if (!Fits(entry, plan, inner, taken, awayFromDoor: false))
                    continue;
                // Prefer a cell against a wall, out of the way.
                bool againstWall = !inner.ContractedBy(1).Contains(cell);
                if (againstWall)
                    return entry;
                fallback = fallback ?? entry;
            }
            return fallback;
        }

        private static bool Fits(PlanEntry entry, RoomPlan plan, CellRect inner, HashSet<IntVec3> taken, bool awayFromDoor)
        {
            var rect = entry.Rect;
            foreach (var c in rect)
            {
                if (!inner.Contains(c) || taken.Contains(c))
                    return false;
                if (awayFromDoor && c.AdjacentToCardinal(plan.doorInside))
                    return false;
            }
            if (entry.def.hasInteractionCell)
            {
                IntVec3 ic = ThingUtility.InteractionCellWhenAt(entry.def, entry.cell, entry.rot, plan.map);
                if (!inner.Contains(ic) || taken.Contains(ic) || rect.Contains(ic))
                    return false;
            }
            return AllFreeReachable(inner, plan.doorInside, taken, rect);
        }

        /// <summary>Every free interior cell can be reached from the cell inside the door (4 neighbours).</summary>
        public static bool AllFreeReachable(CellRect inner, IntVec3 start, HashSet<IntVec3> taken, CellRect extra)
        {
            bool Blocked(IntVec3 c) => (taken.Contains(c) && c != start) || extra.Contains(c);
            int free = 0;
            foreach (var c in inner)
                if (!Blocked(c))
                    free++;
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
            return seen.Count == free;
        }
    }
}
