// Entering and leaving Maglev code: the Maglev half of V8's call sequence
// (a JSFunction whose code is MAGLEV runs it), on-stack replacement from the
// interpreter at a JumpLoop (Runtime_CompileOptimizedOSR and the
// OSR entry of maglev-code-generator.cc), and the continuation in the
// interpreter after a deoptimization (V8's InterpreterEnterAtBytecode /
// InterpreterEnterAtNextBytecode builtins).
//
// Maglev code runs in the interpreter frame InterpreterExecution.EnterFrame
// built (architecture.md section 9.2): when it deoptimizes, the Deoptimizer
// has written the frames and the state, and the interpreter's Run loop
// continues there.
using V8Sharp.Interpreter;

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        /// <summary>The values a deopt exit hands the Deoptimizer (the optimized frame's translation values).</summary>
        public JSValue[] MaglevDeoptScratch = new JSValue[64];

        /// <summary>Set by the Deoptimizer: the Maglev code returned to continue in the interpreter.</summary>
        public bool MaglevDeoptPending;

        /// <summary>Some function has Maglev code (enables the interpreter's OSR check).</summary>
        public bool MayHaveMaglevCode;
    }
}

namespace V8Sharp.Maglev
{
    public static class MaglevExecution
    {
        /// <summary>
        /// Runs <paramref name="code"/> for the frame <paramref name="state"/>
        /// describes; after a deoptimization, the interpreter continues the
        /// materialised frames.
        /// </summary>
        public static JSValue Run(Isolate isolate, ref InterpreterState state, MaglevCode code)
        {
            ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[state.FrameIndex];
            frame.IsBaseline = false;
            frame.IsMaglev = true;
            JSValue result = code.Entry(isolate, ref state);
            if (!isolate.MaglevDeoptPending) return result;
            isolate.MaglevDeoptPending = false;
            isolate.InterpreterFrames[state.FrameIndex - 0].IsMaglev = false;
            ClearMaglevFlags(isolate, ref state);
            // InterpreterEnterAtBytecode: the frames continue in the interpreter.
            return InterpreterExecution.Run(isolate, ref state);
        }

        static void ClearMaglevFlags(Isolate isolate, ref InterpreterState state)
        {
            InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
            for (int i = state.BaseFrameIndex; i <= state.FrameIndex; i++) frames[i].IsMaglev = false;
        }

        /// <summary>
        /// OSR into Maglev code from an interpreted frame at the loop header the
        /// OSR code was compiled for (state.Pc).
        /// </summary>
        public static JSValue RunOsr(Isolate isolate, ref InterpreterState state, MaglevCode code)
        {
            // The OSR code runs this frame (and its inlined callees) from here:
            // for the time it runs, the frame is the base frame of the state, so
            // a deoptimization continues it in a nested interpreter loop that
            // returns at its Return (an inline frame of an outer loop has
            // InlineCall set, which would make that Return pop it and continue
            // the caller in the nested loop). The outer loop pops it afterwards.
            int baseIndex = state.BaseFrameIndex;
            int frameIndex = state.FrameIndex;
            state.BaseFrameIndex = frameIndex;
            InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
            bool inlineCall = frames[frameIndex].InlineCall;
            frames[frameIndex].InlineCall = false;
            frames[frameIndex].IsMaglev = true;
            try
            {
                // The code reads the frame's registers; the interpreter keeps the context in the state.
                isolate.RegisterStack[state.Fp + InterpreterRuntime.kContextOffset] = state.Context;
                JSValue result = code.Entry(isolate, ref state);
                if (isolate.MaglevDeoptPending)
                {
                    isolate.MaglevDeoptPending = false;
                    ClearMaglevFlags(isolate, ref state);
                    result = InterpreterExecution.Run(isolate, ref state);
                }
                return result;
            }
            finally
            {
                frames = isolate.InterpreterFrames;
                frames[frameIndex].IsMaglev = false;
                frames[frameIndex].InlineCall = inlineCall;
                state.BaseFrameIndex = baseIndex;
            }
        }

        /// <summary>
        /// The OSR check of the interpreter's JumpLoop (InterpreterAssembler::
        /// OnStackReplacement / Runtime_CompileOptimizedOSR), run at its budget
        /// interrupt: when the function is hot enough to tier up but this frame
        /// is stuck in a loop, compile (or find) OSR code for this JumpLoop.
        /// </summary>
        public static MaglevCode? TryGetOsrCode(Isolate isolate, JSFunction function, FeedbackVector vector, BytecodeArray bytecode,
            int jumpLoopOffset)
        {
            if (!isolate.UseOptimizer || !isolate.Flags.use_osr || !isolate.Flags.maglev_osr) return null;
            if (vector.MaglevOsrCode is { } cache && cache.TryGetValue(jumpLoopOffset, out MaglevCode? cached))
            {
                return cached.MarkedForDeoptimization ? null : cached;
            }
            // The loop depth of this JumpLoop (operand 1): OSR into the loop when
            // the urgency exceeds it (V8: the osr_state's urgency vs loop depth).
            int loopDepth = bytecode.Bytecodes[jumpLoopOffset + 2];
            if (vector.OsrUrgency <= loopDepth) return null;
            if (MaglevCompiler.OptimizationDisabled(function.Shared)) return null;
            if (isolate.Flags.trace_osr) Console.WriteLine($"[OSR - compiling {function.Shared.Name()} at JumpLoop {jumpLoopOffset}]");
            MaglevCode? code = MaglevCompiler.Compile(isolate, function, jumpLoopOffset);
            if (code is null) return null;
            MaglevCompiler.InstallCode(isolate, code);
            return code;
        }
    }
}
