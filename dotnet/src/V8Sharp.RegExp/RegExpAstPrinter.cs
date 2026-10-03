// Port of src/regexp/regexp-ast-printer.cc (AstNodePrinter, without the graph
// labeller) and the AsUC16/AsUC32 stream helpers of src/utils/ostreams.cc.

using System.Globalization;
using System.Text;

namespace V8Sharp.RegExp;

public sealed class RegExpAstPrinter : IRegExpVisitor
{
    readonly StringBuilder _os = new();

    public static string Print(RegExpTree tree)
    {
        var printer = new RegExpAstPrinter();
        tree.Accept(printer, null);
        return printer._os.ToString();
    }

    static bool IsPrint(int c) => 0x20 <= c && c <= 0x7E;

    // operator<<(std::ostream&, const AsUC16&).
    internal static void AppendUC16(StringBuilder os, int c)
    {
        if (IsPrint(c))
        {
            os.Append((char)c);
        }
        else if (c <= 0xFF)
        {
            os.Append("\\x").Append(c.ToString("x2", CultureInfo.InvariantCulture));
        }
        else
        {
            os.Append("\\u").Append(c.ToString("x4", CultureInfo.InvariantCulture));
        }
    }

    // operator<<(std::ostream&, const AsUC32&).
    internal static void AppendUC32(StringBuilder os, int c)
    {
        if (c <= 0xFFFF)
        {
            AppendUC16(os, c);
            return;
        }
        os.Append("\\u{").Append(c.ToString("x6", CultureInfo.InvariantCulture)).Append('}');
    }

    void VisitCharacterRange(CharacterRange that)
    {
        AppendUC32(_os, that.From);
        if (!that.IsSingleton)
        {
            _os.Append('-');
            AppendUC32(_os, that.To);
        }
    }

    public object? VisitDisjunction(RegExpDisjunction that, object? data)
    {
        _os.Append("(|");
        for (int i = 0; i < that.Alternatives.Count; i++)
        {
            _os.Append(' ');
            that.Alternatives[i].Accept(this, data);
        }
        _os.Append(')');
        return null;
    }

    public object? VisitAlternative(RegExpAlternative that, object? data)
    {
        _os.Append("(:");
        for (int i = 0; i < that.Nodes.Count; i++)
        {
            _os.Append(' ');
            that.Nodes[i].Accept(this, data);
        }
        _os.Append(')');
        return null;
    }

    public object? VisitClassRanges(RegExpClassRanges that, object? data)
    {
        if (that.IsNegated) _os.Append('^');
        _os.Append('[');
        List<CharacterRange> ranges = that.Ranges;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (i > 0) _os.Append(' ');
            VisitCharacterRange(ranges[i]);
        }
        _os.Append(']');
        return null;
    }

    public object? VisitClassSetOperand(RegExpClassSetOperand that, object? data)
    {
        _os.Append("![");
        for (int i = 0; i < that.Ranges.Count; i++)
        {
            if (i > 0) _os.Append(' ');
            VisitCharacterRange(that.Ranges[i]);
        }
        if (that.HasStrings)
        {
            foreach (KeyValuePair<int[], RegExpTree> iter in that.Strings)
            {
                _os.Append(" '");
                // std::string(begin, end) narrows each code point to a char.
                foreach (int c in iter.Key) _os.Append((char)(byte)c);
                _os.Append('\'');
            }
        }
        _os.Append(']');
        return null;
    }

    public object? VisitClassSetExpression(RegExpClassSetExpression that, object? data)
    {
        switch (that.Operation)
        {
            case RegExpClassSetExpression.OperationType.kUnion:
                _os.Append("++");
                break;
            case RegExpClassSetExpression.OperationType.kIntersection:
                _os.Append("&&");
                break;
            case RegExpClassSetExpression.OperationType.kSubtraction:
                _os.Append("--");
                break;
        }
        if (that.IsNegated) _os.Append('^');
        _os.Append('[');
        for (int i = 0; i < that.Operands.Count; i++)
        {
            if (i > 0) _os.Append(' ');
            that.Operands[i].Accept(this, data);
        }
        _os.Append(']');
        return null;
    }

    public object? VisitAssertion(RegExpAssertion that, object? data)
    {
        _os.Append(that.AssertionType switch
        {
            RegExpAssertion.Type.START_OF_INPUT => "@^i",
            RegExpAssertion.Type.END_OF_INPUT => "@$i",
            RegExpAssertion.Type.END_OF_BUFFER => "@$Z",
            RegExpAssertion.Type.START_OF_LINE => "@^l",
            RegExpAssertion.Type.END_OF_LINE => "@$l",
            RegExpAssertion.Type.BOUNDARY => "@b",
            RegExpAssertion.Type.NON_BOUNDARY => "@B",
            _ => "",
        });
        return null;
    }

    public object? VisitAtom(RegExpAtom that, object? data)
    {
        _os.Append('\'');
        string chardata = that.Data;
        for (int i = 0; i < chardata.Length; i++) AppendUC16(_os, chardata[i]);
        _os.Append('\'');
        return null;
    }

    public object? VisitText(RegExpText that, object? data)
    {
        if (that.Elements.Count == 1)
        {
            that.Elements[0].Tree.Accept(this, data);
        }
        else
        {
            _os.Append("(!");
            for (int i = 0; i < that.Elements.Count; i++)
            {
                _os.Append(' ');
                that.Elements[i].Tree.Accept(this, data);
            }
            _os.Append(')');
        }
        return null;
    }

    public object? VisitQuantifier(RegExpQuantifier that, object? data)
    {
        _os.Append("(# ").Append(that.Min.ToString(CultureInfo.InvariantCulture)).Append(' ');
        if (that.Max == RegExpTree.kInfinity)
        {
            _os.Append("- ");
        }
        else
        {
            _os.Append(that.Max.ToString(CultureInfo.InvariantCulture)).Append(' ');
        }
        _os.Append(that.IsGreedy ? "g " : that.IsPossessive ? "p " : "n ");
        that.Body.Accept(this, data);
        _os.Append(')');
        return null;
    }

    public object? VisitCapture(RegExpCapture that, object? data)
    {
        _os.Append("(^ ");
        that.Body.Accept(this, data);
        _os.Append(')');
        return null;
    }

    public object? VisitGroup(RegExpGroup that, object? data)
    {
        _os.Append("(?").Append(that.Flags.ToFlagString()).Append(": ");
        that.Body.Accept(this, data);
        _os.Append(')');
        return null;
    }

    public object? VisitLookaround(RegExpLookaround that, object? data)
    {
        _os.Append('(');
        _os.Append(that.LookaroundType == RegExpLookaround.Type.LOOKAHEAD ? "->" : "<-");
        _os.Append(that.IsPositive ? " + " : " - ");
        that.Body.Accept(this, data);
        _os.Append(')');
        return null;
    }

    public object? VisitBackReference(RegExpBackReference that, object? data)
    {
        _os.Append("(<- ").Append(that.Captures[0].Index.ToString(CultureInfo.InvariantCulture));
        for (int i = 1; i < that.Captures.Count; ++i)
        {
            _os.Append(',').Append(that.Captures[i].Index.ToString(CultureInfo.InvariantCulture));
        }
        _os.Append(')');
        return null;
    }

    public object? VisitEmpty(RegExpEmpty that, object? data)
    {
        _os.Append('%');
        return null;
    }
}
