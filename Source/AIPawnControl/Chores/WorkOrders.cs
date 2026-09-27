using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Bills (STREAMLINE.md §5, §7): the ones a finished room comes with (Outfitting), and stone blocks as a stock-up line.
    /// Code picks the counts. Never a recipe that already has a bill (the player's included).
    /// </summary>
    public static class WorkOrders
    {
        public enum Goal { Meal, Butcher, Medicine, Blocks, Clothes }

        /// <summary>"stone blocks, enough for another room": an order at a stonecutter's table, when there are chunks to cut.</summary>
        public static IEnumerable<ChoreOption> StockOptions(ChoreScan scan)
        {
            Map map = scan.map;
            foreach (var table in map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver giver && giver.BillStack != null))
            {
                var recipe = table.def.AllRecipes.FirstOrDefault(r => GoalOf(r) == Goal.Blocks);
                if (recipe == null || Check(table, recipe) != null)
                    continue;
                const int count = 100;
                var t = table;
                yield return new ChoreOption
                {
                    kind = Chore.Kind.Bill,
                    label = $"stone blocks, enough for another room: an order at the {table.def.label} until there are {count}",
                    useful = 0.5f,
                    check = () => Check(t, recipe),
                    apply = mind => AddBill(mind.pawn, t, recipe, count, 0, $"until there are {count}"),
                };
                yield break;
            }
        }

        /// <summary>Dev report: each table's recipes that fit a goal, and the validator's verdict.</summary>
        public static IEnumerable<string> Explain(Map map)
        {
            foreach (var table in map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver giver && giver.BillStack != null))
                foreach (var recipe in table.def.AllRecipes.Where(r => GoalOf(r) != null))
                    yield return $"{table.def.label}: {recipe.label} ({GoalOf(recipe)}) → {Check(table, recipe) ?? "ok"}";
        }

        public static Goal? GoalOf(RecipeDef recipe)
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
        /// <param name="ingredients">A room's own bills don't wait for ingredients: the bill waits instead.</param>
        public static string Check(Building table, RecipeDef recipe, bool ingredients = true)
        {
            Map map = table.Map;
            if (table.Destroyed || !table.Spawned || table.Faction != Faction.OfPlayer) return "the table is gone";
            if (!table.def.AllRecipes.Contains(recipe) || !recipe.AvailableNow || !recipe.AvailableOnNow(table)) return "can't be made here now";
            if (Bills(map).Any(b => Key(b.recipe) == Key(recipe))) return "already ordered";
            if (!map.mapPawns.FreeColonistsSpawned.Any(p => recipe.PawnSatisfiesSkillRequirements(p)
                                                          && (recipe.requiredGiverWorkType == null || !p.WorkTypeIsDisabled(recipe.requiredGiverWorkType))))
                return "nobody can make it";
            if (ingredients && !HasIngredients(recipe, map)) return "not enough ingredients";
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

        /// <summary>Re-validates, then adds the bill (count = "until N", repeat = "×N", neither = forever) and records it as her chore.</summary>
        public static string AddBill(Pawn pawn, Building table, RecipeDef recipe, int count, int repeat, string mode, bool ingredients = true)
        {
            string why = Check(table, recipe, ingredients);
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
