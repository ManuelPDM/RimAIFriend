using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Rooms the game asks for (BASE_GROWTH.md §6.6 group 2): what vanilla's alerts name (a title's throne room, the
    /// ideoligion's altar, cribs for a baby, a deathrest casket, a prisoner's bed), offered first under "Other rooms". No
    /// priority over the ladder: the minds already see the alerts and decide. Where vanilla lists what the room must hold
    /// (RoomRequirement), the room is built to that list.
    /// </summary>
    public static class AskedFor
    {
        public class Ask
        {
            public RoomKindDef kind;
            public string why;           // "Wehner's title asks for a throne room"
            public Pawn who;             // whose it is (the throne's), or null
            public List<RoomItem> items; // what it holds, from vanilla's list
            public int minArea;
            public List<string> floorTags; // a floor with one of these tags on every cell (a throne room "all floored")
            public Precept_Building precept; // the ideo building's precept, so it's built in the ideoligion's style
            public List<string> alsoWants = new List<string>(); // requirements code doesn't build (impressiveness)

            /// <summary>Makes the kind hold this ask's items (the kinds asked for take their contents from here).</summary>
            public void Apply() => kind.items = items;
        }

        private static RoomKindDef Kind(string askedFor) => DefDatabase<RoomKindDef>.AllDefsListForReading.FirstOrDefault(k => k.askedFor == askedFor);

        /// <summary>What the last Current asked for but couldn't offer, and why (for the dev tools).</summary>
        public static readonly List<string> Dropped = new List<string>();

        /// <summary>Everything asked for on this map now, each buildable (its kind and items resolve).</summary>
        public static List<Ask> Current(Map map)
        {
            var asks = new List<Ask>();
            Dropped.Clear();
            var active = BuildManager.Instance?.ActiveOn(map).Select(p => p.kindDef).ToList() ?? new List<RoomKindDef>();
            void Add(Ask ask)
            {
                var missing = ask.items.Where(i => !i.optional && i.Resolve(map) == null).Select(i => i.need ?? string.Join("/", i.defs.Where(d => d != null).Select(d => d.defName))).ToList();
                string why = ask.kind == null ? "no room kind (DLC off?)" : active.Contains(ask.kind) ? "one is being built"
                    : missing.Count > 0 ? "can't build now (research, power, or costs not in storage): " + string.Join(", ", missing) : null;
                if (why == null)
                    asks.Add(ask);
                else
                    Dropped.Add($"{ask.why}: {why}");
            }
            var colonists = map.mapPawns.FreeColonistsSpawned;

            // A title that wants a throne room, and no throne assigned (vanilla's "needs a throne room").
            foreach (var pawn in colonists)
            {
                if (pawn.royalty == null || !pawn.royalty.CanRequireThroneroom() || pawn.ownership?.AssignedThrone != null)
                    continue;
                var title = pawn.royalty.HighestTitleWithThroneRoomRequirements();
                if (title == null)
                    continue;
                Add(FromRequirements(Kind("throne"), $"{pawn.LabelShort}'s title asks for a throne room", pawn, title.def.throneRoomRequirements, null));
                break; // one at a time
            }

            // The ideoligion wants a building it doesn't have (vanilla's "missing ideo building").
            if (ModsConfig.IdeologyActive)
                foreach (var ideo in Faction.OfPlayer.ideos.AllIdeos)
                    foreach (var precept in ideo.PreceptsListForReading.OfType<Precept_Building>())
                    {
                        var demand = precept.presenceDemand;
                        if (demand == null || !demand.AppliesTo(map) || demand.BuildingPresent(map) || precept.ThingDef == null
                            || !colonists.Any(p => !p.IsSlave && p.Ideo == ideo))
                            continue;
                        Add(FromRequirements(Kind("ideoBuilding"), $"{ideo.name} asks for {Find.ActiveLanguageWorker.WithIndefiniteArticle(precept.LabelCap.ToString())}",
                            null, demand.roomRequirements, new RoomItem { defs = { precept.ThingDef }, backToWall = true }, precept));
                    }

            // Babies with no crib (vanilla's "need baby cribs"): a nursery, cribs for every baby, 2 at least (its role needs 2).
            int babies = !ModsConfig.BiotechActive ? 0 : map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.PrisonersOfColonySpawned).Count(p => p.DevelopmentalStage.Baby());
            int cribs = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => Needs.Meets(Needs.Crib, b.def, null)).Sum(b => b.SleepingSlotsCount);
            if (babies > cribs)
                Add(new Ask
                {
                    kind = Kind("nursery"), why = babies == 1 ? "the baby has no crib" : $"{babies - cribs} babies have no crib",
                    items = new List<RoomItem> { new RoomItem { need = Needs.Crib, backToWall = true, repeat = Mathf.Max(2, babies - cribs), min = Mathf.Max(2, babies - cribs) } },
                });

            // Deathresters with no casket.
            var restless = ModsConfig.BiotechActive ? colonists.Where(p => p.needs?.TryGetNeed<Need_Deathrest>() != null).ToList() : new List<Pawn>();
            int caskets = restless.Count == 0 ? 0 : map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.DeathrestCasket).Count();
            if (restless.Count > caskets)
                Add(new Ask
                {
                    kind = Kind("deathrest"), why = $"{restless[caskets].LabelShort} needs somewhere to deathrest", who = restless[caskets],
                    items = new List<RoomItem> { new RoomItem { defs = { ThingDefOf.DeathrestCasket }, backToWall = true } },
                });

            // A prisoner with no prison bed.
            var prisoners = map.mapPawns.PrisonersOfColonySpawned.Where(p => !p.DevelopmentalStage.Baby()).ToList();
            int prisonBeds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => b.ForPrisoners && !b.Medical).Sum(b => b.SleepingSlotsCount);
            if (prisoners.Count > prisonBeds)
                Add(new Ask
                {
                    kind = Kind("prison"), why = $"{prisoners[prisonBeds].LabelShort} is held with no bed",
                    items = new List<RoomItem> { new RoomItem { need = Needs.Bed, backToWall = true, prisoner = true } },
                });
            return asks;
        }

        /// <summary>
        /// A room built to vanilla's requirement list: things (a throne, 2 braziers) become items, an area the size, "all
        /// floored" a floor. The rest code doesn't build (impressiveness) is said as it is. Forbidden things never come in
        /// anyway: the room only holds its own items.
        /// </summary>
        private static Ask FromRequirements(RoomKindDef kind, string why, Pawn who, List<RoomRequirement> requirements, RoomItem main, Precept_Building precept = null)
        {
            var ask = new Ask { kind = kind, why = why, who = who, precept = precept, items = new List<RoomItem>() };
            if (main != null)
                ask.items.Add(main);
            foreach (var r in requirements ?? new List<RoomRequirement>())
                switch (r)
                {
                    case RoomRequirement_ThingAnyOfCount c:
                        ask.items.Add(new RoomItem { defs = c.things.ToList(), backToWall = true, repeat = c.count, min = c.count });
                        break;
                    case RoomRequirement_AllThingsAnyOfAreGlowing _: // braziers are lit once fueled (vanilla refuels them)
                        break;
                    case RoomRequirement_ThingAnyOf a: // a throne (HasAssignedThroneAnyOf) or any one of them
                        ask.items.Insert(0, new RoomItem { defs = a.things.ToList(), backToWall = true });
                        break;
                    case RoomRequirement_ThingCount c:
                        ask.items.Add(new RoomItem { defs = { c.thingDef }, backToWall = true, repeat = c.count, min = c.count });
                        break;
                    case RoomRequirement_Thing t:
                        ask.items.Add(new RoomItem { defs = { t.thingDef }, backToWall = true });
                        break;
                    case RoomRequirement_Area a:
                        ask.minArea = Mathf.Max(ask.minArea, a.area);
                        break;
                    case RoomRequirement_TerrainWithTags t:
                        ask.floorTags = t.tags.ToList();
                        break;
                    case RoomRequirement_ForbiddenBuildings _:
                    case RoomRequirement_ForbidAltars _:
                    case RoomRequirement_AllThingsAreGlowing _:
                        break;
                    default:
                        ask.alsoWants.Add(r.Label());
                        break;
                }
            return ask;
        }

        /// <summary>
        /// Floor blueprints on the room's inside, after it's placed (a throne room "all floored"): the cheapest floor with
        /// one of the tags that vanilla lets go on this ground and whose cost the colony can pay. The floor, or null.
        /// </summary>
        public static TerrainDef LayFloor(Ask ask, BuildProject project)
        {
            if (ask.floorTags == null || project == null)
                return null;
            Map map = project.map;
            var cells = project.footprint.ContractedBy(1).Cells.ToList();
            TerrainDef floor = DefDatabase<TerrainDef>.AllDefsListForReading
                .Where(f => f.tags != null && f.tags.Any(ask.floorTags.Contains) && RoomKindDef.Buildable(f)
                            && (f.CostList ?? new List<ThingDefCountClass>()).All(c => Supplies.IsWallMaterial(c.thingDef) || map.resourceCounter.GetCount(c.thingDef) >= c.count * cells.Count)
                            && GenConstruct.CanPlaceBlueprintAt(f, cells[0], Rot4.North, map).Accepted)
                .OrderBy(f => f.CostList?.Sum(c => c.count * c.thingDef.BaseMarketValue) ?? 0f).FirstOrDefault();
            if (floor == null)
                return null;
            foreach (var c in cells)
                if (GenConstruct.CanPlaceBlueprintAt(floor, c, Rot4.North, map).Accepted)
                    GenConstruct.PlaceBlueprintForBuild(floor, c, map, Rot4.North, Faction.OfPlayer, null);
            return floor;
        }

        /// <summary>Its size: the kind's own, at least the area vanilla asks for.</summary>
        public static (int w, int h) SizeFor(Ask ask, Map map)
        {
            ask.Apply();
            var size = BaseCall.SizeFor(ask.kind, map);
            if (size.w * size.h >= ask.minArea)
                return size;
            var shape = SiteFinder.FitShapes.Where(s => s.w <= s.h && s.w * s.h >= ask.minArea).OrderBy(s => s.w * s.h).ThenBy(s => s.h - s.w).DefaultIfEmpty((w: 10, h: 10)).First(); // past 10×10: the biggest
            return (shape.w, shape.h);
        }
    }
}
