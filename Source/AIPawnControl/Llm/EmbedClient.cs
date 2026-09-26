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
    public class EmbedResult
    {
        public List<float[]> Vectors; // shaped and unit-length, in input order
        public string Tag;            // Embedding.Tag of every vector here
        public int NativeDims;        // the model's own size, before any cut
        public string Error;
        public double Seconds;

        public bool Ok => Error == null;
    }

    /// <summary>
    /// OpenAI-compatible /embeddings client (PHASE3.md §5). A plain async request with a real-time timeout; it doesn't
    /// wait behind LlmClient's one-in-flight lock, because embedding calls are short and may go to another server.
    /// Call Embed on the main thread (it reads settings); the callback runs on the main thread too.
    /// </summary>
    public static class EmbedClient
    {
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        /// <param name="query">Search queries and stored memories get different prefixes on gemma and nomic.</param>
        public static void Embed(List<string> texts, bool query, Action<EmbedResult> onResult)
        {
            var settings = AIPawnControlMod.Settings;
            string model = settings.embedModel;
            string url = settings.EmbedEndpoint.TrimEnd('/') + "/embeddings";
            int timeoutSeconds = settings.timeoutSeconds;
            string prefix = Embedding.Prefix(model, query);
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["input"] = texts.Select(t => (object)(prefix + t)).ToList(),
            };
            string bodyJson = Json.Write(body);

            Task.Run(async () =>
            {
                EmbedResult result = await Run(model, url, bodyJson, texts.Count, timeoutSeconds);
                ModLog.Prompt(new Dictionary<string, object>
                {
                    ["callType"] = query ? "embed-query" : "embed",
                    ["url"] = url,
                    ["model"] = model,
                    ["seconds"] = Math.Round(result.Seconds, 2),
                    ["input"] = body["input"],
                    ["nativeDims"] = result.NativeDims,
                    ["tag"] = result.Tag,
                    ["error"] = result.Error,
                });
                MainThread.Post(() => onResult(result));
            });
        }

        private static async Task<EmbedResult> Run(string model, string url, string bodyJson, int count, int timeoutSeconds)
        {
            var result = new EmbedResult();
            var watch = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                    using (var response = await http.PostAsync(url, content, cts.Token).ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            result.Error = $"HTTP {(int)response.StatusCode}: {(text.Length > 300 ? text.Substring(0, 300) + "…" : text)}";
                        else
                            Parse(model, text, count, result);
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Error = $"Timed out after {timeoutSeconds}s";
                }
                catch (Exception e)
                {
                    string message = (e.InnerException ?? e).Message;
                    result.Error = new string(message.Where(c => !char.IsControl(c)).ToArray()).Trim(); // Mono pads socket errors with NULs
                }
            }
            result.Seconds = watch.Elapsed.TotalSeconds;
            return result;
        }

        private static void Parse(string model, string text, int count, EmbedResult result)
        {
            try
            {
                var root = (Dictionary<string, object>)Json.Parse(text);
                var data = ((List<object>)root["data"]).Cast<Dictionary<string, object>>()
                    .OrderBy(d => d.TryGetValue("index", out object i) && i is double n ? n : 0).ToList();
                if (data.Count != count)
                {
                    result.Error = $"Asked for {count} vectors, got {data.Count}";
                    return;
                }
                result.Vectors = new List<float[]>();
                foreach (var item in data)
                {
                    var raw = ((List<object>)item["embedding"]).Select(x => (float)(double)x).ToArray();
                    result.NativeDims = raw.Length;
                    result.Vectors.Add(Embedding.Shape(model, raw));
                }
                if (result.NativeDims == 0)
                    result.Error = "The reply had empty vectors (is this an embedding model?)";
                else
                    result.Tag = Embedding.Tag(model, result.Vectors[0].Length);
            }
            catch (Exception e)
            {
                result.Error = "Unreadable reply (is this an embedding model?): " + e.Message;
            }
        }
    }
}
