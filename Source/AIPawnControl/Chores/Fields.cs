using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Fields (PHASE5.md §4.6): "start a new field" (the Colony call scans for sites, fertility a bonus, and asks for crop,
    /// size and site); "switch the crop in my field" changes a field she laid out. Vanilla sows, tends and harvests.
    /// </summary>
    public static class Fields
    {
        public static readonly int[] Sizes = { 4, 6, 8 };
        public static readonly string[] SizeNames = { "small", "medium", "large" };
        private const float MinFertility = 0.7f;

        public static string SizeName(int size) => SizeNames[System.Array.IndexOf(Sizes, size)];

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            if (!scan.SomeoneCanDo(WorkTypeDefOf.Growing))
                yield break;
            var crops = Crops(scan.map);
            if (crops.Count == 0)
                yield break;
            bool none = !scan.map.zoneManager.AllZones.OfType<Zone_Growing>().Any();
            yield return new ChoreOption
            {
                kind = Chore.Kind.Field,
                label = "start a new field",
                useful = 0.5f + ChoreOptions.FoodNeed(scan) + (none ? 1.5f : 0f) + scan.PassionFor(SkillDefOf.Plants) * 0.5f,
                needs = ChoreNeeds.Field,
                check = () => null, // the Colony call scans for sites; its reply is validated when placed
                apply = (mind, choice) =>
                {
                    int size = System.Math.Min(Sizes[choice.size], choice.site.maxSize);
                    var rect = choice.site.Rect(size);
                    string result = Place(mind.pawn, rect, choice.crop, choice.finder.Where(rect, choice.site.steps));
                    return size < Sizes[choice.size] ? result + $" Only {SizeName(size)} fit there." : result;
                },
            };
            // Switch the crop of her own fields (the crop comes from the Colony call's crop list).
            var manager = ChoreManager.Instance;
            if (manager == null)
                yield break;
            foreach (var chore in manager.ActiveOf(scan.pawn).Where(c => c.kind == Chore.Kind.Field && c.zone is Zone_Growing).ToList())
            {
                var zone = (Zone_Growing)chore.zone;
                ThingDef now = zone.GetPlantDefToGrow();
                yield return new ChoreOption
                {
                    kind = Chore.Kind.Field,
                    label = $"switch the crop in my field of {now?.label} ({zone.cells.Count} cells)",
                    useful = 0.3f,
                    needs = ChoreNeeds.Crop,
                    check = () => zone.cells.Count > 0 ? null : "the field is gone",
                    apply = (mind, choice) => choice.crop == now ? $"My field already grows {now.label}." : Switch(mind.pawn, chore, choice.crop),
                };
            }
        }

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

        /// <summary>"- rice plant: gives rice (food), ready in about 5.6 days".</summary>
        public static string CropLine(ThingDef plant) =>
            $"- {plant.label}: gives {plant.plant.harvestedThingDef.label} ({Purpose(plant)}), ready in about {plant.plant.growDays:0.#} days on plain soil";

        public static bool CellOk(IntVec3 c, Map map, List<(IntVec3 pos, float radius)> foci)
        {
            if (!c.InBounds(map) || c.InNoBuildEdgeArea(map) || c.Fogged(map) || c.Roofed(map))
                return false;
            if (c.GetTerrain(map).fertility < MinFertility)
                return false;
            if (map.zoneManager.ZoneAt(c) != null || map.planManager.PlanAt(c) != null || SiteFinder.InFocusRadius(c, foci))
                return false;
            foreach (var t in c.GetThingList(map))
            {
                if (t is Blueprint || t is Frame || t.def.category == ThingCategory.Building)
                    return false;
                if (t.def.category == ThingCategory.Plant && t.def.plant.IsTree)
                    return false; // a tree blocks sowing until someone cuts it
                if (t.def.category == ThingCategory.Item && t.def.IsWithinCategory(ThingCategoryDefOf.Chunks))
                    return false;
            }
            for (int r = 0; r < 4; r++)
            {
                var n = c + new Rot4(r).FacingCell;
                if (n.InBounds(map) && n.GetEdifice(map) is Building_Door)
                    return false; // keep doorways free
            }
            return true;
        }

        public static int CellScore(IntVec3 c, Map map) => (int)UnityEngine.Mathf.Clamp(c.GetTerrain(map).fertility * 50f, 0f, 100f);

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

        private static string Switch(Pawn pawn, Chore chore, ThingDef crop)
        {
            var zone = chore.zone as Zone_Growing;
            if (zone == null || zone.cells.Count == 0)
                return "That field is gone.";
            string old = zone.GetPlantDefToGrow()?.label;
            zone.SetPlantDefToGrow(crop);
            chore.label = crop.label;
            chore.Remember($"I switched my field from {old} to {crop.label}.", 2);
            ModLog.Message($"{pawn.LabelShort} switched a field from {old} to {crop.label}.");
            return $"My field grows {crop.label} now instead of {old}.";
        }
    }
}
