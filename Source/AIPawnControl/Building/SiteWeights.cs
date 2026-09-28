using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AIPawnControl
{
    /// <summary>
    /// Site-scoring weights and limits from Tuning/building.txt (`key = value  # comment`), re-read on every scan so
    /// tuning needs no rebuild. Missing keys keep the defaults below.
    /// </summary>
    public class SiteWeights
    {
        public int maxWalk = 60;
        public float walk = -1f;            // per tile from the base centre to the door
        public float sharedWallCell = 1f;   // per reused wall cell
        public float sharedSide = 4f;       // per side with reused walls (a notch wins)
        public float alignedSide = 1.5f;    // per side continuing an existing wall line within 10 cells
        public float sliverCell = -1f;      // per ring cell with a 1-cell gap to an existing wall
        public float fertility = -0.5f;     // per cell, per 0.1 fertility above plain soil's 1.0
        public float shelter = 0.2f;        // per tile farther from the map edge than the base centre (±15 max)
        public float nearEdge = -1f;        // per tile closer than 15 to the map edge
        public float tree = -2f;
        public float item = -0.5f;
        public float home = 5f;
        public float bigRoom = 4f;          // the 5×5 size
        public float insideDoor = 8f;       // per door opening inside the base instead of outdoors (a new wall costs 1)
        public int outdoorWalk = 20;        // most tiles from any room's door to the outdoors once doors come inside

        public static string FilePath => Path.Combine(AIPawnControlMod.RootDir, "Tuning", "building.txt");

        public static SiteWeights Load()
        {
            var weights = new SiteWeights();
            string path = FilePath;
            if (!File.Exists(path))
                return weights;
            var fields = new Dictionary<string, System.Reflection.FieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in typeof(SiteWeights).GetFields())
                fields[f.Name] = f;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line.Substring(0, hash);
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (!fields.TryGetValue(key, out var field) || !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    ModLog.Warning($"Tuning/building.txt: ignored line \"{raw.Trim()}\"");
                    continue;
                }
                field.SetValue(weights, field.FieldType == typeof(int) ? (object)(int)v : v);
            }
            return weights;
        }
    }
}
