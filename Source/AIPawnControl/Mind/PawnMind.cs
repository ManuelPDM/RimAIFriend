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

        public bool Thinking => current != null;
        public float ThinkingSeconds => UnityEngine.Time.realtimeSinceStartup - requestStartedRealtime;
        public bool Unreachable => failures >= FailuresBeforeBackoff;
        public int ExtraPlansLeft => LocalDay == extraPlansDay ? ExtraPlansPerDay - extraPlansToday : ExtraPlansPerDay;
        public int ActsLeft => AIPawnControlMod.Settings.actsPerDay - (LocalDay == actsDay ? actsToday : 0);

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
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                decisions = decisions ?? new List<string>();
                lastInteractionTicks = lastInteractionTicks ?? new Dictionary<string, int>();
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

        /// <summary>Called by MindManager every check interval.</summary>
        public void CheckTriggers()
        {
            if (Thinking || Find.TickManager.TicksGame < backoffUntilTick || pawn == null || !pawn.Spawned)
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
        public void OnTalkFinished(Pawn target, InteractionDef def, bool success, string failure)
        {
            if (success)
            {
                int now = Find.TickManager.TicksGame;
                lastInteractionTicks[InteractionKey(def, target)] = now;
                if (ActionCatalog.IsNegative(def))
                    lastNegativeTick = now;
            }
            string text = success ? $"Talked to {target.LabelShort} ({def.label})." : $"Didn't get to talk to {target.LabelShort}: {failure}.";
            AddDecision(text);
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
            var values = new Dictionary<string, string>
            {
                ["time"] = SnapshotBuilder.TimeString(pawn.Map),
                ["trigger"] = trigger,
                ["snapshot"] = SnapshotBuilder.Build(pawn, this),
                ["menu"] = ActionCatalog.DescribeMenu(menu),
            };
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", SystemPrompt()),
                new KeyValuePair<string, string>("user", Prompts.Fill("act", values)),
            };
            pendingMenu = menu;
            Send("act", messages, ActionCatalog.ActSchema(menu), OnAct, maxAgeTicks: GenDate.TicksPerHour);
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
            string result;
            try
            {
                result = option.Apply();
            }
            catch (Exception e)
            {
                result = "That went wrong: " + e.Message;
                ModLog.Error($"{pawn.LabelShort}: applying \"{option.Label}\" threw: {e}");
            }
            AddDecision($"{option.Label}: {result}");
            ModLog.Message($"{pawn.LabelShort} act: {option.Label} | {result} | Reason: {lastReason}");
        }

        public void Cancel()
        {
            current?.Cancel();
            current = null;
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
            ModLog.Message($"{pawn.LabelShort} persona: {persona}");
        }

        private void RequestPlan(bool morning)
        {
            if (morning)
                lastMorningPlanDay = LocalDay; // one morning plan per day, even if it fails
            var workTypes = ActionCatalog.PlannableWorkTypes(pawn);
            bool manual = ActionCatalog.ManualPriorities;
            if (!manual)
                WarnManualPrioritiesOff();
            var values = new Dictionary<string, string>
            {
                ["time"] = SnapshotBuilder.TimeString(pawn.Map),
                ["snapshot"] = SnapshotBuilder.Build(pawn, this),
                ["worktypes"] = workTypes.Count > 0 ? ActionCatalog.DescribeWorkTypes(pawn, workTypes) : "(none)",
                ["prioritynote"] = manual
                    ? ""
                    : "Manual priorities are OFF in this colony, so you can only turn work types on (1) or off (0), not rank them.",
                ["schedule"] = ActionCatalog.DescribeSchedule(pawn),
                ["scheduleoptions"] = string.Join(", ", ActionCatalog.ScheduleOptions()),
            };
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", SystemPrompt()),
                new KeyValuePair<string, string>("user", Prompts.Fill("plan", values)),
            };
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

        private string SystemPrompt() => Prompts.Fill("system", new Dictionary<string, string>
        {
            ["name"] = pawn.LabelShort,
            ["persona"] = persona ?? "",
            ["guidance"] = Prompts.Fill("guidance", new Dictionary<string, string>()),
        });

        private static bool warnedManualOff;

        private static void WarnManualPrioritiesOff()
        {
            if (warnedManualOff)
                return;
            warnedManualOff = true;
            Messages.Message("AIPawnControl_ManualPrioritiesOff".Translate(), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <param name="maxAgeTicks">Drop the reply if more game time than this passed while waiting (e.g. at ultrafast speed).</param>
        private void Send(string callType, List<KeyValuePair<string, string>> messages, object schema, Action<Dictionary<string, object>> onReply,
                          int maxAgeTicks = int.MaxValue)
        {
            Cancel();
            int sentTick = Find.TickManager.TicksGame;
            lastThinkTick = sentTick;
            lastThinkRealtime = UnityEngine.Time.realtimeSinceStartup;
            requestGame = Current.Game;
            requestStartedRealtime = UnityEngine.Time.realtimeSinceStartup;
            LlmRequest request = null;
            request = LlmClient.Send(callType, messages, schema, result =>
            {
                // Stale checks: only the latest request, same game, pawn still able to act.
                if (request != current || Current.Game != requestGame)
                    return;
                current = null;
                string paused = PausedReason(ignoreSleep: true);
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

        public void AddDecision(string text)
        {
            string time = pawn.Map != null ? $"{GenLocalDate.HourOfDay(pawn.Map):00}:00 " : "";
            decisions.Add(time + text);
            while (decisions.Count > MaxDecisions)
                decisions.RemoveAt(0);
        }
    }
}
