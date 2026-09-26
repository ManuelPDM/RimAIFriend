using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// OpenAI-compatible chat client (LM Studio). One request in flight across the whole mod; the rest wait.
    /// Call Send on the main thread (it snapshots settings); the callback also runs on the main thread.
    /// </summary>
    public static class LlmClient
    {
        // Backstop only: each request gets its own real-time CancelAfter from settings.
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private static readonly SemaphoreSlim inFlight = new SemaphoreSlim(1, 1);

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
                LlmResult result = await Run(request, url, bodyJson, timeoutSeconds);
                ModLog.Prompt(new Dictionary<string, object>
                {
                    ["id"] = request.Id,
                    ["callType"] = callType,
                    ["url"] = url,
                    ["seconds"] = Math.Round(result.Seconds, 2),
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

        private static async Task<LlmResult> Run(LlmRequest request, string url, string bodyJson, int timeoutSeconds)
        {
            var result = new LlmResult();
            var watch = Stopwatch.StartNew();
            bool acquired = false;
            try
            {
                await inFlight.WaitAsync(request.Cts.Token).ConfigureAwait(false);
                acquired = true;
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
                    inFlight.Release();
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
