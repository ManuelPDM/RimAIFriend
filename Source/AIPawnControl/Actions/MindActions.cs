using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    /// <summary>The only code that changes the game on the AI's behalf. Validates everything; returns a readable result.</summary>
    public static class MindActions
    {
        // Loose safety net only; the real guidance is in Prompts/guidance.txt. (A model once planned 20 h of sleep.)
        public const int MinSleepHours = 4;
        public const int MaxSleepHours = 14;

        /// <summary>Applies a parsed Plan reply. Parts that fail validation are skipped and reported; the rest still apply.</summary>
        public static string ApplyPlan(Pawn pawn, Dictionary<string, object> plan)
        {
            var results = new List<string> { ApplySchedule(pawn, plan), ApplyPriorities(pawn, plan) };
            return string.Join(" ", results.Where(r => r != null));
        }

        // ---------- Act ----------

        /// <summary>Every AI order goes through here, from the main-thread pump (never OnGUI, see the Shift-queue trap).</summary>
        private static bool Order(PawnMind mind, Job job)
        {
            mind.RememberOurJob(job);
            return mind.pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        public static bool CanRestNow(Pawn pawn) =>
            pawn.needs?.rest != null && pawn.needs.rest.CurLevel < 0.9f
            && !RestUtility.TimetablePreventsLayDown(pawn) && !RestUtility.DisturbancePreventsLyingDown(pawn);

        public static string Rest(PawnMind mind)
        {
            Pawn pawn = mind.pawn;
            if (!CanRestNow(pawn))
                return "Couldn't rest: my schedule or surroundings don't allow it right now.";
            Building_Bed bed = RestUtility.FindBedFor(pawn);
            Job job = bed != null ? JobMaker.MakeJob(JobDefOf.LayDown, bed) : JobMaker.MakeJob(JobDefOf.LayDown, pawn.Position);
            return Order(mind, job) ? (bed != null ? "Resting in bed." : "Resting on the ground (no bed).") : "Couldn't start resting.";
        }

        public static string Recreation(PawnMind mind, JoyGiverDef def)
        {
            Job job = def.Worker.TryGiveJob(mind.pawn);
            if (job == null)
                return $"Couldn't do {ActionCatalog.JoyLabel(def)}: nothing available for it right now.";
            job.ignoreJoyTimeAssignment = true; // otherwise a Work hour ends it at once (JoyUtility.JoyTickCheckEnd)
            return Order(mind, job) ? $"Started {ActionCatalog.JoyLabel(def)}." : $"Couldn't start {ActionCatalog.JoyLabel(def)}.";
        }

        public static string TalkTo(PawnMind mind, Pawn target, InteractionDef interaction)
        {
            if (!target.Spawned || target.Map != mind.pawn.Map || target.Downed || !target.Awake())
                return $"Couldn't talk to {target.LabelShort}: they're not available.";
            if (mind.OnCooldown(interaction, target))
                return $"I've done \"{interaction.label}\" too recently.";
            Job job = JobMaker.MakeJob(AIPC_JobDefOf.AIPC_TalkTo, target);
            job.interaction = interaction;
            return Order(mind, job) ? $"Going over to {target.LabelShort}." : $"Couldn't go talk to {target.LabelShort}.";
        }

        public static string GoTo(PawnMind mind, Pawn target)
        {
            if (!target.Spawned || target.Map != mind.pawn.Map)
                return $"Couldn't go to {target.LabelShort}: they're not here.";
            return GoToCell(mind, target.Position, target.LabelShort);
        }

        public static string GoTo(PawnMind mind, Room room)
        {
            var cells = room.Cells.Where(c => c.Standable(mind.pawn.Map)).ToList();
            if (cells.Count == 0)
                return "Couldn't find a spot in that room.";
            return GoToCell(mind, cells.RandomElement(), "the " + room.GetRoomRoleLabel());
        }

        private static string GoToCell(PawnMind mind, IntVec3 cell, string label)
        {
            Pawn pawn = mind.pawn;
            IntVec3 dest = RCellFinder.BestOrderedGotoDestNear(cell, pawn);
            if (!dest.IsValid || !pawn.CanReach(dest, PathEndMode.OnCell, Danger.Deadly))
                return $"Couldn't find a way to {label}.";
            return Order(mind, JobMaker.MakeJob(JobDefOf.Goto, dest)) ? $"Heading to {label}." : $"Couldn't head to {label}.";
        }

        private static string ApplySchedule(Pawn pawn, Dictionary<string, object> plan)
        {
            if (pawn.timetable == null)
                return "I have no schedule to set.";
            if (!(plan.TryGetValue("schedule", out object raw) && raw is List<object> hours) || hours.Count != 24)
                return "Schedule rejected: it needs exactly 24 hours.";

            var options = ActionCatalog.ScheduleOptions();
            var defs = new TimeAssignmentDef[24];
            for (int hour = 0; hour < 24; hour++)
            {
                string option = hours[hour] as string;
                if (!options.Contains(option))
                    return $"Schedule rejected: \"{option}\" isn't a schedule option.";
                defs[hour] = ActionCatalog.ScheduleDef(option);
            }
            int sleepHours = defs.Count(d => d == TimeAssignmentDefOf.Sleep);
            if (sleepHours < MinSleepHours || sleepHours > MaxSleepHours)
                return $"Schedule rejected: {sleepHours} sleep hours, it has to be {MinSleepHours}-{MaxSleepHours}. Kept my old schedule.";

            for (int hour = 0; hour < 24; hour++)
                pawn.timetable.SetAssignment(hour, defs[hour]);
            return "Schedule set.";
        }

        private static string ApplyPriorities(Pawn pawn, Dictionary<string, object> plan)
        {
            var workTypes = ActionCatalog.PlannableWorkTypes(pawn);
            if (workTypes.Count == 0)
                return null;
            bool manual = ActionCatalog.ManualPriorities;
            int changed = 0;
            foreach (var w in workTypes)
            {
                if (!plan.TryGetValue(ActionCatalog.PriorityPrefix + w.defName, out object raw) || !(raw is double value))
                    continue; // left out: keep the current priority
                int priority = Math.Max(0, Math.Min(4, (int)value));
                if (!manual && priority > 0)
                    priority = 3; // with manual priorities off, the game only knows on (3) and off (0)
                if (pawn.workSettings.GetPriority(w) == priority)
                    continue; // (manual off: GetPriority reports 3 for any stored 1-4, so those stay untouched)
                pawn.workSettings.SetPriority(w, priority);
                changed++;
            }
            return changed > 0 ? $"Changed {changed} work priorities." : "Work priorities unchanged.";
        }
    }
}
