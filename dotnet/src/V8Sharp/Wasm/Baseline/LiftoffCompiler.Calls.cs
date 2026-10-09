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

    bool CanTailCall => _tryDepth == 0;

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
        if (tail)
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
        if (!_reachable) return;
        WasmCode target = _data.Code[funcIndex];
        WasmSignature sig = target.Signature;
        if (target == _code)
        {
            _asm.PopToStackWithPrefix(sig.Params.Length, () => _il.Emit(OpCodes.Ldarg_0));
            EmitCall(OpCodes.Call, _method, sig, tail);
            return;
        }
        if (target.State == WasmCodeState.Compiled && target.Method is { } method && target.Instance == _data)
        {
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
        if (!_reachable) return;
        DefType expected = _module.Types[(TypeIdx)(uint)typeIndex];
        WasmSignature sig = WasmSignature.Get((FunctionType)expected.Expansion);
        int k = AddConstant(expected);
        int n = sig.Params.Length;
        bool table64 = TableAddressKind(table) == WasmKind.I64;
        _asm.Settle(n + 1);
        int first = _asm.Height - n - 1;
        EmitStorePc();
        // RuntimeWasm.ResolveIndirect(index, table, constant, code, pc)
        _asm.LoadSettled(first + n);
        if (!table64) _il.Emit(OpCodes.Conv_U8);
        _il.Emit(OpCodes.Ldc_I4, table);
        _il.Emit(OpCodes.Ldc_I4, k);
        EmitRuntimeCall(nameof(RuntimeWasm.ResolveIndirect));
        _il.Emit(OpCodes.Castclass, sig.DelegateType);
        for (int i = 0; i < n; i++) _asm.LoadSettled(first + i);
        _asm.Drop(n + 1);
        EmitCall(OpCodes.Callvirt, sig.Invoke, sig, tail);
    }

    void CallRef(int typeIndex, bool tail)
    {
        if (!_reachable) return;
        DefType type = _module.Types[(TypeIdx)(uint)typeIndex];
        WasmSignature sig = WasmSignature.Get((FunctionType)type.Expansion);
        int n = sig.Params.Length;
        _asm.Settle(n + 1);
        int first = _asm.Height - n - 1;
        EmitStorePc();
        _asm.LoadSettled(first + n);
        EmitRuntimeCall(nameof(RuntimeWasm.ResolveRef));
        _il.Emit(OpCodes.Castclass, sig.DelegateType);
        for (int i = 0; i < n; i++) _asm.LoadSettled(first + i);
        _asm.Drop(n + 1);
        EmitCall(OpCodes.Callvirt, sig.Invoke, sig, tail);
    }
}
