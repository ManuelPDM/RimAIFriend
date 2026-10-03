using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>One group chat message: who wrote it (a colonist's name, or the player) and when.</summary>
    public class GroupMessage : IExposable
    {
        public int tick;
        public string author;
        public string text;

        public GroupMessage() { }

        public GroupMessage(string author, string text)
        {
            tick = Find.TickManager.TicksGame;
            this.author = author;
            this.text = text;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref author, "author");
            Scribe_Values.Look(ref text, "text");
        }
    }

    /// <summary>
    /// The colony-wide group chat (GROUP_CHAT.md): the minds take turns, round robin. Only the mind whose turn it is gets
    /// "post in the group chat" in her Act menu; after her Act, whatever she picked, the turn passes on. The player's
    /// messages are just added. Every call that gets the snapshot sees the summary and the last 5 messages; at 15
    /// messages the Compact call folds all but the last 5 into the summary.
    /// </summary>
    public class GroupChat : GameComponent
    {
        public const string Player = "the player";
        public const int MaxPost = 400;
        private const int Shown = 5;
        private const int CompactAt = 15;

        // Saved
        private List<GroupMessage> messages = new List<GroupMessage>();
        private string summary;
        private int summaryTick = -1; // the newest message folded into it; -1 for summaries from before it was saved
        private Pawn turn;

        // Not saved
        private LlmRequest compacting;

        private static GroupChat instance;
        private readonly Game game;

        public static GroupChat Instance => instance != null && instance.game == Current.Game ? instance : null;

        public List<GroupMessage> Messages => messages;
        public string Summary => summary;
        public bool Compacting => compacting != null;

        public GroupChat(Game game)
        {
            this.game = game;
            instance = this;
        }

        private static List<PawnMind> Minds => MindManager.Instance?.Minds ?? new List<PawnMind>();

        // ---------- The turn ----------

        /// <summary>
        /// Whose turn it is. A holder who can't think now (asleep, downed, drafted, …: PawnMind.PausedReason) or has no
        /// mind any more hands it to the next mind who can; if nobody can, it stays.
        /// </summary>
        public PawnMind Holder()
        {
            var minds = Minds;
            if (minds.Count == 0)
                return null;
            var holder = minds.FirstOrDefault(m => m.pawn == turn);
            if (holder == null)
            {
                holder = minds.FirstOrDefault(m => m.PausedReason() == null) ?? minds[0];
                turn = holder.pawn;
            }
            else if (holder.PausedReason() != null)
                PassTurn(holder);
            return minds.FirstOrDefault(m => m.pawn == turn);
        }

        /// <summary>Who holds the turn, without handing it on (for the window, which mustn't change the game).</summary>
        public PawnMind TurnHolder => Minds.FirstOrDefault(m => m.pawn == turn) ?? Minds.FirstOrDefault();

        /// <summary>Her Act menu gets the line: it's her turn.</summary>
        public bool MayPost(PawnMind mind) => Holder() == mind;

        /// <summary>After her Act: the next mind in the list who can think now gets the turn. Not her turn: nothing happens.</summary>
        public void PassTurn(PawnMind from)
        {
            var minds = Minds;
            int at = minds.IndexOf(from);
            if (at < 0 || from.pawn != turn)
                return;
            for (int i = 1; i < minds.Count; i++)
            {
                var next = minds[(at + i) % minds.Count];
                if (next.PausedReason() == null)
                {
                    turn = next.pawn;
                    return;
                }
            }
        }

        // ---------- Messages ----------

        public void PlayerWrites(string text) => Add(Player, text);

        /// <summary>Adds a message, writes it into every mind's event log, and compacts at 15.</summary>
        public void Add(string author, string text)
        {
            messages.Add(new GroupMessage(author, text));
            foreach (var mind in Minds)
            {
                if (mind.pawn == null)
                    continue;
                if (author == mind.pawn.LabelShort)
                    mind.memory.Record(mind.pawn, "groupchat", null, $"I wrote in the group chat: \"{text}\"", 3, MemoryEvent.TookPart);
                else if (author == Player)
                    mind.memory.Record(mind.pawn, "groupchat", null, $"The player wrote in the group chat: \"{text}\"", 3, MemoryEvent.PlayerSaid, new[] { PersonFile.Player });
                else
                    mind.memory.Record(mind.pawn, "groupchat", null, $"{author} wrote in the group chat: \"{text}\"", 3, MemoryEvent.Told, new[] { author });
            }
            ModLog.Message($"Group chat, {author}: {text}");
            MaybeCompact();
        }

        /// <summary>[Group chat]: the summary, then the last 5 messages, oldest first. Null while it's empty.</summary>
        public string Context(Map map)
        {
            if (messages.Count == 0 && summary == null)
                return null;
            var lines = new List<string>();
            if (summary != null)
                lines.Add((summaryTick >= 0 && map != null ? $"Earlier (up to {GameTime.DayLabel(summaryTick, map).ToLower()}): " : "Earlier: ") + summary);
            lines.AddRange(messages.Skip(messages.Count - Shown).Select(m => Line(m, map)));
            return string.Join("\n", lines);
        }

        /// <summary>"Today 09:00 Dave: …".</summary>
        public static string Line(GroupMessage m, Map map) =>
            (map != null ? $"{GameTime.DayLabel(m.tick, map)} {GameTime.Clock(m.tick, map)} " : "") + $"{m.author}: {m.text}";

        // ---------- The Post call ----------

        /// <summary>The Act chose "post in the group chat": the Post call writes the message. Returns the result line for her decisions.</summary>
        public static string StartPost(PawnMind mind)
        {
            Pawn pawn = mind.pawn;
            Map map = pawn.Map;
            mind.Send("post", PostMessages(mind), PostSchema(), reply => OnPost(mind, reply),
                stillValid: () => pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map ? "gone" : null);
            return "Writing to the group chat.";
        }

        /// <summary>The Post call's prompt: the Act's whole snapshot, plus what she thinks of the people who wrote lately.</summary>
        internal static List<KeyValuePair<string, string>> PostMessages(PawnMind mind)
        {
            var chat = Instance;
            var readers = Minds.Where(m => m != mind && m.pawn != null).Select(m => m.pawn.LabelShort).ToList();
            readers.Add(Player);
            Dictionary<string, string> recalled = null;
            if (AIPawnControlMod.Settings.memoryEnabled && chat != null)
            {
                var authors = chat.messages.Skip(chat.messages.Count - Shown).Select(m => m.author == Player ? PersonFile.Player : m.author)
                    .Where(n => n != mind.pawn.LabelShort).Reverse();
                recalled = new Dictionary<string, string> { ["About"] = Retrieval.About(mind, authors, 4, 110) };
            }
            return PromptBuilder.Build("post", mind, new Dictionary<string, string> { ["readers"] = string.Join(", ", readers) }, recalled);
        }

        internal static Dictionary<string, object> PostSchema() => Schema.Obj(new Dictionary<string, object>
        {
            ["reason"] = Schema.Reason(),
            ["post"] = Schema.Str(MaxPost + 100),
        });

        private static void OnPost(PawnMind mind, Dictionary<string, object> reply)
        {
            string text = SpeechLog.Clean(reply.Str("post"), MaxPost);
            if (text == null || Instance == null)
                return;
            Instance.Add(mind.pawn.LabelShort, text);
            mind.AddDecision($"Wrote in the group chat: \"{text}\"", importance: 0); // Add recorded the event
        }

        // ---------- The Compact call ----------

        private void MaybeCompact()
        {
            if (messages.Count >= CompactAt && compacting == null)
                Compact();
        }

        /// <summary>Folds every message but the last 5 into a new summary, which replaces the old one.</summary>
        public void Compact()
        {
            if (compacting != null || messages.Count <= Shown)
                return;
            var folded = messages.Take(messages.Count - Shown).ToList();
            Map map = Find.AnyPlayerHomeMap;
            string prompt = Prompts.Fill("compact", new Dictionary<string, string>
            {
                ["summary"] = summary ?? "(none yet)",
                ["messages"] = string.Join("\n", folded.Select(m => Line(m, map))),
                ["now"] = Now(map),
            });
            var schema = Schema.Obj(new Dictionary<string, object> { ["summary"] = Schema.Str(900) });
            LlmRequest request = null;
            request = LlmClient.Send("compact", new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("user", prompt) }, schema, result =>
            {
                if (request != compacting || Current.Game != game)
                    return;
                compacting = null;
                string text = null;
                if (result.Ok)
                {
                    try
                    {
                        text = ((Dictionary<string, object>)Json.Parse(result.Content)).Str("summary")?.Trim();
                    }
                    catch (System.Exception e)
                    {
                        ModLog.Warning("Group chat compact: the reply wasn't valid JSON: " + e.Message);
                    }
                }
                else
                    ModLog.Warning("Group chat compact failed: " + result.Error);
                if (string.IsNullOrEmpty(text))
                    return; // the messages stay; the next message or the hourly check tries again
                summary = text;
                summaryTick = folded[folded.Count - 1].tick;
                messages.RemoveAll(folded.Contains);
                ModLog.Message($"Group chat compacted {folded.Count} messages: {summary}");
            });
            compacting = request;
        }

        /// <summary>The colony as it is now, so the summary can drop what's done and who's gone.</summary>
        private static string Now(Map map)
        {
            if (map == null)
                return "(unknown)";
            var rooms = map.regionGrid.AllRooms.Where(r => Ground.Indoor(r) && !r.Fogged && Ground.AnyRole(r)).Select(BuildManager.Label).Distinct().ToList();
            return $"{GameTime.Now(map)}. Colonists: {string.Join(", ", map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort))}. "
                   + SnapshotBuilder.Colony(map) + $" Rooms: {(rooms.Count > 0 ? string.Join(", ", rooms) : "none yet")}.";
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % GenDate.TicksPerHour == 0)
                MaybeCompact(); // after a failed call, or a save loaded at 15
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref messages, "messages", LookMode.Deep);
            Scribe_Values.Look(ref summary, "summary");
            Scribe_Values.Look(ref summaryTick, "summaryTick", -1);
            Scribe_References.Look(ref turn, "turn");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                messages = messages ?? new List<GroupMessage>();
        }
    }
}
