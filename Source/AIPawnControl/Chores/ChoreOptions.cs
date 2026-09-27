using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>One stock-up line the Base call can offer (STREAMLINE.md §5): its words, with the amount and what it's for, and how to apply it.</summary>
    public class ChoreOption
    {
        public Chore.Kind kind;
        public string label;                  // "wood, enough for another room: ~12 trees (+250 wood, near the base)"
        public float useful;                  // orders the group, most useful first
        public Func<string> check;            // the validator: null = fine, else why it fails
        public Func<PawnMind, string> apply;  // main thread; re-validates, then changes the map. Returns the result line
    }

    /// <summary>The live map facts every finder shares, read once per call.</summary>
    public class ChoreScan
    {
        public readonly Map map;
        public readonly Pawn pawn;
        public readonly IntVec3 center;
        public readonly int colonists;
        public readonly float foodDays; // -1 = unknown (no storage yet)
        private int[] walk;

        public ChoreScan(Pawn pawn)
        {
            this.pawn = pawn;
            map = pawn.Map;
            center = SiteFinder.BaseCenter(map);
            colonists = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            foodDays = map.haulDestinationManager.AllGroupsListForReading.Count == 0 ? -1f : map.resourceCounter.TotalHumanEdibleNutrition / colonists;
        }

        /// <summary>
        /// Steps from the base centre over walkable cells (trees pass), or -1 where no colonist can walk (or it's unexplored).
        /// The whole map, not a radius: wood, stone and food come from wherever they are (the user, session 14).
        /// </summary>
        public int WalkAt(IntVec3 c)
        {
            if (walk == null)
                walk = SiteFinder.Walk(map, center, int.MaxValue);
            return c.InBounds(map) ? walk[c.z * map.Size.x + c.x] : -1;
        }

        public static string Near(int steps) => steps <= 20 ? "right by the base" : steps <= 45 ? "near the base" : steps <= 100 ? "a walk from the base" : "far from the base";

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
        /// <summary>Every stock-up option the finders have right now, most useful first.</summary>
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
            Try(Chore.Kind.Cut, TreeCutting.Options);
            Try(Chore.Kind.Mine, Mining.Options);
            Try(Chore.Kind.Bill, WorkOrders.StockOptions);
            Try(Chore.Kind.Hunt, Hunting.Options);
            Try(Chore.Kind.Gather, WildFood.Options);
            return options.OrderByDescending(o => o.useful).ToList();
        }

        /// <summary>"call off my tree cutting": the words for her own stop (dev tool).</summary>
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

        /// <summary>Why nothing could be marked: another mind got there first, or the given reason.</summary>
        public static string NoneLeft(IEnumerable<string> reasons, string otherwise) =>
            reasons.Contains("already marked") ? "Someone already marked the rest." : otherwise;

        /// <summary>The share of its full yield a tree must give before anyone marks it for wood.</summary>
        public const float MinYieldShare = 0.75f;

        /// <summary>
        /// Trees for wood: vanilla lets it be harvested now, and it gives at least 75% of its full yield (cutting early wastes
        /// wood; wild food only needs to be ripe). Vanilla's own yield share (Plant.YieldNow, without the random rounding): 50% at its
        /// harvest point rising to 100% fully grown, times 50-100% for its health.
        /// </summary>
        public static bool WorthHarvesting(Plant p)
        {
            if (!p.HarvestableNow || p.def.plant.harvestYield <= 0f)
                return false;
            float growth = 0.5f + 0.5f * UnityEngine.Mathf.InverseLerp(p.def.plant.harvestMinGrowth, 1f, p.Growth);
            float health = UnityEngine.Mathf.Lerp(0.5f, 1f, (float)p.HitPoints / p.MaxHitPoints);
            return growth * health >= MinYieldShare;
        }

        /// <summary>Stock-ups stop once storage holds this much of a material (wood, steel, stone blocks): the user's cap.</summary>
        public const int StockCap = 1000;

        /// <summary>Food shortage as a usefulness bonus: 2 when under 4 days, 1 under 8 or unknown.</summary>
        public static float FoodNeed(ChoreScan scan) => scan.foodDays < 0f ? 1f : scan.foodDays < 4f ? 2f : scan.foodDays < 8f ? 1f : 0f;

        /// <summary>
        /// The two purposes a stock-up amount can have (STREAMLINE.md §5, defaults to tune): enough for another room, and a big
        /// stockpile for the colony's size. Each is a target for what's in storage.
        /// </summary>
        public static List<(string purpose, int target)> WoodTargets(int colonists) => new List<(string, int)>
        {
            ("enough for another room", 250),
            ("a big stockpile", 200 + 150 * colonists),
        };
    }
}
