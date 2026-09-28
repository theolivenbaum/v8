// Port of test/unittests/regexp/regexp-unittest.cc.
//
// Tests that drive the native (architecture-specific) macro assemblers run
// here through RegExpBytecodeGenerator and the bytecode interpreter, the only
// backend in this port; their names are kept. Tests that exercise the JS
// engine (use counters, external strings, interrupts) are skipped with a
// reason: they belong to the engine, not to src/regexp.

using V8Sharp.RegExp.Unicode;
using REB = V8Sharp.RegExp.Bytecode;

namespace V8Sharp.RegExp.Tests;

public class RegExpTest
{
    static bool CheckParse(string input)
    {
        var result = new RegExpCompileData();
        return RegExpParser.ParseRegExp(input, RegExpFlags.None, result);
    }

    static void CheckParseEq(string input, string expected, bool unicode = false)
    {
        var result = new RegExpCompileData();
        RegExpFlags flags = RegExpFlags.None;
        if (unicode) flags |= RegExpFlags.Unicode;
        Assert.True(RegExpParser.ParseRegExp(input, flags, result), input);
        Assert.NotNull(result.Tree);
        Assert.Equal(RegExpError.None, result.Error);
        Assert.Equal(expected, RegExpAstPrinter.Print(result.Tree!));
    }

    static bool CheckSimple(string input)
    {
        var result = new RegExpCompileData();
        Assert.True(RegExpParser.ParseRegExp(input, RegExpFlags.None, result));
        Assert.NotNull(result.Tree);
        Assert.Equal(RegExpError.None, result.Error);
        return result.Simple;
    }

    static (int MinMatch, int MaxMatch) CheckMinMaxMatch(string input)
    {
        var result = new RegExpCompileData();
        Assert.True(RegExpParser.ParseRegExp(input, RegExpFlags.None, result));
        Assert.NotNull(result.Tree);
        Assert.Equal(RegExpError.None, result.Error);
        return (result.Tree!.MinMatch, result.Tree.MaxMatch);
    }

    static void CHECK_PARSE_ERROR(string input) => Assert.False(CheckParse(input), input);
    static void CHECK_SIMPLE(string input, bool simple) => Assert.Equal(simple, CheckSimple(input));

    static void CHECK_MIN_MAX(string input, int min, int max)
    {
        (int minMatch, int maxMatch) = CheckMinMaxMatch(input);
        Assert.Equal(min, minMatch);
        Assert.Equal(max, maxMatch);
    }

    [Fact]
    public void ConvertFlagsToString()
    {
        // regexp.flags for /ab+c/ig.
        Assert.Equal("gi", (RegExpFlags.Global | RegExpFlags.IgnoreCase).ToFlagString());
    }

    [Fact]
    public void ConvertFlagsToStringNoFlags() => Assert.Equal("", RegExpFlags.None.ToFlagString());

    [Fact]
    public void ConvertFlagsToStringAllFlags()
    {
        RegExpFlags flags = RegExpFlagsExtensions.FromString("dgimsuy")!.Value;
        Assert.Equal("dgimsuy", flags.ToFlagString());
    }

    [Fact]
    public void Parser()
    {
        CHECK_PARSE_ERROR("?");
        CheckParseEq("abc", "'abc'");
        CheckParseEq("", "%");
        CheckParseEq("abc|def", "(| 'abc' 'def')");
        CheckParseEq("abc|def|ghi", "(| 'abc' 'def' 'ghi')");
        CheckParseEq("^xxx$", "(: @^i 'xxx' @$i)");
        CheckParseEq("ab\\b\\d\\bcd", "(: 'ab' @b [0-9] @b 'cd')");
        CheckParseEq("\\w|\\d", "(| [0-9 A-Z _ a-z] [0-9])");
        CheckParseEq("a*", "(# 0 - g 'a')");
        CheckParseEq("a*?", "(# 0 - n 'a')");
        CheckParseEq("abc+", "(: 'ab' (# 1 - g 'c'))");
        CheckParseEq("abc+?", "(: 'ab' (# 1 - n 'c'))");
        CheckParseEq("xyz?", "(: 'xy' (# 0 1 g 'z'))");
        CheckParseEq("xyz??", "(: 'xy' (# 0 1 n 'z'))");
        CheckParseEq("xyz{0,1}", "(: 'xy' (# 0 1 g 'z'))");
        CheckParseEq("xyz{0,1}?", "(: 'xy' (# 0 1 n 'z'))");
        CheckParseEq("xyz{93}", "(: 'xy' (# 93 93 g 'z'))");
        CheckParseEq("xyz{93}?", "(: 'xy' (# 93 93 n 'z'))");
        CheckParseEq("xyz{1,32}", "(: 'xy' (# 1 32 g 'z'))");
        CheckParseEq("xyz{1,32}?", "(: 'xy' (# 1 32 n 'z'))");
        CheckParseEq("xyz{1,}", "(: 'xy' (# 1 - g 'z'))");
        CheckParseEq("xyz{1,}?", "(: 'xy' (# 1 - n 'z'))");
        CheckParseEq("a\\fb\\nc\\rd\\te\\vf", "'a\\x0cb\\x0ac\\x0dd\\x09e\\x0bf'");
        CheckParseEq("a\\nb\\bc", "(: 'a\\x0ab' @b 'c')");
        CheckParseEq("(?:foo)", "(?: 'foo')");
        CheckParseEq("(?: foo )", "(?: ' foo ')");
        CheckParseEq("(foo|bar|baz)", "(^ (| 'foo' 'bar' 'baz'))");
        CheckParseEq("foo|(bar|baz)|quux", "(| 'foo' (^ (| 'bar' 'baz')) 'quux')");
        CheckParseEq("foo(?=bar)baz", "(: 'foo' (-> + 'bar') 'baz')");
        CheckParseEq("foo(?!bar)baz", "(: 'foo' (-> - 'bar') 'baz')");
        CheckParseEq("foo(?<=bar)baz", "(: 'foo' (<- + 'bar') 'baz')");
        CheckParseEq("foo(?<!bar)baz", "(: 'foo' (<- - 'bar') 'baz')");
        CheckParseEq("()", "(^ %)");
        CheckParseEq("(?=)", "(-> + %)");
        CheckParseEq("[]", "^[\\x00-\\u{10ffff}]");
        CheckParseEq("[^]", "[\\x00-\\u{10ffff}]");
        CheckParseEq("[x]", "[x]");
        CheckParseEq("[xyz]", "[x y z]");
        CheckParseEq("[a-zA-Z0-9]", "[a-z A-Z 0-9]");
        CheckParseEq("[-123]", "[- 1 2 3]");
        CheckParseEq("[^123]", "^[1 2 3]");
        CheckParseEq("]", "']'");
        CheckParseEq("}", "'}'");
        CheckParseEq("[a-b-c]", "[a-b - c]");
        CheckParseEq("[\\d]", "[0-9]");
        CheckParseEq("[x\\dz]", "[x 0-9 z]");
        CheckParseEq("[\\d-z]", "[0-9 - z]");
        CheckParseEq("[\\d-\\d]", "[0-9 0-9 -]");
        CheckParseEq("[z-\\d]", "[0-9 z -]");
        CheckParseEq("\\cj\\cJ\\ci\\cI\\ck\\cK", "'\\x0a\\x0a\\x09\\x09\\x0b\\x0b'");
        CheckParseEq("\\c!", "'\\c!'");
        CheckParseEq("\\c_", "'\\c_'");
        CheckParseEq("\\c~", "'\\c~'");
        CheckParseEq("\\c1", "'\\c1'");
        CheckParseEq("[\\c!]", "[\\ c !]");
        CheckParseEq("[\\c_]", "[\\x1f]");
        CheckParseEq("[\\c~]", "[\\ c ~]");
        CheckParseEq("[\\ca]", "[\\x01]");
        CheckParseEq("[\\cz]", "[\\x1a]");
        CheckParseEq("[\\cA]", "[\\x01]");
        CheckParseEq("[\\cZ]", "[\\x1a]");
        CheckParseEq("[\\c1]", "[\\x11]");
        CheckParseEq("[a\\]c]", "[a ] c]");
        CheckParseEq("\\[\\]\\{\\}\\(\\)\\%\\^\\#\\ ", "'[]{}()%^# '");
        CheckParseEq("[\\[\\]\\{\\}\\(\\)\\%\\^\\#\\ ]", "[[ ] { } ( ) % ^ #  ]");
        CheckParseEq("\\0", "'\\x00'");
        CheckParseEq("\\8", "'8'");
        CheckParseEq("\\9", "'9'");
        CheckParseEq("\\11", "'\\x09'");
        CheckParseEq("\\11a", "'\\x09a'");
        CheckParseEq("\\011", "'\\x09'");
        CheckParseEq("\\00011", "'\\x0011'");
        CheckParseEq("\\118", "'\\x098'");
        CheckParseEq("\\111", "'I'");
        CheckParseEq("\\1111", "'I1'");
        CheckParseEq("(x)(x)(x)\\1", "(: (^ 'x') (^ 'x') (^ 'x') (<- 1))");
        CheckParseEq("(x)(x)(x)\\2", "(: (^ 'x') (^ 'x') (^ 'x') (<- 2))");
        CheckParseEq("(x)(x)(x)\\3", "(: (^ 'x') (^ 'x') (^ 'x') (<- 3))");
        CheckParseEq("(x)(x)(x)\\4", "(: (^ 'x') (^ 'x') (^ 'x') '\\x04')");
        CheckParseEq("(x)(x)(x)\\1*", "(: (^ 'x') (^ 'x') (^ 'x') (# 0 - g (<- 1)))");
        CheckParseEq("(x)(x)(x)\\2*", "(: (^ 'x') (^ 'x') (^ 'x') (# 0 - g (<- 2)))");
        CheckParseEq("(x)(x)(x)\\3*", "(: (^ 'x') (^ 'x') (^ 'x') (# 0 - g (<- 3)))");
        CheckParseEq("(x)(x)(x)\\4*", "(: (^ 'x') (^ 'x') (^ 'x') (# 0 - g '\\x04'))");
        CheckParseEq("(x)(x)(x)(x)(x)(x)(x)(x)(x)(x)\\10", "(: (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (<- 10))");
        CheckParseEq("(x)(x)(x)(x)(x)(x)(x)(x)(x)(x)\\11", "(: (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') (^ 'x') '\\x09')");
        CheckParseEq("(a)\\1", "(: (^ 'a') (<- 1))");
        CheckParseEq("(a\\1)", "(^ 'a')");
        CheckParseEq("(\\1a)", "(^ 'a')");
        CheckParseEq("(\\2)(\\1)", "(: (^ (<- 2)) (^ (<- 1)))");
        CheckParseEq("(?=a)?a", "'a'");
        CheckParseEq("(?=a){0,10}a", "'a'");
        CheckParseEq("(?=a){1,10}a", "(: (-> + 'a') 'a')");
        CheckParseEq("(?=a){9,10}a", "(: (-> + 'a') 'a')");
        CheckParseEq("(?!a)?a", "'a'");
        CheckParseEq("\\1(a)", "(: (<- 1) (^ 'a'))");
        CheckParseEq("(?!(a))\\1", "(: (-> - (^ 'a')) (<- 1))");
        CheckParseEq("(?!\\1(a\\1)\\1)\\1", "(: (-> - (: (<- 1) (^ 'a') (<- 1))) (<- 1))");
        CheckParseEq("\\1\\2(a(?:\\1(b\\1\\2))\\2)\\1", "(: (<- 1) (<- 2) (^ (: 'a' (?: (^ 'b')) (<- 2))) (<- 1))");
        CheckParseEq("\\1\\2(a(?<=\\1(b\\1\\2))\\2)\\1", "(: (<- 1) (<- 2) (^ (: 'a' (<- + (^ 'b')) (<- 2))) (<- 1))");
        CheckParseEq("[\\0]", "[\\x00]");
        CheckParseEq("[\\11]", "[\\x09]");
        CheckParseEq("[\\11a]", "[\\x09 a]");
        CheckParseEq("[\\011]", "[\\x09]");
        CheckParseEq("[\\00011]", "[\\x00 1 1]");
        CheckParseEq("[\\118]", "[\\x09 8]");
        CheckParseEq("[\\111]", "[I]");
        CheckParseEq("[\\1111]", "[I 1]");
        CheckParseEq("\\x34", "'4'");
        CheckParseEq("\\x60", "'`'");
        CheckParseEq("\\x3z", "'x3z'");
        CheckParseEq("\\c", "'\\c'");
        CheckParseEq("\\u0034", "'4'");
        CheckParseEq("\\u003z", "'u003z'");
        CheckParseEq("foo[z]*", "(: 'foo' (# 0 - g [z]))");
        CheckParseEq("^^^$$$\\b\\b\\b\\b", "(: @^i @^i @^i @$i @$i @$i @b @b @b @b)");
        CheckParseEq("\\b\\b\\b\\b\\B\\B\\B\\B\\b\\b\\b\\b", "(: @b @b @b @b @B @B @B @B @b @b @b @b)");
        CheckParseEq("\\b\\B\\b", "(: @b @B @b)");
        CheckParseEq("(?=a)(?=a)", "(: (-> + 'a') (-> + 'a'))");
        CheckParseEq("\\u{12345}", "'\\ud808\\udf45'", true);
        CheckParseEq("\\u{12345}\\u{23456}", "(! '\\ud808\\udf45' '\\ud84d\\udc56')", true);
        CheckParseEq("\\u{12345}|\\u{23456}", "(| '\\ud808\\udf45' '\\ud84d\\udc56')", true);
        CheckParseEq("\\u{12345}{3}", "(# 3 3 g '\\ud808\\udf45')", true);
        CheckParseEq("\\u{12345}*", "(# 0 - g '\\ud808\\udf45')", true);
        CheckParseEq("\\ud808\\udf45*", "(# 0 - g '\\ud808\\udf45')", true);
        CheckParseEq("[\\ud808\\udf45-\\ud809\\udccc]", "[\\u{012345}-\\u{0124cc}]", true);
        CHECK_SIMPLE("", false);
        CHECK_SIMPLE("a", true);
        CHECK_SIMPLE("a|b", false);
        CHECK_SIMPLE("a\\n", false);
        CHECK_SIMPLE("^a", false);
        CHECK_SIMPLE("a$", false);
        CHECK_SIMPLE("a\\b!", false);
        CHECK_SIMPLE("a\\Bb", false);
        CHECK_SIMPLE("a*", false);
        CHECK_SIMPLE("a*?", false);
        CHECK_SIMPLE("a?", false);
        CHECK_SIMPLE("a??", false);
        CHECK_SIMPLE("a{0,1}?", false);
        CHECK_SIMPLE("a{1,1}?", false);
        CHECK_SIMPLE("a{1,2}?", false);
        CHECK_SIMPLE("a+?", false);
        CHECK_SIMPLE("(a)", false);
        CHECK_SIMPLE("(a)\\1", false);
        CHECK_SIMPLE("(\\1a)", false);
        CHECK_SIMPLE("\\1(a)", false);
        CHECK_SIMPLE("a\\s", false);
        CHECK_SIMPLE("a\\S", false);
        CHECK_SIMPLE("a\\d", false);
        CHECK_SIMPLE("a\\D", false);
        CHECK_SIMPLE("a\\w", false);
        CHECK_SIMPLE("a\\W", false);
        CHECK_SIMPLE("a.", false);
        CHECK_SIMPLE("a\\q", false);
        CHECK_SIMPLE("a[a]", false);
        CHECK_SIMPLE("a[^a]", false);
        CHECK_SIMPLE("a[a-z]", false);
        CHECK_SIMPLE("a[\\q]", false);
        CHECK_SIMPLE("a(?:b)", false);
        CHECK_SIMPLE("a(?=b)", false);
        CHECK_SIMPLE("a(?!b)", false);
        CHECK_SIMPLE("\\x60", false);
        CHECK_SIMPLE("\\u0060", false);
        CHECK_SIMPLE("\\cA", false);
        CHECK_SIMPLE("\\q", false);
        CHECK_SIMPLE("\\1112", false);
        CHECK_SIMPLE("\\0", false);
        CHECK_SIMPLE("(a)\\1", false);
        CHECK_SIMPLE("(?=a)?a", false);
        CHECK_SIMPLE("(?!a)?a\\1", false);
        CHECK_SIMPLE("(?:(?=a))a\\1", false);
        CheckParseEq("a{}", "'a{}'");
        CheckParseEq("a{,}", "'a{,}'");
        CheckParseEq("a{", "'a{'");
        CheckParseEq("a{z}", "'a{z}'");
        CheckParseEq("a{1z}", "'a{1z}'");
        CheckParseEq("a{12z}", "'a{12z}'");
        CheckParseEq("a{12,", "'a{12,'");
        CheckParseEq("a{12,3b", "'a{12,3b'");
        CheckParseEq("{}", "'{}'");
        CheckParseEq("{,}", "'{,}'");
        CheckParseEq("{", "'{'");
        CheckParseEq("{z}", "'{z}'");
        CheckParseEq("{1z}", "'{1z}'");
        CheckParseEq("{12z}", "'{12z}'");
        CheckParseEq("{12,", "'{12,'");
        CheckParseEq("{12,3b", "'{12,3b'");
        CHECK_MIN_MAX("a", 1, 1);
        CHECK_MIN_MAX("abc", 3, 3);
        CHECK_MIN_MAX("a[bc]d", 3, 3);
        CHECK_MIN_MAX("a|bc", 1, 2);
        CHECK_MIN_MAX("ab|c", 1, 2);
        CHECK_MIN_MAX("a||bc", 0, 2);
        CHECK_MIN_MAX("|", 0, 0);
        CHECK_MIN_MAX("(?:ab)", 2, 2);
        CHECK_MIN_MAX("(?:ab|cde)", 2, 3);
        CHECK_MIN_MAX("(?:ab)|cde", 2, 3);
        CHECK_MIN_MAX("(ab)", 2, 2);
        CHECK_MIN_MAX("(ab|cde)", 2, 3);
        CHECK_MIN_MAX("(ab)\\1", 2, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(ab|cde)\\1", 2, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:ab)?", 0, 2);
        CHECK_MIN_MAX("(?:ab)*", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:ab)+", 2, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a?", 0, 1);
        CHECK_MIN_MAX("a*", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a+", 1, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a??", 0, 1);
        CHECK_MIN_MAX("a*?", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a+?", 1, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a?)?", 0, 1);
        CHECK_MIN_MAX("(?:a*)?", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a+)?", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a?)+", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a*)+", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a+)+", 1, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a?)*", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a*)*", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a+)*", 0, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a{0}", 0, 0);
        CHECK_MIN_MAX("(?:a+){0}", 0, 0);
        CHECK_MIN_MAX("(?:a+){0,0}", 0, 0);
        CHECK_MIN_MAX("a*b", 1, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a+b", 2, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a*b|c", 1, RegExpTree.kInfinity);
        CHECK_MIN_MAX("a+b|c", 1, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:a{5,1000000}){3,1000000}", 15, RegExpTree.kInfinity);
        CHECK_MIN_MAX("(?:ab){4,7}", 8, 14);
        CHECK_MIN_MAX("a\\bc", 2, 2);
        CHECK_MIN_MAX("a\\Bc", 2, 2);
        CHECK_MIN_MAX("a\\sc", 3, 3);
        CHECK_MIN_MAX("a\\Sc", 3, 3);
        CHECK_MIN_MAX("a(?=b)c", 2, 2);
        CHECK_MIN_MAX("a(?=bbb|bb)c", 2, 2);
        CHECK_MIN_MAX("a(?!bbb|bb)c", 2, 2);
        CheckParseEq("(?<a>x)(?<b>x)(?<c>x)\\k<a>", "(: (^ 'x') (^ 'x') (^ 'x') (<- 1))", true);
        CheckParseEq("(?<a>x)(?<b>x)(?<c>x)\\k<b>", "(: (^ 'x') (^ 'x') (^ 'x') (<- 2))", true);
        CheckParseEq("(?<a>x)(?<b>x)(?<c>x)\\k<c>", "(: (^ 'x') (^ 'x') (^ 'x') (<- 3))", true);
        CheckParseEq("(?<a>a)\\k<a>", "(: (^ 'a') (<- 1))", true);
        CheckParseEq("(?<a>a\\k<a>)", "(^ 'a')", true);
        CheckParseEq("(?<a>\\k<a>a)", "(^ 'a')", true);
        CheckParseEq("(?<a>\\k<b>)(?<b>\\k<a>)", "(: (^ (<- 2)) (^ (<- 1)))", true);
        CheckParseEq("\\k<a>(?<a>a)", "(: (<- 1) (^ 'a'))", true);
        CheckParseEq("(?<\\u{03C0}>a)", "(^ 'a')", true);
        CheckParseEq("(?<\\u03C0>a)", "(^ 'a')", true);

    }

    [Fact]
    public void ParserRegression()
    {
        CheckParseEq("[A-Z$-][x]", "(! [A-Z $ -] [x])");
        CheckParseEq("a{3,4*}", "(: 'a{3,' (# 0 - g '4') '}')");
        CheckParseEq("{", "'{'");
        CheckParseEq("a|", "(| 'a' %)");

    }

    static void ExpectError(string input, string expected, bool unicode = false)
    {
        var result = new RegExpCompileData();
        RegExpFlags flags = RegExpFlags.None;
        if (unicode) flags |= RegExpFlags.Unicode;
        Assert.False(RegExpParser.ParseRegExp(input, flags, result));
        Assert.Null(result.Tree);
        Assert.NotEqual(RegExpError.None, result.Error);
        Assert.Equal(expected, RegExpErrors.ErrorString(result.Error));
    }

    [Fact]
    public void Errors()
    {
        const string kEndBackslash = "\\ at end of pattern";
        ExpectError("\\", kEndBackslash);
        const string kUnterminatedGroup = "Unterminated group";
        ExpectError("(foo", kUnterminatedGroup);
        const string kInvalidGroup = "Invalid group";
        ExpectError("(?", kInvalidGroup);
        const string kUnterminatedCharacterClass = "Unterminated character class";
        ExpectError("[", kUnterminatedCharacterClass);
        ExpectError("[a-", kUnterminatedCharacterClass);
        const string kNothingToRepeat = "Nothing to repeat";
        ExpectError("*", kNothingToRepeat);
        ExpectError("?", kNothingToRepeat);
        ExpectError("+", kNothingToRepeat);
        ExpectError("{1}", kNothingToRepeat);
        ExpectError("{1,2}", kNothingToRepeat);
        ExpectError("{1,}", kNothingToRepeat);

        // Check that we don't allow more than kMaxCapture captures
        const int kMaxCaptures = 1 << 16;  // Must match regexp::Parser::kMaxCaptures.
        const string kTooManyCaptures = "Too many captures";
        var os = new System.Text.StringBuilder();
        for (int i = 0; i <= kMaxCaptures; i++) os.Append("()");
        ExpectError(os.ToString(), kTooManyCaptures);

        const string kInvalidCaptureName = "Invalid capture group name";
        ExpectError("(?<>.)", kInvalidCaptureName, true);
        ExpectError("(?<1>.)", kInvalidCaptureName, true);
        ExpectError("(?<_%>.)", kInvalidCaptureName, true);
        ExpectError("\\k<a", kInvalidCaptureName, true);
        const string kDuplicateCaptureName = "Duplicate capture group name";
        ExpectError("(?<a>.)(?<a>.)", kDuplicateCaptureName, true);
        const string kInvalidUnicodeEscape = "Invalid Unicode escape";
        ExpectError("(?<\\u{FISK}", kInvalidUnicodeEscape, true);
        const string kInvalidCaptureReferenced = "Invalid named capture referenced";
        ExpectError("\\k<a>", kInvalidCaptureReferenced, true);
        ExpectError("(?<b>)\\k<a>", kInvalidCaptureReferenced, true);
        const string kInvalidNamedReference = "Invalid named reference";
        ExpectError("\\ka", kInvalidNamedReference, true);
    }

    static bool IsDigit(int c) => '0' <= c && c <= '9';
    static bool NotDigit(int c) => !IsDigit(c);

    // unibrow::IsWhiteSpaceOrLineTerminator.
    static bool IsWhiteSpaceOrLineTerminator(int c) =>
        c is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 or 0x2028 or 0x2029 or 0x202F
            or 0x205F or 0x3000 or 0xFEFF || (c >= 0x2000 && c <= 0x200A);

    static bool NotWhiteSpaceNorLineTermiantor(int c) => !IsWhiteSpaceOrLineTerminator(c);
    static bool IsRegExpWord(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || IsDigit(c) || c == '_';
    static bool NotWord(int c) => !IsRegExpWord(c);
    static bool IsLineTerminator(int c) => c is 0x0A or 0x0D or 0x2028 or 0x2029;
    static bool NotLineTerminator(int c) => !IsLineTerminator(c);

    static void TestCharacterClassEscapes(StandardCharacterSet c, Func<int, bool> pred)
    {
        var ranges = new List<CharacterRange>(2);
        CharacterRange.AddClassEscape(c, ranges, false);
        for (int i = 0; i < (1 << 16); i++)
        {
            bool inClass = false;
            for (int j = 0; !inClass && j < ranges.Count; j++)
            {
                CharacterRange range = ranges[j];
                inClass = range.From <= i && i <= range.To;
            }
            Assert.Equal(pred(i), inClass);
        }
    }

    [Fact]
    public void CharacterClassEscapes()
    {
        TestCharacterClassEscapes(StandardCharacterSet.kNotLineTerminator, NotLineTerminator);
        TestCharacterClassEscapes(StandardCharacterSet.kDigit, IsDigit);
        TestCharacterClassEscapes(StandardCharacterSet.kNotDigit, NotDigit);
        TestCharacterClassEscapes(StandardCharacterSet.kWhitespace, IsWhiteSpaceOrLineTerminator);
        TestCharacterClassEscapes(StandardCharacterSet.kNotWhitespace, NotWhiteSpaceNorLineTermiantor);
        TestCharacterClassEscapes(StandardCharacterSet.kWord, IsRegExpWord);
        TestCharacterClassEscapes(StandardCharacterSet.kNotWord, NotWord);
    }

    // RegExp::CompileForTesting: returns the analyzed node graph.
    static RegExpNode? Compile(string input, bool multiline, bool unicode, bool isOneByte)
    {
        var compileData = new RegExpCompileData { CompilationTarget = CompilationTarget.kNative };
        RegExpFlags flags = RegExpFlags.None;
        if (multiline) flags |= RegExpFlags.Multiline;
        if (unicode) flags |= RegExpFlags.Unicode;
        if (!RegExpParser.ParseRegExp(input, flags, compileData)) return null;
        uint backtrackLimit = RegExpEngine.kNoBacktrackLimit;
        RegExpEngine.CompileIrregexp(compileData, flags, "", input.Length, ref backtrackLimit, false,
            isOneByte);
        return compileData.Node;
    }

    static void Execute(string input, bool multiline, bool unicode, bool isOneByte)
    {
        RegExpNode? node = Compile(input, multiline, unicode, isOneByte);
        Assert.NotNull(node);
    }

    [Fact]
    public void ParsePossessiveRepetition()
    {
        bool oldFlagValue = RegExpParserImpl.s_regexpPossessiveQuantifier;
        try
        {
            // Enable possessive quantifier syntax.
            RegExpParserImpl.s_regexpPossessiveQuantifier = true;

            CheckParseEq("a*+", "(# 0 - p 'a')");
            CheckParseEq("a++", "(# 1 - p 'a')");
            CheckParseEq("a?+", "(# 0 1 p 'a')");
            CheckParseEq("a{10,20}+", "(# 10 20 p 'a')");
            CheckParseEq("za{10,20}+b", "(: 'z' (# 10 20 p 'a') 'b')");

            // Disable possessive quantifier syntax.
            RegExpParserImpl.s_regexpPossessiveQuantifier = false;

            CHECK_PARSE_ERROR("a*+");
            CHECK_PARSE_ERROR("a++");
            CHECK_PARSE_ERROR("a?+");
            CHECK_PARSE_ERROR("a{10,20}+");
            CHECK_PARSE_ERROR("a{10,20}+b");
        }
        finally
        {
            RegExpParserImpl.s_regexpPossessiveQuantifier = oldFlagValue;
        }
    }

    // Tests of interpreter.

    const int kNativeTotalRegisters = 16;

    // The MacroAssemblerNative* tests run on V8's native assembler
    // (ArchRegExpMacroAssembler, here RegExpMacroAssemblerIL) and, as a second
    // configuration, on the bytecode generator and the interpreter.
    static RegExpMacroAssembler NewNativeAssembler(RegExpMacroAssembler.Mode mode, int registersToSave,
        bool native) =>
        native ? new RegExpMacroAssemblerIL(mode, registersToSave) : new RegExpBytecodeGenerator(mode);

    static object GetNativeCode(RegExpMacroAssembler m, string source) => m.GetCode(source, RegExpFlags.None);

    // NativeRegExpMacroAssembler::ExecuteForTesting: `captures` receives the
    // output registers.
    static int ExecuteNative(object code, string input, int startOffset, int[]? captures,
        int totalRegisters = kNativeTotalRegisters)
    {
        if (code is RegExpILCode native) return native.Execute(input, startOffset, captures ?? []);
        // V8 passes a null capture array of size 0 to the native code; the
        // interpreter requires at least the two match registers.
        return IrregexpInterpreter.MatchInternal((byte[])code, input, captures ?? new int[2], totalRegisters,
            startOffset, RegExpEngine.kNoBacktrackLimit);
    }

    static byte[] GetCode(RegExpMacroAssembler m, string source) => (byte[])m.GetCode(source, RegExpFlags.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeSuccess(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 4, native);

        m.Succeed();

        object code = GetNativeCode(m, "");

        int[] captures = [42, 37, 87, 117];
        int result = ExecuteNative(code, "foofoo", 0, captures);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(-1, captures[0]);
        Assert.Equal(-1, captures[1]);
        Assert.Equal(-1, captures[2]);
        Assert.Equal(-1, captures[3]);
    }

    static void EmitSimpleFoo(RegExpMacroAssembler m)
    {
        Label fail = new(), backtrack = new();
        m.PushBacktrack(fail);
        m.CheckNotAtStart(0, null);
        m.LoadCurrentCharacter(2, null);
        m.CheckNotCharacter('o', null);
        m.LoadCurrentCharacter(1, null, false);
        m.CheckNotCharacter('o', null);
        m.LoadCurrentCharacter(0, null, false);
        m.CheckNotCharacter('f', null);
        m.WriteCurrentPositionToRegister(0, 0);
        m.WriteCurrentPositionToRegister(1, 3);
        m.AdvanceCurrentPosition(3);
        m.PushBacktrack(backtrack);
        m.Succeed();
        m.BindJumpTarget(backtrack);
        m.Backtrack();
        m.BindJumpTarget(fail);
        m.Fail();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeSimple(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 4, native);
        EmitSimpleFoo(m);
        object code = GetNativeCode(m, "^foo");

        int[] captures = [42, 37, 87, 117];
        int result = ExecuteNative(code, "foofoo", 0, captures);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, captures[0]);
        Assert.Equal(3, captures[1]);
        Assert.Equal(-1, captures[2]);
        Assert.Equal(-1, captures[3]);

        result = ExecuteNative(code, "barbarbar", 0, captures);

        Assert.Equal(IrregexpInterpreter.FAILURE, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeSimpleUC16(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.UC16, 4, native);
        EmitSimpleFoo(m);
        object code = GetNativeCode(m, "^foo");

        int[] captures = [42, 37, 87, 117];
        // input_data = {'f', 'o', 'o', 'f', 'o', 0x2603}
        int result = ExecuteNative(code, "foofo\u2603", 0, captures);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, captures[0]);
        Assert.Equal(3, captures[1]);
        Assert.Equal(-1, captures[2]);
        Assert.Equal(-1, captures[3]);

        result = ExecuteNative(code, "barbarba\u2603", 0, captures);

        Assert.Equal(IrregexpInterpreter.FAILURE, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeBacktrack(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 0, native);

        Label fail = new();
        Label backtrack = new();
        m.LoadCurrentCharacter(10, fail);
        m.Succeed();
        m.BindJumpTarget(fail);
        m.PushBacktrack(backtrack);
        m.LoadCurrentCharacter(10, null);
        m.Succeed();
        m.BindJumpTarget(backtrack);
        m.Fail();

        object code = GetNativeCode(m, "..........");

        int result = ExecuteNative(code, "foofoo", 0, null);

        Assert.Equal(IrregexpInterpreter.FAILURE, result);
    }

    static void EmitBackReference(RegExpMacroAssembler m)
    {
        m.WriteCurrentPositionToRegister(0, 0);
        m.AdvanceCurrentPosition(2);
        m.WriteCurrentPositionToRegister(1, 0);
        var nomatch = new Label();
        m.CheckNotBackReference(0, false, nomatch);
        m.Fail();
        m.Bind(nomatch);
        m.AdvanceCurrentPosition(2);
        var missingMatch = new Label();
        m.CheckNotBackReference(0, false, missingMatch);
        m.WriteCurrentPositionToRegister(2, 0);
        m.Succeed();
        m.Bind(missingMatch);
        m.Fail();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeBackReferenceLATIN1(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 4, native);
        EmitBackReference(m);
        object code = GetNativeCode(m, "^(..)..\u0001");

        int[] output = new int[4];
        int result = ExecuteNative(code, "fooofo", 0, output);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, output[0]);
        Assert.Equal(2, output[1]);
        Assert.Equal(6, output[2]);
        Assert.Equal(-1, output[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeBackReferenceUC16(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.UC16, 4, native);
        EmitBackReference(m);
        object code = GetNativeCode(m, "^(..)..\u0001");

        int[] output = new int[4];
        int result = ExecuteNative(code, "f\u2028oof\u2028", 0, output);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, output[0]);
        Assert.Equal(2, output[1]);
        Assert.Equal(6, output[2]);
        Assert.Equal(-1, output[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblernativeAtStart(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 0, native);

        Label notAtStart = new(), newline = new(), fail = new();
        m.CheckNotAtStart(0, notAtStart);
        // Check that prevchar = '\n' and current = 'f'.
        m.CheckCharacter('\n', newline);
        m.BindJumpTarget(fail);
        m.Fail();
        m.Bind(newline);
        m.LoadCurrentCharacter(0, fail);
        m.CheckNotCharacter('f', fail);
        m.Succeed();

        m.Bind(notAtStart);
        // Check that prevchar = 'o' and current = 'b'.
        var prevo = new Label();
        m.CheckCharacter('o', prevo);
        m.Fail();
        m.Bind(prevo);
        m.LoadCurrentCharacter(0, fail);
        m.CheckNotCharacter('b', fail);
        m.Succeed();

        object code = GetNativeCode(m, "(^f|ob)");

        int result = ExecuteNative(code, "foobar", 0, null);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);

        result = ExecuteNative(code, "foobar", 3, null);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeBackRefNoCase(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 4, native);

        Label fail = new(), succ = new();

        m.WriteCurrentPositionToRegister(0, 0);
        m.WriteCurrentPositionToRegister(2, 0);
        m.AdvanceCurrentPosition(3);
        m.WriteCurrentPositionToRegister(3, 0);
        m.CheckNotBackReferenceIgnoreCase(2, false, false, fail);  // Match "AbC".
        m.CheckNotBackReferenceIgnoreCase(2, false, false, fail);  // Match "ABC".
        var expectedFail = new Label();
        m.CheckNotBackReferenceIgnoreCase(2, false, false, expectedFail);
        m.BindJumpTarget(fail);
        m.Fail();

        m.Bind(expectedFail);
        m.AdvanceCurrentPosition(3);  // Skip "xYz"
        m.CheckNotBackReferenceIgnoreCase(2, false, false, succ);
        m.Fail();

        m.Bind(succ);
        m.WriteCurrentPositionToRegister(1, 0);
        m.Succeed();

        object code = GetNativeCode(m, "^(abc)\u0001\u0001(?!\u0001)...(?!\u0001)");

        int[] output = new int[4];
        int result = ExecuteNative(code, "aBcAbCABCxYzab", 0, output);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, output[0]);
        Assert.Equal(12, output[1]);
        Assert.Equal(0, output[2]);
        Assert.Equal(3, output[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeRegisters(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 6, native);

        const int out1 = 0, out2 = 1, out3 = 2, out4 = 3, out5 = 4, sp = 6, loopCnt = 7;
        var fail = new Label();
        var backtrack = new Label();
        m.WriteCurrentPositionToRegister(out1, 0);  // Output: [0]
        m.PushRegister(out1, RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck);
        m.PushBacktrack(backtrack);
        m.WriteStackPointerToRegister(sp);
        // Fill stack and registers
        m.AdvanceCurrentPosition(2);
        m.WriteCurrentPositionToRegister(out1, 0);
        m.PushRegister(out1, RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck);
        m.PushBacktrack(fail);
        // Drop backtrack stack frames.
        m.ReadStackPointerFromRegister(sp);
        // And take the first backtrack (to &backtrack)
        m.Backtrack();

        m.PushCurrentPosition();
        m.AdvanceCurrentPosition(2);
        m.PopCurrentPosition();

        m.BindJumpTarget(backtrack);
        m.PopRegister(out1);
        m.ReadCurrentPositionFromRegister(out1);
        m.AdvanceCurrentPosition(3);
        m.WriteCurrentPositionToRegister(out2, 0);  // [0,3]

        var loop = new Label();
        m.SetRegister(loopCnt, 0);  // loop counter
        m.Bind(loop);
        m.AdvanceRegister(loopCnt, 1);
        m.AdvanceCurrentPosition(1);
        m.IfRegisterLT(loopCnt, 3, loop);
        m.WriteCurrentPositionToRegister(out3, 0);  // [0,3,6]

        var loop2 = new Label();
        m.SetRegister(loopCnt, 2);  // loop counter
        m.Bind(loop2);
        m.AdvanceRegister(loopCnt, -1);
        m.AdvanceCurrentPosition(1);
        m.IfRegisterGE(loopCnt, 0, loop2);
        m.WriteCurrentPositionToRegister(out4, 0);  // [0,3,6,9]

        var loop3 = new Label();
        var exitLoop3 = new Label();
        m.PushRegister(out4, RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck);
        m.PushRegister(out4, RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck);
        m.ReadCurrentPositionFromRegister(out3);
        m.Bind(loop3);
        m.AdvanceCurrentPosition(1);
        m.CheckFixedLengthLoop(exitLoop3);
        m.GoTo(loop3);
        m.Bind(exitLoop3);
        m.PopCurrentPosition();
        m.WriteCurrentPositionToRegister(out5, 0);  // [0,3,6,9,9,-1]

        m.Succeed();

        m.BindJumpTarget(fail);
        m.Fail();

        object code = GetNativeCode(m, "<loop test>");

        // String long enough for test (content doesn't matter).
        int[] output = new int[6];
        int result = ExecuteNative(code, "foofoofoofoofoo", 0, output);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, output[0]);
        Assert.Equal(3, output[1]);
        Assert.Equal(6, output[2]);
        Assert.Equal(9, output[3]);
        Assert.Equal(9, output[4]);
        Assert.Equal(-1, output[5]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerStackOverflow(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 0, native);

        var loop = new Label();
        m.Bind(loop);
        m.PushBacktrack(loop);
        m.GoTo(loop);

        object code = GetNativeCode(m, "<stack overflow test>");

        // String long enough for test (content doesn't matter).
        int result = ExecuteNative(code, "dummy", 0, null);

        Assert.Equal(IrregexpInterpreter.EXCEPTION, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MacroAssemblerNativeLotsOfRegisters(bool native)
    {
        var m = NewNativeAssembler(RegExpMacroAssembler.Mode.LATIN1, 2, native);

        // At least 2048, to ensure the allocated space for registers
        // span one full page.
        const int largeNumber = 8000;
        m.WriteCurrentPositionToRegister(largeNumber, 42);
        m.WriteCurrentPositionToRegister(0, 0);
        m.WriteCurrentPositionToRegister(1, 1);
        var done = new Label();
        m.CheckNotBackReference(0, false, done);  // Performs a system-stack push.
        m.Bind(done);
        m.PushRegister(largeNumber, RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck);
        m.PopRegister(1);
        m.Succeed();

        object code = GetNativeCode(m, "<huge register space test>");

        int[] captures = new int[2];
        int result = ExecuteNative(code, "sample text", 0, captures, largeNumber + 1);

        Assert.Equal(IrregexpInterpreter.SUCCESS, result);
        Assert.Equal(0, captures[0]);
        Assert.Equal(42, captures[1]);
    }

    [Fact]
    public void MacroAssembler()
    {
        var m = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);
        // ^f(o)o.
        Label start = new(), fail = new(), backtrack = new();

        m.SetRegister(4, 42);
        m.PushRegister(4, RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck);
        m.AdvanceRegister(4, 42);
        m.GoTo(start);
        m.Fail();
        m.Bind(start);
        m.PushBacktrack(fail);
        m.CheckNotAtStart(0, null);
        m.LoadCurrentCharacter(0, null);
        m.CheckNotCharacter('f', null);
        m.LoadCurrentCharacter(1, null);
        m.CheckNotCharacter('o', null);
        m.LoadCurrentCharacter(2, null);
        m.CheckNotCharacter('o', null);
        m.WriteCurrentPositionToRegister(0, 0);
        m.WriteCurrentPositionToRegister(1, 3);
        m.WriteCurrentPositionToRegister(2, 1);
        m.WriteCurrentPositionToRegister(3, 2);
        m.AdvanceCurrentPosition(3);
        m.PushBacktrack(backtrack);
        m.Succeed();
        m.BindJumpTarget(backtrack);
        m.ClearRegisters(2, 3);
        m.Backtrack();
        m.BindJumpTarget(fail);
        m.PopRegister(0);
        m.Fail();

        byte[] array = GetCode(m, "^f(o)o");
        int[] captures = new int[5];

        Assert.Equal(IrregexpInterpreter.SUCCESS,
            IrregexpInterpreter.MatchInternal(array, "foobar", captures, 5, 0, RegExpEngine.kNoBacktrackLimit));
        Assert.Equal(0, captures[0]);
        Assert.Equal(3, captures[1]);
        Assert.Equal(1, captures[2]);
        Assert.Equal(2, captures[3]);
        Assert.Equal(84, captures[4]);

        Array.Clear(captures);
        Assert.Equal(IrregexpInterpreter.FAILURE,
            IrregexpInterpreter.MatchInternal(array, "barfoo", captures, 5, 0, RegExpEngine.kNoBacktrackLimit));
        // Failed matches don't alter output registers.
        Assert.Equal(0, captures[0]);
        Assert.Equal(0, captures[1]);
        Assert.Equal(0, captures[2]);
        Assert.Equal(0, captures[3]);
        Assert.Equal(0, captures[4]);
    }

    // V8 computes this with ICU's root-locale toUpper; the oracle's
    // String.prototype.toUpperCase is that same function. The oracle (V8 14.7)
    // ships an older Unicode version than the tables here (Unicode 17), so
    // code points it does not know (\p{Cn} there) are reported and skipped.
    static (int[] Canonical, bool[] UnknownToOracle) CanonicalizeNonUnicodeTable()
    {
        using var v8 = new V8Sharp.Oracle.ReferenceV8(allowNativesSyntax: false);
        string s = v8.Eval("""
            (() => {
              const out = [];
              for (let c = 0; c <= 0xffff; c++) {
                const ch = String.fromCharCode(c);
                const u = ch.toUpperCase();
                let r = u.length !== 1 ? c : u.charCodeAt(0);
                if (c >= 128 && r < 128) r = c;
                out.push(/\p{Cn}/u.test(ch) ? -r - 1 : r);
              }
              return out.join(',');
            })()
            """);
        string[] parts = s.Split(',');
        var result = new int[0x10000];
        var unknown = new bool[0x10000];
        for (int i = 0; i < result.Length; i++)
        {
            int v = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
            if (v < 0)
            {
                unknown[i] = true;
                v = -v - 1;
            }
            result[i] = v;
        }
        return (result, unknown);
    }

    [Fact]
    public void NonUnicodeCaseEquivalence()
    {
        (int[] canonical, bool[] unknownToOracle) = CanonicalizeNonUnicodeTable();
        var classes = new List<int>[0x10000];
        for (int c = 0; c <= 0xffff; ++c) (classes[canonical[c]] ??= []).Add(c);

        // Case classes that involve a code point the oracle does not know.
        var tainted = new bool[0x10000];
        for (int c = 0; c <= 0xffff; ++c)
        {
            if (!unknownToOracle[c]) continue;
            var closure = new CodePointSet(c, c);
            CaseFolding.CloseOver(closure, CaseFolding.Mode.kNonUnicode);
            for (int r = 0; r < closure.RangeCount; r++)
            {
                for (int m = closure.GetRangeStart(r); m <= closure.GetRangeEnd(r) && m <= 0xffff; m++)
                {
                    tainted[m] = true;
                    tainted[canonical[m]] = true;
                }
            }
        }

        int[] canonicalToKey = new int[0x10000];
        int[] keyToCanonical = new int[0x10000];
        Array.Fill(canonicalToKey, -1);
        Array.Fill(keyToCanonical, -1);
        int checkedCount = 0;
        for (int c = 0; c <= 0xffff; ++c)
        {
            if (tainted[c] || tainted[canonical[c]]) continue;
            checkedCount++;
            int key = CaseFolding.EquivalenceKey(c, CaseFolding.Mode.kNonUnicode);
            Assert.True(key >= 0);
            Assert.True(key <= 0xffff);
            if (canonicalToKey[canonical[c]] == -1) canonicalToKey[canonical[c]] = key;
            if (keyToCanonical[key] == -1) keyToCanonical[key] = canonical[c];
            Assert.True(canonicalToKey[canonical[c]] == key, $"{c:x}");
            Assert.True(keyToCanonical[key] == canonical[c], $"{c:x}");

            var expected = new CodePointSet();
            foreach (int member in classes[canonical[c]]) expected.Add(member);
            var actual = new CodePointSet(c, c);
            CaseFolding.CloseOver(actual, CaseFolding.Mode.kNonUnicode);
            Assert.True(expected.SetEquals(actual), $"{c:x}");
        }
        Assert.True(checkedCount > 0xf000);
    }

    [Fact]
    public void CaseClosureMixedSets()
    {
        foreach (CaseFolding.Mode mode in new[] { CaseFolding.Mode.kNonUnicode, CaseFolding.Mode.kUnicode })
        {
            var actual = new CodePointSet();
            actual.Add('a', 'c');
            actual.Add('k');
            actual.Add(0x017f);
            actual.Add(0x00df);
            var expected = new CodePointSet(actual);
            expected.Add('A', 'C');
            expected.Add('K');
            if (mode == CaseFolding.Mode.kUnicode)
            {
                expected.Add('s');
                expected.Add('S');
                expected.Add(0x212a);
                expected.Add(0x1e9e);
                actual.Add(0x10400);
                expected.Add(0x10400);
                expected.Add(0x10428);
            }
            CaseFolding.CloseOver(actual, mode);
            Assert.True(expected.SetEquals(actual));
            CaseFolding.CloseOver(actual, mode);
            Assert.True(expected.SetEquals(actual));
        }
    }

    static void TestRangeCaseIndependence(CharacterRange input, CharacterRange[] expected)
    {
        int count = expected.Length;
        var list = new List<CharacterRange>(count) { input };
        CharacterRange.AddCaseEquivalents(list, false);
        list.RemoveAt(0);  // Remove the input before checking results.
        Assert.Equal(count, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            Assert.Equal(expected[i].From, list[i].From);
            Assert.Equal(expected[i].To, list[i].To);
        }
    }

    static void TestSimpleRangeCaseIndependence(CharacterRange input, CharacterRange expected) =>
        TestRangeCaseIndependence(input, [expected]);

    [Fact]
    public void CharacterRangeCaseIndependence()
    {
        TestSimpleRangeCaseIndependence(CharacterRange.Singleton('a'), CharacterRange.Singleton('A'));
        TestSimpleRangeCaseIndependence(CharacterRange.Singleton('z'), CharacterRange.Singleton('Z'));
        TestSimpleRangeCaseIndependence(CharacterRange.Range('c', 'f'), CharacterRange.Range('C', 'F'));
        TestSimpleRangeCaseIndependence(CharacterRange.Range('a', 'b'), CharacterRange.Range('A', 'B'));
        TestSimpleRangeCaseIndependence(CharacterRange.Range('y', 'z'), CharacterRange.Range('Y', 'Z'));
        TestSimpleRangeCaseIndependence(CharacterRange.Range('C', 'F'), CharacterRange.Range('c', 'f'));
    }

    static bool InClass(int c, List<CharacterRange>? ranges)
    {
        if (ranges is null) return false;
        for (int i = 0; i < ranges.Count; i++)
        {
            CharacterRange range = ranges[i];
            if (range.From <= c && c <= range.To) return true;
        }
        return false;
    }

    [Fact]
    public void UnicodeRangeSplitter()
    {
        var @base = new List<CharacterRange>(1) { CharacterRange.Everything() };
        var splitter = new V8Sharp.RegExp.UnicodeRangeSplitter(@base);
        // BMP
        for (int c = 0; c < 0xD800; c++)
        {
            Assert.True(InClass(c, splitter.Bmp));
            Assert.False(InClass(c, splitter.LeadSurrogates));
            Assert.False(InClass(c, splitter.TrailSurrogates));
            Assert.False(InClass(c, splitter.NonBmp));
        }
        // Lead surrogates
        for (int c = 0xD800; c < 0xDBFF; c++)
        {
            Assert.False(InClass(c, splitter.Bmp));
            Assert.True(InClass(c, splitter.LeadSurrogates));
            Assert.False(InClass(c, splitter.TrailSurrogates));
            Assert.False(InClass(c, splitter.NonBmp));
        }
        // Trail surrogates
        for (int c = 0xDC00; c < 0xDFFF; c++)
        {
            Assert.False(InClass(c, splitter.Bmp));
            Assert.False(InClass(c, splitter.LeadSurrogates));
            Assert.True(InClass(c, splitter.TrailSurrogates));
            Assert.False(InClass(c, splitter.NonBmp));
        }
        // BMP
        for (int c = 0xE000; c < 0xFFFF; c++)
        {
            Assert.True(InClass(c, splitter.Bmp));
            Assert.False(InClass(c, splitter.LeadSurrogates));
            Assert.False(InClass(c, splitter.TrailSurrogates));
            Assert.False(InClass(c, splitter.NonBmp));
        }
        // Non-BMP
        for (int c = 0x10000; c < 0x10FFFF; c++)
        {
            Assert.False(InClass(c, splitter.Bmp));
            Assert.False(InClass(c, splitter.LeadSurrogates));
            Assert.False(InClass(c, splitter.TrailSurrogates));
            Assert.True(InClass(c, splitter.NonBmp));
        }
    }

    [Fact]
    public void CanonicalizeCharacterSets()
    {
        var list = new List<CharacterRange>(4);
        var set = new CharacterSet(list);

        list.Add(CharacterRange.Range(10, 20));
        list.Add(CharacterRange.Range(30, 40));
        list.Add(CharacterRange.Range(50, 60));
        set.Canonicalize();
        Assert.Equal(3, list.Count);
        Assert.Equal(10, list[0].From);
        Assert.Equal(20, list[0].To);
        Assert.Equal(30, list[1].From);
        Assert.Equal(40, list[1].To);
        Assert.Equal(50, list[2].From);
        Assert.Equal(60, list[2].To);

        list.Clear();
        list.Add(CharacterRange.Range(10, 20));
        list.Add(CharacterRange.Range(50, 60));
        list.Add(CharacterRange.Range(30, 40));
        set.Canonicalize();
        Assert.Equal(3, list.Count);
        Assert.Equal(10, list[0].From);
        Assert.Equal(20, list[0].To);
        Assert.Equal(30, list[1].From);
        Assert.Equal(40, list[1].To);
        Assert.Equal(50, list[2].From);
        Assert.Equal(60, list[2].To);

        list.Clear();
        list.Add(CharacterRange.Range(30, 40));
        list.Add(CharacterRange.Range(10, 20));
        list.Add(CharacterRange.Range(25, 25));
        list.Add(CharacterRange.Range(100, 100));
        list.Add(CharacterRange.Range(1, 1));
        set.Canonicalize();
        Assert.Equal(5, list.Count);
        Assert.Equal(1, list[0].From);
        Assert.Equal(1, list[0].To);
        Assert.Equal(10, list[1].From);
        Assert.Equal(20, list[1].To);
        Assert.Equal(25, list[2].From);
        Assert.Equal(25, list[2].To);
        Assert.Equal(30, list[3].From);
        Assert.Equal(40, list[3].To);
        Assert.Equal(100, list[4].From);
        Assert.Equal(100, list[4].To);

        list.Clear();
        list.Add(CharacterRange.Range(10, 19));
        list.Add(CharacterRange.Range(21, 30));
        list.Add(CharacterRange.Range(20, 20));
        set.Canonicalize();
        Assert.Single(list);
        Assert.Equal(10, list[0].From);
        Assert.Equal(30, list[0].To);
    }

    [Fact]
    public void CharacterRangeMerge()
    {
        var l1 = new List<CharacterRange>(4);
        var l2 = new List<CharacterRange>(4);
        // Create all combinations of intersections of ranges, both singletons and
        // longer.

        int offset = 0;

        // The five kinds of singleton intersections.
        for (int i = 0; i < 5; i++)
        {
            l1.Add(CharacterRange.Singleton(offset + 2));
            l2.Add(CharacterRange.Singleton(offset + i));
            offset += 6;
        }

        // The seven kinds of singleton/non-singleton intersections.
        for (int i = 0; i < 7; i++)
        {
            l1.Add(CharacterRange.Range(offset + 2, offset + 4));
            l2.Add(CharacterRange.Singleton(offset + i));
            offset += 8;
        }

        // The eleven kinds of non-singleton intersections.
        for (int i = 0; i < 9; i++)
        {
            l1.Add(CharacterRange.Range(offset + 6, offset + 15));  // Length 8.
            l2.Add(CharacterRange.Range(offset + 2 * i, offset + 2 * i + 3));
            offset += 22;
        }
        l1.Add(CharacterRange.Range(offset + 6, offset + 15));
        l2.Add(CharacterRange.Range(offset + 6, offset + 15));
        offset += 22;
        l1.Add(CharacterRange.Range(offset + 6, offset + 15));
        l2.Add(CharacterRange.Range(offset + 4, offset + 17));
        offset += 22;

        // Different kinds of multi-range overlap.
        l1.Add(CharacterRange.Range(offset, offset + 21));
        l1.Add(CharacterRange.Range(offset + 31, offset + 52));
        for (int i = 0; i < 6; i++)
        {
            l2.Add(CharacterRange.Range(offset + 2, offset + 5));
            l2.Add(CharacterRange.Singleton(offset + 8));
            offset += 9;
        }

        Assert.True(CharacterRange.IsCanonical(l1));
        Assert.True(CharacterRange.IsCanonical(l2));
    }

    [Fact]
    public void Graph() => Execute("\\b\\w+\\b", false, true, true);

    [Fact]
    public void AssertionMergingToNode()
    {
        static int CountAllNodes(RegExpNode root)
        {
            var visited = new HashSet<RegExpNode>(ReferenceEqualityComparer.Instance);
            var worklist = new List<RegExpNode?> { root };
            while (worklist.Count != 0)
            {
                RegExpNode? curr = worklist[^1];
                worklist.RemoveAt(worklist.Count - 1);
                if (curr is null || !visited.Add(curr)) continue;

                ChoiceNode? choice = curr.AsChoiceNode();
                if (choice is not null)
                {
                    foreach (GuardedAlternative alt in choice.Alternatives) worklist.Add(alt.Node);
                }
                else if (curr.AsSeqNode() is not null)
                {
                    worklist.Add(curr.AsSeqNode()!.OnSuccess);
                }
            }
            return visited.Count;
        }

        RegExpNode? g1 = Compile("\\b", false, false, true);
        RegExpNode? g2 = Compile("\\b\\b", false, false, true);
        RegExpNode? g3 = Compile("\\b(?:\\b)", false, false, true);
        RegExpNode? g4 = Compile("\\b(?i:\\b)", false, false, true);
        RegExpNode? g5 = Compile("\\b(\\b)", false, false, true);
        RegExpNode? g6 = Compile("\\b(?=\\b)", false, false, true);

        Assert.NotNull(g1);
        Assert.NotNull(g2);
        Assert.NotNull(g3);
        Assert.NotNull(g4);
        Assert.NotNull(g5);
        Assert.NotNull(g6);

        int c1 = CountAllNodes(g1!);
        int c2 = CountAllNodes(g2!);
        int c3 = CountAllNodes(g3!);

        // Same-flag non-capturing groups are flattened and assertions folded during
        // ToNode:
        Assert.Equal(c1, c2);
        Assert.Equal(c1, c3);

        // Different-flag groups, capturing groups, and lookarounds preserve
        // boundaries:
        Assert.NotEqual(c1, CountAllNodes(g4!));
        Assert.NotEqual(c1, CountAllNodes(g5!));
        Assert.NotEqual(c1, CountAllNodes(g6!));
    }

    [Fact(Skip = "Engine-level: exercises RegExp.prototype use counters through JS.")]
    public void UseCountRegExp()
    {
    }

    [Fact(Skip = "Engine-level: exercises uncached external strings through JS.")]
    public void UncachedExternalString()
    {
    }

    // Test bytecode peephole optimization

    static REB GetBytecode(byte[] bytecodeArray, int i) => Bytecodes.FromByte(bytecodeArray[i]);

    static uint ReadU32(byte[] a, int pos) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(pos));

    static byte[] GetCodeWithPeephole(RegExpBytecodeGenerator m, bool peephole, string source)
    {
        m.PeepholeOptimization = peephole;
        return (byte[])m.GetCode(source, RegExpFlags.None);
    }

    static void CreatePeepholeNoChangeBytecode(RegExpMacroAssembler m)
    {
        Label fail = new(), backtrack = new();
        m.PushBacktrack(fail);
        m.CheckNotAtStart(0, null);
        m.LoadCurrentCharacter(2, null);
        m.CheckNotCharacter('o', null);
        m.LoadCurrentCharacter(1, null, false);
        m.CheckNotCharacter('o', null);
        m.LoadCurrentCharacter(0, null, false);
        m.CheckNotCharacter('f', null);
        m.WriteCurrentPositionToRegister(0, 0);
        m.WriteCurrentPositionToRegister(1, 3);
        m.AdvanceCurrentPosition(3);
        m.PushBacktrack(backtrack);
        m.Succeed();
        m.Bind(backtrack);
        m.Backtrack();
        m.Bind(fail);
        m.Fail();
    }

    [Fact]
    public void PeepholeNoChange()
    {
        var orig = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);
        var opt = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);

        CreatePeepholeNoChangeBytecode(orig);
        CreatePeepholeNoChangeBytecode(opt);

        byte[] array = GetCodeWithPeephole(orig, false, "^foo");
        byte[] arrayOptimized = GetCodeWithPeephole(opt, true, "^foo");

        Assert.True(array.AsSpan().SequenceEqual(arrayOptimized.AsSpan(0, array.Length)));
    }

    static void CreatePeepholeSkipUntilBitInTableBytecode(RegExpMacroAssembler m)
    {
        byte[] bitTable = new byte[RegExpMacroAssembler.kTableSize];

        var start = new Label();
        m.Bind(start);
        m.LoadCurrentCharacter(0, null, true);
        m.CheckBitInTable(bitTable, null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
    }

    static void CheckPeephole(Action<RegExpMacroAssembler> create, REB[] expected, REB optimized)
    {
        var orig = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);
        var opt = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);

        create(orig);
        create(opt);

        byte[] array = GetCodeWithPeephole(orig, false, "dummy");
        int length = array.Length;

        byte[] arrayOptimized = GetCodeWithPeephole(opt, true, "dummy");
        int lengthOptimized = arrayOptimized.Length;

        int lengthExpected = 0;
        foreach (REB bc in expected) lengthExpected += Bytecodes.Size(bc);
        lengthExpected += Bytecodes.Size(REB.kBacktrack);
        int lengthOptimizedExpected = Bytecodes.Size(optimized) + Bytecodes.Size(REB.kBacktrack);

        Assert.Equal(length, lengthExpected);
        Assert.Equal(lengthOptimized, lengthOptimizedExpected);

        Assert.Equal(optimized, GetBytecode(arrayOptimized, 0));
        Assert.Equal(REB.kBacktrack, GetBytecode(arrayOptimized, Bytecodes.Size(optimized)));
    }

    [Fact]
    public void PeepholeSkipUntilBitInTable() =>
        CheckPeephole(CreatePeepholeSkipUntilBitInTableBytecode,
            [REB.kLoadCurrentCharacter, REB.kCheckBitInTable, REB.kAdvanceCpAndGoto], REB.kSkipUntilBitInTable);

    static void CreatePeepholeSkipUntilCharBytecode(RegExpMacroAssembler m)
    {
        var start = new Label();
        m.Bind(start);
        m.LoadCurrentCharacter(0, null, true, 1, 2);
        m.CheckCharacter('x', null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
    }

    [Fact]
    public void PeepholeSkipUntilChar() =>
        CheckPeephole(CreatePeepholeSkipUntilCharBytecode,
            [REB.kLoadCurrentCharacter, REB.kCheckCharacter, REB.kAdvanceCpAndGoto], REB.kSkipUntilChar);

    static void CreatePeepholeSkipUntilCharAndBytecode(RegExpMacroAssembler m)
    {
        var start = new Label();
        m.Bind(start);
        m.LoadCurrentCharacter(0, null, true, 1, 2);
        m.CheckCharacterAfterAnd('x', 0xFF, null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
    }

    [Fact]
    public void PeepholeSkipUntilCharAnd() =>
        CheckPeephole(CreatePeepholeSkipUntilCharAndBytecode,
            [REB.kLoadCurrentCharacter, REB.kCheckCharacterAfterAnd, REB.kAdvanceCpAndGoto], REB.kSkipUntilCharAnd);

    static void CreatePeepholeSkipUntilCharOrCharBytecode(RegExpMacroAssembler m)
    {
        var start = new Label();
        m.Bind(start);
        m.LoadCurrentCharacter(0, null, true);
        m.CheckCharacter('x', null);
        m.CheckCharacter('y', null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
    }

    [Fact]
    public void PeepholeSkipUntilCharOrChar() =>
        CheckPeephole(CreatePeepholeSkipUntilCharOrCharBytecode,
            [REB.kLoadCurrentCharacter, REB.kCheckCharacter, REB.kCheckCharacter, REB.kAdvanceCpAndGoto],
            REB.kSkipUntilCharOrChar);

    static void CreatePeepholeSkipUntilGtOrNotBitInTableBytecode(RegExpMacroAssembler m)
    {
        byte[] bitTable = new byte[RegExpMacroAssembler.kTableSize];

        Label start = new(), end = new(), advance = new();
        m.Bind(start);
        m.LoadCurrentCharacter(0, null, true);
        m.CheckCharacterGT('x', null);
        m.CheckBitInTable(bitTable, advance);
        m.GoTo(end);
        m.Bind(advance);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
        m.Bind(end);
    }

    [Fact]
    public void PeepholeSkipUntilGtOrNotBitInTable() =>
        CheckPeephole(CreatePeepholeSkipUntilGtOrNotBitInTableBytecode,
            [REB.kLoadCurrentCharacter, REB.kCheckCharacterGT, REB.kCheckBitInTable, REB.kGoTo, REB.kAdvanceCpAndGoto],
            REB.kSkipUntilGtOrNotBitInTable);

    static void CreatePeepholeLabelFixupsInsideBytecode(RegExpMacroAssembler m, Label dummyBefore,
        Label dummyAfter, Label dummyInside)
    {
        var loop = new Label();
        m.Bind(dummyBefore);
        m.LoadCurrentCharacter(0, dummyBefore);
        m.CheckCharacter('a', dummyAfter);
        m.CheckCharacter('b', dummyInside);
        m.Bind(loop);
        m.LoadCurrentCharacter(0, null, true);
        m.CheckCharacter('x', null);
        m.Bind(dummyInside);
        m.CheckCharacter('y', null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(loop);
        m.Bind(dummyAfter);
        m.LoadCurrentCharacter(0, dummyBefore);
        m.CheckCharacter('a', dummyAfter);
        m.CheckCharacter('b', dummyInside);
    }

    [Fact]
    public void PeepholeLabelFixupsInside()
    {
        var orig = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);
        var opt = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);

        CreatePeepholeLabelFixupsInsideBytecode(opt, new Label(), new Label(), new Label());
        Label dummyBefore = new(), dummyAfter = new(), dummyInside = new();
        CreatePeepholeLabelFixupsInsideBytecode(orig, dummyBefore, dummyAfter, dummyInside);

        Assert.Equal(0x00, dummyBefore.Pos);
        Assert.Equal(0x30, dummyInside.Pos);
        Assert.Equal(0x40, dummyAfter.Pos);

        Label[] labels = [dummyBefore, dummyAfter, dummyInside];
        int[][] labelPositions =
        [
            [0x08, 0x48],  // dummy_before
            [0x10, 0x50],  // dummy after
            [0x18, 0x58],  // dummy inside
        ];

        byte[] array = GetCodeWithPeephole(orig, false, "dummy");

        for (int labelIdx = 0; labelIdx < 3; labelIdx++)
        {
            for (int posIdx = 0; posIdx < 2; posIdx++)
            {
                Assert.Equal(labels[labelIdx].Pos, array[labelPositions[labelIdx][posIdx]]);
            }
        }

        byte[] arrayOptimized = GetCodeWithPeephole(opt, true, "dummy");

        int[] posFixups =
        [
            0,  // Position before optimization should be unchanged.
            4,  // Position after first replacement should be 4 (optimized size (20) -
                // original size (32) + preserve length (16)).
        ];
        int[] targetFixups =
        [
            0,  // dummy_before should be unchanged
            4,  // dummy_inside should be 4
            4,  // dummy_after should be 4
        ];

        for (int labelIdx = 0; labelIdx < 3; labelIdx++)
        {
            for (int posIdx = 0; posIdx < 2; posIdx++)
            {
                int labelPos = labelPositions[labelIdx][posIdx] + posFixups[posIdx];
                int jumpAddress = (int)ReadU32(arrayOptimized, labelPos);
                int expectedJumpAddress = labels[labelIdx].Pos + targetFixups[labelIdx];
                Assert.Equal(expectedJumpAddress, jumpAddress);
            }
        }
    }

    static void CreatePeepholeLabelFixupsComplexBytecode(RegExpMacroAssembler m, Label dummyBefore,
        Label dummyBetween, Label dummyAfter, Label dummyInside)
    {
        Label loop1 = new(), loop2 = new();
        m.Bind(dummyBefore);
        m.LoadCurrentCharacter(0, dummyBefore);
        m.CheckCharacter('a', dummyBetween);
        m.CheckCharacter('b', dummyAfter);
        m.CheckCharacter('c', dummyInside);
        m.Bind(loop1);
        m.LoadCurrentCharacter(0, null, true);
        m.CheckCharacter('x', null);
        m.CheckCharacter('y', null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(loop1);
        m.Bind(dummyBetween);
        m.LoadCurrentCharacter(0, dummyBefore);
        m.CheckCharacter('a', dummyBetween);
        m.CheckCharacter('b', dummyAfter);
        m.CheckCharacter('c', dummyInside);
        m.Bind(loop2);
        m.LoadCurrentCharacter(0, null, true);
        m.CheckCharacter('x', null);
        m.Bind(dummyInside);
        m.CheckCharacter('y', null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(loop2);
        m.Bind(dummyAfter);
        m.LoadCurrentCharacter(0, dummyBefore);
        m.CheckCharacter('a', dummyBetween);
        m.CheckCharacter('b', dummyAfter);
        m.CheckCharacter('c', dummyInside);
    }

    [Fact]
    public void PeepholeLabelFixupsComplex()
    {
        var orig = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);
        var opt = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);

        CreatePeepholeLabelFixupsComplexBytecode(opt, new Label(), new Label(), new Label(), new Label());
        Label dummyBefore = new(), dummyBetween = new(), dummyAfter = new(), dummyInside = new();
        CreatePeepholeLabelFixupsComplexBytecode(orig, dummyBefore, dummyBetween, dummyAfter, dummyInside);

        Assert.Equal(0x00, dummyBefore.Pos);
        Assert.Equal(0x48, dummyBetween.Pos);
        Assert.Equal(0x80, dummyInside.Pos);
        Assert.Equal(0x90, dummyAfter.Pos);

        Label[] labels = [dummyBefore, dummyBetween, dummyAfter, dummyInside];
        int[][] labelPositions =
        [
            [0x08, 0x50, 0x98],  // dummy_before
            [0x10, 0x58, 0xa0],  // dummy between
            [0x18, 0x60, 0xa8],  // dummy after
            [0x20, 0x68, 0xb0],  // dummy inside
        ];

        byte[] array = GetCodeWithPeephole(orig, false, "dummy");

        for (int labelIdx = 0; labelIdx < 4; labelIdx++)
        {
            for (int posIdx = 0; posIdx < 3; posIdx++)
            {
                Assert.Equal(labels[labelIdx].Pos, array[labelPositions[labelIdx][posIdx]]);
            }
        }

        byte[] arrayOptimized = GetCodeWithPeephole(opt, true, "dummy");

        int[] posFixups =
        [
            0,    // Position before optimization should be unchanged.
            -12,  // Position after first replacement should be -12 (optimized size =
                  // 20 - 32 = original size).
            -8,   // Position after second replacement should be -8 (-12 from first
                  // optimization -12 from second optimization + 16 preserved
                  // bytecodes).
        ];
        int[] targetFixups =
        [
            0,    // dummy_before should be unchanged
            -12,  // dummy_between should be -12
            -8,   // dummy_inside should be -8
            -8,   // dummy_after should be -8
        ];

        for (int labelIdx = 0; labelIdx < 4; labelIdx++)
        {
            for (int posIdx = 0; posIdx < 3; posIdx++)
            {
                int labelPos = labelPositions[labelIdx][posIdx] + posFixups[posIdx];
                int jumpAddress = (int)ReadU32(arrayOptimized, labelPos);
                int expectedJumpAddress = labels[labelIdx].Pos + targetFixups[labelIdx];
                Assert.Equal(expectedJumpAddress, jumpAddress);
            }
        }
    }

    static void EmitMasked3PrecursorRegion(RegExpMacroAssembler m, byte[] bitTable)
    {
        // Region A precursor for SkipUntilOneOfMasked3. The SkipUntilBitInTable
        // header is itself peephole-derived (LCC+CBIT+ACAG) so that the Masked3 fold
        // is delayed to pass 3.
        Label start = new(), afterLoop = new(), bc4 = new(), bc5 = new();
        m.Bind(start);
        m.LoadCurrentCharacter(0, afterLoop, true);
        m.CheckBitInTable(bitTable, null);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
        m.Bind(afterLoop);
        m.LoadCurrentCharacter(0, null, true, 4, 4);
        m.CheckCharacterAfterAnd(0x61626364, 0xffffffff, bc5);
        m.Bind(bc4);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
        m.Bind(bc5);
        m.LoadCurrentCharacter(0, bc4, true, 4);
        m.CheckCharacterAfterAnd(0x65666768, 0xffffffff, null);
        m.CheckCharacterAfterAnd(0x696a6b6c, 0xffffffff, null);
        m.CheckNotCharacterAfterAnd(0x6d6e6f70, 0xffffffff, bc4);
    }

    static void EmitMaskedPrecursorRegion(RegExpMacroAssembler m)
    {
        // Region B precursor for SkipUntilOneOfMasked. Folds in pass 2 and emits
        // on_match2 via kOffsetAfterSequence.
        Label start = new(), second = new(), adv = new();
        m.Bind(start);
        m.LoadCurrentCharacter(0, null, true, 4, 4);
        m.CheckCharacterAfterAnd(0x61616161, 0xffffffff, second);
        m.Bind(adv);
        m.AdvanceCurrentPosition(1);
        m.GoTo(start);
        m.Bind(second);
        m.CheckCharacterAfterAnd(0x62626262, 0xffffffff, null);
        m.CheckNotCharacterAfterAnd(0x63636363, 0xffffffff, adv);
    }

    [Fact]
    public void PeepholeOffsetAfterSequenceTrackedAcrossPasses()
    {
        var opt = new RegExpBytecodeGenerator(RegExpMacroAssembler.Mode.LATIN1);
        byte[] bitTable = new byte[RegExpMacroAssembler.kTableSize];
        EmitMasked3PrecursorRegion(opt, bitTable);
        EmitMasked3PrecursorRegion(opt, bitTable);
        EmitMaskedPrecursorRegion(opt);

        byte[] arrayOptimized = GetCodeWithPeephole(opt, true, "dummy");
        int lengthOptimized = arrayOptimized.Length;

        // Find the SkipUntilOneOfMasked emitted by pass 2 and verify its on_match2
        // operand was properly remapped through the pass-3 shrink.
        int pc = 0;
        bool found = false;
        while (pc < lengthOptimized)
        {
            REB bc = GetBytecode(arrayOptimized, pc);
            if (bc == REB.kSkipUntilOneOfMasked)
            {
                int kOnMatch2Off = Bytecodes.Info(REB.kSkipUntilOneOfMasked).Offset("on_match2");
                uint onMatch2 = ReadU32(arrayOptimized, pc + kOnMatch2Off);
                Assert.True(onMatch2 < lengthOptimized);
                Assert.True(Bytecodes.IsValidJumpTarget(arrayOptimized[onMatch2]));
                found = true;
            }
            pc += Bytecodes.Size(bc);
        }
        Assert.True(found);
    }

    [Fact]
    public void UnicodePropertyEscapeCodeSize()
    {
        // FlagScope<bool> f(&v8_flags.regexp_tier_up, false).
        CompiledRegExp re = RegExpEngine.Compile("\\p{L}\\p{L}\\p{L}", RegExpFlags.Unicode,
            RegExpEngine.kNoBacktrackLimit, RegExpTierPolicy.NativeOnly).RegExp!;
        int[] regs = new int[re.RegistersPerMatch];
        re.Exec("\u200b", 0, regs);

        const int kMaxSize = 200 * 1024;
        const bool kIsNotLatin1 = false;
        if (re.GetBytecode(kIsNotLatin1) is { } bytecode)
        {
            // On x64, excessive inlining produced >250KB.
            Assert.True(bytecode.Length < kMaxSize);
        }
        else if (re.GetNativeCode(kIsNotLatin1) is { } code)
        {
            // On x64, excessive inlining produced >360KB.
            Assert.True(code.ILSize < kMaxSize);
        }
        else
        {
            Assert.Fail("UNREACHABLE");
        }
    }

    [Fact(Skip = "Engine-level: needs isolate interrupts (RequestInterrupt) and native irregexp.")]
    public void RegExpInterruptReentrantExecution()
    {
    }

    [Fact]
    public void QuickCheckDeterminesPerfectly()
    {
        // Create an EndNode to serve as the successor.
        var accept = new EndNode(EndNode.Action.ACCEPT, RegExpFlags.None);

        bool CheckDeterminesPerfectly(List<CharacterRange> ranges, bool isOneByte)
        {
            TextNode node = TextNode.CreateForCharacterRanges(ranges, false, accept, RegExpFlags.None);
            var compiler = new RegExpCompiler(0, RegExpFlags.None, isOneByte);
            var details = new QuickCheckDetails(1);
            node.GetQuickCheckDetails(details, compiler, 0, false, RegExpNode.kRecursionBudget);
            return details.Positions(0).DeterminesPerfectly;
        }

        // 1. Singleton range 'a' (always perfect)
        Assert.True(CheckDeterminesPerfectly([CharacterRange.Singleton('a')], true));

        // 2. Disjoint ranges containing 'a' and 'A' (perfect, differing in 1 bit:
        // 0x20)
        Assert.True(CheckDeterminesPerfectly([CharacterRange.Singleton('a'), CharacterRange.Singleton('A')], true));

        // 3. Disjoint ranges 'a', 'A', 'b', 'B' (not perfect, zero bits = 3 (0x20,
        // 0x02, 0x01), combinations = 8, total = 4)
        Assert.False(CheckDeterminesPerfectly(
        [
            CharacterRange.Singleton('a'), CharacterRange.Singleton('A'), CharacterRange.Singleton('b'),
            CharacterRange.Singleton('B'),
        ], true));

        // 4. Non-canonical ranges that overlap but can be canonicalized into a
        // perfect check.
        Assert.True(CheckDeterminesPerfectly([CharacterRange.Range(0, 2), CharacterRange.Range(1, 3)], true));

        // 5. Perfect single range [\x60-\x63] (diff = 3, contiguous block of trailing
        // 1s, starts at boundary)
        Assert.True(CheckDeterminesPerfectly([CharacterRange.Range(0x60, 0x63)], true));

        // 6. Imperfect single range [\x61-\x62] (diff = 3, starts at incorrect
        // boundary, e.g. not aligned with block of 4)
        Assert.False(CheckDeterminesPerfectly([CharacterRange.Range(0x61, 0x62)], true));

        // 7. Imperfect single range [\x60-\x62] (diff = 2, non-contiguous block of
        // trailing 1s, i.e. bit 0 is fixed)
        Assert.False(CheckDeterminesPerfectly([CharacterRange.Range(0x60, 0x62)], true));
    }
}
