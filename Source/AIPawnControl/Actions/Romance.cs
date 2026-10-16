using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIPawnControl
{
    /// <summary>
    /// Romance between minds and the colony: a mind starts a romance, a proposal or a breakup only from its menu, and a
    /// mind that is asked out or proposed to answers with its own Answer call. Vanilla runs the interaction with that answer.
    /// </summary>
    public static class Romance
    {
        private static bool inRandom;
        private static bool? answer;

        private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> TrackerPawn =
            AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");

        /// <summary>Asking out and proposing: the ones the other pawn can turn down.</summary>
        public static bool IsAsk(InteractionDef def) => def == InteractionDefOf.RomanceAttempt || def == InteractionDefOf.MarriageProposal;

        /// <summary>Runs the interaction with the asked mind's answer as vanilla's roll.</summary>
        public static bool WithAnswer(bool accept, Func<bool> interact)
        {
            answer = accept;
            try
            {
                return interact();
            }
            finally
            {
                answer = null;
            }
        }

        internal static void StartRandom() => inRandom = true;
        internal static void EndRandom() => inRandom = false;

        /// <summary>Vanilla's random pick: never a romance, proposal or breakup started by a mind.</summary>
        internal static bool BlockedRandom(Pawn initiator) => inRandom && MindManager.Instance?.MindOf(initiator) != null;

        internal static float? ForcedChance => answer is bool accept ? (accept ? 1f : 0f) : (float?)null;

        /// <summary>
        /// TryInteractWith: an ask aimed at a mind waits for its answer. The asker stays with it in a talk job, which runs
        /// the interaction once the answer is in. False lets vanilla go on.
        /// </summary>
        internal static bool Hold(Pawn_InteractionsTracker tracker, Pawn recipient, InteractionDef def)
        {
            if (answer != null || !IsAsk(def) || !tracker.CanInteractNowWith(recipient, def))
                return false;
            var mind = MindManager.Instance?.MindOf(recipient);
            if (mind?.persona == null)
                return false;
            Pawn asker = TrackerPawn(tracker);
            Job cur = asker.CurJob;
            bool inOurTalk = cur?.def == AIPC_JobDefOf.AIPC_TalkTo && cur.targetA.Thing == recipient;
            if (!inOurTalk && (asker.Drafted || asker.InMentalState || asker.GetLord() != null))
                return true; // can't stay for the answer: the attempt doesn't happen
            mind.Ask(asker, def, SpeechLog.PendingLine(asker));
            if (!inOurTalk)
            {
                Job job = JobMaker.MakeJob(AIPC_JobDefOf.AIPC_TalkTo, recipient);
                job.interaction = def;
                asker.jobs.StartJob(job, JobCondition.InterruptForced);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn_InteractionsTracker), "TryInteractRandomly")]
    internal static class Patch_TryInteractRandomly
    {
        private static void Prefix() => Romance.StartRandom();

        private static Exception Finalizer(Exception __exception)
        {
            Romance.EndRandom();
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class Patch_LifeChangingWeight
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InteractionWorker_RomanceAttempt), nameof(InteractionWorker.RandomSelectionWeight));
            yield return AccessTools.Method(typeof(InteractionWorker_MarriageProposal), nameof(InteractionWorker.RandomSelectionWeight));
            yield return AccessTools.Method(typeof(InteractionWorker_Breakup), nameof(InteractionWorker.RandomSelectionWeight));
        }

        private static void Postfix(ref float __result, Pawn initiator)
        {
            if (Romance.BlockedRandom(initiator))
                __result = 0f;
        }
    }

    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    internal static class Patch_TryInteractWith
    {
        private static bool Prefix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, ref bool __result)
        {
            if (!Romance.Hold(__instance, recipient, intDef))
                return true;
            __result = true;
            return false;
        }
    }

    /// <summary>Last, so the mind's answer wins over other mods' changes to the odds (Romance on the Rim).</summary>
    [HarmonyPatch(typeof(InteractionWorker_RomanceAttempt), nameof(InteractionWorker_RomanceAttempt.SuccessChance))]
    internal static class Patch_RomanceSuccessChance
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref float __result)
        {
            if (Romance.ForcedChance is float chance)
                __result = chance;
        }
    }

    [HarmonyPatch(typeof(InteractionWorker_MarriageProposal), nameof(InteractionWorker_MarriageProposal.AcceptanceChance))]
    internal static class Patch_ProposalAcceptanceChance
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref float __result)
        {
            if (Romance.ForcedChance is float chance)
                __result = chance;
        }
    }
}
