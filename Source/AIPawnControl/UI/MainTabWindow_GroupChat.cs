using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>The "Group chat" bottom-bar tab (GROUP_CHAT.md §9): the summary, the messages, whose turn it is, and a box to write in.</summary>
    public class MainTabWindow_GroupChat : MainTabWindow
    {
        private const float InputHeight = 30f;
        private const string InputControl = "AIPC_GroupChatInput";

        private Vector2 scroll;
        private int lastCount = -1;
        private string input = "";

        public override Vector2 RequestedTabSize => new Vector2(620f, 560f);

        public override void DoWindowContents(Rect inRect)
        {
            var chat = GroupChat.Instance;
            if (chat == null)
                return;
            Map map = Find.CurrentMap;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "AIPawnControl_GroupChatTitle".Translate());
            Text.Font = GameFont.Small;
            string turn = chat.TurnHolder?.pawn?.LabelShort;
            string status = turn != null ? "AIPawnControl_GroupChatTurn".Translate(turn).ToString() : "AIPawnControl_GroupChatNoMinds".Translate().ToString();
            if (chat.Compacting)
                status += " " + "AIPawnControl_GroupChatCompacting".Translate();
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 24f), status.Colorize(ColoredText.SubtleGrayColor));

            var lines = chat.Messages.Select(m => GroupChat.Line(m, map)).ToList();
            if (chat.Summary != null)
                lines.Insert(0, ("AIPawnControl_GroupChatEarlier".Translate() + " " + chat.Summary).Colorize(ColoredText.SubtleGrayColor));
            string text = lines.Count > 0 ? string.Join("\n\n", lines) : "AIPawnControl_GroupChatEmpty".Translate().ToString();

            Rect outRect = new Rect(inRect.x, inRect.y + 62f, inRect.width, inRect.height - 62f - InputHeight - 8f);
            Widgets.DrawMenuSection(outRect);
            outRect = outRect.ContractedBy(6f);
            float width = outRect.width - 16f;
            Rect viewRect = new Rect(0f, 0f, width, Text.CalcHeight(text, width));
            if (lines.Count != lastCount)
            {
                lastCount = lines.Count;
                scroll.y = float.MaxValue; // follow new messages
            }
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            Widgets.Label(viewRect, text);
            Widgets.EndScrollView();

            Rect inputRect = new Rect(inRect.x, inRect.yMax - InputHeight, inRect.width - 90f, InputHeight);
            bool enter = Event.current.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == InputControl
                         && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (enter)
                Event.current.Use();
            GUI.SetNextControlName(InputControl);
            input = Widgets.TextField(inputRect, input, GroupChat.MaxPost);
            bool send = Widgets.ButtonText(new Rect(inputRect.xMax + 6f, inputRect.y, 84f, InputHeight), "AIPawnControl_ChatSend".Translate());
            if ((send || enter) && !string.IsNullOrWhiteSpace(input))
            {
                string message = input.Trim();
                input = "";
                MainThread.Post(() => GroupChat.Instance?.PlayerWrites(message)); // game changes happen in the pump, never in OnGUI
            }
        }
    }
}
