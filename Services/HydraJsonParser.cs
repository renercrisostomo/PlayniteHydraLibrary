using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HydraPlaynite.Services
{
    public static class HydraJsonParser
    {
        public static Dictionary<string, string> ParseTopLevelJson(string json)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json))
                return dict;

            int i = 0;
            int n = json.Length;
            while (i < n && json[i] != '{') i++;
            i++;

            while (i < n)
            {
                SkipWhitespace(json, ref i, n);
                if (i >= n || json[i] == '}') break;
                if (json[i] == ',') { i++; continue; }

                if (json[i] != '"') break;
                string key = ReadJsonString(json, ref i, n);

                SkipWhitespace(json, ref i, n);
                if (i >= n || json[i] != ':') break;
                i++;

                SkipWhitespace(json, ref i, n);
                if (i >= n) break;

                char c = json[i];
                if (c == '"')
                {
                    dict[key] = ReadJsonString(json, ref i, n);
                }
                else if (c == '{' || c == '[')
                {
                    int structStart = i;
                    SkipNestedStructure(json, ref i, n);
                    dict[key] = json.Substring(structStart, i - structStart);
                }
                else
                {
                    int start = i;
                    while (i < n && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i]))
                        i++;

                    string rawVal = json.Substring(start, i - start);
                    if (!string.Equals(rawVal, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        dict[key] = rawVal;
                    }
                }
            }

            return dict;
        }

        private static void SkipWhitespace(string s, ref int i, int n)
        {
            while (i < n && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadJsonString(string s, ref int i, int n)
        {
            StringBuilder sb = new StringBuilder();
            i++;
            while (i < n)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < n)
                {
                    char esc = s[i++];
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
                            if (i + 4 <= n)
                            {
                                string hex = s.Substring(i, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static void SkipNestedStructure(string s, ref int i, int n)
        {
            int depth = 0;
            bool inStr = false;
            bool esc = false;
            while (i < n)
            {
                char c = s[i++];
                if (esc) { esc = false; continue; }
                if (c == '\\') { if (inStr) esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (!inStr)
                {
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth <= 0) break;
                    }
                }
            }
        }

        public static List<Dictionary<string, string>> ParseJsonArrayOfObjects(string jsonArray)
        {
            List<Dictionary<string, string>> items = new List<Dictionary<string, string>>();
            if (string.IsNullOrWhiteSpace(jsonArray))
                return items;

            int i = 0;
            int n = jsonArray.Length;
            while (i < n)
            {
                int start = jsonArray.IndexOf('{', i);
                if (start == -1) break;

                int scan = start;
                SkipNestedStructure(jsonArray, ref scan, n);
                if (scan > start)
                {
                    string objStr = jsonArray.Substring(start, scan - start);
                    Dictionary<string, string> parsed = ParseTopLevelJson(objStr);
                    if (parsed.Count > 0)
                    {
                        items.Add(parsed);
                    }
                    i = scan;
                }
                else
                {
                    i = start + 1;
                }
            }
            return items;
        }
    }
}
