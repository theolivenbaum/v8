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
        ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[st.FrameIndex];
        bool stepped = false;
        // A call or return run in this loop (InterpreterInlineCalls) switches
        // frames by updating {st} and reloading the locals here.
        goto start;
    reload:
        fpSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), st.Fp);
        code = ref MemoryMarshal.GetArrayDataReference(st.Bytecode.Bytecodes);
        acc = st.Accumulator;
        pc = st.Pc;
        frame = ref isolate.InterpreterFrames[st.FrameIndex];
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

            frame.Pc = pc;
            switch ((Bytecode)Unsafe.Add(ref code, pc))
            {
                // ---- Prefixes ----------------------------------------------------------
                case Bytecode.Wide:
                case Bytecode.ExtraWide:
                {
                    if ((typeof(TS) != typeof(SingleScale))) throw new InvalidOperationException("V8Sharp: nested operand scale prefix");
                    bool wide = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.Wide;
                    st.Pc = pc + 1;
                    st.Accumulator = acc;
                    if (wide) Loop<DoubleScale>(isolate, ref st);
                    else Loop<QuadrupleScale>(isolate, ref st);
                    if (st.Done)
                    {
                        st.Done = false;
                        return st.Accumulator;
                    }
                    pc = st.Pc;
                    acc = st.Accumulator;
                    continue;
                }

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
                    acc = st.Constants[Unsigned<TS>(ref code, pc + 1)];
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
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slot];
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
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)) = acc;
                    pc += 1 + S;
                    continue;
                case Bytecode.Mov:
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S)) = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    pc += 1 + 2 * S;
                    continue;
                case Bytecode.Star0:
                    Unsafe.Add(ref fpSlot, 0) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star1:
                    Unsafe.Add(ref fpSlot, 1) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star2:
                    Unsafe.Add(ref fpSlot, 2) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star3:
                    Unsafe.Add(ref fpSlot, 3) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star4:
                    Unsafe.Add(ref fpSlot, 4) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star5:
                    Unsafe.Add(ref fpSlot, 5) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star6:
                    Unsafe.Add(ref fpSlot, 6) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star7:
                    Unsafe.Add(ref fpSlot, 7) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star8:
                    Unsafe.Add(ref fpSlot, 8) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star9:
                    Unsafe.Add(ref fpSlot, 9) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star10:
                    Unsafe.Add(ref fpSlot, 10) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star11:
                    Unsafe.Add(ref fpSlot, 11) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star12:
                    Unsafe.Add(ref fpSlot, 12) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star13:
                    Unsafe.Add(ref fpSlot, 13) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star14:
                    Unsafe.Add(ref fpSlot, 14) = acc;
                    pc += 1;
                    continue;
                case Bytecode.Star15:
                    Unsafe.Add(ref fpSlot, 15) = acc;
                    pc += 1;
                    continue;

                case Bytecode.PushContext:
                {
                    // Saves the current context in <context>, and pushes the accumulator
                    // as the new current context.
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)) = st.Context;
                    st.Context = acc.UncheckedAs<Context>();
                    Unsafe.Add(ref fpSlot, InterpreterRuntime.kContextOffset) = st.Context;
                    isolate.Context = st.Context;
                    pc += 1 + S;
                    continue;
                }
                case Bytecode.PopContext:
                    st.Context = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).UncheckedAs<Context>();
                    Unsafe.Add(ref fpSlot, InterpreterRuntime.kContextOffset) = st.Context;
                    isolate.Context = st.Context;
                    pc += 1 + S;
                    continue;

                // ---- Test operations -------------------------------------------------------
                case Bytecode.TestReferenceEqual:
                    acc = JSValue.FromBoolean(Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).IsIdenticalTo(acc));
                    pc += 1 + S;
                    continue;
                case Bytecode.TestUndetectable:
                    acc = JSValue.FromBoolean(InterpreterOps.IsUndetectable(acc));
                    pc += 1;
                    continue;
                case Bytecode.TestNull:
                    acc = JSValue.FromBoolean(acc.IsNull);
                    pc += 1;
                    continue;
                case Bytecode.TestUndefined:
                    acc = JSValue.FromBoolean(acc.IsUndefined);
                    pc += 1;
                    continue;
                case Bytecode.TestTypeOf:
                    acc = JSValue.FromBoolean(InterpreterOps.TestTypeOf(acc, (TestTypeOfFlags.LiteralFlag)Byte(ref code, pc + 1)));
                    pc += 2;
                    continue;

                // ---- Globals ---------------------------------------------------------------
                case Bytecode.LdaGlobal:
                case Bytecode.LdaGlobalInsideTypeof:
                {
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    TypeofMode mode = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.LdaGlobal ? TypeofMode.NotInside : TypeofMode.Inside;
                    acc = LoadGlobalIC.Load(isolate, st.FeedbackVector, slot, st.Context, name, mode);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.StaGlobal:
                {
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    StoreGlobalIC.Store(isolate, st.FeedbackVector, slot, st.Context, name, acc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Context stores ------------------------------------------------------------
                case Bytecode.StaContextSlotNoCell:
                case Bytecode.StaContextSlot:
                {
                    Context c = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).UncheckedAs<Context>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slot] = acc;
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.StaCurrentContextSlotNoCell:
                case Bytecode.StaCurrentContextSlot:
                    st.Context.Slots[Unsigned<TS>(ref code, pc + 1)] = acc;
                    pc += 1 + S;
                    continue;

                // ---- Lookup slots ------------------------------------------------------------------
                case Bytecode.LdaLookupSlot:
                    acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>(),
                        ShouldThrow.ThrowOnError);
                    pc += 1 + S;
                    continue;
                case Bytecode.LdaLookupSlotInsideTypeof:
                    acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>(),
                        ShouldThrow.DontThrow);
                    pc += 1 + S;
                    continue;
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
                        acc = RuntimeScopes.LoadLookupSlot(isolate, st.Context, st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>(),
                            insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);
                    }
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.LdaLookupGlobalSlot:
                case Bytecode.LdaLookupGlobalSlotInsideTypeof:
                {
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    bool insideTypeof = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.LdaLookupGlobalSlotInsideTypeof;
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>();
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
                    continue;
                }
                case Bytecode.StaLookupSlot:
                {
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<JSString>();
                    byte flags = (byte)Byte(ref code, pc + 1 + S);
                    acc = RuntimeScopes.StoreLookupSlot(isolate, st.Context, name, acc,
                        StoreLookupSlotFlags.GetLanguageMode(flags) == LanguageMode.Strict
                            ? V8Sharp.Common.LanguageMode.Strict
                            : V8Sharp.Common.LanguageMode.Sloppy,
                        StoreLookupSlotFlags.IsLookupHoistingMode(flags));
                    pc += 2 + S;
                    continue;
                }

                // ---- Property loads ------------------------------------------------------------------
                case Bytecode.GetNamedProperty:
                {
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = LoadIC.LoadNamed(isolate, st.FeedbackVector, slot, receiver, name);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.GetNamedPropertyFromSuper:
                {
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = LoadIC.LoadSuper(isolate, st.FeedbackVector, slot, receiver, acc, name);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.GetKeyedProperty:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = KeyedLoadIC.Load(isolate, st.FeedbackVector, slot, obj, acc);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.GetEnumeratedKeyedProperty:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue enumIndex = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheType = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 2 * S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    acc = KeyedLoadIC.LoadEnumerated(isolate, st.FeedbackVector, slot, obj, acc, enumIndex, cacheType);
                    pc += 1 + 4 * S;
                    continue;
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
                    continue;
                }

                // ---- Module variables -------------------------------------------------------------------
                case Bytecode.LdaModuleVariable:
                {
                    int cellIndex = Signed<TS>(ref code, pc + 1);
                    int depth = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = InterpreterOps.LoadModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(st.Context, depth), cellIndex);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.StaModuleVariable:
                {
                    int cellIndex = Signed<TS>(ref code, pc + 1);
                    int depth = Unsigned<TS>(ref code, pc + 1 + S);
                    InterpreterOps.StoreModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(st.Context, depth), cellIndex, acc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Property stores ------------------------------------------------------------------------
                case Bytecode.SetNamedProperty:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    StoreIC.StoreNamed(isolate, st.FeedbackVector, slot, obj, name, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.DefineNamedOwnProperty:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    var name = st.Constants[Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    StoreIC.DefineNamedOwn(isolate, st.FeedbackVector, slot, obj, name, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.SetKeyedProperty:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue key = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    KeyedStoreIC.Store(isolate, st.FeedbackVector, slot, obj, key, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.DefineKeyedOwnProperty:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue key = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 2 + 2 * S);
                    KeyedStoreIC.DefineKeyedOwn(isolate, st.FeedbackVector, slot, obj, key, acc, (DefineKeyedOwnPropertyFlags)flags);
                    pc += 2 + 3 * S;
                    continue;
                }
                case Bytecode.StaInArrayLiteral:
                {
                    JSValue array = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue index = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    KeyedStoreIC.StoreInArrayLiteral(isolate, st.FeedbackVector, slot, array, index, acc);
                    pc += 1 + 3 * S;
                    continue;
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
                    continue;
                }
                case Bytecode.SetPrototypeProperties:
                {
                    JSValue boilerplate = st.Constants[Unsigned<TS>(ref code, pc + 1)];
                    int startSlot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = RuntimeLiterals.SetPrototypeProperties(isolate, st.Context, acc, boilerplate.UncheckedAs<ObjectBoilerplateDescription>(),
                        JSFunctionFeedback.GetClosureFeedbackCellArray(st.Function), startSlot);
                    pc += 1 + 2 * S;
                    continue;
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
                    continue;
                }

                // ---- Binary operators ----------------------------------------------------------------------
                case Bytecode.Add:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.AddNumbers(isolate, lhs._num, acc._num, ref feedback);
                    }
                    else
                    {
                        acc = InterpreterOps.AddSlow(isolate, lhs, acc, ref feedback);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Sub:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.SubtractNumbers(lhs._num, acc._num, ref feedback);
                    }
                    else
                    {
                        acc = InterpreterOps.BinarySlow(isolate, Operation.Subtract, lhs, acc, ref feedback);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Mul:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.MultiplyNumbers(lhs._num, acc._num, ref feedback);
                    }
                    else
                    {
                        acc = InterpreterOps.BinarySlow(isolate, Operation.Multiply, lhs, acc, ref feedback);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Div:
                    acc = InterpreterOps.Binary(isolate, Operation.Divide, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.Mod:
                    acc = InterpreterOps.Binary(isolate, Operation.Modulus, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.Exp:
                    acc = InterpreterOps.Binary(isolate, Operation.Exponentiate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseOr:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseOr, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseXor:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseXor, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseAnd:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseAnd, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftLeft:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftLeft, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftRight:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftRight, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftRightLogical:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftRightLogical, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)),
                        acc, ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.Add_StringConstant_Internalize:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int variant = Byte(ref code, pc + 1 + 2 * S);
                    acc = InterpreterOps.AddStringConstantAndInternalize(isolate, st.FeedbackVector, slot, lhs, acc,
                        (AddStringConstantAndInternalizeVariant)variant);
                    pc += 2 + 2 * S;
                    continue;
                }

                // ---- Binary operators with an immediate ---------------------------------------------------------
                case Bytecode.AddSmi:
                {
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    int imm = Signed<TS>(ref code, pc + 1);
                    if (acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.AddNumbers(isolate, acc._num, imm, ref feedback);
                    }
                    else
                    {
                        acc = InterpreterOps.AddSlow(isolate, acc, JSValue.FromInt(imm), ref feedback);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.SubSmi:
                {
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    int imm = Signed<TS>(ref code, pc + 1);
                    if (acc._obj == NumberTag.Instance)
                    {
                        acc = InterpreterOps.SubtractNumbers(acc._num, imm, ref feedback);
                    }
                    else
                    {
                        acc = InterpreterOps.BinarySlow(isolate, Operation.Subtract, acc, JSValue.FromInt(imm), ref feedback);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.MulSmi:
                    acc = InterpreterOps.Binary(isolate, Operation.Multiply, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.DivSmi:
                    acc = InterpreterOps.Binary(isolate, Operation.Divide, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ModSmi:
                    acc = InterpreterOps.Binary(isolate, Operation.Modulus, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ExpSmi:
                    acc = InterpreterOps.Binary(isolate, Operation.Exponentiate, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseOrSmi:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseOr, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseXorSmi:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseXor, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseAndSmi:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseAnd, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftLeftSmi:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftLeft, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftRightSmi:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftRight, acc, JSValue.FromInt(Signed<TS>(ref code, pc + 1)),
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftRightLogicalSmi:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftRightLogical, acc,
                        JSValue.FromInt(Signed<TS>(ref code, pc + 1)), ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;

                // ---- Unary operators ------------------------------------------------------------------------------
                case Bytecode.Inc:
                    acc = InterpreterOps.Increment(isolate, acc, ref Unsafe.Add(ref code, pc + 1));
                    pc += 2;
                    continue;
                case Bytecode.Dec:
                    acc = InterpreterOps.Decrement(isolate, acc, ref Unsafe.Add(ref code, pc + 1));
                    pc += 2;
                    continue;
                case Bytecode.Negate:
                    acc = InterpreterOps.Negate(isolate, acc, ref Unsafe.Add(ref code, pc + 1));
                    pc += 2;
                    continue;
                case Bytecode.BitwiseNot:
                    acc = InterpreterOps.BitwiseNot(isolate, acc, ref Unsafe.Add(ref code, pc + 1));
                    pc += 2;
                    continue;
                case Bytecode.ToBooleanLogicalNot:
                    acc = JSValue.FromBoolean(!InterpreterOps.ToBoolean(acc));
                    pc += 1;
                    continue;
                case Bytecode.LogicalNot:
                    acc = JSValue.FromBoolean(!acc.IsTrue);
                    pc += 1;
                    continue;
                case Bytecode.TypeOf:
                    acc = InterpreterOps.TypeOf(isolate, acc, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.DeletePropertyStrict:
                    acc = RuntimeObject.DeleteProperty(isolate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        V8Sharp.Common.LanguageMode.Strict);
                    pc += 1 + S;
                    continue;
                case Bytecode.DeletePropertySloppy:
                    acc = RuntimeObject.DeleteProperty(isolate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        V8Sharp.Common.LanguageMode.Sloppy);
                    pc += 1 + S;
                    continue;
                case Bytecode.GetSuperConstructor:
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)) = InterpreterOps.GetSuperConstructor(isolate, acc.As<JSFunction>());
                    pc += 1 + S;
                    continue;
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
                    continue;
                }

                // ---- Calls ------------------------------------------------------------------------------------------
                case Bytecode.CallAnyReceiver:
                case Bytecode.CallProperty:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    JSValue receiver = Unsafe.Add(ref fpSlot, first);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, st.Fp + first + 1, count - 1, default, default, pc + 1 + 4 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call(isolate, callee, receiver, st.Fp + first + 1, count - 1,
                        (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.CallProperty
                            ? ConvertReceiverMode.NotNullOrUndefined
                            : ConvertReceiverMode.Any);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallProperty0:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, 0, 0, default, default, pc + 1 + 3 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call(isolate, callee, receiver, 0, 0, ConvertReceiverMode.NotNullOrUndefined);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.CallProperty1:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int receiverOperand = Signed<TS>(ref code, pc + 1 + S);
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + receiverOperand);
                    int argOperand = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1, default, default, pc + 1 + 4 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call(isolate, callee, receiver, st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1,
                        ConvertReceiverMode.NotNullOrUndefined);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallProperty2:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    int arg0 = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int arg1 = Signed<TS>(ref code, pc + 1 + 3 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, arg1 == arg0 - 1 ? st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0 : -1, 2,
                            Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg0), Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg1), pc + 1 + 5 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call2(isolate, callee, receiver, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg0), Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg1),
                        st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
                        ConvertReceiverMode.NotNullOrUndefined);
                    pc += 1 + 5 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), st.Fp + first, count, default, default, pc + 1 + 4 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call(isolate, callee, default(JSValue), st.Fp + first, count,
                        ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver0:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), 0, 0, default, default, pc + 1 + 2 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call(isolate, callee, default(JSValue), 0, 0, ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver1:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int argOperand = Signed<TS>(ref code, pc + 1 + S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1, default, default, pc + 1 + 3 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call(isolate, callee, default(JSValue), st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand,
                        1, ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver2:
                {
                    JSValue callee = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int arg0 = Signed<TS>(ref code, pc + 1 + S);
                    int arg1 = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
                    if (!(typeof(TS) != typeof(SingleScale)) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
                    {
                        InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), arg1 == arg0 - 1 ? st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0 : -1, 2,
                            Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg0), Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg1), pc + 1 + 4 * S);
                        goto reload;
                    }
                    acc = InterpreterCalls.Call2(isolate, callee, default(JSValue), Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg0), Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + arg1),
                        st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
                        ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 4 * S;
                    continue;
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
                    continue;
                }
                case Bytecode.CallRuntime:
                {
                    int id = Short(ref code, pc + 1);
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 3);
                    int count = Unsigned<TS>(ref code, pc + 3 + S);
                    acc = RuntimeTable.Call(isolate, (FunctionId)id, isolate.RegisterStack.AsSpan(st.Fp + first, count));
                    pc += 3 + 2 * S;
                    continue;
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
                    continue;
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
                    continue;
                }
                case Bytecode.InvokeIntrinsic:
                {
                    int id = Byte(ref code, pc + 1);
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 2);
                    int count = Unsigned<TS>(ref code, pc + 2 + S);
                    acc = InterpreterIntrinsicsDispatch.Invoke(isolate, (IntrinsicsHelper.IntrinsicId)id,
                        isolate.RegisterStack.AsSpan(st.Fp + first, count));
                    pc += 2 + 2 * S;
                    continue;
                }

                // ---- Construct ----------------------------------------------------------------------------------------
                case Bytecode.Construct:
                {
                    JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    if (!(typeof(TS) != typeof(SingleScale)) &&
                        InterpreterInlineCalls.TryPushConstructFrame(isolate, ref st, slot, constructor, acc, st.Fp + first, count,
                            pc + 1 + 4 * S))
                    {
                        goto reload;
                    }
                    acc = InterpreterCalls.Construct(isolate, st.FeedbackVector, slot, constructor, acc, st.Fp + first, count);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.ConstructWithSpread:
                {
                    JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    acc = InterpreterCalls.ConstructWithSpread(isolate, st.FeedbackVector, slot, constructor, acc, isolate.RegisterStack.AsSpan(st.Fp + first, count));
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.ConstructForwardAllArgs:
                {
                    JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = InterpreterCalls.ConstructForwardAllArgs(isolate, st.FeedbackVector, slot, constructor, acc, st.Fp, st.Argc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Compare operations -------------------------------------------------------------------------------
                case Bytecode.TestEqual:
                    acc = InterpreterOps.Equal(isolate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.TestEqualStrict:
                    acc = InterpreterOps.StrictEqual(Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.TestLessThan:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
                        ? InterpreterOps.CompareNumbers(Operation.LessThan, lhs._num, acc._num, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.LessThan, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestGreaterThan:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
                        ? InterpreterOps.CompareNumbers(Operation.GreaterThan, lhs._num, acc._num, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.GreaterThan, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestLessThanOrEqual:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
                        ? InterpreterOps.CompareNumbers(Operation.LessThanOrEqual, lhs._num, acc._num, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.LessThanOrEqual, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestGreaterThanOrEqual:
                {
                    JSValue lhs = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
                        ? InterpreterOps.CompareNumbers(Operation.GreaterThanOrEqual, lhs._num, acc._num, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.GreaterThanOrEqual, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestInstanceOf:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = InterpreterOps.InstanceOf(isolate, st.FeedbackVector, slot, obj, acc);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.TestIn:
                {
                    JSValue name = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = KeyedHasIC.Has(isolate, st.FeedbackVector, slot, acc, name);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Cast operators ---------------------------------------------------------------------------------------
                case Bytecode.ToName:
                    acc = ObjectOps.ToName(isolate, acc);
                    pc += 1;
                    continue;
                case Bytecode.ToNumber:
                    acc = InterpreterOps.ToNumberOrNumeric(isolate, acc, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1), numeric: false);
                    pc += 1 + S;
                    continue;
                case Bytecode.ToNumeric:
                    acc = InterpreterOps.ToNumberOrNumeric(isolate, acc, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1), numeric: true);
                    pc += 1 + S;
                    continue;
                case Bytecode.ToObject:
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)) = acc.IsJSReceiver ? acc : ObjectOps.ToObject(isolate, acc);
                    pc += 1 + S;
                    continue;
                case Bytecode.ToString:
                    if (!acc.IsString) acc = ObjectOps.ToString(isolate, acc);
                    pc += 1;
                    continue;
                case Bytecode.ToBoolean:
                    acc = JSValue.FromBoolean(InterpreterOps.ToBoolean(acc));
                    pc += 1;
                    continue;

                // ---- Literals ----------------------------------------------------------------------------------------------
                case Bytecode.CreateRegExpLiteral:
                {
                    JSValue pattern = st.Constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int flags = Short(ref code, pc + 1 + 2 * S);
                    acc = RuntimeLiterals.CreateRegExpLiteral(isolate, st.FeedbackVector, slot, pattern.UncheckedAs<JSString>(), flags);
                    pc += 3 + 2 * S;
                    continue;
                }
                case Bytecode.CreateArrayLiteral:
                {
                    JSValue description = st.Constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    acc = RuntimeLiterals.CreateArrayLiteral(isolate, st.FeedbackVector, slot, description.UncheckedAs<ArrayBoilerplateDescription>(),
                        CreateArrayLiteralFlags.DecodeFlags((byte)flags));
                    pc += 2 + 2 * S;
                    continue;
                }
                case Bytecode.CreateArrayFromIterable:
                    acc = InterpreterIterators.IterableToListWithSymbolLookup(isolate, acc);
                    pc += 1;
                    continue;
                case Bytecode.CreateEmptyArrayLiteral:
                    acc = RuntimeLiterals.CreateEmptyArrayLiteral(isolate, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.CreateObjectLiteral:
                {
                    JSValue description = st.Constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    acc = RuntimeLiterals.CreateObjectLiteral(isolate, st.FeedbackVector, slot, description.UncheckedAs<ObjectBoilerplateDescription>(),
                        CreateObjectLiteralFlags.DecodeFlags((byte)flags));
                    pc += 2 + 2 * S;
                    continue;
                }
                case Bytecode.CreateEmptyObjectLiteral:
                    acc = RuntimeLiterals.CreateEmptyObjectLiteral(isolate, st.Context.NativeContext);
                    pc += 1;
                    continue;
                case Bytecode.CloneObject:
                {
                    JSValue source = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int flags = Byte(ref code, pc + 1 + S);
                    int slot = Unsigned<TS>(ref code, pc + 2 + S);
                    acc = CloneObjectIC.Clone(isolate, st.FeedbackVector, slot, source, CreateObjectLiteralFlags.DecodeFlags((byte)flags));
                    pc += 2 + 2 * S;
                    continue;
                }
                case Bytecode.GetTemplateObject:
                {
                    JSValue description = st.Constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = RuntimeLiterals.GetTemplateObject(isolate, st.Function.Shared, description.UncheckedAs<TemplateObjectDescription>(), st.FeedbackVector, slot);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CreateClosure:
                {
                    var shared = st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<SharedFunctionInfo>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    FeedbackCell cell = JSFunctionFeedback.GetClosureFeedbackCellArray(st.Function).Get(slot);
                    acc = RuntimeClosures.NewClosure(isolate, shared, st.Context, cell);
                    pc += 2 + 2 * S;
                    continue;
                }

                // ---- Context allocation ----------------------------------------------------------------------------------------
                case Bytecode.CreateBlockContext:
                    acc = isolate.Factory.NewBlockContext(st.Context, st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<ScopeInfo>());
                    pc += 1 + S;
                    continue;
                case Bytecode.CreateCatchContext:
                {
                    JSValue exception = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    var scopeInfo = st.Constants[Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<ScopeInfo>();
                    acc = isolate.Factory.NewCatchContext(st.Context, scopeInfo, exception);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CreateFunctionContext:
                case Bytecode.CreateFunctionContextWithCells:
                case Bytecode.CreateEvalContext:
                {
                    var scopeInfo = st.Constants[Unsigned<TS>(ref code, pc + 1)].UncheckedAs<ScopeInfo>();
                    acc = RuntimeScopes.NewFunctionContext(isolate, st.Context, scopeInfo,
                        (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.CreateEvalContext);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CreateWithContext:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    var scopeInfo = st.Constants[Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<ScopeInfo>();
                    acc = RuntimeScopes.PushWithContext(isolate, st.Context, obj, scopeInfo);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Arguments allocation --------------------------------------------------------------------------------------
                case Bytecode.CreateMappedArguments:
                    acc = InterpreterArguments.NewSloppyArguments(isolate, st.Function, st.Context, st.Fp, st.Argc);
                    pc += 1;
                    continue;
                case Bytecode.CreateUnmappedArguments:
                    acc = InterpreterArguments.NewStrictArguments(isolate, st.Function, st.Fp, st.Argc);
                    pc += 1;
                    continue;
                case Bytecode.CreateRestParameter:
                    acc = InterpreterArguments.NewRestParameter(isolate, st.Function, st.Fp, st.Argc);
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
                    if ((cell.InterruptBudget -= relative) < 0 || isolate.StackGuard.HasPendingInterrupts)
                    {
                        st.FeedbackVector = InterpreterTiering.OnBudgetInterrupt(isolate, st.Function, withStackCheck: true);
                        Unsafe.Add(ref fpSlot, InterpreterRuntime.kFeedbackVectorOffset) = st.FeedbackVector is null ? default(JSValue) : st.FeedbackVector;
                    }
                    pc -= relative;
                    continue;
                }
                case Bytecode.Jump:
                    pc += Unsigned<TS>(ref code, pc + 1);
                    continue;
                case Bytecode.JumpConstant:
                    pc += (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num;
                    continue;
                case Bytecode.JumpIfNullConstant:
                    pc += acc.IsNull ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfNotNullConstant:
                    pc += !acc.IsNull ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefinedConstant:
                    pc += acc.IsUndefined ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfNotUndefinedConstant:
                    pc += !acc.IsUndefined ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefinedOrNullConstant:
                    pc += acc.IsNullOrUndefined ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfTrueConstant:
                    pc += acc.IsTrue ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfFalseConstant:
                    pc += acc.IsFalse ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfJSReceiverConstant:
                    pc += acc.IsJSReceiver ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfForInDoneConstant:
                {
                    JSValue index = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheLength = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 2 * S));
                    pc += index.IsIdenticalTo(cacheLength) ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + 3 * S;
                    continue;
                }
                case Bytecode.JumpIfToBooleanTrueConstant:
                    pc += InterpreterOps.ToBoolean(acc) ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfToBooleanFalseConstant:
                    pc += !InterpreterOps.ToBoolean(acc) ? (int)st.Constants[Unsigned<TS>(ref code, pc + 1)]._num : 1 + S;
                    continue;
                case Bytecode.JumpIfToBooleanTrue:
                    pc += InterpreterOps.ToBoolean(acc) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfToBooleanFalse:
                    pc += !InterpreterOps.ToBoolean(acc) ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfTrue:
                    pc += acc.IsTrue ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfFalse:
                    pc += acc.IsFalse ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfNull:
                    pc += acc.IsNull ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfNotNull:
                    pc += !acc.IsNull ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefined:
                    pc += acc.IsUndefined ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfNotUndefined:
                    pc += !acc.IsUndefined ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefinedOrNull:
                    pc += acc.IsNullOrUndefined ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfJSReceiver:
                    pc += acc.IsJSReceiver ? Unsigned<TS>(ref code, pc + 1) : 1 + S;
                    continue;
                case Bytecode.JumpIfForInDone:
                {
                    JSValue index = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheLength = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + 2 * S));
                    pc += index.IsIdenticalTo(cacheLength) ? Unsigned<TS>(ref code, pc + 1) : 1 + 3 * S;
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
                        pc += (int)st.Constants[tableStart + caseValue]._num;
                    }
                    else
                    {
                        pc += 1 + 3 * S;
                    }
                    continue;
                }

                // ---- for-in / for-of ------------------------------------------------------------------------------------------------------
                case Bytecode.ForInEnumerate:
                    acc = RuntimeForIn.ForInEnumerate(isolate, Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1)).As<JSReceiver>());
                    pc += 1 + S;
                    continue;
                case Bytecode.ForInPrepare:
                {
                    int output = Signed<TS>(ref code, pc + 1);
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    RuntimeForIn.ForInPrepare(isolate, acc.Object, st.FeedbackVector, slot, out JSValue cacheArray, out int cacheLength);
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output) = acc;
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output - 1) = cacheArray;
                    Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + output - 2) = JSValue.FromInt(cacheLength);
                    acc = JSValue.Zero;
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.ForInNext:
                {
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int index = (int)Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S))._num;
                    int pairOperand = Signed<TS>(ref code, pc + 1 + 2 * S);
                    JSValue cacheType = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + pairOperand);
                    JSValue cacheArray = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + pairOperand - 1);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    JSValue key = cacheArray.UncheckedAs<FixedArray>()[index];
                    if (receiver.HeapObjectOrNull is JSReceiver r && ReferenceEquals(r.Map, cacheType.HeapObjectOrNull))
                    {
                        // Enum cache in use for {receiver}, the {key} is definitely valid.
                        acc = key;
                    }
                    else
                    {
                        acc = RuntimeForIn.ForInNextSlow(isolate, st.FeedbackVector, slot, receiver, key, cacheType);
                    }
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.ForInStep:
                {
                    ref JSValue index = ref Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    index = JSValue.FromNumber(index._num + 1);
                    pc += 1 + S;
                    continue;
                }
                case Bytecode.ForOfNext:
                {
                    JSValue obj = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    JSValue next = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = InterpreterIterators.ForOfNext(isolate, st.FeedbackVector, slot, obj, next);
                    pc += 1 + 3 * S;
                    continue;
                }

                // ---- Non-local control flow ------------------------------------------------------------------------------------------------
                case Bytecode.SetPendingMessage:
                {
                    JSValue previous = isolate.PendingMessage;
                    isolate.PendingMessage = acc;
                    acc = previous;
                    pc += 1;
                    continue;
                }
                case Bytecode.Throw:
                {
                    // Runtime_Throw: Isolate::Throw creates the message, then the
                    // exception unwinds to the handler of this frame (dispatched
                    // directly when there is one) or leaves the frame.
                    JSMessageObject? message = CreateMessageForThrow(isolate, acc);
                    if (TryDispatchToHandler(isolate, ref st, acc, message))
                    {
                        pc = st.Pc;
                        acc = st.Accumulator;
                        continue;
                    }
                    throw new JavaScriptException(acc, message);
                }
                case Bytecode.ReThrow:
                {
                    // Runtime_ReThrow: rethrows with the pending message.
                    JSMessageObject? message = isolate.PendingMessage.HeapObjectOrNull as JSMessageObject;
                    if (TryDispatchToHandler(isolate, ref st, acc, message))
                    {
                        pc = st.Pc;
                        acc = st.Accumulator;
                        continue;
                    }
                    throw new JavaScriptException(acc, message);
                }
                case Bytecode.Return:
                {
                    // UpdateInterruptBudgetOnReturn: the weight is the current bytecode offset.
                    FeedbackCell cell = st.Function.RawFeedbackCell;
                    if ((cell.InterruptBudget -= pc + 1) < 0) InterpreterTiering.OnBudgetInterrupt(isolate, st.Function, withStackCheck: false);
                    if ((typeof(TS) != typeof(SingleScale)))
                    {
                        st.Done = true;
                        st.Accumulator = acc;
                    }
                    else if (frame.InlineCall)
                    {
                        // Return to a caller running in this loop (InterpreterInlineCalls).
                        st.Accumulator = InterpreterInlineCalls.Return(isolate, ref st, acc);
                        goto reload;
                    }
                    return acc;
                }
                case Bytecode.ThrowReferenceErrorIfTdzHole:
                    if (acc.IsTheHole)
                    {
                        RuntimeScopes.ThrowAccessedUninitializedVariable(isolate, st.Constants[Unsigned<TS>(ref code, pc + 1)]);
                    }
                    pc += 1 + S;
                    continue;
                case Bytecode.ThrowSuperNotCalledIfTdzHole:
                    if (acc.IsTheHole) isolate.ThrowReferenceError(MessageTemplate.SuperNotCalled);
                    pc += 1;
                    continue;
                case Bytecode.ThrowSuperAlreadyCalledIfNotTdzHole:
                    if (!acc.IsTheHole) isolate.ThrowReferenceError(MessageTemplate.SuperAlreadyCalled);
                    pc += 1;
                    continue;
                case Bytecode.ThrowIfNotSuperConstructor:
                {
                    JSValue constructor = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    if (!ObjectOps.IsConstructor(constructor)) RuntimeClasses.ThrowNotSuperConstructor(isolate, constructor, st.Function);
                    pc += 1 + S;
                    continue;
                }

                // ---- Generators ---------------------------------------------------------------------------------------------------------------
                case Bytecode.SwitchOnGeneratorState:
                {
                    JSValue maybeGenerator = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    if (maybeGenerator.IsUndefined)
                    {
                        pc += 1 + 3 * S;
                        continue;
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
                    pc += (int)st.Constants[tableStart + state]._num;
                    continue;
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
                    return acc;
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
                    continue;
                }

                // ---- Iterator protocol --------------------------------------------------------------------------------------------------------
                case Bytecode.GetIterator:
                {
                    JSValue receiver = Unsafe.Subtract(ref fpSlot, -InterpreterRuntime.kRegisterOperandBase + Signed<TS>(ref code, pc + 1));
                    int loadSlot = Unsigned<TS>(ref code, pc + 1 + S);
                    int callSlot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = InterpreterIterators.GetIterator(isolate, st.FeedbackVector, loadSlot, callSlot, receiver);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.ArrayDestructure:
                {
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1);
                    int count = Unsigned<TS>(ref code, pc + 1 + S);
                    InterpreterIterators.ArrayDestructure(isolate, acc, isolate.RegisterStack.AsSpan(st.Fp + first, count));
                    acc = default(JSValue);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Debugger, coverage, abort ---------------------------------------------------------------------------------------------------
                case Bytecode.Debugger:
                    // Runtime_HandleDebuggerStatement: no debugger is attached.
                    acc = default(JSValue);
                    pc += 1;
                    continue;
                case Bytecode.IncBlockCounter:
                    InterpreterOps.IncBlockCounter(isolate, st.Function, Unsigned<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.Abort:
                    RuntimeInternal.Abort(isolate, Byte(ref code, pc + 1));
                    pc += 2;
                    continue;

                default:
                    throw new InvalidOperationException(
                        $"V8Sharp: unexpected bytecode {(Bytecode)Unsafe.Add(ref code, pc)} at offset {pc}");
            }
        }
    }
}
