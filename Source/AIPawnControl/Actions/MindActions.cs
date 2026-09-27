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

        public static readonly string[] Priorities = { "1", "2", "3", "4", "off" };

        /// <summary>
        /// Reflect's one work change (FURNISHING.md §6): set one kind of work straight to 1-4 or off, for what the colony
        /// needs or how she feels about it. The value it already has is a no-op, not an error. Returns the result line.
        /// </summary>
        public static string ChangePriority(Pawn pawn, WorkTypeDef work, string priority)
        {
            if (pawn.WorkTypeIsDisabled(work) || ActionCatalog.ProtectedWorkTypes.Contains(work.defName))
                return $"I can't change how I do {work.labelShort}.";
            int p = pawn.workSettings.GetPriority(work);
            int to = priority == "off" ? 0 : int.Parse(priority);
            if (!ActionCatalog.ManualPriorities && to > 0)
                to = 3; // without manual priorities work is only on (3) or off
            if ((to > 0) == (p > 0) && (to == p || !ActionCatalog.ManualPriorities))
                return $"I'll keep {work.labelShort} where it is.";
            pawn.workSettings.SetPriority(work, to);
            return to == 0 ? $"I'll stop doing {work.labelShort}."
                : p == 0 ? $"I'll take on {work.labelShort} ({to})."
                : to < p ? $"I'll do more {work.labelShort} ({to})." : $"I'll do less {work.labelShort} ({to}).";
        }
    }
}
