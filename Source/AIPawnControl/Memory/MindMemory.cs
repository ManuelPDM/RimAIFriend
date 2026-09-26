using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// One mind's memory, saved with it (PHASE3.md §2): the raw event log (scratch, 7 days) and what Reflect makes of it
    /// (memories, diary, person files, `lately`), plus goals.
    /// </summary>
    public class MindMemory : IExposable
    {
        private const int KeepEventsTicks = 7 * GenDate.TicksPerDay;
        private const int ArchiveAfterTicks = GenDate.TicksPerQuadrum;
        private const int ArchiveMaxImportance = 3;
        private const int MergeTicks = 2 * GenDate.TicksPerHour;
        private const int FirstTimeBonus = 2;
        private const int FirstTimeMinImportance = 5;
        public const int MaxGoals = 3;
        public const int MaxLately = 600;

        public List<MemoryEvent> events = new List<MemoryEvent>();
        private List<string> firsts = new List<string>(); // kind|def of important events she's had, for the "first time" bonus
        private int nextId = 1;

        public List<MemoryRecord> memories = new List<MemoryRecord>();
        public List<DiaryEntry> diary = new List<DiaryEntry>();
        public List<PersonFile> files = new List<PersonFile>();
        public List<Goal> goals = new List<Goal>();
        public string lately;
        private int nextMemoryId = 1;
        // What the last Reflect saw when it started. Newer events go into the next one, and so does an older event that
        // merged a repeat since. Ids, not ticks: a player chatting while paused adds events in the very same tick.
        public int lastReflectEventId;
        public int lastReflectTick = -1;
        public int lastReflectNight = -1; // local day index of the last night reflected on
        public int lastOnMindTick = -99999999; // [On my mind] shows at most once every 4 hours

        public int NewMemoryId() => nextMemoryId++;

        public int LastEventId => nextId - 1;

        /// <summary>
        /// Retention (PHASE3.md §7): memories older than a quadrum, of importance 3 or less and never used, are archived.
        /// They stay in the save but are never retrieved. Returns how many were archived.
        /// </summary>
        public int Archive(int now)
        {
            int count = 0;
            foreach (var m in memories)
            {
                if (!m.archived && now - m.tick > ArchiveAfterTicks && m.importance <= ArchiveMaxImportance && m.usedCount == 0)
                {
                    m.archived = true;
                    count++;
                }
            }
            return count;
        }

        public void MarkReflected(int eventId, int tick)
        {
            lastReflectEventId = eventId;
            lastReflectTick = tick;
        }

        public PersonFile File(string name, bool create)
        {
            var file = files.FirstOrDefault(f => f.name == name);
            if (file == null && create)
            {
                file = new PersonFile { name = name };
                files.Add(file);
            }
            return file;
        }

        /// <summary>At most 3 goals; a new one pushes out the oldest. The same goal twice is kept once.</summary>
        public void AddGoal(string text, string why, string source)
        {
            if (goals.Any(g => string.Equals(g.text, text, StringComparison.OrdinalIgnoreCase)))
                return;
            goals.Add(new Goal { text = text, why = why, source = source, since = Find.TickManager.TicksGame });
            while (goals.Count > MaxGoals)
                goals.RemoveAt(0);
        }

        public string Promises => JoinGoals(Goal.Promise);

        public string OwnGoals => JoinGoals(Goal.Mine);

        private string JoinGoals(string source)
        {
            var texts = goals.Where(g => g.source == source).Select(g => g.text).ToList();
            return texts.Count > 0 ? string.Join(" · ", texts) : null;
        }

        /// <summary>
        /// Adds an event. With merge, a repeat of the same kind, def and people from the last 2 hours only bumps that
        /// event's count. Important events (5+) get +2 the first time their kind and def come up.
        /// </summary>
        public MemoryEvent Record(Pawn pawn, string kind, string def, string text, int importance, string source,
                                  IEnumerable<string> people = null, string place = null, bool merge = false)
        {
            int now = Find.TickManager.TicksGame;
            events.RemoveAll(e => now - e.lastTick > KeepEventsTicks);
            var names = people?.Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n).ToList() ?? new List<string>();
            importance = Mathf.Clamp(importance, 1, 10);

            if (merge)
            {
                var same = events.LastOrDefault(e => e.kind == kind && e.def == def && now - e.lastTick <= MergeTicks && e.people.SequenceEqual(names));
                if (same != null)
                {
                    same.count++;
                    same.lastTick = now;
                    same.importance = Math.Max(same.importance, importance);
                    return same;
                }
            }
            if (importance >= FirstTimeMinImportance)
            {
                string key = kind + "|" + def;
                if (!firsts.Contains(key))
                {
                    firsts.Add(key);
                    importance = Mathf.Clamp(importance + FirstTimeBonus, 1, 10);
                }
            }
            var added = new MemoryEvent
            {
                id = nextId++,
                tick = now,
                lastTick = now,
                kind = kind,
                def = def,
                text = text,
                people = names,
                place = place ?? (pawn.Spawned ? SnapshotBuilder.RoomLabel(pawn) : null),
                source = source,
                importance = importance,
            };
            events.Add(added);
            return added;
        }

        /// <summary>
        /// [Today so far]: the day's most important events, oldest first, as "14:05 text". Her own decisions and chat
        /// turns are left out: [Recent] and the chat history already show them.
        /// </summary>
        public string TodaySoFar(Pawn pawn, int max)
        {
            Map map = pawn.Map;
            if (map == null)
                return null;
            int dayStart = Find.TickManager.TicksGame - GenLocalDate.DayTick(map);
            var top = events
                .Where(e => e.lastTick >= dayStart && e.kind != "decision" && e.kind != "chat")
                .OrderByDescending(e => e.importance).ThenByDescending(e => e.lastTick)
                .Take(max)
                .OrderBy(e => e.tick)
                .Select(e => $"{e.Clock(map)} {e.Text}")
                .ToList();
            return top.Count > 0 ? string.Join(" · ", top) : null;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref events, "events", LookMode.Deep);
            Scribe_Collections.Look(ref firsts, "firsts", LookMode.Value);
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Collections.Look(ref memories, "memories", LookMode.Deep);
            Scribe_Collections.Look(ref diary, "diary", LookMode.Deep);
            Scribe_Collections.Look(ref files, "files", LookMode.Deep);
            Scribe_Collections.Look(ref goals, "goals", LookMode.Deep);
            Scribe_Values.Look(ref lately, "lately");
            Scribe_Values.Look(ref nextMemoryId, "nextMemoryId", 1);
            Scribe_Values.Look(ref lastReflectEventId, "lastReflectEventId");
            Scribe_Values.Look(ref lastReflectTick, "lastReflectTick", -1);
            Scribe_Values.Look(ref lastReflectNight, "lastReflectNight", -1);
            Scribe_Values.Look(ref lastOnMindTick, "lastOnMindTick", -99999999);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                events = events ?? new List<MemoryEvent>();
                firsts = firsts ?? new List<string>();
                memories = memories ?? new List<MemoryRecord>();
                diary = diary ?? new List<DiaryEntry>();
                files = files ?? new List<PersonFile>();
                goals = goals ?? new List<Goal>();
            }
        }
    }
}
