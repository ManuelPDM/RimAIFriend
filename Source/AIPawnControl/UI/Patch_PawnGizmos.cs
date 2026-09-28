using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>The "AI mind" toggle on every colonist, and in dev mode the dev gizmos (Dev/DevGizmos.cs).</summary>
    [StaticConstructorOnStartup]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_PawnGizmos
    {
        public static readonly Texture2D MindIcon = ContentFinder<Texture2D>.Get("Things/Mote/SpeechSymbols/Chitchat");

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

            if (Prefs.DevMode)
                foreach (var gizmo in DevGizmos.For(pawn, mind))
                    yield return gizmo;
        }
    }
}
