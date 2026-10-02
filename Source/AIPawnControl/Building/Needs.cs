using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Room items named by what they do (FURNISHING.md §4), not by defName, so research and mods reach the minds: every
    /// buildable def that meets the need, the best by the stat that matters for it, the cheaper on a tie. Anything that
    /// isn't a wall material (steel, components, gold) must be in storage.
    /// </summary>
    public static class Needs
    {
        public const string Bed = "bed", Seat = "seat", Shelf = "shelf", Accessory = "accessory", Cooler = "cooler", Heater = "heater", Crib = "crib", AnimalBed = "animalBed", Joy = "joy";

        /// <summary>
        /// The best def for the need, or null. Accessory needs the anchor it links to (a bed). A heater only on a map that
        /// gets cold. Joy: one of a joy kind not among <paramref name="placed"/> (a great hall's games differ).
        /// </summary>
        public static ThingDef Best(string need, Map map, ThingDef anchor = null, int count = 1, IEnumerable<ThingDef> placed = null) =>
            need == Heater && !Cold(map) ? null
            : Candidates(need, map, anchor, count).FirstOrDefault(d => need != Joy || placed == null || placed.All(p => p.building?.joyKind != d.building.joyKind));

        /// <summary>
        /// The map's coldest season (vanilla's world-map minimum) is below what a colonist's race finds comfortable (a
        /// human: 16°C), so its rooms want heat. Apparel is left out: it changes with the season.
        /// </summary>
        public static bool Cold(Map map)
        {
            float coldest = GenTemperature.MinTemperatureAtTile(map.Tile);
            return map.mapPawns.FreeColonistsSpawned.Any(p => p.def.GetStatValueAbstract(StatDefOf.ComfyTemperatureMin) > coldest);
        }

        /// <param name="count">How many the room places (a dining room's seats): its own material must cover them all.</param>
        public static IEnumerable<ThingDef> Candidates(string need, Map map, ThingDef anchor = null, int count = 1) =>
            DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer && Meets(need, d, anchor)
                            && RoomKindDef.Buildable(d, map) && OtherCostsInStorage(d, map) && StuffCanBeHad(d, map, count) && CanRun(d, map))
                .OrderByDescending(d => Score(need, d))
                .ThenByDescending(d => need == Heater && d.HasComp(typeof(CompTempControl))) // on a tie, a heater holds its setting; a campfire heats on to 28°C
                .ThenBy(d => d.GetStatValueAbstract(StatDefOf.MarketValue, GenStuff.DefaultStuffFor(d)));

        public static bool Meets(string need, ThingDef d, ThingDef anchor)
        {
            var b = d.building;
            if (b == null)
                return false;
            switch (need)
            {
                case Bed: // a colonist's bed: not medical, not a crib
                    return d.IsBed && b.bed_humanlike && !b.bed_defaultMedical && b.bed_maxBodySize >= LifeStageDefOf.HumanlikeAdult.bodySizeFactor;
                case Crib: // a baby's bed (a nursery needs 2)
                    return d.IsBed && b.bed_humanlike && !b.bed_defaultMedical && b.bed_maxBodySize < LifeStageDefOf.HumanlikeChild.bodySizeFactor;
                case AnimalBed: // a bed or sleeping spot for animals (a barn)
                    return d.IsBed && !b.bed_humanlike;
                case Seat: // a plain seat; special classes (a throne) make rooms of their own
                    return b.isSittable && !d.IsBed && d.thingClass == typeof(Building);
                case Shelf:
                    return typeof(Building_Storage).IsAssignableFrom(d.thingClass);
                case Accessory:
                    return anchor?.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities?.Contains(d) == true;
                case Cooler: // one that goes in a wall and cools below freezing (a passive cooler stops at 17°C, BASE_GROWTH.md §4)
                    return b.canPlaceOverWall && d.GetCompProperties<CompProperties_TempControl>() is CompProperties_TempControl temp
                           && temp.energyPerSecond < 0f && temp.minTargetTemperature < 0f;
                case Heater: // one that stands in the room and heats it: a heater, or a campfire (vanilla files it under Temperature; a torch is a light)
                    return !b.canPlaceOverWall && (d.GetCompProperties<CompProperties_TempControl>()?.energyPerSecond > 0f
                                                   || (d.GetCompProperties<CompProperties_HeatPusher>()?.heatPerSecond > 0f && d.designationCategory?.defName == "Temperature"));
                case Joy: // a game or screen that makes a rec room (vanilla's rec-room joy givers list it), used indoors (not a telescope)
                    return b.joyKind != null && d.canBeUsedUnderRoof && DefDatabase<JoyGiverDef>.AllDefsListForReading.Any(g => g.countsForRecRoom && g.thingDefs?.Contains(d) == true);
                default:
                    return false;
            }
        }

        /// <summary>A game vanilla only plays from a seat beside it (chess, poker: a sit-adjacent joy giver with requireChair).</summary>
        public static bool PlayedSitting(ThingDef d) =>
            DefDatabase<JoyGiverDef>.AllDefsListForReading.Any(g => g.requireChair && g.thingDefs?.Contains(d) == true
                                                                    && typeof(JoyGiver_InteractBuildingSitAdjacent).IsAssignableFrom(g.giverClass));

        /// <summary>
        /// Cooks for the colony. The campfire is a heater here (it gets no bill): it doesn't make a kitchen, nor end a
        /// walk-through room, the one building that is both.
        /// </summary>
        public static bool MealSource(ThingDef d) => d.building?.isMealSource == true && d != ThingDefOf.Campfire;

        /// <summary>
        /// This cooler or heater works on the room: it stands in it, or it's in the wall with its cold side facing in (a
        /// cooler's cold side is behind it, PlaceWorker_Cooler; its hot side vents into the other room).
        /// </summary>
        public static bool Controls(Thing t, Room room) =>
            t.def.building?.canPlaceOverWall == true ? room.ContainsCell(t.Position + IntVec3.South.RotatedBy(t.Rotation)) : room.ContainsCell(t.Position);

        /// <summary>
        /// It can run here (BASE_GROWTH.md §6.3): a thing that draws power only when the base's power nets have that much to
        /// spare, and one that burns fuel only when some of its fuel can be had (by default a wall material, which rooms get,
        /// or some in storage). Power itself is the player's: code never places generators or conduits. Plumbing too (Hygiene.Plumbed).
        /// </summary>
        public static bool CanRun(ThingDef d, Map map, Func<ThingDef, bool> canHave = null)
        {
            if (!Hygiene.Plumbed(d, map))
                return false;
            var fuel = d.GetCompProperties<CompProperties_Refuelable>();
            if (fuel?.fuelFilter != null && !fuel.fuelFilter.AllowedThingDefs.Any(canHave ?? (f => Supplies.IsWallMaterial(f) || map.resourceCounter.GetCount(f) > 0)))
                return false;
            var power = d.GetCompProperties<CompProperties_Power>();
            if (power == null || power.compClass != typeof(CompPowerTrader) || power.PowerConsumption <= 0f)
                return true;
            float spare = map.powerNetManager.AllNetsListForReading.Sum(n => n.CurrentEnergyGainRate()) / CompPower.WattsToWattDaysPerTick;
            return spare >= power.PowerConsumption;
        }

        private static float Score(string need, ThingDef d)
        {
            ThingDef stuff = GenStuff.DefaultStuffFor(d);
            switch (need)
            {
                case Bed:
                case Crib:
                case AnimalBed:
                    return d.GetStatValueAbstract(StatDefOf.BedRestEffectiveness, stuff) * 10f + d.GetStatValueAbstract(StatDefOf.Comfort, stuff);
                case Seat:
                    return d.GetStatValueAbstract(StatDefOf.Comfort, stuff);
                case Shelf:
                    return Kinds(d) * d.building.maxItemsInCell * d.size.Area;
                case Accessory:
                    return FacilityBonus(d);
                case Cooler:
                    return -d.GetCompProperties<CompProperties_TempControl>().energyPerSecond;
                case Heater:
                    return d.GetCompProperties<CompProperties_TempControl>()?.energyPerSecond ?? d.GetCompProperties<CompProperties_HeatPusher>().heatPerSecond;
                case Joy:
                    return d.GetStatValueAbstract(StatDefOf.JoyGainFactor, stuff);
                default:
                    return 0f;
            }
        }

        private static readonly Dictionary<ThingDef, int> kinds = new Dictionary<ThingDef, int>();

        /// <summary>How many storable item defs a shelf's own filter lets it hold (a weapon rack: only weapons).</summary>
        private static int Kinds(ThingDef shelf)
        {
            if (!kinds.TryGetValue(shelf, out int n))
            {
                ThingFilter filter = shelf.building.fixedStorageSettings?.filter;
                n = kinds[shelf] = DefDatabase<ThingDef>.AllDefsListForReading.Count(t => t.EverStorable(false) && (filter == null || filter.Allows(t)));
            }
            return n;
        }

        /// <summary>The candidates tied with the best (the same score and value: a crate, tall crate and pallet), for a room to mix.</summary>
        public static List<ThingDef> Tied(string need, Map map, int count = 1)
        {
            var all = Candidates(need, map, null, count).ToList();
            if (all.Count == 0)
                return all;
            ThingDef best = all[0];
            float MarketValue(ThingDef d) => d.GetStatValueAbstract(StatDefOf.MarketValue, GenStuff.DefaultStuffFor(d));
            return all.Where(d => Score(need, d) == Score(need, best) && MarketValue(d) == MarketValue(best)).ToList();
        }

        /// <summary>What a facility adds to what it links to: the sum of its stat offsets (an end table: comfort +0.05).</summary>
        public static float FacilityBonus(ThingDef d) =>
            d.GetCompProperties<CompProperties_Facility>()?.statOffsets?.Sum(s => s.value) ?? 0f;

        /// <summary>Made from a wall material (wood or stone blocks, which the room gets), or its own material is already in storage for every copy (a couch's cloth).</summary>
        public static bool StuffCanBeHad(ThingDef d, Map map, int count = 1) =>
            !d.MadeFromStuff || GenStuff.AllowedStuffsFor(d).Any(s => Supplies.IsWallMaterial(s) || map.resourceCounter.GetCount(s) >= d.costStuffCount * count);

        /// <summary>Costs other than a wall material (a campfire's wood gets marked with the room's) are already in storage, for <paramref name="count"/> copies.</summary>
        public static bool OtherCostsInStorage(ThingDef d, Map map, int count = 1) =>
            d.costList == null || d.costList.All(c => Supplies.IsWallMaterial(c.thingDef) || map.resourceCounter.GetCount(c.thingDef) >= c.count * count);
    }
}
