using System;
using System.Collections.Generic;
using System.IO;
using Verse;

namespace AIPawnControl
{
    /// <summary>Prefixed game-log wrapper plus the JSONL prompt/response log.</summary>
    public static class ModLog
    {
        private const string Prefix = "[AI Pawn Control] ";
        private static readonly object fileLock = new object();
        private static string logFolder;

        /// <summary>Call on the main thread at startup; GenFilePaths isn't safe to touch from background threads.</summary>
        public static void Init()
        {
            logFolder = Path.Combine(GenFilePaths.SaveDataFolderPath, "AIPawnControl");
        }

        public static void Message(string text) => Log.Message(Prefix + text);
        public static void Warning(string text) => Log.Warning(Prefix + text);
        public static void Error(string text) => Log.Error(Prefix + text);

        /// <summary>Appends one JSON line to prompts-YYYY-MM-DD.jsonl. Safe to call from any thread; never throws.</summary>
        public static void Prompt(Dictionary<string, object> entry)
        {
            if (logFolder == null || !AIPawnControlMod.Settings.logPrompts)
                return;
            try
            {
                entry["time"] = DateTime.Now.ToString("o");
                string line = Json.Write(entry) + "\n";
                lock (fileLock)
                {
                    Directory.CreateDirectory(logFolder);
                    File.AppendAllText(Path.Combine(logFolder, $"prompts-{DateTime.Now:yyyy-MM-dd}.jsonl"), line);
                }
            }
            catch (Exception e)
            {
                MainThread.Post(() => Warning("Couldn't write prompt log: " + e.Message));
            }
        }
    }
}
