using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    /// <summary>
    /// Danger response (DANGER_RESPONSE.md): what's dangerous on the map, the Inside area, and the danger menu. Every
    /// choice is carried out with vanilla's levers (allowed area, hostility response, an ordered job).
    /// </summary>
    public static class DangerResponse
    {
        /// <summary>
        /// Hostile things that are an active threat to the colony now, by vanilla's own test: raiders, manhunters, awake
        /// mechs, and a predator hunting a colonist (GenHostility counts it hostile while it hunts).
        /// </summary>
        public static List<Thing> Threats(Map map) =>
            map.attackTargetsCache.TargetsHostileToColony.Where(t => GenHostility.IsActiveThreatToPlayer(t)).Select(t => t.Thing).ToList();

        /// <summary>The colonist a threat is going after right now (its job's target or the enemy it fights), or null.</summary>
        public static Pawn Victim(Thing threat)
        {
            if (!(threat is Pawn p))
                return null;
            var target = p.CurJob?.targetA.Thing as Pawn ?? p.mindState?.enemyTarget as Pawn;
            return target != null && target.IsFreeColonist && target.Spawned && target.Map == p.Map ? target : null;
        }

        public static string Label(Thing t) => (t as Pawn)?.KindLabel ?? t.def.label;

        private static string Weapon(Pawn p) => p.equipment?.Primary?.def.label;

        // ---------- The Inside area ----------

        private static string InsideLabel => "AIPawnControl_InsideArea".Translate();

        /// <summary>Every indoor room of the base (Layout.OfBase) and its doors: all of the inside, nothing per room.</summary>
        public static HashSet<IntVec3> InsideCells(Map map)
        {
            var cells = new HashSet<IntVec3>();
            foreach (var room in map.regionGrid.AllRooms.Where(Layout.OfBase))
            {
                cells.UnionWith(room.Cells);
                foreach (var door in Layout.Doors(room))
                    cells.Add(door.Position);
            }
            return cells;
        }

        private static Area_Allowed FindInside(Map map) =>
            map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(a => a.Label == InsideLabel);

        public static bool CanGoInside(Map map) =>
            (FindInside(map) != null || map.areaManager.CanMakeNewAllowed()) && InsideCells(map).Count > 0;

        /// <summary>The mod's "Inside" allowed area, made if missing and redrawn to the base as it is now; null if it can't be.</summary>
        public static Area_Allowed Inside(Map map)
        {
            var cells = InsideCells(map);
            if (cells.Count == 0)
                return null;
            var area = FindInside(map);
            if (area == null)
            {
                if (!map.areaManager.TryMakeNewAllowed(out area))
                    return null;
                area.RenamableLabel = InsideLabel;
            }
            area.Clear();
            foreach (var c in cells)
                area[c] = true;
            return area;
        }

        // ---------- What the prompt shows ----------

        /// <summary>
        /// [Danger]: the threats by kind (count, weapons, the nearest one's distance), who they're going after, her own
        /// weapon and fighting skills, and who else is armed. Null when there's no danger.
        /// </summary>
        public static string Line(Pawn pawn)
        {
            var threats = Threats(pawn.Map);
            if (threats.Count == 0)
                return null;
            var parts = threats.GroupBy(Label).Select(g =>
            {
                var weapons = g.Select(t => t is Pawn p ? Weapon(p) : null).Where(w => w != null).Distinct().ToList();
                int nearest = (int)g.Min(t => t.Position.DistanceTo(pawn.Position));
                return $"{g.Key} x{g.Count()}{(weapons.Count > 0 ? $" ({string.Join(", ", weapons)})" : "")}, the nearest {nearest} tiles from me";
            }).ToList();
            var attacked = threats.Select(t => (t, victim: Victim(t))).Where(x => x.victim != null)
                .Select(x => $"{Label(x.t)} is after {(x.victim == pawn ? "me" : x.victim.LabelShort)}").Distinct().ToList();
            if (attacked.Count > 0)
                parts.Add(string.Join(", ", attacked));
            parts.Add(pawn.WorkTagIsDisabled(WorkTags.Violent) ? "I can't fight"
                : $"Me: {Weapon(pawn) ?? "no weapon"}, Shooting {pawn.skills?.GetSkill(SkillDefOf.Shooting).Level ?? 0}, Melee {pawn.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0}");
            var armed = pawn.Map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn && Weapon(p) != null).Select(p => $"{p.LabelShort} ({Weapon(p)})").ToList();
            parts.Add("Armed: " + (armed.Count > 0 ? string.Join(", ", armed) : "nobody else"));
            return string.Join(". ", parts) + ".";
        }

        // ---------- The danger menu ----------

        /// <summary>
        /// While there's danger: go inside, fight, help each colonist something is going after. Only what she can do now:
        /// no fighting for someone incapable of violence, no "go inside" without indoor rooms.
        /// </summary>
        public static List<ActionCatalog.ActOption> Options(Pawn pawn, PawnMind mind, List<Thing> threats, int firstId)
        {
            var options = new List<ActionCatalog.ActOption>();
            Map map = pawn.Map;
            var current = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            if (pawn.playerSettings != null && (current == null || current.Label != InsideLabel) && CanGoInside(map))
                options.Add(new ActionCatalog.ActOption { Id = firstId + options.Count, Key = "inside", Label = "go inside and stay there until the danger is over",
                    Apply = _ => MindActions.GoInside(mind) });
            if (pawn.WorkTagIsDisabled(WorkTags.Violent) || pawn.playerSettings == null)
                return options;

            var reachable = threats.OrderBy(t => t.Position.DistanceToSquared(pawn.Position))
                .Where(t => pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly)).ToList();
            if (reachable.Count == 0)
                return options;
            var nearest = reachable[0];
            options.Add(new ActionCatalog.ActOption { Id = firstId + options.Count, Key = "fight",
                Label = $"fight: go after the nearest enemy ({Label(nearest)}, {(int)nearest.Position.DistanceTo(pawn.Position)} tiles away)",
                Apply = _ => MindActions.Fight(mind, nearest) });
            foreach (var group in reachable.Select(t => (t, victim: Victim(t))).Where(x => x.victim != null && x.victim != pawn).GroupBy(x => x.victim))
            {
                var victim = group.Key;
                var attacker = group.First().t; // the nearest to her
                options.Add(new ActionCatalog.ActOption { Id = firstId + options.Count, Key = "help " + victim.ThingID,
                    Label = $"help {victim.LabelShort}: go after the {Label(attacker)} attacking them",
                    Apply = _ => MindActions.Fight(mind, attacker) });
            }
            return options;
        }

        /// <summary>For a ranged fighter: where to shoot from, within the range vanilla's Attack response shoots at (2/3 of it, at most 20).</summary>
        public static bool TryShootingPosition(Pawn pawn, Thing target, Verb verb, out IntVec3 dest) =>
            CastPositionFinder.TryFindCastPosition(new CastPositionRequest
            {
                caster = pawn,
                target = target,
                verb = verb,
                maxRangeFromTarget = Mathf.Clamp(verb.EffectiveRange * 0.66f, 2f, 20f),
                wantCoverFromTarget = verb.EffectiveRange > 7f,
            }, out dest);
    }
}
