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
        public bool furnishing; // one item added to a room she built (§9)

        public bool Active => state == State.Placed;
        public string Kind => kindDef?.label ?? "room";
        public string SizeLabel => $"{footprint.Width - 2}×{footprint.Height - 2}";
        public PawnMind Mind => MindManager.Instance?.MindOf(pawn);
        public PlanEntry Bed => kindDef != null && kindDef.owned ? entries.Find(e => e.def.IsBed) : null;

        /// <summary>A build event in her memory (Phase 3), if she has a mind.</summary>
        public void Remember(string text, int importance) =>
            Mind?.memory.Record(pawn, "build", kindDef?.defName, text, importance, MemoryEvent.TookPart);

        public bool Built(PlanEntry e)
        {
            foreach (var t in e.cell.GetThingList(map))
                if (t.def == e.def && t.Position == e.cell)
                    return true;
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

        public Room Room => footprint.ContractedBy(1).CenterCell.GetRoom(map);

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
                CheckFirstNight();
                return;
            }

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
            if (bedEntry != null && pawn.ownership != null && bedEntry.cell.GetFirstThing<Building_Bed>(map) is Building_Bed bed
                && bed.def == bedEntry.def && bed.Faction == Faction.OfPlayer && !bed.IsOwner(pawn))
            {
                pawn.ownership.ClaimBedIfNonMedical(bed);
                ModLog.Message($"{pawn.LabelShort} claimed the bed in the new {Kind}.");
            }
            if (IsDone(out Room room))
            {
                state = State.Done;
                ModLog.Message($"{pawn.LabelShort}'s {(furnishing ? entries[0].def.label + " " + where : Kind)} is done: {room.Role.label}, {room.CellCount} cells, impressiveness {room.GetStat(RoomStatDefOf.Impressiveness):0.0} ({BuildManager.Impressiveness(room)}).");
                Messages.Message($"{pawn.LabelShort}'s new {Kind} is finished.", pawn, MessageTypeDefOf.PositiveEvent, false);
                if (furnishing)
                    Remember($"Added {where}: {entries[0].def.label}.", 4);
                else
                    Remember($"The {Kind} I designed is finished ({SizeLabel}, {material?.label}) {where}.", 6);
            }
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
            if (room == null || !room.ProperRoom || room.OpenRoofCount > 0 || room.PsychologicallyOutdoors)
                return false;
            if (kindDef?.role != null && room.Role != kindDef.role)
                return false;
            PlanEntry bedEntry = Bed;
            return bedEntry == null || pawn.ownership?.OwnedBed?.Position == bedEntry.cell;
        }

        private void CheckFirstNight()
        {
            if (firstNight || furnishing || Bed == null)
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
            var need = new Dictionary<ThingDef, int>();
            foreach (var e in entries)
            {
                if (Built(e))
                    continue;
                var pending = Pending(e);
                if (pending is Frame frame)
                    foreach (var cost in frame.TotalMaterialCost())
                        Add(need, cost.thingDef, frame.ThingCountNeeded(cost.thingDef));
                else if (pending is Blueprint blueprint)
                    foreach (var cost in blueprint.TotalMaterialCost())
                        Add(need, cost.thingDef, cost.count);
            }
            var missing = new Dictionary<ThingDef, int>();
            foreach (var kv in need)
            {
                int short_ = kv.Value - map.resourceCounter.GetCount(kv.Key);
                if (short_ > 0)
                    missing[kv.Key] = short_;
            }
            return missing;
        }

        private static void Add(Dictionary<ThingDef, int> d, ThingDef def, int n)
        {
            d.TryGetValue(def, out int had);
            d[def] = had + n;
        }

        /// <summary>
        /// "Hospital (5×5, granite blocks) against the kitchen's west wall: walls 14/20 built, door not started, bed 0/3,
        /// waiting on 40 granite blocks."
        /// </summary>
        public string StatusLine()
        {
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
                parts.Add(Bed != null && pawn.ownership?.OwnedBed?.Position != Bed.cell ? "the bed isn't mine yet" : "waiting for the roof");
            if (furnishing)
                return $"Adding a {entries[0].def.label} ({material?.label ?? "no material"}) {where}: {string.Join(", ", parts)}.";
            return $"{Kind.CapitalizeFirst()} ({SizeLabel}, {material?.label}) {where}: {string.Join(", ", parts)}.";
        }

        /// <summary>Her choice (Act) or the player's veto (a cancelled blueprint): leftover blueprints go, frames and built walls stay.</summary>
        public void Abandon(bool byPlayer)
        {
            foreach (var e in entries)
                if (Pending(e) is Blueprint b)
                    b.Destroy(DestroyMode.Cancel);
            state = State.Abandoned;
            if (byPlayer && furnishing)
                Remember($"Someone cancelled the {entries[0].def.label} I was adding {where}.", 5);
            else if (byPlayer)
            {
                Remember($"Someone cancelled the blueprints for my {Kind} {where}. The project is off.", 6);
                Messages.Message($"{pawn.LabelShort}'s {Kind} project was called off: its blueprints were cancelled.", pawn, MessageTypeDefOf.NeutralEvent, false);
            }
            else
                Remember(furnishing ? $"I gave up on adding a {entries[0].def.label} {where}." : $"I gave up on my {Kind} {where}.", 4);
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
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                kindDef = kindDef ?? RoomKindDef.Bedroom; // saves from before room kinds held only bedrooms
                builtOnce = builtOnce ?? new List<int>();
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

        public void Add(BuildProject project) => projects.Add(project);

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
                parts.Add($"My bedroom: {(mine != null ? mine.SizeLabel : own.CellCount + " cells")}, {Impressiveness(own)}.");
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
            ModLog.Message($"{pawn.LabelShort} laid out a {plan.kind.label} ({plan.SizeLabel}) in {material.label}, {where}\n{TextMap.Draw(plan)}");
            project.Remember($"I laid out a {plan.kind.label} ({plan.SizeLabel}, {material.label}) {where}.", 5);
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
            Scribe_Collections.Look(ref projects, "projects", LookMode.Deep);
            Scribe_Collections.Look(ref emptyScans, "emptyScans", LookMode.Reference, LookMode.Value, ref emptyScanKeys, ref emptyScanValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                projects = projects ?? new List<BuildProject>();
                projects.RemoveAll(p => p.pawn == null || p.map == null);
                emptyScans = emptyScans ?? new Dictionary<Pawn, int>();
                emptyScans.RemoveAll(kv => kv.Key == null);
            }
        }
    }
}
