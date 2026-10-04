using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    /// <summary>
    /// The colony's plan for one danger (DANGER_RESPONSE.md §9): one mind (the leader, or whoever thinks first) picks a
    /// plan and who fights and who goes inside; the others wait for it, then think with their part in it. Made once per
    /// danger, never re-planned. Code keeps each fighter at her part until she leaves it. Not saved: after a reload the
    /// danger starts again, and so does the plan.
    /// </summary>
    public class DangerPlan : GameComponent
    {
        public const string Hold = "hold", Out = "out", Save = "save", None = "none";
        private const int CheckInterval = 250;
        private const int SpotRadius = 8; // a fighter's spot is at most this far from the base's edge
        private const int WaitTicks = GenDate.TicksPerDay; // Wait_Combat must have an expiry while undrafted; longer than any raid

        /// <summary>One plan on the list: what the caller can pick.</summary>
        public class Option
        {
            public string kind;
            public string label;
            public Pawn victim; // for Save
        }

        public class Plan
        {
            public Map map;
            public PawnMind caller;
            public bool made;
            public Option picked;
            public IntVec3 edge = IntVec3.Invalid; // the base's cell nearest the enemy, for Hold and Out
            public IntVec3 enemyAt = IntVec3.Invalid; // where that enemy was, for cover against it
            public string side;                    // "east", for the labels
            public readonly Dictionary<Pawn, IntVec3> fighters = new Dictionary<Pawn, IntVec3>(); // her spot (Invalid for Save)
            public readonly List<Pawn> inside = new List<Pawn>();
            public readonly HashSet<Pawn> left = new HashSet<Pawn>(); // broke from their part
            public bool goneOut; // Out: everyone arrived, they went

            public bool InRole(Pawn p) => !left.Contains(p) && (fighters.ContainsKey(p) || inside.Contains(p));
        }

        private readonly Dictionary<Map, Plan> plans = new Dictionary<Map, Plan>();

        private static DangerPlan instance;
        private readonly Game game;

        public static DangerPlan Instance => instance != null && instance.game == Current.Game ? instance : null;

        public DangerPlan(Game game)
        {
            this.game = game;
            instance = this;
        }

        public static Plan Of(Map map) => map != null && Instance != null && Instance.plans.TryGetValue(map, out var plan) ? plan : null;

        // ---------- Making the plan ----------

        private static bool Able(PawnMind m) => m.persona != null && m.PausedReason(ignoreSleep: true) == null && !m.Unreachable;

        /// <summary>The minds on this map that can be given a part.</summary>
        private static List<PawnMind> Candidates(Map map) =>
            (MindManager.Instance?.Minds ?? new List<PawnMind>()).Where(m => m.pawn != null && m.pawn.Map == map && m.pawn.playerSettings != null
                && m.PausedReason(ignoreSleep: true) == null).ToList();

        private static bool CanFight(Pawn p) => !p.WorkTagIsDisabled(WorkTags.Violent);

        /// <summary>
        /// Called when a mind would think about the danger. True while the plan isn't made yet: she waits for it. If nobody
        /// is making it, the leader makes it if she can think now, otherwise this mind does.
        /// </summary>
        public bool Waiting(PawnMind mind)
        {
            Map map = mind.pawn.Map;
            if (!plans.TryGetValue(map, out var plan))
                plans[map] = plan = new Plan { map = map };
            if (plan.made)
                return false;
            if (plan.caller != null && plan.caller.Thinking)
                return true;
            var leader = (MindManager.Instance?.Minds ?? new List<PawnMind>()).FirstOrDefault(m => m.pawn?.Map == map
                && Customs.RoleOf(m.pawn)?.def.leaderRole == true && Able(m) && !m.Thinking);
            if (Ask(leader ?? mind, plan, leader != null))
                return true;
            plan.made = true; // nothing to pick: no plan, everyone thinks for herself
            return false;
        }

        /// <summary>The plans possible now: Hold and Out need the base's rooms and someone who can fight; Save, someone attacked.</summary>
        internal static List<Option> Options(Plan plan, List<PawnMind> candidates)
        {
            var options = new List<Option>();
            var threats = DangerResponse.Threats(plan.map);
            if (candidates.Any(m => CanFight(m.pawn)))
            {
                if (plan.edge.IsValid)
                {
                    options.Add(new Option { kind = Hold, label = $"Hold the base: the fighters wait just outside the base on the {plan.side} side, where the enemy is, and fight what comes to them. Home advantage: they're in place first, and the enemy has to come to them" });
                    options.Add(new Option { kind = Out, label = $"Go out together: the fighters meet just outside the base on the {plan.side} side, and once they're all there, go after the enemy together" });
                }
                foreach (var group in threats.Select(t => (t, victim: DangerResponse.Victim(t))).Where(x => x.victim != null).GroupBy(x => x.victim))
                    options.Add(new Option { kind = Save, victim = group.Key,
                        label = $"Save {group.Key.LabelShort}: the fighters run straight to {group.Key.LabelShort} and go after the {DangerResponse.Label(group.First().t)} attacking them" });
            }
            options.Add(new Option { kind = None, label = "No fighting" });
            return options;
        }

        /// <summary>Where the enemy meets the base: the base's cell nearest the threat nearest the base, and which side that is.</summary>
        internal static void FindEdge(Plan plan)
        {
            var inside = DangerResponse.InsideCells(plan.map);
            var threats = DangerResponse.Threats(plan.map);
            if (inside.Count == 0 || threats.Count == 0)
                return;
            var near = threats.Select(t => (t, cell: inside.MinBy(c => c.DistanceToSquared(t.Position))))
                .MinBy(x => x.cell.DistanceToSquared(x.t.Position));
            plan.edge = near.cell;
            plan.enemyAt = near.t.Position;
            var center = new IntVec3((int)inside.Average(c => c.x), 0, (int)inside.Average(c => c.z));
            var d = near.t.Position - center;
            plan.side = Mathf.Abs(d.x) > Mathf.Abs(d.z) ? (d.x > 0 ? "east" : "west") : (d.z > 0 ? "north" : "south");
        }

        /// <summary>"- Dylan: rifle, Shooting 8, Melee 3, health 100%" for each candidate. By name: numbers got mixed up with the plans'.</summary>
        private static string ColonistLines(List<PawnMind> candidates, Pawn me) => string.Join("\n", candidates.Select(m =>
        {
            Pawn p = m.pawn;
            string fight = CanFight(p)
                ? $"{DangerResponse.Weapon(p) ?? "no weapon"}, Shooting {p.skills?.GetSkill(SkillDefOf.Shooting).Level ?? 0}, Melee {p.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0}"
                : "can't fight";
            return $"- {p.LabelShort}{(p == me ? " (me)" : "")}: {fight}, health {p.health.summaryHealth.SummaryHealthPercent.ToStringPercent()}{(p.Awake() ? "" : ", asleep")}";
        }));

        /// <summary>Sends the Plan call. False if there's nothing to pick (only "No fighting" and nowhere inside).</summary>
        private bool Ask(PawnMind mind, Plan plan, bool leader)
        {
            FindEdge(plan);
            var candidates = Candidates(plan.map);
            var options = Options(plan, candidates);
            bool canInside = DangerResponse.CanGoInside(plan.map);
            if (options.Count < 2 && !canInside)
                return false;
            var fightNames = candidates.Where(m => CanFight(m.pawn)).Select(m => m.pawn.LabelShort).ToList();
            var allNames = candidates.Select(m => m.pawn.LabelShort).ToList();
            string plansText = string.Join("\n", options.Select((o, i) => $"{i + 1}. {o.label}"));
            var messages = PromptBuilder.Build("plan", mind, new Dictionary<string, string>
            {
                ["why"] = leader ? "you lead the colony" : "you're the first to react",
                ["colonists"] = ColonistLines(candidates, mind.pawn),
                ["plans"] = plansText,
                ["noinside"] = canInside ? "" : " There's no inside to go to now, so leave it empty.",
            });
            var schema = Schema.Obj(new Dictionary<string, object>
            {
                ["reason"] = Schema.Reason(),
                ["plan"] = Schema.IntEnum(Enumerable.Range(1, options.Count)),
                ["fighters"] = Names(fightNames),
                ["inside"] = Names(canInside ? allNames : new List<string>()),
                ["say"] = Schema.Say(),
            });
            plan.caller = mind;
            ModLog.Message($"{mind.pawn.LabelShort} makes the danger plan ({(leader ? "the leader" : "first to react")}):\n{plansText}");
            mind.Send("plan", messages, schema, reply => OnReply(plan, candidates, options, reply),
                stillValid: () => DangerResponse.Threats(plan.map).Count == 0 ? "the danger is over" : mind.PausedReason(ignoreSleep: true));
            mind.DangerThought();
            return true;
        }

        private static Dictionary<string, object> Names(List<string> names) =>
            Schema.Arr(Schema.StrEnum(names.Count > 0 ? names : new List<string> { "-" }), names.Count);

        private void OnReply(Plan plan, List<PawnMind> candidates, List<Option> options, Dictionary<string, object> reply)
        {
            if (plan.made || !plans.TryGetValue(plan.map, out var current) || current != plan)
                return; // made meanwhile (the dev tool), or the danger ended
            int pick = reply.Int("plan", -1);
            if (pick < 1 || pick > options.Count)
            {
                ModLog.Warning($"{plan.caller.pawn.LabelShort}: plan reply chose {pick}, which isn't on the list.");
                return; // the next mind to think asks again
            }
            var named = new Dictionary<string, List<PawnMind>>();
            foreach (var key in new[] { "fighters", "inside" })
                named[key] = (reply.TryGetValue(key, out object v) && v is List<object> list ? list.OfType<string>() : Enumerable.Empty<string>()).Distinct()
                    .Select(n => candidates.FirstOrDefault(m => m.pawn.LabelShort == n)).Where(m => m != null && m.PausedReason(ignoreSleep: true) == null).ToList();
            var option = options[pick - 1];
            string say = SpeechLog.Clean(reply.Str("say"));
            string summary = Assign(plan, option, named["fighters"], named["inside"]);
            GroupChat.Instance?.Add(plan.caller.pawn.LabelShort, $"(plan: {summary})" + (say != null ? $" {say}" : ""));
            if (!plan.fighters.ContainsKey(plan.caller.pawn) && !plan.inside.Contains(plan.caller.pawn))
                plan.caller.AddDecision($"Made the plan for the danger: {summary}" + (say != null ? $" Said: \"{say}\"" : ""), importance: 0); // the group chat recorded it
            ModLog.Message($"{plan.caller.pawn.LabelShort}'s danger plan: {summary} | Reason: {plan.caller.lastReason}");
        }

        /// <summary>The plans possible on this map now, for the dev tool that forces one.</summary>
        internal static List<Option> OptionsNow(Map map)
        {
            var plan = new Plan { map = map };
            FindEdge(plan);
            return Options(plan, Candidates(map));
        }

        /// <summary>
        /// Dev: makes the plan on this map without the Plan call (replacing one being made), so every plan can be tried in
        /// a real raid. The first mind is the caller; the armed fight and the rest go inside (everyone fights if nobody's armed).
        /// </summary>
        internal string Force(Map map, string kind, Pawn victim)
        {
            var candidates = Candidates(map);
            var plan = new Plan { map = map, caller = candidates.FirstOrDefault() };
            FindEdge(plan);
            var option = Options(plan, candidates).FirstOrDefault(o => o.kind == kind && o.victim == victim);
            if (option == null || candidates.Count == 0)
                return null;
            if (plans.TryGetValue(map, out var old) && !old.made)
                old.caller?.Cancel();
            plans[map] = plan;
            var inside = candidates.Where(m => DangerResponse.Weapon(m.pawn) == null).ToList();
            if (inside.Count == candidates.Count)
                inside.Clear();
            string summary = Assign(plan, option, candidates.Except(inside).ToList(), inside);
            GroupChat.Instance?.Add(plan.caller.pawn.LabelShort, $"(plan: {summary})");
            ModLog.Message($"DEV forced the danger plan: {summary}");
            return summary;
        }

        /// <summary>
        /// Makes the plan and gives everyone named their part at once: fighters to their spots (or after X's attacker),
        /// the rest of the named inside. Returns the summary for the group chat.
        /// </summary>
        internal static string Assign(Plan plan, Option option, List<PawnMind> fighters, List<PawnMind> inside)
        {
            plan.made = true;
            plan.picked = option;
            if (option.kind == None)
                fighters = new List<PawnMind>();
            fighters = fighters.Where(m => CanFight(m.pawn) && m.pawn != option.victim).ToList(); // the one being saved isn't her own rescuer
            inside = inside.Where(m => !fighters.Contains(m)).ToList();
            var spots = plan.edge.IsValid ? Spots(plan) : new List<IntVec3>();
            foreach (var m in fighters)
            {
                var spot = IntVec3.Invalid;
                if (option.kind == Hold || option.kind == Out)
                {
                    spot = spots.FirstOrDefault(c => !plan.fighters.ContainsValue(c) && m.pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
                    if (!spot.IsValid)
                        continue; // nowhere she can get to: she keeps going
                }
                plan.fighters[m.pawn] = spot;
                m.KeepSettings();
                m.pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                m.pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                Keep(plan, m);
            }
            if (plan.fighters.Count == 0 && option.kind != None)
                plan.picked = option = new Option { kind = None, label = "No fighting" }; // a fight with nobody in it is no fight
            foreach (var m in inside)
            {
                MindActions.GoInside(m);
                plan.inside.Add(m.pawn);
            }
            string by = plan.caller?.pawn.LabelShort ?? "someone";
            foreach (var m in fighters.Concat(inside).Where(m => plan.InRole(m.pawn)))
                m.AddDecision($"{(m == plan.caller ? "My" : by + "'s")} plan for the danger: {option.label}. My part: {Part(plan, m.pawn)}.");
            string summary = option.label.Split(':')[0];
            if (plan.fighters.Count > 0)
                summary += "; fighting: " + string.Join(", ", plan.fighters.Keys.Select(p => p.LabelShort));
            if (plan.inside.Count > 0)
                summary += "; inside: " + string.Join(", ", plan.inside.Select(p => p.LabelShort));
            return summary;
        }

        /// <summary>
        /// Standable cells outside the base's rooms around its edge facing the enemy: those that can see the enemy first
        /// (a wall between them blocks shots both ways), then the best cover against it (vanilla's block chance from trees,
        /// rocks, walls and sandbags next to the cell), then the nearest.
        /// </summary>
        private static List<IntVec3> Spots(Plan plan)
        {
            Map map = plan.map;
            var inside = DangerResponse.InsideCells(map);
            return GenRadial.RadialCellsAround(plan.edge, SpotRadius, true)
                .Where(c => c.InBounds(map) && !inside.Contains(c) && c.Standable(map) && c.GetDoor(map) == null && !c.Fogged(map))
                .OrderByDescending(c => GenSight.LineOfSight(c, plan.enemyAt, map))
                .ThenByDescending(c => CoverUtility.CalculateOverallBlockChance(c, plan.enemyAt, map))
                .ThenBy(c => c.DistanceToSquared(plan.edge)).ToList();
        }

        // ---------- During the danger ----------

        /// <summary>Her part in words, or null if she has none (not named, or she left it).</summary>
        public static string Part(Plan plan, Pawn p)
        {
            if (plan == null || !plan.made || !plan.InRole(p))
                return null;
            if (plan.inside.Contains(p))
                return "stay inside until the danger is over";
            var others = plan.fighters.Keys.Where(f => f != p && plan.InRole(f)).Select(f => f.LabelShort).ToList();
            string with = others.Count > 0 ? " with " + string.Join(", ", others) : "";
            switch (plan.picked.kind)
            {
                case Hold: return $"hold the {plan.side} side of the base{with}";
                case Out: return plan.goneOut ? $"go after the enemy{with}" : $"meet just outside the base on the {plan.side} side{with}, then go after the enemy together";
                default: return $"save {plan.picked.victim.LabelShort}{with}";
            }
        }

        /// <summary>The plan's line in [Danger]: what was picked, by whom, who does what, and her part. Null before it's made.</summary>
        public static string Line(Pawn pawn)
        {
            var plan = Of(pawn.Map);
            if (plan == null || !plan.made || plan.picked == null)
                return null;
            bool mine = plan.caller?.pawn == pawn;
            string text = $"The plan{(mine ? "" : $" ({plan.caller?.pawn.LabelShort}'s call)")}: {plan.picked.label.Split(':')[0]}{(mine ? ". I made this plan" : "")}";
            var fighting = plan.fighters.Keys.Where(plan.InRole).Select(p => p.LabelShort).ToList();
            var inside = plan.inside.Where(plan.InRole).Select(p => p.LabelShort).ToList();
            if (fighting.Count > 0)
                text += ". Fighting: " + string.Join(", ", fighting);
            if (inside.Count > 0)
                text += ". Inside: " + string.Join(", ", inside);
            if (plan.left.Count > 0)
                text += ". Left the plan: " + string.Join(", ", plan.left.Select(p => p.LabelShort));
            text += ". " + (Part(plan, pawn) is string part ? "My part: " + part : "I have no part in it");
            return text + ".";
        }

        /// <summary>Within her weapon's range (bare hands: next to her).</summary>
        private static bool InReach(Pawn pawn, Thing enemy) => enemy.Position.DistanceTo(pawn.Position) <= (pawn.TryGetAttackVerb(enemy)?.EffectiveRange ?? 0f);

        /// <summary>She holds a spot right now (Hold, or Out before they go): a gun fighter shoots from it rather than walking off.</summary>
        public static bool Holding(Pawn pawn) => Of(pawn.Map) is Plan plan && plan.picked != null && plan.InRole(pawn) && plan.fighters.ContainsKey(pawn)
            && (plan.picked.kind == Hold || (plan.picked.kind == Out && !plan.goneOut));

        /// <summary>
        /// She picked something else in the danger menu: she's out of the plan from now on. Still her part: helping the one
        /// a Save plan is saving, and for a Hold or Out fighter, fighting an enemy that's after her or within her weapon's
        /// range (or any enemy once Out has gone). Her spot keeps her afterwards.
        /// </summary>
        public static void Leave(Pawn pawn, Pawn helping = null, Thing fighting = null)
        {
            var plan = Of(pawn.Map);
            if (helping != null && plan?.picked?.kind == Save && plan.picked.victim == helping && plan.fighters.ContainsKey(pawn))
                return;
            if (fighting != null && plan?.picked != null && plan.fighters.ContainsKey(pawn) && (plan.picked.kind == Hold || plan.picked.kind == Out)
                && ((plan.picked.kind == Out && plan.goneOut) || DangerResponse.Victim(fighting) == pawn
                    || InReach(pawn, fighting)))
                return;
            if (plan != null && plan.InRole(pawn))
            {
                plan.left.Add(pawn);
                ModLog.Message($"{pawn.LabelShort} left the danger plan.");
            }
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 0 || plans.Count == 0)
                return;
            foreach (var plan in plans.Values.ToList())
            {
                if (!Find.Maps.Contains(plan.map) || DangerResponse.Threats(plan.map).Count == 0)
                {
                    End(plan);
                    continue;
                }
                if (!plan.made || plan.fighters.Count == 0)
                    continue;
                var minds = plan.fighters.Keys.Where(plan.InRole).Select(p => MindManager.Instance?.MindOf(p))
                    .Where(m => m != null && m.pawn.Spawned && m.pawn.Map == plan.map && m.PausedReason(ignoreSleep: true) == null).ToList();
                if (plan.picked.kind == Out && !plan.goneOut && minds.All(m => m.pawn.Position == plan.fighters[m.pawn]))
                {
                    plan.goneOut = true;
                    ModLog.Message("Danger plan: the fighters are all out, going after the enemy.");
                }
                foreach (var m in minds)
                    Keep(plan, m);
            }
        }

        /// <summary>
        /// Keeps a fighter at her part: to her spot, then wait there and fight what comes (vanilla's Wait_Combat shoots
        /// what's in range); once Out has gone, or for Save, after the nearest enemy (X's attacker for Save).
        /// </summary>
        internal static void Keep(Plan plan, PawnMind mind)
        {
            Pawn p = mind.pawn;
            Job job = p.CurJob;
            if (job != null && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic))
                return; // fighting already
            var spot = plan.fighters[p];
            if (plan.picked.kind == Hold || (plan.picked.kind == Out && !plan.goneOut))
            {
                if (p.Position != spot)
                {
                    if (job?.def != JobDefOf.Goto || job.targetA.Cell != spot)
                    {
                        MindActions.Order(mind, JobMaker.MakeJob(JobDefOf.Goto, spot));
                        MindActions.Order(mind, WaitOn(spot), queue: true); // straight after, or vanilla hands her work before the next check
                    }
                }
                else if (job?.def != JobDefOf.Wait_Combat)
                    MindActions.Order(mind, WaitOn(spot));
                return;
            }
            if (job != null && job.def == JobDefOf.Goto && job.playerForced)
                return; // on her way to a shooting spot
            if (job?.def == JobDefOf.Wait_Combat && DangerResponse.Threats(plan.map).Any(t => InReach(p, t) && GenSight.LineOfSight(p.Position, t.Position, plan.map)))
                return; // at her firing position with an enemy in reach: the wait shoots
            var target = DangerResponse.Threats(plan.map)
                .Where(t => plan.picked.kind != Save || DangerResponse.Victim(t) == plan.picked.victim)
                .OrderBy(t => t.Position.DistanceToSquared(p.Position))
                .FirstOrDefault(t => p.CanReach(t, PathEndMode.Touch, Danger.Deadly));
            if (target == null)
                return;
            MindActions.Fight(mind, target);
            if (p.CurJob?.def == JobDefOf.Goto && p.CurJob.playerForced)
                MindActions.Order(mind, WaitOn(p.CurJob.targetA.Cell), queue: true); // ready at her firing position, not back to chores
        }

        private static Job WaitOn(IntVec3 spot)
        {
            var wait = JobMaker.MakeJob(JobDefOf.Wait_Combat, spot);
            wait.expiryInterval = WaitTicks;
            wait.checkOverrideOnExpire = true; // on expiry vanilla only checks for something more urgent; she keeps waiting
            return wait;
        }

        /// <summary>The danger is over: fighters stop waiting at their spots (their settings come back through each mind's EndDanger).</summary>
        private void End(Plan plan)
        {
            plans.Remove(plan.map);
            foreach (var p in plan.fighters.Keys)
                if (p.Spawned && !p.Drafted && p.CurJob?.def == JobDefOf.Wait_Combat)
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (plan.made)
                ModLog.Message("Danger plan over.");
        }
    }
}
