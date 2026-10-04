using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The Base call (STREAMLINE.md §5): after she picks "work on the base", code lists what the base could get now, in
    /// groups (the ladder's next rung, the layout, food, stock-ups, other rooms), each with code's amounts and sizes. She
    /// picks one, plus a site and a material for a room, and makes one remark; code applies it. The call is free: picking
    /// the menu line was the decision.
    /// </summary>
    public static class BaseCall
    {
        private const int MaxStockUps = 7;
        private const int MaxPerKind = 2;
        private const string None = "-";
        private static readonly string[] GroupOrder = { "Next for the base", "Inside the base", "Food", "Medicine", "Storage", "Stock up", "Other rooms" };

        /// <summary>One numbered line. Exactly one of kind, layout, temperature, crop, upgrade, pile or chore is set.</summary>
        internal class Choice
        {
            public string group;
            public string label;
            public RoomKindDef kind;      // a room, at code's size
            public (int w, int h) size;
            public AskedFor.Ask ask;      // a room the game asks for (its items, whose it is), or null
            public ChoreOption chore;     // a stock-up
            public Room upgrade;          // a room to upgrade: the Upgrade call follows
            public ThingDef crop;         // a field
            public CellRect field;
            public string where;          // a field's or stockpile's place in words
            public CellRect pile;         // a stockpile, when the colony has none
            public Func<Pawn, ThingDef, string> layout; // a hub, a door between rooms, a way out closed (BASE_LAYOUT.md)
            public Func<Pawn, string> temperature; // warm or cool a room: survival, so it goes ahead even with a project running
        }

        /// <summary>The call, built but not sent: its prompt, schema and what each number means.</summary>
        public class Prepared
        {
            internal List<Choice> choices = new List<Choice>();
            internal List<RoomPlan> sites = new List<RoomPlan>();
            internal List<string> letters = new List<string>();
            internal List<ThingDef> materials = new List<ThingDef>();
            internal bool noSites; // a room could have been planned, but no site was found
            internal BuildProject underway; // the project running when the menu was made (only the dev call offers rooms then)
            public List<KeyValuePair<string, string>> messages;
            public Dictionary<string, object> schema;
            public int Count => choices.Count;
            public IEnumerable<string> Labels => choices.Select(c => $"{c.group}: {c.label}");
        }

        /// <summary>Whether the Act menu offers "work on the base": a room can be planned, or there's anything to stock up or grow.</summary>
        public static bool AnythingToDo(Pawn pawn)
        {
            if (BuildManager.Instance?.CantPlanReason(pawn) == null || TemperatureChoice(pawn) != null)
                return true;
            if (!AIPawnControlMod.Settings.allowChores || ChoreManager.Instance == null)
                return false;
            return pawn.Map.haulDestinationManager.AllGroupsListForReading.Count == 0 || FoodOutlook.For(pawn.Map).cellsWanted > 0 || Fields.MedicineCrop(pawn.Map) != null || ChoreOptions.All(pawn).Count > 0;
        }

        /// <summary>
        /// Fixed text of what the Base call can offer, so the Act menu scans nothing (the Base call does, once picked). With
        /// a project running (anyone's: one at a time), only heat or cooling, fields and stocking up are left: "work on the base (Stone's
        /// barracks comes first): …".
        /// </summary>
        public static string MenuLabel(Pawn pawn)
        {
            if (BuildManager.Instance?.Underway(pawn.Map) is BuildProject project)
                return $"work on the base ({ProjectName(project, pawn)} comes first): warm or cool a room, plant fields, or stock up by mining, cutting wood, hunting or foraging";
            return "work on the base: build or improve rooms, join rooms with halls and doors, warm or cool a room, plant fields, or stock up by mining, cutting wood, hunting or foraging";
        }

        /// <summary>"my barracks" or "Stone's barracks".</summary>
        private static string ProjectName(BuildProject project, Pawn pawn) =>
            (project.pawn == pawn ? "my " : project.pawn.LabelShort + "'s ") + (project.furnishing ? project.ItemLabel : project.KindFor);

        /// <summary>
        /// Why she can't start the next rung herself: "after Stone's barracks is finished (it's waiting on 15 wood)", or
        /// "not yet: a project was placed less than 2 hours ago". Null when she can.
        /// </summary>
        private static string Blocker(Pawn pawn)
        {
            var manager = BuildManager.Instance;
            if (manager?.Underway(pawn.Map) is BuildProject project)
            {
                var missing = project.Missing();
                return $"after {ProjectName(project, pawn)} is finished" +
                       (missing.Count > 0 ? $" (it's waiting on {string.Join(", ", missing.Select(kv => $"{kv.Value} {kv.Key.label}"))})" : "");
            }
            string why = manager?.CantPlanReason(pawn);
            return why != null ? $"not yet: {why}" : null;
        }

        /// <summary>
        /// Heat or cooling is her choice, not built into rooms: the room furthest outside comfortable, at any rung. It's
        /// survival, so it's offered even with a project running, but not while that room is already getting an item.
        /// </summary>
        private static Choice TemperatureChoice(Pawn pawn)
        {
            var manager = BuildManager.Instance;
            if (manager == null || !AIPawnControlMod.Settings.allowBuilding
                || !(Upgrades.Temperature(pawn) is (Room uncomfortable, Upgrades.Upgrade item, bool cold))
                || manager.ActiveOnAll(pawn.Map).Any(p => p.furnishing && p.Room == uncomfortable))
                return null;
            IntVec3 cell = uncomfortable.Cells.First();
            string name = uncomfortable.Owners.Contains(pawn) ? "my " + uncomfortable.Role.label : "the " + BuildManager.Label(uncomfortable);
            return new Choice
            {
                group = "Inside the base",
                label = $"{(cold ? "warm" : "cool")} {name}: {item.label}",
                temperature = p => cell.GetRoom(p.Map) is Room room && Upgrades.Temperature(p) is (Room again, Upgrades.Upgrade u, _) && again == room
                    ? Upgrades.Place(p, room, u) : "That room doesn't need it any more.",
            };
        }

        /// <summary>Builds the choices and sends the call. Returns the result line for her decisions.</summary>
        /// <param name="dev">"Base call now": the budget, cooldowns and caps don't apply.</param>
        public static string Start(PawnMind mind, bool dev = false)
        {
            Pawn pawn = mind.pawn;
            var clock = Stopwatch.StartNew();
            var call = Prepare(mind, dev);
            if (call.noSites && !dev)
                BuildManager.Instance.EmptyScan(pawn);
            if (call.Count == 0)
            {
                if (!dev)
                    mind.GiveBackAct();
                return "There's nothing I can do for the base right now.";
            }
            Map map = pawn.Map;
            ModLog.Message($"{pawn.LabelShort}: base call with {call.Count} choices ({clock.ElapsedMilliseconds} ms to build).");
            mind.Send("base", call.messages, call.schema, reply => OnReply(mind, reply, call),
                stillValid: () => pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map ? "gone" : null);
            return "Thinking about what the base needs.";
        }

        /// <summary>The choices, prompt and schema, without sending anything or changing the map.</summary>
        public static Prepared Prepare(PawnMind mind, bool dev)
        {
            Pawn pawn = mind.pawn;
            Map map = pawn.Map;
            var call = new Prepared();
            var choices = call.choices;
            var manager = BuildManager.Instance;
            call.underway = manager?.Underway(map);
            bool canPlan = manager != null && (manager.CantPlanReason(pawn) == null
                                               || (dev && AIPawnControlMod.Settings.allowBuilding && manager.ActiveProject(pawn) == null));

            // Rooms: the ladder's next rung, then kinds the colony lacks. Sites only get scanned when a room is possible.
            var rung = Ladder.Current(map);
            string rungText = rung == null ? "Next for the base: nothing; it has what an early colony needs."
                : rung.waiting != null ? $"Next for the base: {rung.label} ({rung.waiting})."
                : $"Next for the base: {rung.label}.";
            SiteFinder finder = null;
            string materialsLine = null;
            if (canPlan)
            {
                finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
                var had = Supplies.WallMaterials(pawn);
                call.materials = had.Select(m => m.stuff).ToList();
                materialsLine = Supplies.WallMaterialsLine(had);
                var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
                finder.For(rung?.kind); // the sites suit the ladder's next room (its goods' neighbours, BASE_GROWTH.md §6.2)
                call.sites = finder.Sites(validator, call.materials[0], out _);
                if (call.sites.Count == 0)
                {
                    call.noSites = true;
                    rungText += " There's no space for a new room near the base.";
                }
                else
                {
                    if (rung?.kind != null && rung.waiting == null
                        && RoomChoice(rung.kind, SizeFor(rung.kind, map), "Next for the base", rung.label, finder, validator, call.sites, call.materials) is Choice next)
                        choices.Add(next);
                    // What the game asks for comes first among the other rooms (BASE_GROWTH.md §6.6), with vanilla's reason.
                    foreach (var ask in AskedFor.Current(map))
                        if (RoomChoice(ask.kind, AskedFor.SizeFor(ask, map), "Other rooms", ask.why, finder, validator, call.sites, call.materials, ask) is Choice asked)
                        {
                            asked.ask = ask;
                            choices.Add(asked);
                        }
                    foreach (var kind in DefDatabase<RoomKindDef>.AllDefsListForReading.Where(k => k != rung?.kind && OtherRoom(k, pawn)))
                        if (RoomChoice(kind, SizeFor(kind, map), "Other rooms", kind == RoomKindDef.Bedroom ? "my own bedroom" : null, finder, validator, call.sites, call.materials) is Choice other)
                            choices.Add(other);
                }
                // Fewer ways out (BASE_LAYOUT.md §5.7): the rung as a hub, a hall, a closed way out, a door between rooms.
                choices.AddRange(Layout.Options(map, rung, finder, validator, call.materials).Select(o => new Choice { group = o.group, label = o.label, layout = o.apply }));
            }
            else if (call.underway != null && rung?.kind != null && rung.waiting == null)
            {
                // A project is underway: the ladder's next room is still an option, in the materials storage has all of it in now.
                finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
                var had = Supplies.WallMaterials(pawn);
                var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
                finder.For(rung.kind);
                call.sites = finder.Sites(validator, had[0].stuff, out _);
                var size = SizeFor(rung.kind, map);
                var plan = call.sites.Count > 0 ? finder.Fit(call.sites[0], rung.kind, size.w, size.h, validator, had[0].stuff, out _) : null;
                call.materials = plan == null ? new List<ThingDef>()
                    : had.Select(m => m.stuff).Where(m => plan.Cost(m).All(kv => map.resourceCounter.GetCount(kv.Key) >= kv.Value)).ToList();
                if (call.materials.Count > 0 && RoomChoice(rung.kind, size, "Next for the base", rung.label, finder, validator, call.sites, call.materials) is Choice next)
                {
                    materialsLine = Supplies.WallMaterialsLine(had.Where(m => call.materials.Contains(m.stuff)).ToList());
                    choices.Add(next);
                }
            }
            if (TemperatureChoice(pawn) is Choice temperature)
                choices.Add(temperature);

            // Food: a field only when the outlook falls short (STREAMLINE.md §6).
            var outlook = FoodOutlook.For(map);
            if (AIPawnControlMod.Settings.allowChores && outlook.cellsWanted > 0 && FieldChoice(pawn, outlook) is Choice field)
                choices.Add(field);

            // Medicine: one small field, once, while there's none.
            if (AIPawnControlMod.Settings.allowChores && Fields.MedicineCrop(map) is ThingDef medicine
                && Fields.FindSite(pawn, 4, out CellRect herbs, out string herbsWhere, out string herbsGround))
                choices.Add(new Choice
                {
                    group = "Medicine",
                    label = $"a field of {medicine.label} (there's no medicine field yet): {herbs.Width}x{herbs.Height}, {herbsGround}, {herbsWhere}",
                    crop = medicine,
                    field = herbs,
                    where = herbsWhere,
                });

            // Stock up: at most two lines per kind.
            if (AIPawnControlMod.Settings.allowChores && ChoreManager.Instance != null)
                choices.AddRange(ChoreOptions.All(pawn, ignoreLimits: dev)
                    .GroupBy(o => o.kind).SelectMany(g => g.Take(MaxPerKind))
                    .OrderByDescending(o => o.useful).Take(MaxStockUps)
                    .Select(o => new Choice { group = "Stock up", label = o.label, chore = o }));

            if (AIPawnControlMod.Settings.allowChores && ChoreManager.Instance != null && map.haulDestinationManager.AllGroupsListForReading.Count == 0
                && Stockpiles.FindSite(pawn, out CellRect pile, out string pileWhere))
                choices.Add(new Choice
                {
                    group = "Storage",
                    label = $"a stockpile for everything ({pile.Width}x{pile.Height}, {pileWhere}): there's none yet, so nothing is stored or counted",
                    pile = pile,
                    where = pileWhere,
                });

            // Past the early base, the ladder's last rung is the room most worth upgrading (the only way upgrades are
            // offered); the Upgrade call offers the concrete upgrades.
            Room worst = rung != null && rung.rooms && canPlan
                ? Upgrades.Worst(Upgrades.Rooms(pawn), pawn) : null;
            if (rung != null && rung.rooms)
                rungText = worst == null && canPlan ? "Next for the base: nothing; every room has what it can get for now." : "Next for the base: better rooms.";
            if (worst != null)
                choices.Add(new Choice { group = "Next for the base", label = "upgrade " + Upgrades.RoomLine(worst, pawn), upgrade = worst });
            if (!canPlan && rung != null && Blocker(pawn) is string blocker)
                rungText = rungText.TrimEnd('.') + ", " + blocker + ".";

            if (choices.Count == 0)
                return call;
            // Numbered in group order, so the list reads as groups.
            call.choices = choices.OrderBy(c => Array.IndexOf(GroupOrder, c.group)).ToList();
            var lines = new List<string>();
            string lastGroup = null;
            for (int i = 0; i < call.choices.Count; i++)
            {
                if (call.choices[i].group != lastGroup)
                    lines.Add(call.choices[i].group);
                lastGroup = call.choices[i].group;
                lines.Add($" {i + 1}: {call.choices[i].label}");
            }
            bool anyRoom = call.choices.Any(c => c.kind != null);
            bool walls = anyRoom || call.choices.Any(c => c.layout != null);
            if (anyRoom)
                call.letters = call.sites.Select((s, i) => ((char)('A' + i)).ToString()).ToList();
            string rooms = (anyRoom
                ? "Sites for a room (walls and door cost at 5x5):\n" + string.Join("\n", call.sites.Select((s, i) => finder.Describe(s, call.letters[i][0], call.materials, finder.MaxFit(s)))) + "\n"
                : "")
                + (walls ? $"Wall materials: {materialsLine}. The colony marks trees or ore for what's missing, but only what's nearby can be had." : "");
            call.messages = PromptBuilder.Build("base", mind, new Dictionary<string, string>
            {
                ["rung"] = rungText,
                ["options"] = string.Join("\n", lines),
                ["rooms"] = rooms,
            });
            call.schema = Schema.Obj(new Dictionary<string, object>
            {
                ["reason"] = Schema.Reason(),
                ["choice"] = Schema.Pick(call.Count),
                ["site"] = Schema.StrEnum(call.letters.Append(None)),
                ["material"] = Schema.StrEnum((walls ? call.materials.Select(m => m.label) : Enumerable.Empty<string>()).Append(None)),
                ["say"] = Schema.Say(),
            });
            return call;
        }

        /// <summary>
        /// Code's size for a kind (STREAMLINE.md §7): the kind's own, a barracks sized to the beds missing, and a room whose
        /// items grow with the colony (a dining hall's tables, BASE_GROWTH.md §6.1) about 12 cells per anchor over the
        /// kind's own size. Fit tries the nearest sizes if this one doesn't hold them.
        /// </summary>
        public static (int w, int h) SizeFor(RoomKindDef kind, Map map)
        {
            if (kind.defName == "AIPC_Barracks")
                return Ladder.Sleepers(map).Count - Ladder.BedSlots(map) > 4 ? (6, 6) : (5, 5);
            int anchors = kind.items.Where(i => i.perAnchor > 0).Select(i => (i.Count(map) + i.perAnchor - 1) / i.perAnchor).DefaultIfEmpty(1).Max();
            if (anchors <= 1)
                return (kind.size.x, kind.size.z);
            int area = kind.size.x * kind.size.z + 12 * (anchors - 1);
            var shape = SiteFinder.FitShapes.Where(s => s.w <= s.h && s.w * s.h >= area).OrderBy(s => s.w * s.h).ThenBy(s => s.h - s.w).DefaultIfEmpty((w: 10, h: 10)).First(); // past 10×10: the biggest
            return (shape.w, shape.h);
        }

        /// <summary>A kind the colony doesn't have yet (a room with its role, or a bench it lacks), or her own bedroom when she has none.</summary>
        private static bool OtherRoom(RoomKindDef kind, Pawn pawn)
        {
            Map map = pawn.Map;
            if (kind == RoomKindDef.Plain || kind.layout || kind.askedFor != null || !kind.Wanted(map) || !kind.BuildableNow(map) || kind.defName == "AIPC_Barracks")
                return false;
            if (kind.owned)
                return pawn.ownership?.OwnedRoom == null;
            if (BuildManager.Instance.ActiveOn(map).Any(p => p.kindDef == kind))
                return false;
            if (kind.items.Any(i => i.preferNew) && kind.items.Where(i => i.preferNew).All(i => i.Resolve(map) is ThingDef def && !map.listerBuildings.ColonistsHaveBuilding(def)))
                return true;
            return kind.role != null && !map.regionGrid.AllRooms.Any(r => r.Role == kind.role && Ground.Indoor(r));
        }

        /// <summary>"a kitchen (5x5: fueled stove, butcher table; 80 steel)". Null when it doesn't fit at the best site.</summary>
        private static Choice RoomChoice(RoomKindDef kind, (int w, int h) size, string group, string name, SiteFinder finder, RoomValidator validator,
                                         List<RoomPlan> sites, List<ThingDef> materials, AskedFor.Ask ask = null)
        {
            if (kind == null)
                return null;
            ask?.Apply();
            var plan = finder.Fit(sites[0], kind, size.w, size.h, validator, materials[0], out _);
            if (plan == null)
                return null;
            var furniture = plan.Furniture.ToList();
            string items = string.Join(", ", furniture.GroupBy(e => e.def).Select(g => g.Count() > 1 ? $"{g.Count()} {Find.ActiveLanguageWorker.Pluralize(g.Key.label, g.Count())}" : g.Key.label));
            var cost = new RoomPlan { kind = kind, map = plan.map, footprint = plan.footprint };
            cost.entries.AddRange(furniture);
            string what = furniture.Count > 0 ? $"{items}; {SiteFinder.CostText(cost, materials)}" : kind.description;
            if (ask?.floorTags != null)
                what += "; all floored";
            if (ask?.alsoWants.Count > 0)
                what += "; it also wants: " + string.Join(", ", ask.alsoWants);
            string label = (name != null && name != "a " + kind.label ? $"{name}: a {kind.label}" : $"a {kind.label}") + $" ({plan.SizeLabel.Replace('×', 'x')}: {what})";
            // Minds stocked up for the next rung instead of laying it out: say where it counts that it fetches its own materials.
            if (group == "Next for the base")
                label += "; whatever it's missing gets marked when you lay it out";
            return new Choice { group = group, label = label, kind = kind, size = size };
        }

        private static Choice FieldChoice(Pawn pawn, FoodOutlook outlook)
        {
            Map map = pawn.Map;
            if (!map.mapPawns.FreeColonistsSpawned.Any(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing))
                || !Fields.FindSite(pawn, outlook.FieldSide, out CellRect rect, out string where, out string ground))
                return null;
            var why = new List<string>();
            if (outlook.growPerDay < outlook.needPerDay)
                why.Add(outlook.growPerDay <= 0f ? $"there are no food fields for {outlook.colonists} people" : $"the fields grow ~{outlook.growPerDay / outlook.needPerDay:P0} of what {outlook.colonists} people eat");
            if (outlook.hasWinter && outlook.daysToWinter > 0 && outlook.WinterCover < outlook.needPerDay * outlook.winterDays)
                why.Add($"crops stop growing in {outlook.daysToWinter} days and the stores won't last until they grow again");
            return new Choice
            {
                group = "Food",
                label = $"more field ({string.Join("; ", why)}): {rect.Width}x{rect.Height} of {outlook.crop.label}, {ground}, {where}",
                crop = outlook.crop,
                field = rect,
                where = where,
            };
        }

        private static void OnReply(PawnMind mind, Dictionary<string, object> reply, Prepared call)
        {
            Pawn pawn = mind.pawn;
            string say = reply.Str("say"), site = reply.Str("site"), material = reply.Str("material");
            int pick = reply.Int("choice", -1);
            if (pick == 0)
            {
                RemarkAndLog(mind, say, "Looked over the base and let the stores build up for now.", "base: not now");
                return;
            }
            var choice = pick >= 1 && pick <= call.Count ? call.choices[pick - 1] : null;
            if (choice == null)
            {
                ModLog.Warning($"{pawn.LabelShort}: base reply chose {pick}, which isn't on the list.");
                return;
            }
            // One project at a time: someone else may have started one while she was thinking.
            if ((choice.kind != null || choice.layout != null || choice.upgrade != null) && call.underway == null
                && BuildManager.Instance.Underway(pawn.Map) is BuildProject started)
            {
                RemarkAndLog(mind, say, $"{ProjectName(started, pawn).CapitalizeFirst()} was started meanwhile, so I left it at that; the base builds one thing at a time.",
                    $"base: {choice.label} (another project started)");
                return;
            }
            if (choice.upgrade != null)
            {
                RemarkAndLog(mind, say, UpgradeCall.Start(mind, choice.upgrade), $"base: upgrade {Upgrades.RoomLine(choice.upgrade, pawn)}");
                return;
            }
            ThingDef stuff = call.materials.Find(m => m.label == material) ?? call.materials.FirstOrDefault();
            string result = MindActions.Safely(pawn, choice.label, () =>
            {
                if (choice.kind != null)
                {
                    int siteIndex = call.letters.IndexOf(site);
                    return PlaceRoom(pawn, choice.kind, choice.size, call.sites[siteIndex >= 0 ? siteIndex : 0], stuff, out _, choice.ask);
                }
                if (choice.layout != null)
                    return choice.layout(pawn, stuff);
                if (choice.temperature != null)
                    return choice.temperature(pawn);
                if (choice.crop != null)
                    return Fields.Place(pawn, choice.field, choice.crop, choice.where);
                if (choice.group == "Storage")
                    return Stockpiles.Place(pawn, choice.pile, choice.where);
                return StockUp(mind, choice.chore);
            });
            RemarkAndLog(mind, say, result, $"base: {choice.label} [{site}, {material}]");
        }

        /// <summary>
        /// Places the room at code's size, picks whose bed it is, and marks what materials are missing. Another mind may have
        /// started the same room, or put blueprints on her site, while she was thinking: then she leaves it to them, or the
        /// sites are found again and the one nearest her pick is used. With no pick (the dev tools), site A, else the next
        /// site it fits at.
        /// </summary>
        public static string PlaceRoom(Pawn pawn, RoomKindDef kind, (int w, int h) size, RoomPlan picked, ThingDef material, out BuildProject project,
                                       AskedFor.Ask ask = null)
        {
            Map map = pawn.Map;
            project = null;
            ask?.Apply();
            var other = BuildManager.Instance.ActiveOn(map).FirstOrDefault(p => p.kindDef == kind && p.pawn != pawn && !kind.owned);
            if (other != null)
                return $"{other.pawn.LabelShort} already started a {kind.label}, so I left it to them.";
            var finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
            var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
            finder.For(kind);
            var sites = finder.Sites(validator, material, out _);
            if (picked != null)
                sites = sites.OrderBy(s => s.Interior.CenterCell.DistanceToSquared(picked.Interior.CenterCell)).Take(1).ToList();
            var plan = sites.Select(s => finder.Fit(s, kind, size.w, size.h, validator, material, out _)).FirstOrDefault(p => p != null);
            if (plan != null && Supplies.BlocksShort(plan, material) is string blocksShort)
                return $"Didn't lay out the {kind.label} in {material.label}: {blocksShort}.";
            if (plan != null && ask?.precept != null)
                foreach (var e in plan.entries.Where(e => e.def == ask.precept.ThingDef))
                    e.precept = ask.precept;
            project = plan != null ? BuildManager.Instance.Place(pawn, plan, material, validator, finder.Where(plan)) : null;
            if (project == null)
            {
                ModLog.Message($"No {kind.label} fits at {sites.Count} sites; the placer's last failure: {RoomPlacer.LastFailure ?? "none"}.");
                return $"Wanted a {kind.label}, but it didn't fit anywhere near the base.";
            }
            if (ask != null)
            {
                project.occupant = ask.who; // the throne's owner, or nobody's
                if (AskedFor.LayFloor(ask, project) is TerrainDef floor)
                    ModLog.Message($"{pawn.LabelShort} laid {floor.label} in the new {kind.label}.");
            }
            if (kind.owned && pawn.ownership?.OwnedRoom != null)
            {
                // Hers if she has no room of her own; else for someone who has none (the ladder's private bedrooms); else free.
                var taken = new HashSet<Pawn>(BuildManager.Instance.ActiveOn(map).Select(p => p.occupant).Where(p => p != null));
                project.occupant = Ladder.Sleepers(map).FirstOrDefault(p => p != pawn && p.ownership?.OwnedRoom == null && !taken.Contains(p));
                project.unclaimed = project.occupant == null;
            }
            string marked = Supplies.MarkFor(pawn, project);
            return $"Laid out a {project.KindFor} ({plan.SizeLabel}, {material.label}) {project.where}." + (marked.Length > 0 ? " " + marked : "");
        }

        /// <summary>Applies a stock-up line; if someone marked some of it meanwhile, the same kind found again now (PHASE6.md §5.1).</summary>
        private static string StockUp(PawnMind mind, ChoreOption option)
        {
            if (option.check() != null)
            {
                var fresh = ChoreOptions.All(mind.pawn, ignoreLimits: true).FirstOrDefault(o => o.kind == option.kind && o.check() == null);
                if (fresh == null)
                    return ChoreOptions.NoneLeft(new[] { option.check() }, $"Couldn't: {option.check()}.");
                ModLog.Message($"{mind.pawn.LabelShort}: \"{option.label}\" was taken meanwhile; took \"{fresh.label}\" instead.");
                option = fresh;
            }
            return option.apply(mind);
        }

        /// <summary>Her one remark (the Act's own "say" was dropped), then the decision line.</summary>
        public static void RemarkAndLog(PawnMind mind, string rawSay, string result, string what)
        {
            string say = SpeechLog.Clean(rawSay);
            if (say != null && AIPawnControlMod.Settings.speakLines)
            {
                SpeechLog.Say(mind.pawn, say);
                result += $" Said: \"{say}\"";
            }
            mind.AddDecision(result, importance: 0); // the room or chore itself records the memory event
            ModLog.Message($"{mind.pawn.LabelShort} {what} | {result} | Reason: {mind.lastReason}");
        }
    }
}
