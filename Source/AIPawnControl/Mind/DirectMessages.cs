using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>One line in a private thread, and which of the two wrote it.</summary>
    public class DmLine : IExposable
    {
        public int id;
        public int tick;
        public bool fromA;
        public string text;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref fromA, "fromA");
            Scribe_Values.Look(ref text, "text");
        }
    }

    /// <summary>The private thread between two minds: a summary of what was folded away, the lines since, and how far each has read.</summary>
    public class DmThread : IExposable
    {
        public Pawn a, b;
        public List<DmLine> lines = new List<DmLine>();
        public string summary;
        public int summaryTick = -1;
        public int readA, readB; // the last line id each has read
        public int nextId = 1;

        public bool Has(Pawn p) => p == a || p == b;
        public Pawn Other(Pawn p) => p == a ? b : a;
        public Pawn Author(DmLine l) => l.fromA ? a : b;
        public int Read(Pawn p) => p == a ? readA : readB;
        public int LastId => nextId - 1;
        public List<DmLine> UnreadBy(Pawn p) => lines.Where(l => l.id > Read(p) && Author(l) != p).ToList();
        public int LastTick => lines.Count > 0 ? lines[lines.Count - 1].tick : summaryTick;

        public void MarkRead(Pawn p, int upTo)
        {
            if (p == a) readA = System.Math.Max(readA, upTo);
            else readB = System.Math.Max(readB, upTo);
        }

        /// <summary>"Today 09:12 Me: …" or "Today 09:12 Linds: …".</summary>
        public string Line(DmLine l, Pawn reader, Map map) =>
            (map != null ? $"{GameTime.DayLabel(l.tick, map)} {GameTime.Clock(l.tick, map)} " : "")
            + (Author(l) == reader ? "Me" : Author(l).LabelShort) + ": " + l.text;

        public void ExposeData()
        {
            Scribe_References.Look(ref a, "a");
            Scribe_References.Look(ref b, "b");
            Scribe_Collections.Look(ref lines, "lines", LookMode.Deep);
            Scribe_Values.Look(ref summary, "summary");
            Scribe_Values.Look(ref summaryTick, "summaryTick", -1);
            Scribe_Values.Look(ref readA, "readA");
            Scribe_Values.Look(ref readB, "readB");
            Scribe_Values.Look(ref nextId, "nextId", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                lines = lines ?? new List<DmLine>();
        }
    }

    /// <summary>
    /// What minds say to each other, near or far: one private thread per pair. Every snapshot shows the last 2 lines of each
    /// thread active since yesterday ([Messages]); only the Message call loads a whole thread. At 15 lines the Compact call
    /// folds all but the last 5 into the thread's summary, as the group chat does.
    /// </summary>
    public class DirectMessages : GameComponent
    {
        private const int Shown = 2;
        private const int KeptAfterCompact = 5;
        private const int CompactAt = 15;

        private List<DmThread> threads = new List<DmThread>();
        private readonly Dictionary<DmThread, LlmRequest> compacting = new Dictionary<DmThread, LlmRequest>(); // not saved

        private static DirectMessages instance;
        private readonly Game game;

        public static DirectMessages Instance => instance != null && instance.game == Current.Game ? instance : null;

        public DirectMessages(Game game)
        {
            this.game = game;
            instance = this;
        }

        public DmThread Thread(Pawn x, Pawn y, bool create)
        {
            var thread = threads.FirstOrDefault(t => t.Has(x) && t.Has(y));
            if (thread == null && create)
            {
                thread = new DmThread { a = x, b = y };
                threads.Add(thread);
            }
            return thread;
        }

        public IEnumerable<DmThread> ThreadsOf(Pawn p) => threads.Where(t => t.Has(p) && t.lines.Count + (t.summary != null ? 1 : 0) > 0)
            .OrderByDescending(t => t.LastTick);

        /// <summary>Adds a line from one of the two; the sender has read everything up to it.</summary>
        public DmThread Add(Pawn from, Pawn to, string text)
        {
            var thread = Thread(from, to, create: true);
            thread.lines.Add(new DmLine { id = thread.nextId++, tick = Find.TickManager.TicksGame, fromA = from == thread.a, text = text });
            thread.MarkRead(from, thread.LastId);
            ModLog.Message($"DM {from.LabelShort} → {to.LabelShort}: {text}");
            MaybeCompact(thread);
            return thread;
        }

        /// <summary>
        /// The thread this pawn answers at once: unread, from someone in vanilla's interaction range (a talk face to face),
        /// the oldest unread first. Null if none: unread lines from farther away wait for the pawn's next Act.
        /// </summary>
        public DmThread Unanswered(Pawn p) => threads
            .Where(t => t.Has(p) && t.UnreadBy(p).Count > 0 && MindActions.InRange(p, t.Other(p)))
            .OrderBy(t => t.UnreadBy(p)[0].tick).FirstOrDefault();

        /// <summary>
        /// [Messages]: each thread active since yesterday or unread, newest first, with its unread count and last 2 lines.
        /// The Mind tab chat with the colonist the player plays is one of them, like any other thread. The Message call
        /// leaves out its own thread, which it shows whole.
        /// </summary>
        public string Context(Pawn p, Map map, Pawn except = null)
        {
            int from = Find.TickManager.TicksGame - GenDate.TicksPerDay;
            var entries = ThreadsOf(p).Where(t => !t.Has(except) && t.lines.Count > 0 && (t.LastTick >= from || t.UnreadBy(p).Count > 0))
                .Select(t => (tick: t.LastTick, text: Entry(t.Other(p), t.UnreadBy(p).Count, t.lines.Skip(t.lines.Count - Shown).Select(l => t.Line(l, p, map)))))
                .ToList();
            Pawn played = MindManager.Instance?.PlayingAs;
            var chat = MindManager.Instance?.MindOf(p)?.chat.Where(l => l.from != ChatLine.From.System).ToList();
            if (played != null && played != except && chat?.Count > 0)
            {
                int answered = chat.FindLastIndex(l => l.from == ChatLine.From.Mind);
                var last = chat[chat.Count - 1];
                if (last.tick >= from || answered < chat.Count - 1)
                    entries.Add((last.tick, Entry(played, chat.Count - 1 - answered, chat.Skip(chat.Count - Shown).Select(l =>
                        (map != null ? $"{GameTime.DayLabel(l.tick, map)} {GameTime.Clock(l.tick, map)} " : "") + (l.from == ChatLine.From.Mind ? "Me" : played.LabelShort) + ": " + l.text))));
            }
            return entries.Count > 0 ? string.Join("\n", entries.OrderByDescending(e => e.tick).Select(e => e.text)) : null;
        }

        private static string Entry(Pawn other, int unread, IEnumerable<string> lines) =>
            $"{other.LabelShort}{(unread > 0 ? $" ({unread} unread)" : "")}: {string.Join(" · ", lines)}";

        /// <summary>The thread as the reader has read it, for the Message call: the summary, then every line but the unread ones.</summary>
        public static string Full(DmThread thread, Pawn reader, Map map)
        {
            var lines = new List<string>();
            if (thread?.summary != null)
                lines.Add($"Earlier (up to {GameTime.DayLabel(thread.summaryTick, map).ToLower()}): {thread.summary}");
            if (thread != null)
                lines.AddRange(thread.lines.Except(thread.UnreadBy(reader)).Select(l => thread.Line(l, reader, map)));
            return lines.Count > 0 ? string.Join("\n", lines) : "(this is the start of the conversation)";
        }

        // ---------- The Compact call ----------

        private void MaybeCompact(DmThread thread)
        {
            if (thread.lines.Count >= CompactAt && !compacting.ContainsKey(thread))
                Compact(thread);
        }

        /// <summary>Folds every line but the last 5 into a new summary, which replaces the old one.</summary>
        public void Compact(DmThread thread)
        {
            if (compacting.ContainsKey(thread) || thread.lines.Count <= KeptAfterCompact)
                return;
            var folded = thread.lines.Take(thread.lines.Count - KeptAfterCompact).ToList();
            Map map = Find.AnyPlayerHomeMap;
            string prompt = Prompts.Fill("compact-dm", new Dictionary<string, string>
            {
                ["a"] = thread.a.LabelShort,
                ["b"] = thread.b.LabelShort,
                ["summary"] = thread.summary ?? "(none yet)",
                ["messages"] = string.Join("\n", folded.Select(l => thread.Line(l, null, map))),
                ["now"] = GroupChat.Now(map),
            });
            var schema = Schema.Obj(new Dictionary<string, object> { ["summary"] = Schema.Str(900) });
            LlmRequest request = null;
            request = LlmClient.Send("compact", new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("user", prompt) }, schema, result =>
            {
                if (!compacting.TryGetValue(thread, out var mine) || mine != request || Current.Game != game)
                    return;
                compacting.Remove(thread);
                string text = null;
                if (result.Ok)
                {
                    try
                    {
                        text = ((Dictionary<string, object>)Json.Parse(result.Content)).Str("summary")?.Trim();
                    }
                    catch (System.Exception e)
                    {
                        ModLog.Warning("Messages compact: the reply wasn't valid JSON: " + e.Message);
                    }
                }
                else
                    ModLog.Warning("Messages compact failed: " + result.Error);
                if (string.IsNullOrEmpty(text))
                    return; // the lines stay; the next line or the hourly check tries again
                thread.summary = text;
                thread.summaryTick = folded[folded.Count - 1].tick;
                thread.lines.RemoveAll(folded.Contains);
                ModLog.Message($"Messages {thread.a.LabelShort} ↔ {thread.b.LabelShort} compacted {folded.Count} lines: {text}");
            });
            compacting[thread] = request;
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % GenDate.TicksPerHour == 0)
                foreach (var thread in threads)
                    MaybeCompact(thread); // after a failed call, or a save loaded at 15
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref threads, "threads", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                threads = threads ?? new List<DmThread>();
                threads.RemoveAll(t => t.a == null || t.b == null);
            }
        }
    }
}
