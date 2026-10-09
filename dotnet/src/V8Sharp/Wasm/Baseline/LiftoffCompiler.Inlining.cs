// Inlining of wasm functions into wasm functions: the inlining parts of
// src/wasm/turboshaft-graph-interface.cc (InlineWasmCall, the speculative
// call_indirect/call_ref inlining with its target checks) and the tier-up
// check of src/wasm/baseline/liftoff-compiler.cc (TierupCheck).
//
// V8's optimizing compiler builds the callee's graph into the caller's.
// V8Sharp decodes the callee with a LiftoffCompiler of its own that emits
// into the caller's method: the arguments go to IL locals that are the
// callee's parameters, its locals are fresh IL locals (zeroed at each entry),
// its value stack is its own, and its returns are branches to a label after
// the body, with the results in the callee's result slots. The callee's
// frame is not pushed: positions inside it name an inlined position (the
// callee and the frames it is inlined into, CompiledFrames.InlinedPosition),
// so stack traces show its frame as V8 shows inlined frames. RyuJIT never
// inlines one DynamicMethod into another, which is why this is done here.
using System.Reflection;
using System.Reflection.Emit;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    /// <summary>The state of a callee decoded into its caller's method.</summary>
    sealed class InlineFrame
    {
        /// <summary>Where the callee's returns go.</summary>
        public Label Join;
        public bool Returned;
        /// <summary>The inlined frames this one is inlined into, innermost first (not the method's own).</summary>
        public int[] OuterFuncs = [];
        public int[] OuterPcs = [];
        /// <summary>The method's own frame's position: the call the outermost inlined frame came from.</summary>
        public int FramePc;
        /// <summary>The outermost inlined frame came from a tail call of the method's function: it replaces that frame.</summary>
        public bool ReplacesFrame;
        /// <summary>The callee's returns are the method's (it was reached by tail calls only): it can make real tail calls.</summary>
        public bool InTailPosition;
    }

    /// <summary>The registered inlined positions of this frame's instructions (0: not yet).</summary>
    int[]? _inlinedPcs;

    /// <summary>A compiler for <paramref name="callee"/>, inlined at the current instruction of <paramref name="caller"/>.</summary>
    LiftoffCompiler(LiftoffCompiler caller, WasmCode callee, WasmInliningTree node, bool tail)
    {
        _code = callee;
        _rootCode = caller._rootCode;
        _tierUp = caller._tierUp;
        _constants = caller._constants;
        _data = caller._data;
        _function = (FunctionInstance)callee.Function;
        _module = _function.Module;
        _bytes = _data.WireBytes;
        _offsets = _function.Definition.InstructionOffsets;
        _instructions = [.. _function.Body.Instructions.Flatten()];
        _linkedOffset = _function.LinkedOffset;
        _method = caller._method;
        _il = caller._il;
        _asm = new LiftoffAssembler(_il) { LoadNaN = caller._asm.LoadNaN };
        _dataLocal = caller._dataLocal;
        _framesLocal = caller._framesLocal;
        _spLocal = caller._spLocal;
        _pcsLocal = caller._pcsLocal;
        _stackGuardLocal = caller._stackGuardLocal;
        _constantLocals = caller._constantLocals;
        _memArray = caller._memArray;
        _memSize = caller._memSize;
        _cacheMemories = caller._cacheMemories;
        _totalLocals = caller._totalLocals;
        _tryDepth = caller._tryDepth;
        _plan = node;
        int callerPc = caller._linkedOffset + caller._instIndex;
        // A tail call's callee takes the caller's place in stack traces.
        _inline = caller._inline is not { } outer
            ? new InlineFrame { Join = _il.DefineLabel(), FramePc = callerPc, ReplacesFrame = tail, InTailPosition = tail }
            : tail
                ? new InlineFrame
                {
                    Join = _il.DefineLabel(),
                    OuterFuncs = outer.OuterFuncs,
                    OuterPcs = outer.OuterPcs,
                    FramePc = outer.FramePc,
                    ReplacesFrame = outer.ReplacesFrame,
                    InTailPosition = outer.InTailPosition,
                }
                : new InlineFrame
                {
                    Join = _il.DefineLabel(),
                    OuterFuncs = [caller._code.Address.Value, .. outer.OuterFuncs],
                    OuterPcs = [callerPc, .. outer.OuterPcs],
                    FramePc = outer.FramePc,
                    ReplacesFrame = outer.ReplacesFrame,
                };
    }

    /// <summary>The pc that names the current instruction of this inlined frame (registered once per instruction).</summary>
    int InlinedPc()
    {
        _inlinedPcs ??= new int[_offsets.Length];
        ref int pc = ref _inlinedPcs[_instIndex];
        if (pc == 0)
        {
            InlineFrame frame = _inline!;
            pc = _data.Frames.AddInlinedPosition(new CompiledFrames.InlinedPosition(
                [_code.Address.Value, .. frame.OuterFuncs], [_linkedOffset + _instIndex, .. frame.OuterPcs], frame.FramePc,
                frame.ReplacesFrame));
        }
        return pc;
    }

    string TracePrefix => "[function " + _code.FunctionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          (_inline is null ? "" : " (inlined)");

    /// <summary>A direct call (or return_call) inlined (V8: InlineWasmCall).</summary>
    void InlineDirectCall(WasmInliningTree node, int callIndex, bool tail)
    {
        if (Trace) Console.Out.Write($"{TracePrefix}: inlining direct call #{callIndex} to function {node.FunctionIndex}]\n");
        EmitInlinedBody(node, tail);
        if (tail && _reachable) DoReturn();
    }

    /// <summary>
    /// Decodes the callee of <paramref name="node"/> into the method: takes its
    /// arguments from the value stack and pushes its results.
    /// </summary>
    void EmitInlinedBody(WasmInliningTree node, bool tail)
    {
        WasmCode callee = _data.Code[node.FunctionIndex];
        WasmSignature sig = callee.Signature;
        int n = sig.Params.Length;
        // The callee branches: nothing of the caller may stay on the IL stack.
        _asm.SpillStackEntries(0);
        var paramLocals = new LocalBuilder[n];
        for (int i = 0; i < n; i++) paramLocals[i] = _il.DeclareLocal(sig.ParamTypes[i]);
        _asm.PopToStack(n);
        for (int i = n - 1; i >= 0; i--) _il.Emit(OpCodes.Stloc, paramLocals[i]);
        var inlined = new LiftoffCompiler(this, callee, node, tail);
        inlined.CompileInlined(paramLocals);
        _genericCount += inlined._genericCount;
        _inlinedCalls += inlined._inlinedCalls + 1;
        if (!inlined._inline!.Returned)
        {
            // Every path of the callee traps, throws or loops forever.
            _reachable = false;
            return;
        }
        _il.MarkLabel(inlined._inline.Join);
        WasmKind[] results = sig.Results;
        for (int i = 0; i < results.Length; i++)
        {
            _il.Emit(OpCodes.Ldloc, inlined._asm.Slot(i, results[i]));
            _asm.PushStack(results[i]);
        }
    }

    /// <summary>The inlined callee's body (CompileFunction without a frame of its own).</summary>
    void CompileInlined(LocalBuilder[] paramLocals)
    {
        _pos = (int)_function.Definition.BodyOffset;
        uint groups = ReadU32();
        for (uint g = 0; g < groups; g++)
        {
            ReadU32();
            ReadValueKind();
        }
        FunctionType type = _function.Type;
        WasmKind[] parameters = WasmKinds.Of(type.ParameterTypes.Types);
        WasmKind[] locals = WasmKinds.Of(_function.Locals);
        _localKinds = [.. parameters, .. locals];
        _asm.ParamCount = 0;
        _asm.Locals = new LocalBuilder?[_localKinds.Length];
        for (int i = 0; i < parameters.Length; i++) _asm.Locals[i] = paramLocals[i];
        // The locals start as their defaults at every entry (the method's IL
        // locals are zero only once).
        for (int i = 0; i < locals.Length; i++)
        {
            LocalBuilder local = _il.DeclareLocal(WasmKinds.ClrType(locals[i]));
            _asm.Locals[parameters.Length + i] = local;
            if (locals[i] == WasmKind.Ref)
            {
                EmitLoadConstantValue(AddConstant(new Value(_function.Locals[i])));
                _il.Emit(OpCodes.Stloc, local);
            }
            else
            {
                _il.Emit(OpCodes.Ldloca, local);
                _il.Emit(OpCodes.Initobj, WasmKinds.ClrType(locals[i]));
            }
        }
        DecodeBody(WasmKinds.Of(type.ResultType.Types));
    }

    /// <summary>A return of an inlined callee: its results to its result slots, then to the join.</summary>
    void InlinedReturn()
    {
        WasmKind[] results = ControlAt(_control.Count - 1).Results;
        _asm.SpillStackEntries(0);
        _asm.Transfer(results.Length, 0);
        _il.Emit(OpCodes.Br, _inline!.Join);
        _inline.Returned = true;
        _reachable = false;
    }

    /// <summary>
    /// Speculative inlining of a call_indirect or call_ref (V8: the target
    /// checks of the inlined cases, then the call for any other target).
    /// The arguments (at <paramref name="first"/>) and the target operand
    /// above them are settled; <paramref name="loadTarget"/> emits the
    /// function address of the target (-1 if there is none) and
    /// <paramref name="emitCall"/> the generic call, which consumes them.
    /// </summary>
    void EmitSpeculativeCall(List<WasmInliningTree> cases, string kind, int callIndex, WasmSignature sig, int first,
        bool tail, Action loadTarget, Action emitCall)
    {
        LocalBuilder target = _il.DeclareLocal(typeof(long));
        loadTarget();
        _il.Emit(OpCodes.Stloc, target);
        List<VarState> entry = _asm.Snapshot();
        WasmKind[] results = sig.Results;
        Label done = _il.DefineLabel();
        bool reachesDone = false;
        foreach (WasmInliningTree c in cases)
        {
            if (Trace)
            {
                Console.Out.Write($"{TracePrefix}: Speculatively inlining {kind} #{callIndex}, case #{c.Case}, to function {c.FunctionIndex}]\n");
            }
            Label next = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, target);
            _il.Emit(OpCodes.Ldc_I8, (long)_data.Code[c.FunctionIndex].Address.Value);
            _il.Emit(OpCodes.Bne_Un, next);
            _asm.Restore(entry);
            _asm.Drop();
            _reachable = true;
            EmitInlinedBody(c, tail);
            if (_reachable)
            {
                if (tail)
                {
                    DoReturn();
                }
                else
                {
                    _asm.SpillStackEntries(0);
                    _asm.Transfer(results.Length, first);
                    _il.Emit(OpCodes.Br, done);
                    reachesDone = true;
                }
            }
            _il.MarkLabel(next);
        }
        _asm.Restore(entry);
        _reachable = true;
        emitCall();
        if (_reachable)
        {
            _asm.SpillStackEntries(0);
            _asm.Transfer(results.Length, first);
            reachesDone = true;
        }
        _il.MarkLabel(done);
        _asm.ResetTo(first, results);
        _reachable = reachesDone;
    }

    // ---- Tier-up -------------------------------------------------------------------

    /// <summary>
    /// LiftoffCompiler::TierupCheck: counts <paramref name="budgetUsed"/>
    /// (wire bytes standing in for Liftoff's code bytes) against the
    /// function's tiering budget and tiers up when it runs out. The IL stack
    /// must be empty.
    /// </summary>
    void EmitTierUpCheck(int budgetUsed)
    {
        const int kTierUpCostForCheck = 20;
        budgetUsed += kTierUpCostForCheck;
        int maxBudgetUse = Math.Max(1, _data.Engine.Isolate.Flags.wasm_tiering_budget / 4);
        if (budgetUsed > maxBudgetUse) budgetUsed = maxBudgetUse;
        Label skip = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, s_codeTieringBudget);
        _il.Emit(OpCodes.Ldc_I4, budgetUsed);
        _il.Emit(OpCodes.Sub);
        _il.Emit(OpCodes.Stfld, s_codeTieringBudget);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, s_codeTieringBudget);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Bge, skip);
        EmitRuntimeCall(nameof(RuntimeWasm.TriggerTierUp));
        _il.MarkLabel(skip);
    }

    /// <summary>The tier-up check of a loop's back edge (BrImpl), with the loop's size as the cost.</summary>
    void EmitBackEdgeTierUpCheck(Control loop)
    {
        if (!_collectFeedback) return;
        EmitTierUpCheck(Math.Max(1, _pos - loop.StartOffset));
    }
}
