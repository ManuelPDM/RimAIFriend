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
            foreach (var p in BuildManager.Instance.ActiveOn(map).Append(project).Distinct())
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
                        results.Add(Blocks(pawn, map, kv.Key, need));
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
            need -= Mining.MarkedYield(scan.map, resource);
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
        /// things). Up to 3 that can be had: wood first, then by what's in storage, then by what's near the base. Grown trees
        /// for wood, reachable chunks for their blocks once a stonecutter's table exists or can be built.
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
                .OrderByDescending(m => m.stuff == ThingDefOf.WoodLog)
                .ThenByDescending(m => m.stock)
                .ThenByDescending(m => m.nearby)
                .Take(3)
                .ToList();
            if (list.Count == 0)
                list.Add((ThingDefOf.WoodLog, 0, 0));
            return list;
        }

        /// <summary>
        /// What these blueprints and frames still need, by material: a frame what hasn't been delivered, a blueprint its whole
        /// cost. Install blueprints (a minified thing being placed) cost nothing, and vanilla logs an error if asked.
        /// </summary>
        public static Dictionary<ThingDef, int> StillNeeds(IEnumerable<Thing> pending)
        {
            var need = new Dictionary<ThingDef, int>();
            foreach (var t in pending)
            {
                var costs = t is Frame frame ? frame.TotalMaterialCost() : t is Blueprint_Build blueprint ? blueprint.TotalMaterialCost() : null;
                foreach (var cost in costs ?? new List<ThingDefCountClass>())
                    need[cost.thingDef] = (need.TryGetValue(cost.thingDef, out int had) ? had : 0) + (t is Frame f ? f.ThingCountNeeded(cost.thingDef) : cost.count);
            }
            return need;
        }

        /// <summary>Walls are wood or stone blocks: steel is for stoves, weapons and components.</summary>
        public static bool IsWallMaterial(ThingDef stuff) => stuff == ThingDefOf.WoodLog || stuff.IsWithinCategory(ThingCategoryDefOf.StoneBlocks);

        /// <summary>A stonecutter's table is built (a room can't wait on blocks nothing can cut yet).</summary>
        public static bool CanCutStone(Map map) =>
            map.listerBuildings.allBuildingsColonist.Any(b => b.def.AllRecipes.Any(r => WorkOrders.GoalOf(r) == WorkOrders.Goal.Blocks));

        /// <summary>"steel 480 in storage (~600 more to mine nearby), wood 31 (~40 more from trees nearby)".</summary>
        public static string WallMaterialsLine(List<(ThingDef stuff, int stock, int nearby)> materials) =>
            string.Join(", ", materials.Select(m => $"{m.stuff.label} {m.stock} in storage" +
                (m.nearby > 0 ? $" (~{m.nearby} more {(m.stuff == ThingDefOf.WoodLog ? "from trees" : "from rock chunks, cut at a stonecutter's table")} nearby)" : " (no more nearby)")));

        /// <summary>
        /// An order for this stone's own blocks (granite blocks for a granite room), until storage has what's short. An
        /// order for them already there is raised if a mind placed it; the player's is left as it is.
        /// </summary>
        private static string Blocks(Pawn pawn, Map map, ThingDef blocks, int need)
        {
            int target = map.resourceCounter.GetCount(blocks) + need; // need is already beyond storage
            var existing = WorkOrders.Bills(map).FirstOrDefault(b => b.recipe.ProducedThingDef == blocks);
            if (existing != null)
                return WorkOrders.Raise(existing, target);
            string why = null;
            foreach (var table in WorkOrders.Tables(map))
            {
                var recipe = table.def.AllRecipes.FirstOrDefault(r => r.ProducedThingDef == blocks);
                if (recipe == null)
                    continue;
                why = WorkOrders.Check(table, recipe, ingredients: false);
                if (why == null)
                    return WorkOrders.AddBill(pawn, table, recipe, target, $"until there are {target}", ingredients: false);
                why = $"couldn't order them at the {table.def.label}: {why}";
            }
            return $"About {need} {blocks.label} short: {why ?? "there's no stonecutter's table to cut them"}.";
        }
    }
}
