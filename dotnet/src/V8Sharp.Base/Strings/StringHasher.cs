// Port of src/strings/string-hasher.h, string-hasher-inl.h, string-hasher.cc,
// src/numbers/hash-seed.{h,cc} and third_party/rapidhash-v8 (rapidhash.h,
// secret.h), with the hash-field layout constants of src/objects/name.h.
//
// V8 hashes one-byte strings as bytes and two-byte strings whose content is
// all Latin-1 as if they were one-byte (so equal strings hash equally in
// either representation). V8Sharp strings are always UTF-16; the same rule
// makes their hashes identical to V8's.
// V8_ENABLE_SEEDED_ARRAY_INDEX_HASH is off by default in V8 and is not ported.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Strings;

/// <summary>The hash field layout of Name (src/objects/name.h) and the
/// string limits it depends on (src/objects/string.h).</summary>
public static class NameHashField
{
    /// <summary>The type of information stored in the hash field.</summary>
    public enum HashFieldType : uint
    {
        kHash = 0b10,
        kIntegerIndex = 0b00,
        kForwardingIndex = 0b01,
        kEmpty = 0b11,
    }

    public static readonly BitField HashFieldTypeBits = new(0, 2);
    public static readonly BitField HashBits = HashFieldTypeBits.Next(32 - 2);

    public const int kHashNotComputedMask = 1;
    /// <summary>Value of empty hash field indicating that the hash is not computed.</summary>
    public const uint kEmptyHashField = (uint)HashFieldType.kEmpty;

    public static readonly BitField IsInternalizedForwardingIndexBit = HashFieldTypeBits.Next(1);
    public static readonly BitField IsExternalForwardingIndexBit = IsInternalizedForwardingIndexBit.Next(1);
    public static readonly BitField ForwardingIndexValueBits = IsExternalForwardingIndexBit.Next(32 - 2 - 1 - 1);

    /// <summary>Array index strings this short can keep their index in the hash field.</summary>
    public const int kMaxCachedArrayIndexLength = 7;
    public const uint kMaxArrayIndex = uint.MaxValue - 1;
    /// <summary>Maximum number of characters to consider when trying to
    /// convert a string value into an array index.</summary>
    public const int kMaxArrayIndexSize = 10;
    /// <summary>Maximum number of characters in a string that can possibly be
    /// an "integer index" (64-bit size_t).</summary>
    public const int kMaxIntegerIndexSize = 16;

    // For strings which are array indexes the hash value has the string length
    // mixed into the hash, mainly to avoid a hash value of zero which would be
    // the case for the string '0'. 24 bits are used for the array index value.
    public const int kArrayIndexValueBits = 24;
    public const uint kArrayIndexValueMask = (1u << kArrayIndexValueBits) - 1;
    public const int kArrayIndexLengthBits = 32 - kArrayIndexValueBits - 2;

    public static readonly BitField ArrayIndexValueBits = HashFieldTypeBits.Next(kArrayIndexValueBits);
    public static readonly BitField ArrayIndexLengthBits = ArrayIndexValueBits.Next(kArrayIndexLengthBits);

    /// <summary>When any of these bits is set then the hash field does not
    /// contain a cached array index.</summary>
    public const uint kDoesNotContainCachedArrayIndexMask =
        (~(uint)kMaxCachedArrayIndexLength << (2 + kArrayIndexValueBits)) | 0b11;

    /// <summary>When any of these bits is set then the hash field does not
    /// contain an integer or forwarding index.</summary>
    public const uint kDoesNotContainIntegerOrForwardingIndexMask = 0b10;

    /// <summary>String::kMaxLength on 64-bit hosts: (1 &lt;&lt; 29) - 24.</summary>
    public const int kMaxStringLength = (1 << 29) - 24;
    /// <summary>Strings longer than this get a trivial (length-based) hash.</summary>
    public const int kMaxHashCalcLength = 16383;

    public static bool IsHashFieldComputed(uint rawHashField) => (rawHashField & kHashNotComputedMask) == 0;
    public static bool IsHash(uint rawHashField) => HashFieldTypeBits.decode(rawHashField) == (uint)HashFieldType.kHash;
    public static bool IsIntegerIndex(uint rawHashField) => HashFieldTypeBits.decode(rawHashField) == (uint)HashFieldType.kIntegerIndex;
    public static bool IsForwardingIndex(uint rawHashField) => HashFieldTypeBits.decode(rawHashField) == (uint)HashFieldType.kForwardingIndex;
    public static bool IsInternalizedForwardingIndex(uint rawHashField) =>
        IsForwardingIndex(rawHashField) && IsInternalizedForwardingIndexBit.decode_bool(rawHashField);
    public static bool IsExternalForwardingIndex(uint rawHashField) =>
        IsForwardingIndex(rawHashField) && IsExternalForwardingIndexBit.decode_bool(rawHashField);

    public static uint CreateHashFieldValue(uint hash, HashFieldType type)
    {
        Debug.Assert(type != HashFieldType.kForwardingIndex);
        return HashBits.encode(hash & HashBits.kMax) | HashFieldTypeBits.encode((uint)type);
    }

    public static uint CreateInternalizedForwardingIndex(uint index) =>
        ForwardingIndexValueBits.encode(index) | IsExternalForwardingIndexBit.encode(false) |
        IsInternalizedForwardingIndexBit.encode(true) | HashFieldTypeBits.encode((uint)HashFieldType.kForwardingIndex);

    public static uint CreateExternalForwardingIndex(uint index) =>
        ForwardingIndexValueBits.encode(index) | IsExternalForwardingIndexBit.encode(true) |
        IsInternalizedForwardingIndexBit.encode(false) | HashFieldTypeBits.encode((uint)HashFieldType.kForwardingIndex);

    public static bool ContainsCachedArrayIndex(uint rawHashField) => (rawHashField & kDoesNotContainCachedArrayIndexMask) == 0;
}

/// <summary>The isolate's hash seed and the rapidhash secrets derived from it.</summary>
public sealed class HashSeed
{
    public const int kSecretsCount = 3;

    // Default secret parameters (RAPIDHASH_DEFAULT_SECRET).
    static readonly ulong[] s_defaultSecret = [0x2d358dccaa6c78a5UL, 0x8bb84b93962eacc9UL, 0x4b33a62ed433d4a3UL];

    readonly ulong[] _secrets;

    /// <summary>The meta seed (--hash-seed; 0 = generate at startup).</summary>
    public ulong seed { get; }

    public ReadOnlySpan<ulong> secret => _secrets;

    HashSeed(ulong seed, ulong[] secrets)
    {
        this.seed = seed;
        _secrets = secrets;
    }

    /// <summary>The static default seed (seed 0, default secrets).</summary>
    public static HashSeed Default { get; } = new(0, s_defaultSecret);

    /// <summary>HashSeed::InitializeRoots: a seed with secrets derived by
    /// rapidhash_make_secret, or with the default secrets when
    /// useDefaultSecret (V8_USE_DEFAULT_HASHER_SECRET).</summary>
    public static HashSeed Create(ulong seed, bool useDefaultSecret = false)
    {
        if (useDefaultSecret) return new HashSeed(seed, s_defaultSecret);
        ulong[] secrets = new ulong[kSecretsCount];
        RapidHash.MakeSecret(seed, secrets);
        return new HashSeed(seed, secrets);
    }
}

/// <summary>third_party/rapidhash-v8/rapidhash.h and secret.h.</summary>
public static class RapidHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Mul128(ref ulong a, ref ulong b)
    {
        ulong high = Math.BigMul(a, b, out ulong low);
        a = low;
        b = high;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong a, ulong b)
    {
        ulong high = Math.BigMul(a, b, out ulong low);
        return low ^ high;
    }

    // Readers. The plain reader reads little-endian bytes; the "convert to 8
    // bit" reader reads UTF-16 code units known to be <= 0xFF as bytes.
    interface IReader
    {
        static abstract int Length(ReadOnlySpan<char> s);
        static abstract ulong Read64(ReadOnlySpan<char> s, int bytePos);
        static abstract ulong Read32(ReadOnlySpan<char> s, int bytePos);
        static abstract ulong ReadSmall(ReadOnlySpan<char> s, int k);
    }

    // Reads the UTF-16 code units' little-endian bytes (V8's PlainHashReader
    // over the two-byte payload).
    readonly struct Utf16BytesReader : IReader
    {
        public static int Length(ReadOnlySpan<char> s) => 2 * s.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte ByteAt(ReadOnlySpan<char> s, int i) => (byte)((i & 1) == 0 ? s[i >> 1] : s[i >> 1] >> 8);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Read64(ReadOnlySpan<char> s, int p)
        {
            if ((p & 1) == 0)
            {
                int c = p >> 1;
                return s[c] | ((ulong)s[c + 1] << 16) | ((ulong)s[c + 2] << 32) | ((ulong)s[c + 3] << 48);
            }
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)ByteAt(s, p + i) << (8 * i);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Read32(ReadOnlySpan<char> s, int p)
        {
            if ((p & 1) == 0)
            {
                int c = p >> 1;
                return s[c] | ((ulong)s[c + 1] << 16);
            }
            ulong v = 0;
            for (int i = 0; i < 4; i++) v |= (ulong)ByteAt(s, p + i) << (8 * i);
            return v;
        }

        public static ulong ReadSmall(ReadOnlySpan<char> s, int k) =>
            ((ulong)ByteAt(s, 0) << 56) | ((ulong)ByteAt(s, k >> 1) << 32) | ByteAt(s, k - 1);
    }

    // ConvertTo8BitHashReader: every code unit is one byte of input.
    readonly struct Latin1Reader : IReader
    {
        public static int Length(ReadOnlySpan<char> s) => s.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Read64(ReadOnlySpan<char> s, int p) =>
            s[p] | ((ulong)s[p + 1] << 8) | ((ulong)s[p + 2] << 16) | ((ulong)s[p + 3] << 24) |
            ((ulong)s[p + 4] << 32) | ((ulong)s[p + 5] << 40) | ((ulong)s[p + 6] << 48) | ((ulong)s[p + 7] << 56);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Read32(ReadOnlySpan<char> s, int p) =>
            s[p] | ((ulong)s[p + 1] << 8) | ((ulong)s[p + 2] << 16) | ((ulong)s[p + 3] << 24);

        public static ulong ReadSmall(ReadOnlySpan<char> s, int k) =>
            ((ulong)s[0] << 56) | ((ulong)s[k >> 1] << 32) | s[k - 1];
    }

    // Plain bytes (V8's PlainHashReader over a byte buffer).
    static ulong Read64(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);
    static ulong Read32(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);

    static ulong Hash<TReader>(ReadOnlySpan<char> input, ulong seed, ReadOnlySpan<ulong> secret) where TReader : IReader
    {
        int len = TReader.Length(input);
        int p = 0;
        seed ^= Mix(seed ^ secret[0], secret[1]) ^ (ulong)len;
        ulong a, b;
        if (len <= 16)
        {
            if (len >= 4)
            {
                // Read the first and last 32 bits (they may overlap).
                int plast = p + (len - 4);
                a = (TReader.Read32(input, p) << 32) | TReader.Read32(input, plast);
                // This is equivalent to: delta = (len >= 8) ? 4 : 0;
                int delta = (len & 24) >> (len >> 3);
                b = (TReader.Read32(input, p + delta) << 32) | TReader.Read32(input, plast - delta);
            }
            else if (len > 0)
            {
                // 1, 2 or 3 bytes.
                a = TReader.ReadSmall(input, len);
                b = 0;
            }
            else
            {
                a = b = 0;
            }
        }
        else
        {
            int i = len;
            if (i > 48)
            {
                ulong see1 = seed, see2 = seed;
                do
                {
                    seed = Mix(TReader.Read64(input, p) ^ secret[0], TReader.Read64(input, p + 8) ^ seed);
                    see1 = Mix(TReader.Read64(input, p + 16) ^ secret[1], TReader.Read64(input, p + 24) ^ see1);
                    see2 = Mix(TReader.Read64(input, p + 32) ^ secret[2], TReader.Read64(input, p + 40) ^ see2);
                    p += 48;
                    i -= 48;
                } while (i >= 48);
                seed ^= see1 ^ see2;
            }
            if (i > 16)
            {
                seed = Mix(TReader.Read64(input, p) ^ secret[2], TReader.Read64(input, p + 8) ^ seed ^ secret[1]);
                if (i > 32) seed = Mix(TReader.Read64(input, p + 16) ^ secret[2], TReader.Read64(input, p + 24) ^ seed);
            }
            // Read the last 2x64 bits.
            a = TReader.Read64(input, p + i - 16);
            b = TReader.Read64(input, p + i - 8);
        }
        a ^= secret[1];
        b ^= seed;
        Mul128(ref a, ref b);
        return Mix(a ^ secret[0] ^ (ulong)len, b ^ secret[1]);
    }

    /// <summary>rapidhash over bytes.</summary>
    public static ulong Hash(ReadOnlySpan<byte> input, ulong seed, ReadOnlySpan<ulong> secret)
    {
        int len = input.Length;
        int p = 0;
        seed ^= Mix(seed ^ secret[0], secret[1]) ^ (ulong)len;
        ulong a, b;
        if (len <= 16)
        {
            if (len >= 4)
            {
                int plast = p + (len - 4);
                a = (Read32(input, p) << 32) | Read32(input, plast);
                int delta = (len & 24) >> (len >> 3);
                b = (Read32(input, p + delta) << 32) | Read32(input, plast - delta);
            }
            else if (len > 0)
            {
                a = ((ulong)input[0] << 56) | ((ulong)input[len >> 1] << 32) | input[len - 1];
                b = 0;
            }
            else
            {
                a = b = 0;
            }
        }
        else
        {
            int i = len;
            if (i > 48)
            {
                ulong see1 = seed, see2 = seed;
                do
                {
                    seed = Mix(Read64(input, p) ^ secret[0], Read64(input, p + 8) ^ seed);
                    see1 = Mix(Read64(input, p + 16) ^ secret[1], Read64(input, p + 24) ^ see1);
                    see2 = Mix(Read64(input, p + 32) ^ secret[2], Read64(input, p + 40) ^ see2);
                    p += 48;
                    i -= 48;
                } while (i >= 48);
                seed ^= see1 ^ see2;
            }
            if (i > 16)
            {
                seed = Mix(Read64(input, p) ^ secret[2], Read64(input, p + 8) ^ seed ^ secret[1]);
                if (i > 32) seed = Mix(Read64(input, p + 16) ^ secret[2], Read64(input, p + 24) ^ seed);
            }
            a = Read64(input, p + i - 16);
            b = Read64(input, p + i - 8);
        }
        a ^= secret[1];
        b ^= seed;
        Mul128(ref a, ref b);
        return Mix(a ^ secret[0] ^ (ulong)len, b ^ secret[1]);
    }

    /// <summary>rapidhash over the UTF-16 code units' bytes (V8's two-byte strings).</summary>
    public static ulong HashUtf16(ReadOnlySpan<char> input, ulong seed, ReadOnlySpan<ulong> secret) =>
        Hash<Utf16BytesReader>(input, seed, secret);

    /// <summary>rapidhash of code units that are all &lt;= 0xFF, as if they
    /// were one byte each (detail::HashConvertingTo8Bit).</summary>
    public static ulong HashConvertingTo8Bit(ReadOnlySpan<char> input, ulong seed, ReadOnlySpan<ulong> secret) =>
        Hash<Latin1Reader>(input, seed, secret);

    // secret.h: rapidhash_make_secret.

    static ulong Wyrand(ref ulong seed)
    {
        seed += 0x2d358dccaa6c78a5UL;
        return Mix(seed, seed ^ 0x8bb84b93962eacc9UL);
    }

    static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((UInt128)a * b % m);

    static ulong PowMod(ulong a, ulong b, ulong m)
    {
        ulong r = 1;
        while (b != 0)
        {
            if ((b & 1) != 0) r = MulMod(r, a, m);
            b >>= 1;
            if (b != 0) a = MulMod(a, a, m);
        }
        return r;
    }

    static bool Sprp(ulong n, ulong a)
    {
        ulong d = n - 1;
        int s = 0;
        while ((d & 0xff) == 0)
        {
            d >>= 8;
            s += 8;
        }
        if ((d & 0xf) == 0)
        {
            d >>= 4;
            s += 4;
        }
        if ((d & 0x3) == 0)
        {
            d >>= 2;
            s += 2;
        }
        if ((d & 0x1) == 0)
        {
            d >>= 1;
            s += 1;
        }
        ulong b = PowMod(a, d, n);
        if (b == 1 || b == n - 1) return true;
        for (int r = 1; r < s; r++)
        {
            b = MulMod(b, b, n);
            if (b <= 1) return false;
            if (b == n - 1) return true;
        }
        return false;
    }

    static bool IsPrime(ulong n)
    {
        if (n < 2 || (n & 1) == 0) return false;
        if (n < 4) return true;
        if (!Sprp(n, 2)) return false;
        if (n < 2047) return true;
        ReadOnlySpan<byte> bases = [3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];
        foreach (byte a in bases)
        {
            if (!Sprp(n, a)) return false;
        }
        return true;
    }

    public static void MakeSecret(ulong seed, Span<ulong> secret)
    {
        ReadOnlySpan<byte> c = [15, 23, 27, 29, 30, 39, 43, 45, 46, 51, 53, 54,
                                57, 58, 60, 71, 75, 77, 78, 83, 85, 86, 89, 90,
                                92, 99, 101, 102, 105, 106, 108, 113, 114, 116, 120, 135,
                                139, 141, 142, 147, 149, 150, 153, 154, 156, 163, 165, 166,
                                169, 170, 172, 177, 178, 180, 184, 195, 197, 198, 201, 202,
                                204, 209, 210, 212, 216, 225, 226, 228, 232, 240];
        for (int i = 0; i < 3; i++)
        {
            bool ok;
            do
            {
                ok = true;
                secret[i] = 0;
                for (int j = 0; j < 64; j += 8) secret[i] |= (ulong)c[(int)(Wyrand(ref seed) % (ulong)c.Length)] << j;
                if (secret[i] % 2 == 0)
                {
                    ok = false;
                    continue;
                }
                for (int j = 0; j < i; j++)
                {
                    if (BitOperations.PopCount(secret[j] ^ secret[i]) != 32)
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok && !IsPrime(secret[i])) ok = false;
            } while (!ok);
        }
    }
}

/// <summary>V8's running (Jenkins one-at-a-time) string hasher.</summary>
public struct RunningStringHasher(uint seed)
{
    uint _runningHash = seed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddCharacter(char c)
    {
        _runningHash += c;
        _runningHash += _runningHash << 10;
        _runningHash ^= _runningHash >> 6;
    }

    public uint Finalize()
    {
        _runningHash += _runningHash << 3;
        _runningHash ^= _runningHash >> 11;
        _runningHash += _runningHash << 15;
        return StringHasher.ConvertRawHashToUsableHash(_runningHash);
    }
}

public static class StringHasher
{
    /// <summary>No string is allowed to have a hash of zero. That value is
    /// reserved for internal properties. If the hash calculation yields zero
    /// then we use 27 instead.</summary>
    public const int kZeroHash = 27;

    internal static uint ConvertRawHashToUsableHash(ulong rawHash)
    {
        // Limit to the supported bits.
        int hash = (int)(rawHash & NameHashField.HashBits.kMax);
        // Ensure that the hash is kZeroHash, if the computed value is 0.
        return hash == 0 ? kZeroHash : (uint)hash;
    }

    /// <summary>Whether every code unit is &lt;= 0xFF.</summary>
    public static bool IsOnly8Bit(ReadOnlySpan<char> chars) => !chars.ContainsAnyExceptInRange((char)0, (char)0xFF);

    static ulong GetRapidHash(ReadOnlySpan<char> chars, HashSeed seed, out bool oneByteContent)
    {
        // For 2-byte strings we need to preserve the same hash for strings in just
        // the latin-1 range.
        oneByteContent = IsOnly8Bit(chars);
        if (oneByteContent) return RapidHash.HashConvertingTo8Bit(chars, seed.seed, seed.secret);
        return RapidHash.HashUtf16(chars, seed.seed, seed.secret);
    }

    static uint GetUsableRapidHash(ReadOnlySpan<char> chars, HashSeed seed, out bool oneByteContent) =>
        ConvertRawHashToUsableHash(GetRapidHash(chars, seed, out oneByteContent));

    public static uint GetTrivialHash(uint length)
    {
        Debug.Assert(length > NameHashField.kMaxHashCalcLength);
        // The hash of a large string is simply computed from the length.
        return NameHashField.CreateHashFieldValue(length, NameHashField.HashFieldType.kHash);
    }

    /// <summary>
    /// The hash value for a string of 1 to kMaxArrayIndexSize digits with no
    /// leading zeros (except "0"). The hash field consists of (from least
    /// significant bit to most): HashFieldType::kIntegerIndex, the value, and
    /// the length of the decimal string.
    /// </summary>
    public static uint MakeArrayIndexHash(uint value, uint length)
    {
        // For array indexes mix the length into the hash as an array index could
        // be zero.
        Debug.Assert(length <= NameHashField.kMaxArrayIndexSize);
        value <<= NameHashField.ArrayIndexValueBits.kShift;
        value |= length << NameHashField.ArrayIndexLengthBits.kShift;
        Debug.Assert(NameHashField.IsIntegerIndex(value));
        Debug.Assert((length <= NameHashField.kMaxCachedArrayIndexLength) == NameHashField.ContainsCachedArrayIndex(value));
        return value;
    }

    public static uint MakeArrayIndexHash(uint value, uint length, HashSeed seed) => MakeArrayIndexHash(value, length);

    /// <summary>Decode array index value from raw hash field.</summary>
    public static uint DecodeArrayIndexFromHashField(uint rawHashField) => NameHashField.ArrayIndexValueBits.decode(rawHashField);

    public static uint DecodeArrayIndexFromHashField(uint rawHashField, HashSeed seed) => DecodeArrayIndexFromHashField(rawHashField);

    enum IndexParseResult { kSuccess, kNonIndex, kOverflow }

    static IndexParseResult TryParseArrayIndex(ReadOnlySpan<char> chars, out int i, out ulong index)
    {
        int length = chars.Length;
        Debug.Assert(length > 0 && length <= NameHashField.kMaxIntegerIndexSize);
        // The leading character can only be a zero for the string "0"; otherwise this
        // isn't a valid index string.
        index = (ulong)(chars[0] - '0');
        i = 1;
        if ((uint)(chars[0] - '0') > 9) return IndexParseResult.kNonIndex;
        if (index == 0)
        {
            if (length > 1) return IndexParseResult.kNonIndex;
            return IndexParseResult.kSuccess;
        }

        if (length > NameHashField.kMaxArrayIndexSize) return IndexParseResult.kOverflow;

        for (; i < length; i++)
        {
            uint val = (uint)(chars[i] - '0');
            if (val > 9) return IndexParseResult.kNonIndex;
            index = 10 * index + val;
        }
        // If we have a large type for index, we'll never overflow it, so we can
        // have a simple comparison for array index overflow.
        if (index > NameHashField.kMaxArrayIndex) return IndexParseResult.kOverflow;
        return IndexParseResult.kSuccess;
    }

    const ulong kMaxSafeIntegerUint64 = 9007199254740991;

    static IndexParseResult TryParseIntegerIndex(ReadOnlySpan<char> chars, int i, ulong index)
    {
        for (; i < chars.Length; i++)
        {
            // We should never be anywhere near overflowing, so we can just do
            // one range check at the end.
            uint val = (uint)(chars[i] - '0');
            if (val > 9) return IndexParseResult.kNonIndex;
            index = 10 * index + val;
        }
        if (index > kMaxSafeIntegerUint64) return IndexParseResult.kOverflow;
        return IndexParseResult.kSuccess;
    }

    /// <summary>V8's StringHasher::HashSequentialString: the raw hash field of
    /// a string (array-index, integer-index, trivial or rapidhash).</summary>
    public static uint HashSequentialString(ReadOnlySpan<char> chars, HashSeed seed) =>
        HashSequentialString(chars, seed, out _);

    /// <summary>As above; oneByteContent reports whether the content fits in
    /// one byte (only meaningful when the content was scanned).</summary>
    public static uint HashSequentialString(ReadOnlySpan<char> chars, HashSeed seed, out bool oneByteContent)
    {
        oneByteContent = false;
        int length = chars.Length;
        if (length >= 1)
        {
            if (length <= NameHashField.kMaxIntegerIndexSize)
            {
                // Possible array or integer index; try to compute the array index hash.
                switch (TryParseArrayIndex(chars, out int i, out ulong index))
                {
                    case IndexParseResult.kSuccess:
                        oneByteContent = true;
                        return MakeArrayIndexHash((uint)index, (uint)length, seed);
                    case IndexParseResult.kNonIndex:
                        // A non-index result from TryParseArrayIndex means we don't need to
                        // check for integer indices.
                        break;
                    case IndexParseResult.kOverflow:
                        // On 64-bit, we might have a valid integer index even if the value
                        // overflowed an array index.
                        if (TryParseIntegerIndex(chars, i, index) == IndexParseResult.kSuccess)
                        {
                            uint hash = NameHashField.CreateHashFieldValue(
                                GetUsableRapidHash(chars, seed, out oneByteContent),
                                NameHashField.HashFieldType.kIntegerIndex);
                            if (NameHashField.ContainsCachedArrayIndex(hash))
                            {
                                // The hash accidentally looks like a cached index. Fix that by
                                // setting a bit that looks like a longer-than-cacheable string
                                // length.
                                hash |= (uint)(NameHashField.kMaxCachedArrayIndexLength + 1) << NameHashField.ArrayIndexLengthBits.kShift;
                            }
                            Debug.Assert(!NameHashField.ContainsCachedArrayIndex(hash));
                            return hash;
                        }
                        break;
                }
                // If the we failed to compute an index hash, this falls through into the
                // non-index hash case.
            }
            else if (length > NameHashField.kMaxHashCalcLength)
            {
                return GetTrivialHash((uint)length);
            }
        }

        // Non-index hash.
        return NameHashField.CreateHashFieldValue(GetUsableRapidHash(chars, seed, out oneByteContent),
                                                  NameHashField.HashFieldType.kHash);
    }

    /// <summary>One-byte overload (V8's uint8_t instantiation).</summary>
    public static uint HashSequentialString(ReadOnlySpan<byte> chars, HashSeed seed)
    {
        char[] tmp = new char[chars.Length];
        for (int i = 0; i < chars.Length; i++) tmp[i] = (char)chars[i];
        return HashSequentialString(tmp, seed);
    }
}
