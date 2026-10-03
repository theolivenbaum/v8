// Compares the identifier and white-space predicates (which V8 answers from
// ICU and V8Sharp from .NET's Unicode data) with the real V8 over every code
// point, using the regexp property escapes that V8 also backs with ICU.

using System.Text;
using V8Sharp.Base.Strings;
using V8Sharp.Oracle;

namespace V8Sharp.Base.Tests.Strings;

public class CharPredicatesOracleTest
{
    // Ranges "a-b" (hex) of code points for which pred holds.
    static string Ranges(Func<int, bool> pred)
    {
        StringBuilder sb = new();
        int start = -1;
        for (int c = 0; c <= 0x110000; c++)
        {
            bool v = c <= 0x10FFFF && pred(c);
            if (v && start < 0) start = c;
            if (!v && start >= 0)
            {
                sb.Append(start.ToString("x")).Append('-').Append((c - 1).ToString("x")).Append(' ');
                start = -1;
            }
        }
        return sb.ToString().TrimEnd();
    }

    static string OracleRanges(string regexp)
    {
        using ReferenceV8 v8 = new(allowNativesSyntax: false);
        string js = "const re = " + regexp + "; const out = []; let start = -1;" +
                    "for (let c = 0; c <= 0x110000; c++) {" +
                    "  const v = c <= 0x10FFFF && re.test(String.fromCodePoint(c));" +
                    "  if (v && start < 0) start = c;" +
                    "  if (!v && start >= 0) { out.push(start.toString(16) + '-' + (c - 1).toString(16)); start = -1; }" +
                    "} print(out.join(' '));";
        return v8.Run(js).TrimEnd('\n');
    }

    [Fact]
    public void IdentifierStartMatchesV8()
    {
        Assert.Equal(OracleRanges(@"/^[\p{ID_Start}$_\\]$/u"), Ranges(CharPredicates.IsIdentifierStart));
    }

    [Fact]
    public void IdentifierPartMatchesV8()
    {
        Assert.Equal(OracleRanges(@"/^[\p{ID_Continue}$_\\‌‍]$/u"), Ranges(CharPredicates.IsIdentifierPart));
    }

    [Fact]
    public void WhiteSpaceMatchesV8()
    {
        // ES WhiteSpace: TAB, VT, FF, ZWNBSP and gC=Zs.
        Assert.Equal(OracleRanges(@"/^[\t\v\f﻿\p{Zs}]$/u"), Ranges(CharPredicates.IsWhiteSpace));
        // And the scanner's view through String.prototype.trim.
        Assert.Equal(OracleRanges(@"/^\s$/u"), Ranges(CharPredicates.IsWhiteSpaceOrLineTerminator));
    }
}
