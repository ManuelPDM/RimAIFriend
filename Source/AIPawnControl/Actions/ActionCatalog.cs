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

        // ---------- Act (STREAMLINE.md §3) ----------

        private const int MaxTalkTargets = 5;
        public const int KeepGoingHours = 2;
        public const string NoOne = "-";
        public static readonly string[] Tones = { "positive", "negative" };

        public class ActOption
        {
            public int Id;
            public string Label;
            public Func<string, string> Apply; // runs on the main thread when chosen, gets her cleaned "say"; returns a readable result
            public Func<Dictionary<string, object>, string, string> ApplyReply; // instead of Apply when it needs more of the reply ("with", "tone")
            public bool IsTalk; // "say" is her opening line; for anything else it's a remark out loud
            public Pawn Target; // who she talks to, for a talk (set when applied, for "talk to someone")
            public bool OwnRemark; // a later call makes her remark (the Base call), so the Act's "say" is dropped
            public List<Pawn> TalkTargets; // "talk to someone": who "with" may name
        }

        /// <summary>Every action valid for this pawn right now, numbered. The schema only allows these ids.</summary>
        /// <param name="inConversation">Chat and Reply: she's already talking, so there's no "talk to someone".</param>
        public static List<ActOption> BuildActMenu(Pawn pawn, PawnMind mind, bool inConversation = false)
        {
            var options = new List<ActOption>();
            void Add(string label, Func<string> apply) => options.Add(new ActOption { Id = options.Count + 1, Label = label, Apply = _ => apply() });

            string doing = pawn.GetJobReport()?.TrimEnd('.');
            Add(string.IsNullOrEmpty(doing) ? "keep going" : $"keep going ({doing})", () => mind.KeepGoing(KeepGoingHours));

            var targets = TalkTargets(pawn);
            if (!inConversation && targets.Count > 0)
            {
                var talk = new ActOption { Id = options.Count + 1, Label = "talk to someone", IsTalk = true, TalkTargets = targets };
                talk.ApplyReply = (reply, say) =>
                {
                    string name = reply.Str("with");
                    Pawn target = targets.FirstOrDefault(p => p.LabelShort == name) ?? targets[0];
                    talk.Target = target;
                    bool positive = reply.Str("tone") != "negative";
                    return MindActions.Talk(mind, target, positive, say);
                };
                options.Add(talk);
            }

            foreach (var (other, interaction) in LifeChangingTalks(pawn, mind, targets))
                options.Add(new ActOption
                {
                    Id = options.Count + 1,
                    Label = LifeChangingLabel(interaction, other),
                    Target = other,
                    Apply = say => MindActions.TalkTo(mind, other, interaction, say),
                    IsTalk = true,
                });

            if (BaseCall.AnythingToDo(pawn))
                options.Add(new ActOption { Id = options.Count + 1, Label = BaseCall.MenuLabel(pawn), Apply = _ => BaseCall.Start(mind), OwnRemark = true });

            if (!inConversation && GroupChat.Instance?.MayPost(mind) == true)
                options.Add(new ActOption { Id = options.Count + 1, Label = "post in the group chat (everyone reads it, wherever they are)", Apply = _ => GroupChat.StartPost(mind), OwnRemark = true });

            return options;
        }

        /// <summary>"ask Kira out", "propose to Kira", "break up with Kira".</summary>
        private static string LifeChangingLabel(InteractionDef def, Pawn other)
        {
            switch (def.defName)
            {
                case "RomanceAttempt": return $"ask {other.LabelShort} out";
                case "MarriageProposal": return $"propose to {other.LabelShort}";
                case "Breakup": return $"break up with {other.LabelShort}";
                default: return $"{def.label} with {other.LabelShort}";
            }
        }

        public static string DescribeMenu(List<ActOption> menu) => string.Join("\n", menu.Select(o => $"{o.Id}: {o.Label}"));

        /// <param name="memories">The memory ids shown in [On my mind]; "memory" names the one she drew on, or 0.</param>
        public static Dictionary<string, object> ActSchema(List<ActOption> menu, List<int> memories)
        {
            var names = menu.FirstOrDefault(o => o.TalkTargets != null)?.TalkTargets.Select(p => p.LabelShort).ToList() ?? new List<string>();
            names.Add(NoOne);
            return Schema.Obj(new Dictionary<string, object>
            {
                ["reason"] = Schema.Reason(),
                ["choice"] = Schema.IntEnum(menu.Select(o => o.Id)),
                ["with"] = Schema.StrEnum(names),
                ["tone"] = Schema.StrEnum(Tones),
                ["say"] = Schema.Say(),
                ["memory"] = MemorySchema(memories),
            });
        }

        private static Dictionary<string, object> MemorySchema(List<int> memories) => Schema.IntEnum(new[] { 0 }.Concat(memories));

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

        /// <summary>The nearest awake colonists she could talk to now, at most 5.</summary>
        public static List<Pawn> TalkTargets(Pawn pawn)
        {
            if (pawn.interactions == null || !SocialInteractionUtility.CanInitiateInteraction(pawn))
                return new List<Pawn>();
            return pawn.Map.mapPawns.FreeColonistsSpawned
                .Where(p => p != pawn && p.Awake() && !p.Downed && !p.Drafted && p.RaceProps.Humanlike && p.interactions != null
                            && SnapshotBuilder.Nearby(pawn, p)) // only people she can talk to now: no decision spent on someone across the map
                .OrderBy(p => p.Position.DistanceToSquared(pawn.Position))
                .Take(MaxTalkTargets)
                .ToList();
        }

        /// <summary>
        /// The interactions vanilla would consider for this pair right now (RandomSelectionWeight > 0, which also covers modded
        /// defs), minus blocked ones and those on cooldown, with their weights.
        /// </summary>
        public static List<(InteractionDef def, float weight)> Interactions(Pawn pawn, Pawn target, PawnMind mind)
        {
            var result = new List<(InteractionDef, float)>();
            foreach (var def in DefDatabase<InteractionDef>.AllDefsListForReading)
            {
                if (BlockedInteractions.Contains(def.defName) || mind.OnCooldown(def, target))
                    continue;
                try
                {
                    float weight = def.Worker.RandomSelectionWeight(pawn, target);
                    if (weight > 0f)
                        result.Add((def, weight));
                }
                catch (Exception e)
                {
                    ModLog.Warning($"Interaction {def.defName} threw in RandomSelectionWeight: {e.Message}");
                }
            }
            return result;
        }

        /// <summary>Romance, proposals and breakups vanilla allows for a pair right now: their own menu lines.</summary>
        private static List<(Pawn, InteractionDef)> LifeChangingTalks(Pawn pawn, PawnMind mind, List<Pawn> targets) =>
            targets.SelectMany(t => Interactions(pawn, t, mind).Where(x => LifeChangingInteractions.Contains(x.def.defName)).Select(x => (t, x.def))).ToList();

        public const int MaxChatReply = 300;

        /// <summary>
        /// Chat and Reply: an optional action from the current menu (0 = none), then what she says back. "act" comes first so
        /// the words are written knowing what she does (PHASE6.md §4.1). Anything for later is just what she said.
        /// </summary>
        public static Dictionary<string, object> ChatSchema(List<ActOption> menu, List<int> memories) => Schema.Obj(new Dictionary<string, object>
        {
            ["act"] = Schema.IntEnum(new[] { 0 }.Concat(menu.Select(o => o.Id))),
            ["reply"] = Schema.Str(MaxChatReply + 60),
            ["memory"] = MemorySchema(memories),
        });

        public static Dictionary<string, object> PersonaSchema() => Schema.Obj(new Dictionary<string, object>
        {
            ["persona"] = Schema.Str(),
            ["say"] = Schema.Say(),
        });
    }
}
