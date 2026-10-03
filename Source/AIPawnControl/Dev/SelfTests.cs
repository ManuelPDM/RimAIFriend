using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// In-game unit tests (CLEANUP.md §4.2): pass or fail in seconds, no LLM, written to self-tests.txt. Read-only tests
    /// run on any map. The ones marked "builds" change the map (god-mode building), so run them on a fresh quicktest map
    /// or a fixture. Each test is also its own debug action, so RimBridge can run one by path.
    /// </summary>
    public static class SelfTests
    {
        private class Test
        {
            public string name;
            public bool builds;
            public Action<Log> run;
        }

        /// <summary>One test's findings: detail lines for the report, and failures.</summary>
        private class Log
        {
            public readonly List<string> lines = new List<string>();
            public readonly List<string> failures = new List<string>();
            public void Line(string text) => lines.Add(text);
            public void Fail(string text) => failures.Add(text);
            public void Check(bool ok, string failure)
            {
                if (!ok)
                    Fail(failure);
            }
        }

        private static readonly List<Test> Tests = new List<Test>
        {
            new Test { name = "Helpers", run = Helpers },
            new Test { name = "Prompts", run = PromptsBuild },
            new Test { name = "Base call menu", run = BaseCallMenu },
            new Test { name = "Sites", run = Sites },
            new Test { name = "Chores", run = Chores },
            new Test { name = "Memory", run = Memory },
            new Test { name = "Hygiene", run = HygieneFixtures },
            new Test { name = "Customs", run = CustomsAndGatherings },
            new Test { name = "Every kind builds", builds = true, run = EveryKindBuilds },
            new Test { name = "Materials get marked", builds = true, run = MaterialsGetMarked },
            new Test { name = "Upgrades", builds = true, run = UpgradesMatchVanilla },
            new Test { name = "Layout", builds = true, run = LayoutLines },
            new Test { name = "Danger response", builds = true, run = DangerOptions },
        };

        [DebugAction(DevTools.Category, "Run self-tests", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> RunSelfTests()
        {
            var nodes = new List<DebugActionNode>
            {
                new DebugActionNode("all read-only", DebugActionType.Action, () => Run(Tests.Where(t => !t.builds))),
                new DebugActionNode("all (builds on this map)", DebugActionType.Action, () => Run(Tests)),
            };
            nodes.AddRange(Tests.Select(t => new DebugActionNode(t.name, DebugActionType.Action, () => Run(new[] { t }))));
            return nodes;
        }

        /// <summary>Runs the tests in order, writes self-tests.txt, and says how many passed.</summary>
        private static string Run(IEnumerable<Test> tests)
        {
            var report = new StringBuilder($"Self-tests, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, map {Find.CurrentMap.Size.x}x{Find.CurrentMap.Size.z}\n");
            int passed = 0;
            var failed = new List<string>();
            foreach (var test in tests)
            {
                var log = new Log();
                var clock = Stopwatch.StartNew();
                try
                {
                    test.run(log);
                }
                catch (Exception e)
                {
                    log.Fail("threw: " + e);
                }
                bool ok = log.failures.Count == 0;
                report.AppendLine($"\n== {test.name}: {(ok ? "PASS" : "FAIL")} ({clock.ElapsedMilliseconds} ms)");
                foreach (var f in log.failures)
                    report.AppendLine("  FAIL " + f);
                foreach (var l in log.lines)
                    report.AppendLine("  " + l);
                if (ok)
                    passed++;
                else
                    failed.Add(test.name);
            }
            DevTools.WriteFile("self-tests.txt", report.ToString());
            string summary = $"Self-tests: {passed} passed" + (failed.Count > 0 ? $", {failed.Count} failed: {string.Join(", ", failed)}" : "") + ".";
            ModLog.Message($"{summary} Report: {Path.Combine(DevTools.Folder, "self-tests.txt")}");
            DevTools.Report(summary);
            return summary;
        }

        private static Map Map => Find.CurrentMap;

        private static List<Pawn> Colonists => Map.mapPawns.FreeColonistsSpawned.ToList();

        // ---------- The tests ----------

        /// <summary>The shared helpers agree with the slow way: area sums with counting, the flood with Manhattan distance, a path's ends.</summary>
        private static void Helpers(Log log)
        {
            Map map = Map;
            int w = map.Size.x;
            var open = new bool[w * map.Size.z];
            for (int i = 0; i < open.Length; i++)
                open[i] = Ground.Open(new IntVec3(i % w, 0, i / w), map);
            var sums = new AreaSums(map, open);
            var rand = new Random(7);
            for (int n = 0; n < 200; n++)
            {
                int x = rand.Next(w - 20), z = rand.Next(map.Size.z - 20);
                var rect = new CellRect(x, z, 1 + rand.Next(19), 1 + rand.Next(19));
                int slow = rect.Cells.Count(c => open[c.z * w + c.x]);
                log.Check(sums.Sum(rect) == slow, $"AreaSums over {rect}: {sums.Sum(rect)}, counted {slow}");
            }
            var area = new CellRect(0, 0, 9, 7);
            var flood = Flood.Run(area, new[] { IntVec3.Zero }, c => true);
            log.Check(area.Cells.All(c => flood[c] == c.x + c.z), "Flood over an open area isn't the Manhattan distance");
            log.Check(flood.Reached.Count == area.Area, $"Flood reached {flood.Reached.Count} of {area.Area} cells");
            var path = Flood.Path(area, new IntVec3(1, 0, 1), new IntVec3(7, 0, 5), c => true);
            log.Check(path.Count == 11 && path[0] == new IntVec3(1, 0, 1) && path[path.Count - 1] == new IntVec3(7, 0, 5), $"Flood.Path has {path.Count} cells");
            log.Check(GameTime.Day(Find.TickManager.TicksGame, map) == GameTime.Today(map), "GameTime.Day(now) isn't Today");
            log.Line($"200 area sums, a flood and a path checked; today is local day {GameTime.Today(map)}, night {GameTime.Night(map)}.");
        }

        private static readonly Regex Placeholder = new Regex(@"\{[A-Za-z]+\}");

        /// <summary>
        /// Every call's prompt builds for every colonist (with her mind, or a new one): no exception, no {placeholder} left
        /// unfilled, and every schema writes as JSON and reads back.
        /// </summary>
        private static void PromptsBuild(Log log)
        {
            foreach (var pawn in Colonists)
            {
                var mind = DevTools.MindOrStandIn(pawn);
                void Check(string call, List<KeyValuePair<string, string>> messages, Dictionary<string, object> schema)
                {
                    if (messages == null)
                    {
                        log.Line($"{pawn.LabelShort} {call}: nothing to ask now");
                        return;
                    }
                    foreach (var m in messages)
                        if (Placeholder.Match(m.Value) is Match match && match.Success)
                            log.Fail($"{pawn.LabelShort} {call}: {m.Key} prompt has {match.Value} unfilled");
                    if (schema != null)
                        log.Check(Json.Parse(Json.Write(schema)) is Dictionary<string, object>, $"{pawn.LabelShort} {call}: the schema doesn't read back");
                    log.Line($"{pawn.LabelShort} {call}: {string.Join(" + ", messages.Select(m => $"{m.Key} {m.Value.Length}"))} chars");
                }
                void Try(string call, Action build)
                {
                    try
                    {
                        build();
                    }
                    catch (Exception e)
                    {
                        log.Fail($"{pawn.LabelShort} {call} threw: {e}");
                    }
                }
                Try("persona", () => Check("persona", mind.PersonaMessages(), ActionCatalog.PersonaSchema()));
                Try("act", () =>
                {
                    var menu = ActionCatalog.BuildActMenu(pawn, mind);
                    var recall = Recall.Act(mind, Recall.Present(pawn), null, null, peek: true);
                    var schema = ActionCatalog.ActSchema(menu, recall.Shown);
                    var ids = (List<object>)((Dictionary<string, object>)((Dictionary<string, object>)schema["properties"])["choice"])["enum"];
                    log.Check(menu.All(o => ids.Contains(o.Id)), $"{pawn.LabelShort} act: a menu id is missing from the schema");
                    Check("act", mind.ActMessages("(self-test)", menu, recall), schema);
                });
                Try("chat", () =>
                {
                    var menu = ActionCatalog.BuildActMenu(pawn, mind, inConversation: true);
                    var recall = Recall.Conversation(mind, PersonFile.Player, null, null, 5, peek: true);
                    Check("chat", mind.ChatMessages("The player is talking to you.", new List<string>(), "How are you?", ActionCatalog.DescribeMenu(menu), recall),
                        ActionCatalog.ChatSchema(menu, recall.Shown));
                });
                if (Colonists.FirstOrDefault(p => p != pawn) is Pawn other)
                    Try("reply", () =>
                    {
                        var recall = Recall.Conversation(mind, other.LabelShort, null, null, 3, peek: true);
                        Check("reply", mind.ReplyMessages(other.LabelShort, "Nice weather today.", "chitchat", "(just answer)", recall), null);
                    });
                Try("base", () =>
                {
                    var call = BaseCall.Prepare(mind, dev: true);
                    Check("base", call.messages, call.schema);
                });
                Try("post", () => Check("post", GroupChat.PostMessages(mind), GroupChat.PostSchema()));
                Try("upgrade", () =>
                {
                    var room = Upgrades.Rooms(pawn).FirstOrDefault(r => Upgrades.For(r, pawn).Count > 0);
                    var call = room != null ? UpgradeCall.Prepare(mind, room) : null;
                    Check("upgrade", call?.messages, call?.schema);
                });
                Try("reflect", () =>
                {
                    var reflection = Reflection.Prepare(mind, dev: true);
                    if (reflection != null)
                        reflection.PickClosest(null, null);
                    Check("reflect", reflection != null ? PromptBuilder.Build("reflect", mind, reflection.PromptValues()) : null, reflection?.ReplySchema());
                });
            }
        }

        /// <summary>The Base call's choices build for every colonist; on a colony with no rooms yet there's at least one.</summary>
        private static void BaseCallMenu(Log log)
        {
            foreach (var pawn in Colonists)
            {
                var clock = Stopwatch.StartNew();
                var call = BaseCall.Prepare(DevTools.MindOrStandIn(pawn), dev: true);
                log.Line($"{pawn.LabelShort}: {call.Count} choices in {clock.ElapsedMilliseconds} ms");
                foreach (var label in call.Labels)
                    log.Line("  " + label);
                // The site letters as the mind reads them (one per style, BASE_GROWTH.md §6.4).
                if (call.messages != null)
                    foreach (var line in call.messages.Last().Value.Split('\n').Where(l => Regex.IsMatch(l, "^[A-C]: ")))
                        log.Line("  site " + line.Trim());
                if (Ladder.Current(Map)?.kind != null && Ladder.Current(Map).waiting == null)
                    log.Check(call.Count > 0, $"{pawn.LabelShort}: the ladder's next room can be built, but the Base call offers nothing");
            }
        }

        /// <summary>Sites are found, every buildable room kind fits at one (Fit only returns validated plans), and the hub search runs.</summary>
        private static void Sites(Log log)
        {
            Map map = Map;
            Pawn pawn = Colonists.FirstOrDefault();
            var clock = Stopwatch.StartNew();
            var finder = new SiteFinder(map, SiteFinder.BaseCenter(map));
            var validator = new RoomValidator(map, finder.center, finder.weights.maxWalk);
            ThingDef material = Supplies.WallMaterials(pawn)[0].stuff;
            var sites = finder.Sites(validator, material, out var candidates);
            log.Line($"{sites.Count} sites from {candidates.Count} candidates in {clock.ElapsedMilliseconds} ms");
            log.Check(sites.Count > 0, "no site for a room near the base");
            log.Line(finder.Rejections(5, 5));
            foreach (var kind in DefDatabase<RoomKindDef>.AllDefsListForReading.Where(k => !k.layout && k.askedFor == null)) // asked-for kinds: Every kind builds
            {
                if (!kind.BuildableNow(map))
                {
                    log.Line($"{kind.label}: not buildable now ({Ladder.Waiting(kind, map)})");
                    continue;
                }
                var size = BaseCall.SizeFor(kind, map);
                var plan = sites.Select(s => finder.Fit(s, kind, size.w, size.h, validator, material, out _)).FirstOrDefault(p => p != null);
                if (plan == null)
                {
                    log.Fail($"{kind.label} fits at none of the {sites.Count} sites");
                    continue;
                }
                log.Line($"{kind.label}: {plan.SizeLabel}, {plan.Furniture.Count()} items, {finder.Where(plan)}");
            }
            clock.Restart();
            var hubs = finder.Hubs(RoomKindDef.Hall, validator, material, 3);
            log.Line($"hub search: {hubs.Count} halls in {clock.ElapsedMilliseconds} ms ({Layout.Line(map)})");
        }

        /// <summary>Every stock-up option passes its own check, and fields and stockpiles find a site.</summary>
        private static void Chores(Log log)
        {
            foreach (var pawn in Colonists)
            {
                var all = ChoreOptions.All(pawn, ignoreLimits: true);
                log.Line($"{pawn.LabelShort}: {all.Count} options");
                foreach (var o in all)
                {
                    string check = o.check();
                    log.Check(check == null, $"{pawn.LabelShort} [{o.kind}] {o.label}: {check}");
                    log.Line($"  [{o.kind}] {o.useful:0.0} {o.label}");
                }
            }
            Pawn first = Colonists.FirstOrDefault();
            log.Line("field site: " + (Fields.FindSite(first, 6, out CellRect field, out string where, out string ground) ? $"{field} {ground}, {where}" : "none"));
            log.Line("stockpile site: " + (Stockpiles.FindSite(first, out CellRect pile, out string pileWhere) ? $"{pile}, {pileWhere}" : "none"));
        }

        /// <summary>
        /// Recall runs for every mind with no vector (people, place, importance and recency), and a peek (the dev tools and
        /// the other self-tests) leaves her memories as they were: nothing marked shown, [On my mind]'s 4 hours not restarted.
        /// </summary>
        private static void Memory(Log log)
        {
            var minds = MindManager.Instance?.Minds ?? new List<PawnMind>();
            if (minds.Count == 0)
                log.Line("no minds: nothing to check");
            foreach (var mind in minds)
            {
                var memory = mind.memory;
                var shownBefore = memory.memories.Select(m => m.lastShownTick).ToList();
                int onMindBefore = memory.lastOnMindTick;
                var act = Recall.Act(mind, Recall.Present(mind.pawn), null, null, peek: true);
                var chat = Recall.Conversation(mind, PersonFile.Player, null, null, 5, peek: true);
                log.Check(memory.lastOnMindTick == onMindBefore && memory.memories.Select(m => m.lastShownTick).SequenceEqual(shownBefore),
                    $"{mind.pawn.LabelShort}: a peek changed her memories");
                log.Line($"{mind.pawn.LabelShort}: {memory.memories.Count} memories, {memory.diary.Count} diary entries; without a vector: "
                         + $"[On my mind] {act.Shown.Count}, [I remember] {chat.Shown.Count}");
            }
        }

        /// <summary>
        /// With Dubs Bad Hygiene (HYGIENE.md): each fixture on its build tab, whether it can be built and runs here (the plumbing
        /// gate), and what the bathroom resolves to. The bathroom's toilets always resolve (a latrine needs nothing).
        /// </summary>
        /// <summary>
        /// [Colony customs] is at most 4 lines and never names the ideoligion as hers; gathering lines build without throwing,
        /// have unique keys and respect the caps; reform options swap within one issue. Read-only (IDEOLOGY.md).
        /// </summary>
        private static void CustomsAndGatherings(Log log)
        {
            log.Line($"Ideology active: {Customs.Active}" + (Customs.Active ? $", fluid: {Customs.Primary.Fluid}, can reform: {Customs.CanReform}" : ""));
            foreach (var pawn in Colonists)
            {
                string section = Customs.Section(pawn);
                log.Check((section != null) == Customs.Active, $"{pawn.LabelShort}: [Colony customs] is {(section == null ? "missing" : "there")} with Ideology {(Customs.Active ? "on" : "off")}");
                if (section != null)
                {
                    int lines = section.Split('\n').Length;
                    log.Check(lines <= 4, $"{pawn.LabelShort}: [Colony customs] has {lines} lines");
                    log.Check(!section.Contains(Customs.Primary.name), $"{pawn.LabelShort}: [Colony customs] names the ideoligion");
                    log.Line($"{pawn.LabelShort}: {section.Replace("\n", " | ")}");
                }
                var gatherings = Gatherings.Lines(pawn);
                log.Check(gatherings.Select(l => l.key).Distinct().Count() == gatherings.Count, $"{pawn.LabelShort}: two gathering lines share a key");
                log.Check(gatherings.Count(l => l.faith) <= 1, $"{pawn.LabelShort}: more than one ideoligion line");
                foreach (var line in gatherings)
                    log.Line($"{pawn.LabelShort} line: {line.label}");
            }
            if (Customs.Active && Find.AnyPlayerHomeMap is Map home)
                foreach (var custom in Customs.Changeable())
                    foreach (var to in Customs.Alternatives(custom))
                        log.Check(to.issue == custom.def.issue && to != custom.def, $"reform value {to.defName} isn't a swap within {custom.def.issue.defName}");
            log.Line(DevTools.CustomsText(Map).Split('\n').Length + " lines in the customs report");
        }

        private static void HygieneFixtures(Log log)
        {
            Map map = Map;
            var bathroom = DefDatabase<RoomKindDef>.GetNamedSilentFail("AIPC_Bathroom");
            if (bathroom == null)
            {
                log.Line("no Dubs Bad Hygiene: no bathroom kind");
                return;
            }
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.designationCategory?.defName == "Hygiene" && d.BuildableByPlayer))
                log.Line($"{def.defName}: buildable {RoomKindDef.Buildable(def, map)}, runs {Needs.CanRun(def, map)}, privacy {Hygiene.NeedsPrivacy(def)}");
            foreach (var item in bathroom.items)
                log.Line($"bathroom item ({string.Join("/", item.defs.Select(d => d.defName))}): {item.Resolve(map)?.defName ?? "none"}");
            log.Check(bathroom.items[0].Resolve(map) != null, "the bathroom's toilets resolve to nothing");
        }

        /// <summary>Builds: every buildable kind laid out for the first colonist and finished at once reaches Done, with vanilla's own role.</summary>
        private static void EveryKindBuilds(Log log)
        {
            Map map = Map;
            Pawn pawn = Colonists.FirstOrDefault();
            // Rooms the game asks for are built through what it asks for (their items come from there), the rest as they are.
            var asks = AskedFor.Current(map);
            foreach (var d in AskedFor.Dropped)
                log.Line("not asked for now: " + d);
            foreach (var kind in DefDatabase<RoomKindDef>.AllDefsListForReading.Where(k => !k.layout && (k.askedFor == null ? k.BuildableNow(map) : asks.Any(a => a.kind == k))))
            {
                var ask = asks.FirstOrDefault(a => a.kind == kind);
                string result = ask != null
                    ? BaseCall.PlaceRoom(pawn, kind, AskedFor.SizeFor(ask, map), null, Supplies.WallMaterials(pawn)[0].stuff, out BuildProject project, ask)
                    : DevTools.PlaceRoom(pawn, kind, out project);
                if (project == null)
                {
                    log.Fail($"{kind.label}: {result}");
                    continue;
                }
                DevTools.FinishInstantly(project);
                Room room = project.Room;
                string what = $"{kind.label}: {project.state}, room {(room == null ? "none" : $"{room.Role?.label ?? "no role"}, proper {room.ProperRoom}, {room.CellCount} cells")}";
                log.Check(project.state == BuildProject.State.Done, what + " | " + project.StatusLine());
                log.Line(what);
            }
        }

        /// <summary>Builds: a room laid out while storage falls short marks what's missing (trees, a vein, an order) or says why it can't.</summary>
        private static void MaterialsGetMarked(Log log)
        {
            Pawn pawn = Colonists.FirstOrDefault();
            var kind = DefDatabase<RoomKindDef>.GetNamed("AIPC_Barracks");
            // The wall material with the least in storage, so a shortfall is likely.
            ThingDef material = Supplies.WallMaterials(pawn).OrderBy(m => m.stock).First().stuff;
            string result = BaseCall.PlaceRoom(pawn, kind, (6, 6), null, material, out BuildProject project);
            if (project == null)
            {
                log.Fail("the barracks didn't fit: " + result);
                return;
            }
            var missing = project.Missing();
            log.Line($"barracks in {material.label}: missing {(missing.Count > 0 ? string.Join(", ", missing.Select(kv => $"{kv.Value} {kv.Key.label}")) : "nothing")}");
            log.Line("result: " + result);
            if (missing.Count > 0)
                log.Check(result.Contains("Marked") || result.Contains("Ordered") || result.Contains("Raised") || result.Contains("short"),
                    "materials are missing, but nothing was marked and no reason was given");
            DevTools.Cancel(project);
        }

        /// <summary>Builds (it spawns each upgrade for a moment): a looks upgrade's predicted impressiveness is within 1 of vanilla's real number.</summary>
        private static void UpgradesMatchVanilla(Log log)
        {
            Pawn pawn = Colonists.FirstOrDefault();
            var rooms = Upgrades.Rooms(pawn);
            if (rooms.Count == 0)
                log.Line("no rooms to upgrade (run after \"Every kind builds\")");
            foreach (var room in rooms)
                foreach (var u in Upgrades.For(room, pawn))
                {
                    if (u.gain == Upgrades.Gain.Looks)
                    {
                        float real = Upgrades.TryForReal(room, u);
                        log.Check(Math.Abs(real - u.predicted) <= 1f, $"{u.label}: predicted impressiveness {u.predicted:0.0}, vanilla {real:0.0}");
                    }
                    log.Line($"{Upgrades.RoomLine(room, pawn)}: [{u.gain}] {u.label}");
                }
        }

        /// <summary>People must always have a way out (the user's rule): walking from the room's doors, through anything, reaches the outdoors.</summary>
        private static bool ReachesOutdoors(Room room)
        {
            Map map = room.Map;
            bool found = false;
            Flood.Run(Flood.All(map), Layout.Doors(room).Select(d => d.Position), c => c.Walkable(map),
                stop: c => found = Ground.OpenAir(c.GetRoom(map)));
            return found;
        }

        /// <summary>
        /// Builds: every layout line, built at once, lowers the ways out by what it promised (a hub) or leaves them (a
        /// doorway), and every room of the base still has a way out.
        /// </summary>
        private static void LayoutLines(Log log)
        {
            Map map = Map;
            for (int i = 0; i < 10; i++)
            {
                int before = Layout.WaysOut(map).Count;
                Pawn pawn = Colonists.FirstOrDefault();
                var line = DevTools.LayoutLines(pawn).FirstOrDefault();
                if (line == null)
                    break;
                string result = DevTools.PlaceLine(pawn, line, out BuildProject project);
                if (project == null)
                {
                    log.Fail($"{line.label}: nothing placed ({result})");
                    break;
                }
                DevTools.FinishInstantly(project);
                int after = Layout.WaysOut(map).Count;
                log.Line($"{line.label} → {project.state}, ways out {before} → {after}");
                foreach (var room in map.regionGrid.AllRooms.Where(Layout.OfBase))
                    log.Check(ReachesOutdoors(room), $"after {line.label}: the {Layout.Name(room)} at {room.ExtentsClose} has no way out");
                log.Check(project.state == BuildProject.State.Done, $"{line.label}: {project.StatusLine()}");
                if (project.kindDef?.defName == "AIPC_ClosedDoor")
                    log.Check(after == before - 1, $"closing a way out left {after} of {before}");
                else if (project.kindDef?.defName != "AIPC_Doorway")
                {
                    int promised = int.Parse(Regex.Match(line.label, @"(\d+) fewer way").Groups[1].Value);
                    log.Check(after == before - promised, $"{line.label}: promised {promised} fewer ways out, got {before} → {after}");
                }
            }
            log.Line($"now {Layout.Line(map)}");
        }

        /// <summary>
        /// Changes the map (spawns an enemy for a moment): with an enemy near the first colonist the menu is the danger menu,
        /// "go inside" sets the Inside area (every indoor room of the base) and Flee, "fight" clears the area and sets Attack,
        /// and once the enemy is gone her own area and response come back.
        /// </summary>
        private static void DangerOptions(Log log)
        {
            Map map = Map;
            Pawn pawn = Colonists.FirstOrDefault();
            var faction = Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
            if (pawn == null || faction == null)
            {
                log.Line("no colonist or no enemy faction: nothing to check");
                return;
            }
            log.Check(DangerResponse.Threats(map).Count == 0, "there's danger before the test: its checks would mislead");
            if (!CellFinder.TryFindRandomCellNear(pawn.Position, map, 15, c => c.Standable(map) && !c.Fogged(map) && c.DistanceTo(pawn.Position) > 8, out IntVec3 cell))
            {
                log.Fail("no cell to spawn the enemy on");
                return;
            }
            string insideLabel = "AIPawnControl_InsideArea".Translate();
            bool hadInside = map.areaManager.AllAreas.Any(a => a.Label == insideLabel);
            var areaBefore = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
            var responseBefore = pawn.playerSettings.hostilityResponse;
            var mind = new PawnMind(pawn, null); // not registered: no calls, just its settings
            Pawn enemy = PawnGenerator.GeneratePawn(new PawnGenerationRequest(faction.RandomPawnKind(), faction));
            GenSpawn.Spawn(enemy, cell, map);
            try
            {
                log.Check(DangerResponse.Threats(map).Contains(enemy), $"the {enemy.KindLabel} isn't a threat");
                log.Line("[Danger] " + DangerResponse.Line(pawn));
                var menu = ActionCatalog.BuildActMenu(pawn, mind);
                log.Line("menu: " + string.Join(" | ", menu.Select(o => o.Label)));
                log.Check(menu.All(o => o.Key == "keep" || o.Key == "inside" || o.Key == "fight" || o.Key.StartsWith("help ")), "the danger menu offers more than the danger options");
                bool canFight = !pawn.WorkTagIsDisabled(WorkTags.Violent);
                log.Check(menu.Any(o => o.Key == "fight") == canFight, $"fight offered: {menu.Any(o => o.Key == "fight")}, can fight: {canFight}");
                var inside = menu.FirstOrDefault(o => o.Key == "inside");
                log.Check((inside != null) == DangerResponse.CanGoInside(map), "\"go inside\" offered when it can't be, or missing when it can");
                if (inside != null)
                {
                    log.Line("go inside: " + inside.Apply(null));
                    var area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
                    log.Check(area != null && area.TrueCount == DangerResponse.InsideCells(map).Count, $"her area is {area?.Label ?? "none"}, not the whole inside");
                    log.Check(pawn.playerSettings.hostilityResponse == HostilityResponseMode.Flee, "\"go inside\" didn't set Flee");
                }
                var fight = menu.FirstOrDefault(o => o.Key == "fight");
                if (fight != null)
                {
                    log.Line("fight: " + fight.Apply(null));
                    log.Check(pawn.playerSettings.AreaRestrictionInPawnCurrentMap == null, "\"fight\" left an area on her");
                    log.Check(pawn.playerSettings.hostilityResponse == HostilityResponseMode.Attack, "\"fight\" didn't set Attack");
                }
            }
            finally
            {
                enemy.Destroy();
                pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
            }
            log.Check(DangerResponse.Threats(map).Count == 0, "still danger after the enemy is gone");
            mind.EndDanger();
            log.Check(pawn.playerSettings.AreaRestrictionInPawnCurrentMap == areaBefore && pawn.playerSettings.hostilityResponse == responseBefore,
                "her own area and hostility response didn't come back");
            if (!hadInside)
                map.areaManager.AllAreas.FirstOrDefault(a => a.Label == insideLabel)?.Delete();
        }
    }
}
