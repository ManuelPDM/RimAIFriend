using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// What a room makes, uses and stores (BASE_GROWTH.md §6.2), read from what its things do in vanilla: a work table's
    /// recipes (ingredients in, products out), an eating surface (meals in), a medical bed (medicine in), and storage
    /// filters. Two rooms are linked when goods flow between them; the site score then counts the walk between them.
    /// </summary>
    public class Goods
    {
        public readonly HashSet<ThingDef> makes = new HashSet<ThingDef>(), uses = new HashSet<ThingDef>(), stores = new HashSet<ThingDef>();

        public bool Empty => makes.Count == 0 && uses.Count == 0 && stores.Count == 0;

        private static readonly Dictionary<ThingDef, Goods> byDef = new Dictionary<ThingDef, Goods>();

        /// <summary>A thing's own flows: its recipes, an eating surface's meals, a medical bed's medicine.</summary>
        private static Goods Of(ThingDef def, bool medical)
        {
            if (!byDef.TryGetValue(def, out var g))
            {
                byDef[def] = g = new Goods();
                foreach (var recipe in def.AllRecipes ?? Enumerable.Empty<RecipeDef>())
                {
                    foreach (var ingredient in recipe.ingredients)
                        foreach (var d in ingredient.filter.AllowedThingDefs)
                            if (recipe.fixedIngredientFilter == null || recipe.fixedIngredientFilter.Allows(d))
                                g.uses.Add(d);
                    foreach (var product in recipe.products)
                        g.makes.Add(product.thingDef);
                    if (recipe.specialProducts?.Contains(SpecialProductType.Butchery) == true)
                        g.makes.UnionWith(ThingCategoryDefOf.MeatRaw.DescendantThingDefs);
                }
                if (def.surfaceType == SurfaceType.Eat)
                    g.uses.UnionWith((DefDatabase<ThingCategoryDef>.GetNamedSilentFail("FoodMeals")?.DescendantThingDefs ?? Enumerable.Empty<ThingDef>()));
            }
            if (!medical)
                return g;
            var withMedicine = new Goods();
            withMedicine.Add(g);
            withMedicine.uses.UnionWith(ThingCategoryDefOf.Medicine.DescendantThingDefs);
            return withMedicine;
        }

        private void Add(Goods other)
        {
            makes.UnionWith(other.makes);
            uses.UnionWith(other.uses);
            stores.UnionWith(other.stores);
        }

        /// <summary>A built room: its things, and what its stockpiles and shelves take.</summary>
        public static Goods OfRoom(Room room)
        {
            var g = new Goods();
            foreach (var t in room.ContainedAndAdjacentThings)
            {
                if (!(t is Building b) || !room.ContainsCell(b.Position) || b.Faction != Faction.OfPlayer)
                    continue;
                g.Add(Of(b.def, b is Building_Bed bed && bed.Medical));
                if (b is Building_Storage storage)
                    g.stores.UnionWith(storage.GetStoreSettings().filter.AllowedThingDefs);
            }
            var zones = new HashSet<Zone>();
            foreach (var c in room.Cells)
                if (room.Map.zoneManager.ZoneAt(c) is Zone_Stockpile pile && zones.Add(pile))
                    g.stores.UnionWith(pile.GetStoreSettings().filter.AllowedThingDefs);
            return g;
        }

        /// <summary>A room kind before it's built: its items as they'd resolve now, and what its stockpile will take.</summary>
        public static Goods OfKind(RoomKindDef kind, Map map)
        {
            var g = new Goods();
            foreach (var item in kind.items.Where(i => i.need != Needs.Heater)) // a campfire there is for heat, not cooking
                if (item.Resolve(map) is ThingDef def)
                    g.Add(Of(def, item.medical));
            if (kind.stores != null)
                g.stores.UnionWith(kind.StoreFilter().AllowedThingDefs);
            return g;
        }

        /// <summary>Something one makes, uses or stores that the other uses, makes or stores (two stores don't count).</summary>
        public bool LinkedTo(Goods other) =>
            makes.Overlaps(other.uses) || makes.Overlaps(other.stores)
            || uses.Overlaps(other.makes) || uses.Overlaps(other.stores)
            || stores.Overlaps(other.makes) || stores.Overlaps(other.uses);
    }
}
