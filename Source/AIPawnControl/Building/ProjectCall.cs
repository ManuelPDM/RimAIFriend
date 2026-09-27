using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The Project call (PHASE4.md §5): after she picks "plan a new room", code scans for sites, then asks her for a kind,
    /// a size, a site and a material from enforced enums, fits the room and places it. The call is free: picking the
    /// option was the decision.
    /// </summary>
    public static class ProjectCall
    {
        /// <summary>Runs the scan and sends the call. Returns the result line for her decisions.</summary>
        /// <param name="dev">The "Plan a room now" gizmo: the budget and cooldown don't apply.</param>
        public static string Start(PawnMind mind, bool dev = false)
        {
            Pawn pawn = mind.pawn;
            Map map = pawn.Map;
            var clock = Stopwatch.StartNew();
            var center = SiteFinder.BaseCenter(map);
            var finder = new SiteFinder(map, center);
            var materials = SiteFinder.Materials(map);
            var validator = new RoomValidator(map, center, finder.weights.maxWalk);
            var sites = finder.Sites(validator, materials[0], out _);
            if (sites.Count == 0)
            {
                BuildManager.Instance.EmptyScan(pawn);
                if (!dev)
                    mind.GiveBackAct();
                mind.memory.Record(pawn, "build", null, "I wanted to build a room, but there's no space for one near the base.", 3, MemoryEvent.TookPart);
                ModLog.Message($"{pawn.LabelShort}: no site for a room ({clock.ElapsedMilliseconds} ms); the option comes back in 2 days.");
                return "No room fits anywhere near the base.";
            }
            var fits = sites.Select(finder.MaxFit).ToList();
            var kinds = new List<(RoomKindDef kind, RoomPlan plan)>();
            foreach (var kind in DefDatabase<RoomKindDef>.AllDefsListForReading)
                if (kind.BuildableNow(map)
                    && finder.Fit(sites[0], kind, SiteFinder.StandardSize, SiteFinder.StandardSize, validator, materials[0], out _) is RoomPlan plan)
                    kinds.Add((kind, plan));
            ModLog.Message($"{pawn.LabelShort}: room scan took {clock.ElapsedMilliseconds} ms ({sites.Count} sites, {kinds.Count} kinds).");

            var letters = sites.Select((s, i) => ((char)('A' + i)).ToString()).ToList();
            // A bedroom can be for someone else with no bed of their own (PHASE6.md §5.2)
            var bedless = map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn && p.ownership != null && p.ownership.OwnedBed == null)
                .Select(p => p.LabelShort).ToList();
            var messages = PromptBuilder.Build("project", mind, new Dictionary<string, string>
            {
                ["forwhom"] = bedless.Count > 0
                    ? $"\"for\": who a bedroom is for: \"me\", or someone with no bed of their own ({string.Join(", ", bedless)}); the bed becomes theirs. For other kinds, \"me\"."
                    : "\"for\": \"me\".",
                ["kinds"] = string.Join("\n", kinds.Select(k => KindLine(k.kind, k.plan, materials))),
                ["sizes"] = "from 4x4 up to 7x7, rectangles too (e.g. 4x6, 5x7)",
                ["sites"] = string.Join("\n", sites.Select((s, i) => finder.Describe(s, letters[i][0], materials, fits[i]))),
                ["stock"] = SiteFinder.StockLine(map, materials),
            });
            var schema = Schema(kinds.Select(k => k.kind.label).ToList(), letters, materials.Select(m => m.label).ToList(), bedless);
            mind.Send("project", messages, schema, reply => OnReply(mind, reply, finder, validator, sites, letters, kinds.Select(k => k.kind).ToList(), materials),
                stillValid: () =>
                {
                    if (pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map)
                        return "gone";
                    return BuildManager.Instance?.ActiveProject(pawn) != null ? "a project is already running" : null;
                });
            return "Thinking about what to build and where.";
        }

        /// <summary>"- kitchen: a fueled stove. At 5×5: fueled stove; 80 steel."</summary>
        private static string KindLine(RoomKindDef kind, RoomPlan plan, List<ThingDef> materials)
        {
            var furniture = plan.Furniture.ToList();
            string line = $"- {kind.label}: {kind.description}";
            if (kind.minSize.x > 4 || kind.minSize.z > 4)
                line += $" (needs at least {kind.minSize.x}x{kind.minSize.z})";
            if (furniture.Count == 0)
                return line + ".";
            string items = string.Join(", ", furniture.GroupBy(e => e.def).Select(g => g.Count() > 1 ? $"{g.Count()} {Find.ActiveLanguageWorker.Pluralize(g.Key.label, g.Count())}" : g.Key.label));
            var cost = new RoomPlan { kind = kind, map = plan.map, footprint = plan.footprint };
            cost.entries.AddRange(furniture);
            return line + $". At {plan.SizeLabel.Replace('×', 'x')}: {items}; {SiteFinder.CostText(cost, materials)}.";
        }

        private static Dictionary<string, object> Schema(List<string> kinds, List<string> letters, List<string> materials, List<string> bedless) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["reason"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 400 },
                ["kind"] = Enum(kinds),
                ["size"] = Enum(SiteFinder.Shapes.Select(s => $"{s.w}x{s.h}").ToList()),
                ["site"] = Enum(letters),
                ["material"] = Enum(materials),
                ["for"] = Enum(new[] { "me" }.Concat(bedless).ToList()),
                ["say"] = ActionCatalog.SaySchema(),
            },
            ["required"] = new List<object> { "reason", "kind", "size", "site", "material", "for", "say" },
            ["additionalProperties"] = false,
        };

        private static Dictionary<string, object> Enum(List<string> values) =>
            new Dictionary<string, object> { ["type"] = "string", ["enum"] = values.Cast<object>().ToList() };

        private static void OnReply(PawnMind mind, Dictionary<string, object> reply, SiteFinder finder, RoomValidator validator, List<RoomPlan> sites,
                                    List<string> letters, List<RoomKindDef> kinds, List<ThingDef> materials)
        {
            Pawn pawn = mind.pawn;
            string Get(string key) => reply.TryGetValue(key, out object v) ? v as string : null;
            var kind = kinds.Find(k => k.label == Get("kind"));
            int siteIndex = letters.IndexOf(Get("site"));
            var material = materials.Find(m => m.label == Get("material")) ?? materials[0];
            var size = (Get("size") ?? "5x5").Split('x');
            if (kind == null || siteIndex < 0 || size.Length != 2 || !int.TryParse(size[0], out int width) || !int.TryParse(size[1], out int height))
            {
                mind.AddDecision("Couldn't make up my mind about the room.");
                ModLog.Warning($"{pawn.LabelShort}: project reply didn't match the options: {Json.Write(reply)}");
                return;
            }
            var plan = finder.Fit(sites[siteIndex], kind, width, height, validator, material, out string note);
            var project = plan != null ? BuildManager.Instance.Place(pawn, plan, material, validator, finder.Where(plan)) : null;
            if (project == null)
            {
                mind.AddDecision($"Wanted a {kind.label} at site {letters[siteIndex]}, but it didn't fit there.");
                return;
            }
            if (kind.owned && Get("for") is string forName && forName != "me")
                project.occupant = pawn.Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.LabelShort == forName && p.ownership?.OwnedBed == null);
            string say = SpeechLog.Clean(Get("say"));
            string result = $"Laid out a {project.KindFor} ({plan.SizeLabel}, {material.label}) {project.where}" + (note != null ? $"; {note}." : ".");
            if (say != null && AIPawnControlMod.Settings.speakLines)
            {
                SpeechLog.Say(pawn, say);
                result += $" Said: \"{say}\"";
            }
            mind.AddDecision(result, importance: 0); // BuildManager.Place recorded the event
            ModLog.Message($"{pawn.LabelShort} project: {project.KindFor} {width}x{height} at {letters[siteIndex]} in {material.label} | {result} | Reason: {mind.lastReason}");
        }
    }
}
