using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    /// <summary>
    /// Hunt options (STREAMLINE.md §5): wild animals grouped by kind (up to 8 each), with the meat and the danger in words from
    /// vanilla's revenge chance. Dangerous kinds only for someone who can shoot. Never owned, bonded or venerated animals.
    /// </summary>
    public static class Hunting
    {
        public const int MaxGroup = 8;
        private const int MinMeat = 60;
        private const int MinShootingForDanger = 6;

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            if (!scan.SomeoneCanDo(WorkTypeDefOf.Hunting))
                yield break;
            Pawn pawn = scan.pawn;
            // Vanilla only sends hunters who carry a ranged weapon: without one, marked animals just wait.
            if (!scan.map.mapPawns.FreeColonistsSpawned.Any(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) && WorkGiver_HunterHunt.HasHuntingWeapon(p)))
            {
                if (EquipOption(scan) is ChoreOption equip)
                    yield return equip;
                yield break;
            }
            bool canShoot = !pawn.WorkTagIsDisabled(WorkTags.Violent) && (pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0) >= MinShootingForDanger;
            var groups = scan.map.mapPawns.AllPawnsSpawned
                .Where(a => Check(a, pawn) == null)
                .GroupBy(a => a.kindDef);
            foreach (var group in groups)
            {
                var animals = group.OrderBy(a => a.Position.DistanceToSquared(scan.center)).Take(MaxGroup).ToList();
                string danger = Danger(animals, out bool dangerous);
                if (dangerous && !canShoot)
                    continue;
                int meat = animals.Sum(a => (int)a.GetStatValue(StatDefOf.MeatAmount));
                if (meat < MinMeat)
                    continue; // a few sparrows aren't worth a hunter's trip
                int steps = scan.WalkAt(animals[0].Position);
                string where = steps >= 0 ? ChoreScan.Near(steps) : "far from the base"; // out of the walk (it reaches the colony, Check): far
                string label = group.Key.label;
                int n = animals.Count;
                yield return new ChoreOption
                {
                    kind = Chore.Kind.Hunt,
                    label = $"food: hunt {n} {label} ({where}, {danger}, ~{meat} meat)",
                    useful = 0.5f + ChoreOptions.FoodNeed(scan) + System.Math.Min(1.5f, meat / 300f) + scan.PassionFor(SkillDefOf.Shooting) - (dangerous ? 1f : 0f),
                    check = () => animals.Select(a => Check(a, pawn)).FirstOrDefault(r => r != null),
                    apply = mind => Apply(mind.pawn, animals, label),
                };
            }
        }

        /// <summary>"pick up the revolver to hunt with": nobody can hunt yet, and a ranged weapon lies around. Not tracked as a chore.</summary>
        private static ChoreOption EquipOption(ChoreScan scan)
        {
            Pawn pawn = scan.pawn;
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) || pawn.equipment == null)
                return null;
            var weapon = scan.map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)
                .Where(t => t.Spawned && t.def.IsRangedWeapon && !t.IsForbidden(pawn) && EquipmentUtility.CanEquip(t, pawn)
                            && pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Verse.Danger.Deadly))
                .OrderBy(t => t.Position.DistanceToSquared(pawn.Position))
                .FirstOrDefault();
            if (weapon == null)
                return null;
            return new ChoreOption
            {
                kind = Chore.Kind.Hunt,
                label = $"pick up the {weapon.def.label} to hunt with (nobody can hunt without a gun)",
                useful = 1f + ChoreOptions.FoodNeed(scan),
                check = () => weapon.Spawned && !weapon.IsForbidden(pawn) ? null : "gone",
                apply = mind =>
                {
                    if (!weapon.Spawned)
                        return $"The {weapon.def.label} is gone.";
                    var job = JobMaker.MakeJob(JobDefOf.Equip, weapon);
                    return MindActions.Order(mind, job) ? $"Going to pick up the {weapon.def.label}." : $"Couldn't pick up the {weapon.def.label}.";
                },
            };
        }

        /// <summary>The validator for one animal: null if it may be marked now.</summary>
        public static string Check(Pawn a, Pawn hunter)
        {
            if (a.RaceProps == null || !a.RaceProps.Animal) return "not an animal";
            if (a.Dead || !a.Spawned || a.Map != hunter.Map) return "gone";
            if (a.Faction != null) return "belongs to a faction";
            if (a.Fogged()) return "not seen";
            if (a.InAggroMentalState) return "already hostile";
            if (a.IsPrisonerInPrisonCell()) return "penned";
            if (a.playerSettings?.Master != null || a.relations?.DirectRelations.Any(r => r.def == PawnRelationDefOf.Bond) == true) return "bonded";
            if (a.Map.designationManager.DesignationOn(a) != null) return "already marked";
            if (hunter.Ideo != null && hunter.Ideo.IsVeneratedAnimal(a)) return "venerated";
            if (!a.Map.reachability.CanReachColony(a.Position)) return "can't be reached";
            return null;
        }

        /// <summary>"safe", "may fight back", "dangerous: …", from vanilla's revenge chance (with the difficulty factor), predators and explosions.</summary>
        public static string Danger(List<Pawn> animals, out bool dangerous)
        {
            float chance = animals.Max(a => PawnUtility.GetManhunterOnDamageChance(a));
            var race = animals[0].RaceProps;
            bool explodes = race.deathAction?.workerClass != null && race.deathAction.workerClass != typeof(DeathActionWorker_Simple);
            dangerous = race.predator || chance >= 0.2f;
            string words = race.predator ? "dangerous: a predator that fights back"
                : chance >= 0.2f ? "dangerous: it often turns on the hunters"
                : chance > 0f ? "may fight back"
                : "safe";
            return explodes ? words + ", explodes when killed" : words;
        }

        private static string Apply(Pawn pawn, List<Pawn> animals, string label)
        {
            var marked = animals.Where(a => Check(a, pawn) == null).ToList();
            if (marked.Count == 0)
                return ChoreOptions.NoneLeft(animals.Select(a => Check(a, pawn)), $"The {label} are gone or can't be hunted now.");
            var chore = ChoreManager.Instance.Mark(pawn, Chore.Kind.Hunt, label, marked.Select(a => new LocalTargetInfo(a)));
            chore.Remember($"I marked animals for hunting: {label} ×{marked.Count}.", 2);
            ModLog.Message($"{pawn.LabelShort} marked {label} ×{marked.Count} for hunting.");
            return $"Marked for hunting: {label} ×{marked.Count}.";
        }
    }
}
