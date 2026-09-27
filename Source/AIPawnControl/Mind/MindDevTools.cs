using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Dev tools for what the minds see and say to each other (PHASE6.md §9), as debug actions RimBridge can run by path.</summary>
    public static class MindDevTools
    {
        public static string ContextPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "AIPawnControl", "context.txt");

        private static PawnMind SelectedMind()
        {
            if (Find.Selector.SingleSelectedThing is Pawn pawn && MindManager.Instance?.MindOf(pawn) is PawnMind mind)
                return mind;
            Messages.Message("Select one colonist with a mind first.", MessageTypeDefOf.RejectInput, false);
            return null;
        }

        /// <summary>
        /// Writes the Act prompt the selected mind would get right now, with each section's size. The memory it would
        /// recall for the situation ([On my mind]) is left out; that needs an embedding and marks memories shown.
        /// </summary>
        [DebugAction("AI Pawn Control", "Show context now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ShowContextNow()
        {
            PawnMind mind = SelectedMind();
            if (mind == null)
                return;
            var menu = ActionCatalog.BuildActMenu(mind.pawn, mind);
            var messages = PromptBuilder.Build("act", mind, new Dictionary<string, string>
            {
                ["trigger"] = "(Show context now)",
                ["menu"] = ActionCatalog.DescribeMenu(menu),
                ["talkto"] = PawnMind.TalkToText(menu),
            }, Recall.Plan(mind).Sections);
            string system = messages[0].Value, user = messages[1].Value;
            var sb = new StringBuilder();
            sb.AppendLine($"{mind.pawn.LabelShort}, tick {Find.TickManager.TicksGame}. System {system.Length} chars, user {user.Length} chars.");
            foreach (string line in user.Split('\n').Where(l => l.StartsWith("[")))
            {
                int end = line.IndexOf(']');
                sb.AppendLine($"  {line.Substring(0, end + 1)} {line.Length}");
            }
            sb.AppendLine().AppendLine("=== SYSTEM").AppendLine(system).AppendLine().AppendLine("=== USER").AppendLine(user);
            Directory.CreateDirectory(Path.GetDirectoryName(ContextPath));
            File.WriteAllText(ContextPath, sb.ToString());
            ModLog.Message($"{mind.pawn.LabelShort}: context written to {ContextPath} ({user.Length} chars).");
        }

        [DebugAction("AI Pawn Control", "Queue status", allowedGameStates = AllowedGameStates.Playing)]
        public static void QueueStatus() => ModLog.Message("LLM queue: " + LlmClient.QueueStatus());

        /// <summary>The selected mind starts a deep talk with another mind, with a fixed line asking for help, so the Reply runs without an Act.</summary>
        [DebugAction("AI Pawn Control", "Talk to mind now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static List<DebugActionNode> TalkToMindNow()
        {
            return (MindManager.Instance?.Minds ?? Enumerable.Empty<PawnMind>())
                .Select(other => new DebugActionNode(other.pawn.LabelShort, DebugActionType.Action, () =>
                {
                    PawnMind mind = SelectedMind();
                    if (mind == null || mind == other)
                        return;
                    string line = $"{other.pawn.LabelShort}, could you cut some trees for us? We're short on wood.";
                    string result = MindActions.TalkTo(mind, other.pawn, InteractionDefOf.DeepTalk, line);
                    mind.AddDecision($"(dev) talk to {other.pawn.LabelShort}: {result}");
                    ModLog.Message($"{mind.pawn.LabelShort} (dev) talk to {other.pawn.LabelShort}: {result}");
                })).ToList();
        }
    }
}
