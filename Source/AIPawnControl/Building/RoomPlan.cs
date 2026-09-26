using System.Collections.Generic;
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
        }
    }

    /// <summary>A planned room at one site: the footprint (walls included), reused walls, and every new thing to place.</summary>
    public class RoomPlan
    {
        public RoomKind kind;
        public Map map;
        public CellRect footprint;
        public IntVec3 door, doorOutside, doorInside;
        public HashSet<IntVec3> reusedWalls = new HashSet<IntVec3>();
        public List<PlanEntry> entries = new List<PlanEntry>();

        // Scoring and description inputs, filled by the site finder.
        public int walk;
        public int sharedSides;
        public int trees, plants, items;
        public bool inHome;
        public float score;
        public Dictionary<string, float> terms = new Dictionary<string, float>();

        public string TermsText()
        {
            var parts = new List<string>();
            foreach (var kv in terms)
                if (kv.Value != 0f)
                    parts.Add($"{kv.Key} {kv.Value:+0.#;-0.#}");
            return $"score {score:0.0} = " + string.Join(", ", parts);
        }

        public CellRect Interior => footprint.ContractedBy(1);
        public int InteriorSize => footprint.Width - 2;

        public PlanEntry Find(ThingDef def) => entries.Find(e => e.def == def);

        public IEnumerable<PlanEntry> Furniture
        {
            get
            {
                foreach (var e in entries)
                    if (e.def != ThingDefOf.Wall && e.def != ThingDefOf.Door)
                        yield return e;
            }
        }

        /// <summary>Walls and the door: cells a flood fill can't pass. Reused walls included.</summary>
        public bool IsRingSolid(IntVec3 c) => footprint.IsOnEdge(c) && (reusedWalls.Contains(c) || entries.Exists(e => e.cell == c && (e.def == ThingDefOf.Wall || e.def == ThingDefOf.Door)));

        /// <summary>The material each entry would use: the chosen one where the def allows it, else vanilla's default.</summary>
        public static ThingDef StuffFor(ThingDef def, ThingDef material)
        {
            if (!def.MadeFromStuff)
                return null;
            foreach (var allowed in GenStuff.AllowedStuffsFor(def))
                if (allowed == material)
                    return material;
            return GenStuff.DefaultStuffFor(def);
        }

        public void ApplyMaterial(ThingDef material)
        {
            foreach (var e in entries)
                e.stuff = StuffFor(e.def, material);
        }

        /// <summary>Total cost of every new entry in the given material.</summary>
        public Dictionary<ThingDef, int> Cost(ThingDef material)
        {
            var total = new Dictionary<ThingDef, int>();
            foreach (var e in entries)
                foreach (var part in e.def.CostListAdjusted(StuffFor(e.def, material)))
                {
                    total.TryGetValue(part.thingDef, out int n);
                    total[part.thingDef] = n + part.count;
                }
            return total;
        }
    }
}
