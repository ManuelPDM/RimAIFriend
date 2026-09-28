using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Room items named by what they do (FURNISHING.md §4), not by defName, so research and mods reach the minds: every
    /// buildable def that meets the need, the best by the stat that matters for it, the cheaper on a tie. Anything that
    /// isn't the room's material (steel, components, gold) must be in storage.
    /// </summary>
    public static class Needs
    {
        public const string Bed = "bed", Seat = "seat", Shelf = "shelf", Accessory = "accessory", Cooler = "cooler", Crib = "crib", AnimalBed = "animalBed";

        /// <summary>The best def for the need, or null. Accessory needs the anchor it links to (a bed).</summary>
        public static ThingDef Best(string need, Map map, ThingDef anchor = null, int count = 1) =>
            Candidates(need, map, anchor, count).FirstOrDefault();

        /// <param name="count">How many the room places (a dining room's seats): its own material must cover them all.</param>
        public static IEnumerable<ThingDef> Candidates(string need, Map map, ThingDef anchor = null, int count = 1) =>
            DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer && Meets(need, d, anchor)
                            && RoomKindDef.Buildable(d) && OtherCostsInStorage(d, map) && StuffCanBeHad(d, map, count) && CanRun(d, map))
                .OrderByDescending(d => Score(need, d))
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
                    return typeof(Building_Storage).IsAssignableFrom(d.thingClass) && d.designationCategory?.defName == "Furniture";
                case Accessory:
                    return anchor?.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities?.Contains(d) == true;
                case Cooler: // one that goes in a wall and cools below freezing (a passive cooler stops at 17°C, BASE_GROWTH.md §4)
                    return b.canPlaceOverWall && d.GetCompProperties<CompProperties_TempControl>() is CompProperties_TempControl temp
                           && temp.energyPerSecond < 0f && temp.minTargetTemperature < 0f;
                default:
                    return false;
            }
        }

        /// <summary>
        /// This cooler or heater works on the room: it stands in it, or it's in the wall with its cold side facing in (a
        /// cooler's cold side is behind it, PlaceWorker_Cooler; its hot side vents into the other room).
        /// </summary>
        public static bool Controls(Thing t, Room room) =>
            t.def.building?.canPlaceOverWall == true ? room.ContainsCell(t.Position + IntVec3.South.RotatedBy(t.Rotation)) : room.ContainsCell(t.Position);

        /// <summary>
        /// It can run here (BASE_GROWTH.md §6.3): a thing that draws power only when the base's power nets have that much to
        /// spare. Power itself is the player's: code never places generators or conduits.
        /// </summary>
        public static bool CanRun(ThingDef d, Map map)
        {
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
                    return d.building.maxItemsInCell * d.size.Area;
                case Accessory:
                    return FacilityBonus(d);
                case Cooler:
                    return -d.GetCompProperties<CompProperties_TempControl>().energyPerSecond;
                default:
                    return 0f;
            }
        }

        /// <summary>What a facility adds to what it links to: the sum of its stat offsets (an end table: comfort +0.05).</summary>
        public static float FacilityBonus(ThingDef d) =>
            d.GetCompProperties<CompProperties_Facility>()?.statOffsets?.Sum(s => s.value) ?? 0f;

        /// <summary>Made from a wall material (wood or stone blocks, which the room gets), or its own material is already in storage for every copy (a couch's cloth).</summary>
        public static bool StuffCanBeHad(ThingDef d, Map map, int count = 1) =>
            !d.MadeFromStuff || GenStuff.AllowedStuffsFor(d).Any(s => Supplies.IsWallMaterial(s) || map.resourceCounter.GetCount(s) >= d.costStuffCount * count);

        /// <summary>Costs other than the room's material are already in storage.</summary>
        public static bool OtherCostsInStorage(ThingDef d, Map map) =>
            d.costList == null || d.costList.All(c => map.resourceCounter.GetCount(c.thingDef) >= c.count);
    }
}
