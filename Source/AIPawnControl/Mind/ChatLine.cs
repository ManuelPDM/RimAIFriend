using Verse;

namespace AIPawnControl
{
    /// <summary>One message in the Mind tab chat: from the player, from her, or a system notice (never sent to the model).</summary>
    public class ChatLine : IExposable
    {
        public enum From { Player, Mind, System }

        public From from;
        public string text;
        public int tick;

        public ChatLine() { }

        public ChatLine(From from, string text)
        {
            this.from = from;
            this.text = text;
            tick = Find.TickManager.TicksGame;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref tick, "tick");
        }
    }
}
