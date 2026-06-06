using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Minimal, dependency-free JSON reader/writer. Parses into nested
    /// <see cref="Dictionary{TKey,TValue}"/> (object), <see cref="List{T}"/> (array), string, double, bool, and null.
    /// Used instead of JsonUtility/Newtonsoft because the project has neither Newtonsoft nor a fixed schema for
    /// the Move API MOTION_DATA payload (which contains arbitrary objects/arrays JsonUtility cannot represent).
    /// Not optimized for huge documents, but motion JSON for a single climb is small enough.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int index = 0;
            object value = ParseValue(json, ref index);
            return value;
        }

        // Typed convenience helpers ---------------------------------------------------------------

        public static Dictionary<string, object> AsObject(object node) => node as Dictionary<string, object>;
        public static List<object> AsArray(object node) => node as List<object>;

        public static bool TryGet(Dictionary<string, object> obj, string key, out object value)
        {
            value = null;
            return obj != null && obj.TryGetValue(key, out value);
        }

        public static double ToDouble(object node, double fallback = 0)
        {
            if (node is double d) return d;
            if (node is long l) return l;
            if (node is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return fallback;
        }

        public static float ToFloat(object node, float fallback = 0) => (float)ToDouble(node, fallback);

        public static string ToStr(object node) => node as string;

        // Parsing ---------------------------------------------------------------------------------

        static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                case 'f': return ParseBool(s, ref i);
                case 'n': i += 4; return null; // null
                default: return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }

                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object value = ParseValue(s, ref i);
                if (key != null) obj[key] = value;
            }
            return obj;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var arr = new List<object>();
            i++; // [
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                arr.Add(ParseValue(s, ref i));
            }
            return arr;
        }

        static string ParseString(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
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
                            if (i + 4 <= s.Length)
                            {
                                string hex = s.Substring(i, 4);
                                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        static object ParseBool(string s, ref int i)
        {
            if (s[i] == 't') { i += 4; return true; }
            i += 5; return false;
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            string num = s.Substring(start, i - start);
            if (double.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
            return 0d;
        }

        static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        // Writing ---------------------------------------------------------------------------------

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> obj: WriteObject(sb, obj); break;
                case IEnumerable<object> arr: WriteArray(sb, arr); break;
                default: WriteString(sb, value.ToString()); break;
            }
        }

        static void WriteObject(StringBuilder sb, IDictionary<string, object> obj)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable<object> arr)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in arr)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
        }

        static void WriteString(StringBuilder sb, string s)
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }
}
