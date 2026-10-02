// Port of src/maglev/maglev-ir.h (the IR: nodes, value representations,
// operation properties, deopt infos) and src/maglev/maglev-node-type.h, for
// the subset of Maglev's nodes that V8Sharp's graph builder emits.
//
// Structure as in V8: a Graph of BasicBlocks; a block holds Phis, Nodes (each
// a NodeBase with inputs, properties and optional eager/lazy deopt info) and
// one ControlNode. ValueNodes produce a value in a ValueRepresentation
// (Tagged, Int32, Uint32, Float64, HoleyFloat64), which the IL code
// generator (MaglevCodeGenerator) keeps in an IL local of the matching CLR
// type (JSValue, int, double).
//
// Deviation (structural): V8 has a C++ class per opcode with its own
// GenerateCode. V8Sharp has one NodeBase hierarchy whose instances carry an
// Opcode and a few parameter fields (the node's constants: a map list, a
// field index, a feedback slot ...); the code generator switches on the
// opcode. Opcode names are V8's; the few nodes V8 does not have are marked
// "V8Sharp" in the enum (frame bookkeeping for inlined frames, calls of
// baseline builtins for generic operations).
using System.Reflection;
using System.Reflection.Emit;
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>ValueRepresentation.</summary>
public enum ValueRepresentation : byte
{
    kTagged,
    kInt32,
    kUint32,
    kFloat64,
    kHoleyFloat64,
    /// <summary>V8Sharp: a raw bool (the CLR's), for fused compare results.</summary>
    kBit,
}

/// <summary>NodeType (maglev-node-type.h): a set of leaf types.</summary>
[Flags]
public enum NodeType : uint
{
    kNone = 0,
    kSmi = 1 << 0,
    kHeapNumber = 1 << 1,
    kNull = 1 << 2,
    kUndefined = 1 << 3,
    kBoolean = 1 << 4,
    kSymbol = 1 << 5,
    kInternalizedString = 1 << 6,
    kOtherString = 1 << 7,
    kContext = 1 << 8,
    kStringWrapper = 1 << 9,
    kJSArray = 1 << 10,
    kJSFunction = 1 << 11,
    kOtherCallable = 1 << 12,
    kJSDataView = 1 << 13,
    kJSGeneratorObject = 1 << 14,
    kOtherHeapObject = 1 << 15,
    kOtherJSReceiver = 1 << 16,
    kBigInt = 1 << 17,

    kUnknown = (1 << 18) - 1,
    kCallable = kJSFunction | kOtherCallable,
    kNullOrUndefined = kNull | kUndefined,
    kOddball = kNullOrUndefined | kBoolean,
    kNumber = kSmi | kHeapNumber,
    kNumeric = kNumber | kBigInt,
    kNumberOrBoolean = kNumber | kBoolean,
    kNumberOrUndefined = kNumber | kUndefined,
    kNumberOrOddball = kNumber | kOddball,
    kString = kInternalizedString | kOtherString,
    kStringOrStringWrapper = kString | kStringWrapper,
    kStringOrOddball = kString | kOddball,
    kName = kString | kSymbol,
    kJSPrimitive = kNumber | kString | kBoolean | kNullOrUndefined | kSymbol | kBigInt,
    kJSReceiver = kJSArray | kCallable | kStringWrapper | kJSDataView | kJSGeneratorObject | kOtherJSReceiver,
    kJSReceiverOrNull = kJSReceiver | kNull,
    kJSReceiverOrNullOrUndefined = kJSReceiver | kNullOrUndefined,
    kAnyHeapObject = kUnknown & ~kSmi,
}

public static class NodeTypes
{
    /// <summary>NodeTypeIs: every leaf of <paramref name="type"/> is in <paramref name="toCheck"/>.</summary>
    public static bool Is(NodeType type, NodeType toCheck) => (type & ~toCheck) == 0;
    public static bool CanBe(NodeType type, NodeType toCheck) => (type & toCheck) != 0;
    public static NodeType Intersect(NodeType a, NodeType b) => a & b;
    public static NodeType Union(NodeType a, NodeType b) => a | b;

    /// <summary>StaticTypeForConstant.</summary>
    public static NodeType ForConstant(JSValue value)
    {
        if (value.IsUndefined) return NodeType.kUndefined;
        if (value.IsNumber) return value.IsSmi ? NodeType.kSmi : NodeType.kHeapNumber;
        HeapObject o = value.Object;
        if (ReferenceEquals(o, Oddball.Null)) return NodeType.kNull;
        if (ReferenceEquals(o, Oddball.True) || ReferenceEquals(o, Oddball.False)) return NodeType.kBoolean;
        if (o is JSString s) return s.IsInternalized ? NodeType.kInternalizedString : NodeType.kOtherString;
        if (o is Symbol) return NodeType.kSymbol;
        if (o is BigInt) return NodeType.kBigInt;
        if (o is Context) return NodeType.kContext;
        if (o is JSFunction) return NodeType.kJSFunction;
        if (o is JSArray) return NodeType.kJSArray;
        if (o is JSReceiver r) return r.Map.IsCallable ? NodeType.kOtherCallable : NodeType.kOtherJSReceiver;
        return NodeType.kOtherHeapObject;
    }

    /// <summary>StaticTypeForMap.</summary>
    public static NodeType ForMap(Map map)
    {
        if (map.InstanceType == InstanceType.JSArrayType) return NodeType.kJSArray;
        if (InstanceTypeChecks.IsJSFunction(map.InstanceType)) return NodeType.kJSFunction;
        if (map.IsCallable) return NodeType.kOtherCallable;
        if (map.InstanceType == InstanceType.JSPrimitiveWrapperType) return NodeType.kJSReceiver;
        return NodeType.kOtherJSReceiver;
    }
}

/// <summary>OpProperties (maglev-ir.h): what a node may do.</summary>
[Flags]
public enum OpProperties : ushort
{
    kNone = 0,
    /// <summary>Calls out (JavaScript or the runtime): anything may change.</summary>
    kCall = 1 << 0,
    kEagerDeopt = 1 << 1,
    kLazyDeopt = 1 << 2,
    kCanThrow = 1 << 3,
    kCanRead = 1 << 4,
    kCanWrite = 1 << 5,
    kCanAllocate = 1 << 6,
    /// <summary>Must not be removed when unused (has an effect).</summary>
    kNotIdempotent = 1 << 7,

    kGenericCall = kCall | kLazyDeopt | kCanThrow | kCanRead | kCanWrite | kCanAllocate | kNotIdempotent,
}

/// <summary>Opcode (NODE_BASE_LIST).</summary>
public enum Opcode : ushort
{
    // ---- Constants ------------------------------------------------------------
    SmiConstant,
    Int32Constant,
    Float64Constant,
    RootConstant,
    /// <summary>HeapConstant / Constant: any JSValue.</summary>
    Constant,

    // ---- Values -----------------------------------------------------------------
    InitialValue,
    Phi,
    /// <summary>The value of an argument of an inlined call (V8: the caller's node itself).</summary>
    Identity,

    // ---- Int32 ------------------------------------------------------------------
    Int32AddWithOverflow,
    Int32SubtractWithOverflow,
    Int32MultiplyWithOverflow,
    Int32DivideWithOverflow,
    Int32ModulusWithOverflow,
    Int32IncrementWithOverflow,
    Int32DecrementWithOverflow,
    Int32NegateWithOverflow,
    Int32BitwiseAnd,
    Int32BitwiseOr,
    Int32BitwiseXor,
    Int32ShiftLeft,
    Int32ShiftRight,
    Int32ShiftRightLogical,
    Int32BitwiseNot,
    Int32AbsWithOverflow,
    Int32Compare,
    Int32ToBoolean,

    // ---- Float64 ----------------------------------------------------------------
    Float64Add,
    Float64Subtract,
    Float64Multiply,
    Float64Divide,
    Float64Modulus,
    Float64Exponentiate,
    Float64Negate,
    Float64Abs,
    Float64Sqrt,
    Float64Round,
    Float64Min,
    Float64Max,
    Float64Ieee754Unary,
    Float64Ieee754Binary,
    Float64Compare,
    Float64ToBoolean,

    // ---- Conversions --------------------------------------------------------------
    CheckedSmiUntag,
    UnsafeSmiUntag,
    CheckedNumberToInt32,
    CheckedNumberOrOddballToFloat64,
    UnsafeNumberToFloat64,
    Int32ToNumber,
    Uint32ToNumber,
    Float64ToTagged,
    HoleyFloat64ToTagged,
    ConvertHoleToUndefined,
    ChangeInt32ToFloat64,
    ChangeUint32ToFloat64,
    CheckedFloat64ToInt32,
    TruncateFloat64ToInt32,
    TruncateCheckedNumberOrOddballToInt32,
    CheckedUint32ToInt32,
    CheckedHoleyFloat64ToFloat64,

    // ---- Checks ---------------------------------------------------------------------
    CheckSmi,
    CheckNumber,
    CheckHeapObject,
    CheckMaps,
    CheckString,
    CheckSymbol,
    CheckValue,
    CheckInt32IsSmi,
    CheckInt32Condition,
    CheckNotHole,
    CheckInstanceType,
    /// <summary>V8Sharp: a prototype chain validity cell is still valid (V8 depends on stable maps instead).</summary>
    CheckValidityCell,

    // ---- Loads and stores ---------------------------------------------------------------
    /// <summary>The map of a JSReceiver as a tagged value (undefined for anything else).</summary>
    LoadMap,
    LoadTaggedField,
    StoreTaggedField,
    /// <summary>A field-adding map transition: grows the PropertyArray if needed, stores the value, then the map.</summary>
    StoreMapTransition,
    LoadElements,
    LoadFixedArrayElement,
    LoadFixedDoubleArrayElement,
    LoadHoleyFixedDoubleArrayElement,
    StoreFixedArrayElement,
    StoreFixedDoubleArrayElement,
    LoadJSArrayLength,
    LoadFixedArrayLength,
    LoadContextSlot,
    StoreContextSlot,
    StringLength,
    BuiltinStringPrototypeCharCodeAt,
    /// <summary>V8Sharp: charCodeAt with NaN out of bounds (TryReduceStringPrototypeCharCodeAt's Select).</summary>
    BuiltinStringPrototypeCharCodeAtOrNaN,
    /// <summary>CheckedObjectToIndex: Smi, integral HeapNumber or array index String to int32.</summary>
    CheckedObjectToIndex,
    /// <summary>CheckValueEqualsString: the value is a string equal to Obj0 (or Value0, a primitive with that name).</summary>
    CheckValueEqualsString,
    /// <summary>TransitionElementsKind: the object (of the source map) transitions to Obj0, the target map.</summary>
    TransitionElementsKind,
    /// <summary>LoadTypedArrayLength: the length of a typed array (0 when detached).</summary>
    LoadTypedArrayLength,
    /// <summary>LoadTypedArrayElement (LoadSignedIntTypedArrayElement ...): Int0 is the elements kind.</summary>
    LoadTypedArrayElement,
    /// <summary>StoreTypedArrayElement (StoreIntTypedArrayElement ...): Int0 is the elements kind; Int1 ignores out of bounds.</summary>
    StoreTypedArrayElement,
    LoadPropertyCellValue,
    StorePropertyCellValue,
    /// <summary>V8Sharp: EnsureWritableFastElements + MaybeGrowFastElements for an append store.</summary>
    MaybeGrowFastElements,
    UpdateJSArrayLength,

    // ---- Operations ------------------------------------------------------------------------
    TaggedEqual,
    TaggedNotEqual,
    ToBoolean,
    ToBooleanLogicalNot,
    LogicalNot,
    TestUndetectable,
    TestTypeOf,
    /// <summary>V8Sharp: a static call of a C# builtin (Maglev's CallBuiltin/generic nodes over BaselineBuiltins).</summary>
    CallBuiltin,
    CallKnownJSFunction,
    FastNewObject,
    HandleNoHeapWritesInterrupt,
    /// <summary>V8Sharp: pushes the interpreter frame of an inlined function (frame record and register window).</summary>
    EnterInlinedFrame,
    /// <summary>V8Sharp: pops the frame an EnterInlinedFrame pushed.</summary>
    LeaveInlinedFrame,
    /// <summary>V8Sharp: sets the current context (isolate.Context and the frame's context slot).</summary>
    SetCurrentContext,
    /// <summary>V8Sharp: stores a value into an interpreter register of the frame (for builtins that read registers).</summary>
    StoreRegister,
    /// <summary>V8Sharp: reads an interpreter register of the frame (an output a builtin wrote through a register reference).</summary>
    LoadRegister,

    // ---- Control --------------------------------------------------------------------------------
    Jump,
    JumpLoop,
    BranchIfToBooleanTrue,
    BranchIfInt32Compare,
    BranchIfFloat64Compare,
    BranchIfReferenceEqual,
    BranchIfRootConstant,
    BranchIfUndefinedOrNull,
    BranchIfJSReceiver,
    BranchIfInt32ToBooleanTrue,
    BranchIfFloat64ToBooleanTrue,
    /// <summary>V8Sharp: branch on a kBit value.</summary>
    BranchIfBit,
    Switch,
    Return,
    Deopt,
}

/// <summary>The comparison of Int32Compare / Float64Compare nodes (V8's Operation subset).</summary>
public enum CompareOperation : byte
{
    kEqual,
    kStrictEqual,
    kLessThan,
    kLessThanOrEqual,
    kGreaterThan,
    kGreaterThanOrEqual,
}

/// <summary>The roots RootConstant can name.</summary>
public enum RootIndex : byte
{
    kUndefinedValue,
    kNullValue,
    kTrueValue,
    kFalseValue,
    kTheHoleValue,
}

/// <summary>NodeBase.</summary>
public abstract class NodeBase(Opcode opcode)
{
    public Opcode Opcode = opcode;
    public ValueNode[] Inputs = [];
    public int Id;
    public OpProperties Properties;
    public EagerDeoptInfo? EagerDeoptInfo;
    public LazyDeoptInfo? LazyDeoptInfo;
    /// <summary>The catch block the node continues at when it throws (null: the exception leaves the code).</summary>
    public ExceptionHandlerInfo? ExceptionHandler;
    /// <summary>The compilation unit (frame) the node belongs to.</summary>
    public MaglevCompilationUnit? Unit;
    /// <summary>The bytecode offset (after any prefix) the frame record holds while the node runs; -1 if none.</summary>
    public int BytecodeOffset = -1;

    // Node parameters (what V8 keeps in the node's own fields).
    public int Int0;
    public int Int1;
    public int Int2;
    public double Double0;
    public JSValue Value0;
    public object? Obj0;
    public object? Obj1;

    public bool CanEagerDeopt => (Properties & OpProperties.kEagerDeopt) != 0;
    public bool CanLazyDeopt => (Properties & OpProperties.kLazyDeopt) != 0;
    public bool IsCall => (Properties & OpProperties.kCall) != 0;
    public bool CanThrow => (Properties & OpProperties.kCanThrow) != 0;
    public bool CanWrite => (Properties & OpProperties.kCanWrite) != 0;

    public ValueNode Input(int i) => Inputs[i];

    public override string ToString() => $"n{Id}: {Opcode}";
}

/// <summary>Node: a non-control node.</summary>
public class Node(Opcode opcode) : NodeBase(opcode)
{
}

/// <summary>ValueNode: a node that produces a value.</summary>
public class ValueNode(Opcode opcode, ValueRepresentation representation) : Node(opcode)
{
    public ValueRepresentation Representation = representation;
    /// <summary>The static type (NodeInfo's type in V8's KnownNodeAspects, for the node itself).</summary>
    public NodeType Type = NodeType.kUnknown;
    public int UseCount;
    /// <summary>Code generation: the IL local holding the value.</summary>
    internal LocalBuilder? Local;
    /// <summary>
    /// For a tagging/untagging conversion, the node it converts (V8 caches
    /// conversions in NodeInfo's alternatives).
    /// </summary>
    public ValueNode? ConversionSource => Inputs.Length == 1 && IsConversion(Opcode) ? Inputs[0] : null;

    public bool IsTagged => Representation == ValueRepresentation.kTagged;
    public bool IsInt32 => Representation == ValueRepresentation.kInt32;
    public bool IsFloat64 => Representation is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64;

    public bool IsConstant => Opcode is Opcode.SmiConstant or Opcode.Int32Constant or Opcode.Float64Constant or
        Opcode.RootConstant or Opcode.Constant;

    public static bool IsConversion(Opcode opcode) => opcode is Opcode.CheckedSmiUntag or Opcode.UnsafeSmiUntag or
        Opcode.CheckedNumberToInt32 or Opcode.CheckedNumberOrOddballToFloat64 or Opcode.UnsafeNumberToFloat64 or
        Opcode.Int32ToNumber or Opcode.Uint32ToNumber or Opcode.Float64ToTagged or Opcode.HoleyFloat64ToTagged or
        Opcode.ChangeInt32ToFloat64 or Opcode.ChangeUint32ToFloat64 or Opcode.CheckedFloat64ToInt32 or
        Opcode.TruncateFloat64ToInt32 or Opcode.TruncateCheckedNumberOrOddballToInt32 or Opcode.CheckedUint32ToInt32;

    /// <summary>The tagged value of a constant node.</summary>
    public JSValue ConstantValue() => Opcode switch
    {
        Opcode.SmiConstant or Opcode.Int32Constant => JSValue.FromInt(Int0),
        Opcode.Float64Constant => JSValue.FromNumber(Double0),
        Opcode.RootConstant => RootValue((RootIndex)Int0),
        Opcode.Constant => Value0,
        _ => throw new InvalidOperationException("not a constant"),
    };

    public static JSValue RootValue(RootIndex index) => index switch
    {
        RootIndex.kUndefinedValue => JSValue.Undefined,
        RootIndex.kNullValue => JSValue.Null,
        RootIndex.kTrueValue => JSValue.True,
        RootIndex.kFalseValue => JSValue.False,
        _ => JSValue.TheHole,
    };

    /// <summary>Whether the node is a constant that is an int32 (TryGetInt32Constant).</summary>
    public bool TryGetInt32Constant(out int value)
    {
        switch (Opcode)
        {
            case Opcode.SmiConstant:
            case Opcode.Int32Constant:
                value = Int0;
                return true;
            case Opcode.Float64Constant:
                value = (int)Double0;
                return value == Double0 && (value != 0 || !double.IsNegative(Double0));
            case Opcode.Constant when Value0.IsNumber:
                value = (int)Value0.Number;
                return value == Value0.Number && (value != 0 || !double.IsNegative(Value0.Number));
            default:
                value = 0;
                return false;
        }
    }

    public bool TryGetFloat64Constant(out double value)
    {
        switch (Opcode)
        {
            case Opcode.SmiConstant:
            case Opcode.Int32Constant:
                value = Int0;
                return true;
            case Opcode.Float64Constant:
                value = Double0;
                return true;
            case Opcode.Constant when Value0.IsNumber:
                value = Value0.Number;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}

/// <summary>Phi.</summary>
public sealed class Phi(Interpreter.Register owner, int mergeOffset) : ValueNode(Opcode.Phi, ValueRepresentation.kTagged)
{
    /// <summary>The interpreter register the phi merges (V8: Phi::owner).</summary>
    public Interpreter.Register Owner = owner;
    public int MergeOffset = mergeOffset;
    public BasicBlock? Block;
    /// <summary>A loop phi whose back-edge input is not set yet.</summary>
    public bool IsLoopPhi;
    /// <summary>The phi's uses as untagged values (PhiRepresentationSelector bookkeeping).</summary>
    public bool UsedAsUntagged;
    /// <summary>
    /// A phi of a catch block (V8: an exception phi): its inputs are the
    /// values at the throwing nodes, in ExceptionHandlerInfo.ThrowIndex order,
    /// moved by the code generator's exception trampolines. Always tagged.
    /// The accumulator's exception phi has no inputs: it is the exception.
    /// </summary>
    public bool IsExceptionPhi;
    public List<ValueNode> InputList = [];

    public override string ToString() => $"n{Id}: Phi({Owner})";
}

/// <summary>
/// ExceptionHandlerInfo: the catch block of a node that can throw inside a
/// try block, and the node's index among the throws merged into it (the
/// input index of the block's exception phis).
/// </summary>
public sealed class ExceptionHandlerInfo(MergePointInterpreterFrameState catchState, int throwIndex)
{
    public readonly MergePointInterpreterFrameState CatchState = catchState;
    public readonly int ThrowIndex = throwIndex;
}

/// <summary>ControlNode.</summary>
public class ControlNode(Opcode opcode) : NodeBase(opcode)
{
    /// <summary>Jump/JumpLoop target, or the true target of a branch.</summary>
    public BasicBlock? Target;
    /// <summary>The false target of a branch.</summary>
    public BasicBlock? FalseTarget;
    /// <summary>Switch targets (by case value - base) and the fallthrough.</summary>
    public BasicBlock?[]? Targets;
    public CompareOperation Operation;
    /// <summary>BranchIfInt32Compare: compare as uint32 (BuildBranchIfUint32Compare).</summary>
    public bool Unsigned;
    public DeoptimizeReason Reason;
}

/// <summary>A frame of a deopt translation (DeoptFrame).</summary>
public abstract class DeoptFrame(DeoptFrame? parent)
{
    public DeoptFrame? Parent = parent;
}

/// <summary>
/// InterpretedDeoptFrame: an interpreter frame to materialise. For the top
/// frame of an eager deopt, execution continues at <see cref="BytecodeOffset"/>;
/// for a lazy deopt or a parent (caller of an inlined frame), the bytecode at
/// <see cref="BytecodeOffset"/> is a call whose result goes to
/// ResultLocation, and execution continues after it.
/// </summary>
public sealed class InterpretedDeoptFrame(MaglevCompilationUnit unit, int bytecodeOffset, int nextOffset,
    (Interpreter.Register Register, ValueNode Value)[] values, ValueNode closure, DeoptFrame? parent) : DeoptFrame(parent)
{
    public MaglevCompilationUnit Unit = unit;
    /// <summary>The offset of the bytecode (its prefix included).</summary>
    public int BytecodeOffset = bytecodeOffset;
    /// <summary>The offset of the next bytecode.</summary>
    public int NextOffset = nextOffset;
    /// <summary>
    /// The values of the frame: parameters (receiver first), live registers,
    /// the current context (Register.CurrentContext) and, if live, the
    /// accumulator (Register.VirtualAccumulator).
    /// </summary>
    public (Interpreter.Register Register, ValueNode Value)[] Values = values;
    public ValueNode Closure = closure;
}

/// <summary>DeoptInfo.</summary>
public abstract class DeoptInfo(DeoptFrame topFrame)
{
    public DeoptFrame TopFrame = topFrame;
    /// <summary>The index of the deopt point in the code's deoptimization data.</summary>
    public int DeoptIndex = -1;
    /// <summary>Code generation: the label of the deopt exit.</summary>
    internal Label? ExitLabel;
}

/// <summary>EagerDeoptInfo.</summary>
public sealed class EagerDeoptInfo(DeoptFrame topFrame, DeoptimizeReason reason) : DeoptInfo(topFrame)
{
    public DeoptimizeReason Reason = reason;
    /// <summary>
    /// The call feedback whose speculation the check made (feedback_to_update):
    /// the deopt disallows speculation there, so the next compilation calls the
    /// builtin generically.
    /// </summary>
    public FeedbackVector? FeedbackToUpdate;
    public int FeedbackSlotToUpdate = -1;
}

/// <summary>LazyDeoptInfo: the result of the call goes to ResultLocation (the accumulator or a register).</summary>
public sealed class LazyDeoptInfo(DeoptFrame topFrame, Interpreter.Register resultLocation, int resultSize) : DeoptInfo(topFrame)
{
    public Interpreter.Register ResultLocation = resultLocation;
    public int ResultSize = resultSize;
}

/// <summary>An argument of a CallBuiltin node: where the IL gets it.</summary>
public enum BuiltinArgKind : byte
{
    /// <summary>The isolate.</summary>
    Isolate,
    /// <summary>The InterpreterState by reference (outermost frame only).</summary>
    State,
    /// <summary>Inputs[Index], converted to the parameter's type.</summary>
    Input,
    /// <summary>An int constant (Index).</summary>
    Int,
    /// <summary>A bool constant (Index != 0).</summary>
    Bool,
    /// <summary>A constant object or JSValue (Object), converted to the parameter's type.</summary>
    Constant,
    /// <summary>The register stack index of register Index in the node's frame (fp + Index).</summary>
    RegisterIndex,
    /// <summary>A reference to register Index's slot in the node's frame.</summary>
    RegisterRef,
    /// <summary>A reference to the embedded feedback byte at bytecode offset Index of the node's bytecode.</summary>
    FeedbackByteRef,
    /// <summary>The frame's closure (JSFunction).</summary>
    Closure,
}

public readonly record struct BuiltinArg(BuiltinArgKind Kind, int Index = 0, object? Object = null)
{
    public static BuiltinArg Isolate => new(BuiltinArgKind.Isolate);
    public static BuiltinArg State => new(BuiltinArgKind.State);
    public static BuiltinArg In(int index) => new(BuiltinArgKind.Input, index);
    public static BuiltinArg I(int value) => new(BuiltinArgKind.Int, value);
    public static BuiltinArg B(bool value) => new(BuiltinArgKind.Bool, value ? 1 : 0);
    public static BuiltinArg C(object? value) => new(BuiltinArgKind.Constant, 0, value);
    public static BuiltinArg RegIndex(Interpreter.Register r) => new(BuiltinArgKind.RegisterIndex, r.Index);
    public static BuiltinArg RegRef(Interpreter.Register r) => new(BuiltinArgKind.RegisterRef, r.Index);
    public static BuiltinArg FeedbackRef(int byteOffset) => new(BuiltinArgKind.FeedbackByteRef, byteOffset);
    public static BuiltinArg Closure => new(BuiltinArgKind.Closure);
}

/// <summary>The parameters of a CallBuiltin node (Obj0).</summary>
public sealed class CallBuiltinInfo(MethodInfo method, BuiltinArg[] args, string name)
{
    public MethodInfo Method = method;
    public BuiltinArg[] Args = args;
    public string Name = name;
    /// <summary>The method returns bool and the node branches/deopts on it (CallBuiltin as a check).</summary>
    public bool DeoptIfFalse;
    /// <summary>Registers to write before the call: (register, value).</summary>
    public (Interpreter.Register Register, ValueNode Value)[] RegisterStores = [];
    /// <summary>Registers to read back after the call into the frame state (outputs written through RegisterRef).</summary>
    public Interpreter.Register[] RegisterOutputs = [];
    /// <summary>
    /// CreateMappedArguments / CreateUnmappedArguments whose object can be
    /// elided (V8 escape-analyses the arguments object): its only uses are
    /// CallForwardArguments calls and deopt frames, which materialize it.
    /// </summary>
    public ArgumentsObjectKind ArgumentsKind;
    /// <summary>The object is not created: the forwarding calls read the frame, a deopt materializes it.</summary>
    public bool Elided;
    /// <summary>CallForwardArguments: the arguments object input.</summary>
    public bool ForwardsArguments;
}

/// <summary>The kind of an elidable arguments object.</summary>
public enum ArgumentsObjectKind : byte
{
    None,
    Mapped,
    Unmapped,
}
