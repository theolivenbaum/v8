// Port of src/objects/name.h, string.h, symbol (the core representation).
// The string table, flattening policy, hashing and comparison helpers are
// filled in by the objects port; this fixes the representation.
namespace V8Sharp.Objects;

public abstract class Name(InstanceType instanceType) : HeapObject(instanceType)
{
    /// <summary>
    /// V8's raw hash field: 0 until computed. For strings that are array
    /// indices, the index is cached as in V8 (see String::AsArrayIndex).
    /// </summary>
    internal uint RawHashField;
}

/// <summary>A JavaScript string (UTF-16 code units).</summary>
public abstract class JSString(InstanceType instanceType) : Name(instanceType)
{
    public abstract int Length { get; }

    /// <summary>True once this string is the canonical copy in the string table.</summary>
    public bool IsInternalized { get; internal set; }

    /// <summary>Returns the flat contents, flattening a rope in place (V8's String::Flatten).</summary>
    public abstract string Flatten();

    public override string ToString() => Flatten();
}

/// <summary>A flat string (V8's SeqTwoByteString; one-byte storage is not modelled).</summary>
public sealed class SeqString(string value) : JSString(InstanceType.SeqStringType)
{
    public readonly string Value = value;
    public override int Length => Value.Length;
    public override string Flatten() => Value;
}

/// <summary>
/// A rope produced by concatenation (V8's ConsString). Flattening replaces the
/// children with the flat result, as V8's in-place flattening does (a flattened
/// cons string keeps first = flat, second = empty).
/// </summary>
public sealed class ConsString : JSString
{
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
    public bool IsFlat => _flat is not null;
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

public sealed class Symbol : Name
{
    public Symbol(JSValue description) : base(InstanceType.SymbolType) => Description = description;

    /// <summary>undefined or a string.</summary>
    public JSValue Description { get; }

    public bool IsPrivate { get; init; }
    public bool IsPrivateName { get; init; }
    public bool IsPrivateBrand { get; init; }
    public bool IsWellKnownSymbol { get; init; }
    public bool IsInPublicSymbolTable { get; init; }
    public bool IsInterestingSymbol { get; init; }

    public override string ToString() => $"Symbol({(Description.IsUndefined ? "" : Description.ToString())})";
}
