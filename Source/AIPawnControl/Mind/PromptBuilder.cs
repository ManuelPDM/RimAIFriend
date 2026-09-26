using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIPawnControl
{
    /// <summary>
    /// Assembles the Plan/Act/Chat prompts from named sections. Each section has a fixed position and a character
    /// cap; each call type has a recipe of the sections it gets. Stable text goes first and volatile text last, so
    /// LM Studio (llama.cpp) can reuse its work on an unchanged prompt start. Over budget, the most droppable
    /// section goes first; the live snapshot is never dropped. Main thread only (it reads the snapshot).
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>
        /// Cap 0 = no limit. Drop 0 = never dropped; over budget, higher numbers are dropped first. A labelled section's
        /// text brings its own "[...]" labels (one "[About X]" line per person).
        /// </summary>
        private class Section
        {
            public readonly string Name;
            public readonly int Cap;
            public readonly int Drop;
            public readonly bool Labelled;

            public Section(string name, int cap = 0, int drop = 0, bool labelled = false)
            {
                Name = name;
                Cap = cap;
                Drop = drop;
                Labelled = labelled;
            }
        }

        // The live snapshot (SnapshotBuilder.Sections): no cap, never dropped.
        private static readonly string[] Snapshot =
        {
            "Me", "Time", "Condition", "Needs", "Feelings", "Skills", "Doing now", "My plan", "I promised the player",
            "People nearby", "Colony", "Colony stores", "Rooms", "Recent",
        };

        // Every user-prompt section, in prompt order: the snapshot, then memory.
        private static readonly Section[] Order = Snapshot.Select(name => new Section(name)).Concat(new[]
        {
            new Section("Today so far", cap: 700, drop: 1),
            new Section("About", cap: 700, drop: 2, labelled: true),
            new Section("On my mind", cap: 600, drop: 3),
            new Section("I remember", cap: 1200, drop: 4),
        }).ToArray();

        // Which sections each call type gets. The call type is also the name of its task template in Prompts/.
        private static readonly Dictionary<string, HashSet<string>> Recipes = new Dictionary<string, HashSet<string>>
        {
            ["plan"] = new HashSet<string>(Snapshot) { "Today so far", "About" },
            ["act"] = new HashSet<string>(Snapshot) { "Today so far", "About", "On my mind" },
            ["chat"] = new HashSet<string>(Snapshot) { "Today so far", "About", "I remember" },
            ["reflect"] = new HashSet<string> { "Me", "Time" }, // its events, people and goals come in the task values
        };

        /// <summary>Characters the sections of one user prompt may use before droppable ones go.</summary>
        private const int ContextBudget = 8000;

        /// <summary>System prompt plus the call's task template, filled with its sections and the task's own values.</summary>
        /// <param name="recalled">Memory sections from Recall, by section name.</param>
        public static List<KeyValuePair<string, string>> Build(string call, PawnMind mind, Dictionary<string, string> task, Dictionary<string, string> recalled = null)
        {
            var texts = SnapshotBuilder.Sections(mind.pawn, mind);
            texts["Today so far"] = mind.memory.TodaySoFar(mind.pawn, call == "act" ? 3 : 5);
            if (recalled != null)
                foreach (var pair in recalled)
                    texts[pair.Key] = pair.Value;
            var values = new Dictionary<string, string>(task)
            {
                ["time"] = SnapshotBuilder.TimeString(mind.pawn.Map),
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
            var goals = memory.goals.Where(g => g.source == Goal.Mine).Select(g => string.IsNullOrEmpty(g.why) ? g.text : $"{g.text} ({g.why})").ToList();
            if (goals.Count > 0)
                text += "\n[My goals] " + string.Join(" · ", goals);
            return text;
        }

        /// <summary>The labelled "[Name] text" lines of the call's recipe, in order. Null texts are left out.</summary>
        private static string Context(string call, Dictionary<string, string> texts)
        {
            var recipe = Recipes[call];
            var shown = Order.Where(s => recipe.Contains(s.Name) && texts.TryGetValue(s.Name, out string text) && text != null).ToList();
            var lines = shown.ToDictionary(s => s.Name, s => s.Labelled ? Cap(texts[s.Name], s.Cap) : $"[{s.Name}] {Cap(texts[s.Name], s.Cap)}");
            foreach (var section in shown.Where(s => s.Drop > 0).OrderByDescending(s => s.Drop))
            {
                if (lines.Values.Sum(l => l.Length + 1) <= ContextBudget)
                    break;
                lines.Remove(section.Name);
            }
            var sb = new StringBuilder();
            foreach (var section in shown)
                if (lines.TryGetValue(section.Name, out string line))
                    sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }

        private static string Cap(string text, int cap) =>
            cap <= 0 || text.Length <= cap ? text : text.Substring(0, cap - 1).TrimEnd() + "…";
    }
}
