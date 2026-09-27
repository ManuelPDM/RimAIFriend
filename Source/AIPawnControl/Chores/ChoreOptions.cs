using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>What an option asks of the Colony call besides its number (PHASE5.md §5).</summary>
    public enum ChoreNeeds { Nothing, Amount, Field, Stockpile, Crop }

    /// <summary>Her answer to the Colony call, resolved against the option she picked.</summary>
    public class ColonyChoice
    {
        public int size = 1;      // 0 small, 1 medium, 2 large
        public int count;         // for an Amount option: its count at that size
        public ThingDef crop;     // a new field or a crop switch
        public ZoneSites.Site site;
        public ZoneSites finder;
        public string holds;      // a new stockpile
    }

    /// <summary>One chore the Colony call can offer (PHASE5.md §4): words for her, how useful it is now, and how to apply it.</summary>
    public class ChoreOption
    {
        public Chore.Kind kind;
        public string label;
        public float useful;                          // orders the list, most useful first; nothing is hidden
        public ChoreNeeds needs;
        public int[] counts;                          // Amount: how many at small, medium, large
        public Func<int, string> describe;            // Amount: words for one count, e.g. "25 trees, about 500 wood"
        public Func<PawnMind, ColonyChoice, string> apply; // main thread; re-validates, then changes the map. Returns the result line
        public Func<string> check;                    // the validator: null = fine, else why it fails

        /// <summary>"small: 10 trees, about 200 wood · medium: … · large: …", or one description when the sizes are equal.</summary>
        public string AmountText()
        {
            if (needs != ChoreNeeds.Amount)
                return null;
            if (counts.Distinct().Count() == 1)
                return describe(counts[0]);
            return string.Join(" · ", counts.Select((n, i) => $"{ChoreOptions.Sizes[i]}: {describe(n)}"));
        }
    }

    /// <summary>The live map facts every finder shares, read once per call.</summary>
    public class ChoreScan
    {
        public const int MaxWalk = 60;

        public readonly Map map;
        public readonly Pawn pawn;
        public readonly IntVec3 center;
        public readonly float foodDays; // -1 = unknown (no storage yet)
        private int[] walk;

        public ChoreScan(Pawn pawn)
        {
            this.pawn = pawn;
            map = pawn.Map;
            center = SiteFinder.BaseCenter(map);
            int colonists = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            foodDays = map.haulDestinationManager.AllGroupsListForReading.Count == 0 ? -1f : map.resourceCounter.TotalHumanEdibleNutrition / colonists;
        }

        /// <summary>Steps from the base centre over walkable cells (trees pass), or -1 past MaxWalk.</summary>
        public int WalkAt(IntVec3 c)
        {
            if (walk == null)
                walk = SiteFinder.Walk(map, center, MaxWalk);
            return c.InBounds(map) ? walk[c.z * map.Size.x + c.x] : -1;
        }

        public static string Near(int steps) => steps <= 20 ? "right by the base" : steps <= 45 ? "near the base" : "a walk from the base";

        /// <summary>Stored count, or a large number when there's no storage to count (don't cry shortage on a fresh map).</summary>
        public int Stock(ThingDef def) => map.haulDestinationManager.AllGroupsListForReading.Count == 0 ? 9999 : map.resourceCounter.GetCount(def);

        public bool SomeoneCanDo(WorkTypeDef work) =>
            map.mapPawns.FreeColonistsSpawned.Any(p => p.workSettings != null && !p.WorkTypeIsDisabled(work));

        public float PassionFor(SkillDef skill)
        {
            var record = pawn.skills?.GetSkill(skill);
            return record == null || record.TotallyDisabled ? 0f : record.passion == RimWorld.Passion.Major ? 1f : record.passion == RimWorld.Passion.Minor ? 0.5f : 0f;
        }
    }

    public static class ChoreOptions
    {
        public static readonly string[] Sizes = { "small", "medium", "large" };

        public const string MenuLabel = "work on the colony (fields, stockpiles, hunting, wood, mining, work orders)";

        /// <summary>Every option each finder has right now, most useful first. Cheap checks only; zone site scans run in the Colony call.</summary>
        /// <param name="ignoreLimits">Dev tools: skip the setting, cooldowns and caps.</param>
        public static List<ChoreOption> All(Pawn pawn, bool ignoreLimits = false)
        {
            var scan = new ChoreScan(pawn);
            var options = new List<ChoreOption>();
            var manager = ChoreManager.Instance;
            void Try(Chore.Kind kind, Func<ChoreScan, IEnumerable<ChoreOption>> finder)
            {
                if (!ignoreLimits && manager?.CantReason(pawn, kind) != null)
                    return;
                try
                {
                    options.AddRange(finder(scan));
                }
                catch (Exception e)
                {
                    ModLog.Error($"Chore finder {kind} threw: {e}");
                }
            }
            Try(Chore.Kind.Hunt, Hunting.Options);
            Try(Chore.Kind.Cut, TreeCutting.Options);
            Try(Chore.Kind.Gather, WildFood.Options);
            Try(Chore.Kind.Mine, Mining.Options);
            Try(Chore.Kind.Bill, WorkOrders.Options);
            Try(Chore.Kind.Field, Fields.Options);
            Try(Chore.Kind.Stockpile, Stockpiles.Options);
            return options.OrderByDescending(o => o.useful).ToList();
        }

        /// <summary>"call off …" for each chore she has running, newest first.</summary>
        public static List<ChoreOption> Stops(Pawn pawn) =>
            ChoreManager.Instance?.ActiveOf(pawn).OrderByDescending(c => c.placedTick)
                .Select(c => new ChoreOption
                {
                    kind = c.kind,
                    label = StopLabel(c),
                    check = () => c.Active ? null : "already over",
                    apply = (mind, choice) => c.Active ? c.Stop() : "That's already over.",
                }).ToList() ?? new List<ChoreOption>();

        /// <summary>Whether the Act menu shows "work on the colony" (PHASE5.md §3): chores are on and there's anything to pick.</summary>
        public static bool AnythingToDo(Pawn pawn) =>
            AIPawnControlMod.Settings.allowChores && ChoreManager.Instance != null && (Stops(pawn).Count > 0 || All(pawn).Count > 0);

        public static string StopLabel(Chore chore)
        {
            switch (chore.kind)
            {
                case Chore.Kind.Hunt: return "call off my hunt: " + chore.label;
                case Chore.Kind.Cut: return "call off my tree cutting";
                case Chore.Kind.Gather: return "call off my wild food gathering";
                case Chore.Kind.Mine: return "call off my mining: " + chore.label;
                case Chore.Kind.Bill: return "cancel my order: " + chore.label;
                case Chore.Kind.Field: return "remove my field: " + chore.label;
                default: return "remove my stockpile: " + chore.label;
            }
        }

        /// <summary>Counts for small, medium and large, never more than what's there.</summary>
        public static int[] Counts(int available, int small, int medium, int large) =>
            new[] { Math.Min(available, small), Math.Min(available, medium), Math.Min(available, large) };

        /// <summary>Why nothing could be marked: another mind got there first, or the given reason.</summary>
        public static string NoneLeft(IEnumerable<string> reasons, string otherwise) =>
            reasons.Contains("already marked") ? "Someone already marked the rest." : otherwise;

        /// <summary>Food shortage as a usefulness bonus: 2 when under 4 days, 1 under 8 or unknown.</summary>
        public static float FoodNeed(ChoreScan scan) => scan.foodDays < 0f ? 1f : scan.foodDays < 4f ? 2f : scan.foodDays < 8f ? 1f : 0f;
    }
}
