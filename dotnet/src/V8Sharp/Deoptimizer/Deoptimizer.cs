// Port of src/deoptimizer/deoptimizer.{h,cc} (and the frame reconstruction
// of translated-state.cc) for Maglev code: a deopt exit of the optimized
// code materialises the interpreter frames its translation describes and
// execution continues in the interpreter.
//
// V8 builds new interpreter frames on the machine stack from the optimized
// frame's slots. A V8Sharp optimized frame already is an interpreter frame
// (InterpreterExecution.EnterFrame built it; Maglev code keeps the values in
// IL locals and writes the frame only here), and the frames of inlined
// functions are pushed by EnterInlinedFrame; so materialising means writing
// the translation's values (copied by the deopt exit into the isolate's
// scratch buffer) into those frames' registers, making the inlined frames
// interpreter inline frames (InterpreterInlineCalls: returning from one
// resumes its caller in the same dispatch loop) and pointing the
// InterpreterState at the innermost frame. MaglevExecution.Run then runs the
// interpreter from there.
//
// Eager deopts continue at the bytecode whose check failed (it runs again in
// the interpreter); lazy deopts (the code was invalidated while a call it
// made was running) continue after the call, with the call's result in its
// result location.
using V8Sharp.Interpreter;
using V8Sharp.Maglev;

namespace V8Sharp.Deoptimizer;

public static class Deoptimizer
{
    /// <summary>
    /// Deoptimizer::New + DoComputeOutputFrames for deopt exit
    /// <paramref name="index"/> of <paramref name="code"/>, whose frame
    /// <paramref name="state"/> describes. The values are in the isolate's
    /// deopt scratch buffer.
    /// </summary>
    public static void Deoptimize(Isolate isolate, ref InterpreterState state, MaglevCode code, int index)
    {
        DeoptPoint point = code.DeoptPoints[index];
        point.Count++;
        JSValue[] scratch = isolate.MaglevDeoptScratch;
        JSValue[] stack = isolate.RegisterStack;
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        int baseIndex = state.FrameIndex;
        DeoptFrameData[] translation = point.Frames;

        if (isolate.Flags.trace_deopt || isolate.Flags.trace_deopt_verbose) TraceDeopt(isolate, code, point);

        for (int i = 0; i < translation.Length; i++)
        {
            DeoptFrameData f = translation[i];
            bool top = i == translation.Length - 1;
            int recordIndex = baseIndex + f.InliningDepth;
            ref InterpreterFrameRecord record = ref frames[recordIndex];
            int fp = record.Fp;
            JSValue accumulator = JSValue.Undefined;
            Context? context = null;
            Register[] registers = f.Registers;
            for (int k = 0; k < registers.Length; k++)
            {
                JSValue value = scratch[f.ScratchStart + k];
                Register r = registers[k];
                if (r == Register.VirtualAccumulator())
                {
                    accumulator = value;
                }
                else if (r == Register.CurrentContext())
                {
                    stack[fp + InterpreterRuntime.kContextOffset] = value;
                    context = value.As<Context>();
                }
                else
                {
                    stack[fp + r.Index] = value;
                }
            }
            context ??= stack[fp + InterpreterRuntime.kContextOffset].As<Context>();
            record.IsBaseline = false;
            if (i > 0) record.InlineCall = true;
            if (!top)
            {
                // A caller of an inlined function: it is at its call bytecode and
                // continues after it with the callee's result.
                record.Pc = CursorOf(f.Bytecode, f.BytecodeOffset);
                record.ReturnPc = f.NextOffset;
                continue;
            }

            int pc = f.BytecodeOffset;
            if (point.Kind == DeoptimizeKind.kLazy)
            {
                pc = f.NextOffset;
                if (point.ResultScratchIndex >= 0)
                {
                    JSValue result = scratch[point.ResultScratchIndex];
                    if (point.ResultLocation == Register.VirtualAccumulator()) accumulator = result;
                    else stack[fp + point.ResultLocation.Index] = result;
                }
            }
            record.Pc = CursorOf(f.Bytecode, pc);
            state.Function = f.Function;
            state.Bytecode = f.Bytecode;
            state.FeedbackVector = f.FeedbackVector;
            state.Context = context;
            state.Accumulator = accumulator;
            state.Pc = pc;
            state.Fp = fp;
            state.FrameIndex = recordIndex;
            state.Argc = record.Argc;
            isolate.Context = context;
            if (f.Bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, f.Bytecode);
        }

        if (point.Kind == DeoptimizeKind.kEager)
        {
            // An eager deopt invalidates the code (V8: Deoptimizer::DeoptimizeFunction
            // via the kEagerDeopt lazy reason); the function is re-optimized later
            // with the feedback the interpreter collects meanwhile.
            MaglevCompiler.InvalidateCode(isolate, code, LazyDeoptimizeReason.kEagerDeopt);
        }
        isolate.MaglevDeoptPending = true;
    }

    /// <summary>The bytecode offset after any prefix (what a frame record holds).</summary>
    static int CursorOf(BytecodeArray bytecode, int offset)
    {
        Bytecode bc = Bytecodes.FromByte(bytecode.Bytecodes[offset]);
        return Bytecodes.IsPrefixScalingBytecode(bc) ? offset + 1 : offset;
    }

    static void TraceDeopt(Isolate isolate, MaglevCode code, DeoptPoint point)
    {
        DeoptFrameData top = point.Frames[^1];
        string kind = point.Kind == DeoptimizeKind.kEager ? "deopt-eager" : "deopt-lazy";
        string reason = point.Kind == DeoptimizeKind.kEager ? DeoptimizeReasons.ToString(point.Reason) : "(code invalidated)";
        Console.WriteLine($"[bailout (kind: {kind}, reason: {reason}): begin. deoptimizing {code.SharedFunctionInfo.Name()}, " +
                          $"bytecode offset {top.BytecodeOffset} in {top.Function.Shared.Name()}, frames {point.Frames.Length}]");
    }
}
