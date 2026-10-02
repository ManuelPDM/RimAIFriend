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
        /// <summary>What it does instead of named defs (FURNISHING.md §4): bed, seat, shelf, accessory (of the nextTo item), joy (each copy a different joy kind).</summary>
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
        /// <summary>Beds: set for prisoners once built (a prison cell).</summary>
        public bool prisoner;
        /// <summary>Free cardinal neighbours to keep around it (a chess table's players).</summary>
        public int clearAround;
        /// <summary>Goes in the room's wall, facing out (a cooler: its hot side outdoors). Placed by the site finder, not the placer.</summary>
        public bool inWall;
        /// <summary>One for every sleeper, and at least repeat (a dining hall's seats; BASE_GROWTH.md §6.1).</summary>
        public bool forEveryone;
        /// <summary>With nextTo: this many per copy of the anchor, and the anchor is repeated until all of them fit (4 seats per table).</summary>
        public int perAnchor;

        /// <summary>How many the room places: repeat, or one per sleeper (at least repeat).</summary>
        public int Count(Map map) => forEveryone ? Mathf.Max(repeat, Ladder.Sleepers(map).Count) : repeat;

        /// <summary>How many must fit: all of them for forEveryone, else min.</summary>
        public int MinCount(Map map) => forEveryone ? Count(map) : Mathf.Min(min, repeat);

        /// <param name="placed">What the room already holds (joy: a different joy kind from these).</param>
        public ThingDef Resolve(Map map, ThingDef anchor = null, IEnumerable<ThingDef> placed = null)
        {
            if (need != null)
                return Needs.Best(need, map, anchor, Count(map), placed);
            ThingDef first = null;
            foreach (var def in defs)
            {
                if (def == null || !RoomKindDef.Buildable(def, map) || !Needs.CanRun(def, map))
                    continue;
                // A hospital bed only when its steel and components are in storage for every bed; else a plain bed, set medical.
                if (medical && !(Needs.OtherCostsInStorage(def, map, Count(map)) && Needs.StuffCanBeHad(def, map, Count(map))))
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
        /// <summary>Made by the base-layout code (a hall, a doorway, a closed doorway), never offered as a room of its own.</summary>
        public bool layout;
        /// <summary>A room the game asks for (AskedFor: throne, ideoBuilding, nursery, deathrest, prison): its items come from there, and it's only offered when asked.</summary>
        public string askedFor;
        /// <summary>Offered under "Other rooms" only when the colony has them: "animals", "children" (BASE_GROWTH.md §6.6 group 3).</summary>
        public string offeredWhen;

        /// <summary>Whatever offeredWhen names is on the map, or there's no condition.</summary>
        public bool Wanted(Map map)
        {
            switch (offeredWhen)
            {
                case null: return true;
                case "animals": return map.mapPawns.SpawnedColonyAnimals.Any();
                case "children": return map.mapPawns.FreeColonistsSpawned.Any(p => p.DevelopmentalStage.Child());
                default: return false;
            }
        }
        /// <summary>
        /// What its stockpile and shelves take once it's done (BASE_GROWTH.md §6.3): these categories, or Root for vanilla's
        /// default "everything" stockpile. Null: no stockpile.
        /// </summary>
        public List<ThingCategoryDef> stores;
        /// <summary>Its stockpile's priority (a food store's is above the storeroom's, so food moves over).</summary>
        public StoragePriority storePriority = StoragePriority.Normal;
        /// <summary>The temperature its coolers or heaters are set to once it's done (a freezer: -10). NaN: vanilla's default.</summary>
        public float holdTemperature = float.NaN;

        /// <summary>The filter its storage gets.</summary>
        public ThingFilter StoreFilter()
        {
            var settings = new StorageSettings();
            if (stores.Contains(ThingCategoryDefOf.Root))
            {
                settings.SetFromPreset(StorageSettingsPreset.DefaultStockpile);
                return settings.filter;
            }
            settings.filter.SetDisallowAll();
            foreach (var category in stores)
                settings.filter.SetAllow(category, true);
            return settings.filter;
        }

        public static RoomKindDef Bedroom => DefDatabase<RoomKindDef>.GetNamed("AIPC_Bedroom");
        public static RoomKindDef Plain => DefDatabase<RoomKindDef>.GetNamed("AIPC_PlainRoom");
        public static RoomKindDef Hall => DefDatabase<RoomKindDef>.GetNamed("AIPC_Hall");
        public static RoomKindDef GreatHall => DefDatabase<RoomKindDef>.GetNamed("AIPC_GreatHall");

        /// <summary>Its designator is allowed (research, difficulty) and someone on the map has the skills to build it (vanilla's check in Designator_Build).</summary>
        public static bool Buildable(BuildableDef def, Map map) =>
            BuildCopyCommandUtility.FindAllowedDesignator(def) != null
            && (map.mapPawns.FreeColonistsSpawned.Any(p => p.skills != null
                    && p.skills.GetSkill(SkillDefOf.Construction).Level >= def.constructionSkillPrerequisite
                    && p.skills.GetSkill(SkillDefOf.Artistic).Level >= def.artisticSkillPrerequisite)
                || MechanitorUtility.AnyPlayerMechCanDoWork(WorkTypeDefOf.Construction, def.constructionSkillPrerequisite, out _));

        /// <summary>Walls and doors are buildable, and every required item has a buildable def.</summary>
        public bool BuildableNow(Map map) =>
            Buildable(ThingDefOf.Wall, map) && Buildable(ThingDefOf.Door, map) && items.All(i => i.optional || i.Resolve(map) != null);

        public bool Fits(int width, int height) =>
            Mathf.Min(width, height) >= minSize.x && Mathf.Max(width, height) >= minSize.z;
    }

    /// <summary>
    /// Rule-based furniture placer: each item takes the best cell its rules allow, one at a time. Interaction cells,
    /// watch cells, the cell inside each door and the walks between the doors stay clear, every item keeps a free
    /// neighbour, and every free cell stays reachable from the door. Pure geometry on the plan's interior.
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
            public readonly List<IntVec3> doorsInside = new List<IntVec3>(); // the cell inside every door: nothing goes next to one
            public readonly List<PlanEntry> stalls = new List<PlanEntry>();  // stall walls and stall doors (HYGIENE.md §5)
            // A room that already has furniture (PlaceOne): one more item mustn't make it worse. Null for a new room.
            public HashSet<IntVec3> reachableBefore;              // free cells the door reaches now
            public HashSet<PlanEntry> boxedInBefore;             // items with no free neighbour already
        }

        /// <summary>Why the last Place failed ("throne: 0 of 1 placed"), for the dev log.</summary>
        public static string LastFailure;

        /// <summary>The kind's items in an empty room. False if a required item doesn't fit.</summary>
        public static bool Place(RoomPlan plan)
        {
            var s = NewState(plan, new List<PlanEntry>());
            Map map = plan.map;
            var kindItems = plan.kind.items;
            var firstOf = new PlanEntry[kindItems.Count];
            var done = new bool[kindItems.Count];
            for (int index = 0; index < kindItems.Count; index++)
            {
                var item = kindItems[index];
                if (done[index] || item.inWall)
                    continue;
                ThingDef anchorDef = item.nextTo >= 0 && item.nextTo < kindItems.Count ? kindItems[item.nextTo].Resolve(map) : null;
                ThingDef def = item.Resolve(map, anchorDef);
                // Items that go perAnchor next to this one: each copy of it gets its share right away (a table, then its seats),
                // and it's repeated until they're all placed.
                var deps = Enumerable.Range(index + 1, kindItems.Count - index - 1).Where(j => kindItems[j].nextTo == index && kindItems[j].perAnchor > 0).ToList();
                var depDefs = deps.ToDictionary(j => j, j => def != null ? kindItems[j].Resolve(map, def) : null);
                var depPlaced = deps.ToDictionary(j => j, j => 0);
                int target = deps.Count > 0 ? deps.Max(j => (kindItems[j].Count(map) + kindItems[j].perAnchor - 1) / kindItems[j].perAnchor) : item.Count(map);
                int count = 0;
                // Shelves tied for best (a crate, tall crate and pallet) are mixed: each copy is one of them at random.
                var tied = item.need == Needs.Shelf && def != null ? Needs.Tied(Needs.Shelf, map, item.Count(map)) : null;
                if (def != null)
                    while (count < target)
                    {
                        if (item.need == Needs.Joy && (def = item.Resolve(map, anchorDef, s.placed.Select(p => p.def))) == null)
                            break; // no game of another joy kind
                        if (tied != null && tied.Count > 0)
                            def = tied.RandomElement();
                        // A game played sitting (chess, poker) gets its seats instead of the cells kept clear around it.
                        bool seated = item.need == Needs.Joy && Needs.PlayedSitting(def);
                        var rules = seated ? new RoomItem { backToWall = item.backToWall } : item;
                        PlanEntry anchor = item.nextTo >= 0 && item.nextTo < firstOf.Length ? firstOf[item.nextTo] : null;
                        List<PlanEntry> stall = null;
                        PlanEntry entry = Hygiene.NeedsPrivacy(def) ? Stall(def, s, out stall)
                            : item.nextTo >= 0 ? (anchor != null ? NextTo(def, anchor, s, rules) : null)
                            : item.centre ? Centre(def, s, rules)
                            : AgainstWall(def, s, rules);
                        if (entry == null)
                            break;
                        if (seated && !WithSeats(entry, s, map))
                            break;
                        if (stall != null)
                            CommitStall(entry, stall, s);
                        else if (!seated)
                            Commit(entry, s, rules);
                        firstOf[index] = firstOf[index] ?? entry;
                        count++;
                        foreach (int j in deps)
                        {
                            var dep = kindItems[j];
                            for (int k = 0; k < dep.perAnchor && depPlaced[j] < dep.Count(map) && depDefs[j] != null; k++)
                            {
                                PlanEntry seat = NextTo(depDefs[j], entry, s, dep);
                                if (seat == null)
                                    break;
                                Commit(seat, s, dep);
                                firstOf[j] = firstOf[j] ?? seat;
                                depPlaced[j]++;
                            }
                        }
                    }
                if (!item.optional && count < Mathf.Min(item.MinCount(map), target))
                {
                    LastFailure = $"{def?.defName ?? "no buildable def"}: {count} of {Mathf.Min(item.MinCount(map), target)} placed in {plan.Width}x{plan.Height}";
                    return false;
                }
                foreach (int j in deps)
                {
                    if (!kindItems[j].optional && depPlaced[j] < kindItems[j].MinCount(map))
                    {
                        LastFailure = $"{depDefs[j]?.defName ?? "no buildable def"}: {depPlaced[j]} of {kindItems[j].MinCount(map)} placed in {plan.Width}x{plan.Height}";
                        return false;
                    }
                    done[j] = true;
                }
            }
            plan.entries.AddRange(s.placed);
            plan.entries.AddRange(s.stalls);
            return true;
        }

        /// <summary>
        /// One more item in a room that already has furniture (furnishing, §9): what's there is taken, its work spots stay
        /// clear, and it keeps a free neighbour. Next to the anchor (a seat by a table) if given, else against a wall.
        /// </summary>
        public static PlanEntry PlaceOne(RoomPlan plan, ThingDef def, List<PlanEntry> existing, PlanEntry nextTo)
        {
            var s = NewState(plan, existing);
            var none = new RoomItem();
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

        /// <summary>
        /// The placer's state for a plan: what's already there is taken, and the cell inside each door plus the shortest
        /// walk between each pair of doors (around what's there) stays free, so people cross the room unhindered.
        /// </summary>
        private static State NewState(RoomPlan plan, List<PlanEntry> existing)
        {
            var s = new State { plan = plan, inner = plan.Interior };
            foreach (var e in existing)
                foreach (var c in e.Rect)
                    (Walkable(e.def) ? s.reserved : s.taken).Add(c);
            var inside = plan.DoorsInside;
            s.doorsInside.AddRange(inside);
            s.reserved.UnionWith(inside);
            for (int i = 0; i < inside.Count; i++)
                for (int j = i + 1; j < inside.Count; j++)
                    s.reserved.UnionWith(Flood.Path(s.inner, inside[i], inside[j], c => !s.taken.Contains(c)));
            return s;
        }

        /// <summary>
        /// Commits the game and up to 2 seats beside it (vanilla won't play it without one: requireChair). False, with
        /// nothing placed, if no seat fits.
        /// </summary>
        private static bool WithSeats(PlanEntry game, State s, Map map)
        {
            ThingDef seatDef = Needs.Best(Needs.Seat, map, null, 2);
            if (seatDef == null)
                return false;
            var taken = new HashSet<IntVec3>(s.taken);
            var reserved = new HashSet<IntVec3>(s.reserved);
            int placed = s.placed.Count;
            var none = new RoomItem();
            Commit(game, s, none);
            int seats = 0;
            for (; seats < 2 && NextTo(seatDef, game, s, none) is PlanEntry seat; seats++)
                Commit(seat, s, none);
            if (seats > 0)
                return true;
            s.taken.Clear();
            s.taken.UnionWith(taken);
            s.reserved.Clear();
            s.reserved.UnionWith(reserved);
            s.placed.RemoveRange(placed, s.placed.Count - placed);
            return false;
        }

        private static void Commit(PlanEntry entry, State s, RoomItem item)
        {
            foreach (var c in entry.Rect)
                (Walkable(entry.def) ? s.reserved : s.taken).Add(c);
            foreach (var c in KeepClear(entry, s, item))
                s.reserved.Add(c);
            if (item.medical)
                entry.medical = true;
            if (item.prisoner)
                entry.prisoner = true;
            s.placed.Add(entry);
        }

        /// <summary>
        /// A stall for a toilet or shower (HYGIENE.md §5): the fixture in the row farthest from the door, facing it, a stall
        /// door in front, and walls beside both, so nobody sees in except through the stall door (DBH's privacy check stops
        /// at walls and stall doors). The spot sharing the most stall walls already there and needing the fewest new ones
        /// wins, so stalls pack into a row; then the farthest from the door. Null if none fits.
        /// </summary>
        /// <param name="parts">The stall's new walls and its stall door.</param>
        private static PlanEntry Stall(ThingDef def, State s, out List<PlanEntry> parts)
        {
            parts = null;
            ThingDef stallDoor = Hygiene.StallDoor;
            if (stallDoor == null || def.size.x != 1 || def.size.z != 1)
                return null;
            RoomPlan plan = s.plan;
            IntVec3 inward = plan.doorInside - plan.door, along = new IntVec3(inward.z, 0, inward.x);
            var walls = new HashSet<IntVec3>(s.stalls.Where(e => e.def == ThingDefOf.Wall).Select(e => e.cell));
            bool Free(IntVec3 c) => s.inner.Contains(c) && !s.taken.Contains(c) && !s.reserved.Contains(c);
            PlanEntry best = null;
            List<IntVec3> bestWalls = null;
            float bestScore = float.MinValue;
            foreach (var cell in s.inner)
            {
                IntVec3 front = cell - inward;
                if (s.inner.Contains(cell + inward) || !Free(cell) || !Free(front) || !s.inner.Contains(front - inward))
                    continue; // the back row, with a stall door row and room to walk in front
                var newWalls = new List<IntVec3>();
                int shared = 0;
                bool ok = true;
                foreach (var side in new[] { along, -along })
                    foreach (var c in new[] { cell + side, front + side })
                    {
                        if (!s.inner.Contains(c))
                            continue; // the room's own wall
                        if (walls.Contains(c))
                            shared++;
                        else if (Free(c))
                            newWalls.Add(c);
                        else
                            ok = false;
                    }
                if (!ok)
                    continue;
                // Everything free stays reachable through the door, and every placed item keeps a free neighbour.
                var added = newWalls.Append(cell).ToList();
                s.taken.UnionWith(added);
                bool fits = AllFreeReachable(s.inner, plan.doorInside, s.taken, CellRect.Empty)
                            && s.placed.All(p => HasFreeNeighbour(p.Rect, s, CellRect.Empty));
                s.taken.ExceptWith(added);
                if (!fits)
                    continue;
                float score = (shared - newWalls.Count) * 100f + cell.DistanceToSquared(plan.doorInside);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new PlanEntry(def, cell, Rot4.FromIntVec3(-inward));
                    bestWalls = newWalls;
                }
            }
            if (best == null)
                return null;
            parts = bestWalls.Select(c => new PlanEntry(ThingDefOf.Wall, c, Rot4.North)).ToList();
            parts.Add(new PlanEntry(stallDoor, best.cell - inward, Rot4.North));
            return best;
        }

        /// <summary>The fixture and its walls are taken; the stall door is kept free (it's walked through).</summary>
        private static void CommitStall(PlanEntry fixture, List<PlanEntry> parts, State s)
        {
            Commit(fixture, s, new RoomItem());
            foreach (var p in parts)
            {
                if (p.def == ThingDefOf.Wall)
                    s.taken.Add(p.cell);
                else
                    s.reserved.Add(p.cell);
                s.stalls.Add(p);
            }
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
                    if (def.building?.isAttachment == true && s.inner.Contains(cell + rot.FacingCell))
                        continue; // a wall lamp hangs on the wall it faces
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
            var blocks = Walkable(entry.def) ? CellRect.Empty : rect; // a wall lamp or a mat is walked over
            foreach (var c in rect)
            {
                if (!s.inner.Contains(c) || s.taken.Contains(c) || s.reserved.Contains(c))
                    return false;
                if (awayFromDoor && s.doorsInside.Any(d => c.AdjacentToCardinal(d)))
                    return false;
            }
            var clear = KeepClear(entry, s, item);
            if (clear == null)
                return false;
            foreach (var c in clear)
                if (!rect.Contains(c) && (!s.inner.Contains(c) || s.taken.Contains(c))) // a throne's interaction cell is its own: it's sat on
                    return false;
            // Every placed item (this one too) keeps a free neighbour to be used and built from.
            if (!HasFreeNeighbour(rect, s, rect))
                return false;
            foreach (var p in s.placed)
                if (s.boxedInBefore?.Contains(p) != true && !HasFreeNeighbour(p.Rect, s, blocks))
                    return false;
            if (s.reachableBefore == null)
                return AllFreeReachable(s.inner, s.plan.doorInside, s.taken, blocks);
            var reachable = Reachable(s.inner, s.plan.doorInside, s.taken, blocks);
            return s.reachableBefore.All(c => blocks.Contains(c) || reachable.Contains(c));
        }

        /// <summary>People walk over it (a wall lamp, a mat, a stall door): it takes its cells from furniture, not from the walk.</summary>
        private static bool Walkable(ThingDef def) => def.passability == Traversability.Standable;

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
        private static HashSet<IntVec3> Reachable(CellRect inner, IntVec3 start, HashSet<IntVec3> taken, CellRect extra) =>
            new HashSet<IntVec3>(Flood.Run(inner, new[] { start }, c => !taken.Contains(c) && !extra.Contains(c)).Reached);
    }
}
