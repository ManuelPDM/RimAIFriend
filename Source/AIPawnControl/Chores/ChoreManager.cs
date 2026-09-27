using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// One thing a mind set up for the colony (PHASE5.md §5): marked animals, trees or rock, a bill, a field or a stockpile.
    /// Read from the map every 250 ticks: what vanilla finished closes quietly, what the player removed is remembered.
    /// </summary>
    public class Chore : IExposable
    {
        public enum Kind { Hunt, Cut, Mine, Bill, Field, Stockpile, Gather }
        public enum State { Active, Done, Stopped, Vetoed, Orphaned }

        public Pawn pawn;
        public Map map;
        public Kind kind;
        public State state;
        public int placedTick;
        public string label;   // game words: "deer", "steel", "cook simple meal", "rice plant"
        public bool byMind;
        public List<Thing> things = new List<Thing>(); // hunt and cut targets still pending
        public List<IntVec3> cells = new List<IntVec3>(); // mine cells still pending
        public Bill_Production bill;
        public Zone zone;
        public int done, vetoed;
        public bool harvested; // a field's first harvest was remembered
        private bool ripeSeen;

        public bool Active => state == State.Active;
        public PawnMind Mind => MindManager.Instance?.MindOf(pawn);

        public void Remember(string text, int importance) =>
            Mind?.memory.Record(pawn, "chore", kind.ToString(), text, importance, MemoryEvent.TookPart);

        private DesignationDef Designation => kind == Kind.Hunt ? DesignationDefOf.Hunt : kind == Kind.Mine ? DesignationDefOf.Mine : DesignationDefOf.HarvestPlant;

        public void Tick()
        {
            if (!Active || pawn == null || map == null)
                return;
            if (pawn.Dead || pawn.Destroyed || (byMind && Mind == null))
            {
                state = State.Orphaned; // what she set up stays for the colony
                ModLog.Message($"Stopped tracking {pawn.LabelShort}'s {kind} chore ({label}): {(pawn.Dead ? "dead" : "no mind")}.");
                return;
            }
            switch (kind)
            {
                case Kind.Hunt:
                case Kind.Cut:
                case Kind.Gather:
                    for (int i = things.Count - 1; i >= 0; i--)
                    {
                        Thing t = things[i];
                        if (t == null || t.Destroyed || (t is Pawn p && p.Dead))
                            done++;
                        else if (!t.Spawned || t.Map != map)
                        { } // wandered off the map: dropped quietly
                        else if (map.designationManager.DesignationOn(t, Designation) != null
                                 || (kind == Kind.Cut && map.designationManager.DesignationOn(t, DesignationDefOf.CutPlant) != null))
                            continue;
                        else if (kind == Kind.Gather && t is Plant bush && !bush.HarvestableNow)
                            done++; // a harvested bush stays and regrows
                        else
                            vetoed++;
                        things.RemoveAt(i);
                    }
                    if (things.Count == 0)
                        Close();
                    break;
                case Kind.Mine:
                    for (int i = cells.Count - 1; i >= 0; i--)
                    {
                        IntVec3 c = cells[i];
                        if (c.GetFirstMineable(map) == null)
                            done++;
                        else if (map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null)
                            continue;
                        else
                            vetoed++;
                        cells.RemoveAt(i);
                    }
                    if (cells.Count == 0)
                        Close();
                    break;
                case Kind.Bill:
                    if (bill == null || bill.DeletedOrDereferenced)
                    {
                        if (bill != null && bill.billStack?.billGiver is Thing table && table.Destroyed)
                            state = State.Orphaned; // the table went; nobody vetoed anything
                        else
                            Veto($"Someone cancelled my order: {label}.");
                        return;
                    }
                    if (bill.repeatMode == BillRepeatModeDefOf.RepeatCount && bill.repeatCount <= 0)
                    {
                        state = State.Done;
                        Remember($"My order is done: {label}.", 3);
                    }
                    break;
                case Kind.Field:
                case Kind.Stockpile:
                    if (zone == null || zone.cells.Count == 0 || !map.zoneManager.AllZones.Contains(zone))
                    {
                        Veto(kind == Kind.Field ? $"Someone removed my field: {label}." : $"Someone removed my stockpile: {label}.");
                        return;
                    }
                    if (kind == Kind.Field && !harvested && FieldHarvested())
                    {
                        harvested = true;
                        Remember($"First harvest from my field: {label}.", 4);
                    }
                    break;
            }
        }

        /// <summary>Ripe plants were seen in the field and are gone now: vanilla harvested them.</summary>
        private bool FieldHarvested()
        {
            int ripe = 0;
            foreach (var c in zone.cells)
                if (c.GetPlant(map) is Plant p && p.def == (zone as Zone_Growing)?.GetPlantDefToGrow() && p.HarvestableNow)
                    ripe++;
            if (ripe > 0)
                ripeSeen = true;
            return ripeSeen && ripe == 0;
        }

        /// <summary>No targets left: done, called off by someone, or both.</summary>
        private void Close()
        {
            if (vetoed > 0 && done == 0)
            {
                Veto(kind == Kind.Hunt ? $"Someone called off the hunt I marked: {label}."
                    : kind == Kind.Cut ? "Someone called off the tree cutting I marked."
                    : kind == Kind.Gather ? "Someone called off the wild food gathering I marked."
                    : $"Someone called off the mining I marked: {label}.");
                return;
            }
            state = State.Done;
            string text = kind == Kind.Hunt ? $"The hunt I called is over: {label} ×{done}."
                : kind == Kind.Cut ? $"The trees I marked are cut: ×{done}."
                : kind == Kind.Gather ? $"The wild food I marked is gathered: ×{done}."
                : $"The {label} I marked is mined out.";
            if (vetoed > 0)
                text += $" Someone called off {vetoed} of them.";
            Remember(text, vetoed > 0 ? 4 : 2);
            ModLog.Message($"{pawn.LabelShort}'s {kind} chore ({label}) is done: {done} done, {vetoed} called off.");
        }

        private void Veto(string text)
        {
            state = State.Vetoed;
            Remember(text, 5);
            ModLog.Message($"{pawn.LabelShort}'s {kind} chore ({label}) was removed by the player.");
        }

        /// <summary>Her own "stop …": removes what's still pending. Returns the result line.</summary>
        public string Stop()
        {
            switch (kind)
            {
                case Kind.Hunt:
                case Kind.Cut:
                case Kind.Gather:
                    foreach (var t in things.Where(t => t != null && t.Spawned))
                    {
                        map.designationManager.TryRemoveDesignationOn(t, Designation);
                        if (kind == Kind.Cut)
                            map.designationManager.TryRemoveDesignationOn(t, DesignationDefOf.CutPlant);
                    }
                    things.Clear();
                    break;
                case Kind.Mine:
                    foreach (var c in cells)
                        map.designationManager.TryRemoveDesignation(c, DesignationDefOf.Mine);
                    cells.Clear();
                    break;
                case Kind.Bill:
                    if (bill != null && !bill.DeletedOrDereferenced)
                        bill.billStack.Delete(bill);
                    break;
                case Kind.Field:
                case Kind.Stockpile:
                    if (zone != null && zone.cells.Count > 0)
                        zone.Delete();
                    break;
            }
            state = State.Stopped;
            Remember($"I changed my mind and called it off ({ChoreOptions.StopLabel(this)}).", 3);
            return "Called it off.";
        }

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // Only live things can be saved as references.
                things.RemoveAll(t => t == null || t.Destroyed);
                if (bill != null && bill.DeletedOrDereferenced && !Active)
                    bill = null;
                if (zone != null && zone.cells.Count == 0 && !Active)
                    zone = null;
            }
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref state, "state");
            Scribe_Values.Look(ref placedTick, "placedTick");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref byMind, "byMind");
            Scribe_Collections.Look(ref things, "things", LookMode.Reference);
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            Scribe_References.Look(ref bill, "bill");
            Scribe_References.Look(ref zone, "zone");
            Scribe_Values.Look(ref done, "done");
            Scribe_Values.Look(ref vetoed, "vetoed");
            Scribe_Values.Look(ref harvested, "harvested");
            Scribe_Values.Look(ref ripeSeen, "ripeSeen");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                things = things ?? new List<Thing>();
                things.RemoveAll(t => t == null);
                cells = cells ?? new List<IntVec3>();
            }
        }
    }

    /// <summary>Holds every Chore, in its own GameComponent like BuildManager. Cooldowns and caps are §6's light guardrails.</summary>
    public class ChoreManager : GameComponent
    {
        private const int CheckInterval = 250;
        public const int CooldownTicks = 2 * GenDate.TicksPerHour;
        public const int MaxBills = 3;

        private List<Chore> chores = new List<Chore>();
        private static ChoreManager instance;
        // [Work waiting]'s amounts ("mapId:workType" → how much waits), as first seen tonight and the night before.
        private Dictionary<string, int> workTonight = new Dictionary<string, int>(), workLastNight = new Dictionary<string, int>();
        private int workNight = -1;
        private readonly Game game;

        public static ChoreManager Instance => instance != null && instance.game == Current.Game ? instance : null;

        public ChoreManager(Game game)
        {
            this.game = game;
            instance = this;
        }

        /// <summary>
        /// Records how much work waits the first time it's asked on a night, and returns the night before's amounts, so
        /// every mind reflecting tonight compares with the same "last night".
        /// </summary>
        public Dictionary<string, int> WorkLastNight(int night, Dictionary<string, int> now)
        {
            if (night != workNight)
            {
                workLastNight = night == workNight + 1 ? workTonight : new Dictionary<string, int>();
                workTonight = new Dictionary<string, int>(now);
                workNight = night;
            }
            return workLastNight;
        }

        public IEnumerable<Chore> ActiveOf(Pawn pawn) => chores.Where(c => c.pawn == pawn && c.Active);

        /// <summary>The chore she set up last, open or closed (closed ones are kept a day), for [Others].</summary>
        public Chore LastOf(Pawn pawn) => chores.Where(c => c.pawn == pawn).OrderByDescending(c => c.placedTick).FirstOrDefault();

        /// <summary>"marked trees to cut", "ordered cook simple meal": what she did, in words anyone could say.</summary>
        public static string Did(Chore chore)
        {
            switch (chore.kind)
            {
                case Chore.Kind.Hunt: return $"marked {chore.label} to hunt";
                case Chore.Kind.Cut: return "marked trees to cut";
                case Chore.Kind.Gather: return "marked wild food to gather";
                case Chore.Kind.Mine: return $"marked {chore.label} to mine";
                case Chore.Kind.Bill: return $"ordered {chore.label}";
                case Chore.Kind.Field: return $"laid out a field ({chore.label})";
                default: return $"made a stockpile ({chore.label})";
            }
        }

        public IEnumerable<Chore> Active(Map map) => chores.Where(c => c.map == map && c.Active);

        /// <summary>Why she can't start a chore of this kind now, or null. Cheap: runs on every menu build.</summary>
        public string CantReason(Pawn pawn, Chore.Kind kind)
        {
            if (!AIPawnControlMod.Settings.allowChores)
                return "colony chores are off in the settings";
            int now = Find.TickManager.TicksGame;
            int active = 0;
            foreach (var c in chores)
            {
                if (c.pawn != pawn || c.kind != kind)
                    continue;
                if (now - c.placedTick < CooldownTicks)
                    return "one was set up less than 2 hours ago";
                if (c.Active)
                    active++;
            }
            int cap = kind == Chore.Kind.Bill ? MaxBills : kind == Chore.Kind.Field || kind == Chore.Kind.Stockpile ? int.MaxValue : 1;
            return active >= cap ? "the last one isn't finished yet" : null;
        }

        public Chore Add(Pawn pawn, Chore.Kind kind, string label)
        {
            var chore = new Chore
            {
                pawn = pawn,
                map = pawn.Map,
                kind = kind,
                label = label,
                placedTick = Find.TickManager.TicksGame,
                byMind = MindManager.Instance?.MindOf(pawn) != null,
            };
            chores.Add(chore);
            return chore;
        }

        /// <summary>Who set this up, if a mind did and it's still tracked.</summary>
        public Pawn OwnerOf(Bill bill) => chores.Find(c => c.Active && c.bill == bill)?.pawn;
        public Pawn OwnerOf(Zone zone) => chores.Find(c => c.Active && c.zone == zone)?.pawn;
        public Pawn OwnerOf(Thing thing) => chores.Find(c => c.Active && c.things.Contains(thing))?.pawn;
        public Pawn OwnerOf(IntVec3 cell, Map map) => chores.Find(c => c.Active && c.map == map && c.kind == Chore.Kind.Mine && c.cells.Contains(cell))?.pawn;

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 0)
                return;
            foreach (var c in chores)
                c.Tick();
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                chores.RemoveAll(c => !c.Active && Find.TickManager.TicksGame - c.placedTick > GenDate.TicksPerDay); // past the cooldown, a closed chore has no use
            Scribe_Collections.Look(ref chores, "chores", LookMode.Deep);
            Scribe_Collections.Look(ref workTonight, "workTonight", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref workLastNight, "workLastNight", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref workNight, "workNight", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                chores = chores ?? new List<Chore>();
                workTonight = workTonight ?? new Dictionary<string, int>();
                workLastNight = workLastNight ?? new Dictionary<string, int>();
                chores.RemoveAll(c => c.pawn == null || c.map == null);
            }
        }
    }
}
