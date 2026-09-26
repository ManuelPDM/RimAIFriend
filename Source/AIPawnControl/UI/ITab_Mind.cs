using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>Inspector tab: status, persona, plan, last reasoning, recent decisions, and a chat with the pawn at the bottom.
    /// Only shown for pawns with a mind.</summary>
    public class ITab_Mind : ITab
    {
        private const float ChatHeight = 230f;
        private const float InputHeight = 30f;
        private const int ShownChatLines = 8;
        private const string InputControl = "AIPC_ChatInput";

        private Vector2 scroll;
        private float lastHeight;
        private Vector2 chatScroll;
        private int lastChatCount;
        private string input = "";

        public ITab_Mind()
        {
            size = new Vector2(460f, 700f);
            labelKey = "AIPawnControl_MindTab";
        }

        private PawnMind Mind => SelPawn != null ? MindManager.Instance?.MindOf(SelPawn) : null;

        public override bool IsVisible => Mind != null;

        protected override void FillTab()
        {
            PawnMind mind = Mind;
            if (mind == null)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("AIPawnControl_TabStatus".Translate(Status(mind)).ToString());
            if (!ActionCatalog.ManualPriorities)
                sb.AppendLine("AIPawnControl_TabManualOff".Translate().Colorize(ColoredText.WarningColor));
            sb.AppendLine();
            Section(sb, "AIPawnControl_TabPersona", mind.persona ?? "…");
            if (!string.IsNullOrWhiteSpace(mind.note))
                Section(sb, "AIPawnControl_TabNote", mind.note);
            if (!string.IsNullOrEmpty(mind.memory.lately))
                Section(sb, "AIPawnControl_TabLately", mind.memory.lately);
            if (mind.memory.goals.Count > 0)
                Section(sb, "AIPawnControl_TabGoals", string.Join("\n", mind.memory.goals.Select(g => (g.source == Goal.Promise ? "AIPawnControl_TabPromise".Translate() + " " : "") + g.text)));
            Section(sb, "AIPawnControl_TabIntent", mind.intent ?? "…");
            Section(sb, "AIPawnControl_TabSchedule", ActionCatalog.DescribeSchedule(mind.pawn));
            Section(sb, "AIPawnControl_TabPriorities", ActionCatalog.DescribePriorities(mind.pawn));
            Section(sb, "AIPawnControl_TabReason", mind.lastReason ?? "…");
            Section(sb, "AIPawnControl_TabRecent", mind.decisions.Count > 0 ? string.Join("\n", Enumerable.Reverse(mind.decisions)) : "…");
            if (mind.memory.diary.Count > 0)
                Section(sb, "AIPawnControl_TabDiary", mind.memory.diary[mind.memory.diary.Count - 1].text);
            sb.AppendLine("AIPawnControl_TabBudget".Translate(Mathf.Max(0, mind.ActsLeft), mind.ExtraPlansLeft).ToString());

            Rect inner = new Rect(0f, 0f, size.x, size.y).ContractedBy(12f);
            inner.yMin += 20f; // leave room for the tab's close button
            Rect outRect = new Rect(inner.x, inner.y, inner.width, inner.height - ChatHeight - 8f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, lastHeight);
            if (Widgets.ButtonText(new Rect(inner.xMax - 110f, inner.y, 110f, 26f), "AIPawnControl_MemoriesButton".Translate()))
                Find.WindowStack.Add(new Dialog_Memories(mind));
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            string text = sb.ToString().TrimEnd();
            lastHeight = Text.CalcHeight(text, viewRect.width);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, lastHeight), text);
            Widgets.EndScrollView();

            DrawChat(mind, new Rect(inner.x, inner.yMax - ChatHeight, inner.width, ChatHeight));
        }

        private void DrawChat(PawnMind mind, Rect rect)
        {
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            string name = mind.pawn.LabelShort;
            Widgets.Label(new Rect(rect.x, rect.y + 4f, rect.width, 24f),
                "AIPawnControl_TabChat".Translate(name).CapitalizeFirst().Colorize(ColoredText.TipSectionTitleColor));

            string you = "AIPawnControl_ChatYou".Translate();
            var lines = mind.chat.Skip(Mathf.Max(0, mind.chat.Count - ShownChatLines)).Select(l =>
                l.from == ChatLine.From.Player ? you + ": " + l.text
                : l.from == ChatLine.From.Mind ? name + ": " + l.text
                : l.text.Colorize(ColoredText.SubtleGrayColor)).ToList();
            if (mind.ChatThinking)
                lines.Add("AIPawnControl_ChatThinking".Translate(name).Colorize(ColoredText.SubtleGrayColor));
            else if (mind.ChatWaitingForWake)
                lines.Add("AIPawnControl_ChatWaiting".Translate(name).Colorize(ColoredText.SubtleGrayColor));

            Rect outRect = new Rect(rect.x, rect.y + 28f, rect.width, rect.height - 28f - InputHeight - 6f);
            Widgets.DrawMenuSection(outRect);
            outRect = outRect.ContractedBy(4f);
            string text = string.Join("\n", lines);
            float width = outRect.width - 16f;
            Rect viewRect = new Rect(0f, 0f, width, Text.CalcHeight(text, width));
            if (lines.Count != lastChatCount)
            {
                lastChatCount = lines.Count;
                chatScroll.y = float.MaxValue; // follow new messages
            }
            Widgets.BeginScrollView(outRect, ref chatScroll, viewRect);
            Widgets.Label(viewRect, text);
            Widgets.EndScrollView();

            Rect inputRect = new Rect(rect.x, rect.yMax - InputHeight, rect.width - 90f, InputHeight);
            bool enter = Event.current.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == InputControl
                         && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (enter)
                Event.current.Use();
            GUI.SetNextControlName(InputControl);
            input = Widgets.TextField(inputRect, input, 300);
            bool send = Widgets.ButtonText(new Rect(inputRect.xMax + 6f, inputRect.y, 84f, InputHeight), "AIPawnControl_ChatSend".Translate());
            if ((send || enter) && !string.IsNullOrWhiteSpace(input))
            {
                string message = input.Trim();
                input = "";
                MainThread.Post(() => mind.PlayerSays(message)); // game changes (waking her) happen in the pump, never in OnGUI
            }
        }

        private static string Status(PawnMind mind)
        {
            if (mind.Thinking)
                return "AIPawnControl_StatusThinking".Translate(mind.ThinkingSeconds.ToString("0"));
            if (mind.Unreachable)
                return "AIPawnControl_StatusUnreachable".Translate();
            string paused = mind.PausedReason();
            return paused != null ? "AIPawnControl_StatusPaused".Translate(paused) : "AIPawnControl_StatusIdle".Translate();
        }

        private static void Section(StringBuilder sb, string key, string body)
        {
            sb.AppendLine(key.Translate().CapitalizeFirst().Colorize(ColoredText.TipSectionTitleColor));
            sb.AppendLine(body);
            sb.AppendLine();
        }
    }

    [StaticConstructorOnStartup]
    internal static class MindTabRegistration
    {
        static MindTabRegistration()
        {
            // Added from code rather than an XML patch, because Human inherits its tab list from the abstract BasePawn.
            ThingDef human = ThingDefOf.Human;
            human.inspectorTabs?.Add(typeof(ITab_Mind));
            human.inspectorTabsResolved?.Add(InspectTabManager.GetSharedInstance(typeof(ITab_Mind)));
        }
    }
}
