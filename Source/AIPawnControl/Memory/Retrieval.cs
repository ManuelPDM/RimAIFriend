using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Which memories reach a prompt (PHASE3.md §6): score = relevance + importance + recency + person/place match −
    /// reuse penalty, each part 0..1, then variety picking and a minimum score. Main thread; a few hundred vectors take
    /// well under a millisecond. The thresholds and weights live here, to be tuned with "Show retrieval".
    /// </summary>
    public static class Retrieval
    {
        public const float MinOnMind = 2.3f;      // [On my mind] in Act: she wasn't asked, so only strong matches (recency and importance alone give about 1.8; a week-old memory of someone present scores about 2.4, Scene 9)
        public const float MinRemember = 2.0f;    // [I remember] in Chat: the player asked about something
        public const int OnMindGapTicks = 4 * GenDate.TicksPerHour;
        private const int UsedRestTicks = 3 * GenDate.TicksPerDay;
        private const int ShownPenaltyTicks = 6 * GenDate.TicksPerHour;
        private const float ShownPenalty = 0.3f;
        private const float ToldPenalty = 1.0f;
        private const float RecencyDays = 14f;
        private const float SameEventCosine = 0.85f;
        private const string Outside = "outside"; // SnapshotBuilder.RoomLabel's word for no room: not a place worth matching

        public class Scored
        {
            public MemoryRecord memory;
            public float relevance, importance, recency, match, penalty;
            public float[] vector;
            public float Total => relevance + importance + recency + match - penalty;

            public override string ToString() =>
                $"{Total:0.00} = rel {relevance:0.00} + imp {importance:0.00} + rec {recency:0.00} + match {match:0.00} - pen {penalty:0.00} · M{memory.id}: {memory.text}";
        }

        /// <param name="query">The situation's vector, or null (embeddings off or down: people, place, importance and recency only).</param>
        /// <param name="present">People in the situation (present, talked to, or mentioned).</param>
        /// <param name="audience">Who she'd be telling: memories already told to them get the heavy penalty.</param>
        public static List<Scored> Rank(MindMemory memory, float[] query, string tag, ICollection<string> present, string place, ICollection<string> audience)
        {
            int now = Find.TickManager.TicksGame;
            string model = AIPawnControlMod.Settings.embedModel;
            var told = new HashSet<int>(audience.Select(n => memory.File(n, create: false)).Where(f => f != null).SelectMany(f => f.toldThem));
            var ranked = new List<Scored>();
            foreach (var m in memory.memories)
            {
                if (m.archived || m.tick > now || (m.lastUsedTick >= 0 && now - m.lastUsedTick < UsedRestTicks))
                    continue;
                var s = new Scored { memory = m };
                if (query != null && m.vector != null && m.vectorTag == tag)
                {
                    s.vector = m.Vector;
                    s.relevance = Embedding.Relevance(model, Embedding.Cosine(query, s.vector));
                }
                s.importance = m.importance / 10f;
                s.recency = (float)Math.Exp(-(now - m.tick) / (float)GenDate.TicksPerDay / RecencyDays);
                s.match = (m.people.Any(present.Contains) ? 0.7f : 0f) + (!string.IsNullOrEmpty(place) && place != Outside && m.place == place ? 0.3f : 0f);
                if (m.lastShownTick >= 0 && now - m.lastShownTick < ShownPenaltyTicks)
                    s.penalty += ShownPenalty;
                if (told.Contains(m.id))
                    s.penalty += ToldPenalty;
                ranked.Add(s);
            }
            return ranked.OrderByDescending(s => s.Total).ToList();
        }

        /// <summary>
        /// Picks the best ones above the minimum, one at a time, skipping any that's about the same event as one already
        /// picked (a shared cited event, or cosine above 0.85). Showing nothing is normal.
        /// </summary>
        public static List<Scored> Pick(List<Scored> ranked, int max, float min)
        {
            var picked = new List<Scored>();
            foreach (var s in ranked)
            {
                if (picked.Count >= max || s.Total < min)
                    break;
                bool same = picked.Any(p => p.memory.events.Intersect(s.memory.events).Any()
                    || (p.vector != null && s.vector != null && Embedding.Cosine(p.vector, s.vector) > SameEventCosine));
                if (!same)
                    picked.Add(s);
            }
            return picked;
        }

        /// <summary>"[About Sheet] impression · threads" lines for the people who have a file, at most max, each cut to lineCap.</summary>
        public static string About(PawnMind mind, IEnumerable<string> names, int max, int lineCap)
        {
            var lines = names.Distinct().Select(n => mind.memory.File(n, create: false))
                .Where(f => f != null && !string.IsNullOrEmpty(f.impression)).Take(max)
                .Select(f =>
                {
                    string text = f.impression + (string.IsNullOrEmpty(f.threads) ? "" : " · " + f.threads);
                    if (text.Length > lineCap)
                    {
                        int cut = text.LastIndexOf(' ', lineCap - 1);
                        text = text.Substring(0, cut > 0 ? cut : lineCap - 1) + "…";
                    }
                    string who = People.Label(f.name, mind.pawn);
                    return $"[About {f.name}] {(who != null ? $"({who}) " : "")}{text}";
                }).ToList();
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }

        /// <summary>The picked memories as "M12 (3 days ago) text" lines, and marks them shown (not when it is only a look).</summary>
        public static string Describe(List<Scored> picked, bool markShown = true)
        {
            int now = Find.TickManager.TicksGame;
            if (markShown)
                foreach (var s in picked)
                    s.memory.lastShownTick = now;
            return string.Join("\n", picked.Select(s => $"M{s.memory.id} ({Ago(now - s.memory.tick)}) {s.memory.text}"));
        }

        /// <summary>Diary entries this relevant or more may be quoted: a clear match, not just the same colony's words.</summary>
        public const float MinDiaryRelevance = 0.5f;

        /// <summary>
        /// "[From my diary]" (PHASE3.md §12: the diary is searchable): the entry that best matches the query, older than a
        /// day ([Since yesterday] has the last one), or null. Needs a query vector: a diary entry has no people or place to match.
        /// </summary>
        public static string Diary(MindMemory memory, float[] query, string tag)
        {
            if (query == null)
                return null;
            int now = Find.TickManager.TicksGame;
            string model = AIPawnControlMod.Settings.embedModel;
            var best = memory.diary
                .Where(d => d.vector != null && d.vectorTag == tag && now - d.tick > GenDate.TicksPerDay)
                .Select(d => (entry: d, relevance: Embedding.Relevance(model, Embedding.Cosine(query, Embedding.Unpack(d.vector)))))
                .Where(x => x.relevance >= MinDiaryRelevance)
                .OrderByDescending(x => x.relevance)
                .FirstOrDefault();
            return best.entry != null ? $"({Ago(now - best.entry.tick)}) {best.entry.text}" : null;
        }

        private static string Ago(int ticks)
        {
            float days = ticks / (float)GenDate.TicksPerDay;
            return days < 1f ? "today" : days < 2f ? "yesterday" : $"{(int)days} days ago";
        }

        /// <summary>What she already brought up today (PawnDiary's anti-repetition): used memories rest 3 days, but she should know she said them.</summary>
        public static string BroughtUpToday(MindMemory memory)
        {
            int now = Find.TickManager.TicksGame;
            var used = memory.memories.Where(m => m.lastUsedTick >= 0 && now - m.lastUsedTick < GenDate.TicksPerDay).Select(m => m.text).ToList();
            return used.Count > 0 ? "Already brought up today, don't repeat: " + string.Join(" · ", used) : null;
        }
    }
}
