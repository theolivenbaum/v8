namespace V8Sharp.RegExp.Tests;

// Tests of the public compile/exec API (the heap-independent parts of
// src/regexp/regexp.cc).
public class RegExpEngineTests
{
    static CompiledRegExp Compile(string pattern, RegExpFlags flags = RegExpFlags.None, uint backtrackLimit = 0)
    {
        RegExpCompileResult r = RegExpEngine.Compile(pattern, flags, backtrackLimit);
        Assert.True(r.Succeeded, r.ErrorMessage);
        return r.RegExp!;
    }

    [Fact]
    public void AtomKindIsChosenForPlainPatterns()
    {
        Assert.Equal(RegExpKind.Atom, Compile("hello").Kind);
        // HasFewDifferentCharacters: the irregexp engine is faster.
        Assert.Equal(RegExpKind.Irregexp, Compile("aaaaaa").Kind);
        Assert.Equal(RegExpKind.Irregexp, Compile("hello", RegExpFlags.IgnoreCase).Kind);
        Assert.Equal(RegExpKind.Irregexp, Compile("hello", RegExpFlags.Sticky).Kind);
        // A tree that is a single atom after escapes.
        CompiledRegExp re = Compile("h\\x65llo");
        Assert.Equal(RegExpKind.Atom, re.Kind);
        Assert.Equal("hello", re.AtomPattern);
    }

    [Fact]
    public void GlobalExecFillsSeveralMatches()
    {
        foreach (string pattern in new[] { "ab", "a(b)?" })
        {
            CompiledRegExp re = Compile(pattern, RegExpFlags.Global);
            int[] regs = new int[re.RegistersPerMatch * 4];
            int n = re.Exec("xabyabzab", 0, regs);
            Assert.Equal(3, n);
            Assert.Equal(1, regs[0]);
            Assert.Equal(4, regs[re.RegistersPerMatch]);
            Assert.Equal(7, regs[2 * re.RegistersPerMatch]);
        }
    }

    [Fact]
    public void AtomStepsBackToLeadSurrogate()
    {
        // /\udc00/gu must not match the trail half of a surrogate pair when
        // started in the middle of it.
        CompiledRegExp re = Compile("\udc00", RegExpFlags.Global | RegExpFlags.Unicode);
        int[] regs = new int[2];
        Assert.Equal(0, re.Exec("𐀀", 1, regs));
    }

    [Fact]
    public void BacktrackLimitReturnsNoMatch()
    {
        CompiledRegExp re = Compile("(a+)+b", RegExpFlags.None, 1000);
        int[] regs = new int[re.RegistersPerMatch];
        Assert.Equal(0, re.Exec(new string('a', 30), 0, regs));
        CompiledRegExp unlimited = Compile("(a+)+b");
        Assert.Equal(1, unlimited.Exec(new string('a', 10) + "b", 0, regs));
    }

    [Fact]
    public void ExcessiveBacktracksFallBackToExperimentalEngine()
    {
        bool old = RegExpEngine.s_enableExperimentalRegExpEngineOnExcessiveBacktracks;
        RegExpEngine.s_enableExperimentalRegExpEngineOnExcessiveBacktracks = true;
        try
        {
            CompiledRegExp re = Compile("(a*)*b");
            Assert.True(re.IsLinearExecutable);
            int[] regs = new int[re.RegistersPerMatch];
            string subject = new string('a', 40);
            Assert.Equal(0, re.Exec(subject, 0, regs));
            Assert.Equal(1, re.Exec(subject + "b", 0, regs));
            Assert.Equal(0, regs[0]);
            Assert.Equal(41, regs[1]);
        }
        finally
        {
            RegExpEngine.s_enableExperimentalRegExpEngineOnExcessiveBacktracks = old;
        }
    }

    [Fact]
    public void LinearFlagUsesExperimentalEngine()
    {
        CompiledRegExp re = Compile("(a|ab)(c|bcd)(d*)", RegExpFlags.Linear);
        Assert.Equal(RegExpKind.Experimental, re.Kind);
        int[] regs = new int[re.RegistersPerMatch];
        Assert.Equal(1, re.Exec("abcd", 0, regs));
        Assert.Equal([0, 4, 0, 1, 1, 4, 4, 4], regs);

        RegExpCompileResult r = RegExpEngine.Compile("(a)\\1", RegExpFlags.Linear);
        Assert.Equal(RegExpError.NotLinear, r.Error);
    }

    [Fact]
    public void CaptureNameMapIsSortedByIndex()
    {
        CompiledRegExp re = Compile("(?<z>a)(?<b>b)|(?<z>c)");
        Assert.NotNull(re.CaptureNameMap);
        Assert.Equal([new("z", 1), new("b", 2), new("z", 3)], re.CaptureNameMap!);
    }

    [Fact]
    public void OneByteAndTwoByteSubjectsCompileSeparately()
    {
        CompiledRegExp re = Compile("(((.*)*)*x)Ā");
        int[] regs = new int[re.RegistersPerMatch];
        // One-byte subject: Ā can never match, the whole graph is a
        // backtrack node (TextNode::CanMatchLatin1).
        Assert.Equal(0, re.Exec("bar.foo baz......", 0, regs));
        Assert.True(re.HasCode(true));
        Assert.False(re.HasCode(false));
        Assert.Equal(1, re.Exec("xĀ", 0, regs));
        Assert.True(re.HasCode(false));
    }

    [Theory]
    [InlineData("", "(?:)")]
    [InlineData("a/b", "a\\/b")]
    [InlineData("[/]", "[/]")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\\\nb", "a\\nb")]
    [InlineData("\u2028", "\\u2028")]
    [InlineData("\\/", "\\/")]
    public void EscapeRegExpSource(string source, string expected) =>
        Assert.Equal(expected, RegExpEngine.EscapeRegExpSource(source));

    [Fact]
    public void VerifyFlags()
    {
        Assert.False(RegExpEngine.VerifyFlags(RegExpFlags.Unicode | RegExpFlags.UnicodeSets));
        Assert.True(RegExpEngine.VerifyFlags(RegExpFlags.Unicode | RegExpFlags.Global));
    }
}
