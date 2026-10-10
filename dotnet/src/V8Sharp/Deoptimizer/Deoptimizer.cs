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
    /// <summary>The feedback index of a deopt exit's reason that marks a hoisted untagging check.</summary>
    internal const int kHoistedUntaggingFeedback = 0x7FFF;

    /// <summary>
    /// Deoptimizer::New + DoComputeOutputFrames for deopt exit
    /// <paramref name="index"/> of <paramref name="code"/>, whose frame
    /// <paramref name="state"/> describes. The values are in the isolate's
    /// deopt scratch buffer.
    /// </summary>
    public static void Deoptimize(Isolate isolate, ref InterpreterState state, MaglevCode code, int index, int reason)
    {
        DeoptPoint point = code.DeoptPoints[index];
        // The exit of a frame state is shared by its checks: the reason is the
        // failed check's, and its high bits name the call feedback the check
        // speculated on (Deoptimizer: feedback_to_update), which now
        // disallows speculation.
        point.Reason = (DeoptimizeReason)(reason & 0xFFFF);
        if (reason >> 16 == kHoistedUntaggingFeedback)
        {
            // V8Sharp: a speculatively hoisted untagging failed; the next
            // compilation keeps the loop phi tagged.
            MaglevCompiler.DisableSpeculativeUntagging(code.SharedFunctionInfo);
        }
        else if (reason >> 16 is > 0 and int feedback)
        {
            (FeedbackVector vector, int slot) = code.SpeculationFeedback[feedback - 1];
            // TranslatedState::DoUpdateFeedback.
            var nexus = new FeedbackNexus(isolate, vector, slot);
            if (point.Reason == DeoptimizeReason.kOutOfBounds)
            {
                nexus.SetSpeculationMode(nexus.GetSpeculationMode() == SpeculationMode.kAllowSpeculation
                    ? SpeculationMode.kDisallowBoundsCheckSpeculation
                    : SpeculationMode.kDisallowSpeculation);
            }
            else
            {
                nexus.SetSpeculationMode(SpeculationMode.kDisallowSpeculation);
            }
        }
        point.Count++;
        JSValue[] scratch = isolate.MaglevDeoptScratch;
        JSValue[] stack = isolate.RegisterStack;
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        int baseIndex = state.FrameIndex;
        DeoptFrameData[] translation = point.Frames;
        // TranslatedState::MaterializeCapturedObjects: the elided allocations
        // the frames hold, once each (a register of any frame that holds one
        // gets the same object).
        JSValue[]? captured = point.CapturedObjects is { } capturedObjects
            ? MaterializeCapturedObjects(capturedObjects, scratch)
            : null;

        if (isolate.Flags.trace_deopt || isolate.Flags.trace_deopt_verbose) TraceDeopt(isolate, code, point);

        for (int i = 0; i < translation.Length; i++)
        {
            DeoptFrameData f = translation[i];
            // An inlined closure of a feedback cell: the code spilled it.
            JSFunction function = f.Function ?? (JSFunction)scratch[f.ClosureScratchSlot].Object;
            bool top = i == translation.Length - 1;
            int recordIndex = baseIndex + f.InliningDepth;
            if (i > 0 && isolate.InterpreterFrameDepth <= recordIndex)
            {
                // A lazily pushed inlined frame the code had not needed yet.
                MaglevBuiltins.EnterInlinedFrame(isolate, function, f.Bytecode, f.FeedbackVector, f.Argc, f.IsConstruct);
            }
            ref InterpreterFrameRecord record = ref frames[recordIndex];
            // A lazy frame (MaglevCalls, "Lazy optimized frames"): its
            // interpreter frame is built from the activation first.
            if (record.IsLazy) MaglevCalls.MaterializeLazyFrame(isolate, ref record, f.FeedbackVector);
            int fp = record.Fp;
            JSValue accumulator = JSValue.Undefined;
            Context? context = null;
            Register[] registers = f.Registers;
            int[] slots = f.ScratchSlots;
            ArgumentsObjectKind[]? materialize = f.Materialize;
            // The registers, then the elided arguments objects (translated-state.cc's
            // captured objects), from the frame's arguments: the translation
            // writes those of a frame pushed here (an inlined frame's arguments
            // beyond its formal parameters among them). One object, whichever
            // registers hold it.
            JSValue materialized = default;
            bool materializedOnce = false;
            for (int pass = 0; pass < (materialize is null ? 1 : 2); pass++)
            {
                for (int k = 0; k < registers.Length; k++)
                {
                    bool isArguments = materialize is not null && materialize[k] != ArgumentsObjectKind.None;
                    if (isArguments != (pass == 1)) continue;
                    JSValue value;
                    if (isArguments)
                    {
                        if (!materializedOnce)
                        {
                            var frameContext = stack[fp + InterpreterRuntime.kContextOffset].As<Context>();
                            materialized = materialize![k] == ArgumentsObjectKind.Mapped
                                ? InterpreterArguments.NewSloppyArguments(isolate, function, frameContext, fp, InterpreterRuntime.FrameArgc(isolate, fp))
                                : InterpreterArguments.NewStrictArguments(isolate, function, fp, InterpreterRuntime.FrameArgc(isolate, fp));
                            materializedOnce = true;
                        }
                        value = materialized;
                    }
                    else
                    {
                        // A spilled value, a literal of the translation, or a captured object.
                        value = slots[k] >= 0 ? scratch[slots[k]]
                            : f.IsConstant is { } isConstant && isConstant[k] ? f.Constants![k]
                            : captured![f.Captured![k]];
                    }
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
            }
            context ??= stack[fp + InterpreterRuntime.kContextOffset].As<Context>();
            // The fixed slots of the interpreter frame (the entries wrote them;
            // the frame now runs this translation's function and bytecode).
            stack[fp + InterpreterRuntime.kClosureOffset] = function;
            stack[fp + InterpreterRuntime.kFeedbackVectorOffset] = f.FeedbackVector is null ? JSValue.Undefined : f.FeedbackVector;
            stack[fp + InterpreterRuntime.kBytecodeArrayOffset] = f.Bytecode;
            record.IsBaseline = false;
            if (i > 0) record.InlineCall = true;
            if (!top)
            {
                // A caller of an inlined function: it is at its call bytecode and
                // continues after it with the callee's result.
                InterpreterRuntime.SetFramePc(isolate, fp, CursorOf(f.Bytecode, f.BytecodeOffset));
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
            InterpreterRuntime.SetFramePc(isolate, fp, CursorOf(f.Bytecode, pc));
            state.Accumulator = accumulator;
            state.Pc = pc;
            state.Fp = fp;
            state.FrameIndex = recordIndex;
            isolate.Context = context;
            if (f.Bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, f.Bytecode);
        }

        if (point.Kind == DeoptimizeKind.kEager && !IsDeoptimizationWithoutCodeInvalidation(point.Reason) &&
            !code.MarkedForDeoptimization)
        {
            // Deoptimizer::Deoptimizer: an eager deopt invalidates the code when it
            // is the function's active code; OSR code only when the exit is inside
            // the OSR'd loop. The function is re-optimized later with the
            // feedback the interpreter collects meanwhile.
            // V8Sharp: OSR code whose exit after the loop is taken again is
            // invalidated as well (V8 keeps it, but its function soon runs
            // function-entry code instead; a function whose own compile
            // failed re-entered the same OSR code and deoptimized at the
            // same exit on every call, as Octane zlib's inflate did).
            bool invalidate = code.OsrOffset < 0
                ? ReferenceEquals(code.FeedbackVector.MaglevCode, code)
                : DeoptExitIsInsideOsrLoop(code, translation[0].BytecodeOffset) || point.Count >= 2;
            if (invalidate) MaglevCompiler.InvalidateCode(isolate, code, LazyDeoptimizeReason.kEagerDeopt);
        }
        isolate.MaglevDeoptPending = true;
    }

    /// <summary>
    /// The objects of a translation's captured objects (elided
    /// InlinedAllocations): an ordinary object of the allocated map's
    /// in-object slot class with the map and fields of the deopt point.
    /// </summary>
    static JSValue[] MaterializeCapturedObjects(CapturedObjectData[] objects, JSValue[] scratch)
    {
        var result = new JSValue[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            CapturedObjectData data = objects[i];
            JSObject obj = JSObject.NewWithInObjectSlots(data.AllocatedMap);
            obj.Map = data.Map;
            for (int f = 0; f < data.FieldSlots.Length; f++)
            {
                obj.InObjectSlot(f) = data.FieldSlots[f] >= 0 ? scratch[data.FieldSlots[f]] : data.FieldConstants[f];
            }
            result[i] = obj;
        }
        // Fields holding captured objects (all of them exist now).
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i].FieldCaptured is not { } nested) continue;
            var obj = (JSObject)result[i].Object;
            for (int f = 0; f < nested.Length; f++) if (nested[f] >= 0) obj.InObjectSlot(f) = result[nested[f]];
        }
        return result;
    }

    /// <summary>
    /// IsDeoptimizationWithoutCodeInvalidation: exits that do not mean an
    /// assumption of the code failed.
    /// </summary>
    static bool IsDeoptimizationWithoutCodeInvalidation(DeoptimizeReason reason) =>
        reason is DeoptimizeReason.kPrepareForOnStackReplacement or DeoptimizeReason.kOSREarlyExit or DeoptimizeReason.kInterrupt;

    /// <summary>DeoptExitIsInsideOsrLoop: the loop from the OSR entry's header to its JumpLoop.</summary>
    static bool DeoptExitIsInsideOsrLoop(MaglevCode code, int deoptExitOffset) =>
        deoptExitOffset >= code.OsrEntryPoint && deoptExitOffset <= code.OsrOffset;

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
        Console.WriteLine($"[bailout (kind: {kind}, reason: {reason}): begin. deoptimizing {MaglevCompiler.DebugName(code.SharedFunctionInfo)}, " +
                          $"bytecode offset {top.BytecodeOffset} in {MaglevCompiler.DebugName(top.Shared)}, frames {point.Frames.Length}]");
    }
}
