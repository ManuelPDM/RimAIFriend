using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Fields (STREAMLINE.md §6): code lays one out when the food outlook falls short and she picks it in the Base call; the
    /// crop, site and size are code's. Vanilla sows, tends and harvests.
    /// </summary>
    public static class Fields
    {
        private const float MinFertility = 0.7f;

        /// <summary>Crops that can be sown in a field here now: researched, in season outdoors, and someone has the skill.</summary>
        public static List<ThingDef> Crops(Map map)
        {
            int best = map.mapPawns.FreeColonistsSpawned.Select(p => p.skills?.GetSkill(SkillDefOf.Plants)?.Level ?? 0).DefaultIfEmpty(0).Max();
            return DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.category == ThingCategory.Plant && d.plant != null && !d.plant.IsTree && !d.plant.cavePlant && d.plant.harvestedThingDef != null
                            && d.plant.sowTags.Contains("Ground") && Command_SetPlantToGrow.IsPlantAvailable(d, map)
                            && PlantUtility.GrowthSeasonNow(map, d) && d.plant.sowMinSkill <= best)
                .OrderBy(d => d.plant.growDays)
                .ToList();
        }

        /// <summary>
        /// A medicine crop (healroot) for a field, once: only while the colony has no medicine field, and someone who does
        /// growing work has the skill to sow it. Null otherwise.
        /// </summary>
        public static ThingDef MedicineCrop(Map map)
        {
            if (map.zoneManager.AllZones.OfType<Zone_Growing>().Any(z => z.GetPlantDefToGrow() is ThingDef p && Purpose(p) == "medicine"))
                return null;
            var growers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)).ToList();
            return Crops(map).FirstOrDefault(d => Purpose(d) == "medicine"
                && growers.Any(p => (p.skills?.GetSkill(SkillDefOf.Plants)?.Level ?? 0) >= d.plant.sowMinSkill));
        }

        /// <summary>What the crop is for, from what it gives: food, cloth, medicine, drug, animal feed.</summary>
        public static string Purpose(ThingDef plant)
        {
            ThingDef product = plant?.plant?.harvestedThingDef;
            if (product == null) return "nothing";
            if (product.IsMedicine) return "medicine";
            if (product.IsDrug) return "drug";
            if (product.IsStuff && product.stuffProps.categories.Contains(StuffCategoryDefOf.Fabric)) return "cloth";
            if (product.IsNutritionGivingIngestible && product.ingestible.HumanEdible && product.ingestible.preferability > FoodPreferability.DesperateOnly) return "food";
            if (product == ThingDefOf.Hay) return "animal feed";
            var made = DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(r => r.ProducedThingDef != null && r.ingredients.Any(i => i.filter.Allows(product)));
            return made != null ? "used to make " + made.ProducedThingDef.label : product.label;
        }

        public static bool CellOk(IntVec3 c, Map map, List<(IntVec3 pos, float radius)> foci)
        {
            if (!Ground.Open(c, map) || c.Roofed(map) || c.GetTerrain(map).fertility < MinFertility || SiteFinder.InFocusRadius(c, foci) || Ground.BesideDoor(c, map))
                return false;
            foreach (var t in c.GetThingList(map))
            {
                if (t.def.category == ThingCategory.Plant && t.def.plant.IsTree)
                    return false; // a tree blocks sowing until someone cuts it
                if (t.def.category == ThingCategory.Item && t.def.IsWithinCategory(ThingCategoryDefOf.Chunks))
                    return false;
            }
            return true;
        }

        public static int CellScore(IntVec3 c, Map map) => (int)UnityEngine.Mathf.Clamp(c.GetTerrain(map).fertility * 50f, 0f, 100f);

        /// <summary>The best free soil for a square field, 4 cells up to this side: where it is in words and what ground it's on. False if none.</summary>
        public static bool FindSite(Pawn pawn, int side, out CellRect rect, out string where, out string ground)
        {
            Map map = pawn.Map;
            var foci = SiteFinder.NoBuildFoci(map);
            var zones = new ZoneSites(new ChoreScan(pawn), c => CellOk(c, map, foci), c => CellScore(c, map));
            var site = zones.Find(new[] { 4, side }, 1).FirstOrDefault();
            rect = site?.Rect(site.maxSize) ?? CellRect.Empty;
            where = site != null ? zones.Where(rect, site.steps) : null;
            ground = site != null ? zones.TerrainLabel(rect) : null;
            return site != null;
        }

        /// <summary>Re-validates, then lays out the zone with the crop, and records it as hers.</summary>
        public static string Place(Pawn pawn, CellRect rect, ThingDef crop, string where)
        {
            Map map = pawn.Map;
            var foci = SiteFinder.NoBuildFoci(map);
            string why = ZoneSites.Check(rect, map, c => CellOk(c, map, foci));
            if (why != null)
                return $"Couldn't lay out the field: {why}.";
            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in rect)
                zone.AddCell(c);
            zone.SetPlantDefToGrow(crop);
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Field, crop.label);
            chore.zone = zone;
            string size = $"{rect.Width}×{rect.Height}";
            chore.Remember($"I laid out a field of {crop.label} ({size}), {where}.", 3);
            ModLog.Message($"{pawn.LabelShort} laid out a field of {crop.label} ({size}) at {rect}, {where}.");
            return $"Laid out a field of {crop.label} ({size}), {where}.";
        }
    }
}
