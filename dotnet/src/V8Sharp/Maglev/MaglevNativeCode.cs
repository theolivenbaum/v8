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
        /// <summary>
        /// The native Maglev backend's buffers (context, spill array) by
        /// activation depth (MaglevNativeCode.ActivationAt): an activation
        /// that runs code (a call) leaves its context to it untouched.
        /// </summary>
        internal Maglev.NativeActivation?[] MaglevNativeActivations = new Maglev.NativeActivation?[8];
        /// <summary>The depth of the next native activation.</summary>
        internal int MaglevNativeDepth;
        nint _registerStackAddress;

        /// <summary>The address of RegisterStack[0] (pinned, allocated once).</summary>
        internal nint RegisterStackAddress => _registerStackAddress != 0
            ? _registerStackAddress
            : _registerStackAddress = Codegen.NativeCode.AddressOf(RegisterStack, 0);

        /// <summary>The pseudo-frame of native Maglev code's direct entry (MaglevNativeCode.FastCall*).</summary>
        internal JSValue[]? MaglevNativeFrame;
        internal nint MaglevNativeFrameAddress;
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
        /// <summary>A frame slot the code writes (StoreRegister, SetCurrentContext): snapshotted before managed nodes.</summary>
        kWrittenFrameSlot,
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
        /// <summary>LoadPropertyCellValue of the cell Constant.Object, done again.</summary>
        kPropertyCell,
        /// <summary>ConvertHoleToUndefined of Input0.</summary>
        kHoleToUndefined,
        /// <summary>The result of a node run by the managed side (kManagedNode): the activation's managed slot SnapSlot.</summary>
        kManaged,
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
        /// <summary>
        /// A value rebuilt by repeating a load, in code with managed nodes or
        /// interrupts that run code: its slot in the activation's managed
        /// array, where an exit that may change the heap leaves its value
        /// (flagged by the home's fourth word); -1 otherwise.
        /// </summary>
        public int SnapSlot = -1;
        /// <summary>A slot load whose kind word was set when it loaded: numbers and oddballs come from it, only objects are loaded again.</summary>
        public bool KindAtLoad;
    }

    /// <summary>A node the managed side runs for native code (an exit, then a resume), as the IL backend does.</summary>
    /// <summary>An inlined function's frame a managed node pushes (MaglevCodeGenerator.EmitPushInlinedFrame's values).</summary>
    internal sealed class NativeInlinedFrame
    {
        public JSFunction? Function;
        /// <summary>The closure's value when <see cref="Function"/> is null, or -1.</summary>
        public int Closure = -1;
        public BytecodeArray Bytecode = null!;
        public FeedbackVector? Feedback;
        public int Argc;
        public bool IsConstruct;
        public int Receiver;
        public int[] Args = [];
        public int NewTarget = -1, NewTargetRegister;
        /// <summary>The call's offset in the caller's bytecode; the frame's own offset.</summary>
        public int CallOffset, Pc;
    }

    /// <summary>The pinned buffers of native activations at one depth (Isolate.MaglevNativeActivations).</summary>
    internal sealed class NativeActivation
    {
        public long[] Context = [];
        public nint ContextAddress;
        public JSValue[] Spill = [];
        public nint SpillAddress;
    }

    internal sealed class NativeManagedNode
    {
        /// <summary>The inlined functions' frames pushed around the node (a call in an inlined function), outermost first.</summary>
        public NativeInlinedFrame[]? InlinedFrames;
        public Opcode Opcode;
        public int[] Inputs = [];
        /// <summary>The node's value (its info), or -1.</summary>
        public int Result = -1;
        public int ResultHome = -1;
        public ValueRepresentation ResultRepresentation;
        public bool ResultNeedsKind;
        public int BytecodeOffset = -1;
        public bool Inlined;
        /// <summary>The DeoptPoint of the node's lazy deopt (the code was invalidated meanwhile), or -1.</summary>
        public int LazyDeoptIndex = -1;
        /// <summary>CallBuiltin as a check, CheckTypedArrayValid: the eager deopt when it fails.</summary>
        public int EagerDeoptIndex = -1;
        public int EagerReason;
        public int Int0, Int1, Int2;
        public object? Obj0, Obj1;
        public System.Reflection.MethodInvoker? Invoker;
        /// <summary>CallBuiltin: the builtin's thunk (NativeBuiltinThunks), its argument buffer (constants filled in) and constant objects.</summary>
        public NativeBuiltinThunk? Thunk;
        public JSValue[] ThunkValues = [];
        public object?[] ThunkObjects = [];
        /// <summary>InlinedAllocation: the in-object slot class's constructor; InitialFields: an object literal's fields.</summary>
        public System.Reflection.ConstructorInvoker? Allocate;
        public JSValue[]? InitialFields;
        public Type[] ParameterTypes = [];
        public Type? ReturnType;
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
        /// <summary>A node the managed side runs (NativeManagedNode); then the code resumes.</summary>
        kManagedNode,
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
        /// <summary>kManagedNode: the node.</summary>
        public NativeManagedNode? Managed;
        /// <summary>Resumable exits that may change the heap: the reload-rebuilt values whose values are kept first.</summary>
        public int[] Snapshot = [];
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
        /// <summary>The managed slots of an activation (results of managed nodes, snapshots).</summary>
        public int ManagedSlots;
        public bool HasManagedNodes;
        /// <summary>For each DeoptPoint: the value of each of its scratch slots (-1: none), as (slot, value) pairs.</summary>
        public (int Slot, int Value)[][] DeoptValues = [];
        /// <summary>The first and last home words of the code (saved around interrupt handling).</summary>
        public int FirstHome, HomeEnd;


        static readonly JSValue[] s_noSpill = GC.AllocateArray<JSValue>(1, pinned: true);
        static readonly nint s_noSpillAddress = NativeCode.AddressOf(s_noSpill, 0);
        internal static nint NoSpillAddress => s_noSpillAddress;

        /// <summary>The first word of a native callee's context in the call area (MaglevNativeCodeGenerator, "Native-to-native calls").</summary>
        public int CalleeContextBase;

        /// <summary>The exit code of a number return: its double is in the context's return word.</summary>
        internal const long kNumberReturn = -1;

        /// <summary>The constants table's address (Constants is on the pinned object heap).</summary>
        public nint ConstantsAddress;

        /// <summary>
        /// The buffers of the native activation at <paramref name="depth"/>, its
        /// context at least <paramref name="words"/> long. Each depth has its own
        /// pinned context and spill array, so an activation's homes survive the
        /// code a managed node or an interrupt runs (deeper activations) without
        /// being saved, and a resume allocates nothing.
        /// </summary>
        internal static NativeActivation ActivationAt(Isolate isolate, int depth, int words)
        {
            if (depth >= isolate.MaglevNativeActivations.Length) Array.Resize(ref isolate.MaglevNativeActivations, depth * 2 + 8);
            NativeActivation a = isolate.MaglevNativeActivations[depth] ??= new NativeActivation();
            if (a.Context.Length < words)
            {
                a.Context = NativeCode.NewContext(Math.Max(words, Math.Max(1024, a.Context.Length * 2)));
                a.ContextAddress = NativeCode.AddressOf(a.Context, 0);
            }
            return a;
        }

        /// <summary>The activation's spill array, at least <paramref name="length"/> long (pinned: the code reads it by address).</summary>
        static JSValue[] SpillOf(NativeActivation a, int length)
        {
            if (length == 0) return s_noSpill;
            if (a.Spill.Length < length)
            {
                a.Spill = GC.AllocateArray<JSValue>(Math.Max(length, 32), pinned: true);
                a.SpillAddress = NativeCode.AddressOf(a.Spill, 0);
            }
            return a.Spill;
        }

        static nint AddressOfSpill(NativeActivation a, JSValue[] spill) => ReferenceEquals(spill, s_noSpill) ? s_noSpillAddress : a.SpillAddress;

        /// <summary>
        /// One entry into the code: the context's fixed words, then the call
        /// (every address is of a pinned array: nothing to pin per call).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        long Enter(Isolate isolate, long[] ctx, nint ctxAddress, nint frame, nint spill)
        {
            ctx[NativeCode.kFrameSlot] = frame;
            ctx[NativeCode.kConstantsSlot] = ConstantsAddress;
            ctx[NativeCode.kSpillSlot] = spill;
            ctx[NativeCode.kInterruptFlagsSlot] = isolate.StackGuard.InterruptFlagsAddress;
            return NativeCode.CallPinned(Entry, ctxAddress);
        }

        // ---- Profile ------------------------------------------------------------------------------------------
        //
        // A managed node costs an exit and a resume (a few hundred
        // nanoseconds) where RyuJIT's code calls the builtin directly; native
        // code wins only when its loops do enough work per managed exit. As
        // Ryu decides from its run-time counts, the code counts its calls,
        // managed exits and loop back edges (the poll budget it used), and a
        // function that takes a managed exit for fewer than kMinBackEdgesPerExit
        // back edges is invalidated and compiled again as IL.

        const int kProfileCalls = 256;
        const long kMinBackEdgesPerExit = 256;
        long _calls, _managedExits, _backEdges;
        bool _replaced;

        void Profile(Isolate isolate)
        {
            if (++_calls % kProfileCalls != 0 || _managedExits == 0) return;
            if (_managedExits * kMinBackEdgesPerExit <= _backEdges || Code.MarkedForDeoptimization || _replaced) return;
            GoBackToIL(isolate);
        }

        /// <summary>
        /// The same decision within an activation, every kProfileExits managed
        /// exits: a long-running loop entered once (whose calls take managed
        /// exits) is not caught by the per-call count.
        /// </summary>
        const long kProfileExits = 1024;

        bool ProfileExit(Isolate isolate)
        {
            if (_managedExits % kProfileExits != 0 || _replaced || Code.MarkedForDeoptimization) return false;
            if (_managedExits * kMinBackEdgesPerExit <= _backEdges) return false;
            GoBackToIL(isolate);
            return true;
        }

        /// <summary>The run-time profile decided for IL (a concurrent IL compile may replace this code).</summary>
        internal bool GoingBackToIL => _replaced;

        void GoBackToIL(Isolate isolate)
        {
            _replaced = true;
            MaglevNativeCodeGenerator.DeclineAtRunTime(Code.SharedFunctionInfo);
            if (isolate.Flags.trace_maglev_native)
            {
                Console.WriteLine($"[maglev native: {MaglevCompiler.DebugName(Code.SharedFunctionInfo)} goes back to IL: " +
                                  $"{_managedExits} managed exits, {_backEdges} back edges in {_calls} calls]");
            }
            // The IL code replaces it: compiled concurrently (RyuJIT optimizes it fully at
            // once; a synchronous compile would start at RyuJIT's tier 0) while the native
            // code keeps running, or at once without concurrent recompilation. (The function
            // stays optimized; this activation and running ones finish in the native code,
            // which stays valid.)
            if (isolate.Flags.concurrent_recompilation && MaglevCompiler.CompileConcurrently(isolate, Code.Function, Code.OsrOffset)) return;
            MaglevCode? il = Code.OsrOffset < 0 ? MaglevCompiler.Compile(isolate, Code.Function, -1) : null;
            if (il is not null) MaglevCompiler.InstallCode(isolate, il);
            else MaglevCompiler.InvalidateCode(isolate, Code, Deoptimizer.LazyDeoptimizeReason.kTesting);
        }

        /// <summary>The MaglevCodeEntry of native code.</summary>
        public JSValue Run(Isolate isolate, ref InterpreterState state)
        {
            if (ManagedSlots > 0 || HasManagedNodes) Profile(isolate);
            int depth = isolate.MaglevNativeDepth++;
            try
            {
                return RunAt(isolate, ref state, ActivationAt(isolate, depth, ContextWords));
            }
            finally
            {
                isolate.MaglevNativeDepth = depth;
            }
        }

        JSValue RunAt(Isolate isolate, ref InterpreterState state, NativeActivation activation)
        {
            long[] ctx = activation.Context;
            int fp = state.Fp;
            ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = 0;
            ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            nint spill = s_noSpillAddress;
            // The activation's managed slots: results of managed nodes, snapshots.
            JSValue[]? managed = null;
            while (true)
            {
                long id = Enter(isolate, ctx, activation.ContextAddress, isolate.RegisterStackAddress + fp * 16, spill);
                _backEdges += kPollBudget - ctx[MaglevNativeCodeGenerator.kPollBudgetSlot];
                if (id == kNumberReturn) return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[MaglevNativeCodeGenerator.kReturnSlot]));
                NativeExit exit = Exits[id];
                switch (exit.Kind)
                {
                    case NativeExitKind.kReturn:
                        return Value(exit.Value, isolate.RegisterStack, ctx, fp, managed);
                    case NativeExitKind.kDeopt:
                        return Deoptimize(isolate, ref state, ctx, exit, managed);
                    case NativeExitKind.kDeoptAfterMigration:
                        MaglevBuiltins.TryMigrateInstanceAndMarkMapAsMigrationTarget(isolate, Value(exit.Value, isolate.RegisterStack, ctx, fp, managed));
                        return Deoptimize(isolate, ref state, ctx, exit, managed);
                    case NativeExitKind.kAbort:
                        throw new IndexOutOfRangeException("V8Sharp: " + exit.Message);
                    case NativeExitKind.kUnreachable:
                        MaglevBuiltins.Unreachable();
                        return JSValue.Undefined;
                    case NativeExitKind.kInterrupt when exit.InterruptKind == 1:
                        // A loop without calls: an interrupt it cannot defer leaves
                        // the code (MaglevCodeGenerator, HandleNoHeapWritesInterrupt).
                        if (isolate.StackGuard.HasUndeferrableInterrupts) return Deoptimize(isolate, ref state, ctx, exit, managed);
                        break;
                    case NativeExitKind.kInterrupt:
                    case NativeExitKind.kManagedNode:
                    {
                        // Code may run that changes the heap (an API interrupt, the
                        // node itself): the values rebuilt by repeating a load are kept
                        // as they are now. (Native code it runs uses deeper activations'
                        // buffers: this activation's homes stay as they are.)
                        if (exit.Snapshot.Length > 0) Snapshot(exit, isolate.RegisterStack, ctx, fp, ref managed);
                        if (exit.Kind == NativeExitKind.kInterrupt)
                        {
                            if (exit.BytecodeOffset >= 0) InterpreterRuntime.SetFramePc(isolate, fp, exit.BytecodeOffset);
                            MaglevBuiltins.HandleInterruptsSlow(isolate);
                            break;
                        }
                        NativeManagedNode node = exit.Managed!;
                        _managedExits++;
                        // (Leaves this activation after the node through its lazy deopt: the
                        // interpreter goes on, and enters the IL code by OSR.)
                        bool leave = ProfileExit(isolate) && node.LazyDeoptIndex >= 0;
                        JSValue result = node.InlinedFrames is null
                            ? Execute(isolate, node, isolate.RegisterStack, ctx, fp, managed, out bool passed)
                            : ExecuteInInlinedFrames(isolate, node, ctx, fp, managed, out passed);
                        if (!passed)
                        {
                            return Deoptimize(isolate, ref state, ctx, new NativeExit { DeoptIndex = node.EagerDeoptIndex, Reason = node.EagerReason }, managed);
                        }
                        if (node.Result >= 0) StoreResult(node, result, ctx, ref managed);
                        if (node.Opcode == Opcode.BranchIfToBooleanTrue) ctx[MaglevNativeCodeGenerator.kBoolResultSlot] = result.IsTrue ? 1 : 0;
                        if (node.LazyDeoptIndex >= 0 && (Code.MarkedForDeoptimization || leave))
                        {
                            // The code was invalidated while the call ran: continue after
                            // it in the interpreter (EmitLazyDeoptCheck).
                            return DeoptimizeLazy(isolate, ref state, ctx, node, result, managed);
                        }
                        break;
                    }
                }
                // A resumable exit: the live tagged values, rebuilt now, then
                // handed back through the spill array.
                int[] live = exit.TaggedLive;
                JSValue[] spillArray = SpillOf(activation, live.Length);
                for (int j = 0; j < live.Length; j++) spillArray[j] = Value(live[j], isolate.RegisterStack, ctx, fp, managed);
                spill = AddressOfSpill(activation, spillArray);
                ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = exit.ResumeId;
                ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            }
        }

        void Snapshot(NativeExit exit, JSValue[] frame, long[] ctx, int fp, ref JSValue[]? managed)
        {
            managed ??= new JSValue[ManagedSlots];
            foreach (int index in exit.Snapshot)
            {
                NativeValueInfo v = Values[index];
                // (Its own value first, then the flag: Value reads the flag.)
                JSValue value = Value(index, frame, ctx, fp, managed);
                managed[v.SnapSlot] = value;
                ctx[v.Home + 3] = 1;
            }
        }

        void StoreResult(NativeManagedNode node, JSValue result, long[] ctx, ref JSValue[]? managed)
        {
            int home = node.ResultHome;
            switch (node.ResultRepresentation)
            {
                case ValueRepresentation.kInt32:
                case ValueRepresentation.kUint32:
                    ctx[home] = (uint)(int)result.Number;
                    return;
                case ValueRepresentation.kFloat64:
                case ValueRepresentation.kHoleyFloat64:
                    ctx[home] = result._bits;
                    return;
            }
            int slot = Values[node.Result].SnapSlot;
            if (slot >= 0)
            {
                managed ??= new JSValue[ManagedSlots];
                managed[slot] = result;
            }
            ctx[home + 1] = result._bits;
            // (Always: a boolean's value is its kind word.)
            ctx[home + 2] = (long)KindOf(result);
        }

        static NativeTaggedKind KindOf(JSValue v) =>
            v.IsUndefined ? NativeTaggedKind.kUndefined
            : v.IsNumber ? NativeTaggedKind.kNumber
            : v.IsTrue ? NativeTaggedKind.kTrue
            : v.IsFalse ? NativeTaggedKind.kFalse
            : v.IsNull ? NativeTaggedKind.kNull
            : v.IsTheHole ? NativeTaggedKind.kTheHole
            : NativeTaggedKind.kOther;

        /// <summary>
        /// Runs a managed node as the IL backend's code does (MaglevCodeGenerator.EmitNode).
        /// <paramref name="passed"/> is false when a check fails (its eager deopt).
        /// </summary>
        JSValue Execute(Isolate isolate, NativeManagedNode node, JSValue[] frame, long[] ctx, int fp, JSValue[]? managed, out bool passed)
        {
            passed = true;
            int[] inputs = node.Inputs;
            JSValue In(int i) => Value(inputs[i], frame, ctx, fp, managed);
            // (A node of an inlined function makes no calls: the frame's offset stays the caller's.)
            if (node.BytecodeOffset >= 0 && !node.Inlined) InterpreterRuntime.SetFramePc(isolate, fp, node.BytecodeOffset);
            switch (node.Opcode)
            {
                case Opcode.StoreTaggedField:
                {
                    JSValue obj = In(0), value = In(1);
                    if (node.Int1 != 0) MaglevBuiltins.StoreDoubleField(obj, node.Int0, value);
                    else MaglevBuiltins.StoreField(obj, node.Int0, value);
                    return default;
                }
                case Opcode.StoreDoubleField:
                    MaglevBuiltins.StoreDoubleField(In(0), node.Int0, In(1));
                    return default;
                case Opcode.StoreFixedArrayElement:
                    MaglevBuiltins.StoreFixedArrayElement(In(0), (int)In(1).Number, In(2));
                    return default;
                case Opcode.StoreContextSlot:
                {
                    var context = (Context)In(0).Object;
                    for (int d = 0; d < node.Int1; d++) context = (Context)context.Slots[(int)Context.Field.PREVIOUS_INDEX].Object;
                    context.Slots[node.Int0] = In(1);
                    return default;
                }
                case Opcode.StorePropertyCellValue:
                    ((PropertyCell)node.Obj0!).Value = In(0);
                    return default;
                case Opcode.ToBoolean:
                {
                    bool b = MaglevBuiltins.ToBoolean(In(0));
                    return JSValue.FromBoolean(b);
                }
                case Opcode.BranchIfToBooleanTrue:
                    return JSValue.FromBoolean(MaglevBuiltins.ToBoolean(In(0)));
                case Opcode.ToBooleanLogicalNot:
                    return JSValue.FromBoolean(!MaglevBuiltins.ToBoolean(In(0)));
                case Opcode.TestUndetectable:
                    return node.Int0 != 0 ? MaglevBuiltins.TestUndefinedOrNull(In(0)) : MaglevBuiltins.TestUndetectable(In(0));
                case Opcode.TestTypeOf:
                    return MaglevBuiltins.TestTypeOf(In(0), node.Int0);
                case Opcode.LoadTypedArrayLength:
                    return node.Int0 == 1
                        ? JSValue.FromNumber(MaglevBuiltins.TypedArrayLengthAsFloat64(In(0)))
                        : JSValue.FromInt(MaglevBuiltins.TypedArrayLength(In(0)));
                case Opcode.CheckTypedArrayValid:
                    passed = MaglevBuiltins.IsTypedArrayValid(In(0), node.Int0 == 1);
                    return default;
                case Opcode.StringLength:
                    return JSValue.FromInt(MaglevBuiltins.StringLength(In(0)));
                case Opcode.InlinedAllocation:
                {
                    // (The IL's EmitInlinedAllocation: native code allocates nothing.)
                    var map = (Map)node.Obj0!;
                    if (node.InitialFields is { } fields) return MaglevBuiltins.AllocateObjectLiteral(map, fields);
                    return JSValue.FromObject((HeapObject)node.Allocate!.Invoke(map, FixedArray.Empty));
                }
                case Opcode.FastNewObject:
                    return MaglevBuiltins.FastNewObject(isolate, (Map)node.Obj0!);
                case Opcode.CheckMaps:
                    passed = MaglevBuiltins.MigrateAndCheckMaps(isolate, In(0), (Map[])node.Obj0!);
                    return default;
                case Opcode.StoreRegister:
                    JSValue.StoreSlot(ref frame[fp + node.Int0], In(0));
                    return default;
                case Opcode.SetCurrentContext:
                {
                    // (An inlined function's frame is not pushed: only the isolate's context, as the IL's lazy frames.)
                    JSValue context = In(0);
                    if (node.Inlined) isolate.Context = (Context)context.Object;
                    else MaglevBuiltins.SetCurrentContext(isolate, ref frame[fp + InterpreterRuntime.kContextOffset], context);
                    return default;
                }
                case Opcode.StoreMapTransition:
                    MaglevBuiltins.StoreTransition(In(0), (Map)node.Obj0!, node.Int0, In(1), node.Int1 != 0);
                    return default;
                case Opcode.MaybeGrowFastElements:
                {
                    JSValue elements = node.Int2 > 0
                        ? MaglevBuiltins.MaybeGrowFastElementsForPush(isolate, In(0), (int)In(1).Number, node.Int2)
                        : MaglevBuiltins.MaybeGrowFastElements(isolate, In(0), (int)In(1).Number, node.Int0, node.Int1);
                    passed = elements._obj is not null;
                    return elements;
                }
                case Opcode.TransitionElementsKind:
                {
                    JSValue obj = In(0);
                    if (node.EagerDeoptIndex < 0 && node.Obj1 is Map[] sources)
                    {
                        // (The IL's: only an object with one of the source maps transitions.)
                        if (MaglevBuiltins.MapOf(obj) is { } map && Array.IndexOf(sources, map) >= 0)
                        {
                            MaglevBuiltins.TransitionElementsKind(isolate, obj, (Map)node.Obj0!);
                        }
                        return default;
                    }
                    passed = MaglevBuiltins.TransitionElementsKind(isolate, obj, (Map)node.Obj0!);
                    return default;
                }
                case Opcode.HasInPrototypeChain:
                {
                    var info = (HasInstanceInfo)node.Obj0!;
                    int has = MaglevBuiltins.HasInPrototypeChain(In(0), info.Function, info.FunctionMap, info.PrototypeOrInitialMap, info.Prototype);
                    passed = has != 2;
                    return MaglevBuiltins.Boolean(has != 0);
                }
                case Opcode.CheckedObjectToIndex:
                {
                    // (The IL's: an integral number inline, the rest through the helper.)
                    JSValue v = In(0);
                    if (v.IsNumber)
                    {
                        double d = v.Number;
                        int i = (int)d;
                        passed = i == d;
                        return JSValue.FromInt(i);
                    }
                    long index = MaglevBuiltins.ObjectToIndexOrMin(v);
                    passed = index != long.MinValue;
                    return JSValue.FromInt((int)index);
                }
                case Opcode.MigrateMapIfNeeded:
                    return MaglevBuiltins.MigrateMapIfNeeded(isolate, In(0), In(1));
                case Opcode.Float64Exponentiate:
                    return JSValue.FromNumber(MaglevBuiltins.Float64Exponentiate(In(0).Number, In(1).Number));
                case Opcode.Float64Ieee754Unary:
                    return JSValue.FromNumber(MaglevBuiltins.Float64Ieee754Unary(In(0).Number, node.Int0));
                case Opcode.LoadFixedArrayLength:
                    return JSValue.FromInt(MaglevBuiltins.LoadFixedArrayLength(In(0)));
                case Opcode.BuiltinStringPrototypeCharCodeAt:
                    return JSValue.FromInt(MaglevBuiltins.StringCharCodeAt(In(0), (int)In(1).Number));
                case Opcode.BuiltinStringPrototypeCharCodeAtOrNaN:
                    return JSValue.FromNumber(MaglevBuiltins.StringCharCodeAtOrNaN(In(0), (int)In(1).Number));
                case Opcode.CheckInstanceType:
                {
                    JSValue v = In(0);
                    passed = node.Int0 switch
                    {
                        1 => MaglevBuiltins.IsInternalizedString(v),
                        2 => MaglevBuiltins.IsJSReceiver(v),
                        4 => MaglevBuiltins.IsJSReceiverOrNullOrUndefined(v),
                        5 => MaglevBuiltins.IsStringOrStringWrapper(v),
                        _ => MaglevBuiltins.IsWritableElements(v),
                    };
                    return default;
                }
                case Opcode.CallKnownJSFunction:
                {
                    var info = (KnownCallInfo)node.Obj0!;
                    JSValue target = info.Target is { } t ? JSValue.FromObject(t) : In(info.TargetInput);
                    long calleeExit = ctx[MaglevNativeCodeGenerator.kCalleeExitSlot];
                    if (calleeExit != 0)
                    {
                        // A native callee called directly left by another exit than a
                        // number return: it continues here, from a copy of its context
                        // and its pseudo-frame rebuilt from this code's values.
                        ctx[MaglevNativeCodeGenerator.kCalleeExitSlot] = 0;
                        MaglevNativeCode callee = info.Vector.MaglevCode!.Native!;
                        var function = (JSFunction)target.Object;
                        long[] calleeCtx = NativeCode.NewContext(callee.ContextWords);
                        ctx.AsSpan(CalleeContextBase, callee.ContextWords).CopyTo(calleeCtx);
                        JSValue[] calleeFrame = NewPseudoFrame(function, In(info.HasConvertedReceiver ? info.ConvertedReceiverInput : 0));
                        for (int i = 0; i < info.FormalCount; i++)
                        {
                            SetPseudoFrameArgument(calleeFrame, i, i < info.Argc ? In(1 + i) : JSValue.Undefined);
                        }
                        return callee.ContinueFrameless(isolate, function, info.Argc, calleeFrame, calleeCtx, calleeExit - 1);
                    }
                    int argc = info.Argc;
                    return MaglevCalls.CallValuesN(isolate, target, In(0), argc, argc > 0 ? In(1) : default, argc > 1 ? In(2) : default,
                        argc > 2 ? In(3) : default, argc > 3 ? In(4) : default, argc > 4 ? In(5) : default, argc > 5 ? In(6) : default, (int)info.Mode);
                }
                case Opcode.CallBuiltin when node.Thunk is { } thunk:
                {
                    // (The buffer's constants are filled in; a recursive call of the node
                    // refills it after this call has read it.)
                    var info = (CallBuiltinInfo)node.Obj0!;
                    JSValue[] values = node.ThunkValues;
                    for (int k = 0; k < info.Args.Length; k++)
                    {
                        BuiltinArg arg = info.Args[k];
                        if (arg.Kind == BuiltinArgKind.Input) values[k] = In(arg.Index);
                        else if (arg.Kind == BuiltinArgKind.Closure) values[k] = frame[fp + InterpreterRuntime.kClosureOffset];
                    }
                    JSValue r = thunk(isolate, values, node.ThunkObjects);
                    if (info.DeoptIfFalse)
                    {
                        passed = r.IsTrue;
                        return default;
                    }
                    return r;
                }
                case Opcode.CallBuiltin:
                {
                    var info = (CallBuiltinInfo)node.Obj0!;
                    var args = new object?[info.Args.Length];
                    for (int k = 0; k < args.Length; k++)
                    {
                        BuiltinArg arg = info.Args[k];
                        Type type = node.ParameterTypes[k];
                        args[k] = arg.Kind switch
                        {
                            BuiltinArgKind.Isolate => isolate,
                            BuiltinArgKind.Input => Convert(In(arg.Index), type),
                            BuiltinArgKind.Int => type == typeof(uint) ? (uint)arg.Index : arg.Index,
                            BuiltinArgKind.Bool => arg.Index != 0,
                            BuiltinArgKind.Constant => type == typeof(JSValue)
                                ? arg.Object is JSValue jv ? jv : JSValue.FromObject((HeapObject?)arg.Object)
                                : arg.Object is JSValue jo ? jo.HeapObjectOrNull : arg.Object,
                            BuiltinArgKind.Closure => Convert(frame[fp + InterpreterRuntime.kClosureOffset], type),
                            _ => throw new InvalidOperationException("builtin argument " + arg.Kind),
                        };
                    }
                    object? r = node.Invoker!.Invoke(null, new Span<object?>(args));
                    if (info.DeoptIfFalse)
                    {
                        passed = (bool)r!;
                        return default;
                    }
                    return r switch
                    {
                        null => default,
                        JSValue v => v,
                        int i => JSValue.FromInt(i),
                        uint u => JSValue.FromNumber(u),
                        double d => JSValue.FromNumber(d),
                        bool b => JSValue.FromBoolean(b),
                        HeapObject o => JSValue.FromObject(o),
                        _ => throw new InvalidOperationException("builtin result " + r.GetType().Name),
                    };
                }
                default:
                    throw new InvalidOperationException("managed node " + node.Opcode);
            }
        }

        /// <summary>A JSValue as a builtin's parameter type (the IL's Load(input, repr) / LoadAsObject).</summary>
        static object? Convert(JSValue v, Type type)
        {
            if (type == typeof(JSValue)) return v;
            if (type == typeof(int)) return (int)v.Number;
            if (type == typeof(uint)) return (uint)(int)v.Number;
            if (type == typeof(double)) return v.Number;
            return v.HeapObjectOrNull;
        }

        /// <summary>
        /// A call (or a throw) in inlined functions: their interpreter frames,
        /// which native code never pushes, are pushed for it (as the IL's
        /// lazily pushed frames, MaglevCodeGenerator.EmitEnsureInlinedFrames)
        /// so that stack walks see them, and popped after it; a lazy deopt
        /// then builds them from the translation, as native code's eager deopts do.
        /// </summary>
        JSValue ExecuteInInlinedFrames(Isolate isolate, NativeManagedNode node, long[] ctx, int fp, JSValue[]? managed, out bool passed)
        {
            NativeInlinedFrame[] frames = node.InlinedFrames!;
            JSValue[] stack = isolate.RegisterStack;
            // The values first (the pushes write above the register stack top only).
            var functions = new JSFunction[frames.Length];
            var values = new JSValue[frames.Length][];
            for (int k = 0; k < frames.Length; k++)
            {
                NativeInlinedFrame f = frames[k];
                functions[k] = f.Function ?? (JSFunction)Value(f.Closure, stack, ctx, fp, managed).Object;
                var v = new JSValue[f.Argc + 2];
                v[0] = Value(f.Receiver, stack, ctx, fp, managed);
                for (int i = 0; i < f.Argc; i++) v[1 + i] = Value(f.Args[i], stack, ctx, fp, managed);
                if (f.NewTarget >= 0) v[f.Argc + 1] = Value(f.NewTarget, stack, ctx, fp, managed);
                values[k] = v;
            }
            int first = isolate.InterpreterFrameDepth;
            JSValue outerContext = stack[fp + InterpreterRuntime.kContextOffset];
            InterpreterRuntime.SetFramePc(isolate, fp, frames[0].CallOffset);
            try
            {
                for (int k = 0; k < frames.Length; k++)
                {
                    NativeInlinedFrame f = frames[k];
                    int ifp = MaglevBuiltins.EnterInlinedFrame(isolate, functions[k], f.Bytecode, f.Feedback, f.Argc, f.IsConstruct);
                    JSValue[] v = values[k];
                    JSValue.StoreSlot(ref stack[ifp + InterpreterRuntime.kReceiverOffset], v[0]);
                    for (int i = 0; i < f.Argc; i++) JSValue.StoreSlot(ref stack[ifp + InterpreterRuntime.kFirstArgumentOffset - i], v[1 + i]);
                    if (f.NewTarget >= 0) JSValue.StoreSlot(ref stack[ifp + f.NewTargetRegister], v[f.Argc + 1]);
                    InterpreterRuntime.SetFramePc(isolate, ifp, f.Pc);
                }
                return Execute(isolate, node, stack, ctx, fp, managed, out passed);
            }
            finally
            {
                MaglevBuiltins.LeaveInlinedFrame(isolate, first, outerContext);
            }
        }

        JSValue DeoptimizeLazy(Isolate isolate, ref InterpreterState state, long[] ctx, NativeManagedNode node, JSValue result, JSValue[]? managed)
        {
            JSValue[] scratch = isolate.MaglevDeoptScratch;
            int fp = state.Fp;
            DeoptPoint point = Code.DeoptPoints[node.LazyDeoptIndex];
            foreach ((int slot, int value) in DeoptValues[node.LazyDeoptIndex]) scratch[slot] = Value(value, isolate.RegisterStack, ctx, fp, managed);
            if (point.ResultScratchIndex >= 0) scratch[point.ResultScratchIndex] = result;
            Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, Code, node.LazyDeoptIndex, (int)Deoptimizer.DeoptimizeReason.kUnknown);
            return JSValue.Undefined;
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
        internal const int kPseudoFp = -InterpreterRuntime.kFirstArgumentOffset + MaglevFastCalls.kMaxArity;

        public int FastCallArity = -1;
        /// <summary>FastCallArity when native callers call the code directly (every return a number), else -1.</summary>
        public int DirectCallArity = -1;

        static JSValue[] PseudoFrame(Isolate isolate) => isolate.MaglevNativeFrame ??= GC.AllocateArray<JSValue>(kPseudoFp + 1, pinned: true);

        static nint PseudoFrameAddress(Isolate isolate) => isolate.MaglevNativeFrameAddress != 0
            ? isolate.MaglevNativeFrameAddress
            : isolate.MaglevNativeFrameAddress = NativeCode.AddressOf(PseudoFrame(isolate), 0);

        static JSValue[] Frame(Isolate isolate, JSFunction function, JSValue receiver)
        {
            JSValue[] frame = PseudoFrame(isolate);
            // (The same function is called again and again: skip the stores, and their write barriers, then.)
            ref JSValue closure = ref frame[kPseudoFp + InterpreterRuntime.kClosureOffset];
            if (!ReferenceEquals(closure._obj, function))
            {
                closure = JSValue.FromObject(function);
                frame[kPseudoFp + InterpreterRuntime.kContextOffset] = JSValue.FromObject(function.Context);
            }
            else if (!ReferenceEquals(frame[kPseudoFp + InterpreterRuntime.kContextOffset]._obj, function.Context))
            {
                frame[kPseudoFp + InterpreterRuntime.kContextOffset] = JSValue.FromObject(function.Context);
            }
            JSValue.StoreSlot(ref frame[kPseudoFp + InterpreterRuntime.kReceiverOffset], receiver);
            return frame;
        }

        static void Arg(JSValue[] frame, int i, JSValue value) => JSValue.StoreSlot(ref frame[kPseudoFp + InterpreterRuntime.kFirstArgumentOffset - i], value);

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
            // (The next depth's buffers, without taking the depth: frameless code
            // makes no calls and its exits run no code before they are done with them.)
            NativeActivation activation = ActivationAt(isolate, isolate.MaglevNativeDepth, ContextWords);
            long[] ctx = activation.Context;
            ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = 0;
            ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
            nint frameAddress = PseudoFrameAddress(isolate) + kPseudoFp * 16;
            long id = Enter(isolate, ctx, activation.ContextAddress, frameAddress, s_noSpillAddress);
            if (id == kNumberReturn) return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[MaglevNativeCodeGenerator.kReturnSlot]));
            return FramelessExits(isolate, function, argc, frame, ctx, activation.ContextAddress, frameAddress, id, activation);
        }

        /// <summary>
        /// Continues a native callee that a native caller called directly and
        /// that left by exit <paramref name="id"/> (MaglevNativeCodeGenerator,
        /// "Native-to-native calls"): <paramref name="ctx"/> is a pinned copy
        /// of its context, <paramref name="frame"/> its pseudo-frame rebuilt
        /// from the caller's values (pinned).
        /// </summary>
        internal JSValue ContinueFrameless(Isolate isolate, JSFunction function, int argc, JSValue[] frame, long[] ctx, long id)
        {
            Interlocked.Increment(ref s_continuedCallees);
            if (id == kNumberReturn) return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[MaglevNativeCodeGenerator.kReturnSlot]));
            return FramelessExits(isolate, function, argc, frame, ctx, NativeCode.AddressOf(ctx, 0), NativeCode.AddressOf(frame, 0) + kPseudoFp * 16, id,
                ActivationAt(isolate, isolate.MaglevNativeDepth, 0));
        }

        /// <summary>Native callees continued on the managed side (tests).</summary>
        internal static long s_continuedCallees;

        /// <summary>A new pinned pseudo-frame for ContinueFrameless.</summary>
        internal static JSValue[] NewPseudoFrame(JSFunction function, JSValue receiver)
        {
            JSValue[] frame = GC.AllocateArray<JSValue>(kPseudoFp + 1, pinned: true);
            frame[kPseudoFp + InterpreterRuntime.kClosureOffset] = JSValue.FromObject(function);
            frame[kPseudoFp + InterpreterRuntime.kContextOffset] = JSValue.FromObject(function.Context);
            frame[kPseudoFp + InterpreterRuntime.kReceiverOffset] = receiver;
            return frame;
        }

        internal static void SetPseudoFrameArgument(JSValue[] frame, int i, JSValue value) => Arg(frame, i, value);

        JSValue FramelessExits(Isolate isolate, JSFunction function, int argc, JSValue[] frame, long[] ctx, nint ctxAddress, nint frameAddress, long id,
            NativeActivation activation)
        {
            while (true)
            {
                NativeExit exit = Exits[id];
                switch (exit.Kind)
                {
                    case NativeExitKind.kReturn:
                        return Value(exit.Value, frame, ctx, kPseudoFp, null);
                    case NativeExitKind.kDeopt:
                        return DeoptimizeFrameless(isolate, function, argc, frame, ctx, exit);
                    case NativeExitKind.kDeoptAfterMigration:
                        MaglevBuiltins.TryMigrateInstanceAndMarkMapAsMigrationTarget(isolate, Value(exit.Value, frame, ctx, kPseudoFp, null));
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
                JSValue[] spill = SpillOf(activation, live.Length);
                for (int j = 0; j < live.Length; j++) spill[j] = Value(live[j], frame, ctx, kPseudoFp, null);
                ctx[MaglevNativeCodeGenerator.kEntryIdSlot] = exit.ResumeId;
                ctx[MaglevNativeCodeGenerator.kPollBudgetSlot] = kPollBudget;
                id = Enter(isolate, ctx, ctxAddress, frameAddress, AddressOfSpill(activation, spill));
                if (id == kNumberReturn) return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[MaglevNativeCodeGenerator.kReturnSlot]));
            }
        }

        JSValue DeoptimizeFrameless(Isolate isolate, JSFunction function, int argc, JSValue[] frame, long[] ctx, NativeExit exit)
        {
            JSValue[] scratch = isolate.MaglevDeoptScratch;
            for (int i = 0; i <= FastCallArity; i++)
            {
                scratch[i] = frame[kPseudoFp + InterpreterRuntime.kReceiverOffset - i];
            }
            foreach ((int slot, int value) in DeoptValues[exit.DeoptIndex]) scratch[slot] = Value(value, frame, ctx, kPseudoFp, null);
            return MaglevCalls.DeoptimizeFrameless(isolate, Code, exit.DeoptIndex, exit.Reason, function, argc, 0);
        }

        JSValue Deoptimize(Isolate isolate, ref InterpreterState state, long[] ctx, NativeExit exit, JSValue[]? managed)
        {
            JSValue[] scratch = isolate.MaglevDeoptScratch;
            int fp = state.Fp;
            foreach ((int slot, int value) in DeoptValues[exit.DeoptIndex]) scratch[slot] = Value(value, isolate.RegisterStack, ctx, fp, managed);
            Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, Code, exit.DeoptIndex, exit.Reason);
            return JSValue.Undefined;
        }

        /// <summary>The JSValue of value <paramref name="index"/> at an exit.</summary>
        internal JSValue Value(int index, JSValue[] frame, long[] ctx, int fp, JSValue[]? managed)
        {
            NativeValueInfo v = Values[index];
            // A value an exit kept before code that may change the heap ran.
            // (Only reload-rebuilt values have the flag: the fourth word of a tagged home.)
            if (v.SnapSlot >= 0 && v.Kind != NativeValueKind.kManaged && ctx[v.Home + 3] != 0) return managed![v.SnapSlot];
            if (v.KindAtLoad)
            {
                switch ((NativeTaggedKind)ctx[v.Home + 2])
                {
                    case NativeTaggedKind.kUndefined: return JSValue.Undefined;
                    case NativeTaggedKind.kNumber: return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[v.Home + 1]));
                    case NativeTaggedKind.kTrue: return JSValue.True;
                    case NativeTaggedKind.kFalse: return JSValue.False;
                    case NativeTaggedKind.kNull: return JSValue.Null;
                    case NativeTaggedKind.kTheHole: return JSValue.TheHole;
                }
            }
            switch (v.Kind)
            {
                case NativeValueKind.kManaged:
                    if (managed is null) throw new InvalidOperationException($"native code: the value of managed node n{v.NodeId} before the node ran");
                    return managed[v.SnapSlot];
                case NativeValueKind.kPropertyCell:
                    return ((PropertyCell)v.Constant.Object).Value;
                case NativeValueKind.kHoleToUndefined:
                {
                    JSValue input = Value(v.Input0, frame, ctx, fp, managed);
                    return input.IsTheHole ? JSValue.Undefined : input;
                }
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
                case NativeValueKind.kWrittenFrameSlot:
                    return frame[fp + v.FrameIndex];
                case NativeValueKind.kTaggedNumber:
                    return JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[v.Home + 1]));
                case NativeValueKind.kTaggedHoleyNumber:
                    return ctx[v.Home + 2] == (long)NativeTaggedKind.kUndefined
                        ? JSValue.Undefined
                        : JSValue.FromNumber(BitConverter.Int64BitsToDouble(ctx[v.Home + 1]));
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
                        _ => Value(v.Other, frame, ctx, fp, managed),
                    };
                case NativeValueKind.kElements:
                    return JSValue.FromObject(((JSObject)Value(v.Input0, frame, ctx, fp, managed).Object).Elements);
                case NativeValueKind.kFixedArrayElement:
                    return ((FixedArray)Value(v.Input0, frame, ctx, fp, managed).Object)._data[(int)Value(v.Input1, frame, ctx, fp, managed).Number];
                case NativeValueKind.kIdentity:
                    return Value(v.Input0, frame, ctx, fp, managed);
                case NativeValueKind.kTaggedField:
                    return MaglevBuiltins.LoadField(Value(v.Input0, frame, ctx, fp, managed), v.FrameIndex);
                case NativeValueKind.kContextSlot:
                {
                    var context = (Context)Value(v.Input0, frame, ctx, fp, managed).Object;
                    for (int d = 0; d < v.Input1; d++) context = (Context)context.Slots[(int)Context.Field.PREVIOUS_INDEX].Object;
                    return context.Slots[v.FrameIndex];
                }
                case NativeValueKind.kMap:
                    return JSValue.FromObject(MaglevBuiltins.MapOf(Value(v.Input0, frame, ctx, fp, managed)));
                default:
                    throw new InvalidOperationException("native value kind " + v.Kind);
            }
        }
    }
}
