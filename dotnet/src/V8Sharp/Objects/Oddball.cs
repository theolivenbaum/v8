// Port of src/objects/oddball.h and src/objects/hole.h.
//
// Deliberate: undefined is not an Oddball instance but the null reference in
// JSValue (so default(JSValue) is undefined). Every other oddball and hole
// is a singleton, compared by reference.
namespace V8Sharp.Objects;

public sealed class Oddball : HeapObject
{
    // V8's Oddball::kind() values (src/objects/oddball.h).
    public enum OddballKind : byte
    {
        False = 0,
        True = 1,
        TheHole = 2,
        Null = 3,
        ArgumentsMarker = 4,
        Undefined = 5,
        Uninitialized = 6,
        Other = 7,
        Exception = 8,
        OptimizedOut = 9,
        StaleRegister = 10,
        SelfReferenceMarker = 11,
        BasicBlockCountersMarker = 12,
        TerminationException = 13,
        PropertyCellHole = 14,
        HashTableHole = 15,
    }

    public OddballKind Kind { get; }

    /// <summary>ToString(oddball): "null", "true", "false" ...</summary>
    public string ToStringValue { get; }

    /// <summary>ToNumber(oddball).</summary>
    public double ToNumberValue { get; }

    /// <summary>typeof result.</summary>
    public string TypeOf { get; }

    Oddball(OddballKind kind, string toString, double toNumber, string typeOf) : base(InstanceType.OddballType)
    {
        Kind = kind;
        ToStringValue = toString;
        ToNumberValue = toNumber;
        TypeOf = typeOf;
    }

    public static readonly Oddball Null = new(OddballKind.Null, "null", 0, "object");
    public static readonly Oddball True = new(OddballKind.True, "true", 1, "boolean");
    public static readonly Oddball False = new(OddballKind.False, "false", 0, "boolean");
    public static readonly Oddball TheHole = new(OddballKind.TheHole, "hole", double.NaN, "undefined");
    public static readonly Oddball HashTableHole = new(OddballKind.HashTableHole, "hash_table_hole", double.NaN, "undefined");
    public static readonly Oddball PropertyCellHole = new(OddballKind.PropertyCellHole, "property_cell_hole", double.NaN, "undefined");
    public static readonly Oddball ArgumentsMarker = new(OddballKind.ArgumentsMarker, "arguments_marker", -4, "undefined");
    public static readonly Oddball Uninitialized = new(OddballKind.Uninitialized, "uninitialized", double.NaN, "undefined");
    public static readonly Oddball Exception = new(OddballKind.Exception, "exception", double.NaN, "undefined");
    public static readonly Oddball OptimizedOut = new(OddballKind.OptimizedOut, "optimized_out", double.NaN, "undefined");
    public static readonly Oddball StaleRegister = new(OddballKind.StaleRegister, "stale_register", double.NaN, "undefined");
    public static readonly Oddball TerminationException = new(OddballKind.TerminationException, "termination_exception", double.NaN, "undefined");
    public static readonly Oddball SelfReferenceMarker = new(OddballKind.SelfReferenceMarker, "self_reference_marker", double.NaN, "undefined");

    public bool IsHole => Kind is OddballKind.TheHole or OddballKind.HashTableHole or OddballKind.PropertyCellHole;

    public override string ToString() => ToStringValue;
}
