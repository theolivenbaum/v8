// Port of src/strings/string-hasher{.h,-inl.h}: the hash-field computation for
// strings, including V8's array-index caching in the hash field.
//
// TODO(merge): V8Sharp.Base ports the real hasher (rapidhash, hash seed). This
// local copy keeps V8's hash-field layout and array-index rules exactly, but
// hashes the characters with the RunningStringHasher (Jenkins one-at-a-time)
// instead of rapidhash. Hash values are not observable from JavaScript.
using System.Runtime.CompilerServices;
using V8Sharp.Objects;

namespace V8Sharp.Strings;

public static class StringHasher
{
    public const int kZeroHash = 27;

    /// <summary>
    /// V8's StringHasher::HashSequentialString: an integer-index hash field for
    /// array indices (value cached when short enough), else a content hash.
    /// </summary>
    public static uint HashSequentialString(ReadOnlySpan<char> chars)
    {
        int length = chars.Length;
        if (length >= 1)
        {
            if (length <= Name.kMaxIntegerIndexSize)
            {
                switch (TryParseArrayIndex(chars, out int i, out ulong index))
                {
                    case IndexParseResult.Success:
                        return MakeArrayIndexHash((uint)index, (uint)length);
                    case IndexParseResult.NonIndex:
                        break;
                    case IndexParseResult.Overflow:
                        if (TryParseIntegerIndex(chars, i, index) == IndexParseResult.Success)
                        {
                            uint hash = Name.CreateHashFieldValue(GetUsableHash(chars), Name.HashFieldType.IntegerIndex);
                            if (Name.ContainsCachedArrayIndex(hash))
                            {
                                hash |= (uint)(Name.kMaxCachedArrayIndexLength + 1) << Name.ArrayIndexLengthShift;
                            }
                            return hash;
                        }
                        break;
                }
            }
            else if (length > JSString.kMaxHashCalcLength)
            {
                return GetTrivialHash((uint)length);
            }
        }
        return Name.CreateHashFieldValue(GetUsableHash(chars), Name.HashFieldType.Hash);
    }

    public static uint GetTrivialHash(uint length) =>
        Name.CreateHashFieldValue(length, Name.HashFieldType.Hash);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MakeArrayIndexHash(uint value, uint length)
    {
        value <<= Name.ArrayIndexValueShift;
        value |= length << Name.ArrayIndexLengthShift;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DecodeArrayIndexFromHashField(uint rawHashField) =>
        (rawHashField >> Name.ArrayIndexValueShift) & Name.kArrayIndexValueMask;

    static uint GetUsableHash(ReadOnlySpan<char> chars)
    {
        // TODO(merge): rapidhash with the isolate's hash seed.
        uint running = 0;
        foreach (char c in chars)
        {
            running += c;
            running += running << 10;
            running ^= running >> 6;
        }
        running += running << 3;
        running ^= running >> 11;
        running += running << 15;
        uint hash = running & Name.HashBitsMax;
        return hash == 0 ? kZeroHash : hash;
    }

    enum IndexParseResult { Success, NonIndex, Overflow }

    static IndexParseResult TryParseArrayIndex(ReadOnlySpan<char> chars, out int i, out ulong index)
    {
        index = (ulong)(chars[0] - '0');
        i = 1;
        if (index > 9) return IndexParseResult.NonIndex;
        if (index == 0)
        {
            return chars.Length > 1 ? IndexParseResult.NonIndex : IndexParseResult.Success;
        }
        if (chars.Length > Name.kMaxArrayIndexSize) return IndexParseResult.Overflow;
        for (; i < chars.Length; i++)
        {
            uint val = (uint)(chars[i] - '0');
            if (val > 9) return IndexParseResult.NonIndex;
            index = 10 * index + val;
        }
        if (index > Name.kMaxArrayIndex) return IndexParseResult.Overflow;
        return IndexParseResult.Success;
    }

    static IndexParseResult TryParseIntegerIndex(ReadOnlySpan<char> chars, int i, ulong index)
    {
        for (; i < chars.Length; i++)
        {
            uint val = (uint)(chars[i] - '0');
            if (val > 9) return IndexParseResult.NonIndex;
            index = 10 * index + val;
        }
        if (index > Name.kMaxSafeIntegerUint64) return IndexParseResult.Overflow;
        return IndexParseResult.Success;
    }
}
