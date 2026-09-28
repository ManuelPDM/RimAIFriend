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
    /// groups (the ladder's next rung, food, stock-ups, other rooms), each with code's amounts and sizes. She
    /// picks one, plus a site and a material for a room, and makes one remark; code applies it. The call is free: picking
    /// the menu line was the decision.
    /// </summary>
    public static class BaseCall
    {
        private const int MaxStockUps = 7;
        private const int MaxPerKind = 2;

        private class Choice
        {
            public string group;
            public string label;
            public RoomKindDef kind;      // a room, at code's size
            public (int w, int h) size;
            public ChoreOption chore;     // a stock-up
            public Room upgrade;          // a room to upgrade: the Upgrade call follows
            public ThingDef crop;         // a field
            public CellRect field;
            public string fieldWhere;
            public CellRect pile;         // a stockpile, when the colony has none
            public Func<Pawn, ThingDef, string> layout; // a common room, a door between rooms, a way out closed (BASE_LAYOUT.md)
        }

        /// <summary>Whether the Act menu offers "work on the base": a room can be planned, or there's anything to stock up or grow.</summary>
        public static bool AnythingToDo(Pawn pawn)
        {
            if (BuildManager.Instance?.CantPlanReason(pawn) == null)
                return true;
            if (!AIPawnControlMod.Settings.allowChores || ChoreManager.Instance == null)
                return false;
            return pawn.Map.haulDestinationManager.AllGroupsListForReading.Count == 0 || FoodOutlook.For(pawn.Map).cellsWanted > 0 || ChoreOptions.All(pawn).Count > 0;
        }

        /// <summary>"work on the base (next: a dining room)", or with her project running "work on the base (stock up; my barracks comes first)".</summary>
        public static string MenuLabel(Pawn pawn)
        {
            if (BuildManager.Instance?.ActiveProject(pawn) is BuildProject project)
                return $"work on the base (stock up; my {ProjectName(project)} comes first)";
            var rung = Ladder.Current(pawn.Map);
            return rung != null ? $"work on the base (next: {rung.label})" : "work on the base (stock up, rooms, furniture)";
        }

        private static string ProjectName(BuildProject project) => project.furnishing ? project.ItemLabel : project.KindFor;

        /// <summary>
        /// Why she can't start the next rung herself: "after my barracks is finished (it's waiting on 15 wood)", or
        /// "not yet: a project was placed less than 2 hours ago". Null when she can.
        /// </summary>
        private static string Blocker(Pawn pawn)
        {
            var manager = BuildManager.Instance;
            if (manager?.ActiveProject(pawn) is BuildProject project)
            {
                var missing = project.Missing();
                return $"after my {ProjectName(project)} is finished" +
                       (missing.Count > 0 ? $" (it's waiting on {string.Join(", ", missing.Select(kv => $"{kv.Value} {kv.Key.label}"))})" : "");
            }
            string why = manager?.CantPlanReason(pawn);
            return why != null ? $"not yet: {why}" : null;
        }

        /// <summary>Builds the choices and sends the call. Returns the result line for her decisions.</summary>
        /// <param name="dev">"Base call now": the budget, cooldowns and caps don't apply.</param>
        public static string Start(PawnMind mind, bool dev = false)
        {
            Pawn pawn = mind.pawn;
            Map map = pawn.Map;
            var clock = Stopwatch.StartNew();
            var choices = new List<Choice>();
            var manager = BuildManager.Instance;
            bool canPlan = manager != null && (manager.CantPlanReason(pawn) == null
                                               || (dev && AIPawnControlMod.Settings.allowBuilding && manager.ActiveProject(pawn) == null));

            // Rooms: the ladder's next rung, then kinds the colony lacks. Sites only get scanned when a room is possible.
            var rung = Ladder.Current(map);
            string rungText = rung == null ? "Next for the base: nothing; it has what an early colony needs."
                : rung.waiting != null ? $"Next for the base: {rung.label} ({rung.waiting})."
                : $"Next for the base: {rung.label}.";
            SiteFinder finder = null;
            RoomValidator validator = null;
            var sites = new List<RoomPlan>();
            var materials = new List<ThingDef>();
            string materialsLine = null;
            if (canPlan)
            {
                finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
                var had = Supplies.WallMaterials(pawn);
                materials = had.Select(m => m.stuff).ToList();
                materialsLine = Supplies.WallMaterialsLine(had);
                validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
                sites = finder.Sites(validator, materials[0], out _);
                if (sites.Count == 0)
                {
                    manager.EmptyScan(pawn);
                    rungText += " There's no space for a new room near the base.";
                }
            }
            if (sites.Count > 0)
            {
                if (rung != null && rung.kind != null && rung.waiting == null && RoomChoice(rung.kind, SizeFor(rung.kind, map), "Next for the base", rung.label, finder, validator, sites, materials) is Choice next)
                    choices.Add(next);
                foreach (var kind in DefDatabase<RoomKindDef>.AllDefsListForReading.Where(k => k != rung?.kind && OtherRoom(k, pawn)))
                    if (RoomChoice(kind, SizeFor(kind, map), "Other rooms", kind == RoomKindDef.Bedroom ? "my own bedroom" : null, finder, validator, sites, materials) is Choice other)
                        choices.Add(other);
            }

            // Fewer ways out (BASE_LAYOUT.md §5.7): the rung as a common room, a plain common room, a closed way out, a door between rooms.
            if (canPlan)
                choices.AddRange(Layout.Options(map, rung).Select(o => new Choice { group = o.group, label = o.label, layout = o.apply }));

            // Food: a field only when the outlook falls short (STREAMLINE.md §6).
            var outlook = FoodOutlook.For(map);
            if (AIPawnControlMod.Settings.allowChores && outlook.cellsWanted > 0 && FieldChoice(pawn, outlook) is Choice field)
                choices.Add(field);

            // Stock up: at most two lines per kind.
            if (AIPawnControlMod.Settings.allowChores && ChoreManager.Instance != null)
                choices.AddRange(ChoreOptions.All(pawn, ignoreLimits: dev)
                    .GroupBy(o => o.kind).SelectMany(g => g.Take(MaxPerKind))
                    .OrderByDescending(o => o.useful).Take(MaxStockUps)
                    .Select(o => new Choice { group = "Stock up", label = o.label, chore = o }));

            if (AIPawnControlMod.Settings.allowChores && ChoreManager.Instance != null && map.haulDestinationManager.AllGroupsListForReading.Count == 0
                && PileChoice(pawn) is Choice pile)
                choices.Add(pile);

            // Past the early base, the ladder's last rung is the room most worth upgrading (the only way upgrades are
            // offered); the Upgrade call offers the concrete upgrades.
            Room worst = rung != null && rung.rooms && canPlan
                ? Upgrades.Worst(Upgrades.Rooms(pawn).Where(r => Upgrades.For(r, pawn).Count > 0), pawn) : null;
            if (rung != null && rung.rooms)
                rungText = worst == null && canPlan ? "Next for the base: nothing; every room has what it can get for now." : "Next for the base: better rooms.";
            if (worst != null)
                choices.Add(new Choice { group = "Next for the base", label = "upgrade " + Upgrades.RoomLine(worst, pawn), upgrade = worst });
            if (!canPlan && rung != null && Blocker(pawn) is string blocker)
                rungText = rungText.TrimEnd('.') + ", " + blocker + ".";

            if (choices.Count == 0)
            {
                if (!dev)
                    mind.GiveBackAct();
                return "There's nothing I can do for the base right now.";
            }
            // Numbered in group order, so the list reads as groups.
            string[] order = { "Next for the base", "Inside the base", "Food", "Storage", "Stock up", "Other rooms" };
            choices = choices.OrderBy(c => Array.IndexOf(order, c.group)).ToList();
            var lines = new List<string>();
            string lastGroup = null;
            for (int i = 0; i < choices.Count; i++)
            {
                if (choices[i].group != lastGroup)
                    lines.Add(choices[i].group);
                lastGroup = choices[i].group;
                lines.Add($" {i + 1}: {choices[i].label}");
            }
            bool anyRoom = choices.Any(c => c.kind != null);
            bool walls = anyRoom || choices.Any(c => c.layout != null);
            var letters = anyRoom ? sites.Select((s, i) => ((char)('A' + i)).ToString()).ToList() : new List<string>();
            string rooms = (anyRoom
                ? "Sites for a room (walls and door cost at 5x5):\n" + string.Join("\n", sites.Select((s, i) => finder.Describe(s, letters[i][0], materials, finder.MaxFit(s)))) + "\n"
                : "")
                + (walls ? $"Wall materials: {materialsLine}. The colony marks trees or ore for what's missing, but only what's nearby can be had." : "");
            var messages = PromptBuilder.Build("base", mind, new Dictionary<string, string>
            {
                ["rung"] = rungText,
                ["options"] = string.Join("\n", lines),
                ["rooms"] = rooms,
            });
            var schema = Schema(choices.Count, letters, walls ? materials.Select(m => m.label).ToList() : new List<string>());
            ModLog.Message($"{pawn.LabelShort}: base call with {choices.Count} choices ({clock.ElapsedMilliseconds} ms to build).");
            mind.Send("base", messages, schema, reply => OnReply(mind, reply, choices, sites, letters, materials),
                stillValid: () => pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map ? "gone" : null);
            return "Thinking about what the base needs.";
        }

        /// <summary>Code's size for a kind (STREAMLINE.md §7): the kind's own, and a barracks sized to the beds missing.</summary>
        private static (int w, int h) SizeFor(RoomKindDef kind, Map map)
        {
            if (kind.defName == "AIPC_Barracks")
                return Ladder.Sleepers(map).Count - Ladder.BedSlots(map) > 4 ? (6, 6) : (5, 5);
            return (kind.size.x, kind.size.z);
        }

        /// <summary>A kind the colony doesn't have yet (a room with its role, or a bench it lacks), or her own bedroom when she has none.</summary>
        private static bool OtherRoom(RoomKindDef kind, Pawn pawn)
        {
            Map map = pawn.Map;
            if (kind == RoomKindDef.Plain || !kind.BuildableNow(map) || kind.defName == "AIPC_Barracks")
                return false;
            if (kind.owned)
                return pawn.ownership?.OwnedRoom == null;
            if (BuildManager.Instance.ActiveOn(map).Any(p => p.kindDef == kind))
                return false;
            if (kind.items.Any(i => i.preferNew) && kind.items.Where(i => i.preferNew).All(i => i.Resolve(map) is ThingDef def && !map.listerBuildings.ColonistsHaveBuilding(def)))
                return true;
            return kind.role != null && !map.regionGrid.AllRooms.Any(r => r.Role == kind.role && !r.PsychologicallyOutdoors);
        }

        /// <summary>"a kitchen: a fueled stove. At 5x5: fueled stove, butcher table; 80 steel." Null when it doesn't fit at the best site.</summary>
        private static Choice RoomChoice(RoomKindDef kind, (int w, int h) size, string group, string name, SiteFinder finder, RoomValidator validator,
                                         List<RoomPlan> sites, List<ThingDef> materials)
        {
            if (kind == null)
                return null;
            var plan = finder.Fit(sites[0], kind, size.w, size.h, validator, materials[0], out _);
            if (plan == null)
                return null;
            var furniture = plan.Furniture.ToList();
            string items = string.Join(", ", furniture.GroupBy(e => e.def).Select(g => g.Count() > 1 ? $"{g.Count()} {Find.ActiveLanguageWorker.Pluralize(g.Key.label, g.Count())}" : g.Key.label));
            var cost = new RoomPlan { kind = kind, map = plan.map, footprint = plan.footprint };
            cost.entries.AddRange(furniture);
            string what = furniture.Count > 0 ? $"{items}; {SiteFinder.CostText(cost, materials)}" : kind.description;
            string label = (name != null && name != "a " + kind.label ? $"{name}: a {kind.label}" : $"a {kind.label}") + $" ({plan.SizeLabel.Replace('×', 'x')}: {what})";
            // Minds stocked up for the next rung instead of laying it out: say where it counts that it fetches its own materials.
            if (group == "Next for the base")
                label += "; whatever it's missing gets marked when you lay it out";
            return new Choice { group = group, label = label, kind = kind, size = size };
        }

        private static Choice FieldChoice(Pawn pawn, FoodOutlook outlook)
        {
            Map map = pawn.Map;
            if (!map.mapPawns.FreeColonistsSpawned.Any(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)))
                return null;
            int side = outlook.FieldSide;
            var foci = SiteFinder.NoBuildFoci(map);
            var zones = new ZoneSites(new ChoreScan(pawn), c => Fields.CellOk(c, map, foci), c => Fields.CellScore(c, map));
            var site = zones.Find(new[] { 4, side }, 1).FirstOrDefault();
            if (site == null)
                return null;
            var rect = site.Rect(site.maxSize);
            var why = new List<string>();
            if (outlook.growPerDay < outlook.needPerDay)
                why.Add(outlook.growPerDay <= 0f ? $"there are no food fields for {outlook.colonists} people" : $"the fields grow ~{outlook.growPerDay / outlook.needPerDay:P0} of what {outlook.colonists} people eat");
            if (outlook.hasWinter && outlook.daysToWinter > 0 && outlook.WinterCover < outlook.needPerDay * outlook.winterDays)
                why.Add($"winter is {outlook.daysToWinter} days off and the stores won't last it");
            string where = zones.Where(rect, site.steps);
            return new Choice
            {
                group = "Food",
                label = $"more field ({string.Join("; ", why)}): {rect.Width}x{rect.Height} of {outlook.crop.label}, {zones.Ground(rect)}, {where}",
                crop = outlook.crop,
                field = rect,
                fieldWhere = where,
            };
        }

        /// <summary>"a stockpile (6x6, right by the base)": offered only while the colony has no stockpile or shelf.</summary>
        private static Choice PileChoice(Pawn pawn)
        {
            Map map = pawn.Map;
            var zones = new ZoneSites(new ChoreScan(pawn), c => Stockpiles.CellOk(c, map), c => Stockpiles.CellScore(c, map));
            var site = zones.Find(new[] { 3, 6 }, 1).FirstOrDefault();
            if (site == null)
                return null;
            var rect = site.Rect(site.maxSize);
            string where = zones.Where(rect, site.steps);
            return new Choice
            {
                group = "Storage",
                label = $"a stockpile for everything ({rect.Width}x{rect.Height}, {where}): there's none yet, so nothing is stored or counted",
                pile = rect,
                fieldWhere = where,
            };
        }

        private const string None = "-";

        private static Dictionary<string, object> Schema(int count, List<string> sites, List<string> materials) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["reason"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 400 },
                ["choice"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = Enumerable.Range(0, count + 1).Cast<object>().ToList() },
                ["site"] = Enum(sites.Append(None).ToList()),
                ["material"] = Enum(materials.Append(None).ToList()),
                ["say"] = ActionCatalog.SaySchema(),
            },
            ["required"] = new List<object> { "reason", "choice", "site", "material", "say" },
            ["additionalProperties"] = false,
        };

        private static Dictionary<string, object> Enum(List<string> values) =>
            new Dictionary<string, object> { ["type"] = "string", ["enum"] = values.Cast<object>().ToList() };

        private static void OnReply(PawnMind mind, Dictionary<string, object> reply, List<Choice> choices, List<RoomPlan> sites, List<string> letters,
                                    List<ThingDef> materials)
        {
            Pawn pawn = mind.pawn;
            string Get(string key) => reply.TryGetValue(key, out object v) ? v as string : null;
            int pick = reply.TryGetValue("choice", out object c) && c is double d ? (int)d : -1;
            if (pick == 0)
            {
                RemarkAndLog(mind, Get("say"), "Looked over the base and let the stores build up for now.", "base: not now");
                return;
            }
            var choice = pick >= 1 && pick <= choices.Count ? choices[pick - 1] : null;
            if (choice == null)
            {
                ModLog.Warning($"{pawn.LabelShort}: base reply chose {pick}, which isn't on the list.");
                return;
            }
            string result;
            try
            {
                if (choice.kind != null)
                {
                    int siteIndex = letters.IndexOf(Get("site"));
                    result = PlaceRoom(pawn, choice, sites[siteIndex >= 0 ? siteIndex : 0], materials.Find(m => m.label == Get("material")) ?? materials[0]);
                }
                else if (choice.layout != null)
                    result = choice.layout(pawn, materials.Find(m => m.label == Get("material")) ?? materials[0]);
                else if (choice.crop != null)
                    result = Fields.Place(pawn, choice.field, choice.crop, choice.fieldWhere);
                else if (choice.upgrade != null)
                {
                    RemarkAndLog(mind, Get("say"), UpgradeCall.Start(mind, choice.upgrade), $"base: upgrade {Upgrades.RoomLine(choice.upgrade, mind.pawn)}");
                    return;
                }
                else if (choice.group == "Storage")
                    result = Stockpiles.Place(pawn, choice.pile, "everything", choice.fieldWhere);
                else
                    result = StockUp(mind, choice.chore);
            }
            catch (Exception e)
            {
                result = "That went wrong: " + e.Message;
                ModLog.Error($"{pawn.LabelShort}: applying \"{choice.label}\" threw: {e}");
            }
            RemarkAndLog(mind, Get("say"), result, $"base: {choice.label} [{Get("site")}, {Get("material")}]");
        }

        /// <summary>
        /// Places the room at code's size, picks whose bed it is, and marks what materials are missing. Another mind may have
        /// started the same room, or put blueprints on her site, while she was thinking: then she leaves it to them, or the
        /// sites are found again and the one nearest her pick is used.
        /// </summary>
        private static string PlaceRoom(Pawn pawn, Choice choice, RoomPlan picked, ThingDef material)
        {
            Map map = pawn.Map;
            var other = BuildManager.Instance.ActiveOn(map).FirstOrDefault(p => p.kindDef == choice.kind && p.pawn != pawn && !choice.kind.owned);
            if (other != null)
                return $"{other.pawn.LabelShort} already started a {choice.kind.label}, so I left it to them.";
            var finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
            var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
            var sites = finder.Sites(validator, material, out _);
            var site = sites.OrderBy(s => s.Interior.CenterCell.DistanceToSquared(picked.Interior.CenterCell)).FirstOrDefault();
            var plan = site != null ? finder.Fit(site, choice.kind, choice.size.w, choice.size.h, validator, material, out _) : null;
            var project = plan != null ? BuildManager.Instance.Place(pawn, plan, material, validator, finder.Where(plan)) : null;
            if (project == null)
                return $"Wanted a {choice.kind.label}, but it didn't fit anywhere near the base.";
            if (choice.kind.owned && pawn.ownership?.OwnedRoom != null)
            {
                // Hers if she has no room of her own; else for someone who has none (the ladder's private bedrooms); else free.
                var taken = new HashSet<Pawn>(BuildManager.Instance.ActiveOn(pawn.Map).Select(p => p.occupant).Where(p => p != null));
                project.occupant = Ladder.Sleepers(pawn.Map).FirstOrDefault(p => p != pawn && p.ownership?.OwnedRoom == null && !taken.Contains(p));
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
