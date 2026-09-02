using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityMcp
{
    /// <summary>
    /// 轻量 JSON 解析/序列化（自包含，零外部依赖）。
    /// 只覆盖 JSON-RPC 所需：对象、数组、字符串、数字(int/long/double)、布尔、null。
    /// 反序列化得到 Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / long / double / bool / null。
    /// </summary>
    public static class MiniJson
    {
        // ==================== 序列化 ====================

        public static string Serialize(object obj)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is string s)
            {
                WriteString(sb, s);
                return;
            }
            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }
            if (value is IDictionary dict)
            {
                WriteObject(sb, dict);
                return;
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                WriteArray(sb, enumerable);
                return;
            }
            if (value is int || value is long || value is short || value is byte)
            {
                sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is float f)
            {
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double d)
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is decimal m)
            {
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            }
            // 兜底：按字符串
            WriteString(sb, value.ToString());
        }

        private static void WriteObject(StringBuilder sb, IDictionary dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                sb.Append(':');
                WriteValue(sb, entry.Value);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable arr)
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

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        // ==================== 反序列化 ====================

        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return new Parser(json).Parse();
        }

        private sealed class Parser
        {
            private readonly string json;
            private int index;

            public Parser(string json)
            {
                this.json = json;
            }

            public object Parse()
            {
                SkipWhitespace();
                var value = ParseValue();
                SkipWhitespace();
                return value;
            }

            private object ParseValue()
            {
                SkipWhitespace();
                if (index >= json.Length) return null;
                switch (json[index])
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                index++; // '{'
                SkipWhitespace();
                if (Peek() == '}') { index++; return dict; }
                while (true)
                {
                    SkipWhitespace();
                    var key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':') throw new FormatException("JSON 对象缺冒号");
                    index++;
                    var value = ParseValue();
                    dict[key] = value;
                    SkipWhitespace();
                    var c = Peek();
                    if (c == ',') { index++; continue; }
                    if (c == '}') { index++; return dict; }
                    throw new FormatException("JSON 对象缺逗号或右括号");
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                index++; // '['
                SkipWhitespace();
                if (Peek() == ']') { index++; return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    var c = Peek();
                    if (c == ',') { index++; continue; }
                    if (c == ']') { index++; return list; }
                    throw new FormatException("JSON 数组缺逗号或右括号");
                }
            }

            private string ParseString()
            {
                index++; // 起始引号
                var sb = new StringBuilder();
                while (true)
                {
                    if (index >= json.Length) throw new FormatException("JSON 字符串未闭合");
                    var c = json[index++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (index >= json.Length) throw new FormatException("JSON 字符串未闭合");
                    var esc = json[index++];
                    switch (esc)
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
                            if (index + 4 > json.Length) throw new FormatException("JSON \\u 转义不完整");
                            var hex = json.Substring(index, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default: throw new FormatException("JSON 非法转义: " + esc);
                    }
                }
            }

            private object ParseNumber()
            {
                var start = index;
                while (index < json.Length && (char.IsDigit(json[index]) || json[index] == '-' || json[index] == '+' || json[index] == '.' || json[index] == 'e' || json[index] == 'E'))
                {
                    index++;
                }
                var token = json.Substring(start, index - start);
                if (token.IndexOf('.') >= 0 || token.IndexOf('e') >= 0 || token.IndexOf('E') >= 0)
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                }
                else
                {
                    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                }
                return null;
            }

            private char Peek()
            {
                SkipWhitespace();
                return index < json.Length ? json[index] : '\0';
            }

            private void SkipWhitespace()
            {
                while (index < json.Length && (json[index] == ' ' || json[index] == '\t' || json[index] == '\n' || json[index] == '\r'))
                {
                    index++;
                }
            }

            private void Expect(string literal)
            {
                if (json.Length - index < literal.Length) throw new FormatException("JSON 非法字面量");
                for (int i = 0; i < literal.Length; i++)
                {
                    if (json[index + i] != literal[i]) throw new FormatException("JSON 非法字面量");
                }
                index += literal.Length;
            }
        }
    }
}
