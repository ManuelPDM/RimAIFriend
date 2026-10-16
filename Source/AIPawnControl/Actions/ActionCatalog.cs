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
            public string Key; // the same option in a menu built later: "keep", "talk", "base", "post", or the life-changing talk and who
            public string Label;
            public Func<string, string> Apply; // runs on the main thread when chosen; gets her message for a life-changing talk, else null; returns a readable result
            public Func<Dictionary<string, object>, string> ApplyReply; // instead of Apply when it needs more of the reply ("with")
            public bool IsTalk; // a life-changing talk in the Message call: her message is its line
            public bool Announces; // a gathering, ritual, role or burial line: the Act's "say" goes with its group chat post
            public Pawn Target; // who she talks to (set when applied, for "talk to someone")
            public List<Pawn> TalkTargets; // "talk to someone": who "with" may name
        }

        /// <summary>Every action valid for this pawn right now, numbered. The schema only allows these ids.</summary>
        /// <param name="inConversation">Chat and Message: she's already talking, so there's no "talk to someone", and no "keep going" (their "act" 0 is that).</param>
        /// <param name="talkingTo">The Message call: who she's talking to, for asking them out, proposing, breaking up, or spending time together.</param>
        public static List<ActOption> BuildActMenu(Pawn pawn, PawnMind mind, bool inConversation = false, Pawn talkingTo = null)
        {
            var options = new List<ActOption>();
            string doing = pawn.GetJobReport()?.TrimEnd('.');
            if (!inConversation)
                options.Add(new ActOption { Id = options.Count + 1, Key = "keep", Label = string.IsNullOrEmpty(doing) ? "keep going" : $"keep going ({doing})",
                    Apply = _ => mind.KeepGoing(KeepGoingHours) });

            var threats = DangerResponse.Threats(pawn.Map);
            if (threats.Count > 0)
            {
                if (!inConversation && DangerPlan.Part(DangerPlan.Of(pawn.Map), pawn) is string part)
                    options[0].Label = $"keep to my part in the plan ({part})";
                options.AddRange(DangerResponse.Options(pawn, mind, threats, options.Count + 1)); // the danger menu: nothing else while it lasts
                return options;
            }

            var targets = TalkTargets(pawn, mind);
            if (!inConversation && targets.Count > 0)
            {
                var talk = new ActOption { Id = options.Count + 1, Key = "talk", Label = "talk to someone", TalkTargets = targets };
                talk.ApplyReply = reply =>
                {
                    string name = reply.Str("with");
                    talk.Target = targets.FirstOrDefault(p => p.LabelShort == name) ?? targets[0];
                    return mind.StartMessage(talk.Target);
                };
                options.Add(talk);
            }

            foreach (var (other, interaction) in LifeChangingTalks(pawn, mind, Nearby(pawn).Where(p => p == talkingTo).ToList()))
                options.Add(new ActOption
                {
                    Id = options.Count + 1,
                    Key = $"{interaction.defName} {other.ThingID}",
                    Label = LifeChangingLabel(interaction, other),
                    Target = other,
                    Apply = line => MindActions.TalkTo(mind, other, interaction, line),
                    IsTalk = true,
                });

            if (talkingTo != null && talkingTo != MindManager.Instance?.PlayingAs) // the player's colonist is the player's to move
                foreach (var activity in Together.For(pawn, talkingTo))
                {
                    string key = activity.key, label = activity.label;
                    options.Add(new ActOption
                    {
                        Id = options.Count + 1,
                        Key = $"together {key} {talkingTo.ThingID}",
                        Label = $"spend time with {talkingTo.LabelShort} now: {label}",
                        Target = talkingTo,
                        Apply = _ => Together.Invite(mind, talkingTo, key, label),
                    });
                }

            if (BaseCall.AnythingToDo(pawn))
                options.Add(new ActOption { Id = options.Count + 1, Key = "base", Label = BaseCall.MenuLabel(pawn), Apply = _ => BaseCall.Start(mind) });

            foreach (var line in Gatherings.Lines(pawn))
            {
                var apply = line.apply;
                options.Add(new ActOption { Id = options.Count + 1, Key = line.key, Label = line.label, Apply = say => apply(pawn, say), Announces = true });
            }

            if (!inConversation && GroupChat.Instance?.MayPost(mind) == true)
                options.Add(new ActOption { Id = options.Count + 1, Key = "post", Label = "post in the group chat (everyone reads it, wherever they are)", Apply = _ => GroupChat.StartPost(mind) });

            return options;
        }

        /// <summary>"ask Kira out (your message is how you ask)", and the same for proposing and breaking up.</summary>
        private static string LifeChangingLabel(InteractionDef def, Pawn other)
        {
            switch (def.defName)
            {
                case "RomanceAttempt": return $"ask {other.LabelShort} out (your message is how you ask)";
                case "MarriageProposal": return $"propose to {other.LabelShort} (your message is the proposal)";
                case "Breakup": return $"break up with {other.LabelShort} (your message is how you end it)";
                default: return $"{def.label} with {other.LabelShort} (your message is what you say)";
            }
        }

        /// <summary>
        /// The option she picked, taken from a menu built now (so it acts on the world as it is), or null if it's no longer
        /// offered: the person she'd talk to left, the base has nothing to do, it isn't her turn in the group chat.
        /// </summary>
        public static ActOption StillOffered(ActOption picked, List<ActOption> now, Dictionary<string, object> reply)
        {
            var option = now.FirstOrDefault(o => o.Key == picked.Key);
            string name = reply.Str("with");
            if (option?.TalkTargets != null && picked.TalkTargets.Any(p => p.LabelShort == name) && !option.TalkTargets.Any(p => p.LabelShort == name))
                return null;
            return option;
        }

        public static string DescribeMenu(List<ActOption> menu) => string.Join("\n", menu.Select(o => $"{o.Id}: {o.Label}"));

        /// <param name="memories">The memory ids shown in [On my mind]; "memory" names the one she drew on, or 0.</param>
        public static Dictionary<string, object> ActSchema(List<ActOption> menu, List<int> memories)
        {
            var names = menu.FirstOrDefault(o => o.TalkTargets != null)?.TalkTargets.Select(p => p.LabelShort).ToList() ?? new List<string>();
            names.Add(NoOne);
            var props = new Dictionary<string, object>
            {
                ["reason"] = Schema.Reason(),
                ["choice"] = Schema.IntEnum(menu.Select(o => o.Id)),
                ["with"] = Schema.StrEnum(names),
            };
            if (menu.Any(o => o.Announces))
                props["say"] = Schema.Say(); // only with a line that posts to the group chat (act-announce.txt)
            props["memory"] = MemorySchema(memories);
            return Schema.Obj(props);
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

        /// <summary>The nearest awake colonists she could walk over to now, at most 5 (asking out, proposing, breaking up).</summary>
        public static List<Pawn> Nearby(Pawn pawn)
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
        /// Who "talk to someone" reaches: the other minds and the colonist the player plays, at any distance, and colonists
        /// without a mind in vanilla's interaction range. Nobody while she's out of messages.
        /// </summary>
        public static List<Pawn> TalkTargets(Pawn pawn, PawnMind mind)
        {
            var manager = MindManager.Instance;
            if (manager == null || mind.MessagesLeft <= 0)
                return new List<Pawn>();
            return manager.Minds.Where(m => m != mind && m.persona != null && !m.pawn.Dead && !m.pawn.Destroyed).Select(m => m.pawn)
                .Concat(Nearby(pawn).Where(p => manager.MindOf(p) == null && MindActions.InRange(pawn, p)))
                .Append(manager.PlayingAs).Where(p => p != null).Distinct().ToList();
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
        /// Chat: an optional action from the current menu (0 = none), then what she says back. "act" comes first so
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
        });
    }
}
