using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Dev-mode gizmos on a colonist for the tools used most, the same as their debug actions (DevTools). The labels are
    /// what docs/test-scripts click.
    /// </summary>
    public static class DevGizmos
    {
        public static IEnumerable<Gizmo> For(Pawn pawn, PawnMind mind)
        {
            Command_Action Button(string label, System.Action action) =>
                new Command_Action { defaultLabel = "DEV: " + label, icon = Patch_PawnGizmos.MindIcon, action = action };

            if (mind != null)
            {
                yield return Button("Think now", () => DevTools.ThinkNow(mind));
                yield return Button("Reflect now", () => DevTools.ReflectNow(mind));
                yield return Button("Regenerate persona", mind.RegeneratePersona);
                yield return Button("Base call now", () => DevTools.BaseCallNow(mind));
                yield return Button("Upgrade call now", () => DevTools.UpgradeCallNow(mind));
            }
            if (BuildManager.Instance?.ActiveProject(pawn) is BuildProject project)
            {
                yield return Button("Finish project instantly", () => DevTools.FinishInstantly(project));
                yield return Button("Cancel project", () => DevTools.Cancel(project));
            }
        }
    }
}
