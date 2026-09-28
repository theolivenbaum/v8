using System.Globalization;
using System.Text;

namespace V8Sharp.RegExp.Tests.Differential;

/// <summary>A regexp found in a JS source file.</summary>
internal readonly record struct CorpusPattern(string Source, string Flags);

/// <summary>
/// Extracts regular expression literals, RegExp("...", "...") constructor
/// arguments and string literals from JavaScript test files with a light
/// tokenizer (no full parse: good enough to build a corpus).
/// </summary>
internal static class JsCorpusScanner
{
    static readonly HashSet<string> s_regexAfterKeywords =
    [
        "return", "typeof", "case", "in", "of", "new", "delete", "void", "throw", "else", "do", "instanceof",
        "yield", "await",
    ];

    public static (List<CorpusPattern> Patterns, List<string> Strings) Scan(string src)
    {
        var patterns = new List<CorpusPattern>();
        var strings = new List<string>();
        int i = 0;
        // The previous significant token: 'i' identifier/keyword (with text),
        // 'n' number, 's' string, 'r' regexp, or the punctuator char.
        char prevKind = ';';
        string prevText = "";
        string prevPrevText = "";
        int n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                int start = i;
                string? s = ReadString(src, ref i, c);
                if (s is not null)
                {
                    strings.Add(s);
                    // RegExp("pattern"[, "flags"])
                    if (prevKind == '(' && prevPrevText == "RegExp")
                    {
                        string flags = "";
                        int j = i;
                        while (j < n && char.IsWhiteSpace(src[j])) j++;
                        if (j < n && src[j] == ',')
                        {
                            j++;
                            while (j < n && char.IsWhiteSpace(src[j])) j++;
                            if (j < n && (src[j] == '"' || src[j] == '\''))
                            {
                                int k = j;
                                string? f = ReadString(src, ref k, src[j]);
                                if (f is not null) flags = f;
                            }
                        }
                        patterns.Add(new CorpusPattern(s, flags));
                    }
                }
                prevPrevText = prevText;
                prevKind = 's';
                prevText = "";
                continue;
            }
            if (c == '`')
            {
                // Skip template literals (nested ${} handled shallowly).
                i++;
                int depth = 0;
                while (i < n)
                {
                    if (src[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (depth == 0 && src[i] == '`')
                    {
                        i++;
                        break;
                    }
                    if (src[i] == '$' && i + 1 < n && src[i + 1] == '{')
                    {
                        depth++;
                        i += 2;
                        continue;
                    }
                    if (depth > 0 && src[i] == '}') depth--;
                    i++;
                }
                prevKind = 's';
                prevPrevText = prevText;
                prevText = "";
                continue;
            }
            if (c == '/')
            {
                bool regexAllowed = prevKind switch
                {
                    'i' => s_regexAfterKeywords.Contains(prevText),
                    'n' or 's' or 'r' or ')' or ']' or '}' => false,
                    _ => true,
                };
                if (regexAllowed && TryReadRegExp(src, ref i, out string body, out string flags))
                {
                    patterns.Add(new CorpusPattern(body, flags));
                    prevKind = 'r';
                    prevPrevText = prevText;
                    prevText = "";
                    continue;
                }
                i++;
                prevKind = '/';
                prevPrevText = prevText;
                prevText = "/";
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$' || c == '\\')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$' || src[i] == '\\'))
                {
                    i++;
                }
                prevKind = 'i';
                prevPrevText = prevText;
                prevText = src[start..i];
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '.')) i++;
                prevKind = 'n';
                prevPrevText = prevText;
                prevText = "";
                continue;
            }
            prevKind = c;
            prevPrevText = prevText;
            prevText = c.ToString();
            i++;
        }
        return (patterns, strings);
    }

    static bool TryReadRegExp(string src, ref int i, out string body, out string flags)
    {
        body = "";
        flags = "";
        int j = i + 1;
        bool inClass = false;
        var sb = new StringBuilder();
        while (j < src.Length)
        {
            char c = src[j];
            if (c == '\n' || c == '\r') return false;
            if (c == '\\')
            {
                if (j + 1 >= src.Length) return false;
                sb.Append(c).Append(src[j + 1]);
                j += 2;
                continue;
            }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass) break;
            sb.Append(c);
            j++;
        }
        if (j >= src.Length) return false;
        if (sb.Length == 0) return false;  // "//" is a comment.
        j++;
        int flagsStart = j;
        while (j < src.Length && char.IsLetter(src[j])) j++;
        body = sb.ToString();
        flags = src[flagsStart..j];
        i = j;
        return true;
    }

    static string? ReadString(string src, ref int i, char quote)
    {
        var sb = new StringBuilder();
        int j = i + 1;
        while (j < src.Length)
        {
            char c = src[j];
            if (c == quote)
            {
                i = j + 1;
                return sb.ToString();
            }
            if (c == '\n')
            {
                i = j;
                return null;
            }
            if (c != '\\')
            {
                sb.Append(c);
                j++;
                continue;
            }
            if (j + 1 >= src.Length) break;
            char e = src[j + 1];
            j += 2;
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\r':
                    if (j < src.Length && src[j] == '\n') j++;
                    break;
                case '\n':
                case '\u2028':
                case '\u2029':
                    break;
                case 'x':
                    if (j + 2 <= src.Length && IsHex(src, j, 2))
                    {
                        sb.Append((char)int.Parse(src.AsSpan(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        j += 2;
                    }
                    else
                    {
                        sb.Append('x');
                    }
                    break;
                case 'u':
                    if (j < src.Length && src[j] == '{')
                    {
                        int close = src.IndexOf('}', j);
                        if (close < 0) return null;
                        if (!int.TryParse(src.AsSpan(j + 1, close - j - 1), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out int cp) || cp > 0x10FFFF)
                        {
                            return null;
                        }
                        sb.Append(char.ConvertFromUtf32(cp >= 0xD800 && cp <= 0xDFFF ? 0xFFFD : cp));
                        if (cp >= 0xD800 && cp <= 0xDFFF) sb[^1] = (char)cp;
                        j = close + 1;
                    }
                    else if (j + 4 <= src.Length && IsHex(src, j, 4))
                    {
                        sb.Append((char)int.Parse(src.AsSpan(j, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        j += 4;
                    }
                    else
                    {
                        sb.Append('u');
                    }
                    break;
                case >= '0' and <= '7':
                {
                    // Legacy octal escapes.
                    int v = e - '0';
                    int max = e <= '3' ? 2 : 1;
                    for (int k = 0; k < max && j < src.Length && src[j] >= '0' && src[j] <= '7'; k++, j++)
                    {
                        v = v * 8 + (src[j] - '0');
                    }
                    sb.Append((char)v);
                    break;
                }
                default:
                    sb.Append(e);
                    break;
            }
        }
        i = src.Length;
        return null;
    }

    static bool IsHex(string s, int at, int count)
    {
        for (int k = 0; k < count; k++)
        {
            if (!Uri.IsHexDigit(s[at + k])) return false;
        }
        return true;
    }
}
