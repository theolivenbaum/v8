// LiftoffCompiler::CallDirect, CallIndirectImpl, CallRefImpl and the tail
// call variants (src/wasm/baseline/liftoff-compiler.cc).
//
// V8 calls through the jump table (direct calls), the dispatch table
// (call_indirect, with a signature check) or the function reference's
// target. V8Sharp calls the function's typed delegate: the instance's
// function slots for direct calls (patched when the callee is compiled), the
// code table for call_indirect and call_ref. A call to the function itself,
// or to one already compiled, is a direct IL call. Tail calls use IL's
// tail. prefix, which RyuJIT honours (with its tail-call helper when it
// cannot make the call a jump).
//
// call_indirect and call_ref go through an inline cache per call site
// (WasmCallSite: the last target and its code, the signature check done
// when it was filled), which also collects V8's call-target feedback.
// Callees the inlining tree chose are inlined (LiftoffCompiler.Inlining.cs):
// direct calls always, call_indirect/call_ref targets behind a check of the
// target (speculative inlining).
using System.Reflection;
using System.Reflection.Emit;
using Wacs.Core.Runtime;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    /// <summary>Pushes a call's results: the return value, then the others from the frames' return buffer.</summary>
    void PushCallResults(WasmSignature sig)
    {
        WasmKind[] results = sig.Results;
        if (results.Length == 0) return;
        _asm.PushStack(results[0]);
        for (int i = 1; i < results.Length; i++)
        {
            _il.Emit(OpCodes.Ldloc, _framesLocal);
            _il.Emit(OpCodes.Ldfld, s_framesReturns);
            _il.Emit(OpCodes.Ldc_I4, i - 1);
            _il.Emit(OpCodes.Ldelem, typeof(Value));
            WasmValues.EmitFromValue(_il, results[i]);
            _asm.PushStack(results[i]);
        }
    }

    /// <summary>
    /// Whether a return_call here is an IL tail call: outside exception
    /// regions, in the method's function or in a callee whose returns are the
    /// method's (inlined for tail calls only).
    /// </summary>
    bool CanTailCall => _tryDepth == 0 && (_inline is null || _inline.InTailPosition);

    /// <summary>
    /// Whether a call here may be inlined: not a return_call inside one of
    /// the function's own try blocks, whose handlers must not see what the
    /// callee throws (the callee replaces the frame that has them).
    /// </summary>
    bool MayInline(bool tail) => (!tail || _inline is not null || _tryDepth == 0) && _il.ILOffset < kMaxILForInlining;

    /// <summary>
    /// No more inlining once the method has this much IL: RyuJIT compiles
    /// methods over about 60 KB of IL with MinOpts (no register allocation
    /// or loop optimization), which costs far more than the calls inlining
    /// saves. V8's budget counts wire bytes; IL is about 8-17 bytes per
    /// instruction here.
    /// </summary>
    const int kMaxILForInlining = 30_000;

    /// <summary>Set during a return_call made as a call (inside an exception region).</summary>
    LocalBuilder? _tailCallFlag;

    /// <summary>Emits the call instruction itself (the target and arguments are on the IL stack).</summary>
    void EmitCall(OpCode op, MethodInfo method, WasmSignature sig, bool tail)
    {
        if (tail && CanTailCall)
        {
            EmitLeaveFrame();
            _il.Emit(OpCodes.Tailcall);
            _il.Emit(op, method);
            _il.Emit(OpCodes.Ret);
            _reachable = false;
            return;
        }
        if (tail && _inline is null)
        {
            // The frame is gone during a tail call: its handlers must not
            // see the callee's exceptions.
            _tailCallFlag ??= _il.DeclareLocal(typeof(bool));
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stloc, _tailCallFlag);
        }
        EmitStorePc();
        _il.Emit(op, method);
        PushCallResults(sig);
        ReloadMemories();
        if (tail) DoReturn();
    }

    void CallDirect(int funcIndex, bool tail)
    {
        int callIndex = _callIndex++;
        if (!_reachable) return;
        if (MayInline(tail) && _plan?.InlinedDirectCall(_instIndex) is { } inlined)
        {
            InlineDirectCall(inlined, callIndex, tail);
            return;
        }
        WasmCode target = _data.Code[funcIndex];
        WasmSignature sig = target.Signature;
        if (target.State == WasmCodeState.Lazy && target.Instance == _data && !target.Compiling &&
            WasmModuleCompiler.MayCompileAhead())
        {
            WasmModuleCompiler.CountCompileAhead();
            target.Compile();
        }
        if (target == _rootCode)
        {
            _asm.PopToStackWithPrefix(sig.Params.Length, () => _il.Emit(OpCodes.Ldarg_0));
            EmitCall(OpCodes.Call, _method, sig, tail);
            return;
        }
        // Code that may tier up is called through its slot, which the
        // tier-up patches (V8: the jump table).
        if (target.State == WasmCodeState.Compiled && target.Method is { } method && target.Instance == _data &&
            !target.MayTierUp)
        {
            if (!target.Shareable) _instanceSpecific = true;
            _asm.PopToStackWithPrefix(sig.Params.Length, () =>
            {
                _il.Emit(OpCodes.Ldloc, _dataLocal);
                _il.Emit(OpCodes.Ldfld, s_dataCode);
                _il.Emit(OpCodes.Ldc_I4, funcIndex);
                _il.Emit(OpCodes.Ldelem_Ref);
            });
            EmitCall(OpCodes.Call, method, sig, tail);
            return;
        }
        _asm.PopToStackWithPrefix(sig.Params.Length, () =>
        {
            _il.Emit(OpCodes.Ldloc, _dataLocal);
            _il.Emit(OpCodes.Ldfld, s_dataFunctions);
            _il.Emit(OpCodes.Ldc_I4, funcIndex);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Castclass, sig.DelegateType);
        });
        EmitCall(OpCodes.Callvirt, sig.Invoke, sig, tail);
    }

    void CallIndirect(int typeIndex, int table, bool tail)
    {
        int callIndex = _callIndex++;
        if (!_reachable) return;
        DefType expected = _module.Types[(TypeIdx)(uint)typeIndex];
        WasmSignature sig = WasmSignature.Get((FunctionType)expected.Expansion);
        // The site: its inline cache and, in code that tiers up, its feedback.
        LocalBuilder site = CallSite().Local;
        LocalBuilder elements = _tableElements[table];
        int n = sig.Params.Length;
        bool table64 = TableAddressKind(table) == WasmKind.I64;
        _asm.Settle(n + 1);
        int first = _asm.Height - n - 1;
        void LoadIndex()
        {
            _asm.LoadSettled(first + n);
            if (!table64) _il.Emit(OpCodes.Conv_U8);
        }
        void EmitGenericCall()
        {
            EmitStorePc();
            // RuntimeWasm.CallIndirectTarget(index, elements, site, code, pc)
            LoadIndex();
            _il.Emit(OpCodes.Ldloc, elements);
            _il.Emit(OpCodes.Ldloc, site);
            EmitRuntimeCall(nameof(RuntimeWasm.CallIndirectTarget));
            _il.Emit(OpCodes.Castclass, sig.DelegateType);
            for (int i = 0; i < n; i++) _asm.LoadSettled(first + i);
            _asm.Drop(n + 1);
            EmitCall(OpCodes.Callvirt, sig.Invoke, sig, tail);
        }
        List<WasmInliningTree>? cases = MayInline(tail) ? _plan?.InlinedCases(_instIndex) : null;
        if (cases is { Count: > 0 })
        {
            EmitSpeculativeCall(cases, tail ? "return_call_indirect" : "call_indirect", callIndex, sig, first, tail, () =>
            {
                LoadIndex();
                _il.Emit(OpCodes.Ldloc, elements);
                _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.CallIndirectTargetAddress)));
            }, EmitGenericCall);
            return;
        }
        EmitGenericCall();
    }

    void CallRef(int typeIndex, bool tail)
    {
        int callIndex = _callIndex++;
        if (!_reachable) return;
        DefType type = _module.Types[(TypeIdx)(uint)typeIndex];
        WasmSignature sig = WasmSignature.Get((FunctionType)type.Expansion);
        LocalBuilder site = CallSite().Local;
        int n = sig.Params.Length;
        _asm.Settle(n + 1);
        int first = _asm.Height - n - 1;
        void EmitGenericCall()
        {
            EmitStorePc();
            _asm.LoadSettled(first + n);
            _il.Emit(OpCodes.Ldloc, site);
            EmitRuntimeCall(nameof(RuntimeWasm.CallRefTarget));
            _il.Emit(OpCodes.Castclass, sig.DelegateType);
            for (int i = 0; i < n; i++) _asm.LoadSettled(first + i);
            _asm.Drop(n + 1);
            EmitCall(OpCodes.Callvirt, sig.Invoke, sig, tail);
        }
        List<WasmInliningTree>? cases = MayInline(tail) ? _plan?.InlinedCases(_instIndex) : null;
        if (cases is { Count: > 0 })
        {
            EmitSpeculativeCall(cases, tail ? "return_call_ref" : "call_ref", callIndex, sig, first, tail, () =>
            {
                _asm.LoadSettled(first + n);
                _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.FunctionAddressOf)));
            }, EmitGenericCall);
            return;
        }
        EmitGenericCall();
    }
}
