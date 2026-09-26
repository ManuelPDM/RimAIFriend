using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>What a mind remembers, for the player and for debugging (PHASE3.md §8): memories, person files and the last 7 days of raw events.</summary>
    public class Dialog_Memories : Window
    {
        private readonly PawnMind mind;
        private Vector2 scroll;
        private float lastHeight;

        public Dialog_Memories(PawnMind mind)
        {
            this.mind = mind;
            doCloseX = true;
            closeOnClickedOutside = true;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize => new Vector2(760f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "AIPawnControl_MemoriesTitle".Translate(mind.pawn.LabelShort));
            Text.Font = GameFont.Small;

            var sb = new StringBuilder();
            Map map = mind.pawn.MapHeld;
            var memory = mind.memory;
            int now = Find.TickManager.TicksGame;
            sb.AppendLine("AIPawnControl_MemoriesMemories".Translate(memory.memories.Count).Colorize(ColoredText.TipSectionTitleColor));
            foreach (var m in memory.memories.OrderByDescending(m => m.tick))
            {
                string age = ((now - m.tick) / (float)GenDate.TicksPerDay).ToString("0.0");
                string meta = $"M{m.id} · importance {m.importance} · {age} days ago · used {m.usedCount}×" +
                              (m.people.Count > 0 ? " · " + string.Join(", ", m.people) : "") + (m.vector != null ? "" : " · no vector") + (m.archived ? " · archived" : "");
                sb.AppendLine($"{meta.Colorize(ColoredText.SubtleGrayColor)}\n{m.text}");
            }
            sb.AppendLine();
            sb.AppendLine("AIPawnControl_MemoriesPeople".Translate(memory.files.Count).Colorize(ColoredText.TipSectionTitleColor));
            foreach (var f in memory.files)
            {
                sb.AppendLine(f.name.CapitalizeFirst().Colorize(ColoredText.SubtleGrayColor));
                if (!string.IsNullOrEmpty(f.impression))
                    sb.AppendLine(f.impression);
                if (!string.IsNullOrEmpty(f.threads))
                    sb.AppendLine("Threads: " + f.threads);
                foreach (var fact in f.facts)
                    sb.AppendLine($"• {fact.text} ({fact.source}{(fact.Open ? "" : ", no longer true")})");
                if (f.toldThem.Count > 0)
                    sb.AppendLine("Told them: " + string.Join(", ", f.toldThem.Select(id => "M" + id)));
            }
            sb.AppendLine();
            sb.AppendLine("AIPawnControl_MemoriesEvents".Translate(memory.events.Count).Colorize(ColoredText.TipSectionTitleColor));
            foreach (var e in Enumerable.Reverse(mind.memory.events))
            {
                string people = e.people.Count > 0 ? " · " + string.Join(", ", e.people) : "";
                string place = string.IsNullOrEmpty(e.place) ? "" : " · " + e.place;
                string meta = $"{e.When(map)} · {e.kind} · {e.source} · {e.importance}{people}{place}".Colorize(ColoredText.SubtleGrayColor);
                sb.AppendLine($"{meta}\n{e.Text}");
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
