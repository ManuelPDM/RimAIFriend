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
                defaultDesc = "Sends a fixed chat message as the player asking for something later (just conversation: no promise list).",
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
                defaultLabel = "DEV: Reflect now",
                defaultDesc = "Runs Reflect over the last 24 hours, whatever the time (twice in a row tests the dedup).",
                icon = MindIcon,
                action = () =>
                {
                    if (!mind.ReflectNow())
                        Messages.Message("Can't reflect now: " + (mind.Thinking ? "already thinking" : "no persona yet"), MessageTypeDefOf.RejectInput, false);
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Show retrieval",
                defaultDesc = "Logs the ranked memories for her current situation, with each score part, and what [On my mind] would get.",
                icon = MindIcon,
                action = mind.ShowRetrieval,
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Fake a year of memories",
                defaultDesc = "Adds 305 fake memories and 60 diary entries with random vectors, for the size and speed test. Only on a test save.",
                icon = MindIcon,
                action = () => Messages.Message(FakeMemories.Fill(mind), MessageTypeDefOf.NeutralEvent, false),
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Chat: ask about a memory",
                defaultDesc = "Asks about her most important memory without giving it away: who it was with, or her worst day here.",
                icon = MindIcon,
                action = () =>
                {
                    var top = mind.memory.memories.Where(m => !m.archived).OrderByDescending(m => m.importance).FirstOrDefault();
                    string who = top?.people.FirstOrDefault(n => n != PersonFile.Player);
                    string question = who != null ? $"Do you remember what happened with {who}?" : "Do you remember your worst day here so far?";
                    MainThread.Post(() => mind.PlayerSays(question));
                },
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Chat: ask about a made-up event",
                defaultDesc = "Asks about something that never happened, to check she doesn't invent it.",
                icon = MindIcon,
                action = () => MainThread.Post(() => mind.PlayerSays("Do you remember the trader with the blue parrot who visited us last week?")),
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Chat: be rude",
                defaultDesc = "Sends a fixed rude order as the player.",
                icon = MindIcon,
                action = () => MainThread.Post(() => mind.PlayerSays("Stop wasting time and go haul something. Now.")),
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Chat: apologise",
                defaultDesc = "Sends a fixed apology as the player.",
                icon = MindIcon,
                action = () => MainThread.Post(() => mind.PlayerSays("Hey, I'm sorry for how I talked to you earlier. That wasn't fair of me.")),
            };
            foreach (bool near in new[] { true, false })
            {
                yield return new Command_Action
                {
                    defaultLabel = near ? "DEV: Others talk nearby" : "DEV: Others talk far away",
                    defaultDesc = "Teleports two other colonists next to each other, " + (near ? "a few cells away in view" : "30+ cells away") +
                                  ", and makes them chitchat, to test whether she witnesses it.",
                    icon = MindIcon,
                    action = () => Messages.Message(OthersTalk(pawn, near), MessageTypeDefOf.NeutralEvent, false),
                };
            }
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

        private static string OthersTalk(Pawn me, bool near)
        {
            Map map = me.Map;
            var others = map.mapPawns.FreeColonistsSpawned.Where(p => p != me && p.Awake() && !p.Downed && !p.InMentalState).Take(2).ToList();
            if (others.Count < 2)
                return "Need two other awake colonists.";
            bool Fits(IntVec3 c) => c.Standable(map) && c.GetFirstPawn(map) == null && (c + IntVec3.East).Standable(map) && (c + IntVec3.East).GetFirstPawn(map) == null;
            bool found = near
                ? CellFinder.TryFindRandomCellNear(me.Position, map, 5, c => Fits(c) && c.DistanceTo(me.Position) >= 3f && GenSight.LineOfSight(me.Position, c, map), out IntVec3 cell)
                : CellFinder.TryFindRandomCellNear(me.Position, map, 60, c => Fits(c) && c.DistanceTo(me.Position) >= 30f, out cell);
            if (!found)
                return "No free spot found.";
            others[0].Position = cell;
            others[0].Notify_Teleported();
            others[1].Position = cell + IntVec3.East;
            others[1].Notify_Teleported();
            if (others[0].interactions.InteractedTooRecentlyToInteract())
                return $"{others[0].LabelShort} talked too recently; try again in a moment.";
            bool ok = others[0].interactions.TryInteractWith(others[1], InteractionDefOf.Chitchat);
            return $"{others[0].LabelShort} → {others[1].LabelShort} at {(int)cell.DistanceTo(me.Position)} cells: " + (ok ? "chitchat happened" : "the interaction was refused");
        }
    }
}
