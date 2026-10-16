using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIPawnControl
{
    /// <summary>
    /// "spend time with X now: playing chess": two pawns doing one of vanilla's joy activities together, so what they agree
    /// on in a conversation can happen. Vanilla's own jobs carry it: a joy thing made for more than one (a chess table, a
    /// TV), or a joy vanilla gives at a gathering spot (relaxing socially, a drink). Each gets one ordinary joy job; the
    /// talk, thoughts and joy while they're together are vanilla's. With Romance on the Rim, lovers also get its romantic
    /// interactions, each when its own check passes. Offered in the Message call for the pawn she's talking to.
    /// </summary>
    public static class Together
    {
        public class Activity
        {
            public string key;
            public string label;
            public Func<Pawn, Pawn, string> start; // asker, other: null once both are on their way, else why not
        }

        /// <summary>Vanilla gives no joy job at a gathering above this (JobGiver_GetJoyInGatheringArea).</summary>
        private const float MaxJoy = 0.92f;

        private static readonly MethodInfo CanInteractWith = AccessTools.Method(typeof(JoyGiver_InteractBuilding), "CanInteractWith");
        private static readonly MethodInfo TryGivePlayJob = AccessTools.Method(typeof(JoyGiver_InteractBuilding), "TryGivePlayJob");

        // Romance on the Rim, when it's loaded: no reference to it, only its names.
        private static readonly Type RomanticDef = GenTypes.GetTypeInAnyAssembly("RomanceOnTheRim.RomanticInteractionDef");
        private static readonly Type RomanticWorker = GenTypes.GetTypeInAnyAssembly("RomanceOnTheRim.RomanticInteractionWorker");

        /// <summary>What the two can do together right now, nearest first within each kind. Empty in danger.</summary>
        public static List<Activity> For(Pawn a, Pawn b)
        {
            var list = new List<Activity>();
            if (!Free(a) || !Free(b) || a.Map != b.Map || DangerResponse.Threats(a.Map).Count > 0)
                return list;
            if (WantsJoy(a) && WantsJoy(b))
            {
                foreach (var def in DefDatabase<JoyGiverDef>.AllDefsListForReading)
                    Try(def.defName, () => SharedThing(def, a, b), list);
                foreach (var def in DefDatabase<JoyGiverDef>.AllDefsListForReading.Where(d => list.All(x => x.key != d.defName)))
                    Try(def.defName, () => AtGatheringSpot(def, a, b), list);
            }
            if (RomanticDef != null && RomanticWorker != null && LovePartnerRelationUtility.LovePartnerRelationExists(a, b))
                foreach (var def in GenDefDatabase.GetAllDefsInDatabaseForDef(RomanticDef))
                    Try(def.defName, () => Romantic(def, a, b), list);
            return list;
        }

        /// <summary>
        /// Asks the other pawn along: a mind answers with its Answer call (PawnMind.Invite); a colonist without one comes
        /// when what it's doing is something vanilla lets a joy or social job interrupt. Returns the result line.
        /// </summary>
        public static string Invite(PawnMind mind, Pawn other, string key, string label)
        {
            string name = other.LabelShort;
            var otherMind = MindManager.Instance?.MindOf(other);
            if (otherMind?.persona != null)
                return otherMind.Invite(mind.pawn, key, label) ? $"Asked {name} along ({label}); waiting for the answer." : $"{name} is answering someone else.";
            Job job = other.CurJob;
            if (job != null && (job.playerForced || !job.def.casualInterruptible))
                return $"{name} is busy ({other.GetJobReport()?.TrimEnd('.')}).";
            return Start(mind.pawn, other, key) is string why ? why : $"Spending time with {name}: {label}.";
        }

        /// <summary>Starts it if it's still possible now: null once both are on their way, else why not.</summary>
        public static string Start(Pawn asker, Pawn other, string key)
        {
            var activity = For(asker, other).FirstOrDefault(x => x.key == key);
            if (activity == null)
                return "it can't happen right now";
            string why = activity.start(asker, other);
            if (why != null)
                return why;
            Remember(asker, other, activity);
            Remember(other, asker, activity);
            return null;
        }

        private static void Remember(Pawn pawn, Pawn other, Activity activity) =>
            MindManager.Instance?.MindOf(pawn)?.memory.Record(pawn, "together", activity.key, $"Spent time with {other.LabelShort}: {activity.label}.", 3,
                MemoryEvent.TookPart, new[] { other.LabelShort });

        private static bool Free(Pawn p) => p.Spawned && p.Awake() && !p.Downed && !p.Drafted && !p.InMentalState && p.GetLord() == null;

        private static bool WantsJoy(Pawn p) => p.needs?.joy != null && p.needs.joy.CurLevelPercentage <= MaxJoy;

        /// <summary>A modded joy giver or worker that throws is left out, like a modded interaction (ActionCatalog.Interactions).</summary>
        private static void Try(string defName, Func<Activity> find, List<Activity> list)
        {
            try
            {
                if (find() is Activity activity)
                    list.Add(activity);
            }
            catch (Exception e)
            {
                ModLog.Warning($"Together: {defName} threw: {e.Message}");
            }
        }

        /// <summary>A joy thing vanilla lets more than one use at once (joyMaxParticipants), the nearest both can use now.</summary>
        private static Activity SharedThing(JoyGiverDef def, Pawn a, Pawn b)
        {
            if (!(def.Worker is JoyGiver_InteractBuilding giver) || def.jobDef.joyMaxParticipants < 2 || def.thingDefs == null
                || !giver.CanBeGivenTo(a) || !giver.CanBeGivenTo(b))
                return null;
            Thing thing = def.thingDefs.SelectMany(t => a.Map.listerThings.ThingsOfDef(t))
                .Where(t => Usable(giver, a, t) && Usable(giver, b, t))
                .OrderBy(t => t.Position.DistanceToSquared(a.Position) + t.Position.DistanceToSquared(b.Position))
                .FirstOrDefault();
            if (thing == null)
                return null;
            return new Activity
            {
                key = def.defName,
                label = Report(def.jobDef, thing),
                start = (x, y) => Both(x, y, p => (Job)TryGivePlayJob.Invoke(giver, new object[] { p, thing })),
            };
        }

        private static bool Usable(JoyGiver_InteractBuilding giver, Pawn p, Thing t) =>
            p.CanReach(t, PathEndMode.Touch, Danger.None) && (bool)CanInteractWith.Invoke(giver, new object[] { p, t, false });

        /// <summary>A joy vanilla gives at a gathering (relaxing socially, a drink), around the nearest gathering spot that gives both one.</summary>
        private static Activity AtGatheringSpot(JoyGiverDef def, Pawn a, Pawn b)
        {
            var giver = def.Worker;
            if (!giver.CanBeGivenTo(a) || !giver.CanBeGivenTo(b))
                return null;
            var spots = a.Map.gatherSpotLister.activeSpots.Select(s => s.parent)
                .OrderBy(t => t.Position.DistanceToSquared(a.Position) + t.Position.DistanceToSquared(b.Position));
            foreach (var spot in spots)
            {
                IntVec3 cell = spot.Position;
                Job first = giver.TryGiveJobInGatheringArea(a, cell);
                if (first == null || giver.TryGiveJobInGatheringArea(b, cell) == null)
                    continue;
                return new Activity
                {
                    key = def.defName,
                    label = Report(def.jobDef, first.targetA),
                    start = (x, y) => Both(x, y, p => giver.TryGiveJobInGatheringArea(p, cell)),
                };
            }
            return null;
        }

        /// <summary>Romance on the Rim's romantic interaction for lovers, when its worker says it can run now (it starts its own lord).</summary>
        private static Activity Romantic(Def def, Pawn a, Pawn b)
        {
            object worker = Traverse.Create(def).Property("Worker").GetValue();
            if (!Run(worker, "CanExecute", a, b))
                return null;
            return new Activity
            {
                key = def.defName,
                label = def.label.Substring(def.label.IndexOf('-') + 1), // its labels carry the mod's name first: "RomanceOnTheRim-Stroll Together"
                start = (x, y) => Run(worker, "CanExecute", x, y) && Run(worker, "TryExecute", x, y) ? null : "it didn't work out",
            };
        }

        private static bool Run(object worker, string method, Pawn x, Pawn y) =>
            (bool)AccessTools.Method(RomanticWorker, method).Invoke(worker, new object[] { x, y });

        /// <summary>Vanilla's own words for the job: "playing chess", "consuming beer".</summary>
        private static string Report(JobDef job, LocalTargetInfo target) => JobUtility.GetResolvedJobReport(job.reportString, target).TrimEnd('.');

        /// <summary>The asker's job first (it takes its seat), then the other's; if the other gets none, the asker's ends too.</summary>
        private static string Both(Pawn x, Pawn y, Func<Pawn, Job> make)
        {
            Job first = make(x);
            if (first == null || !Order(x, first))
                return $"{x.LabelShort} couldn't start it";
            Job second = make(y);
            if (second == null || !Order(y, second))
            {
                x.jobs.EndCurrentJob(JobCondition.InterruptForced);
                return $"{y.LabelShort} couldn't join";
            }
            return null;
        }

        private static bool Order(Pawn p, Job job) =>
            MindManager.Instance?.MindOf(p) is PawnMind mind ? MindActions.Order(mind, job) : p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
    }
}
