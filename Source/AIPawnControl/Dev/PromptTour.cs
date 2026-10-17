using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The prompt tour (docs/test-scripts/prompt_tour.py): launched with -aipcPromptTour on a quicktest colony, every colonist
    /// gets a mind, and the first one goes through the calls of three world states in turn, with each call's real prompt and
    /// schema: the fresh landing, then settled (the test base grown, a joiner asking to join), then a raid. The replies are
    /// shown, not applied; only Persona (the system prompt needs it) and the message thread's lines (the other's prompt reads
    /// them) are kept. Upgrade and Answer are forced when nothing leads to them. With -aipcTourSave=&lt;save&gt; instead, it
    /// loads that save and runs every call once on it as it stands. Writes tours/&lt;time&gt;/tour.json, then closes the game.
    /// </summary>
    public class PromptTour : GameComponent
    {
        private const int MaxThreadLines = 4;
        private const string PlayerLine = "How are you doing?";
        private const string AskOutLine = "Want to watch the stars with me tonight?";

        public PromptTour(Game game) { }

        public override void StartedNewGame()
        {
            if (GenCommandLine.CommandLineArgPassed("aipcPromptTour"))
                LongEventHandler.ExecuteWhenFinished(() => new Run().Start(null));
        }

        public override void LoadedGame()
        {
            if (GenCommandLine.TryGetCommandLineArg("aipcTourSave", out string save))
                LongEventHandler.ExecuteWhenFinished(() => new Run().Start(save));
        }

        /// <summary>Loads the -aipcTourSave save once the game has started up.</summary>
        [StaticConstructorOnStartup]
        private static class LoadSave
        {
            static LoadSave()
            {
                if (GenCommandLine.TryGetCommandLineArg("aipcTourSave", out string save))
                    LongEventHandler.ExecuteWhenFinished(() => LongEventHandler.QueueLongEvent(() =>
                    {
                        // GameDataSaveLoader.LoadGame(string), which can't be named here: its FileInfo overload needs .NET 10
                        Verse.Profile.MemoryUtility.ClearAllMapsAndWorld();
                        Current.Game = new Game { InitData = new GameInitData { gameToLoad = save } };
                    }, "Play", "LoadingLongEvent", true, null));
            }
        }

        private class Run
        {
            private readonly List<object> states = new List<object>();
            private List<object> calls;
            private readonly List<Func<bool>> steps = new List<Func<bool>>(); // each returns whether it sent a call
            private List<PawnMind> minds;
            private PawnMind mind;
            private string actKey;
            private Pawn talkTo;
            private bool answerChoices;

            private Pawn Pawn => mind.pawn;
            private Map Map => mind.pawn.Map;

            public void Start(string save)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused; // the minds' own triggers only run on ticks
                answerChoices = AIPawnControlMod.Settings.answerChoices;
                AIPawnControlMod.Settings.answerChoices = false;  // the tour sends Decide itself
                foreach (var p in Find.CurrentMap.mapPawns.FreeColonistsSpawned.Where(p => p != MindManager.Instance.PlayingAs).ToList())
                    MindManager.Instance.Enable(p, null);
                minds = MindManager.Instance.Minds;
                mind = minds.First(m => m.pawn.Map == Find.CurrentMap);

                if (save != null)
                {
                    steps.Add(() => State("Save", () => save + " as it stands"));
                    foreach (var m in minds.Where(m => m.persona.NullOrEmpty()))
                        steps.Add(() => Persona(m));
                    steps.Add(() => Act("A few hours have passed."));
                    steps.Add(Chat);
                    steps.Add(() => Send("post", null, GroupChat.PostMessages(mind), GroupChat.PostSchema(), null));
                    steps.Add(() => Base(forceUpgrade: true));
                    steps.Add(Thread);
                    steps.Add(Reflect);
                    steps.Add(Decide);
                    steps.Add(Answer);
                    steps.Add(Plan);
                    Next();
                    return;
                }

                steps.Add(() => State("Fresh landing", () => "the quicktest colony as it starts"));
                foreach (var m in minds)
                    steps.Add(() => Persona(m));
                steps.Add(() => Act("A few hours have passed."));
                steps.Add(Chat);
                steps.Add(() => Send("post", null, GroupChat.PostMessages(mind), GroupChat.PostSchema(), null));
                steps.Add(() => Base(forceUpgrade: false));
                steps.Add(Thread);
                steps.Add(Reflect); // after the thread: its lines are the events

                steps.Add(() => State("Settled", Settle));
                steps.Add(() => Act("A few hours have passed."));
                steps.Add(() => Base(forceUpgrade: true));
                steps.Add(Decide);
                steps.Add(Answer);

                steps.Add(() => State("Raid", Raid));
                steps.Add(() => Act("Something important just changed."));
                steps.Add(Plan);
                Next();
            }

            /// <summary>Starts the next world state: sets it up, and its calls go in its own section.</summary>
            private bool State(string name, Func<string> setup)
            {
                calls = new List<object>();
                string how;
                try
                {
                    how = setup();
                }
                catch (Exception e)
                {
                    how = "setup threw: " + e.Message;
                    ModLog.Error("Prompt tour: setting up " + name + ": " + e);
                }
                states.Add(new Dictionary<string, object> { ["name"] = name, ["setup"] = how, ["calls"] = calls });
                ModLog.Message($"Prompt tour: state {name}: {how}");
                return false;
            }

            private string Settle()
            {
                string grown = DevTools.GrowTestBase(Map, false);
                var joiner = DefDatabase<IncidentDef>.GetNamed("WandererJoin");
                bool joined = joiner.Worker.TryExecute(StorytellerUtility.DefaultParmsNow(joiner.category, Map));
                return $"{grown} {(joined ? "A wanderer asks to join." : "The wanderer-joins incident didn't fire.")}";
            }

            private string Raid()
            {
                var faction = Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
                if (faction == null)
                    return "no enemy faction";
                if (!CellFinder.TryFindRandomCellNear(Pawn.Position, Map, 25, c => c.Standable(Map) && !c.Fogged(Map) && c.DistanceTo(Pawn.Position) > 12, out IntVec3 cell))
                    return "no cell to spawn the enemy on";
                Pawn enemy = PawnGenerator.GeneratePawn(new PawnGenerationRequest(faction.RandomPawnKind(), faction));
                GenSpawn.Spawn(enemy, cell, Map);
                return $"a {enemy.KindLabel} of {faction.Name} spawned {(int)cell.DistanceTo(Pawn.Position)} cells from {Pawn.LabelShort}";
            }

            private bool Persona(PawnMind m) =>
                Send("persona", m.pawn.LabelShort, m.PersonaMessages(), ActionCatalog.PersonaSchema(), reply => m.persona = reply?.Str("persona")?.Trim());

            private bool Act(string trigger)
            {
                var menu = ActionCatalog.BuildActMenu(Pawn, mind);
                var recall = Recall.Act(mind, Recall.Present(Pawn), null, null, peek: true);
                return Send("act", $"trigger: \"{trigger}\"", mind.ActMessages(trigger, menu, recall), ActionCatalog.ActSchema(menu, recall.Shown), reply =>
                {
                    var picked = reply == null ? null : menu.FirstOrDefault(o => o.Id == reply.Int("choice", -1));
                    actKey = picked?.Key;
                    talkTo = picked?.TalkTargets?.FirstOrDefault(p => p.LabelShort == reply.Str("with"));
                });
            }

            private bool Chat()
            {
                string speaker = MindManager.Speaker;
                var menu = ActionCatalog.BuildActMenu(Pawn, mind, inConversation: true);
                var recall = Recall.Conversation(mind, speaker, null, null, 5, peek: true);
                return Send("chat", $"{speaker}: \"{PlayerLine}\"",
                    mind.ChatMessages(speaker, $"You're talking with {speaker}.", new List<string>(), PlayerLine, ActionCatalog.DescribeMenu(menu), recall),
                    ActionCatalog.ChatSchema(menu, recall.Shown), null);
            }

            /// <summary>The Base call, then Upgrade when it picks one. Forced: the first room that can be upgraded when it doesn't.</summary>
            private bool Base(bool forceUpgrade)
            {
                var call = BaseCall.Prepare(mind, dev: false);
                if (call.Count == 0)
                {
                    Skip("base", "nothing to do for the base");
                    return Upgrade(null, forceUpgrade);
                }
                return Send("base", actKey == "base" ? "Act picked \"work on the base\"" : null, call.messages, call.schema, reply =>
                {
                    int pick = reply?.Int("choice", -1) ?? -1;
                    var room = pick >= 1 && pick <= call.Count ? call.choices[pick - 1].upgrade : null;
                    steps.Insert(0, () => Upgrade(room, forceUpgrade));
                });
            }

            private bool Upgrade(Room picked, bool force)
            {
                var room = picked ?? (force ? Upgrades.Rooms(Pawn).FirstOrDefault(r => Upgrades.For(r, Pawn).Count > 0) : null);
                if (room == null)
                    return Skip("upgrade", force ? "no room can be upgraded" : "the Base call didn't pick an upgrade");
                var upgrade = UpgradeCall.Prepare(mind, room);
                if (upgrade.upgrades == null)
                    return Skip("upgrade", "nothing to add to that room");
                string name = SnapshotBuilder.RoomName(room, Pawn);
                return Send("upgrade", picked != null ? "Base picked " + name : "forced: " + name, upgrade.messages, upgrade.schema, null);
            }

            /// <summary>The first open choice, by the mind ColonyChoices would ask.</summary>
            private bool Decide()
            {
                var choice = ColonyChoices.All().FirstOrDefault();
                if (choice == null)
                    return Skip("decide", "no open colony choice");
                var decider = ColonyChoices.Decider(minds, choice) ?? mind;
                var options = choice.Options(decider.pawn);
                if (options.Count < 2)
                    return Skip("decide", $"{choice.key} has fewer than 2 options");
                return Send("decide", $"{choice.key}, decided by {decider.pawn.LabelShort}", ColonyChoices.DecideMessages(decider, choice, options), ColonyChoices.DecideSchema(options), null);
            }

            /// <summary>Forced: the second mind asks this one out, with a line, as PawnMind.RequestAnswer builds it.</summary>
            private bool Answer()
            {
                var asker = minds.FirstOrDefault(m => m != mind)?.pawn;
                if (asker == null)
                    return Skip("answer", "nobody to ask");
                string speaker = asker.LabelShort;
                var recall = Recall.Conversation(mind, speaker, null, null, 3, peek: true);
                return Send("answer", $"forced: {speaker} asks {Pawn.LabelShort} out",
                    mind.AnswerMessages(speaker, $"{speaker} just came over to talk to you ({InteractionDefOf.RomanceAttempt.label}).", AskOutLine, recall),
                    PawnMind.AnswerSchema(recall.Shown), null);
            }

            /// <summary>The danger plan, by the leader if there is one, as DangerPlan.Waiting picks the caller.</summary>
            private bool Plan()
            {
                var plan = new DangerPlan.Plan { map = Map };
                DangerPlan.FindEdge(plan);
                var candidates = DangerPlan.Candidates(Map);
                var options = DangerPlan.Options(plan, candidates);
                if (options.Count < 2 && !DangerResponse.CanGoInside(Map))
                    return Skip("plan", "nothing to pick: only \"No fighting\" and nowhere inside");
                var leader = minds.FirstOrDefault(m => Customs.RoleOf(m.pawn)?.def.leaderRole == true);
                var caller = leader ?? mind;
                return Send("plan", $"by {caller.pawn.LabelShort} ({(leader != null ? "the leader" : "first to react")})",
                    DangerPlan.PlanMessages(caller, plan, leader != null, candidates, options), DangerPlan.PlanSchema(plan, candidates, options), null);
            }

            /// <summary>Starts a message thread with the colonist Act picked, or the first one it can talk to.</summary>
            private bool Thread()
            {
                var other = talkTo ?? ActionCatalog.TalkTargets(Pawn, mind).FirstOrDefault();
                return other == null ? Skip("message", "nobody to talk to") : Message(mind, MindManager.Instance.MindOf(other), started: true, line: 1);
            }

            /// <summary>
            /// One line of the thread, as PawnMind.RequestMessage builds it. A line written goes into the thread and both minds'
            /// events, and the other answers next.
            /// </summary>
            private bool Message(PawnMind from, PawnMind to, bool started, int line)
            {
                Pawn pawn = from.pawn, other = to.pawn;
                string name = other.LabelShort;
                var menu = ActionCatalog.BuildActMenu(pawn, from, inConversation: true, talkingTo: other);
                var thread = DirectMessages.Instance?.Thread(pawn, other, create: false);
                var unread = thread?.UnreadBy(pawn) ?? new List<DmLine>();
                int readUpTo = thread?.LastId ?? 0;
                string situation = started ? $"You decided to talk to {name}." : $"{name} is talking to you.";
                string unreadText = string.Join("\n", unread.Select(l => thread.Line(l, pawn, pawn.Map)));
                Recall.ChatQuery(from, unread.Count > 0 ? string.Join(" ", unread.Select(l => l.text)) : name, out var mentioned);
                var recall = Recall.Conversation(from, name, null, null, 3, mentioned, peek: true);
                string note = $"{pawn.LabelShort} to {name}" + (started && actKey == "talk" ? " (Act picked \"talk to someone\")" : "");
                return Send("message", note, from.MessageMessages(other, situation, DirectMessages.Full(thread, pawn, pawn.Map), unreadText, ActionCatalog.DescribeMenu(menu), recall),
                    PawnMind.MessageSchema(menu, recall.Shown), reply =>
                    {
                        DirectMessages.Instance?.Thread(pawn, other, create: false)?.MarkRead(pawn, readUpTo);
                        string text = reply == null ? null : SpeechLog.Clean(reply.Str("message"), ActionCatalog.MaxChatReply);
                        if (text == null)
                            return;
                        DirectMessages.Instance?.Add(pawn, other, text);
                        from.memory.Record(pawn, "dm", null, $"I told {name}: \"{text}\"", 3, MemoryEvent.TookPart, new[] { name });
                        to.memory.Record(other, "dm", null, $"{pawn.LabelShort} told me: \"{text}\"", 3, MemoryEvent.Told, new[] { pawn.LabelShort });
                        if (line < MaxThreadLines)
                            steps.Insert(0, () => Message(to, from, started: false, line + 1));
                    });
            }

            private bool Reflect()
            {
                var reflection = Reflection.Prepare(mind, dev: true);
                if (reflection == null)
                    return Skip("reflect", "nothing has happened to reflect on");
                reflection.PickClosest(null, null);
                return Send("reflect", null, PromptBuilder.Build("reflect", mind, reflection.PromptValues()), reflection.ReplySchema(), null, PawnMind.ReflectTokens);
            }

            /// <summary>Sends one call. Its reply is recorded and handed to then (null on an error), and the tour goes on.</summary>
            private bool Send(string call, string note, List<KeyValuePair<string, string>> messages, Dictionary<string, object> schema,
                              Action<Dictionary<string, object>> then, int maxTokens = 0)
            {
                ModLog.Message($"Prompt tour: {call}{(note != null ? $" ({note})" : "")}");
                var into = calls;
                LlmClient.Send(call, messages, schema, maxTokens, result =>
                {
                    Dictionary<string, object> reply = null;
                    string error = result.Error;
                    if (error == null)
                    {
                        try
                        {
                            reply = (Dictionary<string, object>)Json.Parse(result.Content);
                        }
                        catch (Exception e)
                        {
                            error = "reply wasn't valid JSON: " + e.Message;
                        }
                    }
                    into.Add(new Dictionary<string, object>
                    {
                        ["call"] = call,
                        ["note"] = note,
                        ["messages"] = messages.Select(m => new Dictionary<string, object> { ["role"] = m.Key, ["content"] = m.Value }).ToList(),
                        ["schema"] = schema,
                        ["reply"] = result.Content,
                        ["reasoning"] = result.Reasoning,
                        ["error"] = error,
                        ["seconds"] = Math.Round(result.Seconds, 1),
                        ["tokens"] = result.CompletionTokens,
                    });
                    try
                    {
                        then?.Invoke(reply);
                    }
                    catch (Exception e)
                    {
                        ModLog.Error($"Prompt tour: after {call}: {e}");
                    }
                    Next();
                });
                return true;
            }

            private bool Skip(string call, string why)
            {
                calls.Add(new Dictionary<string, object> { ["call"] = call, ["skipped"] = why });
                return false;
            }

            /// <summary>Runs steps until one sends a call (its reply comes back here), or writes the tour when none are left.</summary>
            private void Next()
            {
                while (steps.Count > 0)
                {
                    var step = steps[0];
                    steps.RemoveAt(0);
                    try
                    {
                        if (step())
                            return;
                    }
                    catch (Exception e)
                    {
                        ModLog.Error("Prompt tour: a step threw: " + e);
                    }
                }
                Finish();
            }

            private void Finish()
            {
                var settings = AIPawnControlMod.Settings;
                settings.answerChoices = answerChoices;
                string folder = Path.Combine(DevTools.Folder, "tours", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "tour.json"), Json.Write(new Dictionary<string, object>
                {
                    ["colonist"] = Pawn.LabelShort,
                    ["model"] = settings.model,
                    ["endpoint"] = settings.endpoint,
                    ["temperature"] = settings.temperature,
                    ["thinking"] = settings.thinking,
                    ["states"] = states,
                }));
                ModLog.Message("Prompt tour written: " + folder);
                Root.Shutdown();
            }
        }
    }
}
