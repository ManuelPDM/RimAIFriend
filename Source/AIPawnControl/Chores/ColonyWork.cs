using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// [Colony work] (PHASE5.md §4): fields, stockpiles, marked targets and work orders, read from the map on every call and
    /// the same for every mind. Names come from the game; "(by me)" marks what she set up herself. No positions.
    /// </summary>
    public static class ColonyWork
    {
        private const int MaxFields = 6;
        private const int MaxMarks = 6;
        private const int MaxOrders = 8;

        public static string Line(Pawn pawn)
        {
            Map map = pawn.Map;
            var manager = ChoreManager.Instance;
            string By(Pawn owner) => owner != null && owner == pawn ? " (by me)" : "";

            var fields = map.zoneManager.AllZones.OfType<Zone_Growing>()
                .Select(z => Field(z, map) + By(manager?.OwnerOf(z))).ToList();
            // Code lays stockpiles out and picks what they hold (STREAMLINE.md §7): just how many, and how many indoors.
            var piles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList();
            int indoors = piles.Count(z => z.cells.Count > 0 && z.cells[0].GetRoom(map) is Room room && !room.PsychologicallyOutdoors);
            string storage = "Stockpiles: " + (piles.Count == 0 ? "none" : indoors > 0 ? $"{piles.Count} ({indoors} indoors)" : piles.Count.ToString());
            var shelves = map.listerBuildings.allBuildingsColonist.Where(b => b is Building_Storage)
                .GroupBy(b => b.def.label).Select(g => $"{g.Key} ×{g.Count()}").ToList();
            if (shelves.Count > 0)
                storage += ". Storage buildings: " + string.Join(", ", shelves);

            return string.Join(". ", new[]
            {
                "Fields: " + Join(fields, MaxFields, "none"),
                storage,
                "Marked: " + Join(Marks(pawn, map), MaxMarks, "nothing"),
                "Orders: " + Join(Orders(pawn, map), MaxOrders, "none"),
            }) + ".";
        }

        private static string Join(List<string> items, int max, string none)
        {
            if (items.Count == 0)
                return none;
            return items.Count > max ? string.Join(" · ", items.Take(max)) + $" · {items.Count - max} more" : string.Join(" · ", items);
        }

        /// <summary>"rice plant (36 cells, 30/36 sown, 60% grown)", or "(20 cells, not sown yet)".</summary>
        public static string Field(Zone_Growing zone, Map map)
        {
            ThingDef plant = zone.GetPlantDefToGrow();
            if (plant == null)
                return $"field ({zone.cells.Count} cells, nothing chosen)";
            zone.ContentsStatistics(out int total, out _, out _, out float growth, out _);
            bool season = zone.cells.Count > 0 && PlantUtility.GrowthSeasonNow(zone.cells[0], map, plant);
            int ripe = 0;
            foreach (var c in zone.cells)
                if (c.GetPlant(map) is Plant p && p.def == plant && p.HarvestableNow)
                    ripe++;
            var words = new List<string> { $"{zone.cells.Count} cells" };
            if (total == 0)
                words.Add(season ? "not sown yet" : "not sown, too cold or hot to grow now");
            else
            {
                words.Add($"{total}/{zone.cells.Count} sown, {growth.ToStringPercent()} grown");
                if (ripe > 0)
                    words.Add($"{ripe} ready to harvest");
                if (!season)
                    words.Add("not growing now (temperature)");
            }
            return $"{plant.label} ({string.Join(", ", words)})";
        }

        /// <summary>"hunt deer ×3 (by me)", "cut trees ×8", "mine steel ×12".</summary>
        private static List<string> Marks(Pawn pawn, Map map)
        {
            var manager = ChoreManager.Instance;
            var groups = new Dictionary<string, (int count, int mine)>();
            var order = new List<string>();
            foreach (var d in map.designationManager.AllDesignations)
            {
                string key = null;
                Pawn owner = null;
                if (d.def == DesignationDefOf.Hunt && d.target.Thing is Pawn animal)
                {
                    key = "hunt " + animal.kindDef.label;
                    owner = manager?.OwnerOf(animal);
                }
                else if ((d.def == DesignationDefOf.HarvestPlant || d.def == DesignationDefOf.CutPlant) && d.target.Thing is Plant plant && plant.def.plant.IsTree)
                {
                    key = "cut trees";
                    owner = manager?.OwnerOf(plant);
                }
                else if (d.def == DesignationDefOf.HarvestPlant && d.target.Thing is Plant wild)
                {
                    key = "harvest " + wild.def.label;
                    owner = manager?.OwnerOf(wild);
                }
                else if (d.def == DesignationDefOf.Mine && d.target.Cell.GetFirstMineable(map) is Mineable rock)
                {
                    key = "mine " + Mining.ResourceLabel(rock.def);
                    owner = manager?.OwnerOf(d.target.Cell, map);
                }
                if (key == null)
                    continue;
                if (!groups.TryGetValue(key, out var g))
                    order.Add(key);
                groups[key] = (g.count + 1, g.mine + (owner == pawn ? 1 : 0));
            }
            return order.Select(k =>
            {
                var (count, mine) = groups[k];
                return $"{k} ×{count}" + (mine == 0 ? "" : mine == count ? " (by me)" : $" ({mine} by me)");
            }).ToList();
        }

        /// <summary>"fueled stove: cook simple meal, until 20 (12 now) (by me)".</summary>
        private static List<string> Orders(Pawn pawn, Map map)
        {
            var manager = ChoreManager.Instance;
            var result = new List<string>();
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (!(building is IBillGiver giver) || giver.BillStack == null)
                    continue;
                foreach (var bill in giver.BillStack.Bills.OfType<Bill_Production>())
                    result.Add($"{building.def.label}: {bill.recipe.label}, {Mode(bill)}" + (manager?.OwnerOf(bill) == pawn ? " (by me)" : ""));
            }
            return result;
        }

        public static string Mode(Bill_Production bill)
        {
            string mode;
            if (bill.repeatMode == BillRepeatModeDefOf.TargetCount)
                mode = $"until {bill.targetCount} ({bill.recipe.WorkerCounter.CountProducts(bill)} now)";
            else if (bill.repeatMode == BillRepeatModeDefOf.RepeatCount)
                mode = bill.repeatCount > 0 ? $"×{bill.repeatCount} left" : "done";
            else
                mode = "forever";
            return bill.suspended ? mode + ", suspended" : mode;
        }
    }
}
