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
        /// <summary>Runs one picked action; if it throws, logs it and returns "That went wrong: …" as its result line.</summary>
        public static string Safely(Pawn pawn, string label, Func<string> apply)
        {
            try
            {
                return apply();
            }
            catch (Exception e)
            {
                ModLog.Error($"{pawn.LabelShort}: applying \"{label}\" threw: {e}");
                return "That went wrong: " + e.Message;
            }
        }

        // ---------- Act ----------

        /// <summary>Every AI order goes through here, from the main-thread pump (never OnGUI, see the Shift-queue trap).</summary>
        internal static bool Order(PawnMind mind, Job job, bool queue = false)
        {
            mind.RememberOurJob(job);
            return mind.pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: queue);
        }

        /// <summary>
        /// Words to someone in vanilla's interaction range are said out loud: vanilla's weights pick the interaction for the
        /// tone (chitchat or deep talk; slight or insult), which carries the line with its thoughts, opinion and bubble. A
        /// negative tone with nothing negative possible for the pair (the Kind trait) falls back to a friendly one; with every
        /// interaction on cooldown it's just a bubble. Returns what was said out loud, or null when they're out of range.
        /// </summary>
        public static string SayTo(PawnMind mind, Pawn target, bool positive, string line)
        {
            Pawn pawn = mind.pawn;
            if (!InRange(pawn, target))
                return null;
            var all = ActionCatalog.Interactions(pawn, target, mind).Where(x => !ActionCatalog.LifeChangingInteractions.Contains(x.def.defName)).ToList();
            var pick = all.Where(x => ActionCatalog.IsNegative(x.def) != positive).ToList();
            if (pick.Count == 0)
                pick = all.Where(x => !ActionCatalog.IsNegative(x.def)).ToList();
            if (pick.TryRandomElementByWeight(x => x.weight, out var chosen) && SpeechLog.WithLine(pawn, line, () => pawn.interactions.TryInteractWith(target, chosen.def)))
            {
                mind.Interacted(target, chosen.def);
                return chosen.def.label;
            }
            if (AIPawnControlMod.Settings.speakLines)
                SpeechLog.Say(pawn, line);
            return "out loud";
        }

        /// <summary>Close enough to talk face to face: vanilla's own test for an interaction between the two.</summary>
        public static bool InRange(Pawn pawn, Pawn other) =>
            pawn.Spawned && other.Spawned && pawn.Map == other.Map && SocialInteractionUtility.IsGoodPositionForInteraction(pawn, other);

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

        // ---------- Danger (DANGER_RESPONSE.md) ----------

        /// <summary>"go inside": the Inside area and the Flee response; vanilla walks her in (JobGiver_SeekAllowedArea) and keeps her there.</summary>
        public static string GoInside(PawnMind mind)
        {
            var area = DangerResponse.Inside(mind.pawn.Map);
            if (area == null)
                return "There's nowhere inside to go.";
            mind.KeepSettings();
            mind.pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            mind.pawn.playerSettings.hostilityResponse = HostilityResponseMode.Flee;
            return "Going inside until the danger is over.";
        }

        /// <summary>
        /// "fight" and "help": no area and the Attack response, then an ordered job: a melee attack on the enemy, or a
        /// walk to where she can shoot it from. Once there, vanilla's Attack response does the shooting.
        /// </summary>
        public static string Fight(PawnMind mind, Thing enemy)
        {
            Pawn pawn = mind.pawn;
            mind.KeepSettings();
            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
            pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
            Verb verb = pawn.TryGetAttackVerb(enemy);
            Job job;
            if (verb == null || verb.IsMeleeAttack)
                job = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
            else if (DangerPlan.Holding(pawn))
                return $"Shooting at the {DangerResponse.Label(enemy)} from my spot."; // her spot's wait shoots what's in range
            else if (DangerResponse.TryShootingPosition(pawn, enemy, verb, out IntVec3 dest))
                job = JobMaker.MakeJob(JobDefOf.Goto, dest);
            else
                return "I'll fight, but I found no spot to shoot from; I'll take on whatever comes close.";
            return Order(mind, job) ? $"Going after the {DangerResponse.Label(enemy)}." : $"Couldn't go after the {DangerResponse.Label(enemy)}; I'll take on whatever comes close.";
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
        public static string ChangePriority(Pawn pawn, WorkTypeDef work, string priority, out bool changed)
        {
            changed = false;
            if (pawn.WorkTypeIsDisabled(work) || ActionCatalog.ProtectedWorkTypes.Contains(work.defName))
                return $"I can't change how I do {work.labelShort}.";
            int p = pawn.workSettings.GetPriority(work);
            int to = priority == "off" ? 0 : int.Parse(priority);
            if (!ActionCatalog.ManualPriorities && to > 0)
                to = 3; // without manual priorities work is only on (3) or off
            if ((to > 0) == (p > 0) && (to == p || !ActionCatalog.ManualPriorities))
                return $"I'll keep {work.labelShort} where it is.";
            pawn.workSettings.SetPriority(work, to);
            changed = true;
            return to == 0 ? $"I'll stop doing {work.labelShort}."
                : p == 0 ? $"I'll take on {work.labelShort} ({to})."
                : to < p ? $"I'll do more {work.labelShort} ({to})." : $"I'll do less {work.labelShort} ({to}).";
        }
    }
}
