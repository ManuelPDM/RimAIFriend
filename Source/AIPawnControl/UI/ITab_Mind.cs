using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>Inspector tab: status, persona, plan, last reasoning and recent decisions. Only shown for pawns with a mind.</summary>
    public class ITab_Mind : ITab
    {
        private Vector2 scroll;
        private float lastHeight;

        public ITab_Mind()
        {
            size = new Vector2(460f, 480f);
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
            Section(sb, "AIPawnControl_TabIntent", mind.intent ?? "…");
            Section(sb, "AIPawnControl_TabSchedule", ActionCatalog.DescribeSchedule(mind.pawn));
            Section(sb, "AIPawnControl_TabPriorities", ActionCatalog.DescribePriorities(mind.pawn));
            Section(sb, "AIPawnControl_TabReason", mind.lastReason ?? "…");
            Section(sb, "AIPawnControl_TabRecent", mind.decisions.Count > 0 ? string.Join("\n", Enumerable.Reverse(mind.decisions)) : "…");
            sb.AppendLine("AIPawnControl_TabBudget".Translate(Mathf.Max(0, mind.ActsLeft), mind.ExtraPlansLeft).ToString());

            Rect outRect = new Rect(0f, 0f, size.x, size.y).ContractedBy(12f);
            outRect.yMin += 20f; // leave room for the tab's close button
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, lastHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            string text = sb.ToString().TrimEnd();
            lastHeight = Text.CalcHeight(text, viewRect.width);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, lastHeight), text);
            Widgets.EndScrollView();
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
