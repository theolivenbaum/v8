// The code object of a Maglev-compiled function: V8's Code of kind MAGLEV
// (src/objects/code.h) with its DeoptimizationData (the translations of
// src/deoptimizer/frame-translation-builder.h, which maglev-code-generator.cc
// writes for every deopt exit).
//
// V8Sharp's translation is the list of interpreter frames to materialise
// (outermost first) and, per frame, the registers it restores; the values
// themselves are written by the code's deopt exit into the isolate's deopt
// scratch buffer in that order (V8 reads them from the optimized frame's
// stack slots and registers instead).
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>
/// The entry point of Maglev code: runs the frame <paramref name="state"/>
/// describes (built by InterpreterExecution.EnterFrame, or the interpreter's
/// frame at an OSR loop header) until it returns or deoptimizes.
/// </summary>
public delegate JSValue MaglevCodeEntry(Isolate isolate, ref InterpreterState state);

/// <summary>A frame of a deopt translation.</summary>
public sealed class DeoptFrameData
{
    /// <summary>The inlining depth: the frame record is the optimized frame's + this.</summary>
    public int InliningDepth;
    /// <summary>Inlined frames: the call's argument count and kind (for pushing the frame at a deopt).</summary>
    public int Argc;
    public bool IsConstruct;
    public JSFunction Function = null!;
    public BytecodeArray Bytecode = null!;
    public FeedbackVector? FeedbackVector;
    /// <summary>The offset of the bytecode (its prefix included) the frame is at.</summary>
    public int BytecodeOffset;
    /// <summary>The offset of the next bytecode (where a frame at a call continues).</summary>
    public int NextOffset;
    /// <summary>The registers restored from the scratch buffer (Register.Index; the context and accumulator by their special registers).</summary>
    public Register[] Registers = [];
    /// <summary>Values the Deoptimizer creates (elided arguments objects), by register position; null if none.</summary>
    public ArgumentsObjectKind[]? Materialize;
    /// <summary>The scratch buffer index of the first value.</summary>
    public int ScratchStart;
}

/// <summary>A deopt exit (DeoptimizationData entry: kind, reason, translation).</summary>
public sealed class DeoptPoint
{
    public DeoptimizeKind Kind;
    public DeoptimizeReason Reason;
    /// <summary>The frames, outermost first; the last is the frame execution continues in.</summary>
    public DeoptFrameData[] Frames = [];
    public int ScratchSize;
    /// <summary>Lazy deopts: where the result of the call goes in the top frame.</summary>
    public Register ResultLocation = Register.VirtualAccumulator();
    /// <summary>Lazy deopts: the scratch index of the result, or -1 (the call wrote its outputs to the frame itself).</summary>
    public int ResultScratchIndex = -1;
    /// <summary>The number of times this exit was taken.</summary>
    public int Count;
}

/// <summary>Code of kind MAGLEV.</summary>
public sealed class MaglevCode
{
    public MaglevCode(JSFunction function, FeedbackVector feedbackVector, int osrOffset)
    {
        Function = function;
        SharedFunctionInfo = function.Shared;
        FeedbackVector = feedbackVector;
        OsrOffset = osrOffset;
    }

    public JSFunction Function { get; }
    public SharedFunctionInfo SharedFunctionInfo { get; }
    public FeedbackVector FeedbackVector { get; }
    /// <summary>The JumpLoop offset an OSR code is entered at, or -1.</summary>
    public int OsrOffset { get; }
    /// <summary>The loop header OSR code continues at (the interpreter's pc at the OSR entry).</summary>
    public int OsrEntryPoint { get; internal set; } = -1;

    public static CodeKind Kind => CodeKind.MAGLEV;

    public MaglevCodeEntry Entry { get; internal set; } = null!;

    /// <summary>Code::marked_for_deoptimization: activations deoptimize lazily when control returns to them.</summary>
    public bool MarkedForDeoptimization;

    public DeoptPoint[] DeoptPoints { get; internal set; } = [];
    /// <summary>The code was generated and jitted on the background compile thread.</summary>
    public bool CompiledConcurrently { get; internal set; }
    /// <summary>The background job's time (IL generation and RyuJIT), for --trace-opt.</summary>
    public double BackgroundMs { get; internal set; }
    /// <summary>The call feedback slots eager deopts disallow speculation for (EagerDeoptInfo.FeedbackToUpdate).</summary>
    public (FeedbackVector Vector, int Slot)[] SpeculationFeedback { get; internal set; } = [];
    public int MaxScratchSize { get; internal set; }

    /// <summary>The dependencies registered for the code (CompilationDependencies).</summary>
    public CompilationDependency[] Dependencies { get; internal set; } = [];

    public int ILSize { get; internal set; }
    public int NodeCount { get; internal set; }
    public int InlinedFunctionCount { get; internal set; }

    public override string ToString() => $"<MaglevCode {MaglevCompiler.DebugName(SharedFunctionInfo)}{(OsrOffset >= 0 ? " OSR@" + OsrOffset : "")}>";
}
