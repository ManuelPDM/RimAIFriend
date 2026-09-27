using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Work-order options (PHASE5.md §2): bills at the colony's own tables, only for a few kinds of things and only when the
    /// ingredients are there: meals "until N", butchering, medicine, stone blocks, and clothes for whoever lacks them.
    /// Never a recipe that already has a bill (the player's included).
    /// </summary>
    public static class WorkOrders
    {
        private enum Goal { Meal, Butcher, Medicine, Blocks, Clothes }

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            Map map = scan.map;
            int colonists = System.Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            var taken = new HashSet<string>(Bills(map).Select(b => Key(b.recipe)));
            var tables = map.listerBuildings.allBuildingsColonist
                .Where(b => b is IBillGiver giver && giver.BillStack != null)
                .OrderBy(b => ((IBillGiver)b).BillStack.Count)
                .ToList();
            bool clothesOffered = false;
            foreach (var table in tables)
            {
                foreach (var recipe in table.def.AllRecipes.OrderBy(r => r.products.Count > 0 ? r.products[0].count : 1))
                {
                    Goal? goal = GoalOf(recipe);
                    if (goal == null || taken.Contains(Key(recipe)) || Check(table, recipe) != null)
                        continue;
                    int[] counts = null; // small, medium, large targets for "until there are N"
                    int repeat = 0;
                    float useful;
                    string mode;
                    switch (goal.Value)
                    {
                        case Goal.Meal:
                            counts = new[] { 2 * colonists, 2 * colonists * 3, 2 * colonists * 6 }; // meals for 1, 3 or 6 days
                            useful = 1f + ChoreOptions.FoodNeed(scan) + (scan.Stock(recipe.ProducedThingDef) < counts[1] / 2 ? 1f : 0f);
                            mode = null;
                            break;
                        case Goal.Butcher:
                            if (!FreshCorpses(map))
                                continue;
                            useful = 2.5f;
                            mode = "whenever there are corpses";
                            break;
                        case Goal.Medicine:
                            counts = new[] { colonists, 2 * colonists, 4 * colonists };
                            useful = 0.5f + (scan.Stock(recipe.ProducedThingDef) < counts[1] ? 1f : 0f);
                            mode = null;
                            break;
                        case Goal.Blocks:
                            counts = new[] { 50, 100, 200 };
                            useful = 0.5f;
                            mode = null;
                            break;
                        default:
                            if (clothesOffered)
                                continue;
                            repeat = NeedClothes(map, recipe.ProducedThingDef);
                            if (repeat == 0)
                                continue;
                            clothesOffered = true;
                            useful = 1.5f;
                            mode = repeat == 1 ? "once" : $"×{repeat}";
                            break;
                    }
                    SkillDef skill = recipe.workSkill;
                    if (skill != null)
                        useful += scan.PassionFor(skill) * 0.5f;
                    taken.Add(Key(recipe));
                    var t = table;
                    var r = recipe;
                    int times = repeat;
                    string fixedMode = mode;
                    yield return new ChoreOption
                    {
                        kind = Chore.Kind.Bill,
                        label = fixedMode != null ? $"order at the {table.def.label}: {recipe.label}, {fixedMode}" : $"order at the {table.def.label}: {recipe.label}",
                        useful = useful,
                        needs = counts != null ? ChoreNeeds.Amount : ChoreNeeds.Nothing,
                        counts = counts,
                        describe = k => $"until there are {k}",
                        check = () => Check(t, r),
                        apply = (mind, choice) => counts != null
                            ? Apply(mind.pawn, t, r, choice.count, 0, $"until there are {choice.count}")
                            : Apply(mind.pawn, t, r, 0, times, fixedMode),
                    };
                }
            }
        }

        /// <summary>Dev report: each table's recipes that fit a goal, and the validator's verdict.</summary>
        public static IEnumerable<string> Explain(Map map)
        {
            foreach (var table in map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver giver && giver.BillStack != null))
                foreach (var recipe in table.def.AllRecipes.Where(r => GoalOf(r) != null))
                    yield return $"{table.def.label}: {recipe.label} ({GoalOf(recipe)}) → {Check(table, recipe) ?? "ok"}";
        }

        private static Goal? GoalOf(RecipeDef recipe)
        {
            if (recipe.specialProducts != null && recipe.specialProducts.Contains(SpecialProductType.Butchery))
                return recipe.fixedIngredientFilter?.AllowedThingDefs.Any(d => d.IsCorpse && d.ingestible?.sourceDef?.race?.Animal == true) == true ? Goal.Butcher : (Goal?)null;
            if (recipe.defName == "Make_StoneBlocksAny")
                return Goal.Blocks;
            ThingDef product = recipe.ProducedThingDef;
            if (product == null)
                return null;
            if (product.ingestible != null && product.ingestible.IsMeal && product.IsNutritionGivingIngestible)
                return Goal.Meal;
            if (product.IsMedicine)
                return Goal.Medicine;
            if (product.IsApparel && product.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, GenStuff.DefaultStuffFor(product)) < 0.3f)
                return Goal.Clothes; // clothes, not armour
            return null;
        }

        /// <summary>One key per product, so bulk and single recipes of the same meal count as one.</summary>
        private static string Key(RecipeDef recipe) => recipe.ProducedThingDef?.defName ?? recipe.defName;

        public static IEnumerable<Bill_Production> Bills(Map map) =>
            map.listerBuildings.allBuildingsColonist
                .Where(b => b is IBillGiver giver && giver.BillStack != null)
                .SelectMany(b => ((IBillGiver)b).BillStack.Bills.OfType<Bill_Production>());

        /// <summary>The validator: null if this table can take a bill for this recipe right now.</summary>
        public static string Check(Building table, RecipeDef recipe)
        {
            Map map = table.Map;
            if (table.Destroyed || !table.Spawned || table.Faction != Faction.OfPlayer) return "the table is gone";
            if (!table.def.AllRecipes.Contains(recipe) || !recipe.AvailableNow || !recipe.AvailableOnNow(table)) return "can't be made here now";
            if (Bills(map).Any(b => Key(b.recipe) == Key(recipe))) return "already ordered";
            if (!map.mapPawns.FreeColonistsSpawned.Any(p => recipe.PawnSatisfiesSkillRequirements(p)
                                                          && (recipe.requiredGiverWorkType == null || !p.WorkTypeIsDisabled(recipe.requiredGiverWorkType))))
                return "nobody can make it";
            if (!HasIngredients(recipe, map)) return "not enough ingredients";
            return null;
        }

        /// <summary>Enough unforbidden, spawned ingredients on the map for one go (nutrition or volume counted as vanilla does).</summary>
        private static bool HasIngredients(RecipeDef recipe, Map map)
        {
            var things = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            foreach (var ing in recipe.ingredients)
            {
                float need = ing.GetBaseCount(), have = 0f;
                foreach (var t in things)
                {
                    if (!t.Spawned || t.IsForbidden(Faction.OfPlayer) || !ing.filter.Allows(t) || !recipe.fixedIngredientFilter.Allows(t)
                        || (recipe.defaultIngredientFilter != null && !recipe.defaultIngredientFilter.Allows(t)))
                        continue;
                    have += ing.IsFixedIngredient ? t.stackCount : t.stackCount * recipe.IngredientValueGetter.ValuePerUnitOf(t.def);
                    if (have >= need)
                        break;
                }
                if (have < need)
                    return false;
            }
            return true;
        }

        private static bool FreshCorpses(Map map) =>
            map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
                .Any(c => c.InnerPawn?.RaceProps?.Animal == true && !c.IsNotFresh() && !c.IsForbidden(Faction.OfPlayer) && !(c.ParentHolder is Building_Grave));

        /// <summary>How many colonists this piece would help: no shirt on the torso for plain clothes, or colder outside than they can stand for warm ones.</summary>
        private static int NeedClothes(Map map, ThingDef apparel)
        {
            float outside = map.mapTemperature.OutdoorTemp;
            bool warm = apparel.GetStatValueAbstract(StatDefOf.Insulation_Cold, GenStuff.DefaultStuffFor(apparel)) >= 15f;
            bool torso = apparel.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.Torso);
            int need = 0;
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (p.apparel == null || !apparel.apparel.PawnCanWear(p))
                    continue;
                if (warm && p.GetStatValue(StatDefOf.ComfyTemperatureMin) > outside)
                    need++;
                else if (!warm && torso && !p.apparel.WornApparel.Any(a => a.def.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.Torso)))
                    need++;
            }
            return need;
        }

        private static string Apply(Pawn pawn, Building table, RecipeDef recipe, int count, int repeat, string mode)
        {
            string why = Check(table, recipe);
            if (why != null)
                return $"Couldn't order {recipe.label}: {why}.";
            var bill = (Bill_Production)recipe.MakeNewBill();
            if (count > 0)
            {
                bill.repeatMode = BillRepeatModeDefOf.TargetCount;
                bill.targetCount = count;
            }
            else if (repeat > 0)
            {
                bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
                bill.repeatCount = repeat;
            }
            else
                bill.repeatMode = BillRepeatModeDefOf.Forever;
            ((IBillGiver)table).BillStack.AddBill(bill);
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Bill, recipe.label);
            chore.bill = bill;
            chore.Remember($"I added an order at the {table.def.label}: {recipe.label}, {mode}.", 2);
            ModLog.Message($"{pawn.LabelShort} added a bill at the {table.def.label}: {recipe.label}, {mode}.");
            return $"Ordered at the {table.def.label}: {recipe.label}, {mode}.";
        }
    }
}
