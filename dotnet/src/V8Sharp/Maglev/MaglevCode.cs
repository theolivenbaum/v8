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

// The direct call entries of Maglev code (MaglevCalls, "Direct calls"): the
// callee closure, the call's argument count, the receiver and one value per
// formal parameter (undefined for the missing ones). Bound to their code.
public delegate JSValue MaglevFastCall0(Isolate isolate, JSFunction function, int argc, JSValue receiver);
public delegate JSValue MaglevFastCall1(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0);
public delegate JSValue MaglevFastCall2(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1);
public delegate JSValue MaglevFastCall3(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1,
    JSValue a2);
public delegate JSValue MaglevFastCall4(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1,
    JSValue a2, JSValue a3);
public delegate JSValue MaglevFastCall5(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1,
    JSValue a2, JSValue a3, JSValue a4);
public delegate JSValue MaglevFastCall6(Isolate isolate, JSFunction function, int argc, JSValue receiver, JSValue a0, JSValue a1,
    JSValue a2, JSValue a3, JSValue a4, JSValue a5);

public static class MaglevFastCalls
{
    /// <summary>The most formal parameters a direct call entry takes.</summary>
    public const int kMaxArity = 6;

    public static readonly Type[] DelegateTypes =
    [
        typeof(MaglevFastCall0), typeof(MaglevFastCall1), typeof(MaglevFastCall2), typeof(MaglevFastCall3),
        typeof(MaglevFastCall4), typeof(MaglevFastCall5), typeof(MaglevFastCall6),
    ];
}

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
    /// <summary>
    /// The captured objects (elided allocations) the registers hold, by
    /// register position: an index into DeoptPoint.CapturedObjects, or -1;
    /// null if none.
    /// </summary>
    public int[]? Captured;
    /// <summary>The translation's literals (constant values), by register position (where IsConstant); null if none.</summary>
    public JSValue[]? Constants;
    public bool[]? IsConstant;
    /// <summary>
    /// The scratch buffer index of each register's value (-1 for literals and
    /// materialized objects). Every value of a code has its own slot, so deopt
    /// exits whose values overlap share the code that spills them.
    /// </summary>
    public int[] ScratchSlots = [];
}

/// <summary>
/// A captured object of a translation (BeginCapturedObject): an elided
/// allocation the Deoptimizer materializes, its in-object fields from the
/// scratch buffer or literals.
/// </summary>
public sealed class CapturedObjectData
{
    /// <summary>The map the object was allocated with (its in-object slot class).</summary>
    public Map AllocatedMap = null!;
    /// <summary>The object's map at the deopt point.</summary>
    public Map Map = null!;
    /// <summary>The scratch index of each field's value, or -1 for a literal.</summary>
    public int[] FieldSlots = [];
    /// <summary>The literal of each field whose FieldSlots entry is -1.</summary>
    public JSValue[] FieldConstants = [];
    /// <summary>The captured object (index in the point's) each field holds, or -1; null if none.</summary>
    public int[]? FieldCaptured;
}

/// <summary>A deopt exit (DeoptimizationData entry: kind, reason, translation).</summary>
public sealed class DeoptPoint
{
    /// <summary>The objects the deopt materializes (elided allocations), or null.</summary>
    public CapturedObjectData[]? CapturedObjects;
    public DeoptimizeKind Kind;
    public DeoptimizeReason Reason;
    /// <summary>The frames, outermost first; the last is the frame execution continues in.</summary>
    public DeoptFrameData[] Frames = [];
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

    /// <summary>
    /// The direct call entry (a MaglevFastCall delegate of the function's
    /// formal parameter count), or null (OSR code, too many parameters).
    /// </summary>
    public Delegate? FastCall;
    /// <summary>The formal parameter count of <see cref="FastCall"/> (-1 without one).</summary>
    public int FastCallArity = -1;

    /// <summary>Code::marked_for_deoptimization: activations deoptimize lazily when control returns to them.</summary>
    public bool MarkedForDeoptimization;

    public DeoptPoint[] DeoptPoints { get; internal set; } = [];
    /// <summary>The code was generated and jitted on the background compile thread.</summary>
    public bool CompiledConcurrently { get; internal set; }

    /// <summary>Why the concurrent job could not generate the code (a bailout of the code generator), or null.</summary>
    public string? FailureReason { get; internal set; }
    /// <summary>The background job's time (IL generation and RyuJIT), for --trace-opt.</summary>
    public double BackgroundMs { get; internal set; }
    /// <summary>The call feedback slots eager deopts disallow speculation for (EagerDeoptInfo.FeedbackToUpdate).</summary>
    public (FeedbackVector Vector, int Slot)[] SpeculationFeedback { get; internal set; } = [];
    public int MaxScratchSize { get; internal set; }

    /// <summary>The dependencies registered for the code (CompilationDependencies).</summary>
    public CompilationDependency[] Dependencies { get; internal set; } = [];
    /// <summary>The instance size predictions of the code's inlined allocations (checked at commit).</summary>
    public (Map Map, int InObjectProperties)[] InstanceSizePredictions { get; internal set; } = [];

    public int ILSize { get; internal set; }
    /// <summary>What RyuJIT's optimization limits count (MaglevILEmitter), for --trace-opt-verbose.</summary>
    public (int Instructions, int BlockBoundaries, int LocalReferences, int Locals) ILCounts { get; internal set; }
    public int NodeCount { get; internal set; }
    public int InlinedFunctionCount { get; internal set; }

    public override string ToString() => $"<MaglevCode {MaglevCompiler.DebugName(SharedFunctionInfo)}{(OsrOffset >= 0 ? " OSR@" + OsrOffset : "")}>";
}
