// The run-time half of the native Maglev backend (MaglevNativeCodeGenerator):
// the code's entry (a MaglevCodeEntry, as the IL backend's methods), its exits
// and the reconstruction of the values native code holds.
//
// Native code runs the way Ryu runs guest code: it never calls managed code.
// Every exit returns its number to MaglevNativeCode.Run, which
//   - returns the result (a Return),
//   - deoptimizes: the translation's values go to the isolate's deopt scratch
//     buffer and V8Sharp's Deoptimizer materializes the interpreter frame, as
//     for IL code (V8: the deoptimizer reads the optimized frame's slots), or
//   - handles interrupts (HandleNoHeapWritesInterrupt) or a GC poll and
//     re-enters the code after the exit (V8: a runtime call from deferred code).
//
// Values: native code keeps every value in a home slot of the context (a
// pinned long[]) or in a register (written to its home at exits). Untagged
// values are their bits. A tagged value is three words: the object reference
// as an address, the number payload, and for the values that need one a
// kind (undefined, number, an oddball, or "other"). Addresses are never
// turned back into references: the managed side rebuilds each tagged value
// from where it came from (a frame slot, a constant, a number's bits, a load
// it repeats), so a GC between the exit and the reconstruction cannot leave
// a stale reference. Resumable exits hand the rebuilt values back to the
// code through a managed array (the spill array), whose elements the code
// reads again as addresses on re-entry.
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        /// <summary>The native Maglev backend's context (MaglevNativeCode); grown on demand.</summary>
        internal long[]? MaglevNativeContext;

        /// <summary>The pseudo-frame of native Maglev code's direct entry (MaglevNativeCode.FastCall*).</summary>
        internal JSValue[]? MaglevNativeFrame;
    }
}

namespace V8Sharp.Maglev
{
    /// <summary>The kind word of a tagged value in a native context (MaglevNativeCodeGenerator.EmitClassify).</summary>
    internal enum NativeTaggedKind
    {
        kUndefined = 0,
        kNumber = 1,
        kTrue = 2,
        kFalse = 3,
        kNull = 4,
        kTheHole = 5,
        kOther = 6,
    }

    /// <summary>How the managed side rebuilds a value of native code.</summary>
    internal enum NativeValueKind : byte
    {
        kConstant,
        kInt32,
        kUint32,
        kFloat64,
        kHoleyFloat64,
        /// <summary>An InitialValue: the frame slot (native code never writes the frame).</summary>
        kFrameSlot,
        /// <summary>A tagged number whose double is the home's second word.</summary>
        kTaggedNumber,
        /// <summary>A tagged holey double (undefined for the hole) in the home's second word.</summary>
        kTaggedHoleyNumber,
        /// <summary>A tagged boolean: the kind word (kTrue/kFalse).</summary>
        kTaggedBoolean,
        /// <summary>A tagged value of any kind: the kind word, and Other for kOther.</summary>
        kTaggedDynamic,
        /// <summary>LoadElements of Input0, done again.</summary>
        kElements,
        /// <summary>LoadFixedArrayElement of Input0 at Input1, done again.</summary>
        kFixedArrayElement,
        /// <summary>The value of Input0.</summary>
        kIdentity,
        /// <summary>LoadTaggedField of Input0 at storage index FrameIndex, done again.</summary>
        kTaggedField,
        /// <summary>LoadContextSlot of Input0: slot FrameIndex, Input1 levels up, done again.</summary>
        kContextSlot,
        /// <summary>LoadMap of Input0, done again.</summary>
        kMap,
    }

    internal sealed class NativeValueInfo
    {
        public NativeValueKind Kind;
        public int Home = -1;
        public int Input0 = -1;
        public int Input1 = -1;
        /// <summary>kTaggedDynamic: the value a kOther kind is (time-invariant), or -1.</summary>
        public int Other = -1;
        public int FrameIndex;
        public JSValue Constant;
        public int NodeId;
    }

    internal enum NativeExitKind : byte
    {
        kReturn,
        kDeopt,
        /// <summary>CheckMapsWithMigrationAndDeopt's failure: migrate the object, then deoptimize.</summary>
        kDeoptAfterMigration,
        /// <summary>HandleNoHeapWritesInterrupt with interrupts pending.</summary>
        kInterrupt,
        /// <summary>The poll budget ran out (a GC safepoint): re-enter at once.</summary>
        kPoll,
        /// <summary>An access the graph's checks should have excluded (V8: a crash; IL: an exception).</summary>
        kAbort,
        /// <summary>Deopt without frame state (unreachable code).</summary>
        kUnreachable,
    }

    internal sealed class NativeExit
    {
        public NativeExitKind Kind;
        /// <summary>kReturn: the value returned. kDeoptAfterMigration: the object to migrate.</summary>
        public int Value = -1;
        /// <summary>kDeopt*: the index of the DeoptPoint; kInterrupt: the deopt for undeferrable interrupts.</summary>
        public int DeoptIndex = -1;
        /// <summary>kDeopt*: the reason (and the speculation feedback in the high bits), as the IL exits pass it.</summary>
        public int Reason;
        /// <summary>kInterrupt, kPoll: the entry number that continues after the exit.</summary>
        public int ResumeId;
        /// <summary>kInterrupt, kPoll: the live tagged values, in spill array order.</summary>
        public int[] TaggedLive = [];
        /// <summary>kInterrupt: the node's flag (1: a loop without calls, which deopts for undeferrable interrupts).</summary>
        public int InterruptKind;
        public int BytecodeOffset = -1;
        public string? Message;
    }

    /// <summary>A function's native code (MaglevCode.Native).</summary>
    internal sealed class MaglevNativeCode
    {
        /// <summary>The context words a code may use beyond its homes (see MaglevNativeCodeGenerator).</summary>
        internal const int kPollBudget = 1 << 20;

        public MaglevCode Code = null!;
        public nint Entry;
        public int CodeSize;
        public int ContextWords;
        public JSValue[] Constants = [];
        public NativeValueInfo[] Values = [];
        public NativeExit[] Exits = [];
        /// <summary>For each DeoptPoint: the value of each of its scratch slots (-1: none), as (slot, value) pairs.</summary>
        public (int Slot, int Value)[][] DeoptValues = [];
        /// <summary>The first and last home words of the code (saved around interrupt handling).</summary>
        public int FirstHome, HomeEnd;
        /// <summary>Some value is rebuilt by repeating a load (deopt after an interrupt that may run code).</summary>
        public bool RebuildsByLoad;

        static readonly JSValue[] s_noSpill = new JSValue[1];

        static long[] NativeContextOf(Isolate isolate, int words)
        {
            long[]? ctx = isolate.MaglevNativeContext;
            if (ctx is null || ctx.Length < words)
            {
                ctx = NativeCode.NewContext(Math.Max(words, Math.Max(1024, (ctx?.Length ?? 0) * 2)));
                isolate.MaglevNativeContext = ctx;
            }
            return ctx;
        }

        /// <summary>The MaglevCodeEntry of native code.</summary>
        public JSValue Run(Isolate isolate, ref InterpreterState state)
        {
            long[] ctx = NativeContextOf(isolate, ContextWords);
            int fp = state.Fp;
            ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = 0;
            ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            JSValue[] spill = s_noSpill;
            while (true)
            {
                long id = NativeCode.Call(Entry, ctx, ref isolate.RegisterStack[fp], Constants, spill, ref isolate.StackGuard.InterruptFlagsRef);
                NativeExit exit = Exits[id];
                switch (exit.Kind)
                {
                    case NativeExitKind.kReturn:
                        return Value(exit.Value, isolate.RegisterStack, ctx, fp);
                    case NativeExitKind.kDeopt:
                        return Deoptimize(isolate, ref state, ctx, exit);
                    case NativeExitKind.kDeoptAfterMigration:
                        MaglevBuiltins.TryMigrateInstanceAndMarkMapAsMigrationTarget(isolate, Value(exit.Value, isolate.RegisterStack, ctx, fp));
                        return Deoptimize(isolate, ref state, ctx, exit);
                    case NativeExitKind.kAbort:
                        throw new IndexOutOfRangeException("V8Sharp: " + exit.Message);
                    case NativeExitKind.kUnreachable:
                        MaglevBuiltins.Unreachable();
                        return JSValue.Undefined;
                }
                // A resumable exit: the live tagged values, rebuilt now (the heap
                // is as the code left it), then handed back through the spill array.
                int[] live = exit.TaggedLive;
                spill = live.Length == 0 ? s_noSpill : new JSValue[live.Length];
                for (int j = 0; j < live.Length; j++) spill[j] = Value(live[j], isolate.RegisterStack, ctx, fp);
                if (exit.Kind == NativeExitKind.kInterrupt)
                {
                    if (exit.InterruptKind == 1)
                    {
                        // A loop without calls: an interrupt it cannot defer leaves
                        // the code (MaglevCodeGenerator, HandleNoHeapWritesInterrupt).
                        if (isolate.StackGuard.HasUndeferrableInterrupts) return Deoptimize(isolate, ref state, ctx, exit);
                    }
                    else
                    {
                        // The interrupt may run code that enters native code again
                        // (an API interrupt): this activation's homes are saved.
                        long[] saved = ctx.AsSpan(FirstHome, HomeEnd - FirstHome).ToArray();
                        JSValue[]? deoptValues = RebuildsByLoad ? RebuildDeoptValues(exit, isolate.RegisterStack, ctx, fp) : null;
                        if (exit.BytecodeOffset >= 0) InterpreterRuntime.SetFramePc(isolate, fp, exit.BytecodeOffset);
                        MaglevBuiltins.HandleInterruptsSlow(isolate);
                        ctx = NativeContextOf(isolate, ContextWords);
                        saved.CopyTo(ctx.AsSpan(FirstHome));
                        // The interrupt may have run code that changed what the
                        // code's reload-rebuilt values read: continue in the
                        // interpreter (the code stays valid). The deopt's values
                        // were rebuilt before the interrupt, into the spill array.
                        if (deoptValues is not null) return DeoptimizeWithValues(isolate, ref state, exit, deoptValues);
                    }
                }
                ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = exit.ResumeId;
                ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            }
        }

        // ---- The direct entry (MaglevCode.FastCall) ------------------------------------------------------
        //
        // A frameless entry, as the IL backend's (MaglevCodeGenerator.
        // TryGenerateFramelessEntry): the code reads its closure, context,
        // receiver and parameters through the frame pointer, so they go into
        // the isolate's pseudo-frame (the slots of an interpreter frame at
        // their offsets, nothing else) and no interpreter frame is built. A
        // deopt builds it (MaglevCalls.DeoptimizeFrameless); the receiver and
        // the arguments are in the first scratch slots, the translation's
        // values after them. The code makes no calls and handles no
        // interrupts (MaglevNativeCodeGenerator.SupportsFramelessEntry), so
        // nothing else can use the pseudo-frame while it runs.

        /// <summary>The frame pointer of the pseudo-frame (its parameters below it).</summary>
        const int kPseudoFp = -InterpreterRuntime.kFirstArgumentOffset + MaglevFastCalls.kMaxArity;

        public int FastCallArity = -1;

        static JSValue[] PseudoFrame(Isolate isolate) => isolate.MaglevNativeFrame ??= new JSValue[kPseudoFp + 1];

        static JSValue[] Frame(Isolate isolate, JSFunction function, JSValue receiver)
        {
            JSValue[] frame = PseudoFrame(isolate);
            frame[kPseudoFp + InterpreterRuntime.kClosureOffset] = JSValue.FromObject(function);
            frame[kPseudoFp + InterpreterRuntime.kContextOffset] = JSValue.FromObject(function.Context);
            frame[kPseudoFp + InterpreterRuntime.kReceiverOffset] = receiver;
            return frame;
        }

        static void Arg(JSValue[] frame, int i, JSValue value) => frame[kPseudoFp + InterpreterRuntime.kFirstArgumentOffset - i] = value;

        public JSValue FastCall0(Isolate isolate, JSFunction function, int argc, JSValue receiver) =>
            RunFrameless(isolate, function, argc, Frame(isolate, function, receiver));

        public JSValue FastCall1(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0)
        {
            JSValue[] frame = Frame(isolate, function, receiver);
            Arg(frame, 0, a0);
            return RunFrameless(isolate, function, argc, frame);
        }

        public JSValue FastCall2(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1)
        {
            JSValue[] frame = Frame(isolate, function, receiver);
            Arg(frame, 0, a0);
            Arg(frame, 1, a1);
            return RunFrameless(isolate, function, argc, frame);
        }

        public JSValue FastCall3(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1, JSValue a2)
        {
            JSValue[] frame = Frame(isolate, function, receiver);
            Arg(frame, 0, a0);
            Arg(frame, 1, a1);
            Arg(frame, 2, a2);
            return RunFrameless(isolate, function, argc, frame);
        }

        public JSValue FastCall4(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1, JSValue a2,
            JSValue a3)
        {
            JSValue[] frame = Frame(isolate, function, receiver);
            Arg(frame, 0, a0);
            Arg(frame, 1, a1);
            Arg(frame, 2, a2);
            Arg(frame, 3, a3);
            return RunFrameless(isolate, function, argc, frame);
        }

        public JSValue FastCall5(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1, JSValue a2,
            JSValue a3, JSValue a4)
        {
            JSValue[] frame = Frame(isolate, function, receiver);
            Arg(frame, 0, a0);
            Arg(frame, 1, a1);
            Arg(frame, 2, a2);
            Arg(frame, 3, a3);
            Arg(frame, 4, a4);
            return RunFrameless(isolate, function, argc, frame);
        }

        public JSValue FastCall6(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1, JSValue a2,
            JSValue a3, JSValue a4, JSValue a5)
        {
            JSValue[] frame = Frame(isolate, function, receiver);
            Arg(frame, 0, a0);
            Arg(frame, 1, a1);
            Arg(frame, 2, a2);
            Arg(frame, 3, a3);
            Arg(frame, 4, a4);
            Arg(frame, 5, a5);
            return RunFrameless(isolate, function, argc, frame);
        }

        /// <summary>The FastCall delegate of the code's arity.</summary>
        public Delegate CreateFastCall() => FastCallArity switch
        {
            0 => new MaglevFastCall0(FastCall0),
            1 => new MaglevFastCall1(FastCall1),
            2 => new MaglevFastCall2(FastCall2),
            3 => new MaglevFastCall3(FastCall3),
            4 => new MaglevFastCall4(FastCall4),
            5 => new MaglevFastCall5(FastCall5),
            _ => new MaglevFastCall6(FastCall6),
        };

        JSValue RunFrameless(Isolate isolate, JSFunction function, int argc, JSValue[] frame)
        {
            long[] ctx = NativeContextOf(isolate, ContextWords);
            ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = 0;
            ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            JSValue[] spill = s_noSpill;
            while (true)
            {
                long id = NativeCode.Call(Entry, ctx, ref frame[kPseudoFp], Constants, spill, ref isolate.StackGuard.InterruptFlagsRef);
                NativeExit exit = Exits[id];
                switch (exit.Kind)
                {
                    case NativeExitKind.kReturn:
                        return Value(exit.Value, frame, ctx, kPseudoFp);
                    case NativeExitKind.kDeopt:
                        return DeoptimizeFrameless(isolate, function, argc, frame, ctx, exit);
                    case NativeExitKind.kDeoptAfterMigration:
                        MaglevBuiltins.TryMigrateInstanceAndMarkMapAsMigrationTarget(isolate, Value(exit.Value, frame, ctx, kPseudoFp));
                        return DeoptimizeFrameless(isolate, function, argc, frame, ctx, exit);
                    case NativeExitKind.kAbort:
                        throw new IndexOutOfRangeException("V8Sharp: " + exit.Message);
                    case NativeExitKind.kUnreachable:
                        MaglevBuiltins.Unreachable();
                        return JSValue.Undefined;
                    case NativeExitKind.kInterrupt when isolate.StackGuard.HasUndeferrableInterrupts:
                        // Only loops without calls (InterruptKind 1) have a frameless entry.
                        return DeoptimizeFrameless(isolate, function, argc, frame, ctx, exit);
                }
                int[] live = exit.TaggedLive;
                spill = live.Length == 0 ? s_noSpill : new JSValue[live.Length];
                for (int j = 0; j < live.Length; j++) spill[j] = Value(live[j], frame, ctx, kPseudoFp);
                ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = exit.ResumeId;
                ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            }
        }

        JSValue DeoptimizeFrameless(Isolate isolate, JSFunction function, int argc, JSValue[] frame, long[] ctx, NativeExit exit)
        {
            JSValue[] scratch = isolate.MaglevDeoptScratch;
            for (int i = 0; i <= FastCallArity; i++)
            {
                scratch[i] = frame[kPseudoFp + InterpreterRuntime.kReceiverOffset - i];
            }
            foreach ((int slot, int value) in DeoptValues[exit.DeoptIndex]) scratch[slot] = Value(value, frame, ctx, kPseudoFp);
            return MaglevCalls.DeoptimizeFrameless(isolate, Code, exit.DeoptIndex, exit.Reason, function, argc, 0);
        }

        /// <summary>The values of an exit's deopt, rebuilt now (before an interrupt that may change the heap).</summary>
        JSValue[] RebuildDeoptValues(NativeExit exit, JSValue[] frame, long[] ctx, int fp)
        {
            (int Slot, int Value)[] values = DeoptValues[exit.DeoptIndex];
            var result = new JSValue[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = Value(values[i].Value, frame, ctx, fp);
            return result;
        }

        /// <summary>A deopt with values rebuilt earlier (RebuildDeoptValues).</summary>
        JSValue DeoptimizeWithValues(Isolate isolate, ref InterpreterState state, NativeExit exit, JSValue[] rebuilt)
        {
            JSValue[] scratch = isolate.MaglevDeoptScratch;
            (int Slot, int Value)[] values = DeoptValues[exit.DeoptIndex];
            for (int i = 0; i < values.Length; i++) scratch[values[i].Slot] = rebuilt[i];
            Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, Code, exit.DeoptIndex, exit.Reason);
            return JSValue.Undefined;
        }

        JSValue Deoptimize(Isolate isolate, ref InterpreterState state, long[] ctx, NativeExit exit)
        {
            JSValue[] scratch = isolate.MaglevDeoptScratch;
            int fp = state.Fp;
            foreach ((int slot, int value) in DeoptValues[exit.DeoptIndex]) scratch[slot] = Value(value, isolate.RegisterStack, ctx, fp);
            Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, Code, exit.DeoptIndex, exit.Reason);
            return JSValue.Undefined;
        }

        /// <summary>The JSValue of value <paramref name="index"/> at an exit.</summary>
        internal JSValue Value(int index, JSValue[] frame, long[] ctx, int fp)
        {
            NativeValueInfo v = Values[index];
            switch (v.Kind)
            {
                case NativeValueKind.kConstant:
                    return v.Constant;
                case NativeValueKind.kInt32:
                    return JSValue.FromInt((int)ctx[v.Home]);
                case NativeValueKind.kUint32:
                    return JSValue.FromNumber((uint)ctx[v.Home]);
                case NativeValueKind.kFloat64:
                    return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[v.Home]));
                case NativeValueKind.kHoleyFloat64:
                    return MaglevBuiltins.HoleyFloat64ToTagged(BitConverter.Int64BitsToDouble(ctx[v.Home]));
                case NativeValueKind.kFrameSlot:
                    return frame[fp + v.FrameIndex];
                case NativeValueKind.kTaggedNumber:
                    return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[v.Home + 1]));
                case NativeValueKind.kTaggedHoleyNumber:
                    return MaglevBuiltins.HoleyFloat64ToTagged(BitConverter.Int64BitsToDouble(ctx[v.Home + 1]));
                case NativeValueKind.kTaggedBoolean:
                    return ctx[v.Home + 2] == (long)NativeTaggedKind.kTrue ? JSValue.True : JSValue.False;
                case NativeValueKind.kTaggedDynamic:
                    return (NativeTaggedKind)ctx[v.Home + 2] switch
                    {
                        NativeTaggedKind.kUndefined => JSValue.Undefined,
                        NativeTaggedKind.kNumber => JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[v.Home + 1])),
                        NativeTaggedKind.kTrue => JSValue.True,
                        NativeTaggedKind.kFalse => JSValue.False,
                        NativeTaggedKind.kNull => JSValue.Null,
                        NativeTaggedKind.kTheHole => JSValue.TheHole,
                        _ => Value(v.Other, frame, ctx, fp),
                    };
                case NativeValueKind.kElements:
                    return JSValue.FromObject(((JSObject)Value(v.Input0, frame, ctx, fp).Object).Elements);
                case NativeValueKind.kFixedArrayElement:
                    return ((FixedArray)Value(v.Input0, frame, ctx, fp).Object)._data[(int)Value(v.Input1, frame, ctx, fp).Number];
                case NativeValueKind.kIdentity:
                    return Value(v.Input0, frame, ctx, fp);
                case NativeValueKind.kTaggedField:
                    return MaglevBuiltins.LoadField(Value(v.Input0, frame, ctx, fp), v.FrameIndex);
                case NativeValueKind.kContextSlot:
                {
                    var context = (Context)Value(v.Input0, frame, ctx, fp).Object;
                    for (int d = 0; d < v.Input1; d++) context = (Context)context.Slots[(int)Context.Field.PREVIOUS_INDEX].Object;
                    return context.Slots[v.FrameIndex];
                }
                case NativeValueKind.kMap:
                    return JSValue.FromObject(MaglevBuiltins.MapOf(Value(v.Input0, frame, ctx, fp)));
                default:
                    throw new InvalidOperationException("native value kind " + v.Kind);
            }
        }
    }
}
