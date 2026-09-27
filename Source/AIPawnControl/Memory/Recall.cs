using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The memory sections of one Act, Chat or Plan prompt (PHASE3.md §6 budgets), and what she used of them. The
    /// situation's query is embedded in the background first; everything else runs on the main thread.
    /// </summary>
    public class Recall
    {
        private const float PresentRange = 12f;

        public readonly Dictionary<string, string> Sections = new Dictionary<string, string>();
        public readonly List<int> Shown = new List<int>(); // memory ids the "memory" field may name

        private static bool Enabled => AIPawnControlMod.Settings.memoryEnabled;

        /// <summary>Other colonists within 12 cells, nearest first.</summary>
        public static List<Pawn> Present(Pawn pawn) => pawn.Map.mapPawns.FreeColonistsSpawned
            .Where(p => p != pawn && p.Position.InHorDistOf(pawn.Position, PresentRange))
            .OrderBy(p => p.Position.DistanceToSquared(pawn.Position)).ToList();

        /// <summary>The situation, not the prompt: who's here and how they are, what's happening, where, when.</summary>
        public static string ActQuery(Pawn pawn, List<Pawn> present)
        {
            string State(Pawn p) => p.Downed ? " (downed)" : p.InMentalState ? $" ({p.MentalStateDef.label})"
                : p.health.hediffSet.hediffs.Any(h => h.Visible && h.def.isBad && h is Hediff_Injury) ? " (hurt)" : "";
            var parts = new List<string>();
            if (present.Count > 0)
                parts.Add("With " + string.Join(", ", present.Take(4).Select(p => p.LabelShort + State(p))));
            if (State(pawn) != "")
                parts.Add("I'm" + State(pawn));
            parts.Add((pawn.GetJobReport() ?? "").TrimEnd('.'));
            int hour = GenLocalDate.HourOfDay(pawn.Map);
            parts.Add($"{SnapshotBuilder.RoomLabel(pawn)}, {(hour < 6 ? "night" : hour < 12 ? "morning" : hour < 18 ? "afternoon" : "evening")}");
            return string.Join(". ", parts.Where(p => p.Length > 0));
        }

        /// <summary>The player's words, plus the people they name.</summary>
        public static string ChatQuery(PawnMind mind, string message, out List<string> mentioned)
        {
            mentioned = Mentioned(mind, message);
            return mentioned.Count > 0 ? $"{message} ({string.Join(", ", mentioned)})" : message;
        }

        private static List<string> Mentioned(PawnMind mind, string message)
        {
            var names = mind.pawn.MapHeld.mapPawns.FreeColonists.Where(p => p != mind.pawn).Select(p => p.LabelShort)
                .Concat(mind.memory.files.Select(f => f.name)).Where(n => n != PersonFile.Player && n != PersonFile.Colony).Distinct();
            return names.Where(n => Regex.IsMatch(message, $@"\b{Regex.Escape(n)}\b", RegexOptions.IgnoreCase)).ToList();
        }

        /// <summary>
        /// Embeds the query when there's anything to compare it with (and Memory is on), then calls back on the main
        /// thread. Without embeddings the callback gets null and retrieval uses people, place, importance and recency.
        /// </summary>
        public static void WithQuery(PawnMind mind, string query, Action<float[], string> then)
        {
            if (!Enabled || !mind.memory.memories.Any(m => m.vector != null && !m.archived))
            {
                then(null, null);
                return;
            }
            Game game = Current.Game;
            EmbedClient.Embed(new List<string> { query }, query: true, result =>
            {
                if (Current.Game == game)
                    then(result.Ok ? result.Vectors[0] : null, result.Tag);
            });
        }

        /// <summary>Act: [About X] for up to 3 people present, and [On my mind]: at most 2 strong matches, at most once every 4 hours.</summary>
        public static Recall Act(PawnMind mind, List<Pawn> present, float[] query, string tag)
        {
            var recall = new Recall();
            if (!Enabled)
                return recall;
            var memory = mind.memory;
            var names = present.Select(p => p.LabelShort).ToList();
            recall.Sections["About"] = Retrieval.About(memory, names, 3, 110);
            int now = Find.TickManager.TicksGame;
            if (now - memory.lastOnMindTick < Retrieval.OnMindGapTicks)
                return recall;
            var picked = Retrieval.Pick(Retrieval.Rank(memory, query, tag, names, SnapshotBuilder.RoomLabel(mind.pawn), names), 2, Retrieval.MinOnMind);
            if (picked.Count == 0)
                return recall;
            memory.lastOnMindTick = now;
            recall.Shown.AddRange(picked.Select(s => s.memory.id));
            recall.Sections["On my mind"] = "(this came back to you; bring it up only if it fits, don't force it)\n" + Retrieval.Describe(picked)
                                            + Line(Retrieval.BroughtUpToday(memory));
            return recall;
        }

        /// <summary>Reply (PHASE6.md §4): the speaker's file and up to 3 memories matched to what they said, as [I remember].</summary>
        public static Recall Reply(PawnMind mind, string speaker, float[] query, string tag)
        {
            var recall = new Recall();
            if (!Enabled)
                return recall;
            var memory = mind.memory;
            recall.Sections["About"] = Retrieval.About(memory, new[] { speaker }, 1, 400);
            var picked = Retrieval.Pick(Retrieval.Rank(memory, query, tag, new List<string> { speaker }, null, new[] { speaker }), 3, Retrieval.MinRemember);
            if (picked.Count == 0)
                return recall;
            recall.Shown.AddRange(picked.Select(s => s.memory.id));
            recall.Sections["I remember"] = Retrieval.Describe(picked) + Line(Retrieval.BroughtUpToday(memory));
            return recall;
        }

        /// <summary>Chat: the player's file and the files of anyone they mention, and up to 5 memories as [I remember].</summary>
        public static Recall Chat(PawnMind mind, List<string> mentioned, float[] query, string tag)
        {
            var recall = new Recall();
            if (!Enabled)
                return recall;
            var memory = mind.memory;
            recall.Sections["About"] = Retrieval.About(memory, new[] { PersonFile.Player }.Concat(mentioned), 4, 400);
            var people = mentioned.Concat(new[] { PersonFile.Player }).ToList();
            var picked = Retrieval.Pick(Retrieval.Rank(memory, query, tag, people, null, new[] { PersonFile.Player }), 5, Retrieval.MinRemember);
            if (picked.Count == 0)
                return recall;
            recall.Shown.AddRange(picked.Select(s => s.memory.id));
            recall.Sections["I remember"] = Retrieval.Describe(picked) + Line(Retrieval.BroughtUpToday(memory));
            return recall;
        }

        /// <summary>
        /// Plan: [About X] for the people in today's events, and the player's file first when it has open threads, so a
        /// conversation from yesterday reaches the new day (PHASE6.md §2.2). Her lately and goals are in the system prompt.
        /// </summary>
        public static Recall Plan(PawnMind mind)
        {
            var recall = new Recall();
            if (!Enabled || mind.pawn.Map == null)
                return recall;
            int dayStart = Find.TickManager.TicksGame - GenLocalDate.DayTick(mind.pawn.Map);
            var names = mind.memory.events.Where(e => e.lastTick >= dayStart).SelectMany(e => e.people)
                .Where(n => n != PersonFile.Player && n != PersonFile.Colony);
            if (!string.IsNullOrEmpty(mind.memory.File(PersonFile.Player, create: false)?.threads))
                names = new[] { PersonFile.Player }.Concat(names);
            recall.Sections["About"] = Retrieval.About(mind.memory, names, 3, 110);
            return recall;
        }

        /// <summary>The "memory" field named one she was shown: it rests 3 days, and the person she told now knows it.</summary>
        public void MarkUsed(PawnMind mind, int memoryId, string audience)
        {
            var used = Shown.Contains(memoryId) ? mind.memory.memories.FirstOrDefault(m => m.id == memoryId) : null;
            if (used == null)
                return;
            used.usedCount++;
            used.lastUsedTick = Find.TickManager.TicksGame;
            if (audience != null)
            {
                var file = mind.memory.File(audience, create: true);
                if (!file.toldThem.Contains(memoryId))
                    file.toldThem.Add(memoryId);
            }
            ModLog.Message($"{mind.pawn.LabelShort} drew on M{memoryId}{(audience != null ? " with " + audience : "")}: {used.text}");
        }

        private static string Line(string text) => text != null ? "\n" + text : "";
    }
}
