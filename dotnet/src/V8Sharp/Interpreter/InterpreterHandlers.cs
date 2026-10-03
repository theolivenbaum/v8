// The bytecode handlers of src/interpreter/interpreter-generator.cc whose work
// is more than a few instructions, out of the dispatch loop (InterpreterLoop.cs
// says why). Each is the body of the handler of the same name: it decodes its
// operands from {code} at {pc} (the offset of the bytecode, after any prefix),
// reads registers relative to {fp}, and returns the new accumulator; the loop
// advances the offset. The call and construct handlers return true when they
// entered the callee in the loop (InterpreterInlineCalls), and otherwise leave
// the result in st.Accumulator.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.IC;
using V8Sharp.Runtime;
using static V8Sharp.Interpreter.Operands;

namespace V8Sharp.Interpreter;

public static partial class InterpreterExecution
{
    const int kRegBase = -InterpreterRuntime.kRegisterOperandBase;

    /// <summary>
    /// SaveBytecodeOffset: records the offset of the current bytecode in the
    /// frame's bytecode offset slot before a handler calls out, for the stack
    /// walker (source positions, Error.stack) and for the exception handler
    /// lookup. The loop does not store it per bytecode, as V8's handlers do not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SavePc(Isolate isolate, ref InterpreterState st, int pc) => InterpreterRuntime.SetFramePc(ref st.FpRef, pc);

    /// <summary><see cref="SavePc(Isolate, ref InterpreterState, int)"/> for the bytecode at <paramref name="ip"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SavePc(Isolate isolate, ref InterpreterState st, ref byte ip) =>
        InterpreterRuntime.SetFramePc(ref st.FpRef, PcOf(ref st, ref ip));

    /// <summary>SaveBytecodeOffset with the frame's slots at hand: one store.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SavePc(ref JSValue fp, ref byte ip) =>
        InterpreterRuntime.SetFramePc(ref fp, PcOf(ref fp, ref ip));

    /// <summary>The bytecode offset of <paramref name="ip"/> in the bytecode of the frame at <paramref name="fp"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PcOf(ref JSValue fp, ref byte ip) =>
        (int)Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(InterpreterRuntime.FrameBytecode(ref fp).Bytecodes), ref ip);

    /// <summary>
    /// The bytecode offset of <paramref name="ip"/>, a reference into the
    /// bytecode array of the frame <paramref name="st"/> describes. The loop
    /// keeps the current bytecode as a reference (V8's handlers keep the
    /// offset in a register next to the array): one register instead of two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PcOf(ref InterpreterState st, ref byte ip) =>
        (int)Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(st.Bytecode.Bytecodes), ref ip);

    /// <summary>A reference to the bytecode at offset <paramref name="pc"/> of the frame <paramref name="st"/> describes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref byte IpAt(BytecodeArray bytecode, int pc) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bytecode.Bytecodes), pc);

    /// <summary>
    /// The register a register operand names (fp - 7 - operand), with the
    /// index widened before scaling so the JIT folds the constant part into
    /// the address.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref JSValue RegAt(ref JSValue fp, int operand) =>
        ref Unsafe.Subtract(ref Unsafe.Subtract(ref fp, kRegBase), (nint)operand);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Scale<TS>() where TS : struct, IOperandScale =>
        typeof(TS) == typeof(SingleScale) ? 1 : typeof(TS) == typeof(DoubleScale) ? 2 : 4;

    /// <summary>The register named by the register operand at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref JSValue Reg<TS>(ref JSValue fp, ref byte ip, int offset) where TS : struct, IOperandScale =>
        ref RegAt(ref fp, Signed<TS>(ref ip, offset));

    /// <summary>
    /// A register store that skips the reference store (a GC write barrier)
    /// when the slot already holds the same object or tag: registers often keep
    /// numbers (the tag never changes) or the same object across iterations.
    /// </summary>
    // The payload is stored first: after the write barrier call nothing needs
    // the slot's address, so the JIT does not spill it around the call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StoreRegister(ref JSValue slot, JSValue value)
    {
        Unsafe.AsRef(in slot._bits) = value._bits;
        if (!ReferenceEquals(slot._obj, value._obj)) Unsafe.AsRef(in slot._obj) = value._obj;
    }

    /// <summary>
    /// Whether a comparison of two numbers leaves its embedded feedback as it
    /// is (the loop then compares inline; the handler updates it otherwise).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool NumberCompareFeedbackCovered(byte feedback, double l, double r) =>
        InterpreterOps.CompareFeedbackIncludesNumber(feedback) ||
        InterpreterOps.CompareFeedbackIncludesSmi(feedback) && InterpreterOps.IsSmiDouble(l) && InterpreterOps.IsSmiDouble(r);

    /// <summary>ToBoolean on a value passed by value (the loop's accumulator must not have its address taken).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ToBoolean(JSValue value)
    {
        HeapObject? o = value._obj;
        if (ReferenceEquals(o, Oddball.True)) return true;
        if (ReferenceEquals(o, Oddball.False) || o is null) return false;
        if (ReferenceEquals(o, NumberTag.Instance))
        {
            double d = value._num;
            return d != 0 && !double.IsNaN(d);
        }
        return ToBooleanSlow(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ToBooleanSlow(JSValue value) => ObjectOps.BooleanValue(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsUndetectableReceiver(HeapObject o) =>
        o.InstanceType >= InstanceTypeChecks.FirstJSReceiver && Unsafe.As<JSReceiver>(o).Map.IsUndetectable;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TestTypeOf(JSValue value, TestTypeOfFlags.LiteralFlag literal) => InterpreterOps.TestTypeOf(value, literal);

    /// <summary>Whether the callee is Function.prototype.call (of any realm).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsFunctionPrototypeCall(JSValue callee) =>
        callee._obj is JSFunction function && function.Shared.BuiltinId == Builtin.FunctionPrototypeCall;

    /// <summary>The handler for <paramref name="map"/> in polymorphic (map, handler) feedback (LoadIC.FindPolymorphicHandler).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static HeapObject? FindPolymorphicHandler(FixedArray feedback, Map map) => LoadIC.FindPolymorphicHandler(feedback, map);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowNestedPrefix() => throw new InvalidOperationException("V8Sharp: nested operand scale prefix");

    /// <summary>
    /// Wide / ExtraWide: runs the prefixed bytecode at st.Pc in the loop for its
    /// scale. True when it returned from the frame (the result in st.Accumulator).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool RunPrefixed(Isolate isolate, ref InterpreterState st)
    {
        bool wide = (Bytecode)st.Bytecode.Bytecodes[st.Pc - 1] == Bytecode.Wide;
        if (wide && TryRunWide(isolate, ref st)) return false;
        if (wide) Loop<DoubleScale>(isolate, ref st);
        else Loop<QuadrupleScale>(isolate, ref st);
        if (st.Done)
        {
            st.Done = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The Wide bytecodes that are frequent in huge functions (more than 128
    /// registers or 256 feedback slots, such as Emscripten's): register moves,
    /// keyed element accesses and the plain backward jump, run here without
    /// entering Loop&lt;DoubleScale&gt; for a single step (its prologue alone costs
    /// more than these bytecodes). False leaves the bytecode to the loop.
    /// </summary>
    static bool TryRunWide(Isolate isolate, ref InterpreterState st)
    {
        ref byte ip = ref IpAt(st.Bytecode, st.Pc);
        ref JSValue fp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), st.Fp);
        switch ((Bytecode)ip)
        {
            case Bytecode.Star:
                StoreRegister(ref RegAt(ref fp, Signed<DoubleScale>(ref ip, 1)), st.Accumulator);
                st.Pc += 1 + 2;
                return true;
            case Bytecode.Ldar:
                st.Accumulator = RegAt(ref fp, Signed<DoubleScale>(ref ip, 1));
                st.Pc += 1 + 2;
                return true;
            case Bytecode.Mov:
                StoreRegister(ref RegAt(ref fp, Signed<DoubleScale>(ref ip, 1 + 2)), RegAt(ref fp, Signed<DoubleScale>(ref ip, 1)));
                st.Pc += 1 + 2 * 2;
                return true;
            case Bytecode.GetKeyedProperty:
                st.Accumulator = GetKeyedProperty<DoubleScale>(isolate, ref st, ref fp, ref ip, st.Accumulator);
                st.Pc += 1 + 2 * 2;
                return true;
            case Bytecode.SetKeyedProperty:
                SetKeyedProperty<DoubleScale>(isolate, ref st, ref fp, ref ip, st.Accumulator);
                st.Pc += 1 + 3 * 2;
                return true;
            case Bytecode.JumpLoop:
            {
                // Loop's JumpLoop without its interrupt and OSR cases.
                int relative = Unsigned<DoubleScale>(ref ip, 1);
                FeedbackCell cell = st.Function.RawFeedbackCell;
                if (cell.InterruptBudget - relative < 0 || isolate.StackGuard.HasPendingInterrupts ||
                    (isolate.MayHaveBaselineCode && st.Function.Shared.BaselineCode is not null))
                {
                    return false;
                }
                cell.InterruptBudget -= relative;
                st.Accumulator = default;
                st.Pc -= relative;
                return true;
            }
        }
        return false;
    }

    // ---- Contexts --------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PushContext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        // Saves the current context in <context>, and pushes the accumulator
        // as the new current context.
        Reg<TS>(ref fp, ref ip, 1) = InterpreterRuntime.FrameContext(ref fp);
        st.Context = acc.UncheckedAs<Context>();
        isolate.Context = InterpreterRuntime.FrameContext(ref fp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PopContext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        st.Context = Reg<TS>(ref fp, ref ip, 1).UncheckedAs<Context>();
        isolate.Context = InterpreterRuntime.FrameContext(ref fp);
    }

    // ---- Globals -----------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue LdaGlobal<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref ip, 1 + S);
        TypeofMode mode = (Bytecode)ip == Bytecode.LdaGlobal ? TypeofMode.NotInside : TypeofMode.Inside;
        return LoadGlobalIC.Load(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, InterpreterRuntime.FrameContext(ref fp), name, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StaGlobal<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref ip, 1 + S);
        StoreGlobalIC.Store(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, InterpreterRuntime.FrameContext(ref fp), name, acc);
    }

    // ---- Property loads and stores --------------------------------------------------

    /// <summary>
    /// GetNamedProperty after the loop's inline monomorphic hits: the
    /// handler cases of AccessorAssembler::HandleLoadICHandlerCase that need
    /// no call (monomorphic or polymorphic field and prototype constant hits),
    /// and JavaScript getters on the prototype chain or own accessor pairs
    /// entered in this loop (InterpreterInlineCalls.TryEnterFast); everything
    /// else is <see cref="GetNamedPropertySlow"/>. Returns the value, or
    /// InterpreterInlineCalls.FrameEntered when a getter's frame was entered
    /// (returning the value in registers, rather than storing it in
    /// st.Accumulator for the loop to load again).
    /// </summary>
    // A separate method so that this path has no calls: the slow path's
    // inlined EnterInline and IC dispatch made RyuJIT save six registers and
    // spill on every polymorphic field load (13% of Octane Gameboy).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue GetNamedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        HeapObject? o = Reg<TS>(ref fp, ref ip, 1)._obj;
        if (o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
            if ((uint)(slot + 1) < (uint)slots.Length)
            {
                Map map = Unsafe.As<JSReceiver>(o).Map;
                HeapObject? feedback = slots[slot]._obj;
                HeapObject? found = null;
                if (ReferenceEquals(feedback, map)) found = slots[slot + 1]._obj;
                else if (feedback is FixedArray polymorphic)
                {
                    // AccessorAssembler::HandlePolymorphicCase: the (map, handler) pairs.
                    JSValue[] data = polymorphic._data;
                    for (int i = 0; i + 1 < data.Length; i += 2)
                    {
                        if (ReferenceEquals(data[i]._obj, map))
                        {
                            // An own field encoded in the pair (FeedbackNexus.EncodeHandler).
                            int field = FeedbackNexus.DecodeOwnField(data[i + 1]);
                            if (field >= 0) return Unsafe.As<JSObject>(o).FieldAt(field);
                            found = data[i + 1]._obj;
                            break;
                        }
                    }
                }
                else if (ReferenceEquals(feedback, ReadOnlyRoots.megamorphic_symbol) && isolate.ICState is { } icState)
                {
                    // LoadIC_Megamorphic: TryProbeStubCache.
                    var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1 + S)].UncheckedAs<Name>();
                    found = icState.LoadStubCache.Get(name, map);
                }
                if (found is LoadHandler handler)
                {
                    if (handler.OwnFieldIndex >= 0)
                    {
                        return Unsafe.As<JSObject>(o).FieldAt(handler.OwnFieldIndex);
                    }
                    LoadHandler.Kind kind = handler.HandlerKind;
                    if (handler.IsValid)
                    {
                        if (handler.IsPrototypeConstant)
                        {
                            return handler.Data;
                        }
                        if (handler.PrototypeFieldIndex >= 0)
                        {
                            JSValue value = Unsafe.As<JSObject>(handler.Holder!).FieldAt(handler.PrototypeFieldIndex);
                            if (!ReferenceEquals(value._obj, Oddball.Uninitialized))
                            {
                                return value;
                            }
                        }
                        // A JavaScript getter (a function that runs in this loop
                        // has no builtin fast path, which LoadNamedOrGetter tries
                        // first): called like a CallProperty0 with the receiver.
                        JSFunction? getter =
                            kind == LoadHandler.Kind.kAccessorFromPrototype && !map.IsDictionaryMap ? handler.Data._obj as JSFunction
                            : kind == LoadHandler.Kind.kAccessorPair ? Unsafe.As<AccessorPair>(handler.Data._obj!).Getter._obj as JSFunction
                            : null;
                        if (typeof(TS) == typeof(SingleScale) && getter is not null)
                        {
                            int pc = PcOf(ref fp, ref ip);
                            if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 3 * S, ref Unsafe.NullRef<JSValue>(), getter,
                                    JSValue.FromObject(o), new Baseline.BaselineCalls.NoArguments()))
                            {
                                return InterpreterInlineCalls.FrameEntered;
                            }
                        }
                    }
                }
            }
        }
        return GetNamedPropertySlow<TS>(isolate, ref st, ref fp, ref ip) ? InterpreterInlineCalls.FrameEntered : st.Accumulator;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool GetNamedPropertySlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue receiver = Reg<TS>(ref fp, ref ip, 1);
        var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1 + S)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        JSValue result = LoadIC.LoadNamedOrGetter(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, receiver, name, out JSFunction? getter);
        if (getter is not null)
        {
            // A JavaScript getter from the feedback (LoadHandler accessor
            // cases): called like a CallProperty0 with the receiver, in this
            // loop; its return value is the accumulator at the next bytecode.
            if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(getter, out JSFunction target, out int mode))
            {
                InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, receiver, new Baseline.BaselineCalls.NoArguments(),
                    PcOf(ref st, ref ip) + 1 + 3 * S);
                return true;
            }
            result = ObjectOps.GetPropertyWithDefinedGetter(isolate, receiver, getter);
        }
        st.Accumulator = result;
        return false;
    }

    /// <summary>
    /// GetKeyedProperty after the loop's inline fast-elements hits: a
    /// monomorphic in-bounds typed array element load (KeyedLoadIC.Load's
    /// first case, V8's EmitElementLoad for the typed kinds) read here, the
    /// rest in <see cref="GetKeyedPropertySlow"/>.
    /// </summary>
    // A separate method with no calls (Emscripten code reads HEAP8/HEAP32 per
    // memory access: zlib and Mandreel spent 10-13% in the IC's handler,
    // which called TypedArrayElementsOps.LoadElement out of line).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue GetKeyedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        if (Reg<TS>(ref fp, ref ip, 1)._obj is JSTypedArray array && acc._obj == NumberTag.Instance &&
            InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + S);
            byte[]? data = array.FastData;
            Map map = array.Map;
            if ((uint)(slot + 1) < (uint)slots.Length && ReferenceEquals(slots[slot]._obj, map) &&
                slots[slot + 1]._obj is LoadHandler { HandlerKind: LoadHandler.Kind.kElement } &&
                data is not null && JSValue.TryGetIndex(acc._num, out int index) && (uint)index < (uint)array.FastLength &&
                (Protectors.IsArrayBufferDetachingIntact(isolate) || !array.Buffer.WasDetached))
            {
                int offset = array.FastByteOffset;
                switch (map.ElementsKind)
                {
                    case ElementsKind.UINT8_ELEMENTS:
                    case ElementsKind.UINT8_CLAMPED_ELEMENTS:
                        return JSValue.FromInt(data[offset + index]);
                    case ElementsKind.INT8_ELEMENTS:
                        return JSValue.FromInt((sbyte)data[offset + index]);
                    case ElementsKind.UINT16_ELEMENTS:
                        return JSValue.FromInt(Unsafe.ReadUnaligned<ushort>(ref data[offset + index * 2]));
                    case ElementsKind.INT16_ELEMENTS:
                        return JSValue.FromInt(Unsafe.ReadUnaligned<short>(ref data[offset + index * 2]));
                    case ElementsKind.INT32_ELEMENTS:
                        return JSValue.FromInt(Unsafe.ReadUnaligned<int>(ref data[offset + index * 4]));
                    case ElementsKind.UINT32_ELEMENTS:
                        return JSValue.FromNumber(Unsafe.ReadUnaligned<uint>(ref data[offset + index * 4]));
                    case ElementsKind.FLOAT32_ELEMENTS:
                        return JSValue.FromNumber(Unsafe.ReadUnaligned<float>(ref data[offset + index * 4]));
                    case ElementsKind.FLOAT64_ELEMENTS:
                        return JSValue.FromNumber(Unsafe.ReadUnaligned<double>(ref data[offset + index * 8]));
                }
            }
        }
        return GetKeyedPropertySlow<TS>(isolate, ref st, ref fp, ref ip, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue GetKeyedPropertySlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref ip, 1);
        int slot = Unsigned<TS>(ref ip, 1 + S);
        return KeyedLoadIC.Load(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, obj, acc);
    }

    /// <summary>
    /// SetNamedProperty after the loop's inline monomorphic hits: the cases of
    /// HandleStoreICHandlerCase that need no call (a monomorphic or
    /// polymorphic field store) and JavaScript setters (prototype accessors
    /// and own accessor pairs) entered in this loop through
    /// InterpreterInlineCalls.TryEnterFast; everything else (transitions, the
    /// megamorphic stub cache, misses) is <see cref="SetNamedPropertySlow"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool SetNamedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        HeapObject? o = Reg<TS>(ref fp, ref ip, 1)._obj;
        if (o is not null && InstanceTypeChecks.IsJSObject(o.InstanceType) && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
            if ((uint)(slot + 1) < (uint)slots.Length)
            {
                var obj = Unsafe.As<JSObject>(o);
                Map map = obj.Map;
                HeapObject? feedback = slots[slot]._obj;
                HeapObject? found = null;
                if (ReferenceEquals(feedback, map)) found = slots[slot + 1]._obj;
                else if (feedback is FixedArray polymorphic)
                {
                    JSValue[] data = polymorphic._data;
                    for (int i = 0; i + 1 < data.Length; i += 2)
                    {
                        if (ReferenceEquals(data[i]._obj, map))
                        {
                            found = data[i + 1]._obj;
                            break;
                        }
                    }
                }
                else if (ReferenceEquals(feedback, ReadOnlyRoots.megamorphic_symbol) && isolate.ICState is { } icState)
                {
                    // StoreIC_Megamorphic: TryProbeStubCache (SetNamedProperty is
                    // never a DefineNamedOwn slot, so the store stub cache).
                    var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1 + S)].UncheckedAs<Name>();
                    found = icState.StoreStubCache.Get(name, map);
                }
                if (found is StoreHandler handler)
                {
                    if (StoreIC.TryStoreField(obj, handler, acc)) return false;
                    // StoreNamedOrSetter's setter cases (a prototype handler
                    // applies to fast-mode receivers only), called like a
                    // CallProperty1 with the receiver and the value.
                    StoreHandler.Kind kind = handler.HandlerKind;
                    JSFunction? setter = !handler.IsValid ? null
                        : kind == StoreHandler.Kind.kAccessorFromPrototype ? (obj.HasFastProperties ? handler.Data._obj as JSFunction : null)
                        : kind == StoreHandler.Kind.kAccessorPair ? Unsafe.As<AccessorPair>(handler.Data._obj!).Setter._obj as JSFunction
                        : null;
                    if (typeof(TS) == typeof(SingleScale) && setter is not null)
                    {
                        int pc = PcOf(ref fp, ref ip);
                        if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 3 * S, ref Unsafe.NullRef<JSValue>(), setter,
                                JSValue.FromObject(obj), new Baseline.BaselineCalls.OneArgument(acc)))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return SetNamedPropertySlow<TS>(isolate, ref st, ref fp, ref ip, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool SetNamedPropertySlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref ip, 1);
        var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1 + S)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        JSFunction? setter = StoreIC.StoreNamedOrSetter(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, obj, name, acc);
        if (setter is null) return false;
        // A JavaScript setter from the feedback (StoreHandler accessor cases):
        // called like a CallProperty1 with the receiver and the value, in this
        // loop. SetNamedProperty clobbers the accumulator (the bytecode
        // generator reloads the value when the assignment's result is used),
        // so the setter's return value may land in it, as in V8.
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(setter, out JSFunction target, out int mode))
        {
            InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, obj, new Baseline.BaselineCalls.OneArgument(acc),
                PcOf(ref st, ref ip) + 1 + 3 * S);
            return true;
        }
        ObjectOps.SetPropertyWithDefinedSetter(isolate, obj, setter, acc, null);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DefineNamedOwnProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref ip, 1);
        var name = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1 + S)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        StoreIC.DefineNamedOwn(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, obj, name, acc);
    }

    /// <summary>
    /// SetKeyedProperty after the loop's inline fast-elements hits: a
    /// monomorphic in-bounds store of a Number into a typed array
    /// (KeyedStoreIC.Store's first case) written here, the rest in
    /// <see cref="SetKeyedPropertySlow"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetKeyedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        JSValue key = Reg<TS>(ref fp, ref ip, 1 + S);
        if (Reg<TS>(ref fp, ref ip, 1)._obj is JSTypedArray array && key._obj == NumberTag.Instance && acc._obj == NumberTag.Instance &&
            InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
            byte[]? data = array.FastData;
            Map map = array.Map;
            if ((uint)(slot + 1) < (uint)slots.Length && ReferenceEquals(slots[slot]._obj, map) &&
                slots[slot + 1]._obj is StoreHandler { HandlerKind: StoreHandler.Kind.kElement, ElementsTransitionMap: null } handler &&
                handler.IsValid && data is not null && JSValue.TryGetIndex(key._num, out int index) && (uint)index < (uint)array.FastLength)
            {
                // ElementAccess.TryStoreTypedElementFast's conditions.
                ElementsKind kind = map.ElementsKind;
                if (!ElementsKinds.IsBigIntTypedArrayElementsKind(kind) &&
                    (Protectors.IsArrayBufferDetachingIntact(isolate) || !array.Buffer.WasDetached) &&
                    (Protectors.IsArrayBufferMutableIntact(isolate) || !array.Buffer.IsImmutable))
                {
                    TypedArrayElementsOps.StoreElement(data, array.FastByteOffset, kind, index, acc._num);
                    return;
                }
            }
        }
        SetKeyedPropertySlow<TS>(isolate, ref st, ref fp, ref ip, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetKeyedPropertySlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref ip, 1);
        JSValue key = Reg<TS>(ref fp, ref ip, 1 + S);
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        KeyedStoreIC.Store(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, obj, key, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StaInArrayLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue array = Reg<TS>(ref fp, ref ip, 1);
        JSValue index = Reg<TS>(ref fp, ref ip, 1 + S);
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        KeyedStoreIC.StoreInArrayLiteral(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, array, index, acc);
    }

    // ---- Binary operators ----------------------------------------------------------

    /// <summary>Add when an operand is not a number (strings, objects, BigInts).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue AddSlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue lhs = Reg<TS>(ref fp, ref ip, 1);
        // Two numbers whose feedback changes (the loop adds the others inline).
        if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
        {
            return InterpreterOps.AddNumbers(isolate, lhs._num, acc._num, ref Unsafe.Add(ref ip, 1 + S));
        }
        return InterpreterOps.AddSlow(isolate, lhs, acc, ref Unsafe.Add(ref ip, 1 + S));
    }

    /// <summary>The binary operators with a register operand (Sub and Mul when an operand is not a number).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue BinaryOp<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue lhs = Reg<TS>(ref fp, ref ip, 1);
        ref byte feedback = ref Unsafe.Add(ref ip, 1 + S);
        switch ((Bytecode)ip)
        {
            case Bytecode.Sub:
                if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                {
                    return InterpreterOps.SubtractNumbers(lhs._num, acc._num, ref feedback);
                }
                return InterpreterOps.BinarySlow(isolate, Operation.Subtract, lhs, acc, ref feedback);
            case Bytecode.Mul:
                if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                {
                    return InterpreterOps.MultiplyNumbers(lhs._num, acc._num, ref feedback);
                }
                return InterpreterOps.BinarySlow(isolate, Operation.Multiply, lhs, acc, ref feedback);
            case Bytecode.Div:
                return InterpreterOps.Binary(isolate, Operation.Divide, lhs, acc, ref feedback);
            case Bytecode.Mod:
                return InterpreterOps.Binary(isolate, Operation.Modulus, lhs, acc, ref feedback);
            case Bytecode.BitwiseOr:
                return InterpreterBitwise.Binary<BitwiseOrOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.BitwiseXor:
                return InterpreterBitwise.Binary<BitwiseXorOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.BitwiseAnd:
                return InterpreterBitwise.Binary<BitwiseAndOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.ShiftLeft:
                return InterpreterBitwise.Binary<ShiftLeftOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.ShiftRight:
                return InterpreterBitwise.Binary<ShiftRightOp>(isolate, lhs, acc, ref feedback);
            default:
                Debug.Assert((Bytecode)ip == Bytecode.ShiftRightLogical);
                return InterpreterBitwise.Binary<ShiftRightLogicalOp>(isolate, lhs, acc, ref feedback);
        }
    }

    /// <summary>The binary operators with an immediate (AddSmi and SubSmi when the accumulator is not a number).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue BinarySmiOp<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        int imm = Signed<TS>(ref ip, 1);
        ref byte feedback = ref Unsafe.Add(ref ip, 1 + S);
        switch ((Bytecode)ip)
        {
            case Bytecode.AddSmi:
                if (acc._obj == NumberTag.Instance) return InterpreterOps.AddNumbers(isolate, acc._num, imm, ref feedback);
                return InterpreterOps.AddSlow(isolate, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.SubSmi:
                if (acc._obj == NumberTag.Instance) return InterpreterOps.SubtractNumbers(acc._num, imm, ref feedback);
                return InterpreterOps.BinarySlow(isolate, Operation.Subtract, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.MulSmi:
                return InterpreterOps.Binary(isolate, Operation.Multiply, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.DivSmi:
                return InterpreterOps.Binary(isolate, Operation.Divide, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.ModSmi:
                return InterpreterOps.Binary(isolate, Operation.Modulus, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.BitwiseOrSmi:
                return InterpreterBitwise.WithSmi<BitwiseOrOp>(isolate, acc, imm, ref feedback);
            case Bytecode.BitwiseXorSmi:
                return InterpreterBitwise.WithSmi<BitwiseXorOp>(isolate, acc, imm, ref feedback);
            case Bytecode.BitwiseAndSmi:
                return InterpreterBitwise.WithSmi<BitwiseAndOp>(isolate, acc, imm, ref feedback);
            case Bytecode.ShiftLeftSmi:
                return InterpreterBitwise.WithSmi<ShiftLeftOp>(isolate, acc, imm, ref feedback);
            case Bytecode.ShiftRightSmi:
                return InterpreterBitwise.WithSmi<ShiftRightOp>(isolate, acc, imm, ref feedback);
            default:
                Debug.Assert((Bytecode)ip == Bytecode.ShiftRightLogicalSmi);
                return InterpreterBitwise.WithSmi<ShiftRightLogicalOp>(isolate, acc, imm, ref feedback);
        }
    }

    /// <summary>Inc and Dec when the accumulator is not a number, Negate, BitwiseNot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue UnaryOp<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        ref byte feedback = ref Unsafe.Add(ref ip, 1);
        return (Bytecode)ip switch
        {
            Bytecode.Inc => InterpreterOps.Increment(isolate, acc, ref feedback),
            Bytecode.Dec => InterpreterOps.Decrement(isolate, acc, ref feedback),
            Bytecode.Negate => InterpreterOps.Negate(isolate, acc, ref feedback),
            _ => InterpreterOps.BitwiseNot(isolate, acc, ref feedback),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ToNumberOrNumeric<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        return InterpreterOps.ToNumberOrNumeric(isolate, acc, InterpreterRuntime.FrameFeedbackVector(ref fp), Unsigned<TS>(ref ip, 1),
            numeric: (Bytecode)ip == Bytecode.ToNumeric);
    }

    // ---- Compare operations ---------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestEqual<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        return InterpreterOps.Equal(isolate, Reg<TS>(ref fp, ref ip, 1), acc, ref Unsafe.Add(ref ip, 1 + S));
    }

    /// <summary>TestLessThan and friends when an operand is not a number.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue Relational<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        Operation op = (Bytecode)ip switch
        {
            Bytecode.TestLessThan => Operation.LessThan,
            Bytecode.TestGreaterThan => Operation.GreaterThan,
            Bytecode.TestLessThanOrEqual => Operation.LessThanOrEqual,
            _ => Operation.GreaterThanOrEqual,
        };
        JSValue lhs = Reg<TS>(ref fp, ref ip, 1);
        ref byte feedback = ref Unsafe.Add(ref ip, 1 + S);
        return lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
            ? InterpreterOps.CompareNumbers(op, lhs._num, acc._num, ref feedback)
            : InterpreterOps.Relational(isolate, op, lhs, acc, ref feedback);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestInstanceOf<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref ip, 1);
        int slot = Unsigned<TS>(ref ip, 1 + S);
        return InterpreterOps.InstanceOf(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, obj, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestIn<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue name = Reg<TS>(ref fp, ref ip, 1);
        int slot = Unsigned<TS>(ref ip, 1 + S);
        return KeyedHasIC.Has(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, acc, name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestEqualStrict<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        return InterpreterOps.StrictEqual(Reg<TS>(ref fp, ref ip, 1), acc, ref Unsafe.Add(ref ip, 1 + S));
    }

    // ---- Literals, closures, contexts, arguments ------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateArrayLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue description = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1)];
        int slot = Unsigned<TS>(ref ip, 1 + S);
        int flags = Byte(ref ip, 1 + 2 * S);
        if (InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv && CreateArrayLiteralFlags.DecodeFastCloneSupported((byte)flags) &&
            RuntimeLiterals.TryCreateShallowLiteral(isolate, fv, slot, CreateArrayLiteralFlags.DecodeFlags((byte)flags)) is { } shallow)
        {
            return shallow;
        }
        return RuntimeLiterals.CreateArrayLiteral(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, description.UncheckedAs<ArrayBoilerplateDescription>(),
            CreateArrayLiteralFlags.DecodeFlags((byte)flags));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateEmptyArrayLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        return RuntimeLiterals.CreateEmptyArrayLiteral(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), Unsigned<TS>(ref ip, 1));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateObjectLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        int slot = Unsigned<TS>(ref ip, 1 + S);
        int flags = Byte(ref ip, 1 + 2 * S);
        if (InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv && CreateObjectLiteralFlags.DecodeFastCloneSupported((byte)flags))
        {
            // CreateShallowObjectLiteral: an allocation, so no SavePc.
            if (RuntimeLiterals.TryCreateShallowObjectLiteral(fv, slot) is { } fast) return fast;
            SavePc(ref fp, ref ip);
            if (RuntimeLiterals.TryCreateShallowLiteral(isolate, fv, slot, CreateObjectLiteralFlags.DecodeFlags((byte)flags)) is { } shallow)
            {
                return shallow;
            }
        }
        SavePc(ref fp, ref ip);
        JSValue description = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1)];
        return RuntimeLiterals.CreateObjectLiteral(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, description.UncheckedAs<ObjectBoilerplateDescription>(),
            CreateObjectLiteralFlags.DecodeFlags((byte)flags));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateEmptyObjectLiteral(Isolate isolate, ref InterpreterState st) =>
        RuntimeLiterals.CreateEmptyObjectLiteral(isolate, st.Context.NativeContext);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateClosure<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        // FastNewClosure: an allocation, which cannot throw, so no SavePc.
        int S = Scale<TS>();
        var shared = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1)].UncheckedAs<SharedFunctionInfo>();
        int slot = Unsigned<TS>(ref ip, 1 + S);
        // LoadClosureFeedbackArray: the feedback vector's, or the cell's own array.
        ClosureFeedbackCellArray cells = InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv ? fv.ClosureFeedbackCellArray
            : JSFunctionFeedback.GetClosureFeedbackCellArray(InterpreterRuntime.FrameFunction(ref fp));
        return Factory.FastNewClosure(shared, InterpreterRuntime.FrameContext(ref fp), cells.Get(slot));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateFunctionContext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        var scopeInfo = InterpreterRuntime.FrameBytecode(ref fp).ConstantPoolValues![Unsigned<TS>(ref ip, 1)].UncheckedAs<ScopeInfo>();
        return RuntimeScopes.NewFunctionContext(isolate, InterpreterRuntime.FrameContext(ref fp), scopeInfo,
            (Bytecode)ip == Bytecode.CreateEvalContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateMappedArguments(Isolate isolate, ref InterpreterState st) =>
        InterpreterArguments.NewSloppyArguments(isolate, st.Function, st.Context, st.Fp, st.Argc);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateUnmappedArguments(Isolate isolate, ref InterpreterState st) =>
        InterpreterArguments.NewStrictArguments(isolate, st.Function, st.Fp, st.Argc);

    // ---- Control flow -------------------------------------------------------------

    /// <summary>JumpLoop's budget interrupt (InterpreterTiering.OnBudgetInterrupt with the stack check).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool JumpLoopInterrupt(Isolate isolate, ref InterpreterState st, ref JSValue fp, int pc, bool osr)
    {
        SavePc(isolate, ref st, pc);
        st.FeedbackVector = InterpreterTiering.OnBudgetInterrupt(isolate, InterpreterRuntime.FrameFunction(ref fp), withStackCheck: true);
        // OnStackReplacement (OSR into Maglev code): checked at the budget
        // interrupt rather than on every back edge (deviations.md, Maglev).
        // Only from unprefixed JumpLoops: a Wide JumpLoop runs in a nested scaled dispatch.
        if (osr && isolate.UseOptimizer && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } vector &&
            Maglev.MaglevExecution.TryGetOsrCode(isolate, InterpreterRuntime.FrameFunction(ref fp), vector, InterpreterRuntime.FrameBytecode(ref fp), pc) is { } osrCode)
        {
            st.OsrCode = osrCode;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Throw: creates the message like Isolate::Throw and continues at this
    /// frame's handler (st.Pc, st.Accumulator), or throws the exception out of
    /// the frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowAccumulator(Isolate isolate, ref InterpreterState st, int pc, JSValue acc)
    {
        SavePc(isolate, ref st, pc);
        JSMessageObject? message = CreateMessageForThrow(isolate, acc);
        if (TryDispatchToHandler(isolate, ref st, acc, message)) return;
        throw new JavaScriptException(acc, message);
    }

    // ---- for-in -------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ForInEnumerate<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        return RuntimeForIn.ForInEnumerate(isolate, Reg<TS>(ref fp, ref ip, 1).As<JSReceiver>());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ForInPrepare<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        int output = Signed<TS>(ref ip, 1);
        int slot = Unsigned<TS>(ref ip, 1 + S);
        RuntimeForIn.ForInPrepare(isolate, acc.Object, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, out JSValue cacheArray, out int cacheLength);
        Unsafe.Subtract(ref fp, kRegBase + output) = acc;
        Unsafe.Subtract(ref fp, kRegBase + output - 1) = cacheArray;
        Unsafe.Subtract(ref fp, kRegBase + output - 2) = JSValue.FromInt(cacheLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ForInNext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue receiver = Reg<TS>(ref fp, ref ip, 1);
        int index = (int)Reg<TS>(ref fp, ref ip, 1 + S)._num;
        int pairOperand = Signed<TS>(ref ip, 1 + 2 * S);
        JSValue cacheType = Unsafe.Subtract(ref fp, kRegBase + pairOperand);
        JSValue cacheArray = Unsafe.Subtract(ref fp, kRegBase + pairOperand - 1);
        int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
        JSValue key = cacheArray.UncheckedAs<FixedArray>()[index];
        if (receiver.HeapObjectOrNull is JSReceiver r && ReferenceEquals(r.Map, cacheType.HeapObjectOrNull))
        {
            // Enum cache in use for {receiver}, the {key} is definitely valid.
            return key;
        }
        return RuntimeForIn.ForInNextSlow(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, receiver, key, cacheType);
    }

    // ---- Calls --------------------------------------------------------------------

    /// <summary>
    /// CallProperty: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallPropertySlow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 1 + S);
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 4 * S, ref slots[slot + 1], function,
                        Unsafe.Add(ref fp, first), new Baseline.BaselineCalls.RegisterArguments(st.Fp + first + 1, Unsigned<TS>(ref ip, 1 + 2 * S) - 1)))
                {
                    return true;
                }
            }
        }
        return CallPropertySlow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallPropertySlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 1 + S);
        int count = Unsigned<TS>(ref ip, 1 + 2 * S);
        int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
        JSValue receiver = Unsafe.Add(ref fp, first);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale))
        {
            if (InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
            {
                InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, receiver,
                    new Baseline.BaselineCalls.RegisterArguments(st.Fp + first + 1, count - 1), PcOf(ref fp, ref ip) + 1 + 4 * S);
                return true;
            }
            // f.call(thisArg, ...args).
            if (IsFunctionPrototypeCall(callee) &&
                InterpreterInlineCalls.TryPushFunctionCallFrame(isolate, ref st, receiver, count > 1 ? Unsafe.Add(ref fp, first + 1) : default,
                    st.Fp + first + 2, count > 1 ? count - 2 : 0, PcOf(ref fp, ref ip) + 1 + 4 * S))
            {
                return true;
            }
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, receiver, st.Fp + first + 1, count - 1,
            (Bytecode)ip == Bytecode.CallProperty
                ? ConvertReceiverMode.NotNullOrUndefined
                : ConvertReceiverMode.Any);
        return false;
    }

    /// <summary>
    /// CallProperty0: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallProperty0Slow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty0<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 3 * S, ref slots[slot + 1], function,
                        Reg<TS>(ref fp, ref ip, 1 + S), new Baseline.BaselineCalls.NoArguments()))
                {
                    return true;
                }
            }
        }
        return CallProperty0Slow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty0Slow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        JSValue receiver = Reg<TS>(ref fp, ref ip, 1 + S);
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
        {
            InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, receiver, new Baseline.BaselineCalls.NoArguments(),
                PcOf(ref fp, ref ip) + 1 + 3 * S);
            return true;
        }
        // a.pop(), a.shift(), n.toString(): the builtins' CSA fast paths.
        if (BuiltinFastPaths.TryCall0(isolate, callee, receiver, out JSValue fastResult))
        {
            st.Accumulator = fastResult;
            return false;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, receiver, 0, 0, ConvertReceiverMode.NotNullOrUndefined);
        return false;
    }

    /// <summary>
    /// CallProperty1: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallProperty1Slow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty1<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 4 * S, ref slots[slot + 1], function,
                        Reg<TS>(ref fp, ref ip, 1 + S), new Baseline.BaselineCalls.OneArgument(Reg<TS>(ref fp, ref ip, 1 + 2 * S))))
                {
                    return true;
                }
            }
        }
        return CallProperty1Slow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty1Slow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        JSValue receiver = Reg<TS>(ref fp, ref ip, 1 + S);
        int argOperand = Signed<TS>(ref ip, 1 + 2 * S);
        int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale))
        {
            if (InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
            {
                InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, receiver,
                    new Baseline.BaselineCalls.OneArgument(Unsafe.Subtract(ref fp, kRegBase + argOperand)), PcOf(ref fp, ref ip) + 1 + 4 * S);
                return true;
            }
            // f.call(thisArg).
            if (IsFunctionPrototypeCall(callee) &&
                InterpreterInlineCalls.TryPushFunctionCallFrame(isolate, ref st, receiver, Reg<TS>(ref fp, ref ip, 1 + 2 * S), 0, 0,
                    PcOf(ref fp, ref ip) + 1 + 4 * S))
            {
                return true;
            }
        }
        // a.push(x), Math.floor(x), s.charCodeAt(i) ...: the builtins' CSA fast paths.
        if (BuiltinFastPaths.TryCall1(isolate, callee, receiver, Reg<TS>(ref fp, ref ip, 1 + 2 * S), out JSValue fastResult))
        {
            st.Accumulator = fastResult;
            return false;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, receiver, st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1,
            ConvertReceiverMode.NotNullOrUndefined);
        return false;
    }

    /// <summary>
    /// CallProperty2: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallProperty2Slow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty2<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 4 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 5 * S, ref slots[slot + 1], function,
                        Reg<TS>(ref fp, ref ip, 1 + S), new Baseline.BaselineCalls.TwoArguments(Reg<TS>(ref fp, ref ip, 1 + 2 * S), Reg<TS>(ref fp, ref ip, 1 + 3 * S))))
                {
                    return true;
                }
            }
        }
        return CallProperty2Slow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty2Slow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        JSValue receiver = Reg<TS>(ref fp, ref ip, 1 + S);
        int arg0 = Signed<TS>(ref ip, 1 + 2 * S);
        int arg1 = Signed<TS>(ref ip, 1 + 3 * S);
        int slot = Unsigned<TS>(ref ip, 1 + 4 * S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale))
        {
            if (InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
            {
                InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, receiver,
                    new Baseline.BaselineCalls.TwoArguments(Unsafe.Subtract(ref fp, kRegBase + arg0), Unsafe.Subtract(ref fp, kRegBase + arg1)),
                    PcOf(ref fp, ref ip) + 1 + 5 * S);
                return true;
            }
            // f.call(thisArg, arg).
            if (IsFunctionPrototypeCall(callee) &&
                InterpreterInlineCalls.TryPushFunctionCallFrame(isolate, ref st, receiver, Unsafe.Subtract(ref fp, kRegBase + arg0),
                    st.Fp + InterpreterRuntime.kRegisterOperandBase - arg1, 1, PcOf(ref fp, ref ip) + 1 + 5 * S))
            {
                return true;
            }
            // f.apply(thisArg, arguments) (Class.create-style constructors).
            if (ReferenceEquals(callee._obj, InterpreterRuntime.FrameContext(ref fp).NativeContext.FunctionPrototypeApply) &&
                InterpreterInlineCalls.TryPushApplyFrame(isolate, ref st, receiver, Unsafe.Subtract(ref fp, kRegBase + arg0),
                    Unsafe.Subtract(ref fp, kRegBase + arg1), PcOf(ref fp, ref ip) + 1 + 5 * S))
            {
                return true;
            }
        }
        // Math.max(a, b), Math.pow(a, b) ...: the builtins' CSA fast paths.
        if (BuiltinFastPaths.TryCall2(isolate, callee, receiver, Unsafe.Subtract(ref fp, kRegBase + arg0), Unsafe.Subtract(ref fp, kRegBase + arg1),
                out JSValue fastResult))
        {
            st.Accumulator = fastResult;
            return false;
        }
        st.Accumulator = InterpreterCalls.Call2(isolate, callee, receiver, Unsafe.Subtract(ref fp, kRegBase + arg0),
            Unsafe.Subtract(ref fp, kRegBase + arg1), st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
            ConvertReceiverMode.NotNullOrUndefined);
        return false;
    }

    /// <summary>
    /// CallUndefinedReceiver: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallUndefinedReceiverSlow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 1 + S);
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 4 * S, ref slots[slot + 1], function,
                        default(JSValue), new Baseline.BaselineCalls.RegisterArguments(st.Fp + first, Unsigned<TS>(ref ip, 1 + 2 * S))))
                {
                    return true;
                }
            }
        }
        return CallUndefinedReceiverSlow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiverSlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 1 + S);
        int count = Unsigned<TS>(ref ip, 1 + 2 * S);
        int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
        {
            InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, default(JSValue),
                new Baseline.BaselineCalls.RegisterArguments(st.Fp + first, count), PcOf(ref fp, ref ip) + 1 + 4 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, default(JSValue), st.Fp + first, count, ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    /// <summary>
    /// CallUndefinedReceiver0: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallUndefinedReceiver0Slow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver0<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 1 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 2 * S, ref slots[slot + 1], function,
                        default(JSValue), new Baseline.BaselineCalls.NoArguments()))
                {
                    return true;
                }
            }
        }
        return CallUndefinedReceiver0Slow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver0Slow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        int slot = Unsigned<TS>(ref ip, 1 + S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
        {
            InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, default(JSValue), new Baseline.BaselineCalls.NoArguments(),
                PcOf(ref fp, ref ip) + 1 + 2 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, default(JSValue), 0, 0, ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    /// <summary>
    /// CallUndefinedReceiver1: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallUndefinedReceiver1Slow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver1<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 3 * S, ref slots[slot + 1], function,
                        default(JSValue), new Baseline.BaselineCalls.OneArgument(Reg<TS>(ref fp, ref ip, 1 + S))))
                {
                    return true;
                }
            }
        }
        return CallUndefinedReceiver1Slow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver1Slow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        int argOperand = Signed<TS>(ref ip, 1 + S);
        int slot = Unsigned<TS>(ref ip, 1 + 2 * S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
        {
            InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, default(JSValue),
                new Baseline.BaselineCalls.OneArgument(Unsafe.Subtract(ref fp, kRegBase + argOperand)), PcOf(ref fp, ref ip) + 1 + 3 * S);
            return true;
        }
        if (BuiltinFastPaths.TryCall1(isolate, callee, default(JSValue), Unsafe.Subtract(ref fp, kRegBase + argOperand), out JSValue fastResult))
        {
            st.Accumulator = fastResult;
            return false;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, default(JSValue), st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand,
            1, ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    /// <summary>
    /// CallUndefinedReceiver2: a call to a function that runs in this loop, with feedback
    /// that needs only its call count bumped, through
    /// InterpreterInlineCalls.TryEnterFast; everything else is CallUndefinedReceiver2Slow.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver2<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        if (typeof(TS) == typeof(SingleScale) &&
            Reg<TS>(ref fp, ref ip, 1)._obj is JSFunction function && InterpreterRuntime.FrameFeedbackVector(ref fp) is { } fv)
        {
            const int S = 1;
            JSValue[] slots = fv.Slots;
            int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
            if (InterpreterInlineCalls.FeedbackCovers(slots, slot, function))
            {
                int pc = PcOf(ref fp, ref ip);
                if (InterpreterInlineCalls.TryEnterFast(isolate, ref st, ref fp, pc, pc + 1 + 4 * S, ref slots[slot + 1], function,
                        default(JSValue), new Baseline.BaselineCalls.TwoArguments(Reg<TS>(ref fp, ref ip, 1 + S), Reg<TS>(ref fp, ref ip, 1 + 2 * S))))
                {
                    return true;
                }
            }
        }
        return CallUndefinedReceiver2Slow<TS>(isolate, ref st, ref fp, ref ip);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver2Slow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref ip, 1);
        int arg0 = Signed<TS>(ref ip, 1 + S);
        int arg1 = Signed<TS>(ref ip, 1 + 2 * S);
        int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
        InterpreterCalls.CollectCallFeedback(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.TryGetInlineMode(callee, out JSFunction target, out int mode))
        {
            InterpreterInlineCalls.EnterInline(isolate, ref st, target, mode, default(JSValue),
                new Baseline.BaselineCalls.TwoArguments(Unsafe.Subtract(ref fp, kRegBase + arg0), Unsafe.Subtract(ref fp, kRegBase + arg1)),
                PcOf(ref fp, ref ip) + 1 + 4 * S);
            return true;
        }
        if (BuiltinFastPaths.TryCall2(callee, Unsafe.Subtract(ref fp, kRegBase + arg0), Unsafe.Subtract(ref fp, kRegBase + arg1),
                out JSValue fastResult))
        {
            st.Accumulator = fastResult;
            return false;
        }
        st.Accumulator = InterpreterCalls.Call2(isolate, callee, default(JSValue), Unsafe.Subtract(ref fp, kRegBase + arg0),
            Unsafe.Subtract(ref fp, kRegBase + arg1), st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
            ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CallRuntime<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        int id = Short(ref ip, 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 3);
        int count = Unsigned<TS>(ref ip, 3 + S);
        return RuntimeTable.Call(isolate, (FunctionId)id, isolate.RegisterStack.AsSpan(st.Fp + first, count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue InvokeIntrinsic<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        int id = Byte(ref ip, 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 2);
        int count = Unsigned<TS>(ref ip, 2 + S);
        return InterpreterIntrinsicsDispatch.Invoke(isolate, (IntrinsicsHelper.IntrinsicId)id, isolate.RegisterStack.AsSpan(st.Fp + first, count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Construct<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte ip, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(ref fp, ref ip);
        int S = Scale<TS>();
        JSValue constructor = Reg<TS>(ref fp, ref ip, 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref ip, 1 + S);
        int count = Unsigned<TS>(ref ip, 1 + 2 * S);
        int slot = Unsigned<TS>(ref ip, 1 + 3 * S);
        if (typeof(TS) == typeof(SingleScale) &&
            InterpreterInlineCalls.TryPushConstructFrame(isolate, ref st, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, constructor, acc, st.Fp + first, count, PcOf(ref fp, ref ip) + 1 + 4 * S))
        {
            return true;
        }
        st.Accumulator = InterpreterCalls.Construct(isolate, InterpreterRuntime.FrameFeedbackVector(ref fp), slot, constructor, acc, st.Fp + first, count);
        return false;
    }
}
