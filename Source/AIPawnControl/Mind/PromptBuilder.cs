using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Assembles the Act/Chat/Base prompts from named sections. Each section has a fixed position and a character
    /// cap; each call type has a recipe of the sections it gets. Stable text goes first and volatile text last, so
    /// LM Studio (llama.cpp) can reuse its work on an unchanged prompt start. Over budget, the most droppable
    /// section goes first; the live snapshot is never dropped. Main thread only (it reads the snapshot).
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>Cap 0 = no limit. Drop 0 = never dropped; over budget, higher numbers are dropped first.</summary>
        private class Section
        {
            public readonly string Name;
            public readonly int Cap;
            public readonly int Drop;

            public Section(string name, int cap = 0, int drop = 0)
            {
                Name = name;
                Cap = cap;
                Drop = drop;
            }
        }

        // The live snapshot (SnapshotBuilder.Sections): no cap, never dropped.
        private static readonly string[] Snapshot =
        {
            "Me", "Time", "Condition", "Needs", "Feelings", "Doing now", "My project",
            "People", "Colony", "Colony stores", "Colony work", "Rooms", "Recent", "Group chat", "Danger",
        };

        // Every user-prompt section, in prompt order: the snapshot, then memory.
        private static readonly Section[] Order = Snapshot.Select(name => new Section(name)).Concat(new[]
        {
            new Section("Since yesterday", cap: 700, drop: 1),
            new Section("On my mind", cap: 600, drop: 3),
            new Section("I remember", cap: 1200, drop: 4),
            new Section("From my diary", cap: 400, drop: 5),
        }).ToArray();

        // Which sections each call type gets. The call type is also the name of its task template in Prompts/. "About" isn't a
        // section of its own: it adds what she thinks of each person to their [People] entry.
        private static readonly Dictionary<string, HashSet<string>> Recipes = new Dictionary<string, HashSet<string>>
        {
            ["act"] = new HashSet<string>(Snapshot) { "Since yesterday", "About", "On my mind" },
            ["chat"] = new HashSet<string>(Snapshot) { "Since yesterday", "About", "I remember", "From my diary" },
            ["message"] = new HashSet<string>(Snapshot) { "Since yesterday", "About", "I remember", "From my diary" }, // Chat, in a private thread with another pawn
            ["answer"] = new HashSet<string>(Snapshot) { "Since yesterday", "About", "I remember", "From my diary" }, // Chat, for a yes or no
            ["base"] = new HashSet<string>(Snapshot) { "Since yesterday" }, // the Act that chose it already recalled memories
            ["post"] = new HashSet<string>(Snapshot) { "Since yesterday", "About" }, // the group chat: her view of who wrote lately
            ["decide"] = new HashSet<string>(Snapshot) { "Since yesterday" }, // a choice the game asks the colony (WORLD.md)
            ["plan"] = new HashSet<string>(Snapshot) { "Since yesterday" }, // the colony's plan for a danger (DANGER_RESPONSE.md §9)
            ["reflect"] = new HashSet<string> { "Me", "Time" }, // its events and people come in the task values
            ["upgrade"] = new HashSet<string> { "Me", "Time", "Condition", "Feelings" }, // small: the Base call just picked the room
        };

        /// <summary>Characters the sections of one user prompt may use before droppable ones go.</summary>
        private const int ContextBudget = 80000; // about 20,000 tokens

        /// <summary>System prompt plus the call's task template, filled with its sections and the task's own values.</summary>
        /// <param name="recall">Memory sections and the people she has a view of, from Recall.</param>
        /// <param name="thread">The other pawn of the Message call's thread: the prompt shows that thread whole, so [People] leaves it out.</param>
        public static List<KeyValuePair<string, string>> Build(string call, PawnMind mind, Dictionary<string, string> task, Recall recall = null, Pawn thread = null)
        {
            var texts = SnapshotBuilder.Sections(mind.pawn, mind);
            texts["Since yesterday"] = mind.memory.SinceYesterday(mind.pawn, call == "act" ? 3 : 5, withChat: call != "chat");
            if (recall != null)
                foreach (var pair in recall.Sections)
                    texts[pair.Key] = pair.Value;
            if (Recipes[call].Contains("People"))
                texts["People"] = People(mind, Recipes[call].Contains("About") ? recall?.About : null,
                    DirectMessages.Instance?.Context(mind.pawn, mind.pawn.Map, except: thread));
            var values = new Dictionary<string, string>(task)
            {
                ["time"] = GameTime.Now(mind.pawn.Map),
                ["snapshot"] = Context(call, texts),
            };
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", System(mind)),
                new KeyValuePair<string, string>("user", Prompts.Fill(call, values)),
            };
        }

        /// <summary>Identity, persona and guidance, then the blocks that change about once a day (written by Reflect).</summary>
        public static string System(PawnMind mind)
        {
            string text = Prompts.Fill("system", new Dictionary<string, string>
            {
                ["name"] = mind.pawn.LabelShort,
                ["persona"] = mind.persona ?? "",
                ["guidance"] = Prompts.Fill("guidance", new Dictionary<string, string>()),
            });
            var memory = mind.memory;
            if (!string.IsNullOrEmpty(memory.lately))
                text += "\n\n[Who I am lately] " + memory.lately;
            return text;
        }

        /// <summary>
        /// [People]: each colonist's own lines from the snapshot, then what she thinks of them and her private thread with
        /// them. Anyone she has a view of or a thread with who isn't among them (the player, someone away) follows as an
        /// entry of their own.
        /// </summary>
        private static string People(PawnMind mind, List<(string name, string text)> about, List<(string name, string text)> threads)
        {
            var here = SnapshotBuilder.People(mind.pawn);
            about = about ?? new List<(string name, string text)>();
            threads = threads ?? new List<(string name, string text)>();
            var names = here.Select(e => e.name).Concat(about.Select(e => e.name)).Concat(threads.Select(e => e.name)).Distinct().ToList();
            if (names.Count == 0)
                return null;
            return string.Join("\n", names.Select(name =>
            {
                string own = here.FirstOrDefault(e => e.name == name).text
                             ?? (AIPawnControl.People.Label(name, mind.pawn) is string who ? $"{name}: {who}" : name);
                var lines = own.Split('\n').Concat(about.Concat(threads).Where(e => e.name == name).SelectMany(e => e.text.Split('\n'))).ToList();
                return "- " + lines[0] + string.Concat(lines.Skip(1).Select(l => "\n  " + l));
            }));
        }

        /// <summary>
        /// The "[Name] text" sections of the call's recipe, in order; a section of several lines starts below its label and
        /// has a blank line before and after it. Null texts are left out.
        /// </summary>
        private static string Context(string call, Dictionary<string, string> texts)
        {
            var recipe = Recipes[call];
            var shown = Order.Where(s => recipe.Contains(s.Name) && texts.TryGetValue(s.Name, out string text) && text != null).ToList();
            var lines = shown.ToDictionary(s => s.Name, s =>
            {
                string text = Cap(texts[s.Name], s.Cap);
                return $"[{s.Name}]" + (text.IndexOf('\n') >= 0 ? "\n" : " ") + text;
            });
            foreach (var section in shown.Where(s => s.Drop > 0).OrderByDescending(s => s.Drop))
            {
                if (lines.Values.Sum(l => l.Length + 1) <= ContextBudget)
                    break;
                lines.Remove(section.Name);
            }
            var sb = new StringBuilder();
            string last = null;
            foreach (var section in shown)
                if (lines.TryGetValue(section.Name, out string line))
                {
                    if (last != null && (line.IndexOf('\n') >= 0 || last.IndexOf('\n') >= 0))
                        sb.AppendLine(); // a blank line around a section of several lines
                    sb.AppendLine(line);
                    last = line;
                }
            return sb.ToString().TrimEnd();
        }

        private static string Cap(string text, int cap) =>
            cap <= 0 || text.Length <= cap ? text : text.Substring(0, cap - 1).TrimEnd() + "…";
    }
}
