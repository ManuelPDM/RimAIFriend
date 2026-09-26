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
        private const int MorningHour = 5;
        private const int ExtraPlansPerDay = 2;
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
        public string intent;
        public string lastReason;
        public List<string> decisions = new List<string>();
        private int lastMorningPlanDay = -1;
        private int extraPlansDay = -1;
        private int extraPlansToday;
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
        public MindMemory memory = new MindMemory(); // events, memories, diary, person files, lately, goals (promises included)

        // Not saved
        private LlmRequest current;
        private Game requestGame;
        private float requestStartedRealtime;
        private int failures;
        private int backoffUntilTick;
        private float lastThinkRealtime = -999f;
        private int idleSinceTick = -1;
        private int lastDanger = -1;
        private int lastMoodBand;
        private int lastInjuryCount;
        private readonly HashSet<int> ourJobIds = new HashSet<int>(); // jobs we ordered, to tell them apart from the player's
        private List<ActionCatalog.ActOption> pendingMenu;
        private int talkLineJobId = -1;
        private string talkLine;
        private bool chatWokeHer;
        private bool chatThinking;
        private bool reflecting; // a Reflect is somewhere between its query embedding and its commit
        private int reflectRun;  // bumped to abandon a running Reflect
        private bool recalling;  // an Act or Chat is waiting for its situation's query vector
        private Recall pendingRecall;

        public bool Thinking => current != null || reflecting || recalling;
        public bool Reflecting => reflecting;
        public float ThinkingSeconds => UnityEngine.Time.realtimeSinceStartup - requestStartedRealtime;
        public bool Unreachable => failures >= FailuresBeforeBackoff;
        public int ExtraPlansLeft => LocalDay == extraPlansDay ? ExtraPlansPerDay - extraPlansToday : ExtraPlansPerDay;
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
            Scribe_Values.Look(ref intent, "intent");
            Scribe_Values.Look(ref lastReason, "lastReason");
            Scribe_Collections.Look(ref decisions, "decisions", LookMode.Value);
            Scribe_Values.Look(ref lastMorningPlanDay, "lastMorningPlanDay", -1);
            Scribe_Values.Look(ref extraPlansDay, "extraPlansDay", -1);
            Scribe_Values.Look(ref extraPlansToday, "extraPlansToday");
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
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                decisions = decisions ?? new List<string>();
                lastInteractionTicks = lastInteractionTicks ?? new Dictionary<string, int>();
                chat = chat ?? new List<ChatLine>();
                memory = memory ?? new MindMemory();
            }
        }

        private int LocalDay => pawn.Map != null ? GenLocalDate.Year(pawn.Map) * GenDate.DaysPerYear + GenLocalDate.DayOfYear(pawn.Map) : -1;

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
            if (chatPending)
                return; // answered from UpdateChat, which also runs while paused
            if (Thinking || Find.TickManager.TicksGame < backoffUntilTick || pawn == null || !pawn.Spawned)
                return;
            if (TryReflect())
                return;
            // Only danger or a new injury may wake a sleeper's mind; a mood drop while asleep just confuses the model.
            Change change = DetectSignificantChange();
            bool significant = change == Change.Urgent || (change == Change.Mood && pawn.Awake());
            if (PausedReason(ignoreSleep: change == Change.Urgent) != null)
            {
                idleSinceTick = -1;
                return;
            }
            if (persona == null)
            {
                RequestPersona();
                return;
            }
            if (pawn.Awake() && lastMorningPlanDay != LocalDay && GenLocalDate.HourOfDay(pawn.Map) >= MorningHour)
            {
                RequestPlan(morning: true);
                return;
            }

            int now = Find.TickManager.TicksGame;
            bool idle = pawn.CurJob == null || pawn.CurJob.def.isIdle || pawn.mindState.IsIdle;
            if (!idle)
                idleSinceTick = -1;
            else if (idleSinceTick < 0)
                idleSinceTick = now;

            if (now - lastThinkTick < MinTicksBetweenThinks || UnityEngine.Time.realtimeSinceStartup - lastThinkRealtime < MinRealSecondsBetweenThinks)
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
            if (trigger == null || ActsLeft <= 0)
                return;
            RequestAct(trigger);
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
        /// Code-side anti-spam: normal interactions once per target per day, negative ones once per day in total,
        /// life-changing ones (romance, proposal, breakup) once per target per week.
        /// </summary>
        public bool OnCooldown(InteractionDef def, Pawn target)
        {
            int now = Find.TickManager.TicksGame;
            if (ActionCatalog.IsNegative(def) && now - lastNegativeTick < GenDate.TicksPerDay)
                return true;
            int cooldown = ActionCatalog.LifeChangingInteractions.Contains(def.defName) ? 7 * GenDate.TicksPerDay : GenDate.TicksPerDay;
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
        }

        public string KeepGoing(int hours)
        {
            wakeAtTick = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            return $"Carrying on; I'll check back in {hours}h.";
        }

        private void RequestAct(string trigger, bool countBudget = true)
        {
            if (countBudget)
            {
                if (actsDay != LocalDay)
                {
                    actsDay = LocalDay;
                    actsToday = 0;
                }
                actsToday++;
            }
            wakeAtTick = -1;
            idleSinceTick = -1;
            var menu = ActionCatalog.BuildActMenu(pawn, this);
            var present = Recall.Present(pawn);
            recalling = true;
            Recall.WithQuery(this, Recall.ActQuery(pawn, present), (query, tag) =>
            {
                if (!recalling || pawn == null || !pawn.Spawned)
                    return; // cancelled meanwhile
                recalling = false;
                var recall = Recall.Act(this, present, query, tag);
                var messages = PromptBuilder.Build("act", this, new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["menu"] = ActionCatalog.DescribeMenu(menu),
                }, recall.Sections);
                pendingMenu = menu;
                pendingRecall = recall;
                Send("act", messages, ActionCatalog.ActSchema(menu, recall.Shown), OnAct, maxAgeTicks: GenDate.TicksPerHour);
            });
        }

        private void OnAct(Dictionary<string, object> reply)
        {
            var menu = pendingMenu;
            pendingMenu = null;
            int choice = reply.TryGetValue("choice", out object c) && c is double d ? (int)d : -1;
            var option = menu?.FirstOrDefault(o => o.Id == choice);
            if (option == null)
            {
                Fail($"act reply chose {choice}, which isn't on the menu");
                return;
            }
            string say = SpeechLog.Clean(reply.TryGetValue("say", out object sayRaw) ? sayRaw as string : null);
            string result;
            try
            {
                result = option.Apply(say);
                if (!option.IsTalk && say != null && AIPawnControlMod.Settings.speakLines)
                {
                    SpeechLog.Say(pawn, say);
                    result += $" Said: \"{say}\"";
                }
            }
            catch (Exception e)
            {
                result = "That went wrong: " + e.Message;
                ModLog.Error($"{pawn.LabelShort}: applying \"{option.Label}\" threw: {e}");
            }
            AddDecision($"{option.Label}: {result}");
            ModLog.Message($"{pawn.LabelShort} act: {option.Label} | {result} | Reason: {lastReason}");
            int memoryId = reply.TryGetValue("memory", out object m) && m is double md ? (int)md : 0;
            pendingRecall?.MarkUsed(this, memoryId, option.IsTalk ? option.Target?.LabelShort : null);
            pendingRecall = null;
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
            var menu = cantAct == null ? ActionCatalog.BuildActMenu(pawn, this) : new List<ActionCatalog.ActOption>();
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
            recalling = true;
            chatThinking = true;
            Recall.WithQuery(this, query, (vector, tag) =>
            {
                if (!recalling || pawn == null || !pawn.Spawned)
                    return; // cancelled meanwhile
                recalling = false;
                var recall = Recall.Chat(this, mentioned, vector, tag);
                var messages = PromptBuilder.Build("chat", this, new Dictionary<string, string>
                {
                    ["situation"] = situation,
                    ["history"] = earlier.Count > 0 ? string.Join("\n", earlier) : "(this is the start of the conversation)",
                    ["message"] = message,
                    ["menu"] = cantAct == null ? ActionCatalog.DescribeMenu(menu) : $"(you can't do anything else right now: {cantAct})",
                }, recall.Sections);
                Send("chat", messages, ActionCatalog.ChatSchema(menu, recall.Shown), reply => OnChat(reply, menu, recall),
                    stillValid: () => pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned ? "gone" : null,
                    onError: error =>
                    {
                        chatThinking = false;
                        AddChat(ChatLine.From.System, "AIPawnControl_ChatUnreachable".Translate(pawn.LabelShort, error));
                    });
                chatThinking = true; // after Send, whose Cancel() clears it
            });
        }

        private void OnChat(Dictionary<string, object> reply, List<ActionCatalog.ActOption> menu, Recall recall)
        {
            chatThinking = false;
            int memoryId = reply.TryGetValue("memory", out object m) && m is double md ? (int)md : 0;
            recall.MarkUsed(this, memoryId, PersonFile.Player);
            string text = SpeechLog.Clean(reply.TryGetValue("reply", out object r) ? r as string : null, ActionCatalog.MaxChatReply) ?? "...";
            AddChat(ChatLine.From.Mind, text);
            memory.Record(pawn, "chat", null, $"I told the player: \"{text}\"", 3, MemoryEvent.TookPart, new[] { PersonFile.Player });
            lastChatTick = Find.TickManager.TicksGame;
            if (AIPawnControlMod.Settings.chatBubbles)
                SpeechLog.Say(pawn, text);

            string note = reply.TryGetValue("note", out object n) ? (n as string)?.Trim() : null;
            if (!string.IsNullOrEmpty(note))
            {
                memory.AddGoal(note, "I promised the player", Goal.Promise); // shown as [I promised the player] until Reflect retires it
                AddDecision($"Promised the player: {note}", importance: 4);
            }

            int choice = reply.TryGetValue("act", out object a) && a is double d ? (int)d : 0;
            var option = PausedReason(ignoreSleep: true) == null ? menu.FirstOrDefault(o => o.Id == choice) : null;
            if (option == null)
                return;
            string result;
            try
            {
                result = option.Apply(null);
            }
            catch (Exception e)
            {
                result = "That went wrong: " + e.Message;
                ModLog.Error($"{pawn.LabelShort}: applying \"{option.Label}\" from chat threw: {e}");
            }
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

        /// <summary>A plan outside the morning one. Dev "force" ignores the daily budget.</summary>
        public bool TryExtraPlan(bool force)
        {
            if (Thinking || PausedReason(ignoreSleep: true) != null)
                return false;
            if (!force && ExtraPlansLeft <= 0)
                return false;
            if (extraPlansDay != LocalDay)
            {
                extraPlansDay = LocalDay;
                extraPlansToday = 0;
            }
            extraPlansToday++;
            RequestPlan(morning: false);
            return true;
        }

        private void RequestPersona()
        {
            var values = new Dictionary<string, string>
            {
                ["identity"] = SnapshotBuilder.Identity(pawn),
                ["note"] = string.IsNullOrWhiteSpace(note) ? "(none)" : note,
            };
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("user", Prompts.Fill("persona", values)),
            };
            Send("persona", messages, ActionCatalog.PersonaSchema(), OnPersona);
        }

        private void OnPersona(Dictionary<string, object> reply)
        {
            if (!(reply.TryGetValue("persona", out object p) && p is string text) || string.IsNullOrWhiteSpace(text))
            {
                Fail("persona reply had no persona");
                return;
            }
            bool first = !decisions.Any();
            persona = text.Trim();
            AddDecision(first ? "I got a mind of my own." : "Persona rewritten.");
            string say = SpeechLog.Clean(reply.TryGetValue("say", out object sayRaw) ? sayRaw as string : null);
            if (first && say != null && AIPawnControlMod.Settings.speakLines)
            {
                SpeechLog.Say(pawn, say);
                AddDecision($"Said: \"{say}\"");
            }
            ModLog.Message($"{pawn.LabelShort} persona: {persona}");
        }

        private void RequestPlan(bool morning)
        {
            if (morning)
            {
                lastMorningPlanDay = LocalDay; // one morning plan per day, even if it fails
                if (!AIPawnControlMod.Settings.memoryEnabled)
                    memory.goals.RemoveAll(g => g.source == Goal.Promise); // no Reflect to retire them, so yesterday's promises expire
            }
            var workTypes = ActionCatalog.PlannableWorkTypes(pawn);
            bool manual = ActionCatalog.ManualPriorities;
            if (!manual)
                WarnManualPrioritiesOff();
            var messages = PromptBuilder.Build("plan", this, new Dictionary<string, string>
            {
                ["worktypes"] = workTypes.Count > 0 ? ActionCatalog.DescribeWorkTypes(pawn, workTypes) : "(none)",
                ["prioritynote"] = manual
                    ? ""
                    : "Manual priorities are OFF in this colony, so you can only turn work types on (1) or off (0), not rank them.",
                ["schedule"] = ActionCatalog.DescribeSchedule(pawn),
                ["scheduleoptions"] = string.Join(", ", ActionCatalog.ScheduleOptions()),
            }, Recall.Plan(this).Sections);
            Send("plan", messages, ActionCatalog.PlanSchema(workTypes), OnPlan);
        }

        private void OnPlan(Dictionary<string, object> reply)
        {
            string result = MindActions.ApplyPlan(pawn, reply);
            if (reply.TryGetValue("intent", out object i) && i is string newIntent && !string.IsNullOrWhiteSpace(newIntent))
                intent = newIntent.Trim();
            AddDecision($"Plan: {intent} {result}");
            ModLog.Message($"{pawn.LabelShort} plan: {intent} | {result} | Reason: {lastReason} | " +
                           $"Now: schedule {ActionCatalog.DescribeSchedule(pawn)}; priorities {ActionCatalog.DescribePriorities(pawn)}");
        }

        // ---------- Nightly Reflect (PHASE3.md §4) ----------

        /// <summary>The night that began most recently at 22:00, as a local day index.</summary>
        private int CurrentNight => GenLocalDate.HourOfDay(pawn.Map) >= 22 ? LocalDay : LocalDay - 1;

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
            Send("reflect", messages, reflection.Schema(), reply =>
                {
                    if (run != reflectRun)
                        return;
                    reflection.Parse(reply);
                    var texts = reflection.TextsToEmbed(out var reembed);
                    if (texts.Count == 0)
                    {
                        FinishReflect(reflection, null, reembed, night);
                        return;
                    }
                    EmbedClient.Embed(texts, query: false, embedded =>
                    {
                        if (run == reflectRun)
                            FinishReflect(reflection, embedded, reembed, night);
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

        private void FinishReflect(Reflection reflection, EmbedResult embedded, List<MemoryRecord> reembed, int night)
        {
            reflecting = false;
            string summary = reflection.Commit(embedded, reembed, night);
            ModLog.Message($"{pawn.LabelShort} reflected{(reflection.Dev ? " (dev)" : "")}: {summary}");
        }

        /// <summary>Dev "Show retrieval": logs the ranked memories for her current situation, each score part, and what [On my mind] would get.</summary>
        public void ShowRetrieval()
        {
            var present = Recall.Present(pawn);
            string query = Recall.ActQuery(pawn, present);
            Recall.WithQuery(this, query, (vector, tag) =>
            {
                var names = present.Select(p => p.LabelShort).ToList();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var ranked = Retrieval.Rank(memory, vector, tag, names, SnapshotBuilder.RoomLabel(pawn), names);
                var picked = Retrieval.Pick(ranked, 2, Retrieval.MinOnMind);
                double ms = watch.Elapsed.TotalMilliseconds;
                int gap = Find.TickManager.TicksGame - memory.lastOnMindTick;
                var toPlayer = Retrieval.Rank(memory, vector, tag, names.Concat(new[] { PersonFile.Player }).ToList(), null, new[] { PersonFile.Player });
                ModLog.Message($"{pawn.LabelShort} retrieval for \"{query}\" (scored {memory.memories.Count} memories in {ms:0.00} ms; vector: {(vector != null ? tag : "none")}, " +
                               $"[On my mind] {(gap >= Retrieval.OnMindGapTicks ? "allowed" : $"waits {(Retrieval.OnMindGapTicks - gap) / (float)GenDate.TicksPerHour:0.0}h")}, " +
                               $"minimum {Retrieval.MinOnMind}):\n" +
                               string.Join("\n", ranked.Take(10).Select(s => (picked.Contains(s) ? "* " : "  ") + s)) +
                               $"\n{ranked.Count - Math.Min(10, ranked.Count)} more; * = would show\n" +
                               "Same situation with the player as the audience (as in Chat):\n" + string.Join("\n", toPlayer.Take(10).Select(s => "  " + s)) +
                               $"\nPenalised with {(names.Count > 0 ? string.Join(", ", names) : "nobody")} as the audience: " + Penalised(ranked) +
                               "\nPenalised with the player as the audience: " + Penalised(toPlayer));
            });
        }

        private static string Penalised(List<Retrieval.Scored> ranked)
        {
            var lines = ranked.Where(s => s.penalty > 0f).Select(s => $"M{s.memory.id} pen {s.penalty:0.00}").ToList();
            return lines.Count > 0 ? string.Join(", ", lines) : "none";
        }

        private void CancelReflect()
        {
            reflectRun++;
            reflecting = false;
            Cancel();
            ModLog.Message($"{pawn.LabelShort}: Reflect paused for the player's message; it runs again later.");
        }

        private static bool warnedManualOff;

        private static void WarnManualPrioritiesOff()
        {
            if (warnedManualOff)
                return;
            warnedManualOff = true;
            Messages.Message("AIPawnControl_ManualPrioritiesOff".Translate(), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <param name="maxAgeTicks">Drop the reply if more game time than this passed while waiting (e.g. at ultrafast speed).</param>
        /// <param name="stillValid">Replaces the usual pause check when the reply arrives; returns why to drop it, or null.</param>
        /// <param name="maxTokens">Overrides the settings' reply budget (0 = use the setting).</param>
        private void Send(string callType, List<KeyValuePair<string, string>> messages, object schema, Action<Dictionary<string, object>> onReply,
                          int maxAgeTicks = int.MaxValue, Func<string> stillValid = null, Action<string> onError = null, int maxTokens = 0)
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
                if (paused == null && Find.TickManager.TicksGame - sentTick > maxAgeTicks)
                    paused = "the situation changed while I was thinking";
                if (paused != null)
                {
                    ModLog.Message($"{pawn?.LabelShort}: dropped {callType} reply ({paused}).");
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
                if (reply.TryGetValue("reason", out object reason) && reason is string r)
                    lastReason = r.Trim();
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
            if (importance > 0)
                memory.Record(pawn, "decision", null, text, importance, MemoryEvent.TookPart);
            while (decisions.Count > MaxDecisions)
                decisions.RemoveAt(0);
        }
    }
}
