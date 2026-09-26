using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// One nightly Reflect (PHASE3.md §4): the only thing that writes memories. Prepare picks the events, the closest
    /// existing memories (for dedup) and the person files; Parse turns the reply into a guarded list of changes
    /// without touching memory; Commit applies them after the embeddings come back. A cancel between the steps
    /// loses nothing, because nothing is applied before Commit.
    /// </summary>
    public class Reflection
    {
        private const int MaxEvents = 40;
        private const int MaxShownMemories = 6;
        private const int MaxFiles = 8;
        private const float DuplicateCosine = 0.92f;
        private const int MaxReembed = 20;

        public const int MaxDiary = 600, MaxMemoryText = 200, MaxImpression = 240, MaxThreads = 160, MaxFactText = 120, MaxGoalText = 120, MaxWhy = 160;

        private readonly PawnMind mind;
        private readonly bool dev;
        private readonly List<MemoryEvent> events;
        private List<MemoryRecord> shown = new List<MemoryRecord>(); // M1..
        private readonly List<string> names;                         // allowed person-file names
        private readonly List<Goal> goalsShown;                      // G1..
        private readonly int seenEventId, seenTick;                  // what this Reflect covers, for the next one

        private MindMemory Memory => mind.memory;

        private Reflection(PawnMind mind, bool dev, List<MemoryEvent> events)
        {
            this.mind = mind;
            this.dev = dev;
            this.events = events;
            goalsShown = Memory.goals.ToList();
            seenEventId = Memory.LastEventId;
            seenTick = Find.TickManager.TicksGame;
            names = events.SelectMany(e => e.people)
                .Where(n => n != PersonFile.Player)
                .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key)
                .Take(MaxFiles).OrderBy(n => n)
                .Concat(new[] { PersonFile.Player, PersonFile.Colony }).ToList();
        }

        /// <summary>
        /// The events to reflect on: everything since the last Reflect (at night), or the last 24 hours ("Reflect now",
        /// so running it twice goes over the same day). Null when nothing happened.
        /// </summary>
        public static Reflection Prepare(PawnMind mind, bool dev)
        {
            var memory = mind.memory;
            int dayAgo = Find.TickManager.TicksGame - GenDate.TicksPerDay;
            var chosen = memory.events.Where(e => dev ? e.lastTick > dayAgo : e.id > memory.lastReflectEventId || e.lastTick > memory.lastReflectTick)
                .OrderByDescending(e => e.importance).ThenByDescending(e => e.lastTick)
                .Take(MaxEvents).OrderBy(e => e.tick).ToList();
            return chosen.Count > 0 ? new Reflection(mind, dev, chosen) : null;
        }

        public bool Dev => dev;

        /// <summary>A throwaway search query from the day's events, only used to find the closest memories.</summary>
        public string QueryText()
        {
            var top = events.OrderByDescending(e => e.importance).Take(8).Select(e => e.text);
            string text = string.Join(" ", top);
            return text.Length > 600 ? text.Substring(0, 600) : text;
        }

        /// <summary>The memories closest to today, by vector (when the tag matches) plus shared people and importance.</summary>
        public void PickClosest(float[] query, string tag)
        {
            var todayPeople = new HashSet<string>(events.SelectMany(e => e.people));
            shown = Memory.memories.Where(m => !m.archived)
                .Select(m =>
                {
                    float score = m.importance / 10f + (m.people.Any(todayPeople.Contains) ? 0.5f : 0f);
                    if (query != null && m.vectorTag == tag && m.vector != null)
                        score += 2f * Embedding.Cosine(query, m.Vector);
                    return (m, score);
                })
                .OrderByDescending(x => x.score).Take(MaxShownMemories).Select(x => x.m).ToList();
        }

        public bool HasMemoriesWithVectors => Memory.memories.Any(m => !m.archived && m.vector != null);

        public Dictionary<string, string> PromptValues()
        {
            Map map = mind.pawn.MapHeld;
            return new Dictionary<string, string>
            {
                ["period"] = events[0].tick < Find.TickManager.TicksGame - GenDate.TicksPerDay ? "the last few days" : "today",
                ["events"] = string.Join("\n", events.Select((e, i) =>
                {
                    string people = e.people.Count > 0 ? " · " + string.Join(", ", e.people) : "";
                    return $"E{i + 1} {e.When(map)} · {e.source}{people} · importance {e.importance}: {e.Text}";
                })),
                ["people"] = string.Join("\n", names.Select(Describe)),
                ["goals"] = goalsShown.Count > 0
                    ? string.Join("\n", goalsShown.Select((g, i) => $"G{i + 1} [{(g.source == Goal.Promise ? "promised the player" : "my own")}] {g.text}" + (string.IsNullOrEmpty(g.why) ? "" : $" (why: {g.why})")))
                    : "(none)",
                ["memories"] = shown.Count > 0
                    ? string.Join("\n", shown.Select((m, i) => $"M{i + 1} day {GenDate.DaysPassedAt(m.tick) + 1} · importance {m.importance}: {m.text}"))
                    : "(none yet)",
            };
        }

        private string Describe(string name)
        {
            var file = Memory.File(name, create: false);
            var sb = new List<string>();
            Pawn pawn = name == PersonFile.Player || name == PersonFile.Colony ? null
                : mind.pawn.MapHeld?.mapPawns.FreeColonists.FirstOrDefault(p => p.LabelShort == name);
            if (pawn != null)
            {
                string relation = mind.pawn.GetMostImportantRelation(pawn)?.GetGenderSpecificLabel(pawn) ?? "colonist";
                sb.Add($"now: {relation}, my opinion {mind.pawn.relations.OpinionOf(pawn):+0;-0;0}");
            }
            if (file == null)
                sb.Add("no file yet");
            else
            {
                if (!string.IsNullOrEmpty(file.impression)) sb.Add("impression: " + file.impression);
                if (!string.IsNullOrEmpty(file.threads)) sb.Add("threads: " + file.threads);
                var open = file.facts.Where(f => f.Open).ToList();
                for (int i = 0; i < open.Count; i++)
                    sb.Add($"F{i + 1} {open[i].text} ({open[i].source}, since day {GenDate.DaysPassedAt(open[i].since) + 1})");
            }
            return $"[{name}] " + string.Join("; ", sb);
        }

        public Dictionary<string, object> Schema()
        {
            Dictionary<string, object> Str(int max) => new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = max + max / 5 };
            Dictionary<string, object> Enum(IEnumerable<object> values) => new Dictionary<string, object> { ["enum"] = values.ToList() };
            Dictionary<string, object> Obj(Dictionary<string, object> props) => new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = props.Keys.Cast<object>().ToList(),
                ["additionalProperties"] = false,
            };
            Dictionary<string, object> Arr(object items, int max) => new Dictionary<string, object> { ["type"] = "array", ["items"] = items, ["maxItems"] = max };
            IEnumerable<object> Numbers(int count) => Enumerable.Range(0, count + 1).Cast<object>();

            var memory = Obj(new Dictionary<string, object>
            {
                ["op"] = Enum(new object[] { "add", "update" }),
                ["id"] = Enum(Numbers(shown.Count)),
                ["text"] = Str(MaxMemoryText),
                ["importance"] = Enum(Enumerable.Range(1, 10).Cast<object>()),
                ["events"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = Enum(Enumerable.Range(1, events.Count).Cast<object>()),
                    ["minItems"] = 1,
                    ["maxItems"] = 8,
                },
            });
            var fact = Obj(new Dictionary<string, object>
            {
                ["op"] = Enum(new object[] { "add", "end" }),
                ["id"] = Enum(Numbers(PersonFile.MaxFacts)),
                ["text"] = Str(MaxFactText),
                ["source"] = Enum(new object[] { MemoryEvent.TookPart, MemoryEvent.Saw, MemoryEvent.News, MemoryEvent.Told, MemoryEvent.PlayerSaid }),
            });
            var person = Obj(new Dictionary<string, object>
            {
                ["name"] = Enum(names),
                ["impression"] = Str(MaxImpression),
                ["threads"] = Str(MaxThreads),
                ["facts"] = Arr(fact, 3),
            });
            var goal = Obj(new Dictionary<string, object>
            {
                ["op"] = Enum(new object[] { "add", "done", "drop" }),
                ["id"] = Enum(Numbers(goalsShown.Count)),
                ["text"] = Str(MaxGoalText),
                ["why"] = Str(MaxWhy),
            });
            return Obj(new Dictionary<string, object>
            {
                ["diary"] = Str(MaxDiary),
                ["lately"] = Str(MindMemory.MaxLately),
                ["memories"] = Arr(memory, 8),
                ["people"] = Arr(person, 6),
                ["goals"] = Arr(goal, 3),
            });
        }

        // ---------- The reply, as changes not yet applied ----------

        private class Draft
        {
            public MemoryRecord target; // null = a new memory
            public string text;
            public int importance;
            public List<int> eventIds;
            public float[] vector;
        }

        private readonly List<Draft> drafts = new List<Draft>();
        private readonly List<(string name, Dictionary<string, object> item)> personOps = new List<(string, Dictionary<string, object>)>();
        private readonly List<(string op, int id, string text, string why)> goalOps = new List<(string, int, string, string)>();
        private string diary, newLately;
        private int dropped, skipped;

        private static readonly Regex NumberList = new Regex(@"\s*\([EMFG]\d+(\s*,\s*[EMFG]\d+)*\)");

        private static string Clean(object raw, int max) => SpeechLog.Clean(raw is string s ? NumberList.Replace(s, "") : null, max); // "(E4, M1)" belongs in "events"/"id"

        private static int Int(Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) && v is double n ? (int)n : 0;

        private static List<Dictionary<string, object>> List(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) && v is List<object> list ? list.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();

        /// <summary>
        /// Reads the reply with the code-side guards: a memory needs at least one real event (no evidence, no memory),
        /// an update must name a memory that was shown, a new memory can't just retell the events of one it already has,
        /// and importance can't sink more than 2 below its events' highest.
        /// </summary>
        public void Parse(Dictionary<string, object> reply)
        {
            diary = Clean(reply.TryGetValue("diary", out object d) ? d : null, MaxDiary);
            newLately = Clean(reply.TryGetValue("lately", out object l) ? l : null, MindMemory.MaxLately);
            foreach (var m in List(reply, "memories"))
            {
                var ids = (m.TryGetValue("events", out object e) && e is List<object> list ? list : new List<object>())
                    .OfType<double>().Select(n => (int)n - 1).Where(i => i >= 0 && i < events.Count).Distinct()
                    .Select(i => events[i].id).ToList();
                string text = Clean(m.TryGetValue("text", out object t) ? t : null, MaxMemoryText);
                int shownIndex = Int(m, "id") - 1;
                bool update = (m.TryGetValue("op", out object op) ? op as string : null) == "update";
                if (ids.Count == 0 || text == null || (update && (shownIndex < 0 || shownIndex >= shown.Count)))
                {
                    dropped++;
                    continue;
                }
                if (!update && Memory.memories.Any(mem => !mem.archived && ids.All(mem.events.Contains)))
                {
                    skipped++; // every event it cites is already in one memory: a retelling, not a new memory
                    continue;
                }
                int prior = events.Where(ev => ids.Contains(ev.id)).Max(ev => ev.importance);
                drafts.Add(new Draft
                {
                    target = update ? shown[shownIndex] : null,
                    text = text,
                    importance = Math.Max(Math.Max(1, Math.Min(10, Int(m, "importance"))), prior - 2),
                    eventIds = ids,
                });
            }
            foreach (var p in List(reply, "people"))
                if (p.TryGetValue("name", out object n) && n is string name && names.Contains(name))
                    personOps.Add((name, p));
            foreach (var g in List(reply, "goals"))
                goalOps.Add((g.TryGetValue("op", out object op) ? op as string : null, Int(g, "id"),
                    Clean(g.TryGetValue("text", out object t) ? t : null, MaxGoalText), Clean(g.TryGetValue("why", out object w) ? w : null, MaxWhy)));
        }

        /// <summary>Texts to embed after the call: the drafts, the diary, then older memories without a current vector.</summary>
        public List<string> TextsToEmbed(out List<MemoryRecord> reembed)
        {
            string model = AIPawnControlMod.Settings.embedModel;
            var draftTargets = new HashSet<MemoryRecord>(drafts.Where(x => x.target != null).Select(x => x.target));
            reembed = Memory.memories
                .Where(m => !m.archived && !draftTargets.Contains(m) && (m.vector == null || m.vectorTag == null || !m.vectorTag.StartsWith(model + "|")))
                .Take(MaxReembed).ToList();
            var texts = drafts.Select(x => x.text).ToList();
            if (diary != null)
                texts.Add(diary);
            texts.AddRange(reembed.Select(m => m.text));
            return texts;
        }

        /// <summary>Applies everything. With vectors, a new memory within cosine 0.92 of one about the same people updates that one instead.</summary>
        public string Commit(EmbedResult embedded, List<MemoryRecord> reembed, int night)
        {
            int now = Find.TickManager.TicksGame;
            bool haveVectors = embedded != null && embedded.Ok;
            if (haveVectors)
                for (int i = 0; i < drafts.Count; i++)
                    drafts[i].vector = embedded.Vectors[i];
            int merged = 0;
            foreach (var draft in drafts)
            {
                var cited = events.Where(e => draft.eventIds.Contains(e.id)).ToList();
                var people = new HashSet<string>(cited.SelectMany(e => e.people));
                if (draft.target == null && draft.vector != null)
                {
                    var twin = Memory.memories.FirstOrDefault(m => !m.archived && m.vectorTag == embedded.Tag && m.vector != null
                        && new HashSet<string>(m.people).SetEquals(people) && Embedding.Cosine(draft.vector, m.Vector) >= DuplicateCosine);
                    if (twin != null)
                    {
                        draft.target = twin;
                        merged++;
                    }
                }
                var memory = draft.target;
                if (memory == null)
                {
                    memory = new MemoryRecord { id = Memory.NewMemoryId(), tick = cited.Min(e => e.tick) };
                    Memory.memories.Add(memory);
                }
                memory.text = draft.text;
                memory.importance = draft.importance;
                memory.tick = Math.Min(memory.tick, cited.Min(e => e.tick));
                memory.events = memory.events.Union(draft.eventIds).ToList();
                memory.people = memory.people.Union(people).OrderBy(n => n).ToList();
                memory.place = cited.Select(e => e.place).Where(p => !string.IsNullOrEmpty(p)).GroupBy(p => p)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? memory.place;
                memory.vector = draft.vector != null ? Embedding.Pack(draft.vector) : null;
                memory.vectorTag = draft.vector != null ? embedded.Tag : null;
            }

            if (diary != null)
            {
                var entry = new DiaryEntry { tick = now, text = diary };
                if (haveVectors)
                {
                    entry.vector = Embedding.Pack(embedded.Vectors[drafts.Count]);
                    entry.vectorTag = embedded.Tag;
                }
                Memory.diary.Add(entry);
            }
            if (haveVectors)
            {
                int offset = drafts.Count + (diary != null ? 1 : 0);
                for (int i = 0; i < reembed.Count; i++)
                {
                    reembed[i].vector = Embedding.Pack(embedded.Vectors[offset + i]);
                    reembed[i].vectorTag = embedded.Tag;
                }
            }
            if (newLately != null)
                Memory.lately = newLately;

            foreach (var (name, item) in personOps)
                ApplyPerson(Memory.File(name, create: true), item, now);
            foreach (var (op, id, text, why) in goalOps)
            {
                if ((op == "done" || op == "drop") && id >= 1 && id <= goalsShown.Count && Memory.goals.Remove(goalsShown[id - 1]))
                    mind.AddDecision((op == "done" ? "Done: " : "Gave up on: ") + goalsShown[id - 1].text, importance: 0);
            }
            foreach (var (op, id, text, why) in goalOps) // after retiring, so a new goal doesn't push out one that was just finished
            {
                if (op == "add" && text != null)
                    Memory.AddGoal(text, why, Goal.Mine);
            }

            Memory.MarkReflected(seenEventId, seenTick);
            int archived = Memory.Archive(now);
            if (!dev)
                Memory.lastReflectNight = night;
            return $"{events.Count} events → {drafts.Count - merged} new/updated memories ({merged} merged as near-duplicates, {skipped} skipped as already remembered, {dropped} dropped by the guards), " +
                   $"{personOps.Count} person files, {goalOps.Count} goal changes, {archived} archived, vectors: {(haveVectors ? embedded.Tag : "none (" + (embedded?.Error ?? "skipped") + ")")}";
        }

        private static void ApplyPerson(PersonFile file, Dictionary<string, object> item, int now)
        {
            string impression = Clean(item.TryGetValue("impression", out object i) ? i : null, MaxImpression);
            if (impression != null)
                file.impression = impression;
            if (item.TryGetValue("threads", out object t) && t is string threads)
                file.threads = Clean(threads, MaxThreads) ?? "";
            var open = file.facts.Where(f => f.Open).ToList();
            foreach (var fact in List(item, "facts"))
            {
                string op = fact.TryGetValue("op", out object o) ? o as string : null;
                int id = Int(fact, "id");
                if (op == "end" && id >= 1 && id <= open.Count)
                    open[id - 1].until = now;
                else if (op == "add")
                {
                    string text = Clean(fact.TryGetValue("text", out object x) ? x : null, MaxFactText);
                    if (text != null)
                        file.facts.Add(new Fact { text = text, source = fact.TryGetValue("source", out object s) ? s as string : MemoryEvent.Told, since = now });
                }
            }
            while (file.facts.Count > PersonFile.MaxFacts)
                file.facts.Remove(file.facts.FirstOrDefault(f => !f.Open) ?? file.facts[0]);
        }
    }
}
