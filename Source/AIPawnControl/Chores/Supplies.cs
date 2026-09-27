using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// A project gets its own materials (STREAMLINE.md §7): once it's placed, what storage and what's already marked can't
    /// cover for all the rooms being built (×1.2) is marked, nearest first: trees for wood, the nearest vein for steel or
    /// another ore, a stonecutter's order for stone blocks.
    /// Marked as the designer's chores, so they show in [Colony work] like any other.
    /// </summary>
    public static class Supplies
    {
        private const float Margin = 1.2f;

        /// <returns>What was marked, for her decision line, or "" when storage covers it.</returns>
        public static string MarkFor(Pawn pawn, BuildProject project)
        {
            if (ChoreManager.Instance == null || !AIPawnControlMod.Settings.allowChores)
                return "";
            Map map = pawn.Map;
            // Every room being built draws on the same storage, so the shortfall is theirs together.
            var total = new Dictionary<ThingDef, int>();
            foreach (var p in BuildManager.Instance.ActiveOn(map))
                foreach (var kv in p.Need())
                    total[kv.Key] = (total.TryGetValue(kv.Key, out int had) ? had : 0) + kv.Value;
            var scan = new ChoreScan(pawn);
            var results = new List<string>();
            foreach (var kv in total.Where(kv => project.Need().ContainsKey(kv.Key)))
            {
                int need = (int)Math.Ceiling(kv.Value * Margin) - map.resourceCounter.GetCount(kv.Key);
                if (need <= 0)
                    continue;
                try
                {
                    if (kv.Key == ThingDefOf.WoodLog)
                        results.Add(Wood(pawn, scan, need));
                    else if (kv.Key.IsWithinCategory(ThingCategoryDefOf.StoneBlocks))
                        results.Add(Blocks(pawn, map, need));
                    else
                        results.Add(Ore(pawn, scan, kv.Key, need));
                }
                catch (Exception e)
                {
                    ModLog.Error($"Marking {kv.Key.label} for {pawn.LabelShort}'s {project.Kind} threw: {e}");
                }
            }
            return string.Join(" ", results.Where(r => !string.IsNullOrEmpty(r)));
        }

        private static string Wood(Pawn pawn, ChoreScan scan, int need)
        {
            need -= TreeCutting.MarkedWood(scan.map);
            if (need <= 0)
                return null;
            var foci = SiteFinder.NoBuildFoci(scan.map);
            var trees = TreeCutting.Take(TreeCutting.Candidates(scan, foci), need);
            int wood = trees.Sum(t => t.YieldNow());
            string result = trees.Count > 0 ? TreeCutting.Apply(pawn, trees, foci) : "";
            return wood < need ? $"{result} About {need - wood} wood short: not enough grown trees near the base.".Trim() : result;
        }

        private static string Ore(Pawn pawn, ChoreScan scan, ThingDef resource, int need)
        {
            need -= (int)scan.map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Mine)
                .Select(d => d.target.Cell.GetFirstMineable(scan.map)?.def)
                .Where(rock => rock?.building?.mineableThing == resource)
                .Sum(rock => Mining.PerCell(rock));
            if (need <= 0)
                return null;
            var vein = Mining.Veins(scan).FirstOrDefault(v => v.rock.building.mineableThing == resource);
            if (vein.rock == null)
                return $"About {need} {resource.label} short: no {resource.label} to mine near the base.";
            // A prefix of the roof-checked vein is just as safe (Mining.Options).
            int cells = Math.Min(vein.cells.Count, (int)Math.Ceiling(need / Math.Max(1f, Mining.PerCell(vein.rock))));
            return Mining.Apply(pawn, vein.cells.Take(cells).ToList(), Mining.ResourceLabel(vein.rock));
        }

        /// <summary>
        /// Wall materials for the Base call (STREAMLINE.md §5): wood or stone blocks only (the user: steel is for other
        /// things). Up to 3, by what's in storage plus what can be had near the base, most first: grown trees for wood,
        /// reachable chunks for their blocks once a stonecutter's table exists or can be built.
        /// </summary>
        public static List<(ThingDef stuff, int stock, int nearby)> WallMaterials(Pawn pawn)
        {
            Map map = pawn.Map;
            map.resourceCounter.UpdateResourceCounts(); // vanilla refreshes every 204 ticks, so it's stale while paused
            var scan = new ChoreScan(pawn);
            var foci = SiteFinder.NoBuildFoci(map);
            int wood = TreeCutting.Candidates(scan, foci).Sum(t => t.YieldNow()) + TreeCutting.MarkedWood(map);
            var blocks = new Dictionary<ThingDef, int>();
            if (CanCutStone(map))
                foreach (var chunk in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                             .Where(t => t.def.IsWithinCategory(ThingCategoryDefOf.StoneChunks) && t.Spawned && scan.WalkAt(t.Position) >= 0 && !t.IsForbidden(Faction.OfPlayer)))
                    foreach (var product in chunk.def.butcherProducts ?? new List<ThingDefCountClass>())
                        blocks[product.thingDef] = (blocks.TryGetValue(product.thingDef, out int had) ? had : 0) + product.count * chunk.stackCount;
            int Nearby(ThingDef stuff) => stuff == ThingDefOf.WoodLog ? wood : blocks.TryGetValue(stuff, out int n) ? n : 0;
            var list = GenStuff.AllowedStuffsFor(ThingDefOf.Wall)
                .Where(IsWallMaterial)
                .Select(s => (stuff: s, stock: map.resourceCounter.GetCount(s), nearby: Nearby(s)))
                .Where(m => m.stock + m.nearby > 0)
                .OrderByDescending(m => m.stock + m.nearby)
                .Take(3)
                .ToList();
            if (list.Count == 0)
                list.Add((ThingDefOf.WoodLog, 0, 0));
            return list;
        }

        /// <summary>Walls are wood or stone blocks: steel is for stoves, weapons and components.</summary>
        public static bool IsWallMaterial(ThingDef stuff) => stuff == ThingDefOf.WoodLog || stuff.IsWithinCategory(ThingCategoryDefOf.StoneBlocks);

        /// <summary>A stonecutter's table exists, or can be built (Stonecutting researched).</summary>
        public static bool CanCutStone(Map map) =>
            map.listerBuildings.allBuildingsColonist.Any(b => b.def.AllRecipes.Any(r => WorkOrders.GoalOf(r) == WorkOrders.Goal.Blocks))
            || DefDatabase<ThingDef>.GetNamedSilentFail("TableStonecutter") is ThingDef table && RoomKindDef.Buildable(table);

        /// <summary>"steel 480 in storage (~600 more to mine nearby), wood 31 (~40 more from trees nearby)".</summary>
        public static string WallMaterialsLine(List<(ThingDef stuff, int stock, int nearby)> materials) =>
            string.Join(", ", materials.Select(m => $"{m.stuff.label} {m.stock} in storage" +
                (m.nearby > 0 ? $" (~{m.nearby} more {(m.stuff == ThingDefOf.WoodLog ? "from trees" : "from rock chunks, cut at a stonecutter's table")} nearby)" : " (no more nearby)")));

        private static string Blocks(Pawn pawn, Map map, int need)
        {
            foreach (var table in map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver giver && giver.BillStack != null))
            {
                var recipe = table.def.AllRecipes.FirstOrDefault(r => WorkOrders.GoalOf(r) == WorkOrders.Goal.Blocks);
                if (recipe == null || WorkOrders.Check(table, recipe, ingredients: false) != null)
                    continue;
                int target = map.resourceCounter.GetCountIn(ThingCategoryDefOf.StoneBlocks) + need; // need is already beyond storage
                return WorkOrders.AddBill(pawn, table, recipe, target, 0, $"until there are {target}", ingredients: false);
            }
            return $"About {need} stone blocks short: there's no stonecutter's table to cut them.";
        }
    }
}
