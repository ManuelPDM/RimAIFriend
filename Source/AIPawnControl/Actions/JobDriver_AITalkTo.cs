using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    [DefOf]
    public static class AIPC_JobDefOf
    {
        public static JobDef AIPC_TalkTo;

        static AIPC_JobDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(AIPC_JobDefOf));
    }

    /// <summary>
    /// Walk into interaction range of TargetA, then start job.interaction with vanilla TryInteractWith
    /// (thoughts, opinion, social log entry, Bubbles bubble all come from vanilla).
    /// Retries a few times if the shared 120-tick interaction cooldown is taken, e.g. by vanilla random chatter.
    /// </summary>
    public class JobDriver_AITalkTo : JobDriver
    {
        private const int MaxAttempts = 3;
        private const int RetryIntervalTicks = 90;
        private const int GiveUpTicks = 1200; // ~20 s at 1x: walking plus waiting

        private int attempts;
        private int talkStartTick = -1;
        private int nextTryTick;

        private Pawn Target => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref attempts, "attempts");
            Scribe_Values.Look(ref talkStartTick, "talkStartTick", -1);
            Scribe_Values.Look(ref nextTryTick, "nextTryTick");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);

            Toil gotoTarget = Toils_Interpersonal.GotoInteractablePosition(TargetIndex.A);
            gotoTarget.AddPreInitAction(() =>
            {
                if (talkStartTick < 0)
                    talkStartTick = Find.TickManager.TicksGame;
            });
            gotoTarget.AddPreTickIntervalAction(_ => CheckGiveUp());
            yield return gotoTarget;

            Toil talk = ToilMaker.MakeToil("AITalk");
            talk.tickIntervalAction = _ =>
            {
                if (CheckGiveUp() || Find.TickManager.TicksGame < nextTryTick)
                    return;
                if (!SocialInteractionUtility.IsGoodPositionForInteraction(pawn, Target))
                {
                    JumpToToil(gotoTarget); // they walked off: follow
                    return;
                }
                string line = MindManager.Instance?.MindOf(pawn)?.TalkLine(job);
                if (SpeechLog.WithLine(pawn, line, () => pawn.interactions.TryInteractWith(Target, job.interaction)))
                {
                    Report(true, null);
                    EndJobWith(JobCondition.Succeeded);
                    return;
                }
                attempts++;
                nextTryTick = Find.TickManager.TicksGame + RetryIntervalTicks;
                if (attempts >= MaxAttempts)
                {
                    Report(false, Target.Awake() ? "they weren't able to talk right now" : "they fell asleep");
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            talk.socialMode = RandomSocialMode.Off;
            talk.defaultCompleteMode = ToilCompleteMode.Never;
            yield return talk;
        }

        private bool CheckGiveUp()
        {
            if (talkStartTick < 0 || Find.TickManager.TicksGame - talkStartTick < GiveUpTicks)
                return false;
            Report(false, "I couldn't get to them in time");
            EndJobWith(JobCondition.Incompletable);
            return true;
        }

        private void Report(bool success, string failure)
        {
            var mind = MindManager.Instance?.MindOf(pawn);
            mind?.OnTalkFinished(Target, job.interaction, success, failure, mind.TalkLine(job));
        }
    }
}
