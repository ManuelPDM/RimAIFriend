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
                defaultDesc = "Skips the LLM: starts the first offered talk option (chitchat preferred) with a fixed test line, to test the talk job and the speech display.",
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
                    string result = MindActions.TalkTo(mind, target, def, $"Test line from {pawn.LabelShort}. Can you read this, {target.LabelShort}?");
                    mind.AddDecision($"(dev) talk to {target.LabelShort}: {def.label}: {result}");
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Say a test line",
                defaultDesc = "Skips the LLM: a solo remark bubble with a fixed line, to test solo entries (they must never be saved).",
                icon = MindIcon,
                action = () => SpeechLog.Say(pawn, $"Solo test line from {pawn.LabelShort}, said out loud."),
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Chat: ask for a talk",
                defaultDesc = "Sends a fixed chat message as the player (\"Could you go talk to <nearest colonist>?\"), like the Mind tab's Send button.",
                icon = MindIcon,
                action = () =>
                {
                    Pawn other = pawn.Map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn).OrderBy(p => p.Position.DistanceToSquared(pawn.Position)).FirstOrDefault();
                    string message = other != null ? $"Could you go talk to {other.LabelShort}? I think they could use some company." : "How are you doing?";
                    MainThread.Post(() => mind.PlayerSays(message));
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Chat: ask for later",
                defaultDesc = "Sends a fixed chat message as the player asking for something later, to test the chat note.",
                icon = MindIcon,
                action = () => MainThread.Post(() => mind.PlayerSays("No rush, but some time later today, could you check in on the others and see how they're holding up?")),
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Knock out",
                defaultDesc = "Gives anesthetic, to test that chat waits until they come to.",
                icon = MindIcon,
                action = () => pawn.health.AddHediff(HediffDefOf.Anesthetic),
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
