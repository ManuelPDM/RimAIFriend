using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Builds the labelled text the LLM sees. Main thread only: most of these getters touch caches.</summary>
    public static class SnapshotBuilder
    {
        private const int MaxThoughts = 8;
        private const int MaxSkills = 8;
        private const int MaxPeople = 6;
        private const int MaxAlerts = 6;

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

        public static string Build(Pawn pawn, PawnMind mind)
        {
            Map map = pawn.Map;
            var sb = new StringBuilder();

            string ideo = pawn.Ideo != null ? $" Ideo: {pawn.Ideo.name}." : "";
            sb.AppendLine($"[Me] {pawn.LabelShort}, {pawn.ageTracker.AgeBiologicalYears}, {pawn.gender.GetLabel()}. " +
                          $"{Backstory(pawn)}Traits: {string.Join(", ", pawn.story?.traits?.allTraits.Select(t => t.LabelCap.ToString()) ?? Enumerable.Empty<string>())}.{ideo}");

            sb.AppendLine($"[Time] {TimeString(map)}, {map.weatherManager.curWeather.label}, {map.mapTemperature.OutdoorTemp.ToStringTemperature("F0")} outside. " +
                          $"I'm in: {RoomLabel(pawn)}. Schedule now: {pawn.timetable?.CurrentAssignment?.label ?? "anything"}.");

            sb.AppendLine("[Condition] " + Condition(pawn));

            var needs = pawn.needs?.AllNeeds
                .Where(n => n.ShowOnNeedList && !(n is Need_Mood))
                .Select(n => $"{n.LabelCap} {n.CurLevelPercentage.ToStringPercent()}");
            if (needs != null)
                sb.AppendLine("[Needs] " + string.Join(", ", needs));

            string feelings = Feelings(pawn);
            if (feelings != null)
                sb.AppendLine("[Feelings] " + feelings);

            string skills = Skills(pawn);
            if (skills != null)
                sb.AppendLine("[Skills] " + skills);

            sb.AppendLine("[Doing now] " + (pawn.GetJobReport() ?? "Nothing yet").TrimEnd('.') + "."); // null between jobs, e.g. as a mental break starts

            if (!string.IsNullOrEmpty(mind.intent))
                sb.AppendLine($"[My plan] {mind.intent}");
            if (mind.notes.Count > 0)
                sb.AppendLine("[I promised the player] " + string.Join(" · ", mind.notes));

            string people = People(pawn);
            if (people != null)
                sb.AppendLine("[People nearby] " + people);

            sb.AppendLine("[Colony] " + Colony(map));
            sb.AppendLine("[Colony stores] " + Stores(map));
            string rooms = Rooms(map);
            if (rooms != null)
                sb.AppendLine("[Rooms] " + rooms);

            if (mind.decisions.Count > 0)
                sb.AppendLine("[Recent] " + string.Join(" · ", mind.decisions.Skip(Math.Max(0, mind.decisions.Count - 5))));

            return sb.ToString().TrimEnd();
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

        private static string RoomLabel(Pawn pawn)
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
                parts.Add($"Mood {pawn.needs.mood.CurLevelPercentage.ToStringPercent()} (minor break at {pawn.mindState.mentalBreaker.BreakThresholdMinor.ToStringPercent()})");
            return string.Join(". ", parts) + ".";
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
                return $"{p.LabelShort} ({relation}, opinion {opinion:+0;-0;0}, {mood}{asleep}, {dist} tiles)";
            }));
        }

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
            return string.Join(", ", parts) + " (in stockpiles and shelves only).";
        }

        /// <summary>The kinds of rooms the colony has, e.g. "bedroom ×2, kitchen", or that it has none yet.</summary>
        private static string Rooms(Map map)
        {
            var roles = map.regionGrid.AllRooms
                .Where(r => !r.PsychologicallyOutdoors && !r.Fogged && r.Role != null && r.Role != RoomRoleDefOf.None)
                .GroupBy(r => r.GetRoomRoleLabel())
                .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
                .ToList();
            return roles.Count > 0 ? string.Join(", ", roles) : "no proper rooms yet";
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
