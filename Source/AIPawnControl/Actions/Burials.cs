using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIPawnControl
{
    /// <summary>
    /// "Bury X and hold their funeral" (IDEOLOGY.md §13): one Act line while a colonist's body lies unburied. Code places a
    /// grave in the colony's cemetery (the first grave starts it near the base, later ones go beside it); the colony digs
    /// it; the mind who picked it carries the body there; then the funeral starts with her as organizer, when the dead
    /// one's ideoligion has one. Vanilla does every step; this only keeps track of the burial until it's done.
    /// </summary>
    public class Burials : GameComponent
    {
        private const int CheckTicks = 250;
        private const int ReorderTicks = GenDate.TicksPerHour * 2; // carrying the body was interrupted: ask her again after a while
        private const int CemeteryRadius = 6;                       // a new grave goes this close to the cemetery's first one

        private class Burial : IExposable
        {
            public Pawn dead;
            public Pawn by;
            public IntVec3 grave;
            public int orderedTick = -99999;

            public void ExposeData()
            {
                Scribe_References.Look(ref dead, "dead", saveDestroyedThings: true);
                Scribe_References.Look(ref by, "by");
                Scribe_Values.Look(ref grave, "grave");
                Scribe_Values.Look(ref orderedTick, "orderedTick", -99999);
            }
        }

        private List<Burial> burials = new List<Burial>();
        private IntVec3 cemetery = IntVec3.Invalid; // the first grave's cell, on the home map

        private static Burials instance;
        private readonly Game game;

        public static Burials Instance => instance != null && instance.game == Current.Game ? instance : null;

        public Burials(Game game)
        {
            this.game = game;
            instance = this;
        }

        // ---------- The Act line ----------

        /// <summary>Colonists' bodies lying unburied on this map, not yet being buried.</summary>
        public static List<Corpse> Unburied(Map map) =>
            map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
                .Where(c => c.Spawned && c.InnerPawn?.RaceProps.Humanlike == true && c.InnerPawn.HomeFaction == Faction.OfPlayer
                            && Instance?.burials.Any(b => b.dead == c.InnerPawn) != true)
                .ToList();

        /// <summary>The dead one's funeral obligation, or null (no Ideology, classic mode, no funeral precept).</summary>
        public static (Precept_Ritual ritual, RitualObligation obligation) Funeral(Pawn dead)
        {
            if (!Customs.Active || dead.Ideo == null)
                return (null, null);
            foreach (var ritual in dead.Ideo.PreceptsListForReading.OfType<Precept_Ritual>())
                foreach (var obligation in ritual.activeObligations ?? new List<RitualObligation>())
                    if (obligation.targetA.Thing == dead && obligation.StillValid)
                        return (ritual, obligation);
            return (null, null);
        }

        public static IEnumerable<Gatherings.Line> Lines(Pawn pawn)
        {
            foreach (var corpse in Unburied(pawn.Map))
            {
                Pawn dead = corpse.InnerPawn;
                var (ritual, obligation) = Funeral(dead);
                yield return new Gatherings.Line
                {
                    key = "bury " + dead.ThingID,
                    label = ritual != null ? $"bury {dead.LabelShort} and hold {dead.gender.GetPossessive()} {ritual.def.label}" : $"bury {dead.LabelShort}",
                    faith = true,
                    due = true,
                    ticksLeft = obligation != null && obligation.expires ? obligation.TicksUntilExpiration : int.MaxValue,
                    apply = (her, say) => Instance?.Start(her, dead, say) ?? "Couldn't: no burial tracker.",
                };
            }
        }

        // ---------- The cemetery ----------

        private bool HasCemetery(Map map) => cemetery.IsValid && map.IsPlayerHome && cemetery.InBounds(map);

        /// <summary>Open, outdoor ground a grave can stand on, outside zones and doorways: where a grave may go.</summary>
        private static bool CellOk(IntVec3 c, Map map)
        {
            if (!c.InBounds(map) || !Ground.Open(c, map) || !c.Standable(map) || Ground.BesideDoor(c, map) || c.GetZone(map) != null)
                return false;
            var terrain = c.GetTerrain(map);
            if (terrain.IsWater || (ThingDefOf.Grave.terrainAffordanceNeeded != null && !terrain.affordances.Contains(ThingDefOf.Grave.terrainAffordanceNeeded)))
                return false;
            Room room = c.GetRoom(map);
            return room == null || room.PsychologicallyOutdoors;
        }

        /// <summary>A free grave cell (the grave's south end, facing south): beside the cemetery's graves, or a new cemetery's first.</summary>
        private IntVec3 GraveCell(Pawn pawn)
        {
            Map map = pawn.Map;
            if (!HasCemetery(map))
            {
                var zones = new ZoneSites(new ChoreScan(pawn), c => CellOk(c, map), c => 50);
                var site = zones.Find(new[] { 3, 5 }, 1).FirstOrDefault(); // room for a few graves
                if (site == null)
                    return IntVec3.Invalid;
                return Fits(site.center, map) ? site.center : IntVec3.Invalid;
            }
            // One empty cell between graves, so each can be reached and the rows read as a cemetery.
            return GenRadial.RadialCellsAround(cemetery, CemeteryRadius, true)
                .Where(c => Fits(c, map) && !GenAdj.OccupiedRect(c, Rot4.South, ThingDefOf.Grave.size).ExpandedBy(1).Cells.Any(n => n.InBounds(map) && HasGrave(n, map)))
                .OrderBy(c => c.DistanceToSquared(cemetery)).DefaultIfEmpty(IntVec3.Invalid).First();
        }

        private static bool Fits(IntVec3 c, Map map) =>
            GenAdj.OccupiedRect(c, Rot4.South, ThingDefOf.Grave.size).Cells.All(x => CellOk(x, map))
            && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Grave, c, Rot4.South, map).Accepted;

        private static bool HasGrave(IntVec3 c, Map map) =>
            c.GetThingList(map).Any(t => t.def == ThingDefOf.Grave || t.def.entityDefToBuild == ThingDefOf.Grave);

        // ---------- Starting and following a burial ----------

        private string Start(Pawn her, Pawn dead, string say)
        {
            if (dead.Corpse == null || !dead.Corpse.Spawned)
                return $"Couldn't: {dead.LabelShort} isn't lying anywhere to bury.";
            IntVec3 cell = GraveCell(her);
            if (!cell.IsValid)
                return "Couldn't: there's no open ground near the base for a grave.";
            GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Grave, cell, her.Map, Rot4.South, Faction.OfPlayer, null);
            if (!cemetery.IsValid || !HasCemetery(her.Map))
                cemetery = cell;
            burials.Add(new Burial { dead = dead, by = her, grave = cell });
            bool funeral = Funeral(dead).ritual != null;
            string what = funeral ? $"started {dead.LabelShort}'s funeral" : $"started burying {dead.LabelShort}";
            GroupChat.Instance?.Add(her.LabelShort, $"({what})" + (say != null ? $" {say}" : ""));
            return what.CapitalizeFirst() + ".";
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckTicks != 0 || burials.Count == 0)
                return;
            foreach (var burial in burials.ToList())
            {
                string done = Follow(burial);
                if (done != null)
                {
                    burials.Remove(burial);
                    ModLog.Message($"Burial of {burial.dead?.LabelShort}: {done}");
                }
            }
        }

        /// <summary>One step of a burial. Null while it goes on, or why it's over.</summary>
        private string Follow(Burial b)
        {
            Map map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && b.grave.InBounds(m));
            Corpse corpse = b.dead?.Corpse;
            if (map == null || b.dead == null || corpse == null || corpse.Destroyed)
                return "the body is gone";
            if (corpse.ParentHolder is Building_Grave)
                return StartFuneral(b, map);
            var grave = b.grave.GetFirstThing<Building_Grave>(map);
            if (grave == null)
                return HasGrave(b.grave, map) ? null : "the grave was cancelled";
            if (grave.HasCorpse)
                return "someone else is in that grave";
            var assign = grave.TryGetComp<CompAssignableToPawn>();
            if (assign != null && !assign.AssignedPawnsForReading.Contains(b.dead))
                assign.TryAssignPawn(b.dead); // the grave is hers: haulers bring this body and no other
            Pawn carrier = Carrier(b, map);
            if (carrier != null && corpse.Spawned && Find.TickManager.TicksGame - b.orderedTick >= ReorderTicks
                && carrier.CanReserveAndReach(corpse, PathEndMode.ClosestTouch, Danger.Deadly) && carrier.CanReach(grave, PathEndMode.InteractionCell, Danger.Deadly))
            {
                Job job = HaulAIUtility.HaulToContainerJob(carrier, corpse, grave); // vanilla's "prioritize burying"
                var mind = MindManager.Instance?.MindOf(carrier);
                if (mind != null ? MindActions.Order(mind, job) : carrier.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                    b.orderedTick = Find.TickManager.TicksGame;
            }
            return null;
        }

        /// <summary>Who carries the body: the one who chose this, while she's free; vanilla's haulers otherwise.</summary>
        private static Pawn Carrier(Burial b, Map map)
        {
            Pawn her = b.by;
            if (her == null || her.Dead || !her.Spawned || her.Map != map || her.Downed || her.InMentalState || her.Drafted || her.GetLord() != null)
                return null;
            if (MindManager.Instance?.MindOf(her) is PawnMind mind && mind.PausedReason(ignoreSleep: false) != null)
                return null;
            return her.CurJob?.def == JobDefOf.HaulToContainer && her.CurJob.targetA.Thing == b.dead.Corpse ? null : her;
        }

        /// <summary>The body is in the grave: the funeral, if the ideoligion has one, with her (or anyone who can) leading.</summary>
        private static string StartFuneral(Burial b, Map map)
        {
            var (ritual, obligation) = Funeral(b.dead);
            if (ritual == null)
                return "buried, no funeral";
            var leaders = new[] { b.by }.Concat(map.mapPawns.FreeColonistsSpawned).Where(p => p != null && p.Spawned && p.Map == map).Distinct().ToList(); // a copy: starting a ritual refreshes vanilla's cached list
            string why = null;
            foreach (var leader in leaders)
            {
                why = Gatherings.StartFuneral(leader, ritual, obligation);
                if (why == null)
                    return $"buried, {leader.LabelShort} leads the funeral";
            }
            return obligation.StillValid ? null : $"buried; the funeral ran out ({why})"; // try again next check
        }

        /// <summary>For the customs report: the cemetery and each burial under way.</summary>
        public string Describe(Map map) =>
            $"Cemetery: {(HasCemetery(map) ? cemetery.ToString() : "none")}. Unburied: {string.Join(", ", Unburied(map).Select(c => c.InnerPawn.LabelShort).DefaultIfEmpty("nobody"))}. " +
            $"Burials: {string.Join("; ", burials.Select(b => $"{b.dead?.LabelShort} by {b.by?.LabelShort} at {b.grave}, in grave: {b.dead?.Corpse?.ParentHolder is Building_Grave}").DefaultIfEmpty("none"))}.";

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref burials, "burials", LookMode.Deep);
            Scribe_Values.Look(ref cemetery, "cemetery", IntVec3.Invalid);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                burials = burials?.Where(b => b.dead != null).ToList() ?? new List<Burial>();
        }
    }
}
