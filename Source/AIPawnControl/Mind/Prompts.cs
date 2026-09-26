using System.Collections.Generic;
using System.IO;

namespace AIPawnControl
{
    /// <summary>
    /// Runtime-editable prompt templates from the mod's Prompts/ folder. {placeholders} are filled by Fill.
    /// Cached after the first read; "Reload prompts" in settings clears the cache.
    /// </summary>
    public static class Prompts
    {
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>();

        public static string Folder => Path.Combine(AIPawnControlMod.RootDir, "Prompts");

        public static void Reload() => cache.Clear();

        public static string Fill(string name, Dictionary<string, string> values)
        {
            if (!cache.TryGetValue(name, out string template))
            {
                template = File.ReadAllText(Path.Combine(Folder, name + ".txt"));
                cache[name] = template;
            }
            foreach (var pair in values)
                template = template.Replace("{" + pair.Key + "}", pair.Value ?? "");
            return template.Trim();
        }
    }
}
