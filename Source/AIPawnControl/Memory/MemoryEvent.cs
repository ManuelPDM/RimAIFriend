using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// One thing that happened, from her point of view (PHASE3.md §2-3). Raw scratch input for Reflect and
    /// [Since yesterday]: kept 7 days, never embedded. Repeats within 2 hours merge into one event with a count.
    /// </summary>
    public class MemoryEvent : IExposable
    {
        public const string TookPart = "took_part", Saw = "saw", News = "news", Told = "told", PlayerSaid = "player_said";

        public int id;
        public int tick;
        public int lastTick;
        public string kind; // thought, talk, letter, hurt, break, relation, tale, decision, chat
        public string def;  // the thought, interaction, letter or tale def: merging and the first-time bonus go by kind + def
        public string text;
        public List<string> people = new List<string>();
        public string place;
        public string source;
        public int importance;
        public int count = 1;

        public string Text => count > 1 ? $"{text} ×{count}" : text;

        /// <summary>"day 12, 14:05" in the map's local time.</summary>
        public string When(Map map) => $"day {GenDate.DaysPassedAt(tick) + 1}, {Clock(map)}";

        /// <summary>"14:05" in the map's local time.</summary>
        public string Clock(Map map)
        {
            float longitude = map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
            float hour = GenDate.HourFloat(GenDate.TickGameToAbs(tick), longitude);
            return $"{(int)hour:00}:{(int)(hour % 1f * 60f):00}";
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref lastTick, "lastTick");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref def, "def");
            Scribe_Values.Look(ref text, "text");
            Scribe_Collections.Look(ref people, "people", LookMode.Value);
            Scribe_Values.Look(ref place, "place");
            Scribe_Values.Look(ref source, "source");
            Scribe_Values.Look(ref importance, "importance");
            Scribe_Values.Look(ref count, "count", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                people = people ?? new List<string>();
        }
    }
}
