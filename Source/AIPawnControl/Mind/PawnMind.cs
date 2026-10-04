using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIPawnControl
{
    /// <summary>One AI-controlled pawn: saved state plus the think state machine (Idle → Waiting → Idle).</summary>
    public class PawnMind : IExposable
    {
        private const int MaxDecisions = 10;
        private const int FailuresBeforeBackoff = 3;
        private const int BackoffTicks = 2 * GenDate.TicksPerHour;
        private const int MinTicksBetweenThinks = GenDate.TicksPerHour;
        private const float MinRealSecondsBetweenThinks = 10f;
        private const int IdleTicksBeforeAct = GenDate.TicksPerHour;
        private const int MaxChatLines = 16; // 8 turns
        private const int ChatForgetTicks = 6 * GenDate.TicksPerHour;
        private const int ReflectTokens = 3000; // the reply is several fields long; a cut-off reply isn't valid JSON

        // Saved
        public Pawn pawn;
        public string persona;
        public string note;
        public string lastReason;
        public List<string> decisions = new List<string>();
        public List<int> decisionTicks = new List<int>(); // parallel to decisions, for the day labels in [Recent]
        private bool prioritiesSet; // work priorities from her passions, once (STREAMLINE.md §8)
        private int actsDay = -1;
        private int actsToday;
        private int lastThinkTick = -99999;
        private int wakeAtTick = -1;
        private Dictionary<string, int> lastInteractionTicks = new Dictionary<string, int>(); // "defName|targetThingId" → tick
        private int lastNegativeTick = -99999999;
        public List<ChatLine> chat = new List<ChatLine>();
        private int lastChatTick = -1;
        private bool chatPending; // unanswered player messages, e.g. while unconscious or while another call was running
        private bool chatWhileOut;
        public MindMemory memory = new MindMemory(); // events, memories, diary, person files, lately
        private bool keptSettings; // a danger choice changed her area and hostility response; they come back when it's over
        private Area areaBefore;
        private HostilityResponseMode responseBefore;

        // Not saved
        private LlmRequest current;
        private Game requestGame;
        private float requestStartedRealtime;
        private int failures;
        private int backoffUntilTick;
        private float lastThinkRealtime = -999f;
        private int idleSinceTick = -1;
        private int suppliesNight = -1; // not saved: after a reload her project's materials are checked once more
        private int lastDanger = -1;
        private int lastMoodBand;
        private int lastInjuryCount;
        private int dangerSinceTick = -1; // when the danger on her map began; -1 = none
        private readonly HashSet<Pawn> hitInDanger = new HashSet<Pawn>(); // colonists hurt since then: each one makes her think once
        private bool dangerThink; // a danger event she hasn't thought about yet
        private HashSet<int> ourJobIds = new HashSet<int>(); // jobs we ordered, to tell them apart from the player's (saved: a reload mid-job isn't a player order)
        private int talkLineJobId = -1;
        private string talkLine;
        private bool chatWokeHer;
        private bool chatThinking;
        private bool reflecting; // a Reflect is somewhere between its query embedding and its commit
        private int reflectRun;  // bumped to abandon a running Reflect
        private bool recalling;  // an Act, Chat or Reply is waiting for its situation's query vector

        public bool Thinking => current != null || reflecting || recalling;
        public float ThinkingSeconds => UnityEngine.Time.realtimeSinceStartup - requestStartedRealtime;
        public bool Unreachable => failures >= FailuresBeforeBackoff;
        public int ActsLeft => AIPawnControlMod.Settings.actsPerDay - (LocalDay == actsDay ? actsToday : 0);
        public bool ChatThinking => chatThinking && Thinking;
        public bool ChatWaitingForWake => chatPending && !CanBeAwake;
        private bool CanBeAwake => pawn.health.capacities.CanBeAwake;

        public PawnMind() { }

        public PawnMind(Pawn pawn, string note)
        {
            this.pawn = pawn;
            this.note = note;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref persona, "persona");
            Scribe_Values.Look(ref note, "note");
            Scribe_Values.Look(ref lastReason, "lastReason");
            Scribe_Collections.Look(ref decisions, "decisions", LookMode.Value);
            Scribe_Collections.Look(ref decisionTicks, "decisionTicks", LookMode.Value);
            Scribe_Values.Look(ref prioritiesSet, "prioritiesSet");
            Scribe_Values.Look(ref actsDay, "actsDay", -1);
            Scribe_Values.Look(ref actsToday, "actsToday");
            Scribe_Values.Look(ref lastThinkTick, "lastThinkTick", -99999);
            Scribe_Values.Look(ref wakeAtTick, "wakeAtTick", -1);
            Scribe_Collections.Look(ref lastInteractionTicks, "lastInteractionTicks", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref lastNegativeTick, "lastNegativeTick", -99999999);
            Scribe_Collections.Look(ref chat, "chat", LookMode.Deep);
            Scribe_Values.Look(ref lastChatTick, "lastChatTick", -1);
            Scribe_Values.Look(ref chatPending, "chatPending");
            Scribe_Values.Look(ref chatWhileOut, "chatWhileOut");
            Scribe_Deep.Look(ref memory, "memory");
            Scribe_Values.Look(ref keptSettings, "keptSettings");
            Scribe_References.Look(ref areaBefore, "areaBefore");
            Scribe_Values.Look(ref responseBefore, "responseBefore");
            if (Scribe.mode == LoadSaveMode.Saving && pawn?.jobs != null)
                ourJobIds.IntersectWith(pawn.jobs.AllJobs().Select(j => j.loadID)); // only the jobs she still has
            Scribe_Collections.Look(ref ourJobIds, "ourJobIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ourJobIds = ourJobIds ?? new HashSet<int>();
                decisions = decisions ?? new List<string>();
                decisionTicks = decisionTicks ?? new List<int>();
                lastInteractionTicks = lastInteractionTicks ?? new Dictionary<string, int>();
                chat = chat ?? new List<ChatLine>();
                memory = memory ?? new MindMemory();
            }
        }

        private int LocalDay => pawn.Map != null ? GameTime.Today(pawn.Map) : -1;

        /// <summary>Why the mind must not think right now, or null if it may. Checked before sending and again before applying.</summary>
        public string PausedReason(bool ignoreSleep = false)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead) return "gone";
            if (!pawn.Spawned || pawn.Map == null) return "not on a map";
            if (pawn.Downed) return "downed";
            if (pawn.InMentalState) return "in a mental state";
            if (pawn.Drafted) return "drafted";
            if (pawn.GetLord() != null) return "in a gathering or group activity";
            Job job = pawn.CurJob;
            if (job != null && job.playerForced && !ourJobIds.Contains(job.loadID)) return "following the player's order";
            if (!ignoreSleep && !pawn.Awake()) return "asleep";
            return null;
        }

        public void RememberOurJob(Job job) => ourJobIds.Add(job.loadID);

        /// <summary>The opening line for one talk job. Not saved: a reload mid-walk just falls back to vanilla text.</summary>
        public void SetTalkLine(Job job, string line)
        {
            talkLineJobId = job.loadID;
            talkLine = line;
        }

        public string TalkLine(Job job) => job.loadID == talkLineJobId ? talkLine : null;

        /// <summary>Called by MindManager every check interval.</summary>
        public void CheckTriggers()
        {
            if (chat.Count > 0 && !chatPending && Find.TickManager.TicksGame - lastChatTick > ChatForgetTicks)
                chat.Clear(); // long chat histories make small models drift; memory across conversations is Phase 3
            if (keptSettings && pawn != null && pawn.Spawned && DangerResponse.Threats(pawn.Map).Count == 0)
                EndDanger();
            if (chatPending)
                return; // answered from UpdateChat, which also runs while paused
            if (Thinking || Find.TickManager.TicksGame < backoffUntilTick || pawn == null || !pawn.Spawned)
                return;
            RecheckSupplies();
            if (TryReflect())
                return;
            bool danger = NoticeDanger();
            Change change = DetectSignificantChange();
            if (danger && change == Change.Urgent)
                change = Change.None; // in danger only its own events count (NoticeDanger), not every hit
            // Only danger or a new injury may wake a sleeper's mind; a mood drop while asleep just confuses the model.
            bool urgent = change == Change.Urgent || dangerThink;
            bool significant = urgent || (change == Change.Mood && pawn.Awake());
            if (PausedReason(ignoreSleep: urgent) != null)
            {
                idleSinceTick = -1;
                return;
            }
            if (persona == null)
            {
                RequestPersona();
                return;
            }
            if (!prioritiesSet)
            {
                prioritiesSet = true;
                if (!ActionCatalog.ManualPriorities)
                    WarnManualPrioritiesOff();
                string set = MindActions.SetPrioritiesFromPassions(pawn);
                AddDecision(set, importance: 0);
                ModLog.Message($"{pawn.LabelShort}: {set}");
            }
            if (dangerThink && DangerPlan.Instance?.Waiting(this) == true)
                return; // the colony's plan comes first (DANGER_RESPONSE.md §9): she makes it, or waits for it

            int now = Find.TickManager.TicksGame;
            bool idle = pawn.CurJob == null || pawn.CurJob.def.isIdle || pawn.mindState.IsIdle;
            if (!idle)
                idleSinceTick = -1;
            else if (idleSinceTick < 0)
                idleSinceTick = now;

            if ((now - lastThinkTick < MinTicksBetweenThinks && !dangerThink) || UnityEngine.Time.realtimeSinceStartup - lastThinkRealtime < MinRealSecondsBetweenThinks)
                return;
            string trigger = null;
            if (significant)
                trigger = "Something important just changed.";
            else if (wakeAtTick >= 0 && now >= wakeAtTick)
                trigger = "It's time to check in, like I planned.";
            else if (wakeAtTick < 0 && now - lastThinkTick >= AIPawnControlMod.Settings.periodicHours * GenDate.TicksPerHour)
                trigger = "A few hours have passed.";
            else if (idleSinceTick >= 0 && now - idleSinceTick >= IdleTicksBeforeAct)
                trigger = "I've been idle for a while.";
            if (trigger == null || (ActsLeft <= 0 && !danger))
                return;
            RequestAct(trigger, countBudget: !danger); // in danger a choice costs no decision
        }

        private enum Change { None, Mood, Urgent }

        /// <summary>Compares a few numbers with the last check: danger up or a new injury (urgent), or mood band worse.</summary>
        private Change DetectSignificantChange()
        {
            int danger = (int)pawn.Map.dangerWatcher.DangerRating;
            int moodBand = MoodBand();
            int injuries = pawn.health.hediffSet.hediffs.Count(h => h.Visible && h.def.isBad);
            bool first = lastDanger < 0;
            Change change = Change.None;
            if (!first && (danger > lastDanger || injuries > lastInjuryCount))
                change = Change.Urgent;
            else if (!first && moodBand > lastMoodBand)
                change = Change.Mood;
            lastDanger = danger;
            lastMoodBand = moodBand;
            lastInjuryCount = injuries;
            return change;
        }

        /// <summary>
        /// While there's danger, she thinks again (at once, past the hourly gap) only when it starts and the first time each
        /// colonist anywhere on the map is hurt, herself included. Returns whether there's danger.
        /// </summary>
        private bool NoticeDanger()
        {
            if (DangerResponse.Threats(pawn.Map).Count == 0)
            {
                dangerSinceTick = -1;
                hitInDanger.Clear();
                dangerThink = false;
                return false;
            }
            if (dangerSinceTick < 0)
            {
                dangerSinceTick = Find.TickManager.TicksGame;
                dangerThink = true;
            }
            foreach (var p in pawn.Map.mapPawns.FreeColonistsSpawned)
                if (p.mindState.lastHarmTick >= dangerSinceTick && hitInDanger.Add(p))
                    dangerThink = true;
            return true;
        }

        private int MoodBand()
        {
            var mood = pawn.needs?.mood;
            if (mood == null) return 0;
            var breaker = pawn.mindState.mentalBreaker;
            if (mood.CurLevel < breaker.BreakThresholdExtreme) return 3;
            if (mood.CurLevel < breaker.BreakThresholdMajor) return 2;
            if (mood.CurLevel < breaker.BreakThresholdMinor) return 1;
            return 0;
        }

        /// <summary>Dev "Think now": an Act call that ignores cooldowns and budgets.</summary>
        public bool ThinkNow()
        {
            if (Thinking || persona == null || PausedReason(ignoreSleep: true) != null)
                return false;
            RequestAct("The player asked me to think about what to do.", countBudget: false);
            return true;
        }

        /// <summary>
        /// Code-side anti-spam: normal interactions once per target every 4 hours, negative ones once per day in total,
        /// life-changing ones (romance, proposal, breakup) once per target per week.
        /// </summary>
        public bool OnCooldown(InteractionDef def, Pawn target)
        {
            int now = Find.TickManager.TicksGame;
            if (ActionCatalog.IsNegative(def) && now - lastNegativeTick < GenDate.TicksPerDay)
                return true;
            int cooldown = ActionCatalog.LifeChangingInteractions.Contains(def.defName) ? 7 * GenDate.TicksPerDay : 4 * GenDate.TicksPerHour;
            return lastInteractionTicks.TryGetValue(InteractionKey(def, target), out int last) && now - last < cooldown;
        }

        private static string InteractionKey(InteractionDef def, Pawn target) => def.defName + "|" + target.ThingID;

        /// <summary>Called by JobDriver_AITalkTo when the conversation happened or was given up.</summary>
        public void OnTalkFinished(Pawn target, InteractionDef def, bool success, string failure, string line)
        {
            if (success)
            {
                int now = Find.TickManager.TicksGame;
                lastInteractionTicks[InteractionKey(def, target)] = now;
                if (ActionCatalog.IsNegative(def))
                    lastNegativeTick = now;
            }
            string text = success ? $"Talked to {target.LabelShort} ({def.label})." : $"Didn't get to talk to {target.LabelShort}: {failure}.";
            if (success && line != null && AIPawnControlMod.Settings.speakLines)
                text += $" I said: \"{line}\"";
            AddDecision(text, importance: success ? 0 : 2); // a talk that happened is recorded from the PlayLog
            ModLog.Message($"{pawn.LabelShort}: {text}");
            if (success && line != null && AIPawnControlMod.Settings.mindsAnswer && AIPawnControlMod.Settings.speakLines)
                MindManager.Instance?.MindOf(target)?.Hear(pawn, def, line);
        }

        // ---------- Replies to other minds (PHASE6.md §4) ----------

        private class Heard
        {
            public Pawn speaker;
            public InteractionDef def;
            public string line;
            public int tick;
        }

        private Heard heard; // not saved: a reload just loses an unanswered line
        private const int ReplyMaxAgeTicks = 2 * GenDate.TicksPerHour;

        /// <summary>Another mind's talk landed on her: she answers once, when she's free (never cancels her own call).</summary>
        public void Hear(Pawn speaker, InteractionDef def, string line)
        {
            if (persona == null)
                return;
            heard = new Heard { speaker = speaker, def = def, line = line, tick = Find.TickManager.TicksGame };
            UpdateReply();
        }

        /// <summary>Called every frame with UpdateChat: the player first, then an answer to another mind.</summary>
        public void UpdateReply()
        {
            if (heard == null || chatPending)
                return;
            if (Find.TickManager.TicksGame - heard.tick > ReplyMaxAgeTicks || PausedReason() != null)
            {
                ModLog.Message($"{pawn.LabelShort}: didn't answer {heard.speaker.LabelShort} ({PausedReason() ?? "too long ago"}).");
                heard = null;
                return;
            }
            if (reflecting)
                CancelReflect(); // a conversation never waits for background memory work
            if (Thinking)
                return;
            var h = heard;
            heard = null;
            RequestReply(h);
        }

        private void RequestReply(Heard h)
        {
            bool mayAct = ActsLeft > 0;
            var menu = mayAct ? ActionCatalog.BuildActMenu(pawn, this, inConversation: true) : new List<ActionCatalog.ActOption>();
            string speaker = h.speaker.LabelShort;
            AfterRecall($"{h.line} ({speaker})", (vector, tag) =>
            {
                var recall = Recall.Conversation(this, speaker, vector, tag, 3);
                string menuText = mayAct ? ActionCatalog.DescribeMenu(menu) : "(you've made all your decisions for today; just answer)";
                Send("reply", ReplyMessages(speaker, h.line, h.def.label, menuText, recall), ActionCatalog.ChatSchema(menu, recall.Shown),
                    reply => OnReply(h, reply, menu, recall), stillValid: () => PausedReason());
            });
        }

        /// <summary>The Reply call's prompt: answering another mind's line.</summary>
        internal List<KeyValuePair<string, string>> ReplyMessages(string speaker, string line, string kind, string menu, Recall recall) =>
            PromptBuilder.Build("reply", this, new Dictionary<string, string>
            {
                ["speaker"] = speaker,
                ["line"] = line,
                ["kind"] = kind,
                ["menu"] = menu,
            }, recall.Sections);

        private void OnReply(Heard h, Dictionary<string, object> reply, List<ActionCatalog.ActOption> menu, Recall recall)
        {
            string speaker = h.speaker.LabelShort;
            recall.MarkUsed(this, reply.Int("memory"), speaker);
            string text = SpeechLog.Clean(reply.Str("reply"), ActionCatalog.MaxChatReply);
            if (text == null)
                return;
            SpeechLog.Say(pawn, text);
            memory.Record(pawn, "talk", h.def.defName, $"I answered {speaker}: \"{text}\"", 3, MemoryEvent.TookPart, new[] { speaker });
            MindManager.Instance?.MindOf(h.speaker)?.memory.Record(h.speaker, "talk", h.def.defName, $"{pawn.LabelShort} answered me: \"{text}\"",
                3, MemoryEvent.Told, new[] { pawn.LabelShort });

            string result = $"Answered {speaker}: \"{text}\"";
            var picked = menu.FirstOrDefault(o => o.Id == reply.Int("act"));
            var option = picked == null ? null : ActionCatalog.StillOffered(picked, ActionCatalog.BuildActMenu(pawn, this, inConversation: true), reply);
            if (option != null)
            {
                CountAct(); // acting on it is her decision (§4); just answering is free
                result += $" Then: {option.Label}: {MindActions.Safely(pawn, option.Label, () => option.Apply(null))}";
            }
            AddDecision(result, importance: 0); // the talk events above are the memory
            ModLog.Message($"{pawn.LabelShort} reply to {speaker}: \"{text}\" | act: {option?.Label ?? (picked != null ? $"{picked.Label} (no longer an option)" : "none")}");
        }

        private void CountAct()
        {
            if (actsDay != LocalDay)
            {
                actsDay = LocalDay;
                actsToday = 0;
            }
            actsToday++;
        }

        public string KeepGoing(int hours)
        {
            wakeAtTick = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            return $"Carrying on; I'll check back in {hours}h.";
        }

        private void RequestAct(string trigger, bool countBudget = true)
        {
            if (countBudget)
                CountAct();
            wakeAtTick = -1;
            idleSinceTick = -1;
            dangerThink = false;
            var menu = ActionCatalog.BuildActMenu(pawn, this);
            bool groupTurn = GroupChat.Instance?.Holder() == this; // this Act is her turn in the group chat
            var present = Recall.Present(pawn);
            AfterRecall(Recall.ActQuery(pawn, present), (query, tag) =>
            {
                var recall = Recall.Act(this, present, query, tag);
                Send("act", ActMessages(trigger, menu, recall), ActionCatalog.ActSchema(menu, recall.Shown), reply => OnAct(reply, menu, recall, groupTurn));
            });
        }

        /// <summary>The Act call's prompt: the trigger, the menu and who's around.</summary>
        internal List<KeyValuePair<string, string>> ActMessages(string trigger, List<ActionCatalog.ActOption> menu, Recall recall) =>
            PromptBuilder.Build("act", this, new Dictionary<string, string>
            {
                ["trigger"] = trigger,
                ["menu"] = ActionCatalog.DescribeMenu(menu),
                ["talkto"] = TalkToText(menu),
                ["danger"] = DangerResponse.Threats(pawn.Map).Count > 0 ? "\n\n" + Prompts.Fill("danger", new Dictionary<string, string>()) : "", // only with the danger menu
            }, recall.Sections);

        /// <summary>
        /// Embeds the situation's query (when memory is on), then goes on with the vector on the main thread, unless the
        /// call was cancelled meanwhile (Cancel clears recalling).
        /// </summary>
        private void AfterRecall(string query, Action<float[], string> then)
        {
            recalling = true;
            Recall.WithQuery(this, query, (vector, tag) =>
            {
                if (!recalling || pawn == null || !pawn.Spawned)
                    return;
                recalling = false;
                then(vector, tag);
            });
        }

        /// <summary>"Kira, Bo (nearest first)" for act.txt, or that nobody's around.</summary>
        public static string TalkToText(List<ActionCatalog.ActOption> menu)
        {
            var targets = menu.FirstOrDefault(o => o.TalkTargets != null)?.TalkTargets;
            return targets != null ? string.Join(", ", targets.Select(p => p.LabelShort)) + " (nearest first)" : "nobody is around to talk to";
        }

        /// <summary>Gives today's decision back (a project found no site, §5).</summary>
        public void GiveBackAct()
        {
            if (actsDay == LocalDay && actsToday > 0)
                actsToday--;
        }

        private void OnAct(Dictionary<string, object> reply, List<ActionCatalog.ActOption> menu, Recall recall, bool groupTurn)
        {
            int choice = reply.Int("choice", -1);
            var picked = menu.FirstOrDefault(o => o.Id == choice);
            if (picked == null)
            {
                Fail($"act reply chose {choice}, which isn't on the menu");
                return;
            }
            var option = ActionCatalog.StillOffered(picked, ActionCatalog.BuildActMenu(pawn, this), reply);
            if (option == null)
            {
                ModLog.Message($"{pawn.LabelShort}: dropped act reply ({picked.Label} is no longer an option).");
                return;
            }
            string say = SpeechLog.Clean(reply.Str("say"));
            string result = MindActions.Safely(pawn, option.Label, () =>
            {
                string applied = option.ApplyReply != null ? option.ApplyReply(reply, say) : option.Apply(say);
                if (!option.IsTalk && !option.OwnRemark && say != null && AIPawnControlMod.Settings.speakLines)
                {
                    SpeechLog.Say(pawn, say);
                    applied += $" Said: \"{say}\"";
                }
                return applied;
            });
            AddDecision($"{option.Label}: {result}");
            ModLog.Message($"{pawn.LabelShort} act: {option.Label} | {result} | Reason: {lastReason}");
            recall.MarkUsed(this, reply.Int("memory"), option.IsTalk ? option.Target?.LabelShort : null);
            if (groupTurn)
                GroupChat.Instance?.PassTurn(this); // her turn in the group chat was this Act, whatever she picked
        }

        // ---------- Danger (DANGER_RESPONSE.md) ----------

        /// <summary>The Plan call was her think about the danger starting.</summary>
        public void DangerThought() => dangerThink = false;

        /// <summary>Before a danger choice changes them: her own area and hostility response, once, to restore afterwards.</summary>
        public void KeepSettings()
        {
            if (keptSettings)
                return;
            keptSettings = true;
            areaBefore = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
            responseBefore = pawn.playerSettings.hostilityResponse;
        }

        /// <summary>The danger is over (or her mind is turned off): her own area and hostility response come back.</summary>
        public void EndDanger()
        {
            if (!keptSettings || pawn?.playerSettings == null || !pawn.Spawned)
                return;
            keptSettings = false;
            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = areaBefore;
            pawn.playerSettings.hostilityResponse = responseBefore;
            areaBefore = null;
            AddDecision("The danger is over; back to my usual area and routine.", importance: 0);
            ModLog.Message($"{pawn.LabelShort}: danger over, area and hostility response restored.");
        }

        // ---------- Player chat ----------

        /// <summary>From the Mind tab, via the main-thread pump. Wakes a sleeper (with vanilla's disturbed-sleep cost).</summary>
        public void PlayerSays(string text)
        {
            AddChat(ChatLine.From.Player, text);
            memory.Record(pawn, "chat", null, $"The player said: \"{text}\"", 3, MemoryEvent.PlayerSaid, new[] { PersonFile.Player });
            lastChatTick = Find.TickManager.TicksGame;
            chatPending = true;
            if (!CanBeAwake)
            {
                chatWhileOut = true;
                return; // answered when she comes to
            }
            if (!pawn.Awake())
            {
                RestUtility.WakeUp(pawn);
                pawn.needs?.mood?.thoughts.memories.TryGainMemory(ThoughtDefOf.SleepDisturbed);
                chatWokeHer = true;
            }
            UpdateChat();
        }

        /// <summary>Called every frame by MindManager (also while paused): answers pending messages once she can.</summary>
        public void UpdateChat()
        {
            if (chatPending && reflecting)
                CancelReflect(); // the player never waits for background memory work; it reruns afterwards
            if (!chatPending || Thinking || pawn == null || !pawn.Spawned || !CanBeAwake)
                return;
            chatPending = false;
            RequestChat();
        }

        private void RequestChat()
        {
            string cantAct = PausedReason(ignoreSleep: true);
            var menu = cantAct == null ? ActionCatalog.BuildActMenu(pawn, this, inConversation: true) : new List<ActionCatalog.ActOption>();
            int lastAnswer = chat.FindLastIndex(l => l.from == ChatLine.From.Mind);
            var unanswered = chat.Skip(lastAnswer + 1).Where(l => l.from == ChatLine.From.Player).Select(l => l.text).ToList();
            var earlier = chat.Take(lastAnswer + 1).Where(l => l.from != ChatLine.From.System)
                .Select(l => (l.from == ChatLine.From.Player ? "Player: " : "Me: ") + l.text).ToList();
            string situation = chatWhileOut ? "You just came to after being unconscious. While you were out, the player spoke to you."
                : chatWokeHer ? "The player's message just woke you up."
                : "The player is talking to you.";
            chatWhileOut = false;
            chatWokeHer = false;
            string message = string.Join("\n", unanswered);
            string query = Recall.ChatQuery(this, message, out var mentioned);
            chatThinking = true;
            AfterRecall(query, (vector, tag) =>
            {
                var recall = Recall.Conversation(this, PersonFile.Player, vector, tag, 5, mentioned);
                string menuText = cantAct == null ? ActionCatalog.DescribeMenu(menu) : $"(you can't do anything else right now: {cantAct})";
                Send("chat", ChatMessages(situation, earlier, message, menuText, recall), ActionCatalog.ChatSchema(menu, recall.Shown), reply => OnChat(reply, menu, recall),
                    stillValid: () => pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned ? "gone" : null,
                    onError: error =>
                    {
                        chatThinking = false;
                        AddChat(ChatLine.From.System, "AIPawnControl_ChatUnreachable".Translate(pawn.LabelShort, error));
                    });
                chatThinking = true; // after Send, whose Cancel() clears it
            });
        }

        /// <summary>The Chat call's prompt: the conversation so far and the player's new message.</summary>
        internal List<KeyValuePair<string, string>> ChatMessages(string situation, List<string> earlier, string message, string menu, Recall recall) =>
            PromptBuilder.Build("chat", this, new Dictionary<string, string>
            {
                ["situation"] = situation,
                ["history"] = earlier.Count > 0 ? string.Join("\n", earlier) : "(this is the start of the conversation)",
                ["message"] = message,
                ["menu"] = menu,
            }, recall.Sections);

        private void OnChat(Dictionary<string, object> reply, List<ActionCatalog.ActOption> menu, Recall recall)
        {
            chatThinking = false;
            recall.MarkUsed(this, reply.Int("memory"), PersonFile.Player);
            string text = SpeechLog.Clean(reply.Str("reply"), ActionCatalog.MaxChatReply) ?? "...";
            AddChat(ChatLine.From.Mind, text);
            memory.Record(pawn, "chat", null, $"I told the player: \"{text}\"", 3, MemoryEvent.TookPart, new[] { PersonFile.Player });
            lastChatTick = Find.TickManager.TicksGame;
            if (AIPawnControlMod.Settings.chatBubbles)
                SpeechLog.Say(pawn, text);

            var option = PausedReason(ignoreSleep: true) == null ? menu.FirstOrDefault(o => o.Id == reply.Int("act")) : null;
            if (option == null)
                return;
            string result = MindActions.Safely(pawn, option.Label, () => option.Apply(null));
            AddDecision($"Asked by the player: {option.Label}: {result}");
            ModLog.Message($"{pawn.LabelShort} chat act: {option.Label} | {result}");
        }

        private void AddChat(ChatLine.From from, string text)
        {
            chat.Add(new ChatLine(from, text));
            while (chat.Count > MaxChatLines)
                chat.RemoveAt(0);
        }

        public void Cancel()
        {
            current?.Cancel();
            current = null;
            recalling = false;
            chatThinking = false;
        }

        public void RegeneratePersona()
        {
            Cancel();
            persona = null;
            RequestPersona();
        }

        private void RequestPersona() => Send("persona", PersonaMessages(), ActionCatalog.PersonaSchema(), OnPersona);

        /// <summary>The Persona call's prompt: who she is from the game, and the player's note. No system prompt.</summary>
        internal List<KeyValuePair<string, string>> PersonaMessages() => new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("user", Prompts.Fill("persona", new Dictionary<string, string>
            {
                ["identity"] = SnapshotBuilder.Identity(pawn),
                ["pairing"] = SnapshotBuilder.PersonaPairing(pawn),
                ["note"] = string.IsNullOrWhiteSpace(note) ? "(none)" : note,
            })),
        };

        private void OnPersona(Dictionary<string, object> reply)
        {
            string text = reply.Str("persona");
            if (string.IsNullOrWhiteSpace(text))
            {
                Fail("persona reply had no persona");
                return;
            }
            bool first = !decisions.Any();
            persona = text.Trim();
            AddDecision(first ? "I got a mind of my own." : "Persona rewritten.");
            ModLog.Message($"{pawn.LabelShort} persona: {persona}");
        }

        // ---------- Nightly Reflect (PHASE3.md §4) ----------

        /// <summary>The night that began most recently at 22:00, as a local day index.</summary>
        private int CurrentNight => GameTime.Night(pawn.Map);

        private bool IsNight
        {
            get
            {
                int hour = GenLocalDate.HourOfDay(pawn.Map);
                return hour >= 22 || hour < 6;
            }
        }

        /// <summary>
        /// Once per night: the first time she's asleep after 22:00, or at 02:00 if she's awake. A night she missed
        /// (downed, away) is made up at the next chance, with every event since the last Reflect.
        /// </summary>
        private bool TryReflect()
        {
            if (!AIPawnControlMod.Settings.memoryEnabled || persona == null || pawn.Map == null)
                return false;
            int night = CurrentNight;
            if (memory.lastReflectNight < 0)
                memory.lastReflectNight = IsNight ? night - 1 : night; // a new mind starts with tonight, not a missed night
            if (memory.lastReflectNight >= night)
                return false;
            int hour = GenLocalDate.HourOfDay(pawn.Map);
            bool due = !IsNight || !pawn.Awake() || (hour >= 2 && hour < 6);
            if (!due || pawn.Downed)
                return false;
            StartReflect(dev: false, night);
            return true;
        }

        /// <summary>
        /// Once a night, her project's materials again (STREAMLINE.md §7). They're marked when it's laid out; one that
        /// waited on a table or an order that came later, or whose trees and ore ran out, would otherwise wait for good.
        /// </summary>
        private void RecheckSupplies()
        {
            if (CurrentNight == suppliesNight)
                return;
            suppliesNight = CurrentNight;
            var project = BuildManager.Instance?.ActiveProject(pawn);
            if (project == null)
                return;
            string marked = Supplies.MarkFor(pawn, project);
            if (string.IsNullOrEmpty(marked))
                return;
            AddDecision(marked, importance: 0);
            ModLog.Message($"{pawn.LabelShort}'s {project.Kind} materials, rechecked: {marked}");
        }

        /// <summary>Dev "Reflect now": over the last 24 hours, whatever the time.</summary>
        public bool ReflectNow()
        {
            if (Thinking || persona == null || pawn.Map == null)
                return false;
            StartReflect(dev: true, CurrentNight);
            return true;
        }

        private void StartReflect(bool dev, int night)
        {
            var reflection = Reflection.Prepare(this, dev);
            if (reflection == null)
            {
                memory.MarkReflected(memory.LastEventId, Find.TickManager.TicksGame);
                if (!dev)
                    memory.lastReflectNight = night; // a quiet day: nothing to reflect on
                return;
            }
            reflecting = true;
            int run = ++reflectRun;
            ModLog.Message($"{pawn.LabelShort}: Reflect started.");
            requestStartedRealtime = UnityEngine.Time.realtimeSinceStartup;
            if (!reflection.HasMemoriesWithVectors)
            {
                reflection.PickClosest(null, null);
                SendReflect(reflection, run, night);
                return;
            }
            EmbedClient.Embed(new List<string> { reflection.QueryText() }, query: true, result =>
            {
                if (run != reflectRun)
                    return;
                reflection.PickClosest(result.Ok ? result.Vectors[0] : null, result.Tag); // embeddings down: people and importance only
                SendReflect(reflection, run, night);
            });
        }

        private void SendReflect(Reflection reflection, int run, int night)
        {
            var messages = PromptBuilder.Build("reflect", this, reflection.PromptValues());
            Send("reflect", messages, reflection.ReplySchema(), reply =>
                {
                    if (run != reflectRun)
                        return;
                    reflection.Parse(reply);
                    var texts = reflection.TextsToEmbed();
                    if (texts.Count == 0)
                    {
                        FinishReflect(reflection, null, night);
                        return;
                    }
                    EmbedClient.Embed(texts, query: false, embedded =>
                    {
                        if (run == reflectRun)
                            FinishReflect(reflection, embedded, night);
                    });
                },
                stillValid: () =>
                {
                    if (pawn != null && !pawn.Destroyed && !pawn.Dead)
                        return null;
                    reflecting = false;
                    return "gone";
                },
                onError: _ => reflecting = false,
                maxTokens: ReflectTokens);
        }

        private void FinishReflect(Reflection reflection, EmbedResult embedded, int night)
        {
            reflecting = false;
            string summary = reflection.Commit(embedded, night);
            ModLog.Message($"{pawn.LabelShort} reflected{(reflection.Dev ? " (dev)" : "")}: {summary}");
        }

        private void CancelReflect()
        {
            reflectRun++;
            reflecting = false;
            Cancel();
            ModLog.Message($"{pawn.LabelShort}: Reflect paused for the player's message; it runs again later.");
        }

        /// <summary>Reflect's one work change (STREAMLINE.md §8): applied and remembered in her decisions.</summary>
        public void ChangeWork(WorkTypeDef work, string priority, string why)
        {
            string result = MindActions.ChangePriority(pawn, work, priority, out bool changed);
            AddDecision($"{result}{(why != null ? $" ({why})" : "")}", importance: changed ? 4 : 0);
            ModLog.Message($"{pawn.LabelShort} work: {work.defName} to {priority} | {result} | why: {why}");
        }

        private static bool warnedManualOff;

        private static void WarnManualPrioritiesOff()
        {
            if (warnedManualOff)
                return;
            warnedManualOff = true;
            Messages.Message("AIPawnControl_ManualPrioritiesOff".Translate(), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <param name="stillValid">Replaces the usual pause check when the reply arrives; returns why to drop it, or null.</param>
        /// <param name="maxTokens">Overrides the settings' reply budget (0 = use the setting).</param>
        internal void Send(string callType, List<KeyValuePair<string, string>> messages, object schema, Action<Dictionary<string, object>> onReply,
                          Func<string> stillValid = null, Action<string> onError = null, int maxTokens = 0)
        {
            Cancel();
            int sentTick = Find.TickManager.TicksGame;
            lastThinkTick = sentTick;
            lastThinkRealtime = UnityEngine.Time.realtimeSinceStartup;
            requestGame = Current.Game;
            requestStartedRealtime = UnityEngine.Time.realtimeSinceStartup;
            LlmRequest request = null;
            request = LlmClient.Send(callType, messages, schema, maxTokens, result =>
            {
                // Stale checks: only the latest request, same game, pawn still able to act.
                if (request != current || Current.Game != requestGame)
                    return;
                current = null;
                string paused = stillValid != null ? stillValid() : PausedReason(ignoreSleep: true);
                if (paused != null)
                {
                    ModLog.Message($"{pawn?.LabelShort}: dropped {callType} reply ({paused}; waited {result.WaitedSeconds:0}s in the queue, {result.Seconds:0}s to answer).");
                    return;
                }
                if (!result.Ok)
                {
                    Fail(result.Error);
                    onError?.Invoke(result.Error);
                    return;
                }
                Dictionary<string, object> reply;
                try
                {
                    reply = (Dictionary<string, object>)Json.Parse(result.Content);
                }
                catch (Exception e)
                {
                    Fail("reply wasn't valid JSON: " + e.Message);
                    onError?.Invoke("the reply wasn't valid JSON");
                    return;
                }
                failures = 0;
                if (reply.Str("reason") is string reason)
                    lastReason = reason.Trim();
                onReply(reply);
            });
            current = request;
        }

        private void Fail(string error)
        {
            failures++;
            ModLog.Warning($"{pawn?.LabelShort}: think failed ({failures} in a row): {error}");
            if (Unreachable)
                backoffUntilTick = Find.TickManager.TicksGame + BackoffTicks;
        }

        /// <param name="importance">For the memory event log; 0 = don't record it there.</param>
        public void AddDecision(string text, int importance = 2)
        {
            string time = pawn.Map != null ? $"{GenLocalDate.HourOfDay(pawn.Map):00}:00 " : "";
            decisions.Add(time + text);
            decisionTicks.Add(Find.TickManager.TicksGame);
            if (importance > 0)
                memory.Record(pawn, "decision", null, text, importance, MemoryEvent.TookPart);
            while (decisions.Count > MaxDecisions)
            {
                decisions.RemoveAt(0);
                decisionTicks.RemoveAt(0);
            }
        }
    }
}
