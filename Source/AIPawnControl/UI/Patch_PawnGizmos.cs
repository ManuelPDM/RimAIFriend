using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    [StaticConstructorOnStartup]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_PawnGizmos
    {
        private static readonly Texture2D MindIcon = ContentFinder<Texture2D>.Get("Things/Mote/SpeechSymbols/Chitchat");

        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (var gizmo in gizmos)
                yield return gizmo;

            var manager = MindManager.Instance;
            if (manager == null || !__instance.IsColonistPlayerControlled || !__instance.RaceProps.Humanlike)
                yield break;

            Pawn pawn = __instance;
            PawnMind mind = manager.MindOf(pawn);
            yield return new Command_Toggle
            {
                defaultLabel = "AIPawnControl_MindToggle".Translate(),
                defaultDesc = "AIPawnControl_MindToggleDesc".Translate(pawn.LabelShort),
                icon = MindIcon,
                isActive = () => mind != null,
                toggleAction = () =>
                {
                    if (mind != null)
                        manager.Disable(pawn);
                    else
                        Find.WindowStack.Add(new Dialog_EnableMind(pawn));
                },
            };

            if (mind == null || !Prefs.DevMode)
                yield break;
            yield return new Command_Action
            {
                defaultLabel = "DEV: Think now",
                icon = MindIcon,
                action = () =>
                {
                    if (!mind.ThinkNow())
                        Messages.Message("Can't think now: " + (mind.Thinking ? "already thinking" : mind.PausedReason(ignoreSleep: true) ?? "no persona yet"), MessageTypeDefOf.RejectInput, false);
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Try a talk",
                defaultDesc = "Skips the LLM: starts the first offered talk option (chitchat preferred) to test the talk job.",
                icon = MindIcon,
                action = () =>
                {
                    var options = ActionCatalog.AvailableInteractions(pawn, mind);
                    if (options.Count == 0)
                    {
                        Messages.Message("No talk options right now.", MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    var (target, def) = options.FirstOrDefault(o => o.Item2 == InteractionDefOf.Chitchat);
                    if (target == null)
                        (target, def) = options[0];
                    string result = MindActions.TalkTo(mind, target, def);
                    mind.AddDecision($"(dev) talk to {target.LabelShort}: {def.label}: {result}");
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Force plan",
                icon = MindIcon,
                action = () =>
                {
                    if (!mind.TryExtraPlan(force: true))
                        Messages.Message("Can't plan now: " + (mind.Thinking ? "already thinking" : mind.PausedReason(ignoreSleep: true)), MessageTypeDefOf.RejectInput, false);
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Regenerate persona",
                icon = MindIcon,
                action = mind.RegeneratePersona,
            };
        }
    }
}
