using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace AIPawnControl
{
    /// <summary>
    /// Act lines for the colony's gatherings (IDEOLOGY.md §5-§6): the ideoligion's rituals, taking a role through the role
    /// change ceremony, a party and a wedding. Each line is offered only when vanilla's own checks pass now, and starting
    /// it is vanilla's (the begin dialog's TryExecuteOn, GatheringWorker.TryExecute, TryStartMarriageCeremony).
    /// </summary>
    public static class Gatherings
    {
        private const float LowQuality = 0.25f;               // Precept_Ritual.LowQualityWarningThreshold: vanilla warns below it
        private const int GatheringSpacingTicks = 600000;     // VoluntarilyJoinableLordsStarter: 10 days between gatherings
        private const float EngagedDays = 10f;                // Pawn_RelationsTracker: a wedding comes 10+ days after the engagement
        private const float RivalOpinion = -20f;              // SocialCardUtility: "Rival" below this opinion


        private static readonly AccessTools.FieldRef<VoluntarilyJoinableLordsStarter, int> LastGatheringTick =
            AccessTools.FieldRefAccess<VoluntarilyJoinableLordsStarter, int>("lastLordStartTick");

        /// <summary>One possible line: its stable key, label, and how to start it.</summary>
        public class Line
        {
            public string key;
            public string label;
            public bool faith;     // an ideoligion line (a burial and funeral, a ritual, a role): at most one per menu
            public bool due;       // an obligation that runs out (listed first)
            public int ticksLeft;
            public System.Func<Pawn, string, string> apply; // (her, cleaned say) → result line
        }

        /// <summary>
        /// The lines for her right now. At most one ideoligion line (the user's call): something due first (a burial and
        /// funeral, a festival day), then a role she can take, then a ritual she can lead. A party and a wedding come apart.
        /// </summary>
        public static List<Line> Lines(Pawn pawn)
        {
            var lines = new List<Line>();
            if (!AIPawnControlMod.Settings.gatherings || pawn.Map == null || !pawn.Map.IsPlayerHome)
                return lines;
            Line faith = Burials.Lines(pawn).OrderBy(l => l.ticksLeft).FirstOrDefault();
            if (faith == null && Customs.Active)
            {
                var rituals = Rituals(pawn).ToList();
                faith = rituals.Where(l => l.due).OrderBy(l => l.ticksLeft).FirstOrDefault()
                        ?? Roles(pawn).FirstOrDefault()
                        ?? rituals.FirstOrDefault();
            }
            if (faith != null)
                lines.Add(faith);
            lines.AddRange(Parties(pawn));
            lines.AddRange(Weddings(pawn));
            return lines;
        }

        // ---------- Rituals (§5.1) ----------

        /// <summary>
        /// A ritual the colony does together: an ordinary ritual precept whose roles are all colonists taking part. Leaves out
        /// rituals done to someone (prisoner, animal, forced, a body-changing target, a convertee), the role change and
        /// special ones (gravship launch).
        /// </summary>
        public static bool Together(Precept_Ritual ritual) =>
            ritual.GetType() == typeof(Precept_Ritual) && ritual.def != PreceptDefOf.RoleChange && ritual.behavior?.def != null
            && (ritual.behavior.def.roles ?? new List<RitualRole>()).All(r =>
                r.GetType() == typeof(RitualRoleTag) || r.GetType() == typeof(RitualRoleColonist) || r.GetType() == typeof(RitualRoleOrganizer));

        /// <summary>Her ideoligion's rituals she could hold now, each with a plan; or why not, for the dev report.</summary>
        public static List<(Precept_Ritual ritual, RitualObligation obligation, Plan plan, string why)> RitualCandidates(Pawn pawn)
        {
            var result = new List<(Precept_Ritual, RitualObligation, Plan, string)>();
            foreach (var ritual in pawn.Ideo?.PreceptsListForReading.OfType<Precept_Ritual>() ?? Enumerable.Empty<Precept_Ritual>())
            {
                if (!Together(ritual))
                    continue;
                if (ritual.abilityOnCooldownUntilTick > Find.TickManager.TicksGame)
                {
                    // Its own cooldown (set when it starts), as vanilla's ritual button checks it (Command_Ritual.cs:101).
                    result.Add((ritual, null, null, $"on cooldown for {(ritual.abilityOnCooldownUntilTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()}"));
                    continue;
                }
                if (!ritual.activeObligations.NullOrEmpty())
                {
                    foreach (var obligation in ritual.activeObligations.Where(o => o.StillValid))
                    {
                        string why = Planned(pawn, ritual, obligation, null, out Plan plan);
                        result.Add((ritual, obligation, plan, why));
                    }
                }
                else if (ritual.isAnytime)
                {
                    if (ritual.RepeatPenaltyActive)
                        result.Add((ritual, null, null, $"held recently (the repeat penalty lasts {ritual.RepeatPenaltyTimeLeft} more)"));
                    else
                    {
                        string why = Planned(pawn, ritual, null, null, out Plan plan);
                        result.Add((ritual, null, plan, why));
                    }
                }
            }
            return result;
        }

        private static IEnumerable<Line> Rituals(Pawn pawn)
        {
            foreach (var (ritual, obligation, plan, why) in RitualCandidates(pawn))
            {
                if (why != null || plan == null || !HeldBy(ritual, pawn))
                    continue;
                yield return new Line
                {
                    key = $"ritual {ritual.Id} {obligation?.ID ?? 0}",
                    label = $"hold the {Customs.ObligationLabel(ritual, obligation)} (expected quality {plan.quality.ToStringPercent()})",
                    faith = true,
                    due = obligation != null,
                    ticksLeft = obligation != null && obligation.expires ? obligation.TicksUntilExpiration : int.MaxValue,
                    apply = (her, say) => Start(her, ritual, obligation, null, say, "started the " + Customs.ObligationLabel(ritual, obligation)),
                };
            }
        }

        /// <summary>
        /// Whether she may lead it: a ritual with a role only an ideoligion role can fill (the leader speech's speaker) goes to
        /// that role's holder, and to no one else.
        /// </summary>
        private static bool HeldBy(Precept_Ritual ritual, Pawn pawn)
        {
            var mine = Customs.RoleOf(pawn)?.def;
            return (ritual.behavior.def.roles ?? new List<RitualRole>())
                .Where(r => r.required && !r.substitutable && r.precept != null)
                .All(r => r.precept == mine);
        }

        /// <summary>A ritual ready to start: where, and who does what.</summary>
        public class Plan
        {
            public TargetInfo target;
            public RitualRoleAssignments assignments;
            public float quality;
            public Precept_Ritual ritual;

            public string Place => target.Thing != null ? "the " + target.Thing.LabelNoCount : "the gathering place";

            public int Hours => System.Math.Max(1, (int)System.Math.Round(ritual.behavior.def.durationTicks.Average / GenDate.TicksPerHour));

            /// <summary>"Mac speaks; " from the assigned roles (empty with none).</summary>
            public string Roles
            {
                get
                {
                    var parts = assignments.AllRolesForReading
                        .Select(r => (role: r, pawn: assignments.FirstAssignedPawn(r)))
                        .Where(x => x.pawn != null)
                        .Select(x => $"{x.pawn.LabelShort} as {x.role.Label.Resolve()}").ToList();
                    return parts.Count > 0 ? string.Join(", ", parts) + "; " : "";
                }
            }
        }

        /// <summary>
        /// Builds the ritual as the begin dialog would (Precept_Ritual.GetRitualBeginWindow, Dialog_BeginRitual.PostOpen) and checks
        /// what the dialog checks before it lets the player start it (BlockingIssues). Null and a plan if it can start now, or why not.
        /// </summary>
        /// <param name="anyQuality">The decision was made already (a burial's funeral): vanilla's expected quality doesn't hold it back.</param>
        public static string Planned(Pawn organizer, Precept_Ritual ritual, RitualObligation obligation, Precept_Role roleChange, out Plan plan, bool anyQuality = false)
        {
            try
            {
                return Planning(organizer, ritual, obligation, roleChange, out plan, anyQuality);
            }
            catch (System.Exception e)
            {
                plan = null; // a modded or odd ritual must never break the Act menu
                ModLog.Warning($"Planning {ritual.Label} for {organizer.LabelShort} threw: {e}");
                return "threw: " + e;
            }
        }

        private static string Planning(Pawn organizer, Precept_Ritual ritual, RitualObligation obligation, Precept_Role roleChange, out Plan plan, bool anyQuality)
        {
            plan = null;
            Map map = organizer.Map;
            if (GatheringsUtility.AnyLordJobPreventsNewRituals(map))
                return "another gathering is on";
            if (!GatheringsUtility.PawnCanStartOrContinueGathering(organizer))
                return "she can't lead a gathering now";
            IntVec3 center = SiteFinder.BaseCenter(map);
            Room heart = SiteFinder.Heart(map); // the great hall, where the colony meets: its ritual spot comes first
            // Some filters list no targets (the leader speech's): vanilla offers the ritual on any colony building whose gizmos
            // pass CanUseTarget, so those are candidates too.
            var targets = ritual.obligationTargetFilter.GetTargets(obligation, map)
                .Concat(map.listerBuildings.allBuildingsColonist.Select(b => new TargetInfo(b)))
                .Where(t => t.IsValid && ritual.CanUseTarget(t, obligation).canUse)
                .Distinct()
                .OrderByDescending(t => heart != null && t.Cell.GetRoom(map) == heart)
                .ThenBy(t => t.Cell.DistanceToSquared(center)).ToList();
            if (targets.Count == 0)
                return "no place for it";
            TargetInfo target = targets[0];
            var forced = roleChange != null ? new Dictionary<string, Pawn> { ["role_changer"] = organizer } : null;
            string cant = ritual.behavior.CanStartRitualNow(target, ritual, roleChange != null ? organizer : null, forced);
            if (!cant.NullOrEmpty())
                return cant;

            Dialog_BeginRitual.PawnFilter filter = (pawn, voluntary, allowOtherIdeos) =>
            {
                if (pawn.GetLord() != null || pawn.IsSubhuman)
                    return false;
                if (pawn.RaceProps.Animal && !ritual.behavior.def.roles.Any(r => r.AppliesToPawn(pawn, out _, target, null, null, null, skipReason: true)))
                    return false;
                return !ritual.ritualOnlyForIdeoMembers || ritual.def.allowSpectatorsFromOtherIdeos || pawn.Ideo == ritual.ideo || !voluntary || allowOtherIdeos
                       || pawn.IsPrisonerOfColony || pawn.RaceProps.Animal || (forced != null && forced.ContainsValue(pawn));
            };
            var assignments = Dialog_BeginRitual.CreateRitualRoleAssignments(ritual, target, map, filter, new List<Pawn> { organizer }, forced, roleChange != null ? organizer : null);
            assignments.FillPawns(filter, target);
            if (roleChange != null)
                assignments.SetRoleChangeSelection(roleChange);
            if (ritual.outcomeEffect != null)
                foreach (var comp in ritual.outcomeEffect.def.comps ?? new List<RitualOutcomeComp>())
                    comp.Notify_AssignmentsChanged(assignments, ritual.outcomeEffect.DataForComp(comp));
            string blocked = Blocking(ritual, target, assignments);
            if (blocked != null)
                return blocked;
            // The role change has no quality (its outcome is attendance, checked in Blocking), and vanilla's abstract quality throws for it.
            float quality = roleChange != null ? 1f : PredictedQuality(ritual, target, assignments, obligation);
            if (roleChange == null && !anyQuality && quality < LowQuality)
                return $"it would go badly (expected quality {quality.ToStringPercent()})";
            plan = new Plan { target = target, assignments = assignments, quality = quality, ritual = ritual };
            return null;
        }

        /// <summary>
        /// The begin dialog's prediction (Dialog_BeginRitual.PredictedQuality, private): the ritual's own factors, plus the
        /// colony's expectations and the repeat penalty, within the outcome's bounds. The abstract one alone runs low.
        /// </summary>
        private static float PredictedQuality(Precept_Ritual ritual, TargetInfo target, RitualRoleAssignments assignments, RitualObligation obligation)
        {
            float quality = RitualUtility.CalculateQualityAbstract(ritual, target, assignments, obligation);
            if (ritual.RepeatPenaltyActive)
                quality += ritual.RepeatQualityPenalty;
            quality += RitualOutcomeEffectWorker_FromQuality.GetExpectationsOffset(target.Map, ritual.def)?.Item2 ?? 0f;
            var outcome = ritual.outcomeEffect.def;
            return UnityEngine.Mathf.Clamp(quality, outcome.minQuality, outcome.maxQuality);
        }

        /// <summary>Dialog_BeginRitual.BlockingIssues (protected there), first issue only.</summary>
        private static string Blocking(Precept_Ritual ritual, TargetInfo target, RitualRoleAssignments assignments)
        {
            if (!assignments.Participants.Any())
                return "nobody can take part";
            if (!ritual.ignoreExtremeTemperatures && assignments.Participants.Any(p => !p.IsPrisoner && !p.SafeTemperatureRange().IncludesEpsilon(target.Cell.GetTemperature(target.Map))))
                return "it's too hot or cold there";
            if (ritual.behavior.SpectatorsRequired() && assignments.SpectatorsForReading.Count == 0)
                return "nobody to watch";
            string issue = ritual.outcomeEffect?.BlockingIssues(ritual, target, assignments).FirstOrDefault()
                           ?? ritual.obligationTargetFilter?.GetBlockingIssues(target, assignments).FirstOrDefault();
            if (issue != null)
                return issue;
            foreach (var group in (ritual.behavior.def.roles ?? new List<RitualRole>()).GroupBy(r => r.mergeId ?? r.id))
            {
                var first = group.First();
                int required = group.Count(r => r.required);
                if (required <= 0)
                    continue;
                var chosen = group.SelectMany(r => assignments.AssignedPawns(r)).ToList();
                foreach (var p in chosen)
                    if (assignments.PawnNotAssignableReason(p, first, out _) is string reason)
                        return reason;
                if (chosen.Count < required)
                    return $"no one to be the {first.Label.Resolve()}";
            }
            if (ritual.ritualOnlyForIdeoMembers && !assignments.Participants.Any(p => p.Ideo == ritual.ideo))
                return "no one of the ideoligion takes part";
            return null;
        }

        /// <summary>Plans it again from the world now and starts it the way the begin dialog does (Precept_Ritual.cs:808).</summary>
        private static string Start(Pawn her, Precept_Ritual ritual, RitualObligation obligation, Precept_Role roleChange, string say, string what,
            bool anyQuality = false, bool announce = true)
        {
            string why = Planned(her, ritual, obligation, roleChange, out Plan plan, anyQuality);
            if (why != null)
                return $"Couldn't: {why}.";
            ritual.behavior.TryExecuteOn(plan.target, her, ritual, obligation, plan.assignments, playerForced: true);
            if (plan.target.Map.lordManager.lords.All(l => !(l.LordJob is LordJob_Ritual job) || job.Ritual != ritual))
                return "Couldn't: it didn't start.";
            if (announce)
                Announce(her, what, say);
            return what.CapitalizeFirst() + ".";
        }

        /// <summary>A buried colonist's funeral, led by this colonist (Burials). Null if it started, else why not.</summary>
        public static string StartFuneral(Pawn leader, Precept_Ritual ritual, RitualObligation obligation)
        {
            string result = Start(leader, ritual, obligation, null, null, "started the " + Customs.ObligationLabel(ritual, obligation), anyQuality: true, announce: false); // the burial line already posted it
            return result.StartsWith("Couldn't") ? result : null;
        }

        /// <summary>The group chat line, as decisions have: "(started the funeral for Vicky) We say goodbye properly."</summary>
        private static void Announce(Pawn her, string what, string say) =>
            GroupChat.Instance?.Add(her.LabelShort, $"({what})" + (say != null ? $" {say}" : ""));

        // ---------- Roles (§5.2-§5.3) ----------

        public static Precept_Ritual RoleChangeRitual(Pawn pawn) =>
            pawn.Ideo?.PreceptsListForReading.OfType<Precept_Ritual>().FirstOrDefault(r => r.def == PreceptDefOf.RoleChange);

        /// <summary>
        /// Roles she can take now, through the role change ceremony: the leader and moral guide when vacant or held by her rival
        /// (single roles only), and the specialist role of her best skill when vacant. Not while she holds a role.
        /// </summary>
        public static IEnumerable<Line> Roles(Pawn pawn)
        {
            if (pawn.Ideo == null || pawn.Ideo.GetRole(pawn) != null || !pawn.IsFreeNonSlaveColonist || RoleChangeRitual(pawn) is not Precept_Ritual ceremony)
                yield break;
            SkillDef best = pawn.skills?.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).FirstOrDefault()?.def;
            foreach (var role in RitualUtility.AllRolesForPawn(pawn).Where(r => r.Active && r.RequirementsMet(pawn)))
            {
                Pawn holder = (role as Precept_RoleSingle)?.ChosenPawnValue;
                bool vacant = !role.ChosenPawns().Any();
                bool status = Customs.IsStatusRole(role);
                bool challenge = status && holder != null && holder != pawn && pawn.relations.OpinionOf(holder) < RivalOpinion;
                bool specialist = !status && vacant && best != null
                                  && role.def.roleRequirements.OfType<RoleRequirement_MinSkillAny>().Any(r => r.skills.Any(s => s.skill == best));
                if (!(status && vacant) && !challenge && !specialist)
                    continue;
                if (Planned(pawn, ceremony, null, role, out _) != null)
                    continue;
                string terms = Terms(role, pawn);
                yield return new Line
                {
                    key = $"role {role.Id} {holder?.ThingID}",
                    label = (challenge ? $"challenge {holder.LabelShort} for the {role.def.label} ideoligion role" : $"take on the {role.def.label} ideoligion role") +
                            (terms != null ? $" ({terms})" : ""),
                    faith = true,
                    apply = (her, say) => Start(her, ceremony, null, role, say,
                        challenge ? $"called a ceremony to take the {role.def.label} role from {holder.LabelShort}" : $"called a ceremony to become the {role.def.label}"),
                };
            }
        }

        /// <summary>
        /// What a role brings and costs, in vanilla's words: "gain: convert, counsel; must wear: war veil; can't do: haul, clean".
        /// Null when it has none of them.
        /// </summary>
        private static string Terms(Precept_Role role, Pawn pawn)
        {
            var parts = new List<string>();
            var gains = role.def.grantedAbilities?.Select(a => a.label).Distinct().ToList();
            if (gains?.Count > 0)
                parts.Add("gain: " + string.Join(", ", gains));
            var wear = role.apparelRequirements?.SelectMany(r => r.requirement.AllRequiredApparel(pawn.gender)).Select(t => t.label).Distinct().ToList();
            if (wear?.Count > 0)
                parts.Add("must wear: " + string.Join(", ", wear));
            var tags = role.def.roleDisabledWorkTags;
            var cant = tags == WorkTags.None ? new List<string>()
                : DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible && (w.workTags & tags) != WorkTags.None).Select(w => w.labelShort).ToList();
            if (cant.Count > 0)
                parts.Add("can't do: " + string.Join(", ", cant));
            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }

        // ---------- Parties and weddings (§6) ----------

        /// <summary>Vanilla's random gatherings (a party, a concert), when vanilla would allow one now and its 10-day spacing has passed.</summary>
        private static IEnumerable<Line> Parties(Pawn pawn)
        {
            Map map = pawn.Map;
            if (Find.TickManager.TicksGame - LastGatheringTick(map.lordsStarter) < GatheringSpacingTicks)
                yield break;
            foreach (var def in DefDatabase<GatheringDef>.AllDefsListForReading.Where(d => d.IsRandomSelectable))
            {
                if (!def.CanExecute(map, pawn))
                    continue;
                var gathering = def;
                yield return new Line
                {
                    key = "party " + def.defName,
                    label = $"throw a {def.label}",
                    ticksLeft = int.MaxValue,
                    apply = (her, say) =>
                    {
                        if (Find.TickManager.TicksGame - LastGatheringTick(her.Map.lordsStarter) < GatheringSpacingTicks || !gathering.CanExecute(her.Map, her)
                            || !gathering.Worker.TryExecute(her.Map, her))
                            return "Couldn't: it can't start now.";
                        LastGatheringTick(her.Map.lordsStarter) = Find.TickManager.TicksGame; // as TryStartGathering does
                        Announce(her, $"started a {gathering.label}", say);
                        return $"Started a {gathering.label}.";
                    },
                };
            }
        }

        /// <summary>Her wedding, when vanilla would start it on its own but for the random wait (Pawn_RelationsTracker.cs:1144-1155).</summary>
        private static IEnumerable<Line> Weddings(Pawn pawn)
        {
            foreach (var relation in pawn.relations?.DirectRelations.Where(r => r.def == PawnRelationDefOf.Fiance).ToList() ?? new List<DirectPawnRelation>())
            {
                Pawn other = relation.otherPawn;
                if (!Ready(pawn, other, relation))
                    continue;
                yield return new Line
                {
                    key = "wedding " + other.ThingID,
                    label = $"hold our wedding with {other.LabelShort} now",
                    ticksLeft = int.MaxValue,
                    apply = (her, say) =>
                    {
                        if (!Ready(her, other, relation) || !her.Map.lordsStarter.TryStartMarriageCeremony(her, other))
                            return "Couldn't: the wedding can't start now.";
                        Announce(her, $"started our wedding with {other.LabelShort}", say);
                        return $"Started our wedding with {other.LabelShort}.";
                    },
                };
            }
        }

        private static bool Ready(Pawn pawn, Pawn other, DirectPawnRelation relation) =>
            other != null && other.Spawned && other.Map == pawn.Map
            && (Find.TickManager.TicksGame - relation.startTicks) / (float)GenDate.TicksPerDay > EngagedDays
            && MarriageCeremonyUtility.AcceptableGameConditionsToStartCeremony(pawn.Map)
            && MarriageCeremonyUtility.FianceReadyToStartCeremony(pawn, other) && MarriageCeremonyUtility.FianceReadyToStartCeremony(other, pawn)
            && GatheringDefOf.MarriageCeremony.CanExecute(pawn.Map, pawn, ignoreGameConditions: true);
    }
}
