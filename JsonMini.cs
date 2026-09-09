using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MailboxReaderClr
{
    /// <summary>
    /// Minimal recursive-descent JSON parser with no dependencies beyond mscorlib/System.
    /// Exists specifically to avoid referencing System.Web.Extensions (JavaScriptSerializer)
    /// or any other assembly that SQL Server's CLR host doesn't auto-trust -- keeping
    /// MailboxReaderClr.dll as the ONLY assembly that needs CREATE ASSEMBLY registration.
    ///
    /// Objects parse to Dictionary&lt;string, object&gt;, arrays to List&lt;object&gt;,
    /// strings to string, numbers to double, booleans to bool, null to null.
    /// </summary>
    internal static class JsonMini
    {
        public static object Parse(string json)
        {
            int i = 0;
            SkipWhitespace(json, ref i);
            object value = ParseValue(json, ref i);
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>();
            i++; // consume '{'
            SkipWhitespace(s, ref i);
            if (s[i] == '}')
            {
                i++;
                return dict;
            }
            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (s[i] != ':')
                {
                    throw new FormatException("Expected ':' in JSON object at position " + i);
                }
                i++;
                object value = ParseValue(s, ref i);
                dict[key] = value;
                SkipWhitespace(s, ref i);
                if (s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (s[i] == '}')
                {
                    i++;
                    break;
                }
                throw new FormatException("Expected ',' or '}' in JSON object at position " + i);
            }
            return dict;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // consume '['
            SkipWhitespace(s, ref i);
            if (s[i] == ']')
            {
                i++;
                return list;
            }
            while (true)
            {
                object value = ParseValue(s, ref i);
                list.Add(value);
                SkipWhitespace(s, ref i);
                if (s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (s[i] == ']')
                {
                    i++;
                    break;
                }
                throw new FormatException("Expected ',' or ']' in JSON array at position " + i);
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (s[i] != '"')
            {
                throw new FormatException("Expected '\"' at position " + i);
            }
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i];
                if (c == '\\')
                {
                    i++;
                    char esc = s[i];
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
                            string hex = s.Substring(i + 1, 4);
                            int code = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default:
                            throw new FormatException("Unrecognized escape sequence \\" + esc);
                    }
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            i++; // consume closing quote
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            {
                i++;
            }
            string numStr = s.Substring(start, i - start);
            double d;
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
            throw new FormatException("Invalid number '" + numStr + "' at position " + start);
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
            {
                throw new FormatException("Expected literal '" + literal + "' at position " + i);
            }
            i += literal.Length;
        }
    }
}
