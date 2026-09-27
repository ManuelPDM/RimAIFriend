using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>The live facts the LLM sees (PromptBuilder assembles them). Main thread only: most of these getters touch caches.</summary>
    public static class SnapshotBuilder
    {
        private const int MaxThoughts = 3;
        private const int MaxSkills = 8;
        private const int MaxPeople = 10;
        private const float LowNeed = 0.25f;
        private const int MaxAlerts = 6;
        private const int MaxRooms = 12;
        private const int MaxRoomItems = 4;

        public static string Identity(Pawn pawn)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {pawn.Name?.ToStringFull ?? pawn.LabelShort}, {pawn.ageTracker.AgeBiologicalYears}, {pawn.gender.GetLabel()}.");
            var story = pawn.story;
            if (story?.Childhood != null)
                sb.AppendLine($"Childhood: {story.Childhood.TitleCapFor(pawn.gender)}. {story.Childhood.FullDescriptionFor(pawn).Resolve()}");
            if (story?.Adulthood != null)
                sb.AppendLine($"Adulthood: {story.Adulthood.TitleCapFor(pawn.gender)}. {story.Adulthood.FullDescriptionFor(pawn).Resolve()}");
            if (story?.traits != null && story.traits.allTraits.Count > 0)
                sb.AppendLine("Traits: " + string.Join(", ", story.traits.allTraits.Select(t => t.LabelCap.ToString())) + ".");
            if (pawn.Ideo != null)
                sb.AppendLine($"Ideoligion: {pawn.Ideo.name} ({string.Join(", ", pawn.Ideo.memes.Select(m => m.LabelCap.ToString()))}).");
            string skills = Skills(pawn);
            if (skills != null)
                sb.AppendLine("Skills: " + skills);
            var family = pawn.relations?.DirectRelations
                .Where(r => r.otherPawn != null)
                .Select(r => $"{r.otherPawn.LabelShort} ({r.def.GetGenderSpecificLabel(r.otherPawn)})")
                .ToList();
            if (family != null && family.Count > 0)
                sb.AppendLine("Relations: " + string.Join(", ", family) + ".");
            return sb.ToString().TrimEnd();
        }

        /// <summary>The live snapshot as section name → text; null means leave it out. PromptBuilder labels and orders them.</summary>
        public static Dictionary<string, string> Sections(Pawn pawn, PawnMind mind)
        {
            Map map = pawn.Map;
            string ideo = pawn.Ideo != null ? $" Ideo: {pawn.Ideo.name}." : "";
            // Only needs that are low: vanilla looks after the rest (STREAMLINE.md §9).
            var needs = pawn.needs?.AllNeeds
                .Where(n => n.ShowOnNeedList && !(n is Need_Mood) && n.CurLevelPercentage < LowNeed)
                .Select(n => $"{n.LabelCap} low").ToList();
            string goodAt = GoodAt(pawn);
            return new Dictionary<string, string>
            {
                ["Me"] = $"{pawn.LabelShort}, {pawn.ageTracker.AgeBiologicalYears}, {pawn.gender.GetLabel()}. " +
                         $"{Backstory(pawn)}Traits: {string.Join(", ", pawn.story?.traits?.allTraits.Select(t => t.LabelCap.ToString()) ?? Enumerable.Empty<string>())}.{ideo}" +
                         (goodAt != null ? $" Good at: {goodAt}." : "") +
                         (mind.WorkFeelingsText() is string work ? " " + work : "") +
                         (BuildManager.Instance?.RoomsPhrase(pawn) is string rooms && rooms.Length > 0 ? " " + rooms : ""),
                ["Time"] = $"{TimeString(map)}, {map.weatherManager.curWeather.label}, {map.mapTemperature.OutdoorTemp.ToStringTemperature("F0")} outside. " +
                           $"I'm in: {RoomLabel(pawn)}.",
                ["Condition"] = Condition(pawn),
                ["Needs"] = needs != null && needs.Count > 0 ? string.Join(", ", needs) : null,
                ["Feelings"] = Feelings(pawn),
                ["Doing now"] = (pawn.GetJobReport() ?? "Nothing yet").TrimEnd('.') + ".", // null between jobs, e.g. as a mental break starts
                ["My project"] = BuildManager.Instance?.ProjectLine(pawn),
                ["People"] = People(pawn),
                ["Others"] = Others(pawn),
                ["Colony"] = Colony(map),
                ["Colony stores"] = Stores(map),
                ["Colony work"] = ColonyWork.Line(pawn),
                ["Rooms"] = Rooms(pawn),
                ["Recent"] = Recent(mind, map),
            };
        }

        /// <summary>Her last 5 decisions grouped by day: "Yesterday: 13:00 … · 21:00 … — Today: 07:00 …".</summary>
        private static string Recent(PawnMind mind, Map map)
        {
            int count = Math.Min(5, mind.decisions.Count);
            if (count == 0)
                return null;
            var groups = new List<KeyValuePair<string, List<string>>>();
            for (int i = mind.decisions.Count - count; i < mind.decisions.Count; i++)
            {
                int tick = i < mind.decisionTicks.Count ? mind.decisionTicks[i] : -1;
                string day = tick >= 0 ? DayLabel(tick, map) : "Earlier";
                if (groups.Count == 0 || groups[groups.Count - 1].Key != day)
                    groups.Add(new KeyValuePair<string, List<string>>(day, new List<string>()));
                groups[groups.Count - 1].Value.Add(mind.decisions[i]);
            }
            return string.Join(" — ", groups.Select(g => $"{g.Key}: {string.Join(" · ", g.Value)}"));
        }

        /// <summary>The map's local day number of a game tick.</summary>
        private static int LocalDayAt(int tick, Map map)
        {
            long abs = GenDate.TickGameToAbs(tick);
            float longitude = Find.WorldGrid.LongLatOf(map.Tile).x;
            return GenDate.Year(abs, longitude) * GenDate.DaysPerYear + GenDate.DayOfYear(abs, longitude);
        }

        /// <summary>"Today", "Yesterday" or "3 days ago", in the map's local time.</summary>
        public static string DayLabel(int tick, Map map)
        {
            int days = LocalDayAt(Find.TickManager.TicksGame, map) - LocalDayAt(tick, map);
            return days <= 0 ? "Today" : days == 1 ? "Yesterday" : $"{days} days ago";
        }

        /// <summary>"14:05" in the map's local time.</summary>
        public static string Clock(int tick, Map map)
        {
            float hour = GenDate.HourFloat(GenDate.TickGameToAbs(tick), Find.WorldGrid.LongLatOf(map.Tile).x);
            return $"{(int)hour:00}:{(int)(hour % 1f * 60f):00}";
        }

        public static string TimeString(Map map)
        {
            string date = GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile));
            float hour = GenLocalDate.HourFloat(map);
            return $"{date}, {(int)hour:00}:{(int)((hour % 1f) * 60f):00}";
        }

        private static string Backstory(Pawn pawn)
        {
            var parts = new List<string>();
            if (pawn.story?.Adulthood != null) parts.Add($"{pawn.story.Adulthood.TitleCapFor(pawn.gender)} (adult)");
            if (pawn.story?.Childhood != null) parts.Add($"{pawn.story.Childhood.TitleCapFor(pawn.gender)} (childhood)");
            return parts.Count > 0 ? string.Join(", ", parts) + ". " : "";
        }

        internal static string RoomLabel(Pawn pawn)
        {
            Room room = pawn.GetRoom();
            if (room == null || room.PsychologicallyOutdoors)
                return "outside";
            return room.GetRoomRoleLabel();
        }

        private static string Condition(Pawn pawn)
        {
            var parts = new List<string>();
            if (pawn.InMentalState)
                parts.Add($"I'm in a mental break right now: {pawn.MentalStateDef.label}" + (pawn.MentalState.causedByMood ? " (my mood snapped)" : ""));
            parts.Add($"Health {pawn.health.summaryHealth.SummaryHealthPercent.ToStringPercent()}");
            var injuries = pawn.health.hediffSet.hediffs
                .Where(h => h.Visible && h.def.isBad)
                .Select(h => h.Part != null ? $"{h.LabelCap} ({h.Part.Label})" : h.LabelCap.ToString())
                .Distinct()
                .Take(6)
                .ToList();
            parts.Add(injuries.Count > 0 ? "Injuries/conditions: " + string.Join(", ", injuries) : "No injuries");
            float pain = pawn.health.hediffSet.PainTotal;
            if (pain > 0.01f) parts.Add($"Pain {pain.ToStringPercent()}");
            if (pawn.health.hediffSet.BleedRateTotal > 0.01f) parts.Add("Bleeding");
            if (pawn.needs?.mood != null)
                parts.Add("Mood " + MoodWords(pawn));
            return string.Join(". ", parts) + ".";
        }

        /// <summary>Mood in words, against her own break threshold (STREAMLINE.md §9): numbers made the model panic well above it.</summary>
        private static string MoodWords(Pawn pawn)
        {
            float mood = pawn.needs.mood.CurLevel;
            float minor = pawn.mindState.mentalBreaker.BreakThresholdMinor;
            if (mood < pawn.mindState.mentalBreaker.BreakThresholdMajor) return "very low (close to a serious break)";
            if (mood < minor) return "low (at risk of a break)";
            if (mood < minor + 0.1f) return "shaky (a little above breaking)";
            if (mood < 0.65f) return "okay";
            return mood < 0.85f ? "good" : "great";
        }

        /// <summary>"construction (burning passion), melee (interested)": her skills with a passion, best first.</summary>
        private static string GoodAt(Pawn pawn)
        {
            var skills = pawn.skills?.skills.Where(s => !s.TotallyDisabled && s.passion != Passion.None).OrderByDescending(s => s.Level)
                .Select(s => $"{s.def.label} ({PassionLabel(s.passion)})").ToList();
            return skills != null && skills.Count > 0 ? string.Join(", ", skills) : null;
        }

        private static string Feelings(Pawn pawn)
        {
            var thoughts = pawn.needs?.mood?.thoughts;
            if (thoughts == null)
                return null;
            var groups = new List<Thought>();
            thoughts.GetDistinctMoodThoughtGroups(groups);
            var lines = groups
                .Select(t => (t, offset: thoughts.MoodOffsetOfGroup(t)))
                .Where(x => Math.Abs(x.offset) >= 0.5f)
                .OrderByDescending(x => Math.Abs(x.offset))
                .Take(MaxThoughts)
                .Select(x => $"{x.offset:+0;-0} {x.t.LabelCap}")
                .ToList();
            return lines.Count > 0 ? string.Join(" · ", lines) : null;
        }

        public static string Skills(Pawn pawn)
        {
            if (pawn.skills == null)
                return null;
            var usable = pawn.skills.skills.Where(s => !s.TotallyDisabled).ToList();
            var shown = usable.OrderByDescending(s => s.Level).Take(MaxSkills)
                .Union(usable.Where(s => s.passion != Passion.None))
                .OrderByDescending(s => s.Level)
                .Select(s => s.passion == Passion.None ? $"{s.def.LabelCap} {s.Level}" : $"{s.def.LabelCap} {s.Level} ({PassionLabel(s.passion)})");
            return string.Join(", ", shown);
        }

        public static string PassionLabel(Passion passion)
        {
            switch (passion)
            {
                case Passion.Minor: return "interested";
                case Passion.Major: return "burning passion";
                default: return passion.ToString().ToLower();
            }
        }

        private const int MaxOthers = 6;
        private const int OthersCap = 600;

        /// <summary>The nouns for [Others]' role words, by skill.</summary>
        private static readonly Dictionary<string, string> RoleWords = new Dictionary<string, string>
        {
            ["Shooting"] = "shooter", ["Melee"] = "fighter", ["Construction"] = "builder", ["Mining"] = "miner",
            ["Cooking"] = "cook", ["Plants"] = "grower", ["Animals"] = "animal handler", ["Crafting"] = "crafter",
            ["Artistic"] = "artist", ["Medicine"] = "doctor", ["Social"] = "talker", ["Intellectual"] = "researcher",
        };

        /// <summary>"miner, cook": her two best skills with a passion, or her best skill if she has no passion.</summary>
        private static string Roles(Pawn pawn)
        {
            var skills = pawn.skills?.skills.Where(s => !s.TotallyDisabled && RoleWords.ContainsKey(s.def.defName)).ToList();
            if (skills == null || skills.Count == 0)
                return null;
            var picked = skills.Where(s => s.passion != Passion.None).OrderByDescending(s => s.Level).Take(2).ToList();
            if (picked.Count == 0)
                picked = skills.OrderByDescending(s => s.Level).Take(1).ToList();
            return string.Join(", ", picked.Select(s => RoleWords[s.def.defName]));
        }

        /// <summary>
        /// [Others] (PHASE6.md §2.3, STREAMLINE.md §4): every other colonist, closest first. Name, role words and what they're
        /// doing; for a mind also its project and the last chore it set up. What anyone in the colony could see or be told.
        /// </summary>
        private static string Others(Pawn pawn)
        {
            Map map = pawn.Map;
            var others = map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn)
                .OrderBy(p => p.Position.DistanceToSquared(pawn.Position)).ToList();
            if (others.Count == 0)
                return null;
            var lines = new List<string>();
            int length = 0;
            foreach (Pawn other in others.Take(MaxOthers))
            {
                string roles = Roles(other);
                var parts = new List<string> { (other.GetJobReport() ?? "idle").TrimEnd('.') };
                var mind = MindManager.Instance?.MindOf(other);
                if (mind != null)
                {
                    if (BuildManager.Instance?.ProjectLine(other) is string project)
                        parts.Add("project: " + project.TrimEnd('.'));
                    if (ChoreManager.Instance?.LastOf(other) is Chore chore)
                        parts.Add($"{Ago(chore.placedTick)} {ChoreManager.Did(chore)}");
                }
                string line = other.LabelShort + (roles != null ? $" ({roles})" : "") + (parts.Count > 0 ? ": " + string.Join(" · ", parts) : "");
                if (lines.Count > 0 && length + line.Length > OthersCap)
                    break;
                lines.Add(line);
                length += line.Length + 3;
            }
            string text = string.Join(" — ", lines) + ".";
            return others.Count > lines.Count ? $"{text} And {others.Count - lines.Count} more." : text;
        }

        /// <summary>"just now", "3 h ago", "2 days ago".</summary>
        private static string Ago(int tick)
        {
            int ticks = Find.TickManager.TicksGame - tick;
            return ticks < GenDate.TicksPerHour ? "just now" : ticks < GenDate.TicksPerDay ? $"{ticks / GenDate.TicksPerHour} h ago"
                : ticks < 2 * GenDate.TicksPerDay ? "yesterday" : $"{ticks / GenDate.TicksPerDay} days ago";
        }

        /// <summary>[People] (STREAMLINE.md §4): every colonist, nearest first: how she relates to them, their mood, where they are.</summary>
        private static string People(Pawn pawn)
        {
            var others = pawn.Map.mapPawns.FreeColonistsSpawned
                .Where(p => p != pawn)
                .OrderBy(p => p.Position.DistanceToSquared(pawn.Position))
                .Take(MaxPeople)
                .ToList();
            if (others.Count == 0)
                return null;
            return string.Join("; ", others.Select(p =>
            {
                string relation = pawn.GetMostImportantRelation(p)?.GetGenderSpecificLabel(p) ?? "colonist";
                int opinion = pawn.relations?.OpinionOf(p) ?? 0;
                string mood = p.InMentalState ? $"in a mental break: {p.MentalStateDef.label}" // MoodString would just say "mental state"
                    : p.needs?.mood != null ? p.needs.mood.MoodString : "";
                int dist = (int)p.Position.DistanceTo(pawn.Position);
                string asleep = p.Awake() ? "" : ", asleep";
                string where = dist <= NearbyTiles ? "nearby" : RoomLabel(p) == "outside" ? $"outside, {dist} tiles away" : $"in the {RoomLabel(p)}";
                return $"{p.LabelShort} ({p.gender.GetLabel()}, {relation}, opinion {opinion:+0;-0;0}, {mood}{asleep}, {where})";
            }));
        }

        /// <summary>What [People] calls "nearby"; a talk with someone this close starts now, else when they next meet.</summary>
        public const int NearbyTiles = 12;

        public static bool Nearby(Pawn a, Pawn b) => a.Map == b.Map && a.Position.DistanceTo(b.Position) <= NearbyTiles;

        private static string Colony(Map map)
        {
            var parts = new List<string>
            {
                $"{map.mapPawns.FreeColonistsSpawnedCount} colonists",
                "Danger: " + map.dangerWatcher.DangerRating.ToString().ToLower(),
            };
            string alerts = Alerts();
            if (alerts != null)
                parts.Add("Alerts: " + alerts);
            parts.Add("Base: " + Ladder.Line(map));
            parts.Add("Food: " + FoodOutlook.For(map).Line());
            return string.Join(". ", parts) + ".";
        }

        /// <summary>What's in storage, like the vanilla resource readout. Food days use vanilla's Low food math (1 nutrition per colonist per day).</summary>
        private static string Stores(Map map)
        {
            // The resource counter only sees stockpiles and shelves. Without any, "0 food" would be a lie
            // (a fresh crash-landing has meals lying around, and a mind panicked about starving).
            if (map.haulDestinationManager.AllGroupsListForReading.Count == 0)
                return "unknown: no stockpile zones or shelves set up yet, so supplies lying around aren't counted.";
            var counter = map.resourceCounter;
            int colonists = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            float nutrition = counter.TotalHumanEdibleNutrition;
            var parts = new List<string>
            {
                $"food for about {nutrition / colonists:0.#} days ({nutrition:0} nutrition)",
                $"medicine {counter.GetCountIn(ThingRequestGroup.Medicine)}",
            };
            foreach (var def in new[] { ThingDefOf.WoodLog, ThingDefOf.Steel, ThingDefOf.ComponentIndustrial, ThingDefOf.Silver })
                parts.Add($"{def.label} {counter.GetCount(def)}");
            string haul = WaitingToBeHauled(map);
            return string.Join(", ", parts) + " (in stockpiles and shelves only)." + (haul != null ? " Waiting to be hauled: " + haul + "." : "");
        }

        /// <summary>
        /// "wood 240, steel 75, 12 corpses, 9 apparel" (FURNISHING.md §3): what vanilla itself lists as needing hauling
        /// (not in its best storage, not forbidden), in the home area. Resources by name with amounts, the rest one count
        /// per top-level category. Null when nothing waits.
        /// </summary>
        private static string WaitingToBeHauled(Map map)
        {
            var named = new Dictionary<ThingDef, int>();
            var grouped = new Dictionary<ThingCategoryDef, int>();
            foreach (var t in map.listerHaulables.ThingsPotentiallyNeedingHauling())
            {
                if (!t.Spawned || !map.areaManager.Home[t.Position])
                    continue;
                if (t.def.CountAsResource)
                {
                    named.TryGetValue(t.def, out int n);
                    named[t.def] = n + t.stackCount;
                }
                else if (TopCategory(t.def) is ThingCategoryDef category)
                {
                    grouped.TryGetValue(category, out int n);
                    grouped[category] = n + t.stackCount;
                }
            }
            var parts = named.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.label} {kv.Value}")
                .Concat(grouped.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key.label}")).ToList();
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        /// <summary>The thing's category just below the root ("corpses", "apparel", "weapons").</summary>
        private static ThingCategoryDef TopCategory(ThingDef def)
        {
            var c = def.FirstThingCategory;
            while (c?.parent != null && c.parent != ThingCategoryDefOf.Root)
                c = c.parent;
            return c;
        }

        /// <summary>The kinds of rooms the colony has, e.g. "bedroom ×2, kitchen", or that it has none yet.</summary>
        /// <summary>
        /// Every indoor room on the map, read fresh each call so it never goes stale (PHASE4.md §7): the room she's in in
        /// detail, then one short phrase per other room with its notable furniture. Her own and her built rooms first.
        /// </summary>
        private static string Rooms(Pawn pawn)
        {
            Map map = pawn.Map;
            Room here = pawn.GetRoom();
            if (here != null && (here.PsychologicallyOutdoors || here.Fogged || here.IsDoorway || here.Role == null || here.Role == RoomRoleDefOf.None))
                here = null; // a door cell is a one-cell "room" of its own
            var built = new HashSet<Room>(BuildManager.Instance?.ProjectsOf(pawn)
                .Where(p => p.state == BuildProject.State.Done && p.map == map)
                .Select(p => p.Room).Where(r => r != null) ?? Enumerable.Empty<Room>());
            var rooms = map.regionGrid.AllRooms
                .Where(r => r != here && !r.PsychologicallyOutdoors && !r.Fogged && r.Role != null && r.Role != RoomRoleDefOf.None)
                .OrderByDescending(r => r.Owners.Contains(pawn))
                .ThenByDescending(r => built.Contains(r))
                .ToList();

            var parts = new List<string>();
            int empty = 0;
            foreach (var room in rooms)
            {
                var items = Furniture(room);
                if (items.Count == 0)
                    empty++;
                else
                    parts.Add($"{RoomName(room, pawn)} ({BuildManager.Impressiveness(room)}{BuiltBy(room, pawn)}): {ItemList(items, MaxRoomItems)}");
            }
            if (parts.Count > MaxRooms)
                parts = parts.Take(MaxRooms).Append($"{parts.Count - MaxRooms} more rooms").ToList();
            if (empty > 0)
                parts.Add(empty == 1 ? "an empty room" : $"{empty} empty rooms");

            string others = parts.Count > 0 ? string.Join(" · ", parts) : null;
            if (here == null)
                return others ?? "no proper rooms yet";
            var hereItems = Furniture(here);
            string name = RoomName(here, pawn);
            string detail = $"I'm in {(here.Owners.Any() ? name : "the " + name)}: {RoomFeel(here, pawn)}. " +
                            (hereItems.Count > 0 ? $"Has: {ItemList(hereItems, int.MaxValue)}." : "Nothing in it.");
            return others != null ? $"{detail} Other rooms: {others}" : detail;
        }

        /// <summary>", built by Sab" or ", built by me" (STREAMLINE.md §4), or "".</summary>
        private static string BuiltBy(Room room, Pawn pawn)
        {
            Pawn builder = BuildManager.Instance?.BuilderOf(room);
            return builder == null ? "" : builder == pawn ? ", built by me" : $", built by {builder.LabelShort}";
        }

        /// <summary>"my bedroom" for hers, vanilla's label otherwise ("Mo's bedroom", "kitchen").</summary>
        private static string RoomName(Room room, Pawn pawn) =>
            room.Owners.Contains(pawn) ? "my " + room.Role.label : room.GetRoomRoleLabel();

        /// <summary>Notable furniture inside the room: no walls, doors, floors, conduits or lights.</summary>
        private static List<Thing> Furniture(Room room) =>
            room.ContainedAndAdjacentThings
                .Where(t => t.def.category == ThingCategory.Building && room.ContainsCell(t.Position)
                            && t.def.designationCategory != null && !HiddenCategories.Contains(t.def.designationCategory.defName)
                            && !t.def.HasComp(typeof(CompGlower)))
                .Distinct()
                .ToList();

        private static readonly HashSet<string> HiddenCategories = new HashSet<string> { "Structure", "Floors", "Power", "Security" };

        /// <summary>"bed, 3 stools, end table": same items grouped, the most valuable first, then "and N more".</summary>
        private static string ItemList(List<Thing> items, int max)
        {
            var groups = items.GroupBy(t => t.def)
                .OrderByDescending(g => g.Max(t => t.MarketValue))
                .Select(g => g.Count() > 1 ? $"{g.Count()} {Find.ActiveLanguageWorker.Pluralize(g.Key.label, g.Count())}" : g.Key.label)
                .ToList();
            return groups.Count > max ? string.Join(", ", groups.Take(max)) + $" and {groups.Count - max} more" : string.Join(", ", groups);
        }

        /// <summary>How the room feels, in vanilla's own stat words: "5×5, awful, cramped, ugly, clean, dark".</summary>
        private static string RoomFeel(Room room, Pawn pawn)
        {
            CellRect extents = room.ExtentsClose;
            var words = new List<string> { extents.Area == room.CellCount ? $"{extents.Width}×{extents.Height}" : $"{room.CellCount} cells" };
            foreach (var stat in new[] { RoomStatDefOf.Impressiveness, RoomStatDefOf.Space, RoomStatDefOf.Beauty, RoomStatDefOf.Cleanliness })
                words.Add(stat.GetScoreStage(room.GetStat(stat)).label);
            if (pawn.Map.glowGrid.PsychGlowAt(pawn.Position) == PsychGlow.Dark)
                words.Add("dark");
            return string.Join(", ", words.Where(w => !string.IsNullOrEmpty(w)));
        }

        private static string Alerts()
        {
            try
            {
                var active = Traverse.Create(Find.Alerts).Field("activeAlerts").GetValue<List<Alert>>();
                if (active == null || active.Count == 0)
                    return null;
                return string.Join(", ", active.OrderByDescending(a => a.Priority).Take(MaxAlerts).Select(a => a.GetLabel()));
            }
            catch
            {
                return null;
            }
        }
    }
}
