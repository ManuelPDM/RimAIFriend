using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIPawnControl
{
    public class LlmResult
    {
        public string Content;
        public string Reasoning;
        public string Error;
        public double Seconds;
        public double WaitedSeconds; // in the queue, behind other requests
        public int CompletionTokens;

        public bool Ok => Error == null;
    }

    /// <summary>Handle for one request. Cancelling guarantees its callback never runs.</summary>
    public class LlmRequest
    {
        private static int nextId;

        public readonly int Id = Interlocked.Increment(ref nextId);
        public readonly string CallType;
        internal readonly CancellationTokenSource Cts = new CancellationTokenSource();
        private int cancelled;

        public LlmRequest(string callType) { CallType = callType; }

        public bool IsCancelled => Volatile.Read(ref cancelled) == 1;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref cancelled, 1) == 0)
                Cts.Cancel();
        }
    }

    /// <summary>
    /// OpenAI-compatible chat client (LM Studio). "Parallel requests" run at once across the whole mod (default 1); the
    /// rest wait in a priority queue (PHASE6.md §6): the player's chat first, Reflect last.
    /// Call Send on the main thread (it snapshots settings); the callback also runs on the main thread.
    /// </summary>
    public static class LlmClient
    {
        // Backstop only: each request gets its own real-time CancelAfter from settings.
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        private class Waiter
        {
            public int priority;
            public long order;
            public string callType;
            public Stopwatch since;
            public TaskCompletionSource<bool> ready;
        }

        private static readonly object gate = new object();
        private static readonly List<Waiter> waiting = new List<Waiter>();
        private static int running;
        private static long nextOrder;

        /// <summary>Lower goes first: a conversation never waits behind background work.</summary>
        private static int Priority(string callType)
        {
            switch (callType)
            {
                case "chat": return 0;
                case "reply": return 1;
                case "base": return 3;
                case "persona": return 4;
                case "reflect": return 5;
                default: return 2; // act, dev tests
            }
        }

        private static Task Acquire(string callType, int slots, CancellationToken token)
        {
            lock (gate)
            {
                if (running < slots && waiting.Count == 0)
                {
                    running++;
                    return Task.CompletedTask;
                }
                var waiter = new Waiter { priority = Priority(callType), order = nextOrder++, callType = callType, since = Stopwatch.StartNew(),
                                          ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
                waiting.Add(waiter);
                token.Register(() =>
                {
                    lock (gate)
                        if (!waiting.Remove(waiter))
                            return; // already let through
                    waiter.ready.TrySetCanceled();
                });
                return waiter.ready.Task;
            }
        }

        private static void Release()
        {
            lock (gate)
            {
                running--;
                LetThrough();
            }
        }

        /// <summary>Starts the most urgent waiters while there are free slots. Holds the gate.</summary>
        private static void LetThrough()
        {
            int slots = Math.Max(1, AIPawnControlMod.Settings.parallelRequests);
            while (running < slots && waiting.Count > 0)
            {
                Waiter next = waiting[0];
                foreach (var w in waiting)
                    if (w.priority < next.priority || (w.priority == next.priority && w.order < next.order))
                        next = w;
                waiting.Remove(next);
                running++;
                next.ready.TrySetResult(true);
            }
        }

        /// <summary>Dev "Queue status": running and waiting requests, by call type and how long they've waited.</summary>
        public static string QueueStatus()
        {
            lock (gate)
                return $"{running} running (slots: {Math.Max(1, AIPawnControlMod.Settings.parallelRequests)}), {waiting.Count} waiting" +
                       (waiting.Count > 0 ? ": " + string.Join(", ", waiting.OrderBy(w => w.priority).ThenBy(w => w.order)
                           .Select(w => $"{w.callType} {w.since.Elapsed.TotalSeconds:0}s")) : "");
        }

        /// <summary>The settings' "Test connection": one short free-text reply.</summary>
        public static void Ping(Action<LlmResult> onResult) =>
            Send("ping", new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("user", "Reply with one short friendly sentence.") }, null, onResult);

        /// <param name="messages">(role, content) pairs.</param>
        /// <param name="schema">JSON schema object for structured output, or null for free text.</param>
        public static LlmRequest Send(string callType, List<KeyValuePair<string, string>> messages, object schema, Action<LlmResult> onResult) =>
            Send(callType, messages, schema, 0, onResult);

        /// <param name="maxTokens">The reply budget; 0 = the setting.</param>
        public static LlmRequest Send(string callType, List<KeyValuePair<string, string>> messages, object schema, int maxTokens, Action<LlmResult> onResult)
        {
            var settings = AIPawnControlMod.Settings;
            if (maxTokens <= 0)
                maxTokens = settings.maxTokens;
            var request = new LlmRequest(callType);
            string url = settings.endpoint.TrimEnd('/') + "/chat/completions";
            int timeoutSeconds = settings.timeoutSeconds;
            int slots = Math.Max(1, settings.parallelRequests);

            var messageList = new List<object>();
            foreach (var m in messages)
                messageList.Add(new Dictionary<string, object> { ["role"] = m.Key, ["content"] = m.Value });

            var body = new Dictionary<string, object>
            {
                ["model"] = settings.model,
                ["messages"] = messageList,
                ["temperature"] = settings.temperature,
                ["max_tokens"] = settings.thinking ? maxTokens * 4 : maxTokens,
                ["stream"] = false,
            };
            // Only reasoning_effort=none turns Qwen thinking off in LM Studio (/no_think and enable_thinking are ignored).
            if (!settings.thinking)
                body["reasoning_effort"] = "none";
            if (schema != null)
            {
                body["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object> { ["name"] = callType, ["strict"] = true, ["schema"] = schema },
                };
            }
            string bodyJson = Json.Write(body);

            Task.Run(async () =>
            {
                LlmResult result = await Run(request, url, bodyJson, timeoutSeconds, slots);
                ModLog.Prompt(new Dictionary<string, object>
                {
                    ["id"] = request.Id,
                    ["callType"] = callType,
                    ["url"] = url,
                    ["seconds"] = Math.Round(result.Seconds, 2),
                    ["waited"] = Math.Round(result.WaitedSeconds, 2),
                    ["request"] = body,
                    ["content"] = result.Content,
                    ["reasoning"] = result.Reasoning,
                    ["completionTokens"] = result.CompletionTokens,
                    ["error"] = result.Error,
                    ["cancelled"] = request.IsCancelled,
                });
                MainThread.Post(() =>
                {
                    if (!request.IsCancelled)
                        onResult(result);
                });
            });
            return request;
        }

        private static async Task<LlmResult> Run(LlmRequest request, string url, string bodyJson, int timeoutSeconds, int slots)
        {
            var result = new LlmResult();
            var watch = Stopwatch.StartNew();
            bool acquired = false;
            try
            {
                await Acquire(request.CallType, slots, request.Cts.Token).ConfigureAwait(false);
                acquired = true;
                result.WaitedSeconds = watch.Elapsed.TotalSeconds;
                watch.Restart(); // don't count time spent waiting behind another request
                request.Cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                using (var response = await http.PostAsync(url, content, request.Cts.Token).ConfigureAwait(false))
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        result.Error = $"HTTP {(int)response.StatusCode}: {Truncate(text, 300)}";
                        return result;
                    }
                    ParseCompletion(text, result);
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = request.IsCancelled ? "Cancelled" : $"Timed out after {timeoutSeconds}s";
            }
            catch (Exception e)
            {
                result.Error = (e.InnerException ?? e).Message.Replace("\r", "").Replace("\n", " ").Trim();
            }
            finally
            {
                if (acquired)
                    Release();
                result.Seconds = watch.Elapsed.TotalSeconds;
            }
            return result;
        }

        private static void ParseCompletion(string text, LlmResult result)
        {
            try
            {
                var root = (Dictionary<string, object>)Json.Parse(text);
                var choice = (Dictionary<string, object>)((List<object>)root["choices"])[0];
                var message = (Dictionary<string, object>)choice["message"];
                result.Content = message.TryGetValue("content", out var c) ? c as string : null;
                if (message.TryGetValue("reasoning_content", out var r) || message.TryGetValue("reasoning", out r))
                    result.Reasoning = r as string;
                if (root.TryGetValue("usage", out var u) && u is Dictionary<string, object> usage
                    && usage.TryGetValue("completion_tokens", out var tokens) && tokens is double t)
                    result.CompletionTokens = (int)t;
                if (string.IsNullOrWhiteSpace(result.Content))
                    result.Error = "Empty reply (the model may have spent its whole token budget thinking)";
            }
            catch (Exception e)
            {
                result.Error = "Unreadable reply: " + e.Message + " — " + Truncate(text, 300);
            }
        }

        private static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
