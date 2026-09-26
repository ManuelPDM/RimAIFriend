using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIPawnControl
{
    /// <summary>
    /// Minimal JSON reader/writer, so we don't bundle Newtonsoft (version clashes with other mods).
    /// Parse returns Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool or null.
    /// Write accepts those plus other numbers, IDictionary and IEnumerable.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var reader = new Reader(text);
            object value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
                throw new FormatException("Unexpected trailing characters at " + reader.Position);
            return value;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case int _:
                case long _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                    sb.Append('{');
                    bool firstPair = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!firstPair) sb.Append(',');
                        firstPair = false;
                        WriteString(sb, entry.Key.ToString());
                        sb.Append(':');
                        WriteValue(sb, entry.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private class Reader
        {
            private readonly string text;
            private int pos;

            public Reader(string text) { this.text = text ?? ""; }

            public int Position => pos;
            public bool AtEnd => pos >= text.Length;

            public void SkipWhitespace()
            {
                while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            }

            public object ReadValue()
            {
                SkipWhitespace();
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                char c = text[pos];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || char.IsDigit(c)) return ReadNumber();
                        throw new FormatException($"Unexpected '{c}' at {pos}");
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>();
                pos++; // {
                SkipWhitespace();
                if (Peek() == '}') { pos++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new FormatException("Expected property name at " + pos);
                    string key = ReadString();
                    SkipWhitespace();
                    if (Next() != ':') throw new FormatException("Expected ':' at " + (pos - 1));
                    result[key] = ReadValue();
                    SkipWhitespace();
                    char c = Next();
                    if (c == '}') return result;
                    if (c != ',') throw new FormatException("Expected ',' or '}' at " + (pos - 1));
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                pos++; // [
                SkipWhitespace();
                if (Peek() == ']') { pos++; return result; }
                while (true)
                {
                    result.Add(ReadValue());
                    SkipWhitespace();
                    char c = Next();
                    if (c == ']') return result;
                    if (c != ',') throw new FormatException("Expected ',' or ']' at " + (pos - 1));
                }
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                pos++; // opening quote
                while (true)
                {
                    char c = Next();
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = Next();
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > text.Length) throw new FormatException("Bad \\u escape at " + pos);
                            sb.Append((char)Convert.ToInt32(text.Substring(pos, 4), 16));
                            pos += 4;
                            break;
                        default: throw new FormatException($"Bad escape '\\{e}' at {pos - 1}");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = pos;
                while (pos < text.Length && "+-0123456789.eE".IndexOf(text[pos]) >= 0) pos++;
                return double.Parse(text.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0)
                    throw new FormatException($"Expected '{word}' at {pos}");
                pos += word.Length;
            }

            private char Peek() => AtEnd ? '\0' : text[pos];

            private char Next()
            {
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                return text[pos++];
            }
        }
    }
}
