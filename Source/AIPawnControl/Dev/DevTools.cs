using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Dev tools that skip the waiting (CLEANUP.md §4): force a call, a build or a chore instead of waiting days for a mind
    /// to pick it, and reports that show state at once. Debug actions under "AI Pawn Control", so RimBridge can run them by
    /// path; the ones that act on a colonist use the selected one. No LLM unless the name says "call".
    /// </summary>
    public static class DevTools
    {
        public const string Category = "AI Pawn Control";

        public static string Folder => Path.Combine(GenFilePaths.SaveDataFolderPath, "AIPawnControl");

        internal static void Report(string text) => Messages.Message(text, MessageTypeDefOf.NeutralEvent, false);

        internal static void Reject(string text) => Messages.Message(text, MessageTypeDefOf.RejectInput, false);

        internal static Pawn SelectedColonist()
        {
            if (Find.Selector.SingleSelectedThing is Pawn pawn && pawn.IsColonist && pawn.Map != null)
                return pawn;
            Reject("Select one colonist first.");
            return null;
        }

        internal static PawnMind SelectedMind()
        {
            if (Find.Selector.SingleSelectedThing is Pawn pawn && MindManager.Instance?.MindOf(pawn) is PawnMind mind)
                return mind;
            Reject("Select one colonist with a mind first.");
            return null;
        }

        /// <summary>Her mind, or a mind-less stand-in for tools that only need a pawn to act for (chores, rooms).</summary>
        internal static PawnMind MindOrStandIn(Pawn pawn) => MindManager.Instance?.MindOf(pawn) ?? new PawnMind(pawn, null);

        internal static void WriteFile(string name, string text)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, name), text);
        }

        // ---------- Mind ----------

        public static void ThinkNow(PawnMind mind)
        {
            if (!mind.ThinkNow())
                Reject("Can't think now: " + (mind.Thinking ? "already thinking" : mind.PausedReason(ignoreSleep: true) ?? "no persona yet"));
        }

        public static void ReflectNow(PawnMind mind)
        {
            if (!mind.ReflectNow())
                Reject("Can't reflect now: " + (mind.Thinking ? "already thinking" : "no persona yet"));
        }

        public static void BaseCallNow(PawnMind mind)
        {
            if (mind.persona == null || mind.Thinking)
            {
                Reject($"{mind.pawn.LabelShort} {(mind.Thinking ? "is thinking already" : "has no persona yet")}.");
                return;
            }
            mind.AddDecision("DEV base call now: " + BaseCall.Start(mind, dev: true), importance: 0);
        }

        public static void UpgradeCallNow(PawnMind mind)
        {
            if (mind.persona == null || mind.Thinking)
            {
                Reject($"{mind.pawn.LabelShort} {(mind.Thinking ? "is thinking already" : "has no persona yet")}.");
                return;
            }
            var room = Upgrades.Rooms(mind.pawn).FirstOrDefault(r => Upgrades.For(r, mind.pawn).Count > 0);
            mind.AddDecision("DEV upgrade call now: " + (room != null ? UpgradeCall.Start(mind, room) : "no room to upgrade"), importance: 0);
        }

        [DebugAction(Category, "Think now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ThinkNowAction() => ForMind(ThinkNow);

        [DebugAction(Category, "Reflect now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReflectNowAction() => ForMind(ReflectNow);

        [DebugAction(Category, "Regenerate persona", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RegeneratePersonaAction() => ForMind(m => m.RegeneratePersona());

        private static void ForMind(Action<PawnMind> act)
        {
            if (SelectedMind() is PawnMind mind)
                act(mind);
        }

        /// <summary>The selected mind starts a deep talk with another mind, with a fixed line asking for help, so the Reply runs without an Act.</summary>
        [DebugAction(Category, "Talk to mind now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> TalkToMindNow() =>
            (MindManager.Instance?.Minds ?? new List<PawnMind>()).Select(other => new DebugActionNode(other.pawn.LabelShort, DebugActionType.Action, () =>
            {
                PawnMind mind = SelectedMind();
                if (mind == null || mind == other)
                    return;
                string result = MindActions.TalkTo(mind, other.pawn, InteractionDefOf.DeepTalk, $"{other.pawn.LabelShort}, could you cut some trees for us? We're short on wood.");
                mind.AddDecision($"(dev) talk to {other.pawn.LabelShort}: {result}");
                ModLog.Message($"{mind.pawn.LabelShort} (dev) talk to {other.pawn.LabelShort}: {result}");
            })).ToList();

        /// <summary>A fixed chat message from the player, like the Mind tab's Send button (RimBridge can't type into the tab).</summary>
        [DebugAction(Category, "Chat as player", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> ChatAsPlayer()
        {
            var lines = new List<(string label, Func<PawnMind, string> text)>
            {
                ("ask for a talk", m => m.pawn.Map.mapPawns.FreeColonistsSpawned.Where(p => p != m.pawn).OrderBy(p => p.Position.DistanceToSquared(m.pawn.Position))
                    .FirstOrDefault() is Pawn other ? $"Could you go talk to {other.LabelShort}? I think they could use some company." : "How are you doing?"),
                ("ask about a memory", m => m.memory.memories.Where(x => !x.archived).OrderByDescending(x => x.importance).FirstOrDefault()?.people
                    .FirstOrDefault(n => n != PersonFile.Player) is string who ? $"Do you remember what happened with {who}?" : "Do you remember your worst day here so far?"),
                ("ask about a made-up event", m => "Do you remember the trader with the blue parrot who visited us last week?"),
                ("be rude", m => "Stop wasting time and go haul something. Now."),
                ("apologise", m => "Hey, I'm sorry for how I talked to you earlier. That wasn't fair of me."),
            };
            return lines.Select(l => new DebugActionNode(l.label, DebugActionType.Action, () =>
            {
                if (SelectedMind() is PawnMind mind)
                {
                    string text = l.text(mind);
                    MainThread.Post(() => mind.PlayerSays(text));
                }
            })).ToList();
        }

        /// <summary>The Act prompt the selected mind would get right now, with each section's size, in context.txt ([On my mind] needs an embedding, so it's left out).</summary>
        [DebugAction(Category, "Show context now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ShowContextNow()
        {
            PawnMind mind = SelectedMind();
            if (mind == null)
                return;
            var menu = ActionCatalog.BuildActMenu(mind.pawn, mind);
            var messages = mind.ActMessages("(Show context now)", menu, Recall.Act(mind, Recall.Present(mind.pawn), null, null, peek: true));
            string system = messages[0].Value, user = messages[1].Value;
            var sb = new StringBuilder();
            sb.AppendLine($"{mind.pawn.LabelShort}, tick {Find.TickManager.TicksGame}. System {system.Length} chars, user {user.Length} chars.");
            foreach (string line in user.Split('\n').Where(l => l.StartsWith("[")))
                sb.AppendLine($"  {line.Substring(0, line.IndexOf(']') + 1)} {line.Length}");
            sb.AppendLine().AppendLine("=== SYSTEM").AppendLine(system).AppendLine().AppendLine("=== USER").AppendLine(user);
            WriteFile("context.txt", sb.ToString());
            ModLog.Message($"{mind.pawn.LabelShort}: context written to {Path.Combine(Folder, "context.txt")} ({user.Length} chars).");
        }

        /// <summary>The ranked memories for her situation now, each score part, and what [On my mind] would get (for tuning Retrieval).</summary>
        [DebugAction(Category, "Show retrieval", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ShowRetrieval()
        {
            PawnMind mind = SelectedMind();
            if (mind == null)
                return;
            var memory = mind.memory;
            var present = Recall.Present(mind.pawn);
            string query = Recall.ActQuery(mind.pawn, present);
            string Penalised(List<Retrieval.Scored> ranked)
            {
                var lines = ranked.Where(s => s.penalty > 0f).Select(s => $"M{s.memory.id} pen {s.penalty:0.00}").ToList();
                return lines.Count > 0 ? string.Join(", ", lines) : "none";
            }
            Recall.WithQuery(mind, query, (vector, tag) =>
            {
                var names = present.Select(p => p.LabelShort).ToList();
                var ranked = Retrieval.Rank(memory, vector, tag, names, SnapshotBuilder.RoomLabel(mind.pawn), names);
                var picked = Retrieval.Pick(ranked, 2, Retrieval.MinOnMind);
                int gap = Find.TickManager.TicksGame - memory.lastOnMindTick;
                var toPlayer = Retrieval.Rank(memory, vector, tag, names.Concat(new[] { PersonFile.Player }).ToList(), null, new[] { PersonFile.Player });
                ModLog.Message($"{mind.pawn.LabelShort} retrieval for \"{query}\" ({memory.memories.Count} memories; vector: {(vector != null ? tag : "none")}, " +
                               $"[On my mind] {(gap >= Retrieval.OnMindGapTicks ? "allowed" : $"waits {(Retrieval.OnMindGapTicks - gap) / (float)GenDate.TicksPerHour:0.0}h")}, minimum {Retrieval.MinOnMind}):\n" +
                               string.Join("\n", ranked.Take(10).Select(s => (picked.Contains(s) ? "* " : "  ") + s)) +
                               $"\n{ranked.Count - Math.Min(10, ranked.Count)} more; * = would show\n" +
                               "With the player as the audience (as in Chat):\n" + string.Join("\n", toPlayer.Take(10).Select(s => "  " + s)) +
                               $"\nPenalised with {(names.Count > 0 ? string.Join(", ", names) : "nobody")} as the audience: {Penalised(ranked)}" +
                               $"\nPenalised with the player as the audience: {Penalised(toPlayer)}");
            });
        }

        [DebugAction(Category, "Queue status", allowedGameStates = AllowedGameStates.Playing)]
        private static void QueueStatus() => ModLog.Message("LLM queue: " + LlmClient.QueueStatus());

        [DebugAction(Category, "Reload prompts", allowedGameStates = AllowedGameStates.Playing)]
        private static void ReloadPrompts()
        {
            Prompts.Reload();
            ModLog.Message("Prompts will be re-read from " + Prompts.Folder);
        }

        /// <summary>Same as the Work tab's "Manual priorities" checkbox, which automation can't click.</summary>
        [DebugAction(Category, "Toggle manual priorities", allowedGameStates = AllowedGameStates.Playing)]
        private static void ToggleManualPriorities()
        {
            Find.PlaySettings.useWorkPriorities = !Find.PlaySettings.useWorkPriorities;
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                if (pawn.Faction == Faction.OfPlayer)
                    pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
            ModLog.Message("Manual priorities: " + (Find.PlaySettings.useWorkPriorities ? "ON" : "OFF"));
        }

        // ---------- Building ----------

        [DebugAction(Category, "Base call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BaseCallNowAction() => ForMind(BaseCallNow);

        [DebugAction(Category, "Upgrade call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void UpgradeCallNowAction() => ForMind(UpgradeCallNow);

        // ---------- Group chat (GROUP_CHAT.md) ----------

        /// <summary>The selected mind writes in the group chat now, whoever's turn it is (the turn doesn't move).</summary>
        [DebugAction(Category, "Group chat: post call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GroupPostNow() => ForMind(mind =>
        {
            if (mind.persona == null || mind.Thinking)
            {
                Reject($"{mind.pawn.LabelShort} {(mind.Thinking ? "is thinking already" : "has no persona yet")}.");
                return;
            }
            mind.AddDecision("DEV group chat post now: " + GroupChat.StartPost(mind), importance: 0);
        });

        [DebugAction(Category, "Group chat: pass the turn", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GroupPassTurn()
        {
            var chat = GroupChat.Instance;
            var holder = chat?.Holder();
            if (holder == null)
            {
                Reject("No mind holds the turn.");
                return;
            }
            chat.PassTurn(holder);
            Report($"Group chat: {holder.pawn.LabelShort} passed; now it's {chat.Holder()?.pawn.LabelShort}'s turn.");
        }

        [DebugAction(Category, "Group chat: compact call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GroupCompactNow()
        {
            var chat = GroupChat.Instance;
            if (chat == null || chat.Messages.Count <= 5)
                Reject("The group chat has 5 messages or fewer: nothing to fold.");
            else
                chat.Compact();
        }

        // ---------- Colony choices (WORLD.md) ----------

        /// <summary>Every open choice with its text and options, plus the quest offers left to the player and why. No LLM.</summary>
        [DebugAction(Category, "Choices: show open", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ChoicesShow()
        {
            Pawn by = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            var sb = new StringBuilder();
            foreach (var choice in ColonyChoices.All())
            {
                sb.AppendLine($"== {choice.key}");
                sb.AppendLine(choice.Describe());
                foreach (var option in choice.Options(by))
                    sb.AppendLine($"  [{option.id}] {option.label}");
            }
            foreach (var quest in Find.QuestManager.QuestsListForReading.Where(q => q.State == QuestState.NotYetAccepted && !q.hidden && ColonyChoices.LeaveReason(q) != null))
                sb.AppendLine($"(the player's: \"{quest.name}\" ({quest.root?.defName}): {ColonyChoices.LeaveReason(quest)})");
            string text = sb.Length > 0 ? sb.ToString() : "No open choices.";
            WriteFile("choices.txt", text);
            ModLog.Message("Open choices:\n" + text);
            Report("Open choices written to choices.txt.");
        }

        /// <summary>Answers a choice from code, as the selected colonist (or the first free one). No LLM.</summary>
        [DebugAction(Category, "Choices: answer...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> ChoicesAnswer()
        {
            Pawn by = Find.Selector.SingleSelectedThing as Pawn ?? Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            return ColonyChoices.All().Select(choice =>
            {
                var node = new DebugActionNode(choice.key, DebugActionType.Action);
                foreach (var option in choice.Options(by))
                    node.AddChild(new DebugActionNode(option.label, DebugActionType.Action, () =>
                    {
                        string result = ColonyChoices.Instance.Answer(choice, option.id, by);
                        ModLog.Message($"DEV answered {choice.key} as {by.LabelShort}: {result ?? "not possible"}; windows open: {string.Join(", ", Find.WindowStack.Windows.Select(w => w.GetType().Name))}");
                        Report(result ?? "Not possible any more.");
                    }));
                return node;
            }).ToList();
        }

        /// <summary>The selected mind decides a choice now (the Decide call), whoever the best negotiator is.</summary>
        [DebugAction(Category, "Choices: decide call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> ChoicesDecideNow() =>
            ColonyChoices.All().Select(choice => new DebugActionNode(choice.key, DebugActionType.Action, () => ForMind(mind =>
            {
                if (mind.persona == null || mind.Thinking)
                    Reject($"{mind.pawn.LabelShort} {(mind.Thinking ? "is thinking already" : "has no persona yet")}.");
                else if (!ColonyChoices.Instance.Decide(mind, choice))
                    Reject("Nothing to pick: fewer than 2 options.");
            }))).ToList();

        // ---------- Customs and gatherings (IDEOLOGY.md) ----------

        /// <summary>
        /// For every free colonist: [Colony customs], each of her ideoligion's rituals with its plan or why not, and the gathering
        /// and role lines her Act menu would get. Then the reform choice. Written to customs.txt. No LLM.
        /// </summary>
        [DebugAction(Category, "Customs: report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CustomsReport() => WriteFile("customs.txt", CustomsText(Find.CurrentMap));

        internal static string CustomsText(Map map)
        {
            var sb = new StringBuilder($"Customs, {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Active: {Customs.Active}.");
            if (Customs.Active)
            {
                var ideo = Customs.Primary;
                sb.Append($" Ideoligion: {ideo.name}, fluid: {ideo.Fluid}" + (ideo.development != null ? $", points {ideo.development.Points}/{ideo.development.NextReformationDevelopmentPoints}, reforms {ideo.development.reformCount}" : "") + ".");
            }
            sb.AppendLine($" Days since the last gathering: {(Find.TickManager.TicksGame - Traverse.Create(map.lordsStarter).Field("lastLordStartTick").GetValue<int>()) / (float)GenDate.TicksPerDay:0.0}.");
            if (Customs.Active)
                sb.AppendLine("Customs: " + string.Join("; ", Customs.Primary.PreceptsListForReading.Where(p => p.GetType() == typeof(Precept)).Select(Customs.Label)));
            if (Burials.Instance != null)
                sb.AppendLine(Burials.Instance.Describe(map));
            Pawn first = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            foreach (var def in DefDatabase<GatheringDef>.AllDefsListForReading.Where(d => d.IsRandomSelectable))
                sb.AppendLine($"Gathering {def.defName}: conditions {GatheringsUtility.AcceptableGameConditionsToStartGathering(map, def)}, " +
                              $"enough guests {GatheringsUtility.EnoughPotentialGuestsToStartGathering(map, def)}, organizer {(first != null && def.Worker.CanExecute(map, first))}, hour {GenLocalDate.HourInteger(map)}.");
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                sb.AppendLine($"\n== {pawn.LabelShort} (role: {pawn.Ideo?.GetRole(pawn)?.LabelCap ?? "none"})");
                sb.AppendLine(Customs.Section(pawn) ?? "(no [Colony customs])");
                if (Customs.Active)
                    foreach (var (ritual, obligation, plan, why) in Gatherings.RitualCandidates(pawn))
                        sb.AppendLine($"  ritual {Customs.ObligationLabel(ritual, obligation)}: " +
                                      (why ?? $"ready at {plan.Place}, {plan.Roles}quality {plan.quality.ToStringPercent()}, {plan.Hours} h"));
                foreach (var line in Gatherings.Lines(pawn))
                    sb.AppendLine($"  LINE [{line.key}] {line.label}");
            }
            foreach (var choice in ColonyChoices.All().Where(c => c.reform))
            {
                Pawn by = ColonyChoices.Decider(MindManager.Instance?.Minds ?? new List<PawnMind>(), choice)?.pawn ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                sb.AppendLine($"\n== {choice.key} (decider: {by?.LabelShort})\n{choice.Describe(by)}");
                foreach (var option in choice.Options(by))
                    sb.AppendLine($"  [{option.id}] {option.label}");
            }
            return sb.ToString();
        }

        /// <summary>Makes the colony's ideoligion fluid (as if chosen at game start), so it can reform.</summary>
        [DebugAction(Category, "Customs: make ideoligion fluid", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CustomsFluid()
        {
            if (!Customs.Active)
            {
                Reject("No ideoligion (Ideology off or classic mode).");
                return;
            }
            Customs.Primary.Fluid = true;
            Report($"{Customs.Primary.name} is fluid now.");
        }

        /// <summary>Adds development points until the colony's ideoligion can reform.</summary>
        [DebugAction(Category, "Customs: points to reform", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CustomsPoints()
        {
            var dev = Customs.Active ? Customs.Primary.development : null;
            if (dev == null)
            {
                Reject("The ideoligion isn't fluid.");
                return;
            }
            dev.TryAddDevelopmentPoints(dev.NextReformationDevelopmentPoints - dev.Points);
            Report($"Points: {dev.Points}/{dev.NextReformationDevelopmentPoints}.");
        }

        /// <summary>Answers the open reform choice with its first change, as the decider would. No LLM.</summary>
        [DebugAction(Category, "Customs: reform (first change)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CustomsReformFirst()
        {
            var choice = ColonyChoices.All().FirstOrDefault(c => c.reform);
            Pawn by = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            var option = choice?.Options(by).FirstOrDefault();
            if (option == null)
            {
                Reject("No reform choice is open.");
                return;
            }
            string result = ColonyChoices.Instance.Answer(choice, option.id, by);
            Report(result ?? "Not possible any more.");
        }

        /// <summary>Kills the selected colonist, for a burial and funeral test. Their body stays where they fell.</summary>
        [DebugAction(Category, "Burials: kill selected colonist", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BurialsKill()
        {
            if (SelectedColonist() is Pawn pawn)
                pawn.Kill(null);
        }

        /// <summary>Starts one of the selected colonist's gathering or role lines, as if her Act picked it. No LLM.</summary>
        [DebugAction(Category, "Gatherings: start...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> GatheringsStart()
        {
            if (!(Find.Selector.SingleSelectedThing is Pawn pawn) || !pawn.IsColonist)
                return new List<DebugActionNode> { new DebugActionNode("(select a colonist first)", DebugActionType.Action, () => { }) };
            return Gatherings.Lines(pawn).Select(line => new DebugActionNode(line.label, DebugActionType.Action, () =>
            {
                string result = MindActions.Safely(pawn, line.label, () => line.apply(pawn, "DEV: everyone, come along."));
                ModLog.Message($"DEV gathering for {pawn.LabelShort}: {line.key} | {result}; windows open: {string.Join(", ", Find.WindowStack.Windows.Select(w => w.GetType().Name))}");
                Report(result);
            })).ToList();
        }

        /// <summary>A kind laid out for the selected colonist as the Base call would (site A, code's size, the best wall material), with its missing materials marked. No LLM.</summary>
        [DebugAction(Category, "Build room now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> BuildRoomNow() =>
            DefDatabase<RoomKindDef>.AllDefsListForReading.Where(k => !k.layout && k.askedFor == null).Select(kind => new DebugActionNode(kind.LabelCap, DebugActionType.Action, () =>
            {
                if (SelectedColonist() is Pawn pawn)
                    ModLog.Message($"Build room now ({kind.label}): {PlaceRoom(pawn, kind, out _)}");
            })).ToList();

        /// <summary>As Build room now, at the site of the "apart" style (a building of its own, BASE_GROWTH.md §6.4), as a mind picking it would.</summary>
        [DebugAction(Category, "Build room apart", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> BuildRoomApart() =>
            DefDatabase<RoomKindDef>.AllDefsListForReading.Where(k => !k.layout && k.askedFor == null).Select(kind => new DebugActionNode(kind.LabelCap, DebugActionType.Action, () =>
            {
                if (!(SelectedColonist() is Pawn pawn))
                    return;
                var finder = new SiteFinder(pawn.Map, SiteFinder.BaseCenter(pawn.Map));
                finder.For(kind);
                ThingDef material = Supplies.WallMaterials(pawn)[0].stuff;
                var site = finder.Sites(new RoomValidator(pawn.Map, finder.center, finder.weights.maxWalk), material, out _).FirstOrDefault(s => s.apart);
                ModLog.Message($"Build room apart ({kind.label}): " + (site == null ? "no site apart"
                    : BaseCall.PlaceRoom(pawn, kind, BaseCall.SizeFor(kind, pawn.Map), site, material, out _)));
            })).ToList();

        internal static string PlaceRoom(Pawn pawn, RoomKindDef kind, out BuildProject project) =>
            BaseCall.PlaceRoom(pawn, kind, BaseCall.SizeFor(kind, pawn.Map), null, Supplies.WallMaterials(pawn)[0].stuff, out project);

        [DebugAction(Category, "Finish project instantly", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void FinishProjectAction()
        {
            if (SelectedColonist() is Pawn pawn && BuildManager.Instance.ActiveProject(pawn) is BuildProject project)
                FinishInstantly(project);
        }

        [DebugAction(Category, "Finish all projects instantly", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void FinishAllAction()
        {
            foreach (var project in BuildManager.Instance.ActiveOnAll(Find.CurrentMap).ToList())
                FinishInstantly(project);
        }

        [DebugAction(Category, "Cancel project", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CancelProjectAction()
        {
            if (SelectedColonist() is Pawn pawn && BuildManager.Instance.ActiveProject(pawn) is BuildProject project)
                Cancel(project);
        }

        /// <summary>
        /// God-mode build of every entry plus a roof, with the ground cleared first as colonists would (plants cut, items
        /// hauled off), then tracking passes, so the project reaches Done (and gets outfitted) at once.
        /// </summary>
        public static void FinishInstantly(BuildProject project)
        {
            Map map = project.map;
            if (project.closeDoor.IsValid)
                project.closeDoor.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
            if (!project.roomCell.IsValid)
                foreach (var c in project.footprint.ContractedBy(1))
                    foreach (var t in c.GetThingList(map).ToList())
                        if (t.def.category == ThingCategory.Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth)
                            t.Destroy(DestroyMode.Vanish);
            // Floor blueprints the room came with (a throne room "all floored", BASE_GROWTH.md §6.6).
            if (!project.roomCell.IsValid)
                foreach (var c in project.footprint.ContractedBy(1))
                    foreach (var t in c.GetThingList(map).ToList())
                        if (t is Blueprint && t.def.entityDefToBuild is TerrainDef laid)
                        {
                            t.Destroy(DestroyMode.Vanish);
                            map.terrainGrid.SetTerrain(c, laid);
                        }
            foreach (var e in project.entries)
            {
                foreach (var c in e.Rect)
                    foreach (var t in c.GetThingList(map).ToList())
                        if (t is Blueprint || t is Frame)
                            t.Destroy(DestroyMode.Vanish);
                if (project.Built(e))
                    continue;
                Thing thing = ThingMaker.MakeThing(e.def, e.stuff);
                thing.SetFactionDirect(Faction.OfPlayer);
                if (e.precept != null)
                    thing.StyleSourcePrecept = e.precept;
                GenSpawn.Spawn(thing, e.cell, map, e.rot, WipeMode.Vanish);
            }
            if (project.floor != null)
                foreach (var c in project.floorCells)
                {
                    foreach (var t in c.GetThingList(map).ToList())
                        if ((t is Blueprint || t is Frame) && t.def.entityDefToBuild == project.floor)
                            t.Destroy(DestroyMode.Vanish);
                    map.terrainGrid.SetTerrain(c, project.floor);
                }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Room room = project.Room;
            // A layout project (a doorway, a closed door) has no box of its own: roof the room it opens or closes.
            var roofed = project.roomCell.IsValid
                ? (room != null && Ground.Indoor(room) ? room.Cells : Enumerable.Empty<IntVec3>()) // never a pocket of open sky
                : project.footprint.ContractedBy(1).Cells;
            foreach (var c in roofed.ToList())
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            project.Tick();
            project.Tick(); // the first pass claims the bed, the second sees it claimed
            ModLog.Message($"Finished {project.pawn.LabelShort}'s {project.Kind} instantly: {project.state}.");
        }

        public static void Cancel(BuildProject project)
        {
            foreach (var e in project.entries)
                foreach (var t in e.cell.GetThingList(project.map).ToList())
                    if (t is Blueprint)
                        t.Destroy(DestroyMode.Cancel);
            project.state = BuildProject.State.Abandoned;
            ModLog.Message($"Cancelled {project.pawn.LabelShort}'s {project.Kind} (dev, no memory event).");
        }

        /// <summary>The Base call's layout lines for this map now, with the materials a mind would have.</summary>
        internal static List<Layout.Option> LayoutLines(Pawn pawn, Ladder.Rung rung = null)
        {
            Map map = pawn.Map;
            var finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
            var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
            return Layout.Options(map, rung, finder, validator, Supplies.WallMaterials(pawn).Select(m => m.stuff).ToList());
        }

        /// <summary>Applies a layout line for the pawn; the project it placed, or null (it placed nothing).</summary>
        internal static string PlaceLine(Pawn pawn, Layout.Option option, out BuildProject project)
        {
            var before = new HashSet<BuildProject>(BuildManager.Instance.ProjectsOf(pawn));
            string result = option.apply(pawn, Supplies.WallMaterials(pawn)[0].stuff);
            project = BuildManager.Instance.ProjectsOf(pawn).LastOrDefault(p => p.Active && !before.Contains(p));
            return result;
        }

        /// <summary>The best layout line placed for the first colonist and finished at once. Null if there's none.</summary>
        internal static string BuildBestLayoutLine(Map map)
        {
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            var option = pawn != null ? LayoutLines(pawn).FirstOrDefault() : null;
            if (option == null)
                return null;
            string result = PlaceLine(pawn, option, out BuildProject project);
            if (project != null)
                FinishInstantly(project);
            return $"{option.label}: {result}";
        }

        [DebugAction(Category, "Build best layout line now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BuildBestLayoutAction() => ModLog.Message("Build best layout line: " + (BuildBestLayoutLine(Find.CurrentMap) ?? "none."));

        /// <summary>
        /// The ladder's rooms for the first colonist, each at site A and finished at once: a base the way the minds grow one, in
        /// seconds. With layout, the rung is taken as a hub when one fits, and every layout line is built after each room.
        /// </summary>
        [DebugAction(Category, "Grow test base", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> GrowTestBaseAction() => new List<DebugActionNode>
        {
            new DebugActionNode("rooms only", DebugActionType.Action, () => GrowTestBase(Find.CurrentMap, false)),
            new DebugActionNode("with layout", DebugActionType.Action, () => GrowTestBase(Find.CurrentMap, true)),
        };

        public static string GrowTestBase(Map map, bool layout)
        {
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (pawn == null)
                return "No colonist.";
            // The ladder's own rungs in turn (BASE_GROWTH.md §6.6), until better rooms or a rung that waits.
            int built = 0;
            for (int step = 0; step < 20; step++)
            {
                var rung = Ladder.Current(map);
                if (rung == null || rung.rooms || rung.kind == null || rung.waiting != null)
                {
                    ModLog.Message($"Grow: stopped at {(rung == null ? "the top of the ladder" : rung.label + (rung.waiting != null ? $" ({rung.waiting})" : ""))}.");
                    break;
                }
                var kind = rung.kind;
                BuildProject project = null;
                // As a mind picking the rung as a hub when it can be walked through and one fits.
                if (layout && LayoutLines(pawn, new Ladder.Rung { kind = kind, label = kind.label }).FirstOrDefault(o => o.group == "Next for the base") is Layout.Option asHub)
                {
                    ModLog.Message($"Grow: {asHub.label}: {PlaceLine(pawn, asHub, out project)}");
                }
                if (project == null)
                    ModLog.Message($"Grow: {rung.label}: {PlaceRoom(pawn, kind, out project)}");
                if (project == null)
                    break;
                FinishInstantly(project);
                built++;
                // As if a mind took every layout line: halls, surplus ways out, doors between neighbours.
                for (int i = 0; layout && i < 10 && BuildBestLayoutLine(map) is string line; i++)
                    ModLog.Message("Grow: " + line);
            }
            // Then everything the game asks for (BASE_GROWTH.md §6.6), as if a mind took each.
            foreach (var ask in AskedFor.Current(map))
            {
                ModLog.Message($"Grow: asked for ({ask.why}): {BaseCall.PlaceRoom(pawn, ask.kind, AskedFor.SizeFor(ask, map), null, Supplies.WallMaterials(pawn)[0].stuff, out BuildProject asked, ask)}");
                if (asked != null)
                {
                    FinishInstantly(asked);
                    built++;
                }
            }
            string result = $"Grew a test base: {built} rooms, {Layout.Line(map)}.";
            ModLog.Message(result);
            BaseMap.Write(map);
            return result;
        }

        // ---------- Chores ----------

        /// <summary>The most useful option of one kind for the selected colonist, ignoring cooldowns and caps. No LLM.</summary>
        [DebugAction(Category, "Apply chore", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> ApplyChore() =>
            ((Chore.Kind[])Enum.GetValues(typeof(Chore.Kind))).Select(kind => new DebugActionNode(kind.ToString(), DebugActionType.Action, () =>
            {
                if (SelectedColonist() is Pawn pawn)
                    ModLog.Message($"Apply chore {kind} for {pawn.LabelShort}: {ApplyChore(pawn, kind)}");
            })).ToList();

        public static string ApplyChore(Pawn pawn, Chore.Kind kind)
        {
            Map map = pawn.Map;
            switch (kind)
            {
                case Chore.Kind.Field:
                    var outlook = FoodOutlook.For(map);
                    if (outlook.crop == null)
                        return "no crop can be sown here now";
                    return Fields.FindSite(pawn, outlook.cellsWanted > 0 ? outlook.FieldSide : 6, out CellRect field, out string fieldWhere, out _)
                        ? Fields.Place(pawn, field, outlook.crop, fieldWhere) : "no free soil for a field";
                case Chore.Kind.Stockpile:
                    return Stockpiles.FindSite(pawn, out CellRect pile, out string pileWhere) ? Stockpiles.Place(pawn, pile, pileWhere) : "no spot for a stockpile";
                default:
                    var option = ChoreOptions.All(pawn, ignoreLimits: true).Where(o => o.kind == kind).OrderByDescending(o => o.useful).FirstOrDefault();
                    return option == null ? "no option" : $"{option.label} → {option.apply(MindOrStandIn(pawn))}";
            }
        }

        /// <summary>What the selected colonist marked, done at once (animals killed, plants harvested, rock mined), so the chores close and she remembers it.</summary>
        [DebugAction(Category, "Finish my chores instantly", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void FinishChoresAction()
        {
            if (SelectedColonist() is Pawn pawn)
                ModLog.Message($"Finish chores for {pawn.LabelShort}: {FinishChores(pawn)}");
        }

        public static string FinishChores(Pawn pawn)
        {
            var results = new List<string>();
            foreach (var chore in ChoreManager.Instance.ActiveOf(pawn).ToList())
            {
                foreach (var t in chore.targets.ToList())
                {
                    if (t.Thing is Pawn animal && !animal.Dead)
                        animal.Kill(null);
                    else if (t.Thing is Plant plant && plant.Spawned)
                    {
                        plant.PlantCollected(pawn, PlantDestructionMode.Cut);
                        pawn.Map.designationManager.RemoveAllDesignationsOn(plant); // a bush regrows: the harvest job would clear its mark
                    }
                    else if (!t.HasThing && t.Cell.GetFirstMineable(pawn.Map) is Mineable rock)
                        rock.DestroyMined(pawn);
                }
                chore.Tick();
                results.Add($"{chore.kind} ({chore.label}): {chore.state}");
            }
            return results.Count > 0 ? string.Join(", ", results) : "nothing marked";
        }

        /// <summary>Removes the selected colonist's chores the way the player would (designations, bills, zones), to test the veto.</summary>
        [DebugAction(Category, "Player removes my chores", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PlayerRemovesChores()
        {
            Pawn pawn = SelectedColonist();
            if (pawn == null)
                return;
            var dm = pawn.Map.designationManager;
            foreach (var chore in ChoreManager.Instance.ActiveOf(pawn).ToList())
            {
                foreach (var t in chore.targets)
                    if (t.HasThing)
                        dm.RemoveAllDesignationsOn(t.Thing);
                    else
                        dm.TryRemoveDesignation(t.Cell, chore.Designation);
                if (chore.bill != null && !chore.bill.DeletedOrDereferenced)
                    chore.bill.billStack.Delete(chore.bill);
                if (chore.zone != null && chore.zone.cells.Count > 0)
                    chore.zone.Delete();
                ModLog.Message($"DEV: the player removed {pawn.LabelShort}'s {chore.kind} ({chore.label}).");
            }
        }

        // ---------- Setup ----------

        [DebugAction(Category, "Build fixture", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> BuildFixture() => new List<DebugActionNode>
        {
            new DebugActionNode("mixed ground", DebugActionType.Action, () => Report(Fixtures.MixedGround(Find.CurrentMap))),
            new DebugActionNode("street base", DebugActionType.Action, () => Report(Fixtures.StreetBase(Find.CurrentMap))),
            new DebugActionNode("fields at the doors", DebugActionType.Action, () => Report(Fixtures.FieldsAtTheDoors(Find.CurrentMap))),
        };

        /// <summary>Colony conditions the ladder and the rooms asked for react to (BASE_GROWTH.md build checks).</summary>
        [DebugAction(Category, "Test setup", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> TestSetup() => new List<DebugActionNode>
        {
            new DebugActionNode("add 3 colonists", DebugActionType.Action, () => ModLog.Message(Fixtures.AddColonists(Find.CurrentMap, 3))),
            new DebugActionNode("add power", DebugActionType.Action, () => ModLog.Message(Fixtures.AddPower(Find.CurrentMap))),
            new DebugActionNode("grant a title", DebugActionType.Action, () => ModLog.Message(Fixtures.AddCondition(Find.CurrentMap, "title"))),
            new DebugActionNode("take a prisoner", DebugActionType.Action, () => ModLog.Message(Fixtures.AddCondition(Find.CurrentMap, "prisoner"))),
            new DebugActionNode("add a baby", DebugActionType.Action, () => ModLog.Message(Fixtures.AddCondition(Find.CurrentMap, "baby"))),
            new DebugActionNode("add a deathrester", DebugActionType.Action, () => ModLog.Message(Fixtures.AddCondition(Find.CurrentMap, "deathrest"))),
        };

        // ---------- Reports ----------

        /// <summary>
        /// Everything the Base call reads, for checking by hand, in base-report.txt: the ladder, the food outlook, the ways out
        /// and layout lines, every stock-up option with its check, and every project's status. No LLM.
        /// </summary>
        [DebugAction(Category, "Base report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BaseReport()
        {
            Map map = Find.CurrentMap;
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            var sb = new StringBuilder($"Base report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, for {pawn?.LabelShort ?? "nobody"}\n");
            sb.AppendLine("\n== Ladder");
            foreach (var r in Ladder.Evaluate(map))
                sb.AppendLine($"  {r.label} ({r.kind?.label ?? "-"}): {(r.met ? "met" : r.underway ? "underway" : "NOT MET")}{(r.waiting != null ? " | " + r.waiting : "")}");
            sb.AppendLine("  [Colony] Base: " + Ladder.Line(map));

            var o = FoodOutlook.For(map);
            sb.AppendLine($"\n== Food: {o.colonists} colonists, need {o.needPerDay:0.00}/day, fields grow {o.growPerDay:0.00}/day, stores {o.stores:0}");
            sb.AppendLine($"  crop for a new field: {o.crop?.label ?? "none"} (in season: {o.cropInSeason}), winter: {(o.hasWinter ? $"in {o.daysToWinter} days for {o.winterDays}, cover {o.WinterCover:0}" : "none")}");
            sb.AppendLine($"  unsown field: {o.unsownField}, a new field: {o.cellsWanted} cells");
            sb.AppendLine("  [Colony] Food: " + o.Line());

            sb.AppendLine($"\n== Layout: {Layout.Line(map)}");
            foreach (var way in Layout.WaysOut(map))
                sb.AppendLine($"  way out {way.door.Position} ({Layout.Name(way.room)}): reaches inside {Layout.ReachesInside(way.room)}, ours {BuildManager.Instance.OurDoor(map, way.door.Position)}");
            if (pawn != null)
                foreach (var line in LayoutLines(pawn, Ladder.Current(map)))
                    sb.AppendLine($"  [{line.group}] {line.label}");

            if (pawn != null)
            {
                sb.AppendLine($"\n== Stock-up options for {pawn.LabelShort} (limits ignored)");
                foreach (var c in ChoreOptions.All(pawn, ignoreLimits: true))
                    sb.AppendLine($"  [{c.kind}] {c.useful:0.0} {c.label} | check: {c.check() ?? "ok"}");
                foreach (Chore.Kind kind in Enum.GetValues(typeof(Chore.Kind)))
                    sb.AppendLine($"  {kind}: {ChoreManager.Instance?.CantReason(pawn, kind) ?? "may start"}");
                sb.AppendLine("  [Colony work] " + ColonyWork.Line(pawn));
            }

            sb.AppendLine("\n== Projects");
            foreach (var p in BuildManager.Instance.ActiveOnAll(map))
                sb.AppendLine($"  {p.pawn.LabelShort}: {p.StatusLine()}");
            WriteFile("base-report.txt", sb.ToString());
            ModLog.Message($"Base report written to {Path.Combine(Folder, "base-report.txt")}.");
        }
    }
}
