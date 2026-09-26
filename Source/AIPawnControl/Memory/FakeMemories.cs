using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Dev "Fake a year of memories" (PHASE3.md §8, §10 item 11): a RimWorld year's worth of store (60 days × 5 memories,
    /// 60 diary entries) with random vectors, for the size and speed test. Five pairs share an event and nearly the same
    /// vector, so variety picking can be checked: a pair must never show up together.
    /// </summary>
    public static class FakeMemories
    {
        private static readonly string[] What =
        {
            "hauled steel in the rain", "argued about the stew", "watched the stars", "patched a leaking roof", "lost a game of horseshoes",
            "sat by the fire", "cleaned up after a raid", "planted rice", "talked about home", "heard wolves at night",
        };

        public static string Fill(PawnMind mind)
        {
            var memory = mind.memory;
            var rand = new Random(1);
            int now = Find.TickManager.TicksGame;
            string tag = memory.memories.Select(m => m.vectorTag).FirstOrDefault(t => t != null)
                         ?? Embedding.Tag(AIPawnControlMod.Settings.embedModel, Embedding.CutDims);
            int dims = int.Parse(tag.Substring(tag.LastIndexOf('|') + 1));
            var people = mind.pawn.MapHeld.mapPawns.FreeColonists.Where(p => p != mind.pawn).Select(p => p.LabelShort).ToList();
            int fakeEvent = -1000000;
            for (int i = 0; i < 300; i++)
            {
                var vector = RandomVector(rand, dims);
                var m = Add(memory, $"(fake) Day {i / 5}: {What[rand.Next(What.Length)]} with {Pick(rand, people)}.", rand.Next(1, 10),
                    now - rand.Next(60 * GenDate.TicksPerDay), new List<int> { fakeEvent-- }, vector, tag, people.Count > 0 ? new List<string> { Pick(rand, people) } : new List<string>());
                if (i < 5) // a pair about the same event: important, recent, near-identical
                {
                    m.importance = 10;
                    m.tick = now - GenDate.TicksPerHour * (i + 1);
                    var twin = (float[])vector.Clone();
                    for (int d = 0; d < dims; d++)
                        twin[d] += (float)(rand.NextDouble() - 0.5) * 0.02f;
                    Add(memory, "(fake twin) " + m.text, 10, m.tick, new List<int>(m.events), twin, tag, new List<string>(m.people));
                }
            }
            for (int d = 0; d < 60; d++)
                memory.diary.Add(new DiaryEntry
                {
                    tick = now - d * GenDate.TicksPerDay,
                    text = "(fake) " + string.Join(" ", Enumerable.Repeat("A long day of work, worry and small joys in the colony.", 9)),
                    vector = Embedding.Pack(RandomVector(rand, dims)),
                    vectorTag = tag,
                });
            return $"Added 305 fake memories and 60 diary entries (tag {tag}); {memory.memories.Count} memories now.";
        }

        private static MemoryRecord Add(MindMemory memory, string text, int importance, int tick, List<int> events, float[] vector, string tag, List<string> people)
        {
            var m = new MemoryRecord
            {
                id = memory.NewMemoryId(), text = text, importance = importance, tick = tick, events = events, people = people,
                place = "outside", vector = Embedding.Pack(vector), vectorTag = tag,
            };
            memory.memories.Add(m);
            return m;
        }

        private static string Pick(Random rand, List<string> names) => names.Count > 0 ? names[rand.Next(names.Count)] : "someone";

        private static float[] RandomVector(Random rand, int dims)
        {
            var v = new float[dims];
            double sum = 0;
            for (int i = 0; i < dims; i++)
            {
                v[i] = (float)(rand.NextDouble() * 2 - 1);
                sum += v[i] * v[i];
            }
            float length = (float)Math.Sqrt(sum);
            for (int i = 0; i < dims; i++)
                v[i] /= length;
            return v;
        }
    }
}
