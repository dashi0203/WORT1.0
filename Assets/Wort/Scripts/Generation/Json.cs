using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wort.Generation
{
    /// <summary>
    /// 极简 JSON 读取：对象 → Dictionary&lt;string, object&gt;，数组 → List&lt;object&gt;，
    /// 数字 → double，字符串 → string，true/false → bool，null → null。
    /// 生成核心不引用 Unity，也不依赖 System.Text.Json（Unity 里没有）。
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            int p = 0;
            var v = Value(text, ref p);
            Skip(text, ref p);
            if (p != text.Length) throw Error(text, p, "多余的内容");
            return v;
        }

        static Exception Error(string s, int p, string msg)
        {
            int line = 1;
            for (int i = 0; i < p && i < s.Length; i++) if (s[i] == '\n') line++;
            return new FormatException($"JSON 第 {line} 行：{msg}");
        }

        static void Skip(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { p++; continue; }
                // 允许 // 行注释，方便在 layout 里写说明
                if (c == '/' && p + 1 < s.Length && s[p + 1] == '/')
                {
                    while (p < s.Length && s[p] != '\n') p++;
                    continue;
                }
                break;
            }
        }

        static object Value(string s, ref int p)
        {
            Skip(s, ref p);
            if (p >= s.Length) throw Error(s, p, "意外结束");
            char c = s[p];
            if (c == '{') return Obj(s, ref p);
            if (c == '[') return Arr(s, ref p);
            if (c == '"') return Str(s, ref p);
            if (c == 't' && Match(s, ref p, "true")) return true;
            if (c == 'f' && Match(s, ref p, "false")) return false;
            if (c == 'n' && Match(s, ref p, "null")) return null;
            return Num(s, ref p);
        }

        static bool Match(string s, ref int p, string word)
        {
            if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0) throw Error(s, p, "无法识别的值");
            p += word.Length;
            return true;
        }

        static Dictionary<string, object> Obj(string s, ref int p)
        {
            var d = new Dictionary<string, object>();
            p++;
            Skip(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return d; }
            while (true)
            {
                Skip(s, ref p);
                if (p >= s.Length || s[p] != '"') throw Error(s, p, "需要键名");
                string key = Str(s, ref p);
                Skip(s, ref p);
                if (p >= s.Length || s[p] != ':') throw Error(s, p, "需要冒号");
                p++;
                d[key] = Value(s, ref p);
                Skip(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == '}') { p++; return d; }
                throw Error(s, p, "需要逗号或 }");
            }
        }

        static List<object> Arr(string s, ref int p)
        {
            var l = new List<object>();
            p++;
            Skip(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return l; }
            while (true)
            {
                l.Add(Value(s, ref p));
                Skip(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == ']') { p++; return l; }
                throw Error(s, p, "需要逗号或 ]");
            }
        }

        static string Str(string s, ref int p)
        {
            var sb = new StringBuilder();
            p++;
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;
                char e = s[p++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw Error(s, p, "字符串没有结束");
        }

        static double Num(string s, ref int p)
        {
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            if (p == start) throw Error(s, p, "无法识别的值");
            return double.Parse(s.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
