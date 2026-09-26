using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>A room a mind laid out (PHASE4.md §6). Steps 1–2 cover placing, bed claiming and done; stalls and cancels come in step 4.</summary>
    public class BuildProject : IExposable
    {
        public enum State { Placed, Done, Abandoned }

        public Pawn pawn;
        public Map map;
        public string kind = "bedroom";
        public CellRect footprint;
        public List<PlanEntry> entries = new List<PlanEntry>();
        public ThingDef material;
        public State state;
        public int placedTick;

        public bool Active => state == State.Placed;
        public PlanEntry Bed => entries.Find(e => e.def == ThingDefOf.Bed);

        public bool Built(PlanEntry e)
        {
            foreach (var t in e.cell.GetThingList(map))
                if (t.def == e.def && t.Position == e.cell)
                    return true;
            return false;
        }

        /// <summary>Claims the bed the moment it's built, then checks done from the map, never assuming it.</summary>
        public void Tick()
        {
            if (!Active || pawn == null || pawn.Dead || map == null || pawn.ownership == null)
                return;
            PlanEntry bedEntry = Bed;
            if (bedEntry != null && bedEntry.cell.GetFirstThing<Building_Bed>(map) is Building_Bed bed && bed.Faction == Faction.OfPlayer && !bed.IsOwner(pawn))
            {
                pawn.ownership.ClaimBedIfNonMedical(bed);
                ModLog.Message($"{pawn.LabelShort} claimed the bed in the new {kind}.");
            }
            if (!entries.TrueForAll(Built))
                return;
            Room room = footprint.ContractedBy(1).CenterCell.GetRoom(map);
            if (room == null || !room.ProperRoom || room.OpenRoofCount > 0 || room.PsychologicallyOutdoors)
                return;
            if (bedEntry != null && pawn.ownership.OwnedBed?.Position != bedEntry.cell)
                return;
            state = State.Done;
            ModLog.Message($"{pawn.LabelShort}'s {kind} is done: {room.Role.label}, {room.CellCount} cells.");
            Messages.Message($"{pawn.LabelShort}'s new {kind} is finished.", pawn, MessageTypeDefOf.PositiveEvent, false);
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref kind, "kind", "bedroom");
            Scribe_Values.Look(ref footprint, "footprint");
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            Scribe_Defs.Look(ref material, "material");
            Scribe_Values.Look(ref state, "state");
            Scribe_Values.Look(ref placedTick, "placedTick");
        }
    }

    /// <summary>Holds every BuildProject, in its own GameComponent so Phase 4 doesn't touch the mind files.</summary>
    public class BuildManager : GameComponent
    {
        private const int CheckInterval = 250;

        private List<BuildProject> projects = new List<BuildProject>();
        private static BuildManager instance;
        private readonly Game game;

        public static BuildManager Instance => instance != null && instance.game == Current.Game ? instance : null;

        public BuildManager(Game game)
        {
            this.game = game;
            instance = this;
        }

        public BuildProject ActiveProject(Pawn pawn) => projects.Find(p => p.pawn == pawn && p.Active);

        public void Add(BuildProject project) => projects.Add(project);

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
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                projects = projects ?? new List<BuildProject>();
                projects.RemoveAll(p => p.pawn == null || p.map == null);
            }
        }
    }
}
