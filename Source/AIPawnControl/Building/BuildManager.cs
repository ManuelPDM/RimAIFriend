using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// A room a mind laid out (PHASE4.md §6), read from the map every 250 ticks: beds claimed or set to medical as they're
    /// built, the player's cancels, done, and her first night in her own room. Her memory gets each milestone. A shortage
    /// only shows in [My project] ("waiting on 40 wood"): vanilla finishes the job once material arrives.
    /// </summary>
    public class BuildProject : IExposable
    {
        public enum State { Placed, Done, Abandoned }

        public Pawn pawn;
        public Map map;
        public RoomKindDef kindDef;
        public CellRect footprint;
        public List<PlanEntry> entries = new List<PlanEntry>();
        public ThingDef material;
        public State state;
        public int placedTick;
        public string where; // "against the kitchen's west wall", for her memory and [My project]
        public bool byMind;  // placed by a mind (not a dev tool without one): losing the mind orphans it
        private List<int> builtOnce = new List<int>(); // entry indexes seen built: gone later is a deconstruct, not a cancel
        public bool firstNight;
        public bool furnishing; // an upgrade to a room: one item, or a floor (FURNISHING.md §5)
        public TerrainDef floor; // a floor upgrade: this terrain on floorCells (entries is empty)
        public List<IntVec3> floorCells = new List<IntVec3>();
        public Pawn occupant;   // a bedroom she built for someone with no room of their own (STREAMLINE.md §7); null = her own
        public bool unclaimed;  // nobody's: the bed stays free and vanilla assigns it
        public bool outfitted;  // its bills and stockpile were added once it was done (STREAMLINE.md §7)
        public IntVec3 roomCell = IntVec3.Invalid; // a cell inside its room, for shapes with no centre (BASE_LAYOUT.md)
        public IntVec3 closeDoor = IntVec3.Invalid; // a way out being closed: the door comes down, then the wall goes up

        /// <summary>Whose bed it is: hers, or the colonist she built it for.</summary>
        public Pawn Owner => occupant ?? pawn;

        /// <summary>"bedroom", or "bedroom for Kira".</summary>
        public string KindFor => occupant != null ? $"{Kind} for {occupant.LabelShort}" : Kind;

        /// <summary>The one she built it for died, left, or got a room of their own elsewhere: the bed stays unclaimed, an ordinary room.</summary>
        private bool OccupantGone(PlanEntry bedEntry) =>
            occupant != null && (occupant.Dead || occupant.Destroyed || occupant.Map != map
                                 || (occupant.ownership?.OwnedRoom != null && occupant.ownership.OwnedBed?.Position != bedEntry.cell));

        public bool Active => state == State.Placed;
        public string Kind => kindDef?.label ?? "room";
        /// <summary>What an upgrade adds: "end table", "wooden floor".</summary>
        public string ItemLabel => floor != null ? floor.label : entries.Count > 0 ? entries[0].def.label : Kind;
        public string SizeLabel => $"{footprint.Width - 2}×{footprint.Height - 2}";
        /// <summary>"5×5, granite blocks", or just the material for a layout project, which has no box size (BASE_LAYOUT.md).</summary>
        private string SizeAndMaterial => roomCell.IsValid ? material?.label : $"{SizeLabel}, {material?.label}";
        public PawnMind Mind => MindManager.Instance?.MindOf(pawn);
        public PlanEntry Bed => kindDef != null && kindDef.owned && !unclaimed ? entries.Find(e => e.def.IsBed) : null;

        /// <summary>A build event in her memory (Phase 3), if she has a mind.</summary>
        public void Remember(string text, int importance) =>
            Mind?.memory.Record(pawn, "build", kindDef?.defName, text, importance, MemoryEvent.TookPart);

        /// <summary>Built, or a wall another room's door has taken the place of (it still closes the room in).</summary>
        public bool Built(PlanEntry e)
        {
            foreach (var t in e.cell.GetThingList(map))
            {
                if (t.def == e.def && t.Position == e.cell)
                    return true;
                if (e.def == ThingDefOf.Wall && (t is Building_Door || ((t is Blueprint || t is Frame) && t.def.entityDefToBuild is ThingDef d && d.IsDoor)))
                    return true;
            }
            return false;
        }

        /// <summary>Its blueprint or frame, if one is there.</summary>
        private Thing Pending(PlanEntry e)
        {
            foreach (var t in e.cell.GetThingList(map))
                if ((t is Blueprint || t is Frame) && t.def.entityDefToBuild == e.def && t.Position == e.cell)
                    return t;
            return null;
        }

        public Room Room => (roomCell.IsValid ? roomCell : footprint.ContractedBy(1).CenterCell).GetRoom(map);

        public void Tick()
        {
            if (pawn == null || map == null || state == State.Abandoned)
                return;
            if (pawn.Dead || pawn.Destroyed || (byMind && Mind == null))
            {
                state = State.Abandoned; // orphaned: tracking stops, the blueprints stay for the colony
                ModLog.Message($"Stopped tracking {pawn.LabelShort}'s {Kind}: {(pawn.Dead ? "dead" : "no mind")}.");
                return;
            }
            if (state == State.Done)
            {
                if (!outfitted && !furnishing)
                {
                    outfitted = true;
                    Outfitting.Outfit(this);
                }
                CheckFirstNight();
                return;
            }

            if (floor != null)
            {
                TickFloor();
                return;
            }
            if (closeDoor.IsValid && !TickClose())
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (Built(e))
                {
                    if (!builtOnce.Contains(i))
                        builtOnce.Add(i);
                    continue;
                }
                if (!builtOnce.Contains(i) && Pending(e) == null)
                {
                    Abandon(byPlayer: true);
                    return;
                }
            }
            foreach (var e in entries)
                if (e.medical && e.cell.GetFirstThing<Building_Bed>(map) is Building_Bed medicalBed && medicalBed.def == e.def && !medicalBed.Medical)
                    medicalBed.Medical = true;
            PlanEntry bedEntry = Bed;
            Pawn owner = Owner;
            if (bedEntry != null && owner.ownership != null && !OccupantGone(bedEntry) && bedEntry.cell.GetFirstThing<Building_Bed>(map) is Building_Bed bed
                && bed.def == bedEntry.def && bed.Faction == Faction.OfPlayer && !bed.IsOwner(owner))
            {
                owner.ownership.ClaimBedIfNonMedical(bed);
                ModLog.Message($"{owner.LabelShort} claimed the bed in the new {KindFor}.");
            }
            if (IsDone(out Room room))
            {
                state = State.Done;
                ModLog.Message($"{pawn.LabelShort}'s {(furnishing ? ItemLabel + " " + where : Kind)} is done: {room.Role.label}, {room.CellCount} cells, impressiveness {room.GetStat(RoomStatDefOf.Impressiveness):0.0} ({BuildManager.Impressiveness(room)}).");
                Messages.Message($"{pawn.LabelShort}'s new {Kind} is finished.", pawn, MessageTypeDefOf.PositiveEvent, false);
                if (furnishing)
                    Remember($"Added {where}: {ItemLabel}.", 4);
                else
                    Remember($"The {KindFor} I designed is finished ({SizeAndMaterial}) {where}.", 6);
                if (occupant != null && !OccupantGone(Bed))
                    MindManager.Instance?.MindOf(occupant)?.memory.Record(occupant, "build", kindDef?.defName,
                        $"{pawn.LabelShort} built me a bedroom {where}.", 6, MemoryEvent.TookPart, new[] { pawn.LabelShort });
            }
        }

        /// <summary>
        /// Closing a way out: wait while the door is deconstructed (cancelling that calls it off), then place the wall.
        /// True once the wall is placed, so the usual tracking takes over.
        /// </summary>
        private bool TickClose()
        {
            if (closeDoor.GetEdifice(map) is Building_Door door)
            {
                if (map.designationManager.DesignationOn(door, DesignationDefOf.Deconstruct) == null)
                    Abandon(byPlayer: true);
                return false;
            }
            var wall = entries[0];
            if (Pending(wall) == null && !Built(wall))
                GenConstruct.PlaceBlueprintForBuild(wall.def, wall.cell, map, wall.rot, Faction.OfPlayer, wall.stuff);
            closeDoor = IntVec3.Invalid;
            return true;
        }

        /// <summary>A floor upgrade is done when no cell waits any more (a cancelled cell just isn't floored); off if none got built.</summary>
        private void TickFloor()
        {
            if (floorCells.Any(c => FloorPending(c) != null))
                return;
            int built = floorCells.Count(c => c.GetTerrain(map) == floor);
            if (built == 0)
            {
                Abandon(byPlayer: true);
                return;
            }
            state = State.Done;
            ModLog.Message($"{pawn.LabelShort}'s {floor.label} {where} is done: {built}/{floorCells.Count} cells.");
            Remember($"Laid {floor.label} {where}.", 4);
        }

        private Thing FloorPending(IntVec3 c)
        {
            foreach (var t in c.GetThingList(map))
                if ((t is Blueprint || t is Frame) && t.def.entityDefToBuild == floor)
                    return t;
            return null;
        }

        /// <summary>Everything built, a proper roofed indoor room, vanilla gives it the kind's role, and for her bedroom, the bed is hers.</summary>
        private bool IsDone(out Room room)
        {
            room = null;
            if (!entries.TrueForAll(Built))
                return false;
            room = Room;
            if (furnishing)
                return room != null;
            if (!Ground.Indoor(room) || !room.ProperRoom || room.OpenRoofCount > 0)
                return false;
            if (kindDef?.role != null && room.Role != kindDef.role)
                return false;
            PlanEntry bedEntry = Bed;
            return bedEntry == null || Owner.ownership?.OwnedBed?.Position == bedEntry.cell || OccupantGone(bedEntry);
        }

        private void CheckFirstNight()
        {
            if (firstNight || furnishing || Bed == null || occupant != null)
                return;
            if (!pawn.Awake() && pawn.CurrentBed() is Building_Bed bed && bed.Position == Bed.cell)
            {
                firstNight = true;
                Remember($"My first night in my own {Kind}.", 5);
            }
        }

        /// <summary>Material the unbuilt parts still need beyond what's in storage.</summary>
        public Dictionary<ThingDef, int> Missing()
        {
            var need = Need();
            var missing = new Dictionary<ThingDef, int>();
            foreach (var kv in need)
            {
                int short_ = kv.Value - map.resourceCounter.GetCount(kv.Key);
                if (short_ > 0)
                    missing[kv.Key] = short_;
            }
            return missing;
        }

        /// <summary>Material the unbuilt parts still need, whatever is in storage.</summary>
        public Dictionary<ThingDef, int> Need()
        {
            var pending = floorCells.Where(c => floor != null).Select(FloorPending)
                .Concat(entries.Where(e => !Built(e)).Select(Pending));
            return Supplies.StillNeeds(pending.Where(t => t != null));
        }

        /// <summary>
        /// "Hospital (5×5, granite blocks) against the kitchen's west wall: walls 14/20 built, door not started, bed 0/3,
        /// waiting on 40 granite blocks."
        /// </summary>
        public string StatusLine()
        {
            if (floor != null)
            {
                int done = floorCells.Count(c => c.GetTerrain(map) == floor);
                var short_ = Missing();
                return $"Laying {floor.label} {where}: {done}/{floorCells.Count} cells" + (short_.Count > 0 ? ", waiting on " + string.Join(", ", short_.Select(kv => $"{kv.Value} {kv.Key.label}")) : "") + ".";
            }
            var parts = new List<string>();
            foreach (var group in entries.GroupBy(e => e.def == ThingDefOf.Wall ? "walls" : e.def.label)
                         .OrderBy(g => g.Key == "walls" ? 0 : g.First().def == ThingDefOf.Door ? 1 : 2))
            {
                int total = group.Count(), built = group.Count(Built), started = group.Count(e => Pending(e) is Frame);
                string label = group.Key;
                if (built == total)
                    parts.Add(total == 1 ? $"{label} built" : $"{label} {built}/{total} built");
                else if (built == 0 && started == 0)
                    parts.Add($"{label} not started");
                else
                    parts.Add(total == 1 ? $"{label} under construction" : $"{label} {built}/{total} built");
            }
            var missing = Missing();
            if (missing.Count > 0)
                parts.Add("waiting on " + string.Join(", ", missing.Select(kv => $"{kv.Value} {kv.Key.label}")));
            if (entries.TrueForAll(Built))
                parts.Add(Bed != null && Owner.ownership?.OwnedBed?.Position != Bed.cell
                    ? (occupant != null ? $"the bed isn't {occupant.LabelShort}'s yet" : "the bed isn't mine yet") : "waiting for the roof");
            if (furnishing)
                return $"Adding a {ItemLabel} ({material?.label ?? "no material"}) {where}: {string.Join(", ", parts)}.";
            return $"{KindFor.CapitalizeFirst()} ({SizeAndMaterial}) {where}: {string.Join(", ", parts)}.";
        }

        /// <summary>Her choice (Act) or the player's veto (a cancelled blueprint): leftover blueprints go, frames and built walls stay.</summary>
        public void Abandon(bool byPlayer)
        {
            foreach (var e in entries)
                if (Pending(e) is Blueprint b)
                    b.Destroy(DestroyMode.Cancel);
            foreach (var c in floorCells)
                if (floor != null && FloorPending(c) is Blueprint fb)
                    fb.Destroy(DestroyMode.Cancel);
            state = State.Abandoned;
            if (byPlayer && furnishing)
                Remember($"Someone cancelled the {ItemLabel} I was adding {where}.", 5);
            else if (byPlayer)
            {
                Remember($"Someone cancelled the blueprints for my {Kind} {where}. The project is off.", 6);
                Messages.Message($"{pawn.LabelShort}'s {Kind} project was called off: its blueprints were cancelled.", pawn, MessageTypeDefOf.NeutralEvent, false);
            }
            else
                Remember(furnishing ? $"I gave up on adding a {ItemLabel} {where}." : $"I gave up on my {Kind} {where}.", 4);
            ModLog.Message($"{pawn.LabelShort}'s {Kind} abandoned ({(byPlayer ? "the player cancelled a blueprint" : "her choice")}).");
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref map, "map");
            Scribe_Defs.Look(ref kindDef, "kindDef");
            Scribe_Values.Look(ref footprint, "footprint");
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            Scribe_Defs.Look(ref material, "material");
            Scribe_Values.Look(ref state, "state");
            Scribe_Values.Look(ref placedTick, "placedTick");
            Scribe_Values.Look(ref where, "where");
            Scribe_Values.Look(ref byMind, "byMind");
            Scribe_Collections.Look(ref builtOnce, "builtOnce", LookMode.Value);
            Scribe_Values.Look(ref firstNight, "firstNight");
            Scribe_Values.Look(ref furnishing, "furnishing");
            Scribe_Defs.Look(ref floor, "floor");
            Scribe_Collections.Look(ref floorCells, "floorCells", LookMode.Value);
            Scribe_References.Look(ref occupant, "occupant");
            Scribe_Values.Look(ref unclaimed, "unclaimed");
            Scribe_Values.Look(ref outfitted, "outfitted");
            Scribe_Values.Look(ref roomCell, "roomCell", IntVec3.Invalid);
            Scribe_Values.Look(ref closeDoor, "closeDoor", IntVec3.Invalid);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                builtOnce = builtOnce ?? new List<int>();
                floorCells = floorCells ?? new List<IntVec3>();
            }
        }
    }

    /// <summary>Holds every BuildProject, in its own GameComponent so Phase 4 doesn't touch the mind files.</summary>
    public class BuildManager : GameComponent
    {
        private const int CheckInterval = 250;
        /// <summary>No new project this long after one is placed (§8); one active project at a time still applies.</summary>
        public const int CooldownTicks = 2 * GenDate.TicksPerHour;
        /// <summary>No new try this long after a scan found nothing (§5): the map needs time to change.</summary>
        public const int EmptyScanRetryTicks = 2 * GenDate.TicksPerDay;

        private List<BuildProject> projects = new List<BuildProject>();
        // Door cells our projects placed, per map id: kept when a project's record goes (its builder died), since only
        // these doors are ever closed (BASE_LAYOUT.md §5.5).
        private HashSet<(int, IntVec3)> ourDoors = new HashSet<(int, IntVec3)>();
        private List<int> ourDoorMaps;
        private List<IntVec3> ourDoorCells;
        private Dictionary<Pawn, int> emptyScans = new Dictionary<Pawn, int>();
        private List<Pawn> emptyScanKeys;
        private List<int> emptyScanValues;
        private static BuildManager instance;
        private readonly Game game;

        public static BuildManager Instance => instance != null && instance.game == Current.Game ? instance : null;

        public BuildManager(Game game)
        {
            this.game = game;
            instance = this;
        }

        public BuildProject ActiveProject(Pawn pawn) => projects.Find(p => p.pawn == pawn && p.Active);

        public IEnumerable<BuildProject> ProjectsOf(Pawn pawn) => projects.Where(p => p.pawn == pawn);

        /// <summary>Rooms being built on the map (not furnishing), for the ladder.</summary>
        public IEnumerable<BuildProject> ActiveOn(Map map) => projects.Where(p => p.Active && p.map == map && !p.furnishing);

        /// <summary>Everything being built on the map, upgrades included.</summary>
        public IEnumerable<BuildProject> ActiveOnAll(Map map) => projects.Where(p => p.Active && p.map == map);

        /// <summary>Who designed a finished room, for [Rooms]' credit, or null.</summary>
        public Pawn BuilderOf(Room room) => projects.Find(p => p.state == BuildProject.State.Done && !p.furnishing && p.map == room.Map && p.Room == room)?.pawn;

        public void Add(BuildProject project) => projects.Add(project);

        /// <summary>A door one of our projects placed: only those are ever closed (BASE_LAYOUT.md §5.5).</summary>
        public bool OurDoor(Map map, IntVec3 cell) => ourDoors.Contains((map.uniqueID, cell));

        private void RecordDoors(BuildProject p)
        {
            if (p.map != null)
                foreach (var e in p.entries)
                    if (e.def != null && e.def.IsDoor)
                        ourDoors.Add((p.map.uniqueID, e.cell));
        }

        private int LastPlacedTick(Pawn pawn)
        {
            int last = -CooldownTicks;
            foreach (var p in projects)
                if (p.pawn == pawn && p.placedTick > last)
                    last = p.placedTick;
            return last;
        }

        /// <summary>Why she can't plan a new room right now, or null. Cheap: the site scan runs only once she picks the option (§14).</summary>
        public string CantPlanReason(Pawn pawn)
        {
            int now = Find.TickManager.TicksGame;
            if (!AIPawnControlMod.Settings.allowBuilding)
                return "building is off in the settings";
            if (pawn.Map == null)
                return "not on a map";
            if (ActiveProject(pawn) != null)
                return "a project is already running";
            if (now - LastPlacedTick(pawn) < CooldownTicks)
                return "a project was placed less than 2 hours ago";
            if (emptyScans.TryGetValue(pawn, out int empty) && now - empty < EmptyScanRetryTicks)
                return "no site was found less than 2 days ago";
            if (!RoomKindDef.Plain.BuildableNow(pawn.Map))
                return "walls or doors can't be built";
            return null;
        }

        public void EmptyScan(Pawn pawn) => emptyScans[pawn] = Find.TickManager.TicksGame;

        /// <summary>[My project]: her running project's progress, or null.</summary>
        public string ProjectLine(Pawn pawn) => ActiveProject(pawn)?.StatusLine();

        /// <summary>
        /// For [Me] (§7): "My bedroom: 5×5, dull." and "I built: kitchen (decent).", or where she sleeps when it isn't her
        /// own room: "I sleep in the barracks." / "I have no bed of my own."
        /// </summary>
        public string RoomsPhrase(Pawn pawn)
        {
            var parts = new List<string>();
            Building_Bed bed = pawn.ownership?.OwnedBed;
            Room own = pawn.ownership?.OwnedRoom;
            if (own != null)
            {
                var mine = projects.Find(p => p.pawn == pawn && p.state == BuildProject.State.Done && !p.furnishing && p.Bed != null && p.Room == own);
                parts.Add($"My {own.Role.label}: {(mine != null ? mine.SizeLabel : own.CellCount + " cells")}, {Impressiveness(own)}.");
            }
            else if (bed == null)
                parts.Add("I have no bed of my own.");
            else
            {
                Room room = bed.GetRoom();
                parts.Add(room == null || room.PsychologicallyOutdoors ? "My bed is outdoors." : $"I sleep in the {room.GetRoomRoleLabel()}.");
            }
            var built = projects.Where(p => p.pawn == pawn && p.state == BuildProject.State.Done && !p.furnishing && p.Bed == null && p.map == pawn.Map)
                .Select(p => (p, room: p.Room))
                .Where(x => x.room != null && x.room.ProperRoom)
                .Select(x => $"{x.room.GetRoomRoleLabel()} ({Impressiveness(x.room)})")
                .ToList();
            if (built.Count > 0)
                parts.Add($"I built: {string.Join(", ", built)}.");
            return string.Join(" ", parts);
        }

        public static string Impressiveness(Room room) =>
            RoomStatDefOf.Impressiveness.GetScoreStage(room.GetStat(RoomStatDefOf.Impressiveness)).label;

        /// <summary>Re-validates, then places every entry as an ordinary player blueprint, records the project and her memory of it.</summary>
        public BuildProject Place(Pawn pawn, RoomPlan plan, ThingDef material, RoomValidator validator, string where)
        {
            var failures = validator.Check(plan, material);
            if (failures.Count > 0)
            {
                ModLog.Warning($"Not placed, the validator failed: {string.Join("; ", failures)}\n{TextMap.Draw(plan)}");
                return null;
            }
            plan.ApplyMaterial(material);
            foreach (var e in plan.entries)
                GenConstruct.PlaceBlueprintForBuild(e.def, e.cell, plan.map, e.rot, Faction.OfPlayer, e.stuff);
            var project = new BuildProject
            {
                pawn = pawn,
                map = plan.map,
                kindDef = plan.kind,
                footprint = plan.footprint,
                entries = plan.entries,
                material = material,
                placedTick = Find.TickManager.TicksGame,
                where = where,
                byMind = MindManager.Instance?.MindOf(pawn) != null,
            };
            projects.Add(project);
            RecordDoors(project);
            ModLog.Message($"{pawn.LabelShort} laid out a {plan.kind.label} ({plan.SizeLabel}) in {material.label}, {where}\n{TextMap.Draw(plan)}");
            project.Remember($"I laid out a {plan.kind.label} ({plan.SizeLabel}, {material.label}) {where}.", 5);
            return project;
        }

        /// <summary>
        /// A doorway or a closed way out (BASE_LAYOUT.md): one cell, its checks were Layout's, so it's placed as is. roomCell
        /// is a cell inside the room it opens or closes.
        /// </summary>
        public BuildProject PlaceLayout(Pawn pawn, RoomPlan plan, ThingDef material, IntVec3 roomCell, string where, Building_Door close = null)
        {
            plan.ApplyMaterial(material);
            if (close != null)
                plan.map.designationManager.AddDesignation(new Designation(close, DesignationDefOf.Deconstruct));
            else
                foreach (var e in plan.entries)
                    GenConstruct.PlaceBlueprintForBuild(e.def, e.cell, plan.map, e.rot, Faction.OfPlayer, e.stuff);
            var project = new BuildProject
            {
                pawn = pawn, map = plan.map, kindDef = plan.kind, footprint = plan.footprint, entries = plan.entries, material = material,
                placedTick = Find.TickManager.TicksGame, where = where, byMind = MindManager.Instance?.MindOf(pawn) != null, roomCell = roomCell,
                closeDoor = close?.Position ?? IntVec3.Invalid,
            };
            projects.Add(project);
            RecordDoors(project);
            ModLog.Message($"{pawn.LabelShort} laid out a {plan.kind.label} in {material.label}, {where}: {plan.entries.Count} blueprints.");
            project.Remember($"I laid out a {plan.kind.label} ({material.label}) {where}.", 5);
            return project;
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 0)
                return;
            foreach (var p in projects)
                p.Tick();
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                // Past the cooldown, an abandoned project or a finished upgrade has no use (finished rooms stay: [Me] and [Rooms] credit them).
                projects.RemoveAll(p => (p.state == BuildProject.State.Abandoned || (p.state == BuildProject.State.Done && p.furnishing))
                                        && Find.TickManager.TicksGame - p.placedTick > GenDate.TicksPerDay);
            Scribe_Collections.Look(ref projects, "projects", LookMode.Deep);
            Scribe_Collections.Look(ref emptyScans, "emptyScans", LookMode.Reference, LookMode.Value, ref emptyScanKeys, ref emptyScanValues);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                ourDoorMaps = ourDoors.Select(d => d.Item1).ToList();
                ourDoorCells = ourDoors.Select(d => d.Item2).ToList();
            }
            Scribe_Collections.Look(ref ourDoorMaps, "ourDoorMaps", LookMode.Value);
            Scribe_Collections.Look(ref ourDoorCells, "ourDoorCells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                projects = projects ?? new List<BuildProject>();
                ourDoors = new HashSet<(int, IntVec3)>();
                for (int i = 0; ourDoorMaps != null && ourDoorCells != null && i < ourDoorMaps.Count && i < ourDoorCells.Count; i++)
                    ourDoors.Add((ourDoorMaps[i], ourDoorCells[i]));
                projects.RemoveAll(p => p.pawn == null || p.map == null);
                emptyScans = emptyScans ?? new Dictionary<Pawn, int>();
                emptyScans.RemoveAll(kv => kv.Key == null);
            }
        }
    }
}
