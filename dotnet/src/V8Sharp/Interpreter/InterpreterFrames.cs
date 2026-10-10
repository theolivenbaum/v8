// The interpreter's stack frames (the parts of src/execution/frames.{h,cc}
// that describe interpreted and builtin frames: InterpretedFrame,
// BuiltinExitFrame, JavaScriptStackFrameIterator and FrameSummary).
//
// A V8 interpreter frame lives on the machine stack; V8Sharp's lives on the
// isolate's register stack (architecture.md section 7). Its layout mirrors
// V8's x64 InterpreterFrameConstants so that a register operand is simply an
// offset from the frame pointer (Register index = fp-relative slot):
//
//   fp - 9 - n ... fp - 10   arguments n .. 1 (parameter registers a(n-1)..a0)
//   fp - 9                   receiver (<this>)
//   fp - 8, fp - 7           (caller fp / pc in V8; unused)
//   fp - 6                   the current context  (Register::current_context)
//   fp - 5                   the closure          (Register::function_closure)
//   fp - 4                   argc, a raw int      (Register::argument_count)
//   fp - 3                   the bytecode array   (Register::bytecode_array)
//   fp - 2                   the bytecode offset, a raw int (Register::bytecode_offset)
//   fp - 1                   the feedback vector  (Register::feedback_vector)
//   fp + 0 ...               the register file r0, r1, ...
//
// Each value is held once, in its slot. A raw int slot has no object half
// (undefined's) and the int in its payload. Besides the slots, each frame
// has a small record in Isolate.InterpreterFrames (fp, flags, the inline
// call's return offset) that the stack walker indexes; a builtin called
// from JavaScript pushes a record too (V8's builtin exit frames), so
// builtins appear in Error.stack as in V8.
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp
{
    /// <summary>The kinds of frames the stack walker knows.</summary>
    public enum InterpreterFrameKind : byte
    {
        /// <summary>An interpreted (bytecode) frame.</summary>
        Interpreted,
        /// <summary>A builtin called with JavaScript arguments (V8's BuiltinExitFrame / JavaScriptBuiltinContinuation).</summary>
        Builtin,
    }

    /// <summary>The bits of <see cref="InterpreterFrameRecord.Flags"/>.</summary>
    [Flags]
    public enum InterpreterFrameFlags : byte
    {
        None = 0,
        /// <summary>A builtin frame (<see cref="InterpreterFrameKind.Builtin"/>); else an interpreted frame.</summary>
        Builtin = 1,
        /// <summary>The frame was entered by [[Construct]].</summary>
        Constructor = 2,
        /// <summary>An interpreted frame that runs baseline (Sparkplug) code: V8's BaselineFrame, which has the interpreter frame's layout.</summary>
        Baseline = 4,
        /// <summary>An interpreted frame that runs Maglev code (V8's MaglevFrame).</summary>
        Maglev = 8,
        /// <summary>
        /// The frame was entered by a call from the dispatch loop of its caller
        /// without a new .NET frame (InterpreterInlineCalls); Return resumes the
        /// caller in the same loop.
        /// </summary>
        InlineCall = 16,
        /// <summary>
        /// An optimized frame whose interpreter frame is not materialized (V8's
        /// MaglevFrame: the values live in the optimized code): the record's
        /// <see cref="InterpreterFrameRecord.Activation"/> holds the closure,
        /// the bytecode offset, the receiver and the arguments
        /// (Maglev.MaglevActivation), and its register window at Fp is reserved
        /// but unwritten until a deopt materializes it (MaglevCalls.MaterializeLazyFrame).
        /// </summary>
        Lazy = 32,
    }

    /// <summary>
    /// A frame record: what the stack walker needs besides the frame's fixed
    /// slots. An interpreted frame keeps its function, context, bytecode
    /// array, bytecode offset, argument count and feedback vector in the
    /// fixed slots of its register stack window (V8's interpreter frame;
    /// InterpreterRuntime.Frame*), each held once; the record has the frame
    /// pointer, the flags and the inline-call return state. A builtin frame
    /// has no register window and keeps its function and receiver here.
    /// </summary>
    public struct InterpreterFrameRecord
    {
        /// <summary>The frame pointer (index into the register stack) of an interpreted frame.</summary>
        public int Fp;
        /// <summary>The caller's bytecode offset to resume at when the frame above (entered inline) returns.</summary>
        public int ReturnPc;
        /// <summary>An inline frame: the register stack top before its arguments were pushed.</summary>
        public int RegisterStart;
        public InterpreterFrameFlags Flags;
        /// <summary>The function of a builtin frame (interpreted frames keep theirs at fp - 5).</summary>
        public JSFunction? BuiltinFunction;
        /// <summary>The receiver of a builtin frame (interpreted frames keep it at fp - 9).</summary>
        public JSValue BuiltinReceiver;
        /// <summary>
        /// A lazy optimized frame (<see cref="InterpreterFrameFlags.Lazy"/>): the
        /// address of its Maglev.MaglevActivation on the .NET stack.
        /// </summary>
        public nint Activation;

        /// <summary>An optimized frame without its interpreter frame (<see cref="InterpreterFrameFlags.Lazy"/>).</summary>
        public readonly bool IsLazy => (Flags & InterpreterFrameFlags.Lazy) != 0;

        public InterpreterFrameKind Kind
        {
            readonly get => (Flags & InterpreterFrameFlags.Builtin) != 0 ? InterpreterFrameKind.Builtin : InterpreterFrameKind.Interpreted;
            set => Set(InterpreterFrameFlags.Builtin, value == InterpreterFrameKind.Builtin);
        }

        /// <summary>The frame was entered by [[Construct]].</summary>
        public bool IsConstructor
        {
            readonly get => (Flags & InterpreterFrameFlags.Constructor) != 0;
            set => Set(InterpreterFrameFlags.Constructor, value);
        }

        /// <summary>An interpreted frame that runs baseline (Sparkplug) code.</summary>
        public bool IsBaseline
        {
            readonly get => (Flags & InterpreterFrameFlags.Baseline) != 0;
            set => Set(InterpreterFrameFlags.Baseline, value);
        }

        /// <summary>An interpreted frame that runs Maglev code.</summary>
        public bool IsMaglev
        {
            readonly get => (Flags & InterpreterFrameFlags.Maglev) != 0;
            set => Set(InterpreterFrameFlags.Maglev, value);
        }

        /// <summary>The frame was entered inline from its caller's dispatch loop.</summary>
        public bool InlineCall
        {
            readonly get => (Flags & InterpreterFrameFlags.InlineCall) != 0;
            set => Set(InterpreterFrameFlags.InlineCall, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Set(InterpreterFrameFlags flag, bool value) => Flags = value ? Flags | flag : Flags & ~flag;

        /// <summary>The frame's function: the closure slot of an interpreted frame, the record's of a builtin frame.</summary>
        public readonly JSFunction GetFunction(Isolate isolate) =>
            (Flags & (InterpreterFrameFlags.Builtin | InterpreterFrameFlags.Lazy)) == 0 ? InterpreterRuntime.FrameFunction(isolate, Fp)
            : (Flags & InterpreterFrameFlags.Builtin) != 0 ? BuiltinFunction!
            : Maglev.MaglevActivation.At(Activation).Function;

        /// <summary>The bytecode of an interpreted frame (its bytecode array slot).</summary>
        public readonly BytecodeArray GetBytecode(Isolate isolate) =>
            (Flags & InterpreterFrameFlags.Lazy) != 0
                ? (BytecodeArray)Maglev.MaglevActivation.At(Activation).Function.Shared.FunctionData!
                : InterpreterRuntime.FrameBytecode(isolate, Fp);

        /// <summary>The current bytecode offset of an interpreted frame (its bytecode offset slot); 0 for a builtin frame.</summary>
        public readonly int GetPc(Isolate isolate) =>
            (Flags & (InterpreterFrameFlags.Builtin | InterpreterFrameFlags.Lazy)) == 0 ? InterpreterRuntime.FramePc(isolate, Fp)
            : (Flags & InterpreterFrameFlags.Builtin) != 0 ? 0
            : Maglev.MaglevActivation.At(Activation).Pc;

        /// <summary>The actual argument count of an interpreted frame (its argument count slot).</summary>
        public readonly int GetArgc(Isolate isolate) =>
            (Flags & InterpreterFrameFlags.Lazy) != 0 ? Maglev.MaglevActivation.At(Activation).Argc : InterpreterRuntime.FrameArgc(isolate, Fp);

        /// <summary>The receiver of an interpreted frame (its receiver slot, or a lazy frame's activation).</summary>
        public readonly JSValue GetReceiver(Isolate isolate) =>
            (Flags & InterpreterFrameFlags.Lazy) != 0
                ? Maglev.MaglevActivation.At(Activation).Receiver
                : isolate.RegisterStack[Fp + InterpreterRuntime.kReceiverOffset];
    }

    public sealed partial class Isolate
    {
        /// <summary>The maximum number of nested JavaScript frames before V8's stack overflow RangeError.</summary>
        public const int kMaxInterpreterFrames = 1 << 16;

        // Pinned (the pinned object heap) like the register stack: see Isolate.RegisterStack.
        readonly InterpreterFrameRecord[] _interpreterFrames =
            GC.AllocateArray<InterpreterFrameRecord>(kMaxInterpreterFrames, pinned: true);

        /// <summary>The frame records of the live frames; index 0 is the outermost.</summary>
        public InterpreterFrameRecord[] InterpreterFrames
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _interpreterFrames;
        }

        /// <summary>The number of live frame records.</summary>
        public int InterpreterFrameDepth;

        /// <summary>
        /// Isolate::pending_message: the message object of the exception being
        /// handled, saved and restored by try/finally (SetPendingMessage).
        /// The hole means "no message".
        /// </summary>
        public JSValue PendingMessage = JSValue.TheHole;

        /// <summary>The interpreter's per-isolate state (interrupt budgets, caches).</summary>
        public InterpreterIsolateData? InterpreterData;

        partial void InitializeInterpreter();

        /// <summary>Pushes a frame record; throws V8's stack overflow RangeError when too deep.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref InterpreterFrameRecord PushFrame()
        {
            int depth = InterpreterFrameDepth;
            InterpreterFrameRecord[] frames = InterpreterFrames;
            if ((uint)depth >= (uint)frames.Length) StackOverflow();
            InterpreterFrameDepth = depth + 1;
            return ref frames[depth];
        }

        /// <summary>Pops frame records down to <paramref name="depth"/>, clearing them for the GC.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PopFramesTo(int depth)
        {
            InterpreterFrameRecord[] frames = _interpreterFrames;
            for (int i = InterpreterFrameDepth - 1; i >= depth; i--) frames[i] = default;
            InterpreterFrameDepth = depth;
        }

        /// <summary>
        /// Clears the records above the live frames and the register stack
        /// above its top. Popped inline frames leave their slots (the closure,
        /// bytecode, feedback vector ...) above the stack top (deviations.md,
        /// Interpreter), which would keep a dead closure reachable across an
        /// explicit collection (gc(), WeakRef tests); CollectGarbage drops them.
        /// </summary>
        internal void ClearStaleFrameRecords()
        {
            InterpreterFrameRecord[] frames = _interpreterFrames;
            // Popped records need not be contiguous (PopFramesTo clears its
            // records), so clear the whole tail; this runs only on explicit GCs.
            frames.AsSpan(InterpreterFrameDepth).Clear();
            // The same for the values popped inline frames left above the stack top.
            int top = RegisterStackTop;
            if (RegisterStackDirtyEnd > top)
            {
                RegisterStack.AsSpan(top, RegisterStackDirtyEnd - top).Clear();
                RegisterStackDirtyEnd = top;
            }
        }
    }
}

namespace V8Sharp.Interpreter
{
    /// <summary>Per-isolate interpreter state.</summary>
    public sealed class InterpreterIsolateData
    {
    }

    /// <summary>
    /// The stack walker over interpreter frames (V8's JavaScriptStackFrameIterator
    /// + FrameSummary), registered as <see cref="Isolate.Frames"/>.
    /// </summary>
    public sealed class InterpreterFrames(Isolate isolate) : IJavaScriptFrames
    {
        /// <summary>Frame index <paramref name="index"/> counted from the top (0 is the innermost frame).</summary>
        public bool TryGetFrame(int index, out JavaScriptFrameSummary summary)
        {
            int depth = isolate.InterpreterFrameDepth;
            int i = depth - 1 - index;
            if (i < 0 || index < 0)
            {
                summary = default;
                return false;
            }
            ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[i];
            summary = Summarize(isolate, ref frame);
            return true;
        }

        public bool TryGetArguments(int index, out JSValue[] arguments)
        {
            int depth = isolate.InterpreterFrameDepth;
            int i = depth - 1 - index;
            if (i < 0 || index < 0)
            {
                arguments = [];
                return false;
            }
            ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[i];
            if (frame.Kind != InterpreterFrameKind.Interpreted)
            {
                arguments = [];
                return false;
            }
            arguments = frame.IsLazy
                ? Maglev.MaglevActivation.GetArguments(frame.Activation)
                : InterpreterRuntime.GetFrameArguments(isolate, frame.Fp, frame.GetArgc(isolate));
            return true;
        }

        internal static JavaScriptFrameSummary Summarize(Isolate isolate, ref InterpreterFrameRecord frame)
        {
            if (frame.Kind == InterpreterFrameKind.Interpreted)
            {
                JSValue receiver = frame.GetReceiver(isolate);
                BytecodeArray bytecode = frame.GetBytecode(isolate);
                JSFunction function = frame.GetFunction(isolate);
                int pc = frame.GetPc(isolate);
                int position = InterpreterRuntime.SourcePositionAt(isolate, function.Shared, bytecode, pc);
                return new JavaScriptFrameSummary(receiver, function, pc, position, frame.IsConstructor);
            }
            return new JavaScriptFrameSummary(frame.BuiltinReceiver, frame.BuiltinFunction!, 0, 0, frame.IsConstructor);
        }
    }
}
