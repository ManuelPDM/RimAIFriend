using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The food outlook (STREAMLINE.md §6): what the colony eats a day, what its fields grow, and whether stores plus the
    /// harvest before winter last through it. A new field is offered by capacity, never because the stores are low today.
    /// Vanilla's numbers throughout: hunger rate, crop yield and grow days, fertility, night rest, the growing period.
    /// </summary>
    public class FoodOutlook
    {
        /// <summary>Plants rest before 25% and after 80% of the day (Plant.Resting).</summary>
        public const float GrowingPartOfDay = 0.55f;
        private const int MinFieldCells = 16, MaxFieldCells = 144;
        private const int SowingDays = 2; // a new field isn't sown the moment it's laid out

        public int colonists;
        public float needPerDay;    // raw nutrition the colony eats a day (cooked meals stretch it)
        public float growPerDay;    // what the food fields give a day while growing
        public float stores = -1f;  // nutrition in storage; -1 = no storage to count
        public ThingDef crop;       // what a new field would grow (corn when it's there)
        public bool cropInSeason;   // it can be sown now
        public bool hasWinter;
        public int daysToWinter, winterDays; // for the field crop: when growing stops, and for how long
        public bool unsownField;
        public int cellsWanted;     // a new field's size in cells; 0 = none

        public float PerColonist => needPerDay / Math.Max(1, colonists);

        public static FoodOutlook For(Map map)
        {
            var o = new FoodOutlook();
            var eaters = map.mapPawns.FreeColonistsSpawned;
            o.colonists = eaters.Count;
            float eat = eaters.Sum(p => p.needs?.food != null ? Need_Food.BaseHungerRate(p.ageTracker.CurLifeStage, p.def) * GenDate.TicksPerDay : 0f);
            bool cooks = map.listerBuildings.allBuildingsColonist.Any(b => Needs.MealSource(b.def));
            o.needPerDay = eat * (cooks ? MealStretch() : 1f);
            if (map.haulDestinationManager.AllGroupsListForReading.Count > 0)
                o.stores = map.resourceCounter.TotalHumanEdibleNutrition;

            foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>())
            {
                ThingDef plant = zone.GetPlantDefToGrow();
                if (plant == null || Fields.Purpose(plant) != "food" || zone.cells.Count == 0)
                    continue;
                float fertility = zone.cells.Average(c => map.fertilityGrid.FertilityAt(c));
                o.growPerDay += zone.cells.Count * PerCellPerDay(plant, fertility);
                zone.ContentsStatistics(out int sown, out _, out _, out _, out _);
                if (sown == 0 && PlantUtility.GrowthSeasonNow(map, plant))
                    o.unsownField = true;
            }

            var crops = Fields.Crops(map).Where(c => Fields.Purpose(c) == "food").ToList();
            o.crop = crops.FirstOrDefault(c => c.defName == "Plant_Corn") ?? crops.FirstOrDefault();
            o.cropInSeason = o.crop != null;
            o.crop = o.crop ?? DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Corn");
            if (o.crop != null)
                o.Winter(map);
            o.cellsWanted = o.CellsWanted(map);
            return o;
        }

        /// <summary>Raw food per nutrition eaten when it's cooked: a simple meal turns 0.5 raw into 0.9.</summary>
        private static float MealStretch()
        {
            var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("CookMealSimple");
            float meal = ThingDefOf.MealSimple.GetStatValueAbstract(StatDefOf.Nutrition);
            return recipe != null && recipe.ingredients.Count > 0 && meal > 0f ? recipe.ingredients[0].GetBaseCount() / meal : 1f;
        }

        /// <summary>Nutrition from one harvest of one plant.</summary>
        public static float PerHarvest(ThingDef plant) =>
            plant.plant.harvestYield * Find.Storyteller.difficulty.cropYieldFactor * plant.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition);

        /// <summary>Days from sowing to harvest: grow days at vanilla's growth rate, with the night rest.</summary>
        public static float CycleDays(ThingDef plant, float fertility) =>
            plant.plant.growDays / (GrowingPartOfDay * Math.Max(0.05f, PlantUtility.GrowthRateFactorFor_Fertility(plant, fertility)));

        public static float PerCellPerDay(ThingDef plant, float fertility) => PerHarvest(plant) / CycleDays(plant, fertility);

        /// <summary>The field crop's growing period, as vanilla shows it ("Outdoor growing period"), in days from today.</summary>
        private void Winter(Map map)
        {
            var growing = GenTemperature.TwelfthsInAverageTemperatureRange(map.Tile, crop.plant.minOptimalGrowthTemperature, crop.plant.maxOptimalGrowthTemperature);
            if (growing.Count == 12)
                return;
            hasWinter = true;
            if (growing.Count == 0)
            {
                winterDays = 60;
                return;
            }
            var now = GenLocalDate.Twelfth(map);
            int left = GenDate.DaysPerTwelfth - GenLocalDate.DayOfTwelfth(map);
            Twelfth Next(Twelfth t) => (Twelfth)(((int)t + 1) % 12);
            var t2 = now;
            int days = 0;
            if (growing.Contains(now))
            {
                days = left;
                for (t2 = Next(now); growing.Contains(t2); t2 = Next(t2))
                    days += GenDate.DaysPerTwelfth;
                daysToWinter = days;
                winterDays = 0;
            }
            else
            {
                winterDays = left;
                t2 = Next(now);
            }
            for (; !growing.Contains(t2) && winterDays < 60; t2 = Next(t2))
                winterDays += GenDate.DaysPerTwelfth;
        }

        /// <summary>Food through the winter: stores now plus what the fields grow beyond what's eaten until winter.</summary>
        public float WinterCover => Math.Max(0f, stores) + Math.Max(0f, growPerDay - needPerDay) * daysToWinter;

        /// <summary>A new field's size: enough to feed everyone now, and to fill the stores before winter if the fields can.</summary>
        private int CellsWanted(Map map)
        {
            if (!cropInSeason || unsownField || needPerDay <= 0f)
                return 0;
            float fertility = 1f;
            float cells = 0f;
            if (growPerDay < needPerDay)
                cells = (needPerDay - growPerDay) / PerCellPerDay(crop, fertility);
            if (hasWinter && daysToWinter > 0)
            {
                float missing = needPerDay * winterDays - WinterCover;
                int harvests = (int)((daysToWinter - SowingDays) / CycleDays(crop, fertility));
                if (missing > 0f && harvests > 0)
                    cells = Math.Max(cells, missing / (harvests * PerHarvest(crop)));
            }
            if (cells < MinFieldCells / 2f)
                return 0;
            return (int)Math.Min(MaxFieldCells, Math.Max(MinFieldCells, Math.Ceiling(cells)));
        }

        /// <summary>A square side for the wanted cells, 4 to 12.</summary>
        public int FieldSide => Math.Max(4, Math.Min(12, (int)Math.Ceiling(Math.Sqrt(cellsWanted))));

        /// <summary>
        /// For [Colony]: "fields feed ~2.4 of 3 · crops stop growing in 12 days, then 20 days without growing; stores and the coming harvest
        /// cover about 11".
        /// </summary>
        public string Line()
        {
            var parts = new List<string>
            {
                growPerDay <= 0f ? "no food fields yet" : $"fields feed ~{growPerDay / Math.Max(0.01f, PerColonist):0.#} of {colonists}",
            };
            if (unsownField)
                parts.Add("a field is waiting to be sown");
            if (hasWinter && crop != null)
            {
                float cover = needPerDay > 0f ? WinterCover / needPerDay : 0f;
                string stored = stores < 0f ? "the stores aren't counted yet (no stockpile)" : $"stores and the coming harvest cover about {cover:0} of those days";
                parts.Add(daysToWinter == 0
                    ? $"{crop.label} can't grow outdoors now, it starts growing again in about {winterDays} days; {stored}"
                    : $"crops stop growing in {daysToWinter} days, then {winterDays} days without growing; {stored}");
            }
            else if (crop != null)
                parts.Add("crops grow all year here");
            return string.Join(" · ", parts);
        }
    }
}
