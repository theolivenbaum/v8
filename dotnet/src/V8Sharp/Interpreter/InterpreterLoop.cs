// Port of the bytecode handlers of src/interpreter/interpreter-generator.cc
// (and the parts of interpreter-assembler.cc they are built from): one switch
// over the bytecodes of Bytecodes.List.cs. Each case does what the handler of
// the same name does; the helpers it calls are the builtins and runtime
// functions the handler calls (ICs in V8Sharp.IC, runtime functions in
// V8Sharp.Runtime, the call/construct builtins in InterpreterCalls).
//
// The loop keeps the accumulator, the bytecode offset, the context and the
// feedback vector in locals, and the register file on the isolate's register
// stack (operands address it relative to the frame pointer).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.IC;
using V8Sharp.Runtime;
using static V8Sharp.Interpreter.Operands;

namespace V8Sharp.Interpreter;

public static partial class InterpreterExecution
{
    /// <summary>
    /// The dispatch loop, specialized per operand scale. For SingleScale it runs
    /// until the frame returns or suspends; for the prefixed scales it runs one
    /// bytecode and returns to the SingleScale loop.
    /// </summary>
    // The shape of this method is dictated by RyuJIT: a method with more than
    // 512 locals (inlinee temps count) stops inlining, and one with more than
    // JitMaxLocalsToTrack stops promoting structs, which puts the accumulator
    // (a 16-byte JSValue) and the offset in memory and makes every handler
    // copy them. So the loop keeps only the handlers whose fast path is a few
    // instructions; everything else is one call to a NoInlining handler in
    // InterpreterHandlers.cs (the same body the case had), with the operands
    // decoded there. The handlers take (isolate, st, fp, code, pc, acc), which
    // fits the six argument registers plus xmm0. RyuJIT inlines in IL order,
    // so the most frequent handlers come first in the switch, and handlers
    // of one shape share a case (the relational comparisons, the bitwise
    // operators): check with the MethodJitInliningFailed events ("too many
    // locals") after changing the loop.
    //
    // AggressiveOptimization: the loop runs for the whole life of a frame and
    // of every frame it calls inline, so it is entered rarely; with tiered
    // compilation it would run as OSR code (tier-0 frame layout, no struct
    // promotion) and rarely reach tier 1, and its tier-0 instrumentation never
    // produces a profile for it anyway.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static JSValue Loop<TS>(Isolate isolate, ref InterpreterState st) where TS : struct, IOperandScale
    {
        int S = typeof(TS) == typeof(SingleScale) ? 1 : typeof(TS) == typeof(DoubleScale) ? 2 : 4;
        // The state that is not in these locals (the function, its bytecode,
        // constants, feedback vector and context, the frame pointer) lives in
        // {st}: a small set of locals lets the JIT keep them in registers
        // across the dispatch switch.
        ref JSValue fpSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), st.Fp);
        ref byte code = ref MemoryMarshal.GetArrayDataReference(st.Bytecode.Bytecodes);
        JSValue acc = st.Accumulator;
        int pc = st.Pc;
        bool stepped = false;
        // A call or return run in this loop (InterpreterInlineCalls) switches
        // frames by updating {st} and reloading the locals here.
        goto start;
    reload:
        fpSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(st.Isolate.RegisterStack), st.Fp);
        code = ref MemoryMarshal.GetArrayDataReference(st.Bytecode.Bytecodes);
        acc = st.Accumulator;
        pc = st.Pc;
    start:

        while (true)
        {
            if ((typeof(TS) != typeof(SingleScale)))
            {
                if (stepped)
                {
                    st.Pc = pc;
                    st.Accumulator = acc;
                    return default;
                }
                stepped = true;
            }

            // The offset is not stored in the frame record here: the handlers
            // that call out store it (SavePc), as V8's SaveBytecodeOffset.
            switch ((Bytecode)Unsafe.Add(ref code, pc))
            {
                // ---- Loading the accumulator -----------------------------------------
                case Bytecode.Ldar:
                    acc = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.LdaZero:
                    acc = JSValue.Zero;
                    pc += 1;
                    continue;
                case Bytecode.LdaSmi:
                    acc = JSValue.FromInt(Signed<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.LdaUndefined:
                    acc = default(JSValue);
                    pc += 1;
                    continue;
                case Bytecode.LdaNull:
                    acc = JSValue.Null;
                    pc += 1;
                    continue;
                case Bytecode.LdaTheHole:
                case Bytecode.LdaTdzHole:
                    acc = JSValue.TheHole;
                    pc += 1;
                    continue;
                case Bytecode.LdaTrue:
                    acc = JSValue.True;
                    pc += 1;
                    continue;
                case Bytecode.LdaFalse:
                    acc = JSValue.False;
                    pc += 1;
                    continue;
                case Bytecode.LdaConstant:
                    acc = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)];
                    pc += 1 + S;
                    continue;

                // Deviation: V8Sharp has no ContextCells (script/function context
                // cells are an optimizing-tier device that V8 --jitless disables),
                // so the cell and no-cell variants are the same load and store.
                case Bytecode.LdaContextSlotNoCell:
                case Bytecode.LdaContextSlot:
                case Bytecode.LdaImmutableContextSlot:
                {
                    Context c = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).UncheckedAs<Context>();
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    while (depth-- > 0) c = Unsafe.As<Context>(c.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
                    acc = c.Slots[Unsigned<TS>(ref code, pc + 1 + S)];
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.LdaCurrentContextSlotNoCell:
                case Bytecode.LdaCurrentContextSlot:
                case Bytecode.LdaImmutableCurrentContextSlot:
                    acc = st.Context.Slots[Unsigned<TS>(ref code, pc + 1)];
                    pc += 1 + S;
                    continue;

                // ---- Register loads ----------------------------------------------------
                case Bytecode.Star:
                    StoreRegister(ref Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc);
                    pc += 1 + S;
                    continue;
                case Bytecode.Mov:
                    StoreRegister(ref Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S)),
                        Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)));
                    pc += 1 + 2 * S;
                    continue;
                case Bytecode.Star0:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 0), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star1:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 1), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star2:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 2), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star3:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 3), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star4:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 4), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star5:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 5), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star6:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 6), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star7:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 7), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star8:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 8), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star9:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 9), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star10:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 10), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star11:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 11), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star12:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 12), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star13:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 13), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star14:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 14), acc);
                    pc += 1;
                    continue;
                case Bytecode.Star15:
                    StoreRegister(ref Unsafe.Add(ref fpSlot, 15), acc);
                    pc += 1;
                    continue;

                case Bytecode.PushContext:
                    PushContext<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + S;
                    continue;
                case Bytecode.PopContext:
                    PopContext<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc);
                    pc += 1 + S;
                    continue;

                // ---- Test operations -------------------------------------------------------
                case Bytecode.TestReferenceEqual:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    acc = JSValue.FromBoolean(ReferenceEquals(lhs._obj, acc._obj) &&
                        (!ReferenceEquals(lhs._obj, NumberTag.Instance) ||
                         BitConverter.DoubleToInt64Bits(lhs._num) == BitConverter.DoubleToInt64Bits(acc._num)));
                    pc += 1 + S;
                    continue;
                }
                case Bytecode.TestNull:
                    acc = JSValue.FromBoolean(ReferenceEquals(acc._obj, Oddball.Null));
                    pc += 1;
                    continue;
                case Bytecode.TestUndefined:
                    acc = JSValue.FromBoolean(acc._obj is null);
                    pc += 1;
                    continue;
                case Bytecode.TestUndetectable:
                    // x == null: hot in object-graph code (Richards, DeltaBlue).
                    acc = JSValue.FromBoolean(acc._obj is null || ReferenceEquals(acc._obj, Oddball.Null) || IsUndetectableReceiver(acc._obj));
                    pc += 1;
                    continue;
                case Bytecode.TestTypeOf:
                    acc = JSValue.FromBoolean(TestTypeOf(acc, (TestTypeOfFlags.LiteralFlag)Byte(ref code, pc + 1)));
                    pc += 2;
                    continue;

                // ---- Globals ---------------------------------------------------------------
                case Bytecode.LdaGlobal:
                case Bytecode.LdaGlobalInsideTypeof:
                {
                    // LoadGlobalIC.Load's hit on a global object PropertyCell.
                    FeedbackVector? fv = st.FeedbackVector;
                    if (fv is not null && fv.Slots[Unsigned<TS>(ref code, pc + 1 + S)]._obj is PropertyCell cell)
                    {
                        JSValue value = cell.Value;
                        if (!ReferenceEquals(value._obj, Oddball.TheHole) && !ReferenceEquals(value._obj, Oddball.PropertyCellHole) &&
                            cell.PropertyDetails.Kind == PropertyKind.Data)
                        {
                            acc = value;
                            pc += 1 + 2 * S;
                            continue;
                        }
                    }
                    acc = LdaGlobal<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.StaGlobal:
                    StaGlobal<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 2 * S;
                    continue;

                // ---- Context stores ------------------------------------------------------------
                case Bytecode.StaContextSlotNoCell:
                case Bytecode.StaContextSlot:
                {
                    Context c = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).UncheckedAs<Context>();
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    while (depth-- > 0) c = Unsafe.As<Context>(c.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
                    c.Slots[Unsigned<TS>(ref code, pc + 1 + S)] = acc;
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.StaCurrentContextSlotNoCell:
                case Bytecode.StaCurrentContextSlot:
                    st.Context.Slots[Unsigned<TS>(ref code, pc + 1)] = acc;
                    pc += 1 + S;
                    continue;

                // ---- Property loads ------------------------------------------------------------------
                case Bytecode.GetNamedProperty:
                {
                    // The monomorphic hits of AccessorAssembler::HandleLoadICHandlerCase
                    // (LoadIC.LoadNamed): an own field, and a constant on the
                    // prototype chain (methods).
                    HeapObject? o = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1))._obj;
                    FeedbackVector? fv = st.FeedbackVector;
                    if (fv is not null && o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver)
                    {
                        JSValue[] slots = fv.Slots;
                        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                        if (ReferenceEquals(slots[slot]._obj, Unsafe.As<JSReceiver>(o).Map) && slots[slot + 1]._obj is LoadHandler handler)
                        {
                            if (handler.OwnFieldIndex >= 0)
                            {
                                acc = Unsafe.As<JSObject>(o).FieldAt(handler.OwnFieldIndex);
                                pc += 1 + 3 * S;
                                continue;
                            }
                            if (handler.IsPrototypeConstant && handler.IsValid)
                            {
                                acc = handler.Data;
                                pc += 1 + 3 * S;
                                continue;
                            }
                            if (handler.HandlerKind == LoadHandler.Kind.kArrayLength)
                            {
                                // Recorded only for JSArray maps (JSArray::kLengthOffset).
                                acc = Unsafe.As<JSArray>(o).Length;
                                pc += 1 + 3 * S;
                                continue;
                            }
                        }
                    }
                    acc = GetNamedProperty<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.GetKeyedProperty:
                {
                    // KeyedLoadIC.Load's element hits (monomorphic, or polymorphic
                    // as KeyedLoadIC.LoadSlow): an in-bounds, non-hole element of a
                    // fast elements kind.
                    HeapObject? o = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1))._obj;
                    FeedbackVector? fv = st.FeedbackVector;
                    if (fv is not null && acc._obj == NumberTag.Instance && o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver)
                    {
                        JSValue[] slots = fv.Slots;
                        int slot = Unsigned<TS>(ref code, pc + 1 + S);
                        int index = (int)acc._num;
                        HeapObject? feedback = slots[slot]._obj;
                        Map map = Unsafe.As<JSReceiver>(o).Map;
                        HeapObject? found = ReferenceEquals(feedback, map) ? slots[slot + 1]._obj
                            : feedback is FixedArray polymorphic ? FindPolymorphicHandler(polymorphic, map) : null;
                        if (found is LoadHandler handler && handler.FastElementsMode != 0 && index == acc._num && index >= 0)
                        {
                            // The map matched the handler's receiver map: {o} is a JSObject.
                            FixedArrayBase elements = Unsafe.As<JSObject>(o).Elements;
                            if (!handler.IsJSArray || index < (int)Unsafe.As<JSArray>(o).Length._num)
                            {
                                if (elements is FixedArray fixedArray)
                                {
                                    JSValue[] data = fixedArray._data;
                                    if ((uint)index < (uint)data.Length && !ReferenceEquals(data[index]._obj, Oddball.TheHole))
                                    {
                                        acc = data[index];
                                        pc += 1 + 2 * S;
                                        continue;
                                    }
                                }
                                else if (elements is FixedDoubleArray doubleArray)
                                {
                                    double[] data = doubleArray._data;
                                    if ((uint)index < (uint)data.Length && !FixedDoubleArray.IsHoleBits(data[index]))
                                    {
                                        acc = JSValue.FromNumber(data[index]);
                                        pc += 1 + 2 * S;
                                        continue;
                                    }
                                }
                            }
                        }
                    }
                    acc = GetKeyedProperty<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Property stores ------------------------------------------------------------------------
                case Bytecode.SetNamedProperty:
                {
                    // The monomorphic hits of StoreIC.StoreNamed: a store to an own
                    // field, or a transition that adds one.
                    HeapObject? o = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1))._obj;
                    FeedbackVector? fv = st.FeedbackVector;
                    if (fv is not null && o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver)
                    {
                        JSValue[] slots = fv.Slots;
                        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                        // A field store handler is only recorded for a JSObject map.
                        if (ReferenceEquals(slots[slot]._obj, Unsafe.As<JSReceiver>(o).Map) && slots[slot + 1]._obj is StoreHandler handler &&
                            StoreIC.TryStoreOwnField(Unsafe.As<JSObject>(o), handler, acc))
                        {
                            pc += 1 + 3 * S;
                            continue;
                        }
                    }
                    SetNamedProperty<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.DefineNamedOwnProperty:
                    DefineNamedOwnProperty<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 3 * S;
                    continue;
                case Bytecode.SetKeyedProperty:
                {
                    // KeyedStoreIC.Store's monomorphic in-bounds element store.
                    HeapObject? o = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1))._obj;
                    JSValue key = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    FeedbackVector? fv = st.FeedbackVector;
                    if (fv is not null && key._obj == NumberTag.Instance && o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver)
                    {
                        JSValue[] slots = fv.Slots;
                        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                        if (ReferenceEquals(slots[slot]._obj, Unsafe.As<JSReceiver>(o).Map) && slots[slot + 1]._obj is StoreHandler handler &&
                            handler.IsSimpleElementStore && ElementAccess.TryStoreInBounds(Unsafe.As<JSObject>(o), key._num, acc))
                        {
                            pc += 1 + 3 * S;
                            continue;
                        }
                    }
                    SetKeyedProperty<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.StaInArrayLiteral:
                    StaInArrayLiteral<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 3 * S;
                    continue;

                // ---- Compare operations -------------------------------------------------------------------------------
                case Bytecode.TestEqual:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    acc = lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
                        ? InterpreterOps.EqualNumbers(lhs._num, acc._num, ref Unsafe.Add(ref code, pc + 1 + S))
                        : TestEqual<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestEqualStrict:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    acc = lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
                        ? InterpreterOps.EqualNumbers(lhs._num, acc._num, ref Unsafe.Add(ref code, pc + 1 + S))
                        : TestEqualStrict<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestLessThan:
                case Bytecode.TestGreaterThan:
                case Bytecode.TestLessThanOrEqual:
                case Bytecode.TestGreaterThanOrEqual:
                {
                    // The four relational comparisons share one case, so the number
                    // path is inlined once (every inlined call site costs RyuJIT
                    // locals, and the loop is at its limit): TestLessThan ..
                    // TestGreaterThanOrEqual are consecutive, bit 0 swaps the
                    // operands (>, >=) and bit 1 includes equality (<=, >=).
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        int k = Unsafe.Add(ref code, pc) - (int)Bytecode.TestLessThan;
                        double l = lhs._num, r = acc._num;
                        double a = (k & 1) == 0 ? l : r, b = (k & 1) == 0 ? r : l;
                        bool result = (k & 2) == 0 ? a < b : a <= b;
                        InterpreterOps.UpdateCompareFeedbackForNumbers(ref Unsafe.Add(ref code, pc + 1 + S), l, r);
                        acc = result ? JSValue.True : JSValue.False;
                    }
                    else
                    {
                        acc = Relational<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestInstanceOf:
                    acc = TestInstanceOf<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 2 * S;
                    continue;
                case Bytecode.TestIn:
                    acc = TestIn<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 2 * S;
                    continue;
                case Bytecode.ToNumber:
                case Bytecode.ToNumeric:
                    if (acc._obj == NumberTag.Instance)
                    {
                        InterpreterOps.ToNumberFeedbackForNumber(st.FeedbackVector, Unsigned<TS>(ref code, pc + 1), acc._num);
                    }
                    else
                    {
                        acc = ToNumberOrNumeric<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 1 + S;
                    continue;
                case Bytecode.ToBoolean:
                    acc = JSValue.FromBoolean(ToBoolean(acc));
                    pc += 1;
                    continue;
                // ---- Control flow --------------------------------------------------------------------------------------------------
                case Bytecode.JumpLoop:
                {
                    int relative = Unsigned<TS>(ref code, pc + 1);
                    acc = default(JSValue);
                    // The budget interrupt (V8: UpdateInterruptBudget on the backward
                    // jump, with the stack/interrupt check folded in).
                    FeedbackCell cell = st.Function.RawFeedbackCell;
                    if ((cell.InterruptBudget -= relative) < 0 || st.Isolate.StackGuard.HasPendingInterrupts)
                    {
                        JumpLoopInterrupt(st.Isolate, ref st, ref fpSlot, pc);
                    }
                    pc -= relative;
                    // OSR to baseline code when the SharedFunctionInfo has some and the
                    // closure has a feedback vector (InterpreterAssembler::OnStackReplacement,
                    // case 3): Run continues the frame in it at the loop header.
                    // (JumpLoop reloads the feedback vector from the closure when the
                    // frame's cache is empty.)
                    if (st.Isolate.MayHaveBaselineCode && st.Function.Shared.BaselineCode is not null &&
                        (st.FeedbackVector ?? st.Function.RawFeedbackCell.Value as FeedbackVector) is { } osrVector)
                    {
                        Unsafe.Add(ref fpSlot, InterpreterRuntime.kFeedbackVectorOffset) = osrVector;
                        st.Pc = pc;
                        st.Accumulator = acc;
                        st.FeedbackVector = osrVector;
                        st.OsrToBaseline = true;
                        if (typeof(TS) != typeof(SingleScale)) st.Done = true;
                        return acc;
                    }
                    continue;
                }
                case Bytecode.Jump:
                    pc += Unsigned<TS>(ref code, pc + 1);
                    continue;
                case Bytecode.JumpIfToBooleanTrue:
                    pc += ToBoolean(acc) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfToBooleanFalse:
                    pc += !ToBoolean(acc) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfTrue:
                    pc += ReferenceEquals(acc._obj, Oddball.True) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfFalse:
                    pc += ReferenceEquals(acc._obj, Oddball.False) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfNull:
                    pc += ReferenceEquals(acc._obj, Oddball.Null) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfNotNull:
                    pc += !ReferenceEquals(acc._obj, Oddball.Null) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefined:
                    pc += acc._obj is null ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfNotUndefined:
                    pc += acc._obj is not null ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefinedOrNull:
                    pc += acc._obj is null || ReferenceEquals(acc._obj, Oddball.Null) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfJSReceiver:
                    pc += acc._obj is not null && acc._obj.InstanceType >= InstanceTypeChecks.FirstJSReceiver ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfForInDone:
                {
                    JSValue index = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheLength = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 2 * S));
                    // Both are numbers (ForInPrepare / ForInStep).
                    pc += index._num == cacheLength._num ? Unsigned<TS>(ref code, pc + 1) : 1 + 3 * S;
                    continue;
                }
                case Bytecode.SwitchOnSmiNoFeedback:
                {
                    int tableStart = Unsigned<TS>(ref code, pc + 1);
                    int tableLength = Unsigned<TS>(ref code, pc + 1 + S);
                    int caseValueBase = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int caseValue = (int)acc._num - caseValueBase;
                    if (caseValue >= 0 && caseValue < tableLength)
                    {
                        pc += (int)st.Bytecode.ConstantPoolValues![tableStart + caseValue]._num;
                    }
                    else
                    {
                        pc += 1 + 3 * S;
                    }
                    continue;
                }

                case Bytecode.Throw:
                    // Runtime_Throw: Isolate::Throw creates the message, then the
                    // exception unwinds to the handler of this frame (dispatched
                    // directly when there is one) or leaves the frame.
                    ThrowAccumulator(st.Isolate, ref st, pc, acc);
                    pc = st.Pc;
                    acc = st.Accumulator;
                    continue;
                case Bytecode.Return:
                {
                    // UpdateInterruptBudgetOnReturn: the weight is the current bytecode offset.
                    FeedbackCell cell = st.Function.RawFeedbackCell;
                    if ((cell.InterruptBudget -= pc + 1) < 0) InterpreterTiering.OnBudgetInterrupt(st.Isolate, st.Function, withStackCheck: false);
                    if ((typeof(TS) != typeof(SingleScale)))
                    {
                        st.Done = true;
                        st.Accumulator = acc;
                    }
                    else if (InterpreterInlineCalls.TryReturnInline(st.Isolate, ref st, acc))
                    {
                        // Returned to a caller running in this loop (InterpreterInlineCalls).
                        goto reload;
                    }
                    return acc;
                }
                // ---- Calls ------------------------------------------------------------------------------------------
                // A call to a function with bytecode enters it in this loop (the
                // handler sets up the frame and returns true); anything else
                // returns false with the result in st.Accumulator.
                case Bytecode.CallAnyReceiver:
                case Bytecode.CallProperty:
                    if (CallProperty<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 4 * S;
                    continue;
                case Bytecode.CallProperty0:
                    if (CallProperty0<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 3 * S;
                    continue;
                case Bytecode.CallProperty1:
                    if (CallProperty1<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 4 * S;
                    continue;
                case Bytecode.CallProperty2:
                    if (CallProperty2<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 5 * S;
                    continue;
                case Bytecode.CallUndefinedReceiver:
                    if (CallUndefinedReceiver<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 4 * S;
                    continue;
                case Bytecode.CallUndefinedReceiver0:
                    if (CallUndefinedReceiver0<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 2 * S;
                    continue;
                case Bytecode.CallUndefinedReceiver1:
                    if (CallUndefinedReceiver1<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 3 * S;
                    continue;
                case Bytecode.CallUndefinedReceiver2:
                    if (CallUndefinedReceiver2<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 4 * S;
                    continue;
                case Bytecode.CallRuntime:
                    acc = CallRuntime<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 3 + 2 * S;
                    continue;
                case Bytecode.InvokeIntrinsic:
                    acc = InvokeIntrinsic<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + 2 * S;
                    continue;

                // ---- Construct ----------------------------------------------------------------------------------------
                case Bytecode.Construct:
                    if (Construct<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc)) goto reload;
                    acc = st.Accumulator;
                    pc += 1 + 4 * S;
                    continue;

                // ---- Unary operators ------------------------------------------------------------------------------
                case Bytecode.Inc:
                    acc = acc._obj == NumberTag.Instance
                        ? InterpreterOps.IncrementNumber(acc._num, ref Unsafe.Add(ref code, pc + 1))
                        : UnaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2;
                    continue;
                case Bytecode.Dec:
                    acc = acc._obj == NumberTag.Instance
                        ? InterpreterOps.DecrementNumber(acc._num, ref Unsafe.Add(ref code, pc + 1))
                        : UnaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2;
                    continue;
                case Bytecode.Negate:
                case Bytecode.BitwiseNot:
                    acc = UnaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2;
                    continue;
                case Bytecode.ToBooleanLogicalNot:
                    acc = JSValue.FromBoolean(!ToBoolean(acc));
                    pc += 1;
                    continue;
                case Bytecode.LogicalNot:
                    acc = JSValue.FromBoolean(!ReferenceEquals(acc._obj, Oddball.True));
                    pc += 1;
                    continue;
                case Bytecode.TypeOf:
                    acc = InterpreterOps.TypeOf(st.Isolate, acc, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;

                // ---- Binary operators with an immediate ---------------------------------------------------------
                case Bytecode.AddSmi:
                    if (acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.AddNumbers(st.Isolate, acc._num, Signed<TS>(ref code, pc + 1), ref Unsafe.Add(ref code, pc + 1 + S));
                    }
                    else
                    {
                        acc = BinarySmiOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 2 + S;
                    continue;
                case Bytecode.SubSmi:
                    if (acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.SubtractNumbers(acc._num, Signed<TS>(ref code, pc + 1), ref Unsafe.Add(ref code, pc + 1 + S));
                    }
                    else
                    {
                        acc = BinarySmiOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 2 + S;
                    continue;
                case Bytecode.MulSmi:
                case Bytecode.DivSmi:
                case Bytecode.ModSmi:
                    acc = BinarySmiOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseOrSmi:
                case Bytecode.BitwiseXorSmi:
                case Bytecode.BitwiseAndSmi:
                case Bytecode.ShiftLeftSmi:
                case Bytecode.ShiftRightSmi:
                case Bytecode.ShiftRightLogicalSmi:
                {
                    // One call site for the six operators (InterpreterBitwise.TryAny).
                    JSValue result = InterpreterBitwise.TryAny((Bytecode)Unsafe.Add(ref code, pc), acc, Signed<TS>(ref code, pc + 1), true,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    acc = result._obj is not null ? result : BinarySmiOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + S;
                    continue;
                }

                // ---- Binary operators ----------------------------------------------------------------------
                case Bytecode.Add:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.AddNumbers(st.Isolate, lhs._num, acc._num, ref Unsafe.Add(ref code, pc + 1 + S));
                    }
                    else
                    {
                        acc = AddSlow<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Sub:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.SubtractNumbers(lhs._num, acc._num, ref Unsafe.Add(ref code, pc + 1 + S));
                    }
                    else
                    {
                        acc = BinaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Mul:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.MultiplyNumbers(lhs._num, acc._num, ref Unsafe.Add(ref code, pc + 1 + S));
                    }
                    else
                    {
                        acc = BinaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Div:
                case Bytecode.Mod:
                    acc = BinaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseOr:
                case Bytecode.BitwiseXor:
                case Bytecode.BitwiseAnd:
                case Bytecode.ShiftLeft:
                case Bytecode.ShiftRight:
                case Bytecode.ShiftRightLogical:
                {
                    // One call site for the six operators (InterpreterBitwise.TryAny).
                    JSValue result = acc._obj == NumberTag.Instance
                        ? InterpreterBitwise.TryAny((Bytecode)Unsafe.Add(ref code, pc),
                            Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc._num,
                            false, ref Unsafe.Add(ref code, pc + 1 + S))
                        : default;
                    acc = result._obj is not null ? result : BinaryOp<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + S;
                    continue;
                }

                // ---- for-in / for-of ------------------------------------------------------------------------------------------------------
                case Bytecode.ForInEnumerate:
                    acc = ForInEnumerate<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + S;
                    continue;
                case Bytecode.ForInPrepare:
                    ForInPrepare<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    acc = JSValue.Zero;
                    pc += 1 + 2 * S;
                    continue;
                case Bytecode.ForInNext:
                    acc = ForInNext<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 4 * S;
                    continue;
                case Bytecode.ForInStep:
                {
                    ref JSValue index = ref Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    index = JSValue.FromNumber(index._num + 1);
                    pc += 1 + S;
                    continue;
                }
                case Bytecode.CreateArrayLiteral:
                    acc = CreateArrayLiteral<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + 2 * S;
                    continue;
                case Bytecode.CreateEmptyArrayLiteral:
                    acc = CreateEmptyArrayLiteral<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + S;
                    continue;
                case Bytecode.CreateObjectLiteral:
                    acc = CreateObjectLiteral<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + 2 * S;
                    continue;
                case Bytecode.CreateEmptyObjectLiteral:
                    acc = CreateEmptyObjectLiteral(st.Isolate, ref st);
                    pc += 1;
                    continue;
                case Bytecode.CreateClosure:
                    acc = CreateClosure<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 2 + 2 * S;
                    continue;
                case Bytecode.CreateFunctionContext:
                case Bytecode.CreateFunctionContextWithCells:
                case Bytecode.CreateEvalContext:
                    acc = CreateFunctionContext<TS>(st.Isolate, ref st, ref fpSlot, ref code, pc, acc);
                    pc += 1 + 2 * S;
                    continue;

                // ---- Arguments allocation --------------------------------------------------------------------------------------
                case Bytecode.CreateMappedArguments:
                    acc = CreateMappedArguments(st.Isolate, ref st);
                    pc += 1;
                    continue;
                case Bytecode.CreateUnmappedArguments:
                    acc = CreateUnmappedArguments(st.Isolate, ref st);
                    pc += 1;
                    continue;

                case Bytecode.Wide:
                case Bytecode.ExtraWide:
                {
                    if ((typeof(TS) != typeof(SingleScale))) ThrowNestedPrefix();
                    // LdaSmi with a 16/32-bit immediate (loop bounds, constants)
                    // is by far the most frequent prefixed bytecode: it is
                    // handled here rather than by a call into the scaled loop.
                    // The same for the bitwise operators with a wide immediate
                    // (masks such as 0x3fff and 0xfffffff in Crypto).
                    {
                        bool isWide = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.Wide;
                        int immediate = isWide ? Signed<DoubleScale>(ref code, pc + 2) : Signed<QuadrupleScale>(ref code, pc + 2);
                        int immediateEnd = pc + 2 + (isWide ? 2 : 4);
                        JSValue result = default;
                        switch ((Bytecode)Unsafe.Add(ref code, pc + 1))
                        {
                            case Bytecode.LdaSmi:
                                acc = JSValue.FromInt(immediate);
                                pc = immediateEnd;
                                continue;
                            case Bytecode.BitwiseAndSmi:
                            case Bytecode.BitwiseOrSmi:
                            case Bytecode.BitwiseXorSmi:
                                result = InterpreterBitwise.TryAny((Bytecode)Unsafe.Add(ref code, pc + 1), acc, immediate, true,
                                    ref Unsafe.Add(ref code, immediateEnd));
                                break;
                        }
                        if (result._obj is not null)
                        {
                            acc = result;
                            pc = immediateEnd + 1;
                            continue;
                        }
                    }
                    st.Pc = pc + 1;
                    st.Accumulator = acc;
                    if (RunPrefixed(st.Isolate, ref st)) return st.Accumulator;
                    pc = st.Pc;
                    acc = st.Accumulator;
                    continue;
                }

                default:
                {
                    // The rare bytecodes (LoopCold): they are out of this method so the
                    // JIT's inlining budget goes to the frequent handlers.
                    st.Accumulator = acc;
                    int next = LoopCold<TS>(st.Isolate, ref st, pc);
                    acc = st.Accumulator;
                    if (next >= 0)
                    {
                        pc = next;
                        continue;
                    }
                    if (next == kColdReload) goto reload;
                    return acc;
                }
            }
        }
    }

    const int kColdReload = -1;
    const int kColdReturn = -2;

    /// <summary>
    /// The handlers of the bytecodes that are rare in hot code. Returns the next
    /// bytecode offset, or kColdReload / kColdReturn; the accumulator is passed in
    /// <see cref="InterpreterState.Accumulator"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int LoopCold<TS>(Isolate isolate, ref InterpreterState st, int pc) where TS : struct, IOperandScale
    {
        int S = typeof(TS) == typeof(SingleScale) ? 1 : typeof(TS) == typeof(DoubleScale) ? 2 : 4;
        ref JSValue fpSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), st.Fp);
        ref byte code = ref MemoryMarshal.GetArrayDataReference(st.Bytecode.Bytecodes);
        ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[st.FrameIndex];
        frame.Pc = pc;
        JSValue acc = st.Accumulator;
        switch ((Bytecode)Unsafe.Add(ref code, pc))
        {
            // ---- Prefixes ----------------------------------------------------------
            // ---- Lookup slots ------------------------------------------------------------------
            case Bytecode.LdaLookupSlot:
                acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>(),
                    ShouldThrow.ThrowOnError);
                pc += 1 + S;
                goto next;
            case Bytecode.LdaLookupSlotInsideTypeof:
                acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>(),
                    ShouldThrow.DontThrow);
                pc += 1 + S;
                goto next;
            case Bytecode.LdaLookupContextSlotNoCell:
            case Bytecode.LdaLookupContextSlot:
            case Bytecode.LdaLookupContextSlotNoCellInsideTypeof:
            case Bytecode.LdaLookupContextSlotInsideTypeof:
            {
                int slot = Unsigned<TS>(ref code, pc + 1 + S);
                int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                Context? slotContext = InterpreterOps.ContextWithoutExtensionsUpToDepth(st.Context, depth);
                if (slotContext is not null)
                {
                    acc = slotContext.Slots[slot];
                }
                else
                {
                    Bytecode b = (Bytecode)Unsafe.Add(ref code, pc);
                    bool insideTypeof = b is Bytecode.LdaLookupContextSlotNoCellInsideTypeof or Bytecode.LdaLookupContextSlotInsideTypeof;
                    acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>(),
                        insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);
                }
                pc += 1 + 3 * S;
                goto next;
            }
            case Bytecode.LdaLookupGlobalSlot:
            case Bytecode.LdaLookupGlobalSlotInsideTypeof:
            {
                int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                bool insideTypeof = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.LdaLookupGlobalSlotInsideTypeof;
                var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>();
                if (InterpreterOps.ContextWithoutExtensionsUpToDepth(st.Context, depth) is not null)
                {
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = LoadGlobalIC.Load(isolate, st.FeedbackVector, slot, st.Context, name, insideTypeof ? TypeofMode.Inside : TypeofMode.NotInside);
                }
                else
                {
                    acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, name,
                        insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);
                }
                pc += 1 + 3 * S;
                goto next;
            }
            case Bytecode.StaLookupSlot:
            {
                var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>();
                byte flags = (byte)Byte(ref code, pc + 1 + S);
                acc = RuntimeScopes.StoreLookupSlot(isolate, st.Context, name, acc,
                    StoreLookupSlotFlags.GetLanguageMode(flags) == LanguageMode.Strict
                        ? V8Sharp.Common.LanguageMode.Strict
                        : V8Sharp.Common.LanguageMode.Sloppy,
                    StoreLookupSlotFlags.IsLookupHoistingMode(flags));
                pc += 2 + S;
                goto next;
            }
            case Bytecode.GetNamedPropertyFromSuper:
            {
                JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
                int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                acc = LoadIC.LoadSuper(isolate, st.FeedbackVector, slot, receiver, acc, name);
                pc += 1 + 3 * S;
                goto next;
            }
            case Bytecode.GetEnumeratedKeyedProperty:
            {
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                JSValue enumIndex = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                JSValue cacheType = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 2 * S));
                int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                acc = KeyedLoadIC.LoadEnumerated(isolate, st.FeedbackVector, slot, obj, acc, enumIndex, cacheType);
                pc += 1 + 4 * S;
                goto next;
            }
            case Bytecode.GetPrivateField:
            {
                Context c = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).UncheckedAs<Context>();
                int slotIndex = Unsigned<TS>(ref code, pc + 1 + S);
                int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                JSValue symbol = InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slotIndex];
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 3 * S));
                int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
                acc = KeyedLoadIC.Load(isolate, st.FeedbackVector, slot, obj, symbol);
                pc += 1 + 5 * S;
                goto next;
            }

            // ---- Module variables -------------------------------------------------------------------
            case Bytecode.LdaModuleVariable:
            {
                int cellIndex = Signed<TS>(ref code, pc + 1);
                int depth = Unsigned<TS>(ref code, pc + 1 + S);
                acc = InterpreterOps.LoadModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(st.Context, depth), cellIndex);
                pc += 1 + 2 * S;
                goto next;
            }
            case Bytecode.StaModuleVariable:
            {
                int cellIndex = Signed<TS>(ref code, pc + 1);
                int depth = Unsigned<TS>(ref code, pc + 1 + S);
                InterpreterOps.StoreModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(st.Context, depth), cellIndex, acc);
                pc += 1 + 2 * S;
                goto next;
            }
            case Bytecode.DefineKeyedOwnProperty:
            {
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                JSValue key = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                int flags = Byte(ref code, pc + 1 + 2 * S);
                int slot = Unsigned<TS>(ref code, pc + 2 + 2 * S);
                KeyedStoreIC.DefineKeyedOwn(isolate, st.FeedbackVector, slot, obj, key, acc, (DefineKeyedOwnPropertyFlags)flags);
                pc += 2 + 3 * S;
                goto next;
            }
            case Bytecode.DefineKeyedOwnPropertyInLiteral:
            {
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                JSValue name = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                int flags = Byte(ref code, pc + 1 + 2 * S);
                int slot = Unsigned<TS>(ref code, pc + 2 + 2 * S);
                RuntimeObject.DefineKeyedOwnPropertyInLiteral(isolate, obj, name, acc,
                    (DefineKeyedOwnPropertyInLiteralFlags)flags, st.FeedbackVector, slot);
                pc += 2 + 3 * S;
                goto next;
            }
            case Bytecode.SetPrototypeProperties:
            {
                JSValue boilerplate = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)];
                int startSlot = Unsigned<TS>(ref code, pc + 1 + S);
                acc = RuntimeLiterals.SetPrototypeProperties(isolate, st.Context, acc, boilerplate.UncheckedAs<ObjectBoilerplateDescription>(),
                    JSFunctionFeedback.GetClosureFeedbackCellArray(st.Function), startSlot);
                pc += 1 + 2 * S;
                goto next;
            }
            case Bytecode.SetPrivateField:
            {
                Context c = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).UncheckedAs<Context>();
                int slotIndex = Unsigned<TS>(ref code, pc + 1 + S);
                int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                JSValue symbol = InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slotIndex];
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 3 * S));
                int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
                KeyedStoreIC.Store(isolate, st.FeedbackVector, slot, obj, symbol, acc);
                pc += 1 + 5 * S;
                goto next;
            }
            case Bytecode.Exp:
                acc = InterpreterOps.Binary(isolate, Operation.Exponentiate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                    ref Unsafe.Add(ref code, pc + 1 + S));
                pc += 2 + S;
                goto next;
            case Bytecode.Add_StringConstant_Internalize:
            {
                JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                int slot = Unsigned<TS>(ref code, pc + 1 + S);
                int variant = Byte(ref code, pc + 1 + 2 * S);
                acc = InterpreterOps.AddStringConstantAndInternalize(isolate, st.FeedbackVector, slot, lhs, acc,
                    (AddStringConstantAndInternalizeVariant)variant);
                pc += 2 + 2 * S;
                goto next;
            }
            case Bytecode.ExpSmi:
                acc = InterpreterOps.Binary(isolate, Operation.Exponentiate, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                    ref Unsafe.Add(ref code, pc + 1 + S));
                pc += 2 + S;
                goto next;
            case Bytecode.DeletePropertyStrict:
                acc = RuntimeObject.DeleteProperty(isolate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                    V8Sharp.Common.LanguageMode.Strict);
                pc += 1 + S;
                goto next;
            case Bytecode.DeletePropertySloppy:
                acc = RuntimeObject.DeleteProperty(isolate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                    V8Sharp.Common.LanguageMode.Sloppy);
                pc += 1 + S;
                goto next;
            case Bytecode.GetSuperConstructor:
                Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)) = InterpreterOps.GetSuperConstructor(isolate, acc.As<JSFunction>());
                pc += 1 + S;
                goto next;
            case Bytecode.FindNonDefaultConstructorOrConstruct:
            {
                var thisFunction = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).As<JSFunction>();
                JSValue newTarget = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                int output = Signed<TS>(ref code, pc + 1 + 2 * S);
                InterpreterOps.FindNonDefaultConstructorOrConstruct(isolate, thisFunction, newTarget,
                    out JSValue first, out JSValue second);
                Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output) = first;
                Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output - 1) = second;
                pc += 1 + 3 * S;
                goto next;
            }
            case Bytecode.CallWithSpread:
            {
                JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
                acc = InterpreterCalls.CallWithSpread(isolate, callee, isolate.RegisterStack.AsSpan(st.Fp + first, count));
                pc += 1 + 4 * S;
                goto next;
            }
            case Bytecode.CallRuntimeForPair:
            {
                int id = Short(ref code, pc + 1);
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 3);
                int count = Unsigned<TS>(ref code, pc + 3 + S);
                int output = Signed<TS>(ref code, pc + 3 + 2 * S);
                JSValue result0 = RuntimeTable.CallForPair(isolate, (FunctionId)id, isolate.RegisterStack.AsSpan(st.Fp + first, count),
                    out JSValue result1);
                Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output) = result0;
                Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output - 1) = result1;
                acc = result0;
                pc += 3 + 3 * S;
                goto next;
            }
            case Bytecode.CallJSRuntime:
            {
                int index = Byte(ref code, pc + 1);
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 2);
                int count = Unsigned<TS>(ref code, pc + 2 + S);
                JSValue target = st.Context.NativeContext.Slots[index];
                acc = InterpreterCalls.Call(isolate, target, default(JSValue), st.Fp + first, count,
                    ConvertReceiverMode.NullOrUndefined);
                pc += 2 + 2 * S;
                goto next;
            }
            case Bytecode.ConstructWithSpread:
            {
                JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                acc = InterpreterCalls.ConstructWithSpread(isolate, st.FeedbackVector, slot, constructor, acc, isolate.RegisterStack.AsSpan(st.Fp + first, count));
                pc += 1 + 4 * S;
                goto next;
            }
            case Bytecode.ConstructForwardAllArgs:
            {
                JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                int slot = Unsigned<TS>(ref code, pc + 1 + S);
                acc = InterpreterCalls.ConstructForwardAllArgs(isolate, st.FeedbackVector, slot, constructor, acc, st.Fp, st.Argc);
                pc += 1 + 2 * S;
                goto next;
            }

            // ---- Cast operators ---------------------------------------------------------------------------------------
            case Bytecode.ToName:
                acc = ObjectOps.ToName(isolate, acc);
                pc += 1;
                goto next;
            case Bytecode.ToObject:
                Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)) = acc.IsJSReceiver ? acc : ObjectOps.ToObject(isolate, acc);
                pc += 1 + S;
                goto next;
            case Bytecode.ToString:
                if (!acc.IsString) acc = ObjectOps.ToString(isolate, acc);
                pc += 1;
                goto next;

            // ---- Literals ----------------------------------------------------------------------------------------------
            case Bytecode.CreateRegExpLiteral:
            {
                JSValue pattern = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)];
                int slot = Unsigned<TS>(ref code, pc + 1 + S);
                int flags = Short(ref code, pc + 1 + 2 * S);
                acc = RuntimeLiterals.CreateRegExpLiteral(isolate, st.FeedbackVector, slot, pattern.UncheckedAs<JSString>(), flags);
                pc += 3 + 2 * S;
                goto next;
            }
            case Bytecode.CreateArrayFromIterable:
                acc = InterpreterIterators.IterableToListWithSymbolLookup(isolate, acc);
                pc += 1;
                goto next;
            case Bytecode.CloneObject:
            {
                JSValue source = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                int flags = Byte(ref code, pc + 1 + S);
                int slot = Unsigned<TS>(ref code, pc + 2 + S);
                acc = CloneObjectIC.Clone(isolate, st.FeedbackVector, slot, source, CreateObjectLiteralFlags.DecodeFlags((byte)flags));
                pc += 2 + 2 * S;
                goto next;
            }
            case Bytecode.GetTemplateObject:
            {
                JSValue description = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)];
                int slot = Unsigned<TS>(ref code, pc + 1 + S);
                acc = RuntimeLiterals.GetTemplateObject(isolate, st.Function.Shared, description.UncheckedAs<TemplateObjectDescription>(), st.FeedbackVector, slot);
                pc += 1 + 2 * S;
                goto next;
            }

            // ---- Context allocation ----------------------------------------------------------------------------------------
            case Bytecode.CreateBlockContext:
                acc = isolate.Factory.NewBlockContext(st.Context, st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<ScopeInfo>());
                pc += 1 + S;
                goto next;
            case Bytecode.CreateCatchContext:
            {
                JSValue exception = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                var scopeInfo = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<ScopeInfo>();
                acc = isolate.Factory.NewCatchContext(st.Context, scopeInfo, exception);
                pc += 1 + 2 * S;
                goto next;
            }
            case Bytecode.CreateWithContext:
            {
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                var scopeInfo = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<ScopeInfo>();
                acc = RuntimeScopes.PushWithContext(isolate, st.Context, obj, scopeInfo);
                pc += 1 + 2 * S;
                goto next;
            }
            case Bytecode.CreateRestParameter:
                acc = InterpreterArguments.NewRestParameter(isolate, st.Function, st.Fp, st.Argc);
                pc += 1;
                goto next;
            case Bytecode.JumpConstant:
                pc += (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num;
                goto next;
            case Bytecode.JumpIfNullConstant:
                pc += acc.IsNull ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfNotNullConstant:
                pc += !acc.IsNull ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfUndefinedConstant:
                pc += acc.IsUndefined ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfNotUndefinedConstant:
                pc += !acc.IsUndefined ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfUndefinedOrNullConstant:
                pc += acc.IsNullOrUndefined ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfTrueConstant:
                pc += acc.IsTrue ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfFalseConstant:
                pc += acc.IsFalse ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfJSReceiverConstant:
                pc += acc.IsJSReceiver ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfForInDoneConstant:
            {
                JSValue index = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                JSValue cacheLength = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 2 * S));
                pc += index.IsIdenticalTo(cacheLength) ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + 3 * S;
                goto next;
            }
            case Bytecode.JumpIfToBooleanTrueConstant:
                pc += InterpreterOps.ToBoolean(acc) ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.JumpIfToBooleanFalseConstant:
                pc += !InterpreterOps.ToBoolean(acc) ? (int)st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                goto next;
            case Bytecode.ForOfNext:
            {
                JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                JSValue next = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                acc = InterpreterIterators.ForOfNext(isolate, st.FeedbackVector, slot, obj, next);
                pc += 1 + 3 * S;
                goto next;
            }

            // ---- Non-local control flow ------------------------------------------------------------------------------------------------
            case Bytecode.SetPendingMessage:
            {
                JSValue previous = isolate.PendingMessage;
                isolate.PendingMessage = acc;
                acc = previous;
                pc += 1;
                goto next;
            }
            case Bytecode.ReThrow:
            {
                // Runtime_ReThrow: rethrows with the pending message.
                JSMessageObject? message = isolate.PendingMessage.HeapObjectOrNull as JSMessageObject;
                if (TryDispatchToHandler(isolate, ref st, acc, message))
                {
                    pc = st.Pc;
                    acc = st.Accumulator;
                    goto next;
                }
                throw new JavaScriptException(acc, message);
            }
            case Bytecode.ThrowReferenceErrorIfTdzHole:
                if (acc.IsTheHole)
                {
                    RuntimeScopes.ThrowAccessedUninitializedVariable(isolate, st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)]);
                }
                pc += 1 + S;
                goto next;
            case Bytecode.ThrowSuperNotCalledIfTdzHole:
                if (acc.IsTheHole) isolate.ThrowReferenceError(MessageTemplate.SuperNotCalled);
                pc += 1;
                goto next;
            case Bytecode.ThrowSuperAlreadyCalledIfNotTdzHole:
                if (!acc.IsTheHole) isolate.ThrowReferenceError(MessageTemplate.SuperAlreadyCalled);
                pc += 1;
                goto next;
            case Bytecode.ThrowIfNotSuperConstructor:
            {
                JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                if (!ObjectOps.IsConstructor(constructor)) RuntimeClasses.ThrowNotSuperConstructor(isolate, constructor, st.Function);
                pc += 1 + S;
                goto next;
            }

            // ---- Generators ---------------------------------------------------------------------------------------------------------------
            case Bytecode.SwitchOnGeneratorState:
            {
                JSValue maybeGenerator = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                if (maybeGenerator.IsUndefined)
                {
                    pc += 1 + 3 * S;
                    goto next;
                }
                var generator = maybeGenerator.As<JSGeneratorObject>();
                int state = generator.ContinuationValue;
                generator.ContinuationValue = JSGeneratorObject.kGeneratorExecuting;
                st.Context = generator.Context;
                Unsafe.Add(ref fpSlot, InterpreterRuntime.kContextOffset) = st.Context;
                isolate.Context = st.Context;
                int tableStart = Unsigned<TS>(ref code, pc + 1 + S);
                int tableLength = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                if ((uint)state >= (uint)tableLength) throw new InvalidOperationException("V8Sharp: bad generator state");
                pc += (int)st.Bytecode.ConstantPoolValues![tableStart + state]._num;
                goto next;
            }
            case Bytecode.SuspendGenerator:
            {
                var generator = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).As<JSGeneratorObject>();
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                int suspendId = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                InterpreterGenerators.ExportParametersAndRegisterFile(isolate, generator, st.Fp, first, count);
                generator.Context = st.Context;
                generator.ContinuationValue = suspendId;
                // Store the bytecode offset in the [input_or_debug_pos] field, to be used by
                // the inspector.
                generator.InputOrDebugPos = JSValue.FromInt(pc);
                if ((typeof(TS) != typeof(SingleScale)))
                {
                    st.Done = true;
                    st.Accumulator = acc;
                }
                st.Accumulator = acc;
                return kColdReturn;
            }
            case Bytecode.ResumeGenerator:
            {
                var generator = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).As<JSGeneratorObject>();
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                InterpreterGenerators.ImportRegisterFile(isolate, generator, st.Fp, first, count);
                // Return the generator's input_or_debug_pos in the accumulator.
                acc = generator.InputOrDebugPos;
                pc += 1 + 3 * S;
                goto next;
            }

            // ---- Iterator protocol --------------------------------------------------------------------------------------------------------
            case Bytecode.GetIterator:
            {
                JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                int loadSlot = Unsigned<TS>(ref code, pc + 1 + S);
                int callSlot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                acc = InterpreterIterators.GetIterator(isolate, st.FeedbackVector, loadSlot, callSlot, receiver);
                pc += 1 + 3 * S;
                goto next;
            }
            case Bytecode.ArrayDestructure:
            {
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1);
                int count = Unsigned<TS>(ref code, pc + 1 + S);
                InterpreterIterators.ArrayDestructure(isolate, acc, isolate.RegisterStack.AsSpan(st.Fp + first, count));
                acc = default(JSValue);
                pc += 1 + 2 * S;
                goto next;
            }

            // ---- Debugger, coverage, abort ---------------------------------------------------------------------------------------------------
            case Bytecode.Debugger:
                // Runtime_HandleDebuggerStatement: no debugger is attached.
                acc = default(JSValue);
                pc += 1;
                goto next;
            case Bytecode.IncBlockCounter:
                InterpreterOps.IncBlockCounter(isolate, st.Function, Unsigned<TS>(ref code, pc + 1));
                pc += 1 + S;
                goto next;
            case Bytecode.Abort:
                RuntimeInternal.Abort(isolate, Byte(ref code, pc + 1));
                pc += 2;
                goto next;


            default:
                throw new InvalidOperationException(
                    $"V8Sharp: unexpected bytecode {(Bytecode)Unsafe.Add(ref code, pc)} at offset {pc}");
        }
    next:
        st.Accumulator = acc;
        return pc;
    }
}
