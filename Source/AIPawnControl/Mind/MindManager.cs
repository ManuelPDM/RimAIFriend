using System.Collections.Generic;
using Verse;

namespace AIPawnControl
{
    /// <summary>Owns every PawnMind, runs their trigger checks, and saves them with the game.</summary>
    public class MindManager : GameComponent
    {
        private const int CheckInterval = 250;

        private List<PawnMind> minds = new List<PawnMind>();

        public static MindManager Instance => Current.Game?.GetComponent<MindManager>();

        public MindManager(Game game) { }

        public PawnMind MindOf(Pawn pawn)
        {
            foreach (var mind in minds)
                if (mind.pawn == pawn)
                    return mind;
            return null;
        }

        public void Enable(Pawn pawn, string note)
        {
            if (MindOf(pawn) != null)
                return;
            minds.Add(new PawnMind(pawn, note));
            ModLog.Message($"Mind enabled for {pawn.LabelShort}.");
        }

        public void Disable(Pawn pawn)
        {
            var mind = MindOf(pawn);
            if (mind == null)
                return;
            mind.Cancel();
            minds.Remove(mind);
            ModLog.Message($"Mind disabled for {pawn.LabelShort}.");
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 0)
                return;
            foreach (var mind in minds)
                mind.CheckTriggers();
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref minds, "minds", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                minds = minds ?? new List<PawnMind>();
                minds.RemoveAll(m => m.pawn == null);
            }
        }
    }
}
