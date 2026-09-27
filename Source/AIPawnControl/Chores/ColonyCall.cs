using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The Colony call (PHASE5.md §5), the building pattern for chores: after she picks "work on the colony" in the Act menu
    /// (or in Chat), code lists every chore it can set up right now, with amounts, crops and sites as enforced enums; she
    /// picks one and makes one remark; code applies it. The call is free: picking the menu line was the decision.
    /// </summary>
    public static class ColonyCall
    {
        /// <summary>Builds the options and sends the call. Returns the result line for her decisions.</summary>
        /// <param name="dev">"Colony call now": cooldowns and caps don't apply, and the decision isn't given back.</param>
        public static string Start(PawnMind mind, bool dev = false)
        {
            Pawn pawn = mind.pawn;
            Map map = pawn.Map;
            var clock = Stopwatch.StartNew();
            var options = ChoreOptions.All(pawn, ignoreLimits: dev);

            // Zone site scans only run here, and only when their option is on the list.
            List<ThingDef> crops = options.Any(o => o.needs == ChoreNeeds.Field || o.needs == ChoreNeeds.Crop) ? Fields.Crops(map) : new List<ThingDef>();
            ZoneSites fieldFinder = null, pileFinder = null;
            var fieldSites = new List<ZoneSites.Site>();
            var pileSites = new List<ZoneSites.Site>();
            var scan = new ChoreScan(pawn);
            if (options.Any(o => o.needs == ChoreNeeds.Field))
            {
                var foci = SiteFinder.NoBuildFoci(map);
                fieldFinder = new ZoneSites(scan, c => Fields.CellOk(c, map, foci), c => Fields.CellScore(c, map));
                fieldSites = fieldFinder.Find(Fields.Sizes);
            }
            if (options.Any(o => o.needs == ChoreNeeds.Stockpile))
            {
                pileFinder = new ZoneSites(scan, c => Stockpiles.CellOk(c, map), c => Stockpiles.CellScore(c, map));
                pileSites = pileFinder.Find(Stockpiles.Sizes);
                for (int i = 0; i < pileSites.Count; i++)
                    pileSites[i].letter = (char)('A' + fieldSites.Count + i); // field sites A-C, stockpile sites after them
            }
            options.RemoveAll(o => (o.needs == ChoreNeeds.Field && (fieldSites.Count == 0 || crops.Count == 0))
                                   || (o.needs == ChoreNeeds.Crop && crops.Count == 0)
                                   || (o.needs == ChoreNeeds.Stockpile && pileSites.Count == 0));
            options.AddRange(ChoreOptions.Stops(pawn));
            if (options.Count == 0)
            {
                if (!dev)
                    mind.GiveBackAct();
                return "There's no colony work I can set up right now.";
            }

            var extras = new List<string>();
            if (crops.Count > 0)
                extras.Add("Crops that grow here now:\n" + string.Join("\n", crops.Select(Fields.CropLine)));
            if (fieldSites.Count > 0)
                extras.Add($"Field sites (sizes: {SizeWords(Fields.Sizes)}):\n" + string.Join("\n", fieldSites.Select(s => DescribeField(fieldFinder, s, map))));
            if (pileSites.Count > 0)
                extras.Add($"Stockpile sites (sizes: {SizeWords(Stockpiles.Sizes)}):\n" + string.Join("\n", pileSites.Select(s => DescribePile(pileFinder, s, map)))
                           + $"\nA stockpile can hold: {string.Join(", ", Stockpiles.Kinds)}.");
            var messages = PromptBuilder.Build("colony", mind, new Dictionary<string, string>
            {
                ["options"] = string.Join("\n", options.Select((o, i) => $"{i + 1}: {Line(o)}")),
                ["extras"] = extras.Count > 0 ? string.Join("\n\n", extras) : "",
            });
            var letters = fieldSites.Concat(pileSites).Select(s => s.letter.ToString()).ToList();
            var schema = Schema(options.Count, crops.Select(c => c.label).ToList(), letters);
            ModLog.Message($"{pawn.LabelShort}: colony call with {options.Count} options ({clock.ElapsedMilliseconds} ms to build).");
            mind.Send("colony", messages, schema, reply => OnReply(mind, reply, options, crops, fieldSites, fieldFinder, pileSites, pileFinder),
                stillValid: () => pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map ? "gone" : null);
            return "Thinking about what the colony needs.";
        }

        /// <summary>"cut trees for wood (near the base). small: 10 trees, about 200 wood · medium: …", with a hint for options that need more.</summary>
        private static string Line(ChoreOption o)
        {
            switch (o.needs)
            {
                case ChoreNeeds.Amount: return $"{o.label}. {o.AmountText()}";
                case ChoreNeeds.Field: return $"{o.label} (pick a crop, a size and a field site below)";
                case ChoreNeeds.Stockpile: return $"{o.label} (pick what it holds, a size and a stockpile site below)";
                case ChoreNeeds.Crop: return $"{o.label} (pick the new crop below)";
                default: return o.label;
            }
        }

        private static string SizeWords(int[] sizes) => string.Join(", ", sizes.Select((s, i) => $"{ChoreOptions.Sizes[i]} {s}x{s}"));

        /// <summary>"A: rich soil (fertility 140%), next to the kitchen, near the base, room for up to large."</summary>
        private static string DescribeField(ZoneSites finder, ZoneSites.Site s, Map map)
        {
            var rect = s.Rect(s.maxSize);
            float fertility = rect.Cells.Average(c => c.GetTerrain(map).fertility);
            return $"{s.letter}: {finder.Ground(rect)} (fertility {fertility.ToStringPercent()}), {finder.Where(rect, s.steps)}, room for up to {Fields.SizeName(s.maxSize)}.";
        }

        /// <summary>"D: under a roof, near the base, room for up to medium."</summary>
        private static string DescribePile(ZoneSites finder, ZoneSites.Site s, Map map)
        {
            var rect = s.Rect(s.maxSize);
            string roof = rect.Cells.All(c => c.Roofed(map)) ? "under a roof" : rect.Cells.Any(c => c.Roofed(map)) ? "partly roofed" : "in the open";
            return $"{s.letter}: {roof}, {finder.Where(rect, s.steps)}, room for up to {Stockpiles.SizeName(s.maxSize)}.";
        }

        private const string None = "-";

        private static Dictionary<string, object> Schema(int count, List<string> crops, List<string> sites) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["reason"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 400 },
                ["choice"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = Enumerable.Range(0, count + 1).Cast<object>().ToList() },
                ["size"] = Enum(ChoreOptions.Sizes.ToList()),
                ["crop"] = Enum(crops.Append(None).ToList()),
                ["site"] = Enum(sites.Append(None).ToList()),
                ["holds"] = Enum(Stockpiles.Kinds.Append(None).ToList()),
                ["say"] = ActionCatalog.SaySchema(),
            },
            ["required"] = new List<object> { "reason", "choice", "size", "crop", "site", "holds", "say" },
            ["additionalProperties"] = false,
        };

        private static Dictionary<string, object> Enum(List<string> values) =>
            new Dictionary<string, object> { ["type"] = "string", ["enum"] = values.Cast<object>().ToList() };

        private static void OnReply(PawnMind mind, Dictionary<string, object> reply, List<ChoreOption> options, List<ThingDef> crops,
                                    List<ZoneSites.Site> fieldSites, ZoneSites fieldFinder, List<ZoneSites.Site> pileSites, ZoneSites pileFinder)
        {
            string Get(string key) => reply.TryGetValue(key, out object v) ? v as string : null;
            int pick = reply.TryGetValue("choice", out object c) && c is double d ? (int)d : -1;
            if (pick == 0)
            {
                RemarkAndLog(mind, Get("say"), "Looked over the colony's chores and left them as they are.", "colony: nothing");
                return;
            }
            var option = pick >= 1 && pick <= options.Count ? options[pick - 1] : null;
            if (option == null)
            {
                ModLog.Warning($"{mind.pawn.LabelShort}: colony reply chose {pick}, which isn't on the list.");
                return;
            }
            var choice = new ColonyChoice
            {
                size = Math.Max(0, Array.IndexOf(ChoreOptions.Sizes, Get("size"))),
                crop = crops.Find(x => x.label == Get("crop")),
                holds = Stockpiles.Kinds.FirstOrDefault(k => k == Get("holds")),
            };
            string missing = null;
            switch (option.needs)
            {
                case ChoreNeeds.Amount:
                    choice.count = option.counts[choice.size];
                    break;
                case ChoreNeeds.Field:
                    choice.site = fieldSites.Find(s => s.letter.ToString() == Get("site"));
                    choice.finder = fieldFinder;
                    missing = choice.crop == null ? "a crop" : choice.site == null ? "a field site" : null;
                    break;
                case ChoreNeeds.Stockpile:
                    choice.site = pileSites.Find(s => s.letter.ToString() == Get("site"));
                    choice.finder = pileFinder;
                    missing = choice.holds == null ? "what it holds" : choice.site == null ? "a stockpile site" : null;
                    break;
                case ChoreNeeds.Crop:
                    missing = choice.crop == null ? "a crop" : null;
                    break;
            }
            if (missing == null && option.check() != null && Refresh(mind.pawn, option) is ChoreOption fresh)
            {
                // Someone marked some of them while she was thinking: the next free ones instead (PHASE6.md §5.1)
                ModLog.Message($"{mind.pawn.LabelShort}: \"{option.label}\" was taken meanwhile; took \"{fresh.label}\" instead.");
                option = fresh;
                choice.count = fresh.counts[choice.size];
            }
            string result;
            if (missing != null)
            {
                result = $"Wanted to {option.label}, but didn't pick {missing}.";
                ModLog.Warning($"{mind.pawn.LabelShort}: colony reply for \"{option.label}\" had no {missing}: {Json.Write(reply)}");
            }
            else if (option.check() is string why)
                result = $"Couldn't {option.label}: {why}.";
            else
            {
                try
                {
                    result = option.apply(mind, choice);
                }
                catch (Exception e)
                {
                    result = "That went wrong: " + e.Message;
                    ModLog.Error($"{mind.pawn.LabelShort}: applying \"{option.label}\" threw: {e}");
                }
            }
            RemarkAndLog(mind, Get("say"), result, $"colony: {option.label} [{Get("size")}, {Get("crop")}, {Get("site")}, {Get("holds")}]");
        }

        /// <summary>
        /// Another mind marked some of the picked targets while she was thinking: the same kind of chore found again on
        /// the map now, the same animal or ore first. Null when none are left or it isn't a marking chore.
        /// </summary>
        private static ChoreOption Refresh(Pawn pawn, ChoreOption stale)
        {
            if (stale.needs != ChoreNeeds.Amount || !(stale.kind == Chore.Kind.Hunt || stale.kind == Chore.Kind.Cut
                                                     || stale.kind == Chore.Kind.Gather || stale.kind == Chore.Kind.Mine))
                return null;
            string Head(string label) => label.Split(new[] { " (", " ×" }, StringSplitOptions.None)[0];
            var same = ChoreOptions.All(pawn, ignoreLimits: true)
                .Where(o => o.kind == stale.kind && o.needs == ChoreNeeds.Amount && o.check() == null).ToList();
            return same.FirstOrDefault(o => Head(o.label) == Head(stale.label)) ?? same.FirstOrDefault();
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
            mind.AddDecision(result, importance: 0); // the chore itself records the memory event
            ModLog.Message($"{mind.pawn.LabelShort} {what} | {result} | Reason: {mind.lastReason}");
        }
    }
}
