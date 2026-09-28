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
        int S = TS.Scale;
        JSValue[] stack = isolate.RegisterStack;
        ref JSValue stack0 = ref MemoryMarshal.GetArrayDataReference(stack);
        int fp = st.Fp;
        ref JSValue fpSlot = ref Unsafe.Add(ref stack0, fp);
        ref JSValue regBase = ref Unsafe.Add(ref stack0, fp + InterpreterRuntime.kRegisterOperandBase);
        JSFunction function = st.Function;
        BytecodeArray bytecodeArray = st.Bytecode;
        ref byte code = ref MemoryMarshal.GetArrayDataReference(bytecodeArray.Bytecodes);
        JSValue[] constants = st.Constants;
        FeedbackVector? fv = st.FeedbackVector;
        Context context = st.Context;
        JSValue acc = st.Accumulator;
        int pc = st.Pc;
        ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[st.FrameIndex];
        bool stepped = false;

        while (true)
        {
            if (TS.SingleStep)
            {
                if (stepped)
                {
                    st.Pc = pc;
                    st.Accumulator = acc;
                    st.Context = context;
                    st.FeedbackVector = fv;
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
                    if (TS.SingleStep) throw new InvalidOperationException("V8Sharp: nested operand scale prefix");
                    bool wide = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.Wide;
                    st.Pc = pc + 1;
                    st.Accumulator = acc;
                    st.Context = context;
                    st.FeedbackVector = fv;
                    if (wide) Loop<DoubleScale>(isolate, ref st);
                    else Loop<QuadrupleScale>(isolate, ref st);
                    if (st.Done)
                    {
                        st.Done = false;
                        return st.Accumulator;
                    }
                    pc = st.Pc;
                    acc = st.Accumulator;
                    context = st.Context;
                    fv = st.FeedbackVector;
                    continue;
                }

                // ---- Loading the accumulator -----------------------------------------
                case Bytecode.Ldar:
                    acc = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
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
                    acc = JSValue.Undefined;
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
                    acc = constants[Unsigned<TS>(ref code, pc + 1)];
                    pc += 1 + S;
                    continue;

                // Deviation: V8Sharp has no ContextCells (script/function context
                // cells are an optimizing-tier device that V8 --jitless disables),
                // so the cell and no-cell variants are the same load and store.
                case Bytecode.LdaContextSlotNoCell:
                case Bytecode.LdaContextSlot:
                case Bytecode.LdaImmutableContextSlot:
                {
                    Context c = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<Context>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slot];
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.LdaCurrentContextSlotNoCell:
                case Bytecode.LdaCurrentContextSlot:
                case Bytecode.LdaImmutableCurrentContextSlot:
                    acc = context.Slots[Unsigned<TS>(ref code, pc + 1)];
                    pc += 1 + S;
                    continue;

                // ---- Register loads ----------------------------------------------------
                case Bytecode.Star:
                    Reg(ref regBase, Signed<TS>(ref code, pc + 1)) = acc;
                    pc += 1 + S;
                    continue;
                case Bytecode.Mov:
                    Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S)) = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
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
                    Reg(ref regBase, Signed<TS>(ref code, pc + 1)) = context;
                    context = acc.As<Context>();
                    Unsafe.Add(ref fpSlot, InterpreterRuntime.kContextOffset) = context;
                    isolate.Context = context;
                    pc += 1 + S;
                    continue;
                }
                case Bytecode.PopContext:
                    context = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<Context>();
                    Unsafe.Add(ref fpSlot, InterpreterRuntime.kContextOffset) = context;
                    isolate.Context = context;
                    pc += 1 + S;
                    continue;

                // ---- Test operations -------------------------------------------------------
                case Bytecode.TestReferenceEqual:
                    acc = JSValue.FromBoolean(Reg(ref regBase, Signed<TS>(ref code, pc + 1)).IsIdenticalTo(acc));
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
                    var name = constants[Unsigned<TS>(ref code, pc + 1)].As<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    TypeofMode mode = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.LdaGlobal ? TypeofMode.NotInside : TypeofMode.Inside;
                    acc = LoadGlobalIC.Load(isolate, fv, slot, context, name, mode);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.StaGlobal:
                {
                    var name = constants[Unsigned<TS>(ref code, pc + 1)].As<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    StoreGlobalIC.Store(isolate, fv, slot, context, name, acc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Context stores ------------------------------------------------------------
                case Bytecode.StaContextSlotNoCell:
                case Bytecode.StaContextSlot:
                {
                    Context c = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<Context>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slot] = acc;
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.StaCurrentContextSlotNoCell:
                case Bytecode.StaCurrentContextSlot:
                    context.Slots[Unsigned<TS>(ref code, pc + 1)] = acc;
                    pc += 1 + S;
                    continue;

                // ---- Lookup slots ------------------------------------------------------------------
                case Bytecode.LdaLookupSlot:
                    acc = RuntimeScopes.LoadLookupSlot(isolate, context, constants[Unsigned<TS>(ref code, pc + 1)].As<JSString>(),
                        ShouldThrow.ThrowOnError);
                    pc += 1 + S;
                    continue;
                case Bytecode.LdaLookupSlotInsideTypeof:
                    acc = RuntimeScopes.LoadLookupSlot(isolate, context, constants[Unsigned<TS>(ref code, pc + 1)].As<JSString>(),
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
                    Context? slotContext = InterpreterOps.ContextWithoutExtensionsUpToDepth(context, depth);
                    if (slotContext is not null)
                    {
                        acc = slotContext.Slots[slot];
                    }
                    else
                    {
                        Bytecode b = (Bytecode)Unsafe.Add(ref code, pc);
                        bool insideTypeof = b is Bytecode.LdaLookupContextSlotNoCellInsideTypeof or Bytecode.LdaLookupContextSlotInsideTypeof;
                        acc = RuntimeScopes.LoadLookupSlot(isolate, context, constants[Unsigned<TS>(ref code, pc + 1)].As<JSString>(),
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
                    var name = constants[Unsigned<TS>(ref code, pc + 1)].As<JSString>();
                    if (InterpreterOps.ContextWithoutExtensionsUpToDepth(context, depth) is not null)
                    {
                        int slot = Unsigned<TS>(ref code, pc + 1 + S);
                        acc = LoadGlobalIC.Load(isolate, fv, slot, context, name, insideTypeof ? TypeofMode.Inside : TypeofMode.NotInside);
                    }
                    else
                    {
                        acc = RuntimeScopes.LoadLookupSlot(isolate, context, name,
                            insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);
                    }
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.StaLookupSlot:
                {
                    var name = constants[Unsigned<TS>(ref code, pc + 1)].As<JSString>();
                    byte flags = (byte)Byte(ref code, pc + 1 + S);
                    acc = RuntimeScopes.StoreLookupSlot(isolate, context, name, acc,
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
                    JSValue receiver = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    var name = constants[Unsigned<TS>(ref code, pc + 1 + S)].As<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = LoadIC.LoadNamed(isolate, fv, slot, receiver, name);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.GetNamedPropertyFromSuper:
                {
                    JSValue receiver = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    var name = constants[Unsigned<TS>(ref code, pc + 1 + S)].As<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = LoadIC.LoadSuper(isolate, fv, slot, receiver, acc, name);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.GetKeyedProperty:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = KeyedLoadIC.Load(isolate, fv, slot, obj, acc);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.GetEnumeratedKeyedProperty:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue enumIndex = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheType = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + 2 * S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    acc = KeyedLoadIC.LoadEnumerated(isolate, fv, slot, obj, acc, enumIndex, cacheType);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.GetPrivateField:
                {
                    Context c = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<Context>();
                    int slotIndex = Unsigned<TS>(ref code, pc + 1 + S);
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    JSValue symbol = InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slotIndex];
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + 3 * S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
                    acc = KeyedLoadIC.Load(isolate, fv, slot, obj, symbol);
                    pc += 1 + 5 * S;
                    continue;
                }

                // ---- Module variables -------------------------------------------------------------------
                case Bytecode.LdaModuleVariable:
                {
                    int cellIndex = Signed<TS>(ref code, pc + 1);
                    int depth = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = InterpreterOps.LoadModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(context, depth), cellIndex);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.StaModuleVariable:
                {
                    int cellIndex = Signed<TS>(ref code, pc + 1);
                    int depth = Unsigned<TS>(ref code, pc + 1 + S);
                    InterpreterOps.StoreModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(context, depth), cellIndex, acc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Property stores ------------------------------------------------------------------------
                case Bytecode.SetNamedProperty:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    var name = constants[Unsigned<TS>(ref code, pc + 1 + S)].As<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    StoreIC.StoreNamed(isolate, fv, slot, obj, name, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.DefineNamedOwnProperty:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    var name = constants[Unsigned<TS>(ref code, pc + 1 + S)].As<Name>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    StoreIC.DefineNamedOwn(isolate, fv, slot, obj, name, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.SetKeyedProperty:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue key = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    KeyedStoreIC.Store(isolate, fv, slot, obj, key, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.DefineKeyedOwnProperty:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue key = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 2 + 2 * S);
                    KeyedStoreIC.DefineKeyedOwn(isolate, fv, slot, obj, key, acc, (DefineKeyedOwnPropertyFlags)flags);
                    pc += 2 + 3 * S;
                    continue;
                }
                case Bytecode.StaInArrayLiteral:
                {
                    JSValue array = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue index = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    KeyedStoreIC.StoreInArrayLiteral(isolate, fv, slot, array, index, acc);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.DefineKeyedOwnPropertyInLiteral:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue name = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 2 + 2 * S);
                    RuntimeObject.DefineKeyedOwnPropertyInLiteral(isolate, obj, name, acc,
                        (DefineKeyedOwnPropertyInLiteralFlags)flags, fv, slot);
                    pc += 2 + 3 * S;
                    continue;
                }
                case Bytecode.SetPrototypeProperties:
                {
                    JSValue boilerplate = constants[Unsigned<TS>(ref code, pc + 1)];
                    int startSlot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = RuntimeLiterals.SetPrototypeProperties(isolate, context, acc, boilerplate.As<ObjectBoilerplateDescription>(),
                        JSFunctionFeedback.GetClosureFeedbackCellArray(function), startSlot);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.SetPrivateField:
                {
                    Context c = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<Context>();
                    int slotIndex = Unsigned<TS>(ref code, pc + 1 + S);
                    int depth = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    JSValue symbol = InterpreterRuntime.GetContextAtDepth(c, depth).Slots[slotIndex];
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + 3 * S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
                    KeyedStoreIC.Store(isolate, fv, slot, obj, symbol, acc);
                    pc += 1 + 5 * S;
                    continue;
                }

                // ---- Binary operators ----------------------------------------------------------------------
                case Bytecode.Add:
                {
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    if (lhs.IsNumber && acc.IsNumber)
                    {
                        acc = InterpreterOps.AddNumbers(isolate, lhs.Number, acc.Number, ref feedback);
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
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    if (lhs.IsNumber && acc.IsNumber)
                    {
                        acc = InterpreterOps.SubtractNumbers(lhs.Number, acc.Number, ref feedback);
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
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    if (lhs.IsNumber && acc.IsNumber)
                    {
                        acc = InterpreterOps.MultiplyNumbers(lhs.Number, acc.Number, ref feedback);
                    }
                    else
                    {
                        acc = InterpreterOps.BinarySlow(isolate, Operation.Multiply, lhs, acc, ref feedback);
                    }
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.Div:
                    acc = InterpreterOps.Binary(isolate, Operation.Divide, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.Mod:
                    acc = InterpreterOps.Binary(isolate, Operation.Modulus, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.Exp:
                    acc = InterpreterOps.Binary(isolate, Operation.Exponentiate, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseOr:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseOr, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseXor:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseXor, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.BitwiseAnd:
                    acc = InterpreterOps.Bitwise(isolate, Operation.BitwiseAnd, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftLeft:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftLeft, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftRight:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftRight, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.ShiftRightLogical:
                    acc = InterpreterOps.Bitwise(isolate, Operation.ShiftRightLogical, Reg(ref regBase, Signed<TS>(ref code, pc + 1)),
                        acc, ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.Add_StringConstant_Internalize:
                {
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int variant = Byte(ref code, pc + 1 + 2 * S);
                    acc = InterpreterOps.AddStringConstantAndInternalize(isolate, fv, slot, lhs, acc,
                        (AddStringConstantAndInternalizeVariant)variant);
                    pc += 2 + 2 * S;
                    continue;
                }

                // ---- Binary operators with an immediate ---------------------------------------------------------
                case Bytecode.AddSmi:
                {
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    int imm = Signed<TS>(ref code, pc + 1);
                    if (acc.IsNumber)
                    {
                        acc = InterpreterOps.AddNumbers(isolate, acc.Number, imm, ref feedback);
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
                    if (acc.IsNumber)
                    {
                        acc = InterpreterOps.SubtractNumbers(acc.Number, imm, ref feedback);
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
                    acc = InterpreterOps.TypeOf(isolate, acc, fv, Unsigned<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.DeletePropertyStrict:
                    acc = RuntimeObject.DeleteProperty(isolate, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        V8Sharp.Common.LanguageMode.Strict);
                    pc += 1 + S;
                    continue;
                case Bytecode.DeletePropertySloppy:
                    acc = RuntimeObject.DeleteProperty(isolate, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        V8Sharp.Common.LanguageMode.Sloppy);
                    pc += 1 + S;
                    continue;
                case Bytecode.GetSuperConstructor:
                    Reg(ref regBase, Signed<TS>(ref code, pc + 1)) = InterpreterOps.GetSuperConstructor(isolate, acc.As<JSFunction>());
                    pc += 1 + S;
                    continue;
                case Bytecode.FindNonDefaultConstructorOrConstruct:
                {
                    var thisFunction = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<JSFunction>();
                    JSValue newTarget = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int output = Signed<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterOps.FindNonDefaultConstructorOrConstruct(isolate, thisFunction, newTarget,
                        out JSValue first, out JSValue second);
                    Reg(ref regBase, output) = first;
                    Reg(ref regBase, output - 1) = second;
                    pc += 1 + 3 * S;
                    continue;
                }

                // ---- Calls ------------------------------------------------------------------------------------------
                case Bytecode.CallAnyReceiver:
                case Bytecode.CallProperty:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    JSValue receiver = Unsafe.Add(ref fpSlot, first);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
                    acc = InterpreterCalls.Call(isolate, callee, receiver, fp + first + 1, count - 1,
                        (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.CallProperty
                            ? ConvertReceiverMode.NotNullOrUndefined
                            : ConvertReceiverMode.Any);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallProperty0:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue receiver = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
                    acc = InterpreterCalls.Call(isolate, callee, receiver, 0, 0, ConvertReceiverMode.NotNullOrUndefined);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.CallProperty1:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int receiverOperand = Signed<TS>(ref code, pc + 1 + S);
                    JSValue receiver = Reg(ref regBase, receiverOperand);
                    int argOperand = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
                    acc = InterpreterCalls.Call(isolate, callee, receiver, fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1,
                        ConvertReceiverMode.NotNullOrUndefined);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallProperty2:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue receiver = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int arg0 = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int arg1 = Signed<TS>(ref code, pc + 1 + 3 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
                    acc = InterpreterCalls.Call2(isolate, callee, receiver, Reg(ref regBase, arg0), Reg(ref regBase, arg1),
                        fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
                        ConvertReceiverMode.NotNullOrUndefined);
                    pc += 1 + 5 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
                    acc = InterpreterCalls.Call(isolate, callee, JSValue.Undefined, fp + first, count,
                        ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver0:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
                    acc = InterpreterCalls.Call(isolate, callee, JSValue.Undefined, 0, 0, ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver1:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int argOperand = Signed<TS>(ref code, pc + 1 + S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
                    acc = InterpreterCalls.Call(isolate, callee, JSValue.Undefined, fp + InterpreterRuntime.kRegisterOperandBase - argOperand,
                        1, ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.CallUndefinedReceiver2:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int arg0 = Signed<TS>(ref code, pc + 1 + S);
                    int arg1 = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
                    acc = InterpreterCalls.Call2(isolate, callee, JSValue.Undefined, Reg(ref regBase, arg0), Reg(ref regBase, arg1),
                        fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
                        ConvertReceiverMode.NullOrUndefined);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallWithSpread:
                {
                    JSValue callee = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
                    acc = InterpreterCalls.CallWithSpread(isolate, callee, stack.AsSpan(fp + first, count));
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.CallRuntime:
                {
                    int id = Short(ref code, pc + 1);
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 3);
                    int count = Unsigned<TS>(ref code, pc + 3 + S);
                    acc = RuntimeTable.Call(isolate, (FunctionId)id, stack.AsSpan(fp + first, count));
                    pc += 3 + 2 * S;
                    continue;
                }
                case Bytecode.CallRuntimeForPair:
                {
                    int id = Short(ref code, pc + 1);
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 3);
                    int count = Unsigned<TS>(ref code, pc + 3 + S);
                    int output = Signed<TS>(ref code, pc + 3 + 2 * S);
                    JSValue result0 = RuntimeTable.CallForPair(isolate, (FunctionId)id, stack.AsSpan(fp + first, count),
                        out JSValue result1);
                    Reg(ref regBase, output) = result0;
                    Reg(ref regBase, output - 1) = result1;
                    acc = result0;
                    pc += 3 + 3 * S;
                    continue;
                }
                case Bytecode.CallJSRuntime:
                {
                    int index = Byte(ref code, pc + 1);
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 2);
                    int count = Unsigned<TS>(ref code, pc + 2 + S);
                    JSValue target = context.NativeContext.Slots[index];
                    acc = InterpreterCalls.Call(isolate, target, JSValue.Undefined, fp + first, count,
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
                        stack.AsSpan(fp + first, count));
                    pc += 2 + 2 * S;
                    continue;
                }

                // ---- Construct ----------------------------------------------------------------------------------------
                case Bytecode.Construct:
                {
                    JSValue constructor = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    acc = InterpreterCalls.Construct(isolate, fv, slot, constructor, acc, fp + first, count);
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.ConstructWithSpread:
                {
                    JSValue constructor = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    acc = InterpreterCalls.ConstructWithSpread(isolate, fv, slot, constructor, acc, stack.AsSpan(fp + first, count));
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.ConstructForwardAllArgs:
                {
                    JSValue constructor = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = InterpreterCalls.ConstructForwardAllArgs(isolate, fv, slot, constructor, acc, fp, st.Argc);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Compare operations -------------------------------------------------------------------------------
                case Bytecode.TestEqual:
                    acc = InterpreterOps.Equal(isolate, Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.TestEqualStrict:
                    acc = InterpreterOps.StrictEqual(Reg(ref regBase, Signed<TS>(ref code, pc + 1)), acc,
                        ref Unsafe.Add(ref code, pc + 1 + S));
                    pc += 2 + S;
                    continue;
                case Bytecode.TestLessThan:
                {
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs.IsNumber && acc.IsNumber
                        ? InterpreterOps.CompareNumbers(Operation.LessThan, lhs.Number, acc.Number, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.LessThan, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestGreaterThan:
                {
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs.IsNumber && acc.IsNumber
                        ? InterpreterOps.CompareNumbers(Operation.GreaterThan, lhs.Number, acc.Number, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.GreaterThan, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestLessThanOrEqual:
                {
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs.IsNumber && acc.IsNumber
                        ? InterpreterOps.CompareNumbers(Operation.LessThanOrEqual, lhs.Number, acc.Number, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.LessThanOrEqual, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestGreaterThanOrEqual:
                {
                    JSValue lhs = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
                    acc = lhs.IsNumber && acc.IsNumber
                        ? InterpreterOps.CompareNumbers(Operation.GreaterThanOrEqual, lhs.Number, acc.Number, ref feedback)
                        : InterpreterOps.Relational(isolate, Operation.GreaterThanOrEqual, lhs, acc, ref feedback);
                    pc += 2 + S;
                    continue;
                }
                case Bytecode.TestInstanceOf:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = InterpreterOps.InstanceOf(isolate, fv, slot, obj, acc);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.TestIn:
                {
                    JSValue name = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = KeyedHasIC.Has(isolate, fv, slot, acc, name);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Cast operators ---------------------------------------------------------------------------------------
                case Bytecode.ToName:
                    acc = ObjectOps.ToName(isolate, acc);
                    pc += 1;
                    continue;
                case Bytecode.ToNumber:
                    acc = InterpreterOps.ToNumberOrNumeric(isolate, acc, fv, Unsigned<TS>(ref code, pc + 1), numeric: false);
                    pc += 1 + S;
                    continue;
                case Bytecode.ToNumeric:
                    acc = InterpreterOps.ToNumberOrNumeric(isolate, acc, fv, Unsigned<TS>(ref code, pc + 1), numeric: true);
                    pc += 1 + S;
                    continue;
                case Bytecode.ToObject:
                    Reg(ref regBase, Signed<TS>(ref code, pc + 1)) = acc.IsJSReceiver ? acc : ObjectOps.ToObject(isolate, acc);
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
                    JSValue pattern = constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int flags = Short(ref code, pc + 1 + 2 * S);
                    acc = RuntimeLiterals.CreateRegExpLiteral(isolate, fv, slot, pattern.As<JSString>(), flags);
                    pc += 3 + 2 * S;
                    continue;
                }
                case Bytecode.CreateArrayLiteral:
                {
                    JSValue description = constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    acc = RuntimeLiterals.CreateArrayLiteral(isolate, fv, slot, description.As<ArrayBoilerplateDescription>(),
                        CreateArrayLiteralFlags.DecodeFlags((byte)flags));
                    pc += 2 + 2 * S;
                    continue;
                }
                case Bytecode.CreateArrayFromIterable:
                    acc = InterpreterIterators.IterableToListWithSymbolLookup(isolate, acc);
                    pc += 1;
                    continue;
                case Bytecode.CreateEmptyArrayLiteral:
                    acc = RuntimeLiterals.CreateEmptyArrayLiteral(isolate, fv, Unsigned<TS>(ref code, pc + 1));
                    pc += 1 + S;
                    continue;
                case Bytecode.CreateObjectLiteral:
                {
                    JSValue description = constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    int flags = Byte(ref code, pc + 1 + 2 * S);
                    acc = RuntimeLiterals.CreateObjectLiteral(isolate, fv, slot, description.As<ObjectBoilerplateDescription>(),
                        CreateObjectLiteralFlags.DecodeFlags((byte)flags));
                    pc += 2 + 2 * S;
                    continue;
                }
                case Bytecode.CreateEmptyObjectLiteral:
                    acc = RuntimeLiterals.CreateEmptyObjectLiteral(isolate, context.NativeContext);
                    pc += 1;
                    continue;
                case Bytecode.CloneObject:
                {
                    JSValue source = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int flags = Byte(ref code, pc + 1 + S);
                    int slot = Unsigned<TS>(ref code, pc + 2 + S);
                    acc = CloneObjectIC.Clone(isolate, fv, slot, source, CreateObjectLiteralFlags.DecodeFlags((byte)flags));
                    pc += 2 + 2 * S;
                    continue;
                }
                case Bytecode.GetTemplateObject:
                {
                    JSValue description = constants[Unsigned<TS>(ref code, pc + 1)];
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    acc = RuntimeLiterals.GetTemplateObject(isolate, function.Shared, description.As<TemplateObjectDescription>(), fv, slot);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CreateClosure:
                {
                    var shared = constants[Unsigned<TS>(ref code, pc + 1)].As<SharedFunctionInfo>();
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    FeedbackCell cell = JSFunctionFeedback.GetClosureFeedbackCellArray(function).Get(slot);
                    acc = RuntimeClosures.NewClosure(isolate, shared, context, cell);
                    pc += 2 + 2 * S;
                    continue;
                }

                // ---- Context allocation ----------------------------------------------------------------------------------------
                case Bytecode.CreateBlockContext:
                    acc = isolate.Factory.NewBlockContext(context, constants[Unsigned<TS>(ref code, pc + 1)].As<ScopeInfo>());
                    pc += 1 + S;
                    continue;
                case Bytecode.CreateCatchContext:
                {
                    JSValue exception = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    var scopeInfo = constants[Unsigned<TS>(ref code, pc + 1 + S)].As<ScopeInfo>();
                    acc = isolate.Factory.NewCatchContext(context, scopeInfo, exception);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CreateFunctionContext:
                case Bytecode.CreateFunctionContextWithCells:
                case Bytecode.CreateEvalContext:
                {
                    var scopeInfo = constants[Unsigned<TS>(ref code, pc + 1)].As<ScopeInfo>();
                    acc = RuntimeScopes.NewFunctionContext(isolate, context, scopeInfo,
                        (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.CreateEvalContext);
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.CreateWithContext:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    var scopeInfo = constants[Unsigned<TS>(ref code, pc + 1 + S)].As<ScopeInfo>();
                    acc = RuntimeScopes.PushWithContext(isolate, context, obj, scopeInfo);
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Arguments allocation --------------------------------------------------------------------------------------
                case Bytecode.CreateMappedArguments:
                    acc = InterpreterArguments.NewSloppyArguments(isolate, function, context, fp, st.Argc);
                    pc += 1;
                    continue;
                case Bytecode.CreateUnmappedArguments:
                    acc = InterpreterArguments.NewStrictArguments(isolate, function, fp, st.Argc);
                    pc += 1;
                    continue;
                case Bytecode.CreateRestParameter:
                    acc = InterpreterArguments.NewRestParameter(isolate, function, fp, st.Argc);
                    pc += 1;
                    continue;

                // ---- Control flow --------------------------------------------------------------------------------------------------
                case Bytecode.JumpLoop:
                {
                    int relative = Unsigned<TS>(ref code, pc + 1);
                    acc = JSValue.Undefined;
                    // The budget interrupt (V8: UpdateInterruptBudget on the backward
                    // jump, with the stack/interrupt check folded in).
                    FeedbackCell cell = function.RawFeedbackCell;
                    if ((cell.InterruptBudget -= relative) < 0 || isolate.StackGuard.HasPendingInterrupts)
                    {
                        fv = InterpreterTiering.OnBudgetInterrupt(isolate, function, withStackCheck: true);
                        st.FeedbackVector = fv;
                        Unsafe.Add(ref fpSlot, InterpreterRuntime.kFeedbackVectorOffset) = fv is null ? JSValue.Undefined : fv;
                    }
                    pc -= relative;
                    // OSR to baseline code when the SharedFunctionInfo has some and the
                    // closure has a feedback vector (InterpreterAssembler::OnStackReplacement,
                    // case 3): Run continues the frame in it at the loop header.
                    if (function.Shared.BaselineCode is not null && fv is not null)
                    {
                        st.Pc = pc;
                        st.Accumulator = acc;
                        st.Context = context;
                        st.FeedbackVector = fv;
                        st.OsrToBaseline = true;
                        if (TS.SingleStep) st.Done = true;
                        return acc;
                    }
                    continue;
                }
                case Bytecode.Jump:
                    pc += Unsigned<TS>(ref code, pc + 1);
                    continue;
                case Bytecode.JumpConstant:
                    pc += (int)constants[Unsigned<TS>(ref code, pc + 1)].Number;
                    continue;
                case Bytecode.JumpIfNullConstant:
                    pc += acc.IsNull ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfNotNullConstant:
                    pc += !acc.IsNull ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefinedConstant:
                    pc += acc.IsUndefined ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfNotUndefinedConstant:
                    pc += !acc.IsUndefined ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfUndefinedOrNullConstant:
                    pc += acc.IsNullOrUndefined ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfTrueConstant:
                    pc += acc.IsTrue ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfFalseConstant:
                    pc += acc.IsFalse ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfJSReceiverConstant:
                    pc += acc.IsJSReceiver ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfForInDoneConstant:
                {
                    JSValue index = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheLength = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + 2 * S));
                    pc += index.IsIdenticalTo(cacheLength) ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + 3 * S;
                    continue;
                }
                case Bytecode.JumpIfToBooleanTrueConstant:
                    pc += InterpreterOps.ToBoolean(acc) ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
                    continue;
                case Bytecode.JumpIfToBooleanFalseConstant:
                    pc += !InterpreterOps.ToBoolean(acc) ? (int)constants[Unsigned<TS>(ref code, pc + 1)].Number : 1 + S;
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
                    JSValue index = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    JSValue cacheLength = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + 2 * S));
                    pc += index.IsIdenticalTo(cacheLength) ? Unsigned<TS>(ref code, pc + 1) : 1 + 3 * S;
                    continue;
                }
                case Bytecode.SwitchOnSmiNoFeedback:
                {
                    int tableStart = Unsigned<TS>(ref code, pc + 1);
                    int tableLength = Unsigned<TS>(ref code, pc + 1 + S);
                    int caseValueBase = Signed<TS>(ref code, pc + 1 + 2 * S);
                    int caseValue = (int)acc.Number - caseValueBase;
                    if (caseValue >= 0 && caseValue < tableLength)
                    {
                        pc += (int)constants[tableStart + caseValue].Number;
                    }
                    else
                    {
                        pc += 1 + 3 * S;
                    }
                    continue;
                }

                // ---- for-in / for-of ------------------------------------------------------------------------------------------------------
                case Bytecode.ForInEnumerate:
                    acc = RuntimeForIn.ForInEnumerate(isolate, Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<JSReceiver>());
                    pc += 1 + S;
                    continue;
                case Bytecode.ForInPrepare:
                {
                    int output = Signed<TS>(ref code, pc + 1);
                    int slot = Unsigned<TS>(ref code, pc + 1 + S);
                    RuntimeForIn.ForInPrepare(isolate, acc.Object, fv, slot, out JSValue cacheArray, out int cacheLength);
                    Reg(ref regBase, output) = acc;
                    Reg(ref regBase, output - 1) = cacheArray;
                    Reg(ref regBase, output - 2) = JSValue.FromInt(cacheLength);
                    acc = JSValue.Zero;
                    pc += 1 + 2 * S;
                    continue;
                }
                case Bytecode.ForInNext:
                {
                    JSValue receiver = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int index = (int)Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S)).Number;
                    int pairOperand = Signed<TS>(ref code, pc + 1 + 2 * S);
                    JSValue cacheType = Reg(ref regBase, pairOperand);
                    JSValue cacheArray = Reg(ref regBase, pairOperand - 1);
                    int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    JSValue key = cacheArray.As<FixedArray>()[index];
                    if (receiver.HeapObjectOrNull is JSReceiver r && ReferenceEquals(r.Map, cacheType.HeapObjectOrNull))
                    {
                        // Enum cache in use for {receiver}, the {key} is definitely valid.
                        acc = key;
                    }
                    else
                    {
                        acc = RuntimeForIn.ForInNextSlow(isolate, fv, slot, receiver, key, cacheType);
                    }
                    pc += 1 + 4 * S;
                    continue;
                }
                case Bytecode.ForInStep:
                {
                    ref JSValue index = ref Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    index = JSValue.FromNumber(index.Number + 1);
                    pc += 1 + S;
                    continue;
                }
                case Bytecode.ForOfNext:
                {
                    JSValue obj = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    JSValue next = Reg(ref regBase, Signed<TS>(ref code, pc + 1 + S));
                    int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = InterpreterIterators.ForOfNext(isolate, fv, slot, obj, next);
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
                    st.FeedbackVector = fv;
                    if (TryDispatchToHandler(isolate, ref st, acc, message))
                    {
                        pc = st.Pc;
                        acc = st.Accumulator;
                        context = st.Context;
                        continue;
                    }
                    throw new JavaScriptException(acc, message);
                }
                case Bytecode.ReThrow:
                {
                    // Runtime_ReThrow: rethrows with the pending message.
                    JSMessageObject? message = isolate.PendingMessage.HeapObjectOrNull as JSMessageObject;
                    st.FeedbackVector = fv;
                    if (TryDispatchToHandler(isolate, ref st, acc, message))
                    {
                        pc = st.Pc;
                        acc = st.Accumulator;
                        context = st.Context;
                        continue;
                    }
                    throw new JavaScriptException(acc, message);
                }
                case Bytecode.Return:
                {
                    // UpdateInterruptBudgetOnReturn: the weight is the current bytecode offset.
                    FeedbackCell cell = function.RawFeedbackCell;
                    if ((cell.InterruptBudget -= pc + 1) < 0) InterpreterTiering.OnBudgetInterrupt(isolate, function, withStackCheck: false);
                    if (TS.SingleStep)
                    {
                        st.Done = true;
                        st.Accumulator = acc;
                    }
                    return acc;
                }
                case Bytecode.ThrowReferenceErrorIfTdzHole:
                    if (acc.IsTheHole)
                    {
                        RuntimeScopes.ThrowAccessedUninitializedVariable(isolate, constants[Unsigned<TS>(ref code, pc + 1)]);
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
                    JSValue constructor = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    if (!ObjectOps.IsConstructor(constructor)) RuntimeClasses.ThrowNotSuperConstructor(isolate, constructor, function);
                    pc += 1 + S;
                    continue;
                }

                // ---- Generators ---------------------------------------------------------------------------------------------------------------
                case Bytecode.SwitchOnGeneratorState:
                {
                    JSValue maybeGenerator = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    if (maybeGenerator.IsUndefined)
                    {
                        pc += 1 + 3 * S;
                        continue;
                    }
                    var generator = maybeGenerator.As<JSGeneratorObject>();
                    int state = generator.ContinuationValue;
                    generator.ContinuationValue = JSGeneratorObject.kGeneratorExecuting;
                    context = generator.Context;
                    Unsafe.Add(ref fpSlot, InterpreterRuntime.kContextOffset) = context;
                    isolate.Context = context;
                    int tableStart = Unsigned<TS>(ref code, pc + 1 + S);
                    int tableLength = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    if ((uint)state >= (uint)tableLength) throw new InvalidOperationException("V8Sharp: bad generator state");
                    pc += (int)constants[tableStart + state].Number;
                    continue;
                }
                case Bytecode.SuspendGenerator:
                {
                    var generator = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<JSGeneratorObject>();
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    int suspendId = Unsigned<TS>(ref code, pc + 1 + 3 * S);
                    InterpreterGenerators.ExportParametersAndRegisterFile(isolate, generator, fp, first, count);
                    generator.Context = context;
                    generator.ContinuationValue = suspendId;
                    // Store the bytecode offset in the [input_or_debug_pos] field, to be used by
                    // the inspector.
                    generator.InputOrDebugPos = JSValue.FromInt(pc);
                    if (TS.SingleStep)
                    {
                        st.Done = true;
                        st.Accumulator = acc;
                    }
                    return acc;
                }
                case Bytecode.ResumeGenerator:
                {
                    var generator = Reg(ref regBase, Signed<TS>(ref code, pc + 1)).As<JSGeneratorObject>();
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
                    int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    InterpreterGenerators.ImportRegisterFile(isolate, generator, fp, first, count);
                    // Return the generator's input_or_debug_pos in the accumulator.
                    acc = generator.InputOrDebugPos;
                    pc += 1 + 3 * S;
                    continue;
                }

                // ---- Iterator protocol --------------------------------------------------------------------------------------------------------
                case Bytecode.GetIterator:
                {
                    JSValue receiver = Reg(ref regBase, Signed<TS>(ref code, pc + 1));
                    int loadSlot = Unsigned<TS>(ref code, pc + 1 + S);
                    int callSlot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
                    acc = InterpreterIterators.GetIterator(isolate, fv, loadSlot, callSlot, receiver);
                    pc += 1 + 3 * S;
                    continue;
                }
                case Bytecode.ArrayDestructure:
                {
                    int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1);
                    int count = Unsigned<TS>(ref code, pc + 1 + S);
                    InterpreterIterators.ArrayDestructure(isolate, acc, stack.AsSpan(fp + first, count));
                    acc = JSValue.Undefined;
                    pc += 1 + 2 * S;
                    continue;
                }

                // ---- Debugger, coverage, abort ---------------------------------------------------------------------------------------------------
                case Bytecode.Debugger:
                    // Runtime_HandleDebuggerStatement: no debugger is attached.
                    acc = JSValue.Undefined;
                    pc += 1;
                    continue;
                case Bytecode.IncBlockCounter:
                    InterpreterOps.IncBlockCounter(isolate, function, Unsigned<TS>(ref code, pc + 1));
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
