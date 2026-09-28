using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>One thing to place: a def at a cell with a rotation. Stuff is filled in once a material is chosen.</summary>
    public class PlanEntry : IExposable
    {
        public ThingDef def;
        public IntVec3 cell;
        public Rot4 rot;
        public ThingDef stuff;
        public bool medical; // a bed code sets to medical once built (hospital)
        public bool prisoner; // a bed code sets for prisoners once built (a prison cell)
        public Precept_Building precept; // an ideo building: placed in its ideoligion's style (BASE_GROWTH.md §6.6)

        public PlanEntry() { }

        public PlanEntry(ThingDef def, IntVec3 cell, Rot4 rot)
        {
            this.def = def;
            this.cell = cell;
            this.rot = rot;
        }

        public CellRect Rect => GenAdj.OccupiedRect(cell, rot, def.size);

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref rot, "rot");
            Scribe_Defs.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref medical, "medical");
            Scribe_Values.Look(ref prisoner, "prisoner");
            Scribe_References.Look(ref precept, "precept");
        }

        public PlanEntry Moved(IntVec3 by) => new PlanEntry(def, cell + by, rot) { stuff = stuff, medical = medical, prisoner = prisoner, precept = precept };
    }

    /// <summary>A planned room at one site: the footprint (walls included), reused walls, and every new thing to place.</summary>
    public class RoomPlan
    {
        public RoomKindDef kind;
        public Map map;
        public CellRect footprint;
        public IntVec3 door, doorOutside, doorInside;
        public HashSet<IntVec3> reusedWalls = new HashSet<IntVec3>();
        public List<IntVec3> broughtIn = new List<IntVec3>(); // existing doors in its ring that now open into it (a hub), or other doors of the room (an upgrade)
        public List<PlanEntry> entries = new List<PlanEntry>();

        // Scoring and description inputs, filled by the site finder.
        public int walk;
        public int sharedSides;
        public int trees, items;
        public float score;
        public int waysOutSaved; // a hub's
        public bool joined, apart; // its style (BASE_GROWTH.md §6.4)
        public List<string> near = new List<string>(); // rooms its goods flow to or from, nearest first: "the storeroom (3 tiles)"

        /// <summary>The cell inside each of its doors, its own first: nothing may stand there.</summary>
        public List<IntVec3> DoorsInside
        {
            get
            {
                var cells = new List<IntVec3> { doorInside };
                foreach (var d in broughtIn)
                    foreach (var dir in GenAdj.CardinalDirections)
                        if (Interior.Contains(d + dir))
                            cells.Add(d + dir);
                return cells;
            }
        }

        public CellRect Interior => footprint.ContractedBy(1);
        public int Width => footprint.Width - 2;
        public int Height => footprint.Height - 2;
        public string SizeLabel => $"{Width}×{Height}";

        public IEnumerable<PlanEntry> Furniture
        {
            get
            {
                foreach (var e in entries)
                    if (e.def != ThingDefOf.Wall && e.def != ThingDefOf.Door)
                        yield return e;
            }
        }

        /// <summary>A wall of the finished room, new or reused: what people can't walk through once it's built.</summary>
        public bool IsWall(IntVec3 c) => footprint.IsOnEdge(c) && (reusedWalls.Contains(c) || entries.Exists(e => e.cell == c && e.def != ThingDefOf.Door));

        /// <summary>The material each entry would use: the chosen one where the def allows it, else the allowed one storage has most of (leather for a couch when there's no cloth), else vanilla's default.</summary>
        public static ThingDef StuffFor(ThingDef def, ThingDef material, Map map = null)
        {
            if (!def.MadeFromStuff)
                return null;
            var allowed = GenStuff.AllowedStuffsFor(def).ToList();
            if (allowed.Contains(material))
                return material;
            var stocked = map == null ? null : allowed.Where(s => map.resourceCounter.GetCount(s) >= def.costStuffCount)
                .OrderByDescending(s => map.resourceCounter.GetCount(s)).FirstOrDefault();
            return stocked ?? GenStuff.DefaultStuffFor(def);
        }

        public void ApplyMaterial(ThingDef material)
        {
            foreach (var e in entries)
                e.stuff = StuffFor(e.def, material, map);
        }

        /// <summary>Total cost of every new entry in the given material.</summary>
        public Dictionary<ThingDef, int> Cost(ThingDef material)
        {
            var total = new Dictionary<ThingDef, int>();
            foreach (var e in entries)
                foreach (var part in e.def.CostListAdjusted(StuffFor(e.def, material, map)))
                {
                    total.TryGetValue(part.thingDef, out int n);
                    total[part.thingDef] = n + part.count;
                }
            return total;
        }
    }
}
