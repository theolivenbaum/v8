// Port of src/objects/name.h, name-inl.h, symbol parts of name.h, and the
// representation classes of src/objects/string.h (SeqString, ConsString,
// SlicedString). String operations are in String.cs.
using System.Runtime.CompilerServices;
using V8Sharp.Strings;

namespace V8Sharp.Objects;

/// <summary>V8's PrivateSymbolKind.</summary>
public enum PrivateSymbolKind : byte
{
    Public,
    Internal,
    FieldName,
    Brand,
}

/// <summary>
/// V8's Name: a string or a symbol, with the raw hash field (V8's layout,
/// including the cached array index of short integer-index strings).
/// </summary>
public abstract class Name : HeapObject
{
    protected Name(InstanceType instanceType) : base(instanceType) { }

    /// <summary>V8's raw hash field; <see cref="kEmptyHashField"/> until computed.</summary>
    internal uint RawHashField = kEmptyHashField;

    public enum HashFieldType : uint
    {
        Hash = 0b10,
        IntegerIndex = 0b00,
        ForwardingIndex = 0b01,
        Empty = 0b11,
    }

    // HashFieldTypeBits = BitField<HashFieldType, 0, 2>; HashBits = Next<uint32_t, 30>.
    public const int HashFieldTypeMask = 0b11;
    public const int HashShift = 2;
    public const uint HashBitsMax = (1u << 30) - 1;
    public const int kHashNotComputedMask = 1;
    public const uint kEmptyHashField = (uint)HashFieldType.Empty;

    public const int kMaxCachedArrayIndexLength = 7;
    public const uint kMaxArrayIndex = uint.MaxValue - 1;
    public const int kMaxArrayIndexSize = 10;
    public const int kMaxIntegerIndexSize = 16;
    public const ulong kMaxSafeIntegerUint64 = 9007199254740991UL;
    public const int kArrayIndexValueBits = 24;
    public const uint kArrayIndexValueMask = (1u << kArrayIndexValueBits) - 1;
    public const int kArrayIndexLengthBits = 32 - kArrayIndexValueBits - 2;
    public const int ArrayIndexValueShift = 2;
    public const int ArrayIndexLengthShift = ArrayIndexValueShift + kArrayIndexValueBits;
    public const uint kDoesNotContainCachedArrayIndexMask =
        (~(uint)kMaxCachedArrayIndexLength << ArrayIndexLengthShift) | HashFieldTypeMask;
    public const uint kDoesNotContainIntegerOrForwardingIndexMask = 0b10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHashFieldComputed(uint rawHashField) => (rawHashField & kHashNotComputedMask) == 0;

    public static bool IsHash(uint rawHashField) => (rawHashField & HashFieldTypeMask) == (uint)HashFieldType.Hash;

    public static bool IsIntegerIndex(uint rawHashField) => (rawHashField & HashFieldTypeMask) == (uint)HashFieldType.IntegerIndex;

    public static uint CreateHashFieldValue(uint hash, HashFieldType type) =>
        ((hash & HashBitsMax) << HashShift) | (uint)type;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsCachedArrayIndex(uint rawHashField) =>
        (rawHashField & kDoesNotContainCachedArrayIndexMask) == 0;

    public bool HasHashCode => IsHashFieldComputed(RawHashField);

    /// <summary>V8's EnsureRawHash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint EnsureRawHash()
    {
        uint field = RawHashField;
        if (IsHashFieldComputed(field)) return field;
        return Unsafe.As<JSString>(this).ComputeAndSetRawHash();
    }

    /// <summary>V8's EnsureHash: the 30-bit hash, computing it if needed.</summary>
    public uint EnsureHash() => EnsureRawHash() >> HashShift;

    /// <summary>V8's hash(): the hash of a name whose hash is already computed.</summary>
    public uint Hash
    {
        get
        {
            Debug.Assert(HasHashCode);
            return RawHashField >> HashShift;
        }
    }

    public bool TryGetHash(out uint hash)
    {
        uint field = RawHashField;
        hash = field >> HashShift;
        return IsHashFieldComputed(field);
    }

    /// <summary>Name::Equals: identity, or equal string contents.</summary>
    public bool Equals(Name other)
    {
        if (ReferenceEquals(other, this)) return true;
        if (this is not JSString a || other is not JSString b) return false;
        if (a.IsInternalized && b.IsInternalized) return false;
        return a.SlowEquals(b);
    }

    /// <summary>True for internalized strings and symbols (V8's IsUniqueName).</summary>
    public bool IsUniqueName => this is Symbol || Unsafe.As<JSString>(this).IsInternalized;

    public bool IsArrayIndex() => this is JSString s && s.AsArrayIndex(out _);

    public bool AsArrayIndex(out uint index)
    {
        if (this is JSString s) return s.AsArrayIndex(out index);
        index = 0;
        return false;
    }

    public bool AsIntegerIndex(out ulong index)
    {
        if (this is JSString s) return s.AsIntegerIndex(out index);
        index = 0;
        return false;
    }

    /// <summary>
    /// Whether this name can affect the behaviour of the object it is a key of
    /// (Symbol.toStringTag, Symbol.toPrimitive, "toJSON", "get", "then").
    /// </summary>
    public bool IsInteresting()
    {
        if (this is Symbol sym) return sym.IsInterestingSymbol;
        return ReferenceEquals(this, Roots.ReadOnlyRoots.toJSON_string) ||
               ReferenceEquals(this, Roots.ReadOnlyRoots.get_string) ||
               ReferenceEquals(this, Roots.ReadOnlyRoots.then_string);
    }

    public bool IsAnyPrivate => this is Symbol s && s.IsAnyPrivate;
    public bool IsPrivateInternal => this is Symbol s && s.IsPrivateInternal;
    public bool IsAnyPrivateName => this is Symbol s && s.IsAnyPrivateName;
    public bool IsPrivateBrand => this is Symbol s && s.IsPrivateBrand;
}

/// <summary>A JavaScript string (UTF-16 code units). Operations are in String.cs.</summary>
public abstract partial class JSString : Name
{
    protected JSString(InstanceType instanceType) : base(instanceType) { }

    public abstract int Length { get; }

    /// <summary>True once this string is the canonical copy in the string table.</summary>
    public bool IsInternalized { get; internal set; }

    /// <summary>
    /// The internalized copy of this string, once known (V8 turns such a string
    /// into a ThinString; ours keep their representation and remember the
    /// canonical copy here).
    /// </summary>
    internal JSString? InternalizedForward;

    /// <summary>Returns the flat contents, flattening a rope in place (V8's String::Flatten).</summary>
    public abstract string Flatten();

    /// <summary>True when <see cref="Flatten"/> does not need to allocate (V8's IsFlat).</summary>
    public abstract bool IsFlat { get; }

    public override string ToString() => Flatten();
}

/// <summary>A flat string (V8's SeqTwoByteString; one-byte storage is not modelled).</summary>
public sealed class SeqString(string value) : JSString(InstanceType.SeqStringType)
{
    public readonly string Value = value;
    public override int Length => Value.Length;
    public override bool IsFlat => true;
    public override string Flatten() => Value;
}

/// <summary>
/// A rope produced by concatenation (V8's ConsString). Flattening replaces the
/// children with the flat result, as V8's in-place flattening does (a flattened
/// cons string keeps first = flat, second = empty).
/// </summary>
public sealed class ConsString : JSString
{
    /// <summary>Minimum length for a cons string (shorter results are flat).</summary>
    public const int kMinLength = 13;

    JSString _first;
    JSString? _second;
    string? _flat;
    readonly int _length;

    public ConsString(JSString first, JSString second) : base(InstanceType.ConsStringType)
    {
        _first = first;
        _second = second;
        _length = checked(first.Length + second.Length);
    }

    public override int Length => _length;
    public override bool IsFlat => _flat is not null;
    public JSString First => _first;
    public JSString? Second => _second;

    public override string Flatten()
    {
        if (_flat is not null) return _flat;
        // Iterative in-order walk so deep left- or right-leaning ropes do not
        // overflow the native stack.
        string flat = string.Create(_length, this, static (span, root) =>
        {
            var stack = new Stack<JSString>();
            stack.Push(root);
            int pos = 0;
            while (stack.Count > 0)
            {
                JSString s = stack.Pop();
                if (s is ConsString c && c._flat is null)
                {
                    if (c._second is not null) stack.Push(c._second);
                    stack.Push(c._first);
                    continue;
                }
                string part = s.Flatten();
                part.AsSpan().CopyTo(span[pos..]);
                pos += part.Length;
            }
        });
        _flat = flat;
        _first = new SeqString(flat);
        _second = null;
        return flat;
    }
}

/// <summary>
/// A substring view of a flat string (V8's SlicedString). Created by
/// Factory.NewSubString for results of at least <see cref="kMinLength"/> characters.
/// </summary>
public sealed class SlicedString : JSString
{
    public const int kMinLength = 13;

    readonly JSString _parent;
    readonly int _offset;
    readonly int _length;
    string? _flat;

    public SlicedString(JSString parent, int offset, int length) : base(InstanceType.SlicedStringType)
    {
        Debug.Assert(parent is SeqString);
        _parent = parent;
        _offset = offset;
        _length = length;
    }

    public JSString Parent => _parent;
    public int Offset => _offset;
    public override int Length => _length;
    public override bool IsFlat => true;

    /// <summary>The characters, without materialising a new string.</summary>
    public ReadOnlySpan<char> AsSpan() => _parent.Flatten().AsSpan(_offset, _length);

    public override string Flatten() => _flat ??= _parent.Flatten().Substring(_offset, _length);
}

public sealed class Symbol : Name
{
    public Symbol(JSValue description) : base(InstanceType.SymbolType)
    {
        Description = description;
        SetHash(IdentityHash.Next());
    }

    /// <summary>undefined or a string.</summary>
    public JSValue Description { get; set; }

    public PrivateSymbolKind PrivateSymbolKind { get; set; }

    public bool IsWellKnownSymbol { get; set; }
    public bool IsInPublicSymbolTable { get; set; }
    public bool IsInterestingSymbol { get; set; }

    public bool IsAnyPrivate => PrivateSymbolKind != PrivateSymbolKind.Public;
    public bool IsPrivateInternal => PrivateSymbolKind == PrivateSymbolKind.Internal;
    public bool IsAnyPrivateName => PrivateSymbolKind >= PrivateSymbolKind.FieldName;
    public bool IsPrivateBrand => PrivateSymbolKind == PrivateSymbolKind.Brand;

    // Kept for existing callers of the skeleton API.
    public bool IsPrivate => IsAnyPrivate;
    public bool IsPrivateName => IsAnyPrivateName;

    /// <summary>
    /// Assigns the symbol's hash (V8: a random identity hash at allocation).
    /// </summary>
    internal void SetHash(uint hash) => RawHashField = CreateHashFieldValue(hash, HashFieldType.Hash);

    public override string ToString() => $"Symbol({(Description.IsUndefined ? "" : Description.ToString())})";
}
