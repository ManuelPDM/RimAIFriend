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
        // ---------- Act ----------

        /// <summary>Every AI order goes through here, from the main-thread pump (never OnGUI, see the Shift-queue trap).</summary>
        internal static bool Order(PawnMind mind, Job job)
        {
            mind.RememberOurJob(job);
            return mind.pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        /// <summary>
        /// "talk to someone" (STREAMLINE.md §4): she picks the person and the tone, vanilla's weights pick the interaction
        /// (chitchat or deep talk; slight or insult). A negative tone with nothing negative possible for the pair (the Kind
        /// trait) falls back to a friendly one.
        /// </summary>
        public static string Talk(PawnMind mind, Pawn target, bool positive, string line)
        {
            var all = ActionCatalog.Interactions(mind.pawn, target, mind)
                .Where(x => !ActionCatalog.LifeChangingInteractions.Contains(x.def.defName)).ToList();
            var pick = all.Where(x => ActionCatalog.IsNegative(x.def) != positive).ToList();
            string note = "";
            if (pick.Count == 0 && !positive)
            {
                pick = all.Where(x => !ActionCatalog.IsNegative(x.def)).ToList();
                note = " I couldn't bring myself to be unkind, so I kept it friendly.";
            }
            if (!pick.TryRandomElementByWeight(x => x.weight, out var chosen))
                return $"I've talked with {target.LabelShort} a lot lately; nothing more to say for now.";
            return TalkTo(mind, target, chosen.def, line) + note;
        }

        /// <param name="line">Her opening line, shown instead of vanilla's text when the interaction fires. May be null.</param>
        public static string TalkTo(PawnMind mind, Pawn target, InteractionDef interaction, string line)
        {
            if (!target.Spawned || target.Map != mind.pawn.Map || target.Downed || !target.Awake())
                return $"Couldn't talk to {target.LabelShort}: they're not available.";
            if (mind.OnCooldown(interaction, target))
                return $"I've done \"{interaction.label}\" too recently.";
            Job job = JobMaker.MakeJob(AIPC_JobDefOf.AIPC_TalkTo, target);
            job.interaction = interaction;
            mind.SetTalkLine(job, line);
            return Order(mind, job) ? $"Going over to {target.LabelShort} ({interaction.label})." : $"Couldn't go talk to {target.LabelShort}.";
        }

        // ---------- Work priorities (STREAMLINE.md §8) ----------

        /// <summary>
        /// Once per mind: a major passion's work first (1), a minor one's next (2), the rest 3. Work she can't do, work the
        /// player turned off, and the protected types stay as they are. Only with manual priorities on: there's nothing to rank otherwise.
        /// </summary>
        public static string SetPrioritiesFromPassions(Pawn pawn)
        {
            if (!ActionCatalog.ManualPriorities)
                return "Manual priorities are off, so my work stays as it is.";
            int changed = 0;
            foreach (var w in ActionCatalog.PlannableWorkTypes(pawn))
            {
                if (pawn.workSettings.GetPriority(w) == 0)
                    continue;
                var passion = w.relevantSkills.Select(s => pawn.skills?.GetSkill(s)).Where(s => s != null && !s.TotallyDisabled)
                    .Select(s => s.passion).DefaultIfEmpty(Passion.None).Max();
                int priority = passion == Passion.Major ? 1 : passion == Passion.Minor ? 2 : 3;
                if (pawn.workSettings.GetPriority(w) == priority)
                    continue;
                pawn.workSettings.SetPriority(w, priority);
                changed++;
            }
            return $"Set my work by my passions ({changed} changed): {ActionCatalog.DescribePriorities(pawn)}.";
        }

        public static readonly string[] Feelings = { "love", "dislike", "refuse", "raise", "lower" };

        /// <summary>How she feels about the work (shown in [Me]); raise and lower are one step for the colony's need (FURNISHING.md §6).</summary>
        public static bool IsFeeling(string change) => change == "love" || change == "dislike" || change == "refuse";

        /// <summary>
        /// Reflect's one work change: love → first (1, or on), dislike → last (4), refuse → off. A refusal is ignored when
        /// nobody else here could do that work. Returns the result line, or why it didn't change.
        /// </summary>
        public static string ApplyWorkFeeling(Pawn pawn, WorkTypeDef work, string feeling)
        {
            if (pawn.WorkTypeIsDisabled(work) || ActionCatalog.ProtectedWorkTypes.Contains(work.defName))
                return $"I can't change how I do {work.labelShort}.";
            bool manual = ActionCatalog.ManualPriorities;
            switch (feeling)
            {
                case "love":
                    pawn.workSettings.SetPriority(work, 1);
                    return $"I'll put {work.labelShort} first.";
                case "dislike":
                    if (!manual)
                        return $"I'll keep doing {work.labelShort}, grudgingly.";
                    pawn.workSettings.SetPriority(work, 4);
                    return $"I'll do {work.labelShort} last.";
                case "refuse":
                    bool others = pawn.Map != null && pawn.Map.mapPawns.FreeColonistsSpawned
                        .Any(p => p != pawn && p.workSettings != null && !p.WorkTypeIsDisabled(work) && p.workSettings.GetPriority(work) > 0);
                    if (!others)
                        return $"I'd rather not do {work.labelShort}, but nobody else can, so I'll keep at it.";
                    pawn.workSettings.SetPriority(work, 0);
                    return $"I won't do {work.labelShort} anymore.";
                case "raise":
                {
                    int p = pawn.workSettings.GetPriority(work);
                    int to = !manual ? 3 : p == 0 ? 4 : Math.Max(1, p - 1);
                    if (to == p || (!manual && p > 0))
                        return $"I already do {work.labelShort} as much as I can.";
                    pawn.workSettings.SetPriority(work, to);
                    return p == 0 ? $"I'll take on {work.labelShort}." : $"I'll do more {work.labelShort}.";
                }
                case "lower":
                {
                    int p = pawn.workSettings.GetPriority(work);
                    if (!manual || p == 0 || p == 4)
                        return $"I'll keep {work.labelShort} where it is.";
                    pawn.workSettings.SetPriority(work, p + 1);
                    return $"I'll do less {work.labelShort}.";
                }
                default:
                    return null;
            }
        }
    }
}
