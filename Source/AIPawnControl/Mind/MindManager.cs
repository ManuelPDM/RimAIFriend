using System.Collections.Generic;
using Verse;

namespace AIPawnControl
{
    /// <summary>Owns every PawnMind, runs their trigger checks, and saves them with the game.</summary>
    public class MindManager : GameComponent
    {
        private const int CheckInterval = 250;

        private List<PawnMind> minds = new List<PawnMind>();
        private SpokenLines lines = new SpokenLines();
        private Pawn playingAs;

        private static MindManager instance;

        /// <summary>Cached, because the log text patch asks for it on every log draw.</summary>
        public static MindManager Instance => instance != null && instance.game == Current.Game ? instance : null;

        private readonly Game game;

        public SpokenLines Lines => lines;

        public List<PawnMind> Minds => minds;

        /// <summary>The colonist the player plays as (mod settings), or null. Never a mind; dead or gone, it's the player again.</summary>
        public Pawn PlayingAs
        {
            get => playingAs != null && !playingAs.Dead && playingAs.IsColonist && MindOf(playingAs) == null ? playingAs : null;
            set => playingAs = value;
        }

        /// <summary>Who the player's chat and group chat messages come from: the played colonist's name, or "the player".</summary>
        public static string Speaker => Instance?.PlayingAs?.LabelShort ?? PersonFile.Player;

        public MindManager(Game game)
        {
            this.game = game;
            instance = this;
        }

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
            mind.EndDanger();
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

        /// <summary>Runs every frame, also while paused, so the player can chat with the game paused.</summary>
        public override void GameComponentUpdate()
        {
            foreach (var mind in minds)
            {
                mind.UpdateChat();
                mind.UpdateReply();
                mind.UpdateAsked();
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref minds, "minds", LookMode.Deep);
            Scribe_Deep.Look(ref lines, "spokenLines");
            Scribe_References.Look(ref playingAs, "playingAs");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                minds = minds ?? new List<PawnMind>();
                minds.RemoveAll(m => m.pawn == null);
                lines = lines ?? new SpokenLines();
            }
        }

        public override void LoadedGame() => lines.Prune();
    }
}
