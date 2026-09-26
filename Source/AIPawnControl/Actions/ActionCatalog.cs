using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Builds what the model may choose from, for this pawn right now, and the JSON schema that enforces it.</summary>
    public static class ActionCatalog
    {
        /// <summary>Never offered or changed: turning these off stops treatment, bed rest when hurt, or firefighting.</summary>
        public static readonly HashSet<string> ProtectedWorkTypes = new HashSet<string> { "Patient", "PatientBedRest", "Firefighter" };

        public const string PriorityPrefix = "priority_";

        public static bool ManualPriorities => Find.PlaySettings.useWorkPriorities;

        public static List<WorkTypeDef> PlannableWorkTypes(Pawn pawn)
        {
            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
                return new List<WorkTypeDef>();
            return DefDatabase<WorkTypeDef>.AllDefsListForReading
                .Where(w => w.visible && !ProtectedWorkTypes.Contains(w.defName) && !pawn.WorkTypeIsDisabled(w))
                .OrderByDescending(w => w.naturalPriority)
                .ToList();
        }

        public static List<string> ScheduleOptions()
        {
            var options = new List<string> { "sleep", "work", "joy", "anything" };
            if (TimeAssignmentDefOf.Meditate != null)
                options.Add("meditate");
            return options;
        }

        public static TimeAssignmentDef ScheduleDef(string option)
        {
            switch (option)
            {
                case "sleep": return TimeAssignmentDefOf.Sleep;
                case "work": return TimeAssignmentDefOf.Work;
                case "joy": return TimeAssignmentDefOf.Joy;
                case "meditate": return TimeAssignmentDefOf.Meditate;
                default: return TimeAssignmentDefOf.Anything;
            }
        }

        public static string ScheduleOption(TimeAssignmentDef def)
        {
            if (def == TimeAssignmentDefOf.Sleep) return "sleep";
            if (def == TimeAssignmentDefOf.Work) return "work";
            if (def == TimeAssignmentDefOf.Joy) return "joy";
            if (def != null && def == TimeAssignmentDefOf.Meditate) return "meditate";
            return "anything";
        }

        /// <summary>The pawn's current timetable as compact runs, e.g. "0-5 sleep, 6-21 anything, 22-23 sleep".</summary>
        public static string DescribeSchedule(Pawn pawn)
        {
            if (pawn.timetable == null)
                return "none";
            var runs = new List<string>();
            int start = 0;
            for (int hour = 1; hour <= 24; hour++)
            {
                if (hour < 24 && pawn.timetable.GetAssignment(hour) == pawn.timetable.GetAssignment(start))
                    continue;
                string option = ScheduleOption(pawn.timetable.GetAssignment(start));
                runs.Add(start == hour - 1 ? $"{start} {option}" : $"{start}-{hour - 1} {option}");
                start = hour;
            }
            return string.Join(", ", runs);
        }

        /// <summary>Current priorities read back from the game, highest first, e.g. "Doctor 1, Cooking 2, Hauling off".</summary>
        public static string DescribePriorities(Pawn pawn)
        {
            var workTypes = PlannableWorkTypes(pawn);
            if (workTypes.Count == 0)
                return "none";
            return string.Join(", ", workTypes
                .Select(w => (w, p: pawn.workSettings.GetPriority(w)))
                .OrderBy(x => x.p == 0 ? 99 : x.p)
                .Select(x => $"{x.w.labelShort.CapitalizeFirst()} {(x.p == 0 ? "off" : x.p.ToString())}"));
        }

        public static string DescribeWorkTypes(Pawn pawn, List<WorkTypeDef> workTypes)
        {
            var sb = new StringBuilder();
            foreach (var w in workTypes)
            {
                string skills = string.Join(", ", w.relevantSkills
                    .Select(s => pawn.skills?.GetSkill(s))
                    .Where(s => s != null && !s.TotallyDisabled)
                    .Select(s => s.passion == Passion.None ? $"{s.def.LabelCap} {s.Level}" : $"{s.def.LabelCap} {s.Level} ({SnapshotBuilder.PassionLabel(s.passion)})"));
                int now = pawn.workSettings.GetPriority(w);
                sb.AppendLine($"- {PriorityPrefix}{w.defName}: {w.labelShort.CapitalizeFirst()} (now {now}){(skills.Length > 0 ? ", " + skills : "")}");
            }
            return sb.ToString().TrimEnd();
        }

        public static Dictionary<string, object> PlanSchema(List<WorkTypeDef> workTypes)
        {
            var priorityEnum = new List<object>();
            int max = ManualPriorities ? 4 : 1;
            for (int i = 0; i <= max; i++)
                priorityEnum.Add(i);

            var properties = new Dictionary<string, object>
            {
                ["reason"] = ReasonSchema(),
                ["intent"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 200 },
                ["schedule"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = ScheduleOptions().Cast<object>().ToList() },
                    ["minItems"] = 24,
                    ["maxItems"] = 24,
                },
            };
            var required = new List<object> { "reason", "intent", "schedule" };
            foreach (var w in workTypes)
            {
                properties[PriorityPrefix + w.defName] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = priorityEnum };
                required.Add(PriorityPrefix + w.defName);
            }
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        // ---------- Act ----------

        private const int MaxRecreation = 8;
        private const int MaxGoToPawns = 6;
        private const int MaxGoToRooms = 5;
        private static readonly int[] KeepGoingHours = { 2, 4, 8 };

        public class ActOption
        {
            public int Id;
            public string Label;
            public Func<string, string> Apply; // runs on the main thread when chosen, gets her cleaned "say"; returns a readable result
            public bool IsTalk; // "say" is her opening line; for anything else it's a remark out loud
        }

        /// <summary>Every action valid for this pawn right now, numbered. The schema only allows these ids.</summary>
        public static List<ActOption> BuildActMenu(Pawn pawn, PawnMind mind)
        {
            var options = new List<ActOption>();
            void Add(string label, Func<string> apply) => options.Add(new ActOption { Id = options.Count + 1, Label = label, Apply = _ => apply() });

            foreach (int hours in KeepGoingHours)
                Add($"keep going, check back in {hours}h", () => mind.KeepGoing(hours));

            if (MindActions.CanRestNow(pawn))
                Add("rest now", () => MindActions.Rest(mind));

            foreach (var (other, interaction) in AvailableInteractions(pawn, mind))
                options.Add(new ActOption
                {
                    Id = options.Count + 1,
                    Label = $"talk to {other.LabelShort}: {interaction.label}",
                    Apply = say => MindActions.TalkTo(mind, other, interaction, say),
                    IsTalk = true,
                });

            foreach (var joy in AvailableRecreation(pawn))
                Add("recreation: " + JoyLabel(joy), () => MindActions.Recreation(mind, joy));

            foreach (var other in pawn.Map.mapPawns.FreeColonistsSpawned
                         .Where(p => p != pawn)
                         .OrderBy(p => p.Position.DistanceToSquared(pawn.Position))
                         .Take(MaxGoToPawns)
                         .ToList())
                Add("walk over to " + other.LabelShort + " (no conversation)", () => MindActions.GoTo(mind, other));

            foreach (var room in NotableRooms(pawn))
                Add("go to the " + room.GetRoomRoleLabel(), () => MindActions.GoTo(mind, room));

            if (mind.ExtraPlansLeft > 0)
                Add("re-plan my day", () => mind.TryExtraPlan(force: false) ? "Re-planning." : "Couldn't re-plan now.");

            return options;
        }

        public static string DescribeMenu(List<ActOption> menu) => string.Join("\n", menu.Select(o => $"{o.Id}: {o.Label}"));

        /// <summary>Looser than the 160 characters we show, because maxLength cuts mid-sentence (SpeechLog.Clean trims to a sentence end).</summary>
        public static Dictionary<string, object> SaySchema() => new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 220 };

        /// <summary>Caps rambling (a confused model once wrote 3,000 characters). Constrained decoding enforces it.</summary>
        private static Dictionary<string, object> ReasonSchema() => new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 400 };

        public static Dictionary<string, object> ActSchema(List<ActOption> menu) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["reason"] = ReasonSchema(),
                ["choice"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = menu.Select(o => (object)o.Id).ToList() },
                ["say"] = SaySchema(),
            },
            ["required"] = new List<object> { "reason", "choice", "say" },
            ["additionalProperties"] = false,
        };

        /// <summary>
        /// Never offered: their workers assume prisoners, slaves, animals, rituals or roles, or they change
        /// ideoligion (ConvertIdeoAttempt passes RandomSelectionWeight for any two colonists of different ideos).
        /// </summary>
        public static readonly HashSet<string> BlockedInteractions = new HashSet<string>
        {
            "RecruitAttempt", "EnslaveAttempt", "ReduceWill", "Suppress", "SparkJailbreak", "SparkSlaveRebellion",
            "BuildRapport", "ConvertIdeoAttempt", "Convert_Success", "Convert_Failure", "Counsel_Success", "Counsel_Failure",
            "Reassure", "Trial_Accuse", "Trial_Defend", "Speech_AcceptRole", "Speech_RemoveRole",
            "PrisonerStudyAnomaly", "InterrogateIdentity", "AnimalChat", "TrainAttempt", "TameAttempt", "Nuzzle", "ReleaseToWild",
        };

        /// <summary>Change relationships for good, so they get a much longer cooldown.</summary>
        public static readonly HashSet<string> LifeChangingInteractions = new HashSet<string> { "RomanceAttempt", "MarriageProposal", "Breakup" };

        public static bool IsNegative(InteractionDef def) => def.socialFightBaseChance > 0f || def.defName == "Insult" || def.defName == "Slight";

        /// <summary>
        /// For each nearby awake colonist, every interaction vanilla would consider for this pair right now
        /// (RandomSelectionWeight > 0, which also covers modded defs), minus blocked ones and those on cooldown.
        /// </summary>
        public static List<(Pawn, InteractionDef)> AvailableInteractions(Pawn pawn, PawnMind mind)
        {
            var result = new List<(Pawn, InteractionDef)>();
            if (pawn.interactions == null || !SocialInteractionUtility.CanInitiateInteraction(pawn))
                return result;
            var targets = pawn.Map.mapPawns.FreeColonistsSpawned
                .Where(p => p != pawn && p.Awake() && !p.Downed && !p.Drafted && p.RaceProps.Humanlike && p.interactions != null)
                .OrderBy(p => p.Position.DistanceToSquared(pawn.Position))
                .Take(MaxGoToPawns)
                .ToList();
            foreach (var target in targets)
            {
                foreach (var def in DefDatabase<InteractionDef>.AllDefsListForReading)
                {
                    if (BlockedInteractions.Contains(def.defName) || mind.OnCooldown(def, target))
                        continue;
                    try
                    {
                        if (def.Worker.RandomSelectionWeight(pawn, target) > 0f)
                            result.Add((target, def));
                    }
                    catch (Exception e)
                    {
                        ModLog.Warning($"Interaction {def.defName} threw in RandomSelectionWeight: {e.Message}");
                    }
                }
            }
            return result;
        }

        /// <summary>Mirrors JobGiver_GetJoy's filters, and only keeps activities that can actually start right now.</summary>
        public static List<JoyGiverDef> AvailableRecreation(Pawn pawn)
        {
            var joy = pawn.needs?.joy;
            if (joy == null || joy.CurLevel >= 0.99f)
                return new List<JoyGiverDef>();
            var result = new List<JoyGiverDef>();
            var seen = new HashSet<string>();
            foreach (var def in DefDatabase<JoyGiverDef>.AllDefsListForReading
                         .Where(d => !joy.tolerances.BoredOf(d.joyKind) && d.Worker.CanBeGivenTo(pawn))
                         .OrderByDescending(d => d.Worker.GetChance(pawn)))
            {
                if (result.Count >= MaxRecreation)
                    break;
                if (def.Worker.GetChance(pawn) <= 0f || !seen.Add(JoyLabel(def)))
                    continue;
                try
                {
                    if (def.Worker.TryGiveJob(pawn) != null)
                        result.Add(def);
                }
                catch (Exception e)
                {
                    ModLog.Warning($"Recreation {def.defName} threw while checking availability: {e.Message}");
                }
            }
            return result;
        }

        public static string JoyLabel(JoyGiverDef def)
        {
            if (!def.label.NullOrEmpty()) return def.label;
            if (def.jobDef != null && !def.jobDef.label.NullOrEmpty()) return def.jobDef.label;
            return GenText.SplitCamelCase(def.defName.Replace("_", " ")).ToLower();
        }

        /// <summary>The nearest room of each role (bedroom, dining room...), excluding the one the pawn is in.</summary>
        private static List<Room> NotableRooms(Pawn pawn)
        {
            Room here = pawn.GetRoom();
            return pawn.Map.regionGrid.AllRooms
                .Where(r => r != here && !r.PsychologicallyOutdoors && !r.Fogged && r.Role != null && r.Role != RoomRoleDefOf.None)
                .GroupBy(r => r.GetRoomRoleLabel())
                .Select(g => g.OrderBy(r => r.Cells.First().DistanceToSquared(pawn.Position)).First())
                .Take(MaxGoToRooms)
                .ToList();
        }

        public const int MaxChatReply = 300;

        /// <summary>Chat reply: what she says back, an optional action from the current menu (0 = none), and a note for later.</summary>
        public static Dictionary<string, object> ChatSchema(List<ActOption> menu) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["reply"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = MaxChatReply + 60 },
                ["act"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = new List<object> { 0 }.Concat(menu.Select(o => (object)o.Id)).ToList() },
                ["note"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 120 },
            },
            ["required"] = new List<object> { "reply", "act", "note" },
            ["additionalProperties"] = false,
        };

        public static Dictionary<string, object> PersonaSchema() => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["persona"] = new Dictionary<string, object> { ["type"] = "string" },
                ["say"] = SaySchema(),
            },
            ["required"] = new List<object> { "persona", "say" },
            ["additionalProperties"] = false,
        };
    }
}
