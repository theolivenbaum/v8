// The port of V8's Tagged<Object>: see dotnet/docs/architecture.md section 3.
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace V8Sharp.Objects;

/// <summary>
/// A JavaScript value: undefined, a number, or a reference to a
/// <see cref="HeapObject"/>. 16 bytes, never allocates for numbers.
/// </summary>
public readonly struct JSValue : IEquatable<JSValue>
{
    internal readonly HeapObject? _obj;
    /// <summary>
    /// The number payload as its bit pattern. A long rather than a double
    /// field: the System V x64 ABI has no callee-saved XMM registers, so the
    /// JIT keeps a double that lives across calls (the interpreter's
    /// accumulator) in memory, while a long gets a callee-saved general
    /// register. It also makes the value two integer words for the calling
    /// convention (passed and returned in general registers).
    /// </summary>
    internal readonly long _bits;

    /// <summary>The number payload (meaningful when <see cref="IsNumber"/>).</summary>
    internal double _num
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitConverter.Int64BitsToDouble(_bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    JSValue(HeapObject? obj, double num)
    {
        _obj = obj;
        _bits = BitConverter.DoubleToInt64Bits(num);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue(HeapObject obj)
    {
        _obj = obj;
        _bits = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue(double number)
    {
        _obj = NumberTag.Instance;
        _bits = BitConverter.DoubleToInt64Bits(number);
    }

    public static JSValue Undefined => default;
    public static readonly JSValue Null = new(Oddball.Null);
    public static readonly JSValue True = new(Oddball.True);
    public static readonly JSValue False = new(Oddball.False);
    public static readonly JSValue TheHole = new(Oddball.TheHole);
    /// <summary>
    /// V8's canonical quiet NaN, std::numeric_limits&lt;double&gt;::quiet_NaN()
    /// (0x7FF8000000000000). .NET's double.NaN is 0xFFF8000000000000 (sign bit
    /// set); the bits are observable through Float64Array and DataView.
    /// </summary>
    public static readonly double QuietNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);
    public static readonly JSValue NaN = new(QuietNaN);
    public static readonly JSValue Zero = new(0.0);
    public static readonly JSValue MinusZero = new(-0.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue FromNumber(double d) => new(NumberTag.Instance, d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue FromInt(int i) => new(NumberTag.Instance, i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue FromBoolean(bool b) => b ? True : False;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue FromObject(HeapObject? o) => new(o, 0);

    public static implicit operator JSValue(HeapObject obj) => new(obj);

    // ---- Type tests --------------------------------------------------------

    public bool IsUndefined
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _obj is null;
    }

    public bool IsNull => ReferenceEquals(_obj, Oddball.Null);
    public bool IsNullOrUndefined => _obj is null || ReferenceEquals(_obj, Oddball.Null);
    public bool IsTrue => ReferenceEquals(_obj, Oddball.True);
    public bool IsFalse => ReferenceEquals(_obj, Oddball.False);
    public bool IsBoolean => ReferenceEquals(_obj, Oddball.True) || ReferenceEquals(_obj, Oddball.False);
    public bool IsTheHole => ReferenceEquals(_obj, Oddball.TheHole);

    public bool IsNumber
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ReferenceEquals(_obj, NumberTag.Instance);
    }

    /// <summary>
    /// Whether V8 would represent this value as a Smi: an integral number in
    /// the 31-bit range that is not -0 (V8's default pointer-compression config).
    /// </summary>
    public bool IsSmi
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IsNumber && IsSmiDouble(_num);
    }

    /// <summary>
    /// Whether a double would be a Smi in V8 (31-bit, integral, not -0).
    /// Comparing the bits of the int round trip rejects fractions, NaN and -0
    /// in one compare (a double compare needs a parity branch for NaN and
    /// cannot tell -0 from 0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSmiDouble(double d)
    {
        int i = TruncateToInt32(d);
        return BitConverter.DoubleToInt64Bits(i) == BitConverter.DoubleToInt64Bits(d) &&
            (uint)(i - SmiMinValue) <= (uint)(SmiMaxValue - SmiMinValue);
    }

    /// <summary>
    /// The double truncated to an int32, or int.MinValue when it is NaN or out
    /// of range (cvttsd2si). C#'s (int) cast saturates, which costs a NaN mask
    /// and a compare; callers that check the round trip do not need it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TruncateToInt32(double d) =>
        Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(d)) : (int)d;

    /// <summary>
    /// The array index a number key names when it is an integer in [0, 2^31):
    /// V8's TryToIntptr for a keyed access (-0 is index 0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetIndex(double d, out int index)
    {
        int i = TruncateToInt32(d);
        index = i;
        return i >= 0 && i == d;
    }

    public const int SmiMinValue = -(1 << 30);
    public const int SmiMaxValue = (1 << 30) - 1;

    /// <summary>True for heap objects (anything that is neither undefined nor a number).</summary>
    public bool IsHeapObject => _obj is not null && !ReferenceEquals(_obj, NumberTag.Instance);

    // An instance-type range check: `is JSString` of an abstract class is a
    // call to the runtime's cast helper.
    public bool IsString
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _obj is { } o && o.InstanceType <= InstanceTypeChecks.LastString;
    }

    /// <summary>The string, or null for anything else (a cheap `is JSString`).</summary>
    public JSString? StringOrNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _obj is { } o && o.InstanceType <= InstanceTypeChecks.LastString ? Unsafe.As<JSString>(o) : null;
    }
    public bool IsSymbol => _obj is Symbol;
    public bool IsName => _obj is Name;
    public bool IsBigInt => _obj is BigInt;
    public bool IsOddball => _obj is Oddball;

    public bool IsJSReceiver
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _obj is not null && _obj.InstanceType >= InstanceTypeChecks.FirstJSReceiver;
    }

    public bool IsJSObject => _obj is not null && _obj.InstanceType >= InstanceTypeChecks.FirstJSObject;

    public InstanceType? InstanceTypeOrNull => IsHeapObject ? _obj!.InstanceType : null;

    // ---- Accessors ---------------------------------------------------------

    public double Number
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { Debug.Assert(IsNumber); return _num; }
    }

    /// <summary>The heap object, or null for undefined and numbers.</summary>
    public HeapObject? HeapObjectOrNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ReferenceEquals(_obj, NumberTag.Instance) ? null : _obj;
    }

    /// <summary>The heap object; the caller has established <see cref="IsHeapObject"/>.</summary>
    public HeapObject Object
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { Debug.Assert(IsHeapObject); return _obj!; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T As<T>() where T : HeapObject => (T)_obj!;

    /// <summary>
    /// <see cref="As{T}"/> without the type check, for values whose type an
    /// invariant guarantees (bytecode constants and the registers the bytecode
    /// generator reserves): the checked cast of a non-sealed class is a helper call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T UncheckedAs<T>() where T : HeapObject
    {
        Debug.Assert(_obj is T);
        return Unsafe.As<T>(_obj!);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? AsOrNull<T>() where T : HeapObject => _obj as T;

    public bool BooleanValue => ReferenceEquals(_obj, Oddball.True);

    // ---- Identity ----------------------------------------------------------

    /// <summary>
    /// Bit identity, V8's <c>a == b</c> on tagged words (for numbers: same bits,
    /// so NaN == NaN with the same payload and 0 != -0). Not a JS equality.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsIdenticalTo(in JSValue other) =>
        ReferenceEquals(_obj, other._obj) &&
        (!ReferenceEquals(_obj, NumberTag.Instance) || BitConverter.DoubleToInt64Bits(_num) == BitConverter.DoubleToInt64Bits(other._num));

    public bool Equals(JSValue other) => IsIdenticalTo(other);
    public override bool Equals(object? obj) => obj is JSValue v && IsIdenticalTo(v);
    public override int GetHashCode() =>
        IsNumber ? _num.GetHashCode() : _obj is null ? 0 : RuntimeHelpers.GetHashCode(_obj);

    public override string ToString() =>
        _obj is null ? "undefined" : IsNumber ? _num.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : _obj.ToString() ?? "";
}
