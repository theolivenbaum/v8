namespace V8Sharp.RegExp.Tests;

public class SmokeTests
{
    [Theory]
    [InlineData("abc", "", "xxabcxx", "[2,\"abc\"]")]
    [InlineData("a(b)c", "", "xxabcxx", "[2,\"abc\",\"b\"]")]
    [InlineData("a+", "", "baaab", "[1,\"aaa\"]")]
    [InlineData("a*?b", "", "aab", "[0,\"aab\"]")]
    [InlineData("(a|ab)(c|bcd)(d*)", "", "abcd", "[0,\"abcd\",\"a\",\"bcd\",\"\"]")]
    [InlineData("^abc$", "m", "x\nabc\ny", "[2,\"abc\"]")]
    [InlineData("\\bfoo\\b", "", "a foo b", "[2,\"foo\"]")]
    [InlineData("(?<=\\$)\\d+", "", "cost $42", "[6,\"42\"]")]
    [InlineData("(?<!\\$)\\d+", "", "$42 17", "[2,\"2\"]")]
    [InlineData("(a)\\1", "i", "aA", "[0,\"aA\",\"a\"]")]
    [InlineData("\\u{1F600}", "u", "x😀", "[1,\"😀\"]")]
    [InlineData("[^a]", "u", "😀", "[0,\"😀\"]")]
    [InlineData("\\p{Lu}+", "u", "abcDEFg", "[3,\"DEF\"]")]
    [InlineData("(?<year>\\d{4})-(?<month>\\d{2})", "", "on 2024-05-01", "[3,\"2024-05\",\"2024\",\"05\"]")]
    [InlineData("x{2,3}", "", "xxxxx", "[0,\"xxx\"]")]
    [InlineData("(?:a|b)*c", "", "ababx", "null")]
    [InlineData("[\\p{L}--[a-z]]", "v", "abcD", "[3,\"D\"]")]
    [InlineData("[\\q{abc|d}]", "v", "xabc", "[1,\"abc\"]")]
    [InlineData("k", "iu", "K", "[0,\"K\"]")]
    [InlineData("ſ", "i", "s", "null")]
    [InlineData("(?i:a)b", "", "Ab", "[0,\"Ab\"]")]
    [InlineData("a$", "", "ba", "[1,\"a\"]")]
    [InlineData("(", "", "", "SyntaxError: Unterminated group")]
    public void Exec(string pattern, string flags, string subject, string expected)
    {
        Assert.Equal(expected, RegExpTestHelpers.Exec(pattern, flags, subject));
    }
}
