// Port of src/objects/heap-object.h: the root of every non-number value.
//
// Deliberate: no map word on primitives and internal objects. V8 needs the
// map pointer to know an object's layout; the CLR knows it already. Only
// JSReceivers carry a Map (hidden class), because property access and the
// inline caches key on it. InstanceType is stored in every object so hot
// code can switch on it instead of chaining type tests.
namespace V8Sharp.Objects;

public abstract class HeapObject(InstanceType instanceType)
{
    public readonly InstanceType InstanceType = instanceType;

    /// <summary>
    /// The hash word: Name's raw hash field, or a JSReceiver's identity hash
    /// (V8 keeps that in properties_or_hash). One field in the root class,
    /// next to InstanceType, so that it fills the header word's padding: the
    /// CLR lays out a class's own fields after its references, and a field of
    /// JSReceiver would add 8 bytes to every object.
    /// </summary>
    private protected uint _hashField;

    public bool IsString => InstanceTypeChecks.IsString(InstanceType);
    public bool IsName => InstanceTypeChecks.IsName(InstanceType);
    public bool IsJSReceiver => InstanceTypeChecks.IsJSReceiver(InstanceType);
    public bool IsJSObject => InstanceTypeChecks.IsJSObject(InstanceType);
    public bool IsJSFunction => InstanceTypeChecks.IsJSFunction(InstanceType);

    public override string ToString() => $"<{InstanceType}>";
}

/// <summary>
/// The sentinel stored in <see cref="JSValue"/> to mark a number. It is never
/// exposed as an object; see architecture.md section 3.
/// </summary>
public sealed class NumberTag : HeapObject
{
    public static readonly NumberTag Instance = new();
    NumberTag() : base(InstanceType.HeapNumberType) { }
}
