using System.Collections.Generic;
using System.Linq;

namespace AIPawnControl
{
    /// <summary>Builds the JSON schemas the model's replies must follow (strict json_schema).</summary>
    public static class Schema
    {
        /// <summary>An object whose every property is required, and nothing else is allowed.</summary>
        public static Dictionary<string, object> Obj(Dictionary<string, object> props) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = props.Keys.Cast<object>().ToList(),
            ["additionalProperties"] = false,
        };

        public static Dictionary<string, object> Str() => new Dictionary<string, object> { ["type"] = "string" };

        public static Dictionary<string, object> Str(int maxLength) => new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = maxLength };

        public static Dictionary<string, object> StrEnum(IEnumerable<string> values) =>
            new Dictionary<string, object> { ["type"] = "string", ["enum"] = values.Cast<object>().ToList() };

        public static Dictionary<string, object> IntEnum(IEnumerable<int> values) =>
            new Dictionary<string, object> { ["type"] = "integer", ["enum"] = values.Cast<object>().ToList() };

        /// <summary>An enum with no type (Reflect's fields mix numbers and words).</summary>
        public static Dictionary<string, object> Enum(IEnumerable<object> values) => new Dictionary<string, object> { ["enum"] = values.ToList() };

        public static Dictionary<string, object> Arr(object items, int maxItems) =>
            new Dictionary<string, object> { ["type"] = "array", ["items"] = items, ["maxItems"] = maxItems };

        /// <summary>Caps rambling (a confused model once wrote 3,000 characters). Constrained decoding enforces it.</summary>
        public static Dictionary<string, object> Reason() => Str(400);

        /// <summary>Looser than the 160 characters we show, because maxLength cuts mid-sentence (SpeechLog.Clean trims to a sentence end).</summary>
        public static Dictionary<string, object> Say() => Str(220);

        /// <summary>A pick from a numbered list: 0 (none, or "never mind") up to count.</summary>
        public static Dictionary<string, object> Pick(int count) => IntEnum(Enumerable.Range(0, count + 1));
    }

    /// <summary>Reads a parsed reply (Json.Parse gives numbers as double). A missing or wrong-typed field gives the default.</summary>
    public static class Reply
    {
        public static int Int(this Dictionary<string, object> reply, string key, int missing = 0) =>
            reply.TryGetValue(key, out object v) && v is double d ? (int)d : missing;

        public static string Str(this Dictionary<string, object> reply, string key) =>
            reply.TryGetValue(key, out object v) ? v as string : null;

        public static Dictionary<string, object> Obj(this Dictionary<string, object> reply, string key) =>
            reply.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;

        /// <summary>The objects in an array field (anything else in it is skipped); empty if there's none.</summary>
        public static List<Dictionary<string, object>> Objects(this Dictionary<string, object> reply, string key) =>
            reply.TryGetValue(key, out object v) && v is List<object> list ? list.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();

        /// <summary>The numbers in an array field; empty if there's none.</summary>
        public static List<int> Ints(this Dictionary<string, object> reply, string key) =>
            reply.TryGetValue(key, out object v) && v is List<object> list ? list.OfType<double>().Select(d => (int)d).ToList() : new List<int>();
    }
}
