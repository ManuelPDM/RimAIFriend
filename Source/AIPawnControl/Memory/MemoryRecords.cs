using System.Collections.Generic;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// A curated memory in her voice (PHASE3.md §2). Only Reflect writes these, and each cites the raw events it came
    /// from. Its vector is tagged with the model that made it; vectors with another tag are never compared.
    /// </summary>
    public class MemoryRecord : IExposable
    {
        public int id;
        public string text;
        public int importance;
        public List<string> people = new List<string>();
        public string place;
        public int tick;         // when it happened (its earliest cited event); recency counts from here
        public List<int> events = new List<int>();
        public string vector;    // Embedding.Pack, or null until embedded
        public string vectorTag;
        public int usedCount;
        public int lastUsedTick = -1;
        public int lastShownTick = -1;
        public bool archived;

        private float[] unpacked;   // not saved: Vector unpacks once and again only when vector changes
        private string unpackedFrom;

        public float[] Vector
        {
            get
            {
                if (!ReferenceEquals(unpackedFrom, vector))
                {
                    unpacked = Embedding.Unpack(vector);
                    unpackedFrom = vector;
                }
                return unpacked;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref importance, "importance");
            Scribe_Collections.Look(ref people, "people", LookMode.Value);
            Scribe_Values.Look(ref place, "place");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Collections.Look(ref events, "events", LookMode.Value);
            Scribe_Values.Look(ref vector, "vector");
            Scribe_Values.Look(ref vectorTag, "vectorTag");
            Scribe_Values.Look(ref usedCount, "usedCount");
            Scribe_Values.Look(ref lastUsedTick, "lastUsedTick", -1);
            Scribe_Values.Look(ref lastShownTick, "lastShownTick", -1);
            Scribe_Values.Look(ref archived, "archived");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                people = people ?? new List<string>();
                events = events ?? new List<int>();
            }
        }
    }

    /// <summary>One diary entry per Reflect, in her voice.</summary>
    public class DiaryEntry : IExposable
    {
        public int tick;
        public string text;
        public string vector;
        public string vectorTag;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref vector, "vector");
            Scribe_Values.Look(ref vectorTag, "vectorTag");
        }
    }

    /// <summary>
    /// What she believes about one person (the player and "colony" included). Beliefs are facts with a source and a
    /// time span: Reflect ends a fact that stopped being true instead of deleting it (Scene 10).
    /// </summary>
    public class PersonFile : IExposable
    {
        public const string Player = "the player", Colony = "colony";
        public const int MaxFacts = 5;

        public string name;
        public string impression;
        public string threads;
        public List<Fact> facts = new List<Fact>();
        public List<int> toldThem = new List<int>(); // memory ids she has already told this person (set by code)

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref impression, "impression");
            Scribe_Values.Look(ref threads, "threads");
            Scribe_Collections.Look(ref facts, "facts", LookMode.Deep);
            Scribe_Collections.Look(ref toldThem, "toldThem", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                facts = facts ?? new List<Fact>();
                toldThem = toldThem ?? new List<int>();
            }
        }
    }

    public class Fact : IExposable
    {
        public string text;
        public string source;
        public int since;
        public int until = -1; // -1 = still true

        public bool Open => until < 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref source, "source");
            Scribe_Values.Look(ref since, "since");
            Scribe_Values.Look(ref until, "until", -1);
        }
    }

    /// <summary>Something she wants, carried across days: her own ("me") or a promise to the player ("promise").</summary>
    public class Goal : IExposable
    {
        public const string Mine = "me", Promise = "promise";

        public string text;
        public string why;
        public string source;
        public int since;

        public void ExposeData()
        {
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref why, "why");
            Scribe_Values.Look(ref source, "source");
            Scribe_Values.Look(ref since, "since");
        }
    }
}
