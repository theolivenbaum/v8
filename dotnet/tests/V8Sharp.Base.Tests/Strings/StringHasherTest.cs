// Tests of the port of src/strings/string-hasher*.h, hash-seed and
// third_party/rapidhash-v8. V8 has no unit test for the string hasher; these
// check the invariants the rest of the engine relies on.

using V8Sharp.Base.Strings;

namespace V8Sharp.Base.Tests.Strings;

public class StringHasherTest
{
    static readonly HashSeed Seed = HashSeed.Default;

    [Fact]
    public void ArrayIndexHash()
    {
        foreach (string s in new[] { "0", "1", "42", "1234567", "9999999" })
        {
            uint field = StringHasher.HashSequentialString(s, Seed);
            Assert.True(NameHashField.IsIntegerIndex(field));
            Assert.True(NameHashField.ContainsCachedArrayIndex(field));
            Assert.Equal(uint.Parse(s), StringHasher.DecodeArrayIndexFromHashField(field));
            Assert.Equal((uint)s.Length, NameHashField.ArrayIndexLengthBits.decode(field));
        }
    }

    [Fact]
    public void NonCachedIndices()
    {
        // Too long for the cached array index, but still an integer index.
        uint field = StringHasher.HashSequentialString("12345678", Seed);
        Assert.False(NameHashField.ContainsCachedArrayIndex(field));
        Assert.True(NameHashField.IsIntegerIndex(field));
        // Leading zeros and non-digits are ordinary hashes.
        Assert.True(NameHashField.IsHash(StringHasher.HashSequentialString("01", Seed)));
        Assert.True(NameHashField.IsHash(StringHasher.HashSequentialString("1a", Seed)));
        Assert.True(NameHashField.IsHash(StringHasher.HashSequentialString("", Seed)));
        // kMaxArrayIndex + 1 = 2^32 - 1 is not an array index but is an integer index.
        Assert.True(NameHashField.IsIntegerIndex(StringHasher.HashSequentialString("4294967295", Seed)));
        // Beyond kMaxSafeInteger digits it is a plain hash.
        Assert.True(NameHashField.IsHash(StringHasher.HashSequentialString("12345678901234567890", Seed)));
    }

    [Fact]
    public void OneByteAndTwoByteAgree()
    {
        // A string hashes the same whether it is stored as one or two bytes.
        string[] samples = ["a", "hello", "caf\u00e9", "0123456789abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ", new string('x', 200)];
        foreach (string s in samples)
        {
            byte[] bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
            uint twoByte = StringHasher.HashSequentialString(s, Seed, out bool oneByte);
            Assert.True(oneByte);
            Assert.Equal(StringHasher.HashSequentialString(bytes, Seed), twoByte);
        }
    }

    [Fact]
    public void HashesDiffer()
    {
        HashSet<uint> seen = [];
        for (int i = 0; i < 2000; i++)
        {
            uint field = StringHasher.HashSequentialString("key" + i, Seed);
            Assert.True(NameHashField.IsHash(field));
            uint hash = NameHashField.HashBits.decode(field);
            Assert.NotEqual(0u, hash);
            seen.Add(hash);
        }
        Assert.True(seen.Count > 1990);
        Assert.NotEqual(StringHasher.HashSequentialString("\u4e2d\u6587", Seed),
                        StringHasher.HashSequentialString("\u6587\u4e2d", Seed));
    }

    [Fact]
    public void SeedChangesHash()
    {
        HashSeed other = HashSeed.Create(12345);
        Assert.NotEqual(StringHasher.HashSequentialString("property", Seed),
                        StringHasher.HashSequentialString("property", other));
        // Array index hashes do not depend on the seed.
        Assert.Equal(StringHasher.HashSequentialString("17", Seed),
                     StringHasher.HashSequentialString("17", other));
        Assert.Equal(HashSeed.Create(12345).secret.ToArray(), other.secret.ToArray());
    }

    [Fact]
    public void TrivialHashForLongStrings()
    {
        string s = new('q', NameHashField.kMaxHashCalcLength + 1);
        uint field = StringHasher.HashSequentialString(s, Seed);
        Assert.Equal(StringHasher.GetTrivialHash((uint)s.Length), field);
    }

    [Fact]
    public void RunningHasherIsDeterministic()
    {
        RunningStringHasher a = new(7), b = new(7);
        foreach (char c in "running") { a.AddCharacter(c); b.AddCharacter(c); }
        Assert.Equal(a.Finalize(), b.Finalize());
    }
}
