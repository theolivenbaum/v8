// Port of src/interpreter/bytecode-register.h/.cc.
//
// Register indices follow V8's interpreter frame layout on a 64-bit target
// without an embedded constant pool (x64/arm64), so that operands encode
// exactly as V8's (r0 is 0xf9, <this> is 0x02, <context> is 0xff ...):
//
//   OffsetFromFPToRegisterIndex(offset) =
//       (InterpreterFrameConstants::kRegisterFileFromFp - offset) / kSystemPointerSize
//
// with kSystemPointerSize = 8, kRegisterFileFromFp = -56, kFirstParamFromFp =
// kCallerSPOffset = 16, kFunctionOffset = -16, kContextOffset = -8,
// kBytecodeArrayFromFp = -32, kBytecodeOffsetFromFp = -40,
// kFeedbackVectorFromFp = -48, kCallerPCOffset = 8, kArgCOffset = -24.
using System.Globalization;
using System.Runtime.CompilerServices;

namespace V8Sharp.Interpreter;

/// <summary>
/// An interpreter Register which is located in the function's register file
/// in its stack frame. Registers hold parameters, this, and expression values.
/// </summary>
public readonly struct Register : IEquatable<Register>, IComparable<Register>
{
    // The frame constants used to derive V8's register indices (see file header).
    const int kSystemPointerSize = 8;
    const int kRegisterFileFromFp = -56;
    const int kFirstParamFromFp = 16;
    const int kFunctionOffset = -16;
    const int kContextOffset = -8;
    const int kBytecodeArrayFromFp = -32;
    const int kBytecodeOffsetFromFp = -40;
    const int kFeedbackVectorFromFp = -48;
    const int kCallerPCOffset = 8;
    const int kArgCOffset = -24;

    static int OffsetFromFPToRegisterIndex(int offset) => (kRegisterFileFromFp - offset) / kSystemPointerSize;

    const int kInvalidIndex = int.MaxValue;

    /// <summary>OffsetFromFPToRegisterIndex(0) = -7.</summary>
    public const int kRegisterFileStartOffset = (kRegisterFileFromFp - 0) / kSystemPointerSize;
    const int kFirstParamRegisterIndex = (kRegisterFileFromFp - kFirstParamFromFp) / kSystemPointerSize;
    const int kFunctionClosureRegisterIndex = (kRegisterFileFromFp - kFunctionOffset) / kSystemPointerSize;
    const int kCurrentContextRegisterIndex = (kRegisterFileFromFp - kContextOffset) / kSystemPointerSize;
    const int kBytecodeArrayRegisterIndex = (kRegisterFileFromFp - kBytecodeArrayFromFp) / kSystemPointerSize;
    const int kBytecodeOffsetRegisterIndex = (kRegisterFileFromFp - kBytecodeOffsetFromFp) / kSystemPointerSize;
    const int kFeedbackVectorRegisterIndex = (kRegisterFileFromFp - kFeedbackVectorFromFp) / kSystemPointerSize;
    const int kCallerPCOffsetRegisterIndex = (kRegisterFileFromFp - kCallerPCOffset) / kSystemPointerSize;
    const int kArgumentCountRegisterIndex = (kRegisterFileFromFp - kArgCOffset) / kSystemPointerSize;

    readonly int _index;

    public Register(int index) => _index = index;

    // Deviation: V8's default-constructed Register is invalid; a C# default(Register)
    // is r0 because structs are zero-initialized. Use InvalidValue() explicitly.
    public int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _index;
    }

    public bool IsParameter => _index < 0;
    public bool IsValid => _index != kInvalidIndex;

    public static Register FromParameterIndex(int index)
    {
        Debug.Assert(index >= 0);
        int registerIndex = kFirstParamRegisterIndex - index;
        Debug.Assert(registerIndex < 0);
        return new Register(registerIndex);
    }

    public int ToParameterIndex()
    {
        Debug.Assert(IsParameter);
        return kFirstParamRegisterIndex - _index;
    }

    public static Register Receiver() => FromParameterIndex(0);
    public bool IsReceiver => ToParameterIndex() == 0;

    /// <summary>Returns an invalid register (V8's default-constructed Register).</summary>
    public static Register InvalidValue() => new(kInvalidIndex);

    public static Register FunctionClosure() => new(kFunctionClosureRegisterIndex);
    public bool IsFunctionClosure => _index == kFunctionClosureRegisterIndex;

    public static Register CurrentContext() => new(kCurrentContextRegisterIndex);
    public bool IsCurrentContext => _index == kCurrentContextRegisterIndex;

    public static Register BytecodeArray() => new(kBytecodeArrayRegisterIndex);
    public bool IsBytecodeArray => _index == kBytecodeArrayRegisterIndex;

    public static Register BytecodeOffset() => new(kBytecodeOffsetRegisterIndex);
    public bool IsBytecodeOffset => _index == kBytecodeOffsetRegisterIndex;

    public static Register FeedbackVector() => new(kFeedbackVectorRegisterIndex);
    public bool IsFeedbackVector => _index == kFeedbackVectorRegisterIndex;

    public static Register ArgumentCount() => new(kArgumentCountRegisterIndex);

    /// <summary>A register that represents the accumulator within the
    /// interpreter; never emitted in bytecode.</summary>
    public static Register VirtualAccumulator() => new(kCallerPCOffsetRegisterIndex);

    public OperandSize SizeOfOperand()
    {
        int operand = ToOperand();
        if (operand >= sbyte.MinValue && operand <= sbyte.MaxValue) return OperandSize.Byte;
        if (operand >= short.MinValue && operand <= short.MaxValue) return OperandSize.Short;
        return OperandSize.Quad;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToOperand() => kRegisterFileStartOffset - _index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Register FromOperand(int operand) => new(kRegisterFileStartOffset - operand);

    public static Register FromShortStar(Bytecode bytecode)
    {
        Debug.Assert(Bytecodes.IsShortStar(bytecode));
        return new Register((int)Bytecode.Star0 - (int)bytecode);
    }

    /// <summary>The short Star bytecode (Star0..Star15) for this register, if any.</summary>
    public Bytecode? TryToShortStar()
    {
        if (_index >= 0 && _index < Bytecodes.kShortStarCount)
        {
            var bytecode = (Bytecode)((int)Bytecode.Star0 - _index);
            Debug.Assert(bytecode >= Bytecodes.kFirstShortStar && bytecode <= Bytecodes.kLastShortStar);
            return bytecode;
        }
        return null;
    }

    public override string ToString()
    {
        if (IsCurrentContext) return "<context>";
        if (IsFunctionClosure) return "<closure>";
        if (this == VirtualAccumulator()) return "<accumulator>";
        if (IsParameter)
        {
            int parameterIndex = ToParameterIndex();
            if (parameterIndex == 0) return "<this>";
            return "a" + (parameterIndex - 1).ToString(CultureInfo.InvariantCulture);
        }
        return "r" + _index.ToString(CultureInfo.InvariantCulture);
    }

    public bool Equals(Register other) => _index == other._index;
    public override bool Equals(object? obj) => obj is Register r && Equals(r);
    public override int GetHashCode() => _index;
    public int CompareTo(Register other) => _index.CompareTo(other._index);

    public static bool operator ==(Register a, Register b) => a._index == b._index;
    public static bool operator !=(Register a, Register b) => a._index != b._index;
    public static bool operator <(Register a, Register b) => a._index < b._index;
    public static bool operator <=(Register a, Register b) => a._index <= b._index;
    public static bool operator >(Register a, Register b) => a._index > b._index;
    public static bool operator >=(Register a, Register b) => a._index >= b._index;
}

/// <summary>A contiguous list of registers.</summary>
public readonly struct RegisterList
{
    readonly int _firstRegIndex;
    readonly int _registerCount;

    /// <summary>An empty list (V8's default <c>RegisterList()</c> starts at the invalid index).</summary>
    public static RegisterList Empty => new(Register.InvalidValue().Index, 0);

    public RegisterList(Register r) : this(r.Index, 1) { }

    // In V8 this constructor is private and friend classes (the allocator,
    // decoder, iterator, BytecodeUtils in tests) use it; C# has no friends,
    // so it is internal and exposed to tests via InternalsVisibleTo-free
    // BytecodeUtils.NewRegisterList below.
    internal RegisterList(int firstRegIndex, int registerCount)
    {
        _firstRegIndex = firstRegIndex;
        _registerCount = registerCount;
    }

    /// <summary>A new list which is a truncated version of this list, with |new_count| registers.</summary>
    public RegisterList Truncate(int newCount)
    {
        Debug.Assert(newCount >= 0 && newCount < _registerCount);
        return new RegisterList(_firstRegIndex, newCount);
    }

    public RegisterList PopLeft()
    {
        Debug.Assert(_registerCount >= 0);
        return new RegisterList(_firstRegIndex + 1, _registerCount - 1);
    }

    public Register this[int i]
    {
        get
        {
            Debug.Assert(i < _registerCount);
            return new Register(_firstRegIndex + i);
        }
    }

    public Register FirstRegister() => _registerCount == 0 ? new Register(0) : this[0];

    public Register LastRegister() => _registerCount == 0 ? new Register(0) : this[_registerCount - 1];

    public int RegisterCount => _registerCount;

    /// <summary>The raw first index (invalid for an empty default list).</summary>
    internal int FirstIndexRaw => _firstRegIndex;

    /// <summary>Increases the size of the register list by one.</summary>
    internal RegisterList IncrementRegisterCount() => new(_firstRegIndex, _registerCount + 1);
}

/// <summary>Port of test/unittests/interpreter/bytecode-utils.h BytecodeUtils:
/// exposes raw RegisterList construction.</summary>
public static class BytecodeUtils
{
    public static RegisterList NewRegisterList(int firstRegIndex, int registerCount) =>
        new(firstRegIndex, registerCount);
}
