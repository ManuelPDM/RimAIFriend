using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Upgrading a room (FURNISHING.md §5). Code lists every change vanilla allows (add an item, a better version in
    /// place, a floor), scores each on one of three gains, and offers the best of each gain per cost: up to 3. Looks use
    /// vanilla's own impressiveness formula on the room's stats with the change applied, so nothing is judged by hand.
    /// </summary>
    public static class Upgrades
    {
        public enum Gain { Looks, Comfort, Temperature, Better } // Better: a faster bench, or one with no fuel to keep it going

        public const int MaxOptions = 3;

        public class Upgrade
        {
            public Gain gain;
            public float value;       // how much it gains, compared within its kind only
            public float cost;        // market value of what it costs
            public float predicted;   // looks: vanilla's impressiveness once it's built
            public string label;      // "a wooden floor (75 wood): awful → dull"
            public ThingDef def, stuff;
            public PlanEntry entry;   // an item to add, or to place over `replaces`
            public Thing replaces;
            public bool removeFirst;  // `replaces` comes down before the new one goes up (vanilla has no in-place swap for it)
            public TerrainDef floor;
            public List<IntVec3> cells;
            public List<IntVec3> conduits = new List<IntVec3>(); // its line to the powered grid (Wiring)
            public bool art;          // made at a bench and installed (Art): picking it orders it
            public bool bench;        // a work table the colony lacks (NewBench): its room is outfitted once it's built
        }

        private static readonly HashSet<string> ItemCategories = new HashSet<string> { "Furniture", "Joy", "Temperature", "Misc", "Hygiene" }; // Hygiene: Dubs Bad Hygiene's tab (a basin, a bath mat)

        /// <summary>Colony rooms that can be upgraded: proper, roofed, indoors, with a role, nothing already being added.</summary>
        public static List<Room> Rooms(Pawn pawn)
        {
            Map map = pawn.Map;
            var busy = new HashSet<Room>(BuildManager.Instance?.ActiveOnAll(map).Select(p => p.Room).Where(r => r != null) ?? Enumerable.Empty<Room>());
            return map.regionGrid.AllRooms
                .Where(r => Ground.Indoor(r) && r.ProperRoom && !r.Fogged && r.OpenRoofCount == 0 && Ground.AnyRole(r)
                            && !busy.Contains(r) && SnapshotBuilder.Furniture(r).Any(t => t.Faction == Faction.OfPlayer))
                .OrderByDescending(r => r.Owners.Contains(pawn))
                .ToList();
        }

        /// <summary>"my bedroom (awful, dark, 102°F)": dark and the temperature only when they're a problem.</summary>
        public static string RoomLine(Room room, Pawn pawn)
        {
            string line = $"{SnapshotBuilder.RoomName(room, pawn)} ({BuildManager.Impressiveness(room)}";
            if (Dark(room))
                line += ", dark";
            if (TooHot(room, pawn) || TooCold(room, pawn))
                line += ", " + room.Temperature.ToStringTemperature("F0");
            return line + ")";
        }

        /// <summary>
        /// The room most worth upgrading for her (the ladder's last rung): one vanilla gives a bad mood for (dark, too hot
        /// or cold) first, then the least impressive, of the rooms with an upgrade to offer. Null if none. Rooms are
        /// ranked first and asked for upgrades (the slow part) in that order until one has some.
        /// </summary>
        public static Room Worst(IEnumerable<Room> rooms, Pawn pawn)
        {
            List<(ThingDef stuff, int stock, int nearby)> materials = null; // read once for every room asked
            return rooms.OrderByDescending(r => (Dark(r) ? 1 : 0) + (TooHot(r, pawn) || TooCold(r, pawn) ? 1 : 0))
                .ThenBy(r =>
                {
                    r.Notify_TerrainChanged(); // read fresh, as For does (Stats.Of)
                    return r.GetStat(RoomStatDefOf.Impressiveness);
                })
                .FirstOrDefault(r => For(r, pawn, materials ??= Supplies.WallMaterials(pawn)).Count > 0);
        }

        /// <summary>
        /// How many of the room's dark cells a light at the cell would light, by vanilla's falloff (ComputeGlowGridsJob:
        /// lerp(1 - d/radius, 1/d², 0.4) of its colour) added to the glow already there, against vanilla's lit threshold.
        /// </summary>
        private static int LitCells(Room room, ThingDef def, IntVec3 at, out int darkCells)
        {
            Map map = room.Map;
            var glower = def.GetCompProperties<CompProperties_Glower>();
            float colour = Mathf.Max(glower.glowColor.r, Mathf.Max(glower.glowColor.g, glower.glowColor.b)) / 255f * 3.6f; // GlowGrid.GroundGlowAt's factor
            int lit = 0;
            darkCells = 0;
            foreach (var c in room.Cells)
            {
                if (map.glowGrid.PsychGlowAt(c) != PsychGlow.Dark)
                    continue;
                darkCells++;
                float d = c.DistanceTo(at);
                if (d > glower.glowRadius)
                    continue;
                float falloff = d < 1f ? 1f : Mathf.Lerp(1f - d / glower.glowRadius, 1f / (d * d), 0.4f);
                if (GlowGrid.PsychGlowAtGlow(Mathf.Min(0.5f, map.glowGrid.GroundGlowAt(c) + colour * falloff)) != PsychGlow.Dark)
                    lit++;
            }
            return lit;
        }

        /// <summary>Vanilla's darkness: most of the room is dark to the eye (the "in darkness" mood).</summary>
        private static bool Dark(Room room) => room.Cells.Count(c => room.Map.glowGrid.PsychGlowAt(c) == PsychGlow.Dark) * 2 > room.CellCount;

        internal static bool TooHot(Room room, Pawn pawn) => room.Temperature > pawn.GetStatValue(StatDefOf.ComfyTemperatureMax);
        internal static bool TooCold(Room room, Pawn pawn) => room.Temperature < pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);

        /// <summary>
        /// Up to 3 upgrades for the room: the most gain for its cost of each kind first, then the next best of any kind, so
        /// there's a real choice even when only looks can improve. Never the same item or floor twice.
        /// </summary>
        /// <param name="materials">Her wall materials (Supplies.WallMaterials), when the caller already has them.</param>
        public static List<Upgrade> For(Room room, Pawn pawn, List<(ThingDef stuff, int stock, int nearby)> materials = null)
        {
            var all = new List<Upgrade>();
            var ctx = new Context(room, pawn, materials ?? Supplies.WallMaterials(pawn));
            all.AddRange(ItemUpgrades(ctx));
            all.AddRange(Replacements(ctx));
            all.AddRange(BetterVersions(ctx));
            all.AddRange(ArtUpgrades(ctx));
            if (FloorUpgrade(ctx) is Upgrade floor)
                all.Add(floor);
            var ranked = all.Where(u => u.value > 0f).OrderByDescending(u => u.value / Math.Max(1f, u.cost)).ToList();
            var picked = ranked.GroupBy(u => u.gain).Select(g => g.First()).ToList();
            foreach (var u in ranked)
            {
                if (picked.Count >= MaxOptions)
                    break;
                if (!picked.Contains(u) && !picked.Any(p => ((object)p.floor ?? p.def) == ((object)u.floor ?? u.def)))
                    picked.Add(u);
            }
            return picked.Take(MaxOptions).OrderBy(u => u.gain).ToList();
        }

        /// <summary>
        /// The colony room people stay in that's furthest outside comfortable (too cold or too hot for her) and the item that best brings it back:
        /// a campfire or heater, a passive cooler. Null when every room is comfortable or nothing can be built.
        /// </summary>
        public static (Room room, Upgrade item, bool cold)? Temperature(Pawn pawn)
        {
            float min = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin), max = pawn.GetStatValue(StatDefOf.ComfyTemperatureMax);
            List<(ThingDef stuff, int stock, int nearby)> materials = null;
            foreach (var room in Rooms(pawn).Where(r => (TooCold(r, pawn) || TooHot(r, pawn)) && StayedIn(r))
                         .OrderByDescending(r => Math.Max(min - r.Temperature, r.Temperature - max)))
            {
                var ctx = new Context(room, pawn, materials ??= Supplies.WallMaterials(pawn));
                var best = ItemUpgrades(ctx).Where(u => u.gain == Gain.Temperature).OrderByDescending(u => u.value / Math.Max(1f, u.cost)).FirstOrDefault();
                if (best != null)
                    return (room, best, TooCold(room, pawn));
            }
            return null;
        }

        /// <summary>People spend time here: a bed for people, a work table, a seat or a game. Not a store or a barn.</summary>
        private static bool StayedIn(Room room) =>
            SnapshotBuilder.Furniture(room).Any(t => (t.def.IsBed && t.def.building.bed_humanlike) || t is IBillGiver || t.def.building?.isSittable == true || t.def.building?.joyKind != null);

        private class Context
        {
            public readonly Room room;
            public readonly Pawn pawn;
            public readonly Map map;
            public readonly List<(ThingDef stuff, int stock, int nearby)> materials;
            public readonly List<Thing> things;
            public readonly Stats stats;

            public Context(Room room, Pawn pawn, List<(ThingDef stuff, int stock, int nearby)> materials)
            {
                this.room = room;
                this.pawn = pawn;
                map = room.Map;
                this.materials = materials;
                things = room.ContainedAndAdjacentThings.Where(t => t.def.category == ThingCategory.Building && room.ContainsCell(t.Position)).Distinct().ToList();
                stats = Stats.Of(room);
            }

            /// <summary>The material an item is made of: the colony's best wall material it takes, else vanilla's default if it's in storage.</summary>
            public ThingDef StuffFor(ThingDef def, int count)
            {
                if (!def.MadeFromStuff)
                    return null;
                var allowed = GenStuff.AllowedStuffsFor(def).ToList();
                foreach (var m in materials)
                    if (allowed.Contains(m.stuff) && m.stock + m.nearby >= count)
                        return m.stuff;
                var fallback = GenStuff.DefaultStuffFor(def);
                return fallback != null && map.resourceCounter.GetCount(fallback) >= count ? fallback : null;
            }

            public bool CanPay(ThingDef def, out ThingDef stuff)
            {
                stuff = def.MadeFromStuff ? StuffFor(def, def.costStuffCount) : null;
                return (!def.MadeFromStuff || stuff != null) && (def.costList == null || def.costList.All(c => CanHave(c.thingDef, c.count)));
            }

            /// <summary>Enough of it in storage, or for wood and stone blocks, in storage plus what can be had (it gets marked).</summary>
            public bool CanHave(ThingDef thing, int count)
            {
                var m = materials.FirstOrDefault(x => x.stuff == thing);
                return (m.stuff != null ? m.stock + m.nearby : map.resourceCounter.GetCount(thing)) >= count;
            }

            /// <summary>What it needs to work once built is there: power from a generator for what draws power, some fuel for what burns it.</summary>
            public bool CanRun(ThingDef def) => Needs.CanRun(def, map, f => CanHave(f, 1));

            /// <summary>
            /// The upgrade's conduit line to the powered grid (none for what draws no power), paid for with the item: false
            /// when no line can be laid or the steel isn't there. Its cost and label then count the conduits.
            /// </summary>
            public bool Wire(Upgrade u)
            {
                var line = Wiring.Route(map, u.def, u.entry.cell, u.entry.rot);
                if (line == null)
                    return false;
                if (line.Count == 0)
                    return true;
                var costs = u.def.CostListAdjusted(u.stuff).Concat(Wiring.Cost(line.Count)).GroupBy(c => c.thingDef);
                if (!costs.All(g => CanHave(g.Key, g.Sum(c => c.count))))
                    return false;
                u.conduits = line;
                u.cost += Wiring.Cost(line.Count).Sum(c => c.count * c.thingDef.BaseMarketValue);
                int colon = u.label.IndexOf("): ");
                if (colon >= 0)
                    u.label = u.label.Insert(colon, $"; {ThingDefOf.PowerConduit.label} x{line.Count}");
                return true;
            }
        }

        // ---------- Candidates ----------

        private static IEnumerable<Upgrade> ItemUpgrades(Context ctx)
        {
            Room room = ctx.room;
            if (!Rectangular(room))
                yield break;
            var present = new HashSet<ThingDef>(ctx.things.Select(t => t is Blueprint || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def));
            bool hot = TooHot(room, ctx.pawn), cold = TooCold(room, ctx.pawn);
            bool dark = Dark(room);
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.category != ThingCategory.Building || !def.BuildableByPlayer || def.IsBed || def.size.x > 3 || def.size.z > 3
                    || def.designationCategory == null || !RoomKindDef.Buildable(def, ctx.map))
                    continue;
                float heat = Heat(def, room.Temperature);
                bool warms = heat != 0f;
                if (present.Contains(def) && !warms)
                    continue; // one of each item; a hot room may need a second cooler
                if (!ItemCategories.Contains(def.designationCategory.defName) && !warms)
                    continue;
                if (def.PlaceWorkers != null && def.PlaceWorkers.Any(w => w is PlaceWorker_Cooler || w is PlaceWorker_Vent))
                    continue; // they sit in a wall: the walls step
                if (Needs.PlayedSitting(def))
                    continue; // needs seats beside it, which a one-item upgrade doesn't bring (chess, poker)
                if (Hygiene.NeedsPrivacy(def))
                    continue; // needs a stall, which a one-item upgrade doesn't bring (a toilet, a shower)
                if (Hygiene.IsPlumbing(def))
                    continue; // the plumbing is the player's, like power conduits
                if (typeof(Building_Throne).IsAssignableFrom(def.thingClass))
                    continue; // a throne makes its room a throne room (RoomRoleWorker_ThroneRoom reads the room's cached things, so KeepsRole can't see it); the title asks for that room itself
                if (!ctx.CanPay(def, out ThingDef stuff) || !ctx.CanRun(def))
                    continue;
                var u = new Upgrade { def = def, stuff = stuff, cost = Cost(def, stuff) };
                Thing anchor = ctx.things.FirstOrDefault(t => LinksTo(def, t));
                // A lamp (vanilla files lights under furniture; a torch warms a little below 23°C) lights a dark room, as
                // long as the room isn't already too hot for its heat.
                bool light = dark && def.HasComp(typeof(CompGlower)) && def.designationCategory.defName == "Furniture" && !(hot && heat > 0f);
                if (warms && !light)
                {
                    if (!(hot && heat < 0f) && !(cold && heat > 0f))
                        continue;
                    u.gain = Gain.Temperature;
                    u.value = Mathf.Abs(heat);
                    u.label = $"{def.label}{CostText(def, stuff)}: the room is {room.Temperature.ToStringTemperature("F0")}, comfortable is " +
                              $"{ctx.pawn.GetStatValue(StatDefOf.ComfyTemperatureMin).ToStringTemperature("F0")}-{ctx.pawn.GetStatValue(StatDefOf.ComfyTemperatureMax).ToStringTemperature("F0")}";
                }
                else if (anchor != null)
                {
                    u.gain = Gain.Comfort;
                    u.value = Needs.FacilityBonus(def);
                    u.label = $"{def.label}{CostText(def, stuff)}: {FacilityText(def)} for the {anchor.def.label}";
                }
                else if (light)
                {
                    // A dark room is vanilla's "in darkness" mood: the light that lights most of it for its cost wins (counted once it has a slot).
                    u.gain = Gain.Comfort;
                    u.value = 1f;
                }
                else
                {
                    if (!Decoration(def, stuff))
                        continue;
                    ThingDef plant = PotPlant(def);
                    var s = ctx.stats;
                    s.wealth += def.GetStatValueAbstract(StatDefOf.MarketValue, stuff) + (plant?.BaseMarketValue ?? 0f);
                    s.beautySum += def.GetStatValueAbstract(StatDefOf.Beauty, stuff) + (plant?.GetStatValueAbstract(StatDefOf.Beauty) ?? 0f);
                    s.cleanSum += def.GetStatValueAbstract(StatDefOf.Cleanliness, stuff);
                    s.space -= SpaceTaken(def);
                    u.gain = Gain.Looks;
                    u.value = s.Impressiveness - ctx.stats.Impressiveness;
                    u.predicted = s.Impressiveness;
                    u.label = $"{def.label}{(plant != null ? $" ({plant.label})" : "")}{CostText(def, stuff)}: {LooksText(ctx.stats, s)}";
                }
                // For offers gains only, so the slow checks (the room's role, a free slot) are left for those.
                if (u.value <= 0f || !KeepsRole(room, def, stuff))
                    continue;
                u.entry = Slot(ctx, def, anchor);
                if (u.entry == null)
                    continue;
                // A pot's plant only grows where it gets vanilla's light for it (a daylily: lit).
                if (PotPlant(def) is ThingDef potPlant && ctx.map.glowGrid.GroundGlowAt(u.entry.cell) < potPlant.plant.growMinGlow)
                    continue;
                if (light && anchor == null)
                {
                    int lit = LitCells(room, def, u.entry.cell, out int darkCells);
                    if (lit == 0)
                        continue;
                    u.value = lit;
                    u.label = $"{def.label}{CostText(def, stuff)}: lights {lit} of the {darkCells} dark cells";
                }
                if (!ctx.Wire(u))
                    continue;
                u.entry.stuff = stuff;
                yield return u;
            }
        }

        /// <summary>
        /// A more comfortable version of an item in the room, placed over it: vanilla swaps it (replace tags: bed, chair,
        /// table). Comfort only: looks come from decoration (the user), so a swap for looks alone isn't offered.
        /// </summary>
        private static IEnumerable<Upgrade> Replacements(Context ctx)
        {
            foreach (var old in ctx.things.Where(t => t.Faction == Faction.OfPlayer && t.def.replaceTags != null && !(t is Blueprint) && !(t is Frame)))
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer
                             && d.replaceTags != null && d.size == old.def.size && RoomKindDef.Buildable(d, ctx.map)))
                {
                    if (!ctx.CanPay(def, out ThingDef stuff) || !GenConstruct.CanReplace(def, old.def, stuff, old.Stuff))
                        continue;
                    float comfort = def.GetStatValueAbstract(StatDefOf.Comfort, stuff) - old.GetStatValue(StatDefOf.Comfort);
                    if (def.IsBed)
                        comfort += def.GetStatValueAbstract(StatDefOf.BedRestEffectiveness, stuff) - old.GetStatValue(StatDefOf.BedRestEffectiveness);
                    if (comfort <= 0f)
                        continue;
                    string name = stuff != null && def == old.def ? $"{stuff.label} {def.label}" : GenLabel.ThingLabel(def, stuff);
                    var u = new Upgrade
                    {
                        gain = Gain.Comfort, value = comfort, def = def, stuff = stuff, cost = Cost(def, stuff), replaces = old,
                        entry = new PlanEntry(def, old.Position, old.Rotation) { stuff = stuff },
                        label = $"{name} in place of the {old.LabelNoCount}{CostText(def, stuff)}: comfort {comfort:+0.00}",
                    };
                    if (!GenConstruct.CanPlaceBlueprintAt(def, old.Position, old.Rotation, ctx.map, stuffDef: stuff).Accepted)
                        continue;
                    yield return u;
                }
        }

        /// <summary>
        /// A better version of a bench or light in the room, in its place: one for the same work (every recipe the old bench
        /// has, or research, or light) that vanilla rates faster, or that needs no fuel. Vanilla can't swap these in place,
        /// so the old one comes down first (BuildProject.removeFirst). Only where the new one fits and a conduit line reaches.
        /// </summary>
        private static IEnumerable<Upgrade> BetterVersions(Context ctx)
        {
            foreach (var old in ctx.things.Where(t => t.Faction == Faction.OfPlayer && !(t is Blueprint) && !(t is Frame) && t.def.BuildableByPlayer))
            {
                if (Required(ctx.room, old.def))
                    continue; // a throne room's braziers stay
                StatDef speed = SpeedStat(old.def);
                if (speed == null && !IsLight(old.def))
                    continue;
                bool oldFuel = old.def.HasComp(typeof(CompRefuelable));
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d != old.def && d.category == ThingCategory.Building && d.BuildableByPlayer
                             && SameWork(old.def, d) && RoomKindDef.Buildable(d, ctx.map)))
                {
                    float before = speed != null ? old.def.GetStatValueAbstract(speed, old.Stuff) : 1f;
                    float after = speed != null ? def.GetStatValueAbstract(speed, GenStuff.DefaultStuffFor(def)) : 1f;
                    bool noFuel = oldFuel && !def.HasComp(typeof(CompRefuelable));
                    if (after < before || (after == before && !noFuel))
                        continue;
                    if (!ctx.CanPay(def, out ThingDef stuff) || !ctx.CanRun(def)
                        || !GenConstruct.CanPlaceBlueprintAt(def, old.Position, old.Rotation, ctx.map, thingToIgnore: old, stuffDef: stuff).Accepted)
                        continue;
                    var gains = new List<string>();
                    if (after > before)
                        gains.Add($"{speed.label} {before.ToStringPercent()} → {after.ToStringPercent()}");
                    if (noFuel)
                        gains.Add("no fuel to keep it going");
                    var u = new Upgrade
                    {
                        gain = Gain.Better, def = def, stuff = stuff, cost = Cost(def, stuff), replaces = old, removeFirst = true,
                        value = after / before - 1f + (noFuel ? 1f : 0f),
                        entry = new PlanEntry(def, old.Position, old.Rotation) { stuff = stuff },
                        label = $"{GenLabel.ThingLabel(def, stuff)} in place of the {old.LabelNoCount}{CostText(def, stuff)}: {string.Join(", ", gains)}",
                    };
                    if (ctx.Wire(u))
                        yield return u;
                }
            }
        }

        /// <summary>
        /// A piece of art for the room (a sculpture), made at the colony's bench in a wall material storage holds: its looks
        /// at normal quality, since the artist decides the rest. One on order per room at a time.
        /// </summary>
        private static IEnumerable<Upgrade> ArtUpgrades(Context ctx)
        {
            if (Art.Ordered(ctx.map, ctx.room))
                yield break;
            foreach (var def in Art.Pieces)
            {
                if (def.size.x > 3 || def.size.z > 3 || ctx.things.Any(t => t.def == def) || !(Art.Bench(ctx.map, def) is Building bench))
                    continue;
                ThingDef stuff = def.MadeFromStuff ? ctx.materials.Select(m => m.stuff).FirstOrDefault(m => GenStuff.AllowedStuffsFor(def).Contains(m)
                                     && ctx.map.resourceCounter.GetCount(m) >= def.CostListAdjusted(m).Where(c => c.thingDef == m).Sum(c => c.count)) : null;
                if (def.MadeFromStuff && stuff == null)
                    continue;
                var s = ctx.stats;
                s.wealth += def.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
                s.beautySum += def.GetStatValueAbstract(StatDefOf.Beauty, stuff);
                s.space -= SpaceTaken(def);
                var u = new Upgrade
                {
                    gain = Gain.Looks, art = true, def = def, stuff = stuff, cost = Cost(def, stuff),
                    value = s.Impressiveness - ctx.stats.Impressiveness, predicted = s.Impressiveness,
                    label = $"{def.label}, made at the {bench.def.label}{CostText(def, stuff)}: {LooksText(ctx.stats, s)} at normal quality",
                };
                if (u.value <= 0f || !KeepsRole(ctx.room, def, stuff))
                    continue;
                u.entry = Slot(ctx, def, null);
                if (u.entry == null)
                    continue;
                u.entry.stuff = stuff;
                yield return u;
            }
        }

        /// <summary>What vanilla rates a bench's work by: research speed for a research bench, work speed for a work table; null for neither.</summary>
        private static StatDef SpeedStat(ThingDef def) =>
            typeof(Building_ResearchBench).IsAssignableFrom(def.thingClass) ? StatDefOf.ResearchSpeedFactor
            : typeof(Building_WorkTable).IsAssignableFrom(def.thingClass) && def.AllRecipes.Count > 0 ? StatDefOf.WorkTableWorkSpeedFactor : null;

        /// <summary>
        /// Something only there to look at (the user: looks upgrades are decoration): it adds beauty, or grows a plant that
        /// does (a plant pot), and has no other job: no seat, bed, table, storage, game, work, light, facility, heat or
        /// bathroom fixture.
        /// </summary>
        private static bool Decoration(ThingDef def, ThingDef stuff)
        {
            var b = def.building;
            if (b == null || b.isSittable || def.IsBed || def.surfaceType != SurfaceType.None || b.joyKind != null
                || typeof(Building_Storage).IsAssignableFrom(def.thingClass) || typeof(IBillGiver).IsAssignableFrom(def.thingClass)
                || typeof(Building_ResearchBench).IsAssignableFrom(def.thingClass) || def.HasComp(typeof(CompGlower)) || def.GetCompProperties<CompProperties_Facility>() != null
                || def.HasComp(typeof(CompTempControl)) || def.HasComp(typeof(CompHeatPusher)) || def.designationCategory?.defName == "Hygiene")
                return false;
            return def.GetStatValueAbstract(StatDefOf.Beauty, stuff) > 0f || PotPlant(def) != null;
        }

        /// <summary>The plant a grower is sown with by default (a plant pot: daylilies), whose beauty is the pot's point; null for anything else.</summary>
        private static ThingDef PotPlant(ThingDef def) =>
            typeof(Building_PlantGrower).IsAssignableFrom(def.thingClass) ? def.building?.defaultPlantToGrow : null;

        /// <summary>A light: something vanilla files under furniture that glows (a torch, a standing lamp).</summary>
        private static bool IsLight(ThingDef def) => def.HasComp(typeof(CompGlower)) && def.designationCategory?.defName == "Furniture";

        /// <summary>
        /// Something vanilla's requirements for this room name, so it stays: a throne room's braziers (its owner's title),
        /// what an ideoligion building asks of its room.
        /// </summary>
        private static bool Required(Room room, ThingDef def)
        {
            var requirements = new List<RoomRequirement>();
            foreach (var t in room.ContainedAndAdjacentThings.Distinct())
            {
                if (t is Building_Throne throne && throne.AssignedPawn?.royalty?.HighestTitleWithThroneRoomRequirements()?.def.throneRoomRequirements is List<RoomRequirement> titled)
                    requirements.AddRange(titled);
                if (t.StyleSourcePrecept is Precept_Building precept && precept.presenceDemand?.roomRequirements is List<RoomRequirement> asked)
                    requirements.AddRange(asked);
            }
            return requirements.Any(r => r switch
            {
                RoomRequirement_ThingCount c => c.thingDef == def,
                RoomRequirement_ThingAnyOfCount c => c.things.Contains(def),
                RoomRequirement_Thing c => c.thingDef == def,
                RoomRequirement_ThingAnyOf c => c.things.Contains(def),
                _ => false,
            });
        }

        /// <summary>The colony has a building for this work: the def itself, or one that does all its work (an electric tailor bench for a hand one).</summary>
        public static bool HasBenchFor(Map map, ThingDef def) =>
            map.listerBuildings.allBuildingsColonist.Any(b => b.def == def || (SpeedStat(def) != null && SameWork(def, b.def)));

        /// <summary>The new one does all the old one's work: a research bench for a research bench, every recipe of a work table, a light for a light.</summary>
        private static bool SameWork(ThingDef old, ThingDef def)
        {
            StatDef speed = SpeedStat(old);
            if (speed == StatDefOf.ResearchSpeedFactor)
                return SpeedStat(def) == speed;
            if (speed != null)
                return SpeedStat(def) == speed && old.AllRecipes.All(r => def.AllRecipes.Contains(r));
            return IsLight(def) && def.building?.canPlaceOverWall == old.building?.canPlaceOverWall;
        }

        /// <summary>The wood or stone floor that does the most for the room's looks, on every cell it would improve.</summary>
        private static Upgrade FloorUpgrade(Context ctx)
        {
            Upgrade best = null;
            // Wood or stone only, like walls (the user: steel is for other things).
            foreach (var floor in DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.BuildableByPlayer && t.designationCategory?.defName == "Floors" && RoomKindDef.Buildable(t, ctx.map)
                         && t.CostList != null && t.CostList.Count > 0 && t.CostList.All(c => Supplies.IsWallMaterial(c.thingDef))))
            {
                float beauty = floor.GetStatValueAbstract(StatDefOf.Beauty), value = floor.GetStatValueAbstract(StatDefOf.MarketValue);
                var cells = ctx.room.Cells.Where(c =>
                {
                    var old = c.GetTerrain(ctx.map);
                    return old != floor && beauty >= old.GetStatValueAbstract(StatDefOf.Beauty) && value > old.GetStatValueAbstract(StatDefOf.MarketValue)
                           && GenConstruct.CanPlaceBlueprintAt(floor, c, Rot4.North, ctx.map).Accepted;
                }).ToList();
                if (cells.Count == 0 || !FloorAffordable(ctx, floor, cells.Count))
                    continue;
                var s = ctx.stats;
                foreach (var c in cells)
                {
                    var old = c.GetTerrain(ctx.map);
                    s.wealth += value - old.GetStatValueAbstract(StatDefOf.MarketValue);
                    s.beautySum += beauty - old.GetStatValueAbstract(StatDefOf.Beauty);
                    s.cleanSum += floor.GetStatValueAbstract(StatDefOf.Cleanliness) - old.GetStatValueAbstract(StatDefOf.Cleanliness);
                }
                float cost = (floor.CostList ?? new List<ThingDefCountClass>()).Sum(c => c.count * c.thingDef.BaseMarketValue) * cells.Count;
                var u = new Upgrade
                {
                    gain = Gain.Looks, floor = floor, cells = cells, cost = cost, value = s.Impressiveness - ctx.stats.Impressiveness, predicted = s.Impressiveness,
                    label = $"{floor.label} ({string.Join(", ", floor.CostList.Select(c => $"{c.count * cells.Count} {c.thingDef.label}"))}): {LooksText(ctx.stats, s)}",
                };
                if (best == null || u.value / Math.Max(1f, u.cost) > best.value / Math.Max(1f, best.cost))
                    best = u;
            }
            return best;
        }

        private static bool FloorAffordable(Context ctx, TerrainDef floor, int cells)
        {
            if (floor.CostList == null)
                return true;
            return floor.CostList.All(c => ctx.CanHave(c.thingDef, c.count * cells));
        }

        // ---------- Scoring (vanilla's room stats) ----------

        /// <summary>The room's impressiveness inputs; a copy is changed to see what vanilla would say after an upgrade.</summary>
        private struct Stats
        {
            public float wealth, beautySum, space, cleanSum;
            public int cells;

            public static Stats Of(Room room)
            {
                room.Notify_TerrainChanged(); // vanilla caches room stats; read them fresh
                return new Stats
                {
                    wealth = room.GetStat(RoomStatDefOf.Wealth),
                    beautySum = room.GetStat(RoomStatDefOf.Beauty) * CellCountFactor(room.CellCount),
                    space = room.GetStat(RoomStatDefOf.Space),
                    cleanSum = room.GetStat(RoomStatDefOf.Cleanliness) * room.CellCount,
                    cells = room.CellCount,
                };
            }

            /// <summary>RoomStatWorker_Impressiveness.GetScore, on these numbers.</summary>
            public float Impressiveness
            {
                get
                {
                    float w = Factor(wealth / 1500f), b = Factor(beautySum / CellCountFactor(cells) / 3f), s = Factor(Mathf.Min(space, 350f) / 125f);
                    float c = Factor(1f + Mathf.Min(cleanSum / cells, 0f) / 2.5f);
                    float num = Mathf.Lerp((w + b + s + c) / 4f, Mathf.Min(w, Mathf.Min(b, Mathf.Min(s, c))), 0.35f);
                    float cap = s * 5f;
                    if (num > cap)
                        num = Mathf.Lerp(num, cap, 0.75f);
                    return num * 100f;
                }
            }

            private static float Factor(float x) => Mathf.Abs(x) < 1f ? x : x > 0f ? 1f + Mathf.Log(x) : -1f - Mathf.Log(-x);

            /// <summary>RoomStatWorker_Beauty's CellCountCurve: (0, 20) to (40, 40), then the cell count.</summary>
            private static float CellCountFactor(int n) => n <= 40 ? 20f + n / 2f : n;
        }

        /// <summary>"awful → dull", or "awful, impressiveness +4" when the label stays.</summary>
        private static string LooksText(Stats before, Stats after)
        {
            string from = RoomStatDefOf.Impressiveness.GetScoreStage(before.Impressiveness).label;
            string to = RoomStatDefOf.Impressiveness.GetScoreStage(after.Impressiveness).label;
            return from != to ? $"{from} → {to}" : $"{from}, impressiveness {after.Impressiveness - before.Impressiveness:+0.#;-0.#}";
        }

        /// <summary>Cells an item takes from vanilla's space stat: 1.4 per standable cell it blocks, 0.9 if it can still be walked over.</summary>
        private static float SpaceTaken(ThingDef def) =>
            def.passability == Traversability.Impassable ? 1.4f * def.size.Area : def.passability == Traversability.PassThroughOnly ? 0.9f * def.size.Area : 0f;

        /// <summary>Heat pushed per second: a heat pusher's own, or a temperature control's energy (a heater +, a cooler −). 0 for neither.</summary>
        private static float Heat(ThingDef def, float roomTemperature)
        {
            var pusher = def.GetCompProperties<CompProperties_HeatPusher>();
            if (pusher != null && pusher.heatPerSecond != 0f)
                // Vanilla's pusher only works between its limits: a torch stops heating above 23°C.
                return roomTemperature < pusher.heatPushMaxTemperature && roomTemperature > pusher.heatPushMinTemperature ? pusher.heatPerSecond : 0f;
            var control = def.GetCompProperties<CompProperties_TempControl>();
            return control?.energyPerSecond ?? 0f;
        }

        /// <summary>
        /// The room's role stays the same with the item in it (a dining room turning rec room, or back, is the same great hall). Every role worker scores the room by the same code it uses
        /// for real rooms, with an unspawned copy of the item counted in (listed in one of the room's regions, then taken
        /// out). Vanilla's GetScoreDeltaIfBuildingPlaced isn't used: vanilla only asks it for work tables, and several
        /// workers there test thingClass the wrong way round, scoring a lamp as a bed.
        /// </summary>
        private static bool KeepsRole(Room room, ThingDef def, ThingDef stuff)
        {
            RoomRoleDef Best() => DefDatabase<RoomRoleDef>.AllDefsListForReading.OrderByDescending(r => r.Worker.GetScore(room)).First();
            var before = Best();
            Thing probe = ThingMaker.MakeThing(def, stuff);
            var lister = room.Regions[0].ListerThings;
            lister.Add(probe);
            try
            {
                var after = Best();
                return after == before || (Ground.HallRole(after) && Ground.HallRole(before));
            }
            catch (Exception e)
            {
                ModLog.Warning($"Scoring {def.defName} in {room.Role?.defName}: {e.Message}");
                return false;
            }
            finally
            {
                lister.Remove(probe);
            }
        }

        private static bool LinksTo(ThingDef facility, Thing anchor)
        {
            var links = anchor.def.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities;
            if (links == null || !links.Contains(facility))
                return false;
            int max = facility.GetCompProperties<CompProperties_Facility>()?.maxSimultaneous ?? 1;
            var linked = anchor.TryGetComp<CompAffectedByFacilities>()?.LinkedFacilitiesListForReading;
            return linked == null || linked.Count(f => f.def == facility) < max;
        }

        private static string FacilityText(ThingDef def) =>
            string.Join(", ", def.GetCompProperties<CompProperties_Facility>()?.statOffsets?.Select(s => $"{s.stat.label} {s.stat.ValueToString(s.value, ToStringNumberSense.Offset)}") ?? Enumerable.Empty<string>());

        private static float Cost(ThingDef def, ThingDef stuff) => def.CostListAdjusted(stuff).Sum(c => c.count * c.thingDef.BaseMarketValue);

        private static string CostText(ThingDef def, ThingDef stuff) =>
            " (" + string.Join(", ", def.CostListAdjusted(stuff).Select(c => $"{c.count} {c.thingDef.label}")) + ")";

        // ---------- Placement ----------

        /// <summary>A free slot by the placer's rules, around what's already in the room; next to its anchor for a facility or a seat.</summary>
        private static PlanEntry Slot(Context ctx, ThingDef def, Thing anchor)
        {
            Map map = ctx.map;
            CellRect interior = ctx.room.ExtentsClose;
            CellRect footprint = interior.ExpandedBy(1);
            var doors = footprint.EdgeCells.Where(c => c.GetDoor(map) != null && interior.Cells.Any(i => i.AdjacentToCardinal(c))).ToList();
            if (doors.Count == 0)
                return null;
            IntVec3 door = doors[0];
            Rot4 side = SiteFinder.SideOf(footprint, door);
            // Every door counts, not just the first: nothing right inside one, and the walk between each pair stays clear.
            var plan = new RoomPlan { map = map, footprint = footprint, door = door, doorInside = door - side.FacingCell, doorOutside = door + side.FacingCell };
            plan.broughtIn.AddRange(doors.Skip(1));
            var existing = ctx.things.Where(t => interior.Contains(t.Position))
                .Select(t => new PlanEntry(t is Blueprint || t is Frame ? (ThingDef)t.def.entityDefToBuild : t.def, t.Position, t.Rotation)).ToList();
            // Walls inside the room (a bathroom's stalls) are taken like furniture.
            existing.AddRange(interior.Cells.Where(c => !ctx.room.ContainsCell(c)).Select(c => new PlanEntry(ThingDefOf.Wall, c, Rot4.North)));
            PlanEntry next = anchor != null ? existing.Find(e => e.cell == anchor.Position)
                : def.building != null && def.building.isSittable ? existing.Find(e => e.def.surfaceType == SurfaceType.Eat) : null;
            var entry = RoomPlacer.PlaceOne(plan, def, existing, next);
            if (entry == null)
                return null;
            return GenConstruct.CanPlaceBlueprintAt(def, entry.cell, entry.rot, map, stuffDef: ctx.StuffFor(def, def.costStuffCount)).Accepted ? entry : null;
        }

        /// <summary>Items go in rectangular rooms only (every room a mind builds is one), walls inside allowed (a bathroom's stalls).</summary>
        private static bool Rectangular(Room room) =>
            !room.ExtentsClose.Cells.Any(c => !room.ContainsCell(c) && !(c.GetEdifice(room.Map) is Building b && b.def.IsWall));

        /// <summary>
        /// A work table the colony lacks, in a free slot of the room (one more bench in the workshop), with its conduit line:
        /// "stonecutter's table (60 wood): in the workshop". Null when it can't be paid, run, wired or fit.
        /// </summary>
        public static Upgrade NewBench(Room room, Pawn pawn, ThingDef def)
        {
            var ctx = new Context(room, pawn, Supplies.WallMaterials(pawn));
            if (!Rectangular(room) || !ctx.CanPay(def, out ThingDef stuff) || !ctx.CanRun(def))
                return null;
            var u = new Upgrade { def = def, stuff = stuff, cost = Cost(def, stuff), bench = true, entry = Slot(ctx, def, null),
                                  label = $"{def.label}{CostText(def, stuff)}: in the {BuildManager.Label(room)}" };
            if (u.entry == null || !ctx.Wire(u))
                return null;
            u.entry.stuff = stuff;
            return u;
        }

        /// <summary>A free slot for one more item in the room, by the same rules as an upgrade's (a made sculpture whose planned spot was taken).</summary>
        public static PlanEntry SlotIn(Room room, Pawn pawn, ThingDef def, ThingDef stuff)
        {
            var entry = Slot(new Context(room, pawn, Supplies.WallMaterials(pawn)), def, null);
            if (entry != null)
                entry.stuff = stuff;
            return entry;
        }

        /// <summary>
        /// Dev check (FURNISHING.md §7, check 5): applies a looks upgrade for real (the floor laid, the item spawned, the old
        /// one hidden), reads vanilla's impressiveness, and puts everything back. Returns vanilla's number.
        /// </summary>
        public static float TryForReal(Room room, Upgrade u)
        {
            Map map = room.Map;
            IntVec3 probe = room.Cells.First();
            float result;
            if (u.floor != null)
            {
                var old = u.cells.Select(c => (c, t: c.GetTerrain(map))).ToList();
                foreach (var c in u.cells)
                    map.terrainGrid.SetTerrain(c, u.floor);
                result = Recount(probe, map);
                foreach (var (c, t) in old)
                    map.terrainGrid.SetTerrain(c, t);
            }
            else
            {
                IntVec3 oldPos = u.replaces?.Position ?? IntVec3.Invalid;
                Rot4 oldRot = u.replaces?.Rotation ?? Rot4.North;
                u.replaces?.DeSpawn();
                Thing thing = ThingMaker.MakeThing(u.def, u.stuff);
                thing.SetFactionDirect(Faction.OfPlayer);
                thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, null); // what the prediction assumes (art: normal quality)
                GenSpawn.Spawn(thing, u.entry.cell, map, u.entry.rot);
                Plant grown = null;
                if (PotPlant(u.def) is ThingDef plant)
                {
                    grown = (Plant)ThingMaker.MakeThing(plant);
                    grown.Growth = 1f;
                    GenSpawn.Spawn(grown, u.entry.cell, map);
                }
                result = Recount(probe, map);
                grown?.Destroy(DestroyMode.Vanish);
                thing.Destroy(DestroyMode.Vanish);
                if (u.replaces != null)
                    GenSpawn.Spawn(u.replaces, oldPos, map, oldRot);
            }
            Recount(probe, map);
            return result;
        }

        private static float Recount(IntVec3 cell, Map map)
        {
            Room room = cell.GetRoom(map);
            room.Notify_TerrainChanged(); // marks its stats dirty: GetStat recounts
            return room.GetStat(RoomStatDefOf.Impressiveness);
        }

        /// <summary>A one-item (or floor) upgrade project for the room, not yet added.</summary>
        public static BuildProject NewProject(Pawn pawn, Room room, string where, ThingDef material) => new BuildProject
        {
            pawn = pawn, map = pawn.Map, footprint = room.ExtentsClose.ExpandedBy(1), placedTick = Find.TickManager.TicksGame,
            where = where, byMind = MindManager.Instance?.MindOf(pawn) != null, furnishing = true, material = material,
            kindDef = (Ground.HallRole(room.Role) ? RoomKindDef.GreatHall : DefDatabase<RoomKindDef>.AllDefsListForReading.FirstOrDefault(k => k.role == room.Role)) ?? RoomKindDef.Plain,
        };

        /// <summary>Places her pick as blueprints and tracks it as a one-item (or floor) upgrade project; marks what's missing.</summary>
        public static string Place(Pawn pawn, Room room, Upgrade u)
        {
            Map map = pawn.Map;
            string where = room.Owners.Contains(pawn) ? $"in my {room.Role.label}" : $"in the {BuildManager.Label(room)}";
            if (u.art)
                return Art.Order(pawn, u.def, u.stuff, null, u.entry, where);
            var project = NewProject(pawn, room, where, u.stuff);
            project.bench = u.bench;
            if (u.floor != null)
            {
                var cells = u.cells.Where(c => c.GetTerrain(map) != u.floor && GenConstruct.CanPlaceBlueprintAt(u.floor, c, Rot4.North, map).Accepted).ToList();
                if (cells.Count == 0)
                    return $"Couldn't lay the {u.floor.label}: nowhere left to put it.";
                foreach (var c in cells)
                    GenConstruct.PlaceBlueprintForBuild(u.floor, c, map, Rot4.North, Faction.OfPlayer, null);
                project.floor = u.floor;
                project.floorCells = cells;
            }
            else
            {
                var e = u.entry;
                var report = GenConstruct.CanPlaceBlueprintAt(e.def, e.cell, e.rot, map, thingToIgnore: u.removeFirst ? u.replaces : null, stuffDef: e.stuff);
                if (!report.Accepted)
                    return $"Couldn't place the {e.def.label}: {report.Reason}";
                var conduits = Wiring.Entries(u.conduits);
                foreach (var c in conduits)
                    BuildManager.Place(c.def, c.cell, map, c.rot, null);
                if (u.removeFirst)
                {
                    // The old one comes down first; its blueprint follows (BuildProject.TickRemoveFirst), and its orders move over once built.
                    map.designationManager.AddDesignation(new Designation(u.replaces, DesignationDefOf.Deconstruct));
                    project.removeFirst = u.replaces.Position;
                    project.removeDef = u.replaces.def;
                    project.bills = (u.replaces as IBillGiver)?.BillStack?.Bills.Select(b => b.Clone()).ToList();
                }
                else
                    BuildManager.Place(e.def, e.cell, map, e.rot, e.stuff);
                project.entries = new List<PlanEntry> { e };
                project.entries.AddRange(conduits);
            }
            BuildManager.Instance.Add(project);
            string marked = Supplies.MarkFor(pawn, project);
            ModLog.Message($"{pawn.LabelShort} upgrades {where}: {u.label}; impressiveness now {room.GetStat(RoomStatDefOf.Impressiveness):0.0}.");
            string what = u.floor != null ? $"to lay {u.floor.label}" : u.replaces != null ? $"a {u.def.label} in place of the {u.replaces.LabelNoCount}" : $"a {u.def.label}";
            return $"Planned {what} {where}." + (marked.Length > 0 ? " " + marked : "");
        }
    }
}
