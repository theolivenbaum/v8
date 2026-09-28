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
//   fp - 4                   argc                 (Register::argument_count)
//   fp - 3, fp - 2           bytecode array / offset (kept in the frame record)
//   fp - 1                   the feedback vector  (Register::feedback_vector)
//   fp + 0 ...               the register file r0, r1, ...
//
// Besides the slots, each frame has a record in Isolate.InterpreterFrames
// (function, fp, current bytecode offset ...) that the stack walker reads; a
// builtin called from JavaScript pushes a record too (V8's builtin exit
// frames), so builtins appear in Error.stack as in V8.
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

    /// <summary>A frame record: what the stack walker needs to know about a live frame.</summary>
    public struct InterpreterFrameRecord
    {
        public JSFunction Function;
        /// <summary>The bytecode of an interpreted frame (null for builtin frames).</summary>
        public BytecodeArray? Bytecode;
        /// <summary>The receiver of a builtin frame (interpreted frames keep it at fp - 9).</summary>
        public JSValue Receiver;
        /// <summary>The frame pointer (index into the register stack) of an interpreted frame.</summary>
        public int Fp;
        /// <summary>The current bytecode offset of an interpreted frame.</summary>
        public int Pc;
        /// <summary>The actual argument count (without the receiver).</summary>
        public int Argc;
        public InterpreterFrameKind Kind;
        /// <summary>The frame was entered by [[Construct]].</summary>
        public bool IsConstructor;
        /// <summary>
        /// An interpreted frame that runs baseline (Sparkplug) code: V8's
        /// BaselineFrame, which has the interpreter frame's layout.
        /// </summary>
        public bool IsBaseline;
        /// <summary>
        /// The frame was entered by a call from the dispatch loop of its caller
        /// without a new .NET frame (InterpreterInlineCalls); Return resumes the
        /// caller in the same loop.
        /// </summary>
        public bool InlineCall;
        /// <summary>An inline frame: the caller's bytecode offset to resume at.</summary>
        public int ReturnPc;
        /// <summary>An inline frame: the register stack top before its arguments were pushed.</summary>
        public int RegisterStart;
    }

    public sealed partial class Isolate
    {
        /// <summary>The maximum number of nested JavaScript frames before V8's stack overflow RangeError.</summary>
        public const int kMaxInterpreterFrames = 1 << 16;

        InterpreterFrameRecord[]? _interpreterFrames;

        /// <summary>The frame records of the live frames; index 0 is the outermost.</summary>
        public InterpreterFrameRecord[] InterpreterFrames => _interpreterFrames ??= new InterpreterFrameRecord[kMaxInterpreterFrames];

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
            InterpreterFrameRecord[] frames = _interpreterFrames!;
            for (int i = InterpreterFrameDepth - 1; i >= depth; i--) frames[i] = default;
            InterpreterFrameDepth = depth;
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
            arguments = InterpreterRuntime.GetFrameArguments(isolate, frame.Fp, frame.Argc);
            return true;
        }

        internal static JavaScriptFrameSummary Summarize(Isolate isolate, ref InterpreterFrameRecord frame)
        {
            if (frame.Kind == InterpreterFrameKind.Interpreted)
            {
                JSValue receiver = isolate.RegisterStack[frame.Fp + InterpreterRuntime.kReceiverOffset];
                BytecodeArray bytecode = frame.Bytecode!;
                int position = InterpreterRuntime.SourcePositionAt(isolate, frame.Function.Shared, bytecode, frame.Pc);
                return new JavaScriptFrameSummary(receiver, frame.Function, frame.Pc, position, frame.IsConstructor);
            }
            return new JavaScriptFrameSummary(frame.Receiver, frame.Function, 0, 0, frame.IsConstructor);
        }
    }
}
