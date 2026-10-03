// Sanity checks of the unibrow case-mapping tables (src/strings/unicode.cc,
// the !V8_INTL_SUPPORT configuration). V8 has no unit test for them; these
// check known mappings, including the multi-character and context-dependent
// special cases, and compare the simple mappings with .NET's invariant data.

using System.Globalization;
using V8Sharp.Base.Unibrow;

namespace V8Sharp.Base.Tests.Strings;

public class UnibrowTablesTest
{
    static uint[] Map<T>(uint c, uint next = 0) where T : ICharMapping
    {
        Span<uint> result = stackalloc uint[Unicode.kMaxMappingSize];
        bool allowCaching = true;
        int n = T.Convert(c, next, result, ref allowCaching);
        return result[..n].ToArray();
    }

    [Fact]
    public void AsciiAndLatin1()
    {
        for (uint c = 'a'; c <= 'z'; c++) Assert.Equal([c - 32], Map<ToUppercase>(c));
        for (uint c = 'A'; c <= 'Z'; c++) Assert.Equal([c + 32], Map<ToLowercase>(c));
        Assert.Empty(Map<ToUppercase>('A'));
        Assert.Empty(Map<ToLowercase>('1'));
        // U+00DF LATIN SMALL LETTER SHARP S uppercases to "SS".
        Assert.Equal([(uint)'S', 'S'], Map<ToUppercase>(0xDF));
        // U+00FF uppercases to U+0178.
        Assert.Equal([0x178u], Map<ToUppercase>(0xFF));
    }

    [Fact]
    public void SpecialCases()
    {
        // U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE lowercases to "i̇".
        Assert.Equal([0x69u, 0x307], Map<ToLowercase>(0x130));
        // Final sigma depends on the next character.
        Assert.Equal([0x3C2u], Map<ToLowercase>(0x3A3, 0));
        Assert.Equal([0x3C3u], Map<ToLowercase>(0x3A3, 'a'));
        // U+FB00 LATIN SMALL LIGATURE FF.
        Assert.Equal([(uint)'F', 'F'], Map<ToUppercase>(0xFB00));
    }

    [Fact]
    public void Canonicalize()
    {
        // ECMA-262 Canonicalize (non-unicode): uppercase, unless that maps a
        // non-ASCII character into ASCII.
        Assert.Equal([(uint)'A'], Map<Ecma262Canonicalize>('a'));
        Assert.Empty(Map<Ecma262Canonicalize>(0x17F));  // LATIN SMALL LETTER LONG S -> 'S' is excluded.
        uint[] equivalents = Map<Ecma262UnCanonicalize>('k');
        Assert.Contains((uint)'K', equivalents);
        Assert.Contains((uint)'k', equivalents);
    }

    [Fact]
    public void SimpleMappingsAgreeWithInvariantCulture()
    {
        // Where a character has a single-character mapping in both, the tables
        // (of V8's Unicode version) and .NET agree for the BMP letters that
        // exist in both.
        int checkedCount = 0;
        for (uint c = 0; c < 0x2000; c++)
        {
            if (char.IsSurrogate((char)c)) continue;
            // .NET's invariant casing leaves the dotless i and the long s alone.
            if (c == 0x131 || c == 0x17F) continue;
            uint[] up = Map<ToUppercase>(c);
            if (up.Length == 1)
            {
                Assert.Equal(char.ToUpperInvariant((char)c), (char)up[0]);
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 500);
    }

    [Fact]
    public void Predicates()
    {
        Assert.True(Letter.Is('a'));
        Assert.False(Letter.Is('1'));
        Assert.True(Uppercase.Is('Q'));
        Assert.False(Uppercase.Is('q'));
        Assert.True(ID_Start.Is(0x2118));
        Assert.False(ID_Start.Is(0x2E2F));
        Assert.True(WhiteSpace.Is(0x3000));
        Assert.False(WhiteSpace.Is(0x180E));
        V8Sharp.Base.Unibrow.Predicate<Letter> cached = new();
        Assert.True(cached.get('x'));
        Assert.True(cached.get('x'));
        Mapping<ToUppercase> mapping = new();
        Span<uint> r = stackalloc uint[4];
        Assert.Equal(1, mapping.get('x', 0, r));
        Assert.Equal((uint)'X', r[0]);
        Assert.Equal(1, mapping.get('x', 0, r));
        Assert.Equal((uint)'X', r[0]);
    }
}
