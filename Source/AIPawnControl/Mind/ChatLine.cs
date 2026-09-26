using Verse;

namespace AIPawnControl
{
    /// <summary>One message in the Mind tab chat: from the player, from her, or a system notice (never sent to the model).</summary>
    public class ChatLine : IExposable
    {
        public enum From { Player, Mind, System }

        public From from;
        public string text;

        public ChatLine() { }

        public ChatLine(From from, string text)
        {
            this.from = from;
            this.text = text;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref text, "text");
        }
    }
}
