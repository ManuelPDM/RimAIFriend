using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Inbox for work that must run on Unity's main thread (anything that touches game state).
    /// Background threads Post; a Root.Update postfix drains it every frame, including while paused or in menus.
    /// </summary>
    public static class MainThread
    {
        private static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();

        public static void Post(Action action) => queue.Enqueue(action);

        internal static void Drain()
        {
            if (LongEventHandler.ShouldWaitForEvent)
                return;
            while (queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    ModLog.Error("Main-thread work item failed: " + e);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class Patch_Root_Update
    {
        private static void Postfix() => MainThread.Drain();
    }
}
