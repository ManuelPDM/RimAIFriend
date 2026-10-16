using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>A mind's private threads with other minds, newest first, read-only: each thread's summary, then its lines.</summary>
    public class Dialog_Messages : Window
    {
        private readonly PawnMind mind;
        private Vector2 scroll;
        private float lastHeight;

        public Dialog_Messages(PawnMind mind)
        {
            this.mind = mind;
            doCloseX = true;
            closeOnClickedOutside = true;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize => new Vector2(620f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "AIPawnControl_MessagesTitle".Translate(mind.pawn.LabelShort));
            Text.Font = GameFont.Small;

            var sb = new StringBuilder();
            Map map = mind.pawn.MapHeld;
            var threads = DirectMessages.Instance?.ThreadsOf(mind.pawn).ToList();
            if (threads == null || threads.Count == 0)
                sb.AppendLine("AIPawnControl_MessagesNone".Translate());
            else
                foreach (var thread in threads)
                {
                    sb.AppendLine(thread.Other(mind.pawn).LabelShort.Colorize(ColoredText.TipSectionTitleColor));
                    if (thread.summary != null)
                        sb.AppendLine(thread.summary.Colorize(ColoredText.SubtleGrayColor));
                    foreach (var line in thread.lines)
                        sb.AppendLine(thread.Line(line, mind.pawn, map));
                    sb.AppendLine();
                }

            Rect outRect = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, lastHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            string text = sb.ToString().TrimEnd();
            lastHeight = Text.CalcHeight(text, viewRect.width);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, lastHeight), text);
            Widgets.EndScrollView();
        }
    }
}
