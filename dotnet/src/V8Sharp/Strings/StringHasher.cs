// Port of src/strings/string-hasher{.h,-inl.h}: the hash-field computation for
// strings, including V8's array-index caching in the hash field. The hashing
// itself is V8Sharp.Base's port (rapidhash with the default hash seed), which
// the parser's AstValueFactory uses too, so an AstRawString's hash field is
// the internalized string's (AstValueFactory::Internalize hands it to the
// StringTable instead of hashing again, as V8 does).
using System.Runtime.CompilerServices;
using V8Sharp.Base.Strings;

namespace V8Sharp.Strings;

public static class StringHasher
{
    public const int kZeroHash = Base.Strings.StringHasher.kZeroHash;

    /// <summary>
    /// V8's StringHasher::HashSequentialString: an integer-index hash field for
    /// array indices (value cached when short enough), else a content hash.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashSequentialString(ReadOnlySpan<char> chars) =>
        Base.Strings.StringHasher.HashSequentialString(chars, HashSeed.Default);

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
}
