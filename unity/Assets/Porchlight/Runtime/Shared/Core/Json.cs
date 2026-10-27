// A small JSON reader and writer: objects become Dictionary<string, object>,
// arrays List<object>, numbers double, plus string, bool and null. Enough for
// glTF headers and the server's HTTP answers, with no engine dependency.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Porchlight.Core
{
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            var v = Value(text, ref i);
            Skip(text, ref i);
            if (i != text.Length) throw new FormatException($"unexpected text at {i}");
            return v;
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var o = new Dictionary<string, object>();
                Skip(s, ref i);
                if (s[i] == '}')
                {
                    i++;
                    return o;
                }
                for (;;)
                {
                    Skip(s, ref i);
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    if (s[i] != ':') throw new FormatException($"expected : at {i}");
                    i++;
                    o[key] = Value(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    if (s[i] == '}')
                    {
                        i++;
                        return o;
                    }
                    throw new FormatException($"expected , or }} at {i}");
                }
            }
            if (c == '[')
            {
                i++;
                var a = new List<object>();
                Skip(s, ref i);
                if (s[i] == ']')
                {
                    i++;
                    return a;
                }
                for (;;)
                {
                    a.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    if (s[i] == ']')
                    {
                        i++;
                        return a;
                    }
                    throw new FormatException($"expected , or ] at {i}");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0)
            {
                i += 4;
                return true;
            }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0)
            {
                i += 5;
                return false;
            }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0)
            {
                i += 4;
                return null;
            }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException($"unexpected character '{c}' at {i}");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException($"expected string at {i}");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
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
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"bad escape at {i}");
                }
            }
            throw new FormatException("unterminated string");
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: sb.Append(Numbers.NumberToString(d)); break;
                case float f: sb.Append(Numbers.NumberToString(f)); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> o:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case IEnumerable e:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var x in e)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, x);
                    }
                    sb.Append(']');
                    break;
                }
                default: WriteString(sb, v.ToString()); break;
            }
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
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Typed access helpers for parsed values.

        public static Dictionary<string, object> Obj(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static List<object> Arr(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as List<object> : null;

        public static double Num(object o, string key, double fallback = double.NaN) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is double n ? n : fallback;

        public static string Str(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as string : null;

        public static bool Has(object o, string key) => o is Dictionary<string, object> d && d.ContainsKey(key);
    }
}
