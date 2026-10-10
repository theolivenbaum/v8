// Port of the parts of Python's eval() that tools/testrunner/local/statusfile.py
// relies on: status files are Python literals (lists, dicts, strings, bare
// keywords such as PASS), and their section conditions are Python boolean
// expressions over the build variables.
using System.Globalization;
using System.Text;

namespace V8Sharp.TestRunner.Status;

/// <summary>A tokenizer for the Python subset used by <c>.status</c> files.</summary>
internal sealed class PyLexer
{
    public enum Kind { Ident, String, Number, Punct, End }

    public readonly record struct Token(Kind Kind, string Text, int Line);

    readonly List<Token> _tokens = [];
    int _pos;

    public PyLexer(string source)
    {
        int i = 0, line = 1;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c) || c == '\\') { i++; continue; }
            if (c == '#')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c is '\'' or '"')
            {
                var sb = new StringBuilder();
                // Python concatenates adjacent string literals; the caller sees one token.
                while (true)
                {
                    char quote = source[i];
                    bool triple = i + 2 < source.Length && source[i + 1] == quote && source[i + 2] == quote;
                    i += triple ? 3 : 1;
                    while (true)
                    {
                        if (i >= source.Length) throw new FormatException($"unterminated string at line {line}");
                        char d = source[i];
                        if (triple ? (d == quote && i + 2 < source.Length && source[i + 1] == quote && source[i + 2] == quote) : d == quote)
                        {
                            i += triple ? 3 : 1;
                            break;
                        }
                        if (d == '\\' && i + 1 < source.Length)
                        {
                            char e = source[i + 1];
                            sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '\'' => '\'', '"' => '"', _ => e });
                            if (e is not ('n' or 't' or 'r' or '\\' or '\'' or '"')) sb.Insert(sb.Length - 1, '\\');
                            i += 2;
                            continue;
                        }
                        if (d == '\n') line++;
                        sb.Append(d);
                        i++;
                    }
                    int j = i;
                    while (j < source.Length && (source[j] is ' ' or '\t' or '\r' or '\n' or '\\'))
                    {
                        j++;
                    }
                    if (j < source.Length && source[j] is '\'' or '"')
                    {
                        for (int k = i; k < j; k++) if (source[k] == '\n') line++;
                        i = j;
                        continue;
                    }
                    break;
                }
                _tokens.Add(new Token(Kind.String, sb.ToString(), line));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                _tokens.Add(new Token(Kind.Ident, source[s..i], line));
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '.')) i++;
                _tokens.Add(new Token(Kind.Number, source[s..i], line));
                continue;
            }
            if (i + 1 < source.Length)
            {
                string two = source.Substring(i, 2);
                if (two is "==" or "!=" or "<=" or ">=")
                {
                    _tokens.Add(new Token(Kind.Punct, two, line));
                    i += 2;
                    continue;
                }
            }
            _tokens.Add(new Token(Kind.Punct, c.ToString(), line));
            i++;
        }
        _tokens.Add(new Token(Kind.End, "", line));
    }

    public Token Peek => _tokens[_pos];
    public Token PeekAt(int n) => _tokens[Math.Min(_pos + n, _tokens.Count - 1)];
    public Token Next() => _tokens[_pos++];

    public bool Accept(string punctOrKeyword)
    {
        var t = Peek;
        if ((t.Kind == Kind.Punct || t.Kind == Kind.Ident) && t.Text == punctOrKeyword)
        {
            _pos++;
            return true;
        }
        return false;
    }

    public void Expect(string punct)
    {
        if (!Accept(punct)) throw new FormatException($"expected '{punct}' but found '{Peek.Text}' at line {Peek.Line}");
    }
}

/// <summary>
/// Parses a status file into plain objects: <c>List&lt;object?&gt;</c>,
/// <c>List&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> for dicts (keeping
/// source order), <c>string</c>, <c>double</c>, <c>bool</c>. Bare identifiers
/// evaluate to their own name, which is what <c>eval(content, KEYWORDS)</c>
/// does for the status keywords (PASS, FAIL, SKIP, ALWAYS ...).
/// </summary>
internal static class PyLiteral
{
    public static object? Parse(string source)
    {
        var lx = new PyLexer(source);
        var v = ParseValue(lx);
        if (lx.Peek.Kind != PyLexer.Kind.End) throw new FormatException($"trailing content at line {lx.Peek.Line}");
        return v;
    }

    static object? ParseValue(PyLexer lx)
    {
        var t = lx.Next();
        switch (t.Kind)
        {
            case PyLexer.Kind.String: return t.Text;
            case PyLexer.Kind.Number: return double.Parse(t.Text, CultureInfo.InvariantCulture);
            case PyLexer.Kind.Ident:
                return t.Text switch { "True" => true, "False" => false, "None" => null, _ => t.Text };
            case PyLexer.Kind.Punct when t.Text is "[" or "(":
            {
                string close = t.Text == "[" ? "]" : ")";
                var list = new List<object?>();
                while (!lx.Accept(close))
                {
                    list.Add(ParseValue(lx));
                    if (!lx.Accept(",")) { lx.Expect(close); break; }
                }
                return list;
            }
            case PyLexer.Kind.Punct when t.Text == "{":
            {
                var dict = new List<KeyValuePair<string, object?>>();
                while (!lx.Accept("}"))
                {
                    var key = ParseValue(lx) as string ?? throw new FormatException($"dict key must be a string at line {t.Line}");
                    lx.Expect(":");
                    dict.Add(new(key, ParseValue(lx)));
                    if (!lx.Accept(",")) { lx.Expect("}"); break; }
                }
                return dict;
            }
            default:
                throw new FormatException($"unexpected '{t.Text}' at line {t.Line}");
        }
    }
}

/// <summary>Thrown when a condition references the <c>variant</c> identifier,
/// which statusfile.py evaluates once per variant.</summary>
internal sealed class VariantExpressionException : Exception
{
    public VariantExpressionException() : base("variant expression") { }
}

/// <summary>
/// Evaluates a status-file condition with Python semantics: <c>and</c>/<c>or</c>
/// return an operand, <c>not</c>, <c>==</c>, <c>!=</c>, <c>in</c>,
/// <c>not in</c>, tuples and lists, string and number literals.
/// </summary>
internal sealed class PyExpression
{
    readonly PyLexer _lx;
    readonly IReadOnlyDictionary<string, object?> _vars;

    PyExpression(string source, IReadOnlyDictionary<string, object?> vars)
    {
        _lx = new PyLexer(source);
        _vars = vars;
    }

    /// <summary>Evaluates <paramref name="source"/>. Throws
    /// <see cref="VariantExpressionException"/> if it needs the undefined
    /// <c>variant</c> identifier and <see cref="KeyNotFoundException"/> for any
    /// other undefined identifier (Python's NameError).</summary>
    public static object? Evaluate(string source, IReadOnlyDictionary<string, object?> vars)
    {
        var e = new PyExpression(source, vars);
        var v = e.Or();
        if (e._lx.Peek.Kind != PyLexer.Kind.End) throw new FormatException($"unexpected '{e._lx.Peek.Text}' in condition '{source}'");
        return v;
    }

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length != 0,
        List<object?> l => l.Count != 0,
        _ => true,
    };

    // While > 0, operands are parsed but not evaluated: Python's and/or
    // short-circuit, so 'x or variant == y' needs no variant when x holds.
    int _skip;

    object? Or()
    {
        var left = And();
        while (_lx.Accept("or"))
        {
            bool done = _skip == 0 && Truthy(left);
            if (done) _skip++;
            var right = And();
            if (done) _skip--;
            else left = right;
        }
        return left;
    }

    object? And()
    {
        var left = Not();
        while (_lx.Accept("and"))
        {
            bool done = _skip == 0 && !Truthy(left);
            if (done) _skip++;
            var right = Not();
            if (done) _skip--;
            else left = right;
        }
        return left;
    }

    object? Not()
    {
        if (_lx.Accept("not")) return !Truthy(Not());
        return Comparison();
    }

    object? Comparison()
    {
        var left = Primary();
        while (true)
        {
            if (_lx.Accept("==")) { left = PyEquals(left, Primary()); continue; }
            if (_lx.Accept("!=")) { left = !PyEquals(left, Primary()); continue; }
            if (_lx.Accept("in"))
            {
                var container = Primary();
                left = _skip > 0 ? null : Contains(container, left);
                continue;
            }
            if (_lx.Peek.Text == "not" && _lx.PeekAt(1).Text == "in")
            {
                _lx.Next(); _lx.Next();
                var container = Primary();
                left = _skip > 0 ? null : !Contains(container, left);
                continue;
            }
            return left;
        }
    }

    static bool Contains(object? container, object? item) => container switch
    {
        List<object?> l => l.Exists(x => PyEquals(x, item)),
        string s when item is string i => s.Contains(i, StringComparison.Ordinal),
        _ => throw new FormatException("'in' needs a list, tuple or string"),
    };

    public static bool PyEquals(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (bool x, bool y) => x == y,
        (bool x, double y) => (x ? 1 : 0) == y,
        (double x, bool y) => x == (y ? 1 : 0),
        (double x, double y) => x == y,
        (string x, string y) => x == y,
        _ => false,
    };

    object? Primary()
    {
        var t = _lx.Next();
        switch (t.Kind)
        {
            case PyLexer.Kind.String: return t.Text;
            case PyLexer.Kind.Number: return double.Parse(t.Text, CultureInfo.InvariantCulture);
            case PyLexer.Kind.Ident:
                switch (t.Text)
                {
                    case "True": return true;
                    case "False": return false;
                    case "None": return null;
                }
                if (_skip > 0) return null;
                if (_vars.TryGetValue(t.Text, out var v)) return v;
                if (t.Text == "variant") throw new VariantExpressionException();
                throw new KeyNotFoundException($"name '{t.Text}' is not defined");
            case PyLexer.Kind.Punct when t.Text is "(" or "[":
            {
                string close = t.Text == "(" ? ")" : "]";
                var items = new List<object?>();
                bool tuple = t.Text == "[";
                while (!_lx.Accept(close))
                {
                    items.Add(Or());
                    if (_lx.Accept(",")) { tuple = true; continue; }
                    _lx.Expect(close);
                    break;
                }
                // "(x)" is just x; "(x,)", "(x, y)" and "[...]" are sequences.
                return tuple || items.Count != 1 ? items : items[0];
            }
            default:
                throw new FormatException($"unexpected '{t.Text}' in condition");
        }
    }
}
