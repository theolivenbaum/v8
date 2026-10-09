// Exception handling: LiftoffCompiler::Try, CatchException, CatchAll,
// Delegate, Rethrow, TryTable, CatchCase, Throw and ThrowRef
// (src/wasm/baseline/liftoff-compiler.cc).
//
// V8 unwinds to handler tables of the machine code. V8Sharp maps each try
// and try_table to an IL exception region with a filter: it accepts the
// WasmHostException that carries a wasm exception (thrown by throw, by an
// import, or out of the interpreter) if one of the catches takes its tag,
// and the handler then dispatches on the tag. Filters decide without
// unwinding, so an exception that no handler of a frame takes passes it
// without a catch and rethrow (which in .NET would nest a new exception
// dispatch per frame on the native stack). Traps are TrapExceptions and are
// never caught. A delegate's filter marks the exception with its target and
// declines it; the filters between decline it too.
using System.Reflection.Emit;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    sealed class CatchClause
    {
        public byte Kind; // 0 catch, 1 catch_ref, 2 catch_all, 3 catch_all_ref
        public int Tag;
        public Control Target = null!;
    }

    readonly Dictionary<Control, List<CatchClause>> _tryTableClauses = [];

    WasmKind[] TagParams(int tag)
    {
        var type = (FunctionType)_data.Engine.Store[_data.Tags[tag]].Type.Expansion;
        return WasmKinds.Of(type.ParameterTypes.Types);
    }

    void BeginTryRegion(Control c)
    {
        c.Label = _il.DefineLabel();
        c.TryId = ++_nextTryId;
        c.HandlerTags = [];
        c.HandlerTagsConstant = AddConstant(c.HandlerTags);
        _il.BeginExceptionBlock();
        _tryDepth++;
    }

    /// <summary>
    /// The filter and the start of the handler: the filter takes the
    /// exceptions the try's catches take (RuntimeWasm.HandlerFilter); the
    /// handler stores the exception, takes back the frames the exception
    /// unwound and re-reads the memories.
    /// </summary>
    LocalBuilder BeginHandler(Control c)
    {
        _il.BeginExceptFilterBlock();
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, c.HandlerTagsConstant);
        _il.Emit(OpCodes.Ldloc, _spLocal);
        _il.Emit(OpCodes.Ldc_I4, c.TryId);
        if (_tailCallFlag != null) _il.Emit(OpCodes.Ldloc, _tailCallFlag);
        else _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.HandlerFilter)));
        _il.BeginCatchBlock(null!);
        LocalBuilder exception = _il.DeclareLocal(typeof(WasmHostException));
        _il.Emit(OpCodes.Castclass, typeof(WasmHostException));
        _il.Emit(OpCodes.Stloc, exception);
        _il.Emit(OpCodes.Ldloc, _framesLocal);
        _il.Emit(OpCodes.Ldloc, _spLocal);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Add);
        _il.Emit(OpCodes.Stfld, s_framesSp);
        ReloadMemories();
        return exception;
    }

    void EmitTagTest(LocalBuilder exception, int tag)
    {
        _il.Emit(OpCodes.Ldloc, exception);
        _il.Emit(OpCodes.Ldloc, _dataLocal);
        _il.Emit(OpCodes.Ldc_I4, tag);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.ExnTagIs)));
    }

    /// <summary>Stores the exception's payload field <paramref name="index"/> in the slot at <paramref name="depth"/>.</summary>
    void EmitPayloadToSlot(LocalBuilder exception, int index, WasmKind kind, int depth)
    {
        _il.Emit(OpCodes.Ldloc, exception);
        _il.Emit(OpCodes.Ldc_I4, index);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.ExnField)));
        WasmValues.EmitFromValue(_il, kind);
        _il.Emit(OpCodes.Stloc, _asm.Slot(depth, kind));
    }

    // ---- try_table ---------------------------------------------------------------

    void TryTable()
    {
        var (p, r) = ReadBlockType();
        uint count = ReadU32();
        var clauses = new List<CatchClause>((int)count);
        for (uint i = 0; i < count; i++)
        {
            byte kind = ReadU8();
            int tag = kind is 0 or 1 ? (int)ReadU32() : -1;
            int depth = (int)ReadU32();
            // The labels are relative to the block around the try_table.
            clauses.Add(new CatchClause { Kind = kind, Tag = tag, Target = _reachable ? ControlAt(depth) : null! });
        }
        Control c = PushControl(ControlKind.TryTable, p, r);
        if (!c.Reachable) return;
        _tryTableClauses[c] = clauses;
        BeginTryRegion(c);
        foreach (CatchClause clause in clauses) c.HandlerTags!.Add(clause.Kind is 0 or 1 ? clause.Tag : -1);
    }

    void EndTry(Control c)
    {
        if (_reachable)
        {
            FallThruTo(c);
            _il.Emit(OpCodes.Leave, c.Label);
        }
        switch (c.Kind)
        {
            case ControlKind.TryTable:
                EmitTryTableHandler(c);
                break;
            case ControlKind.Try:
                // A try without catches catches nothing.
                BeginHandler(c);
                _il.Emit(OpCodes.Rethrow);
                break;
            case ControlKind.TryCatch:
                EmitLegacyDispatch(c);
                break;
        }
        _il.EndExceptionBlock();
        _tryDepth--;
        if (c.BranchedTo) _il.MarkLabel(c.Label);
        else EmitUnreachableEnd();
    }

    void EmitTryTableHandler(Control c)
    {
        LocalBuilder exception = BeginHandler(c);
        foreach (CatchClause clause in _tryTableClauses[c])
        {
            Label next = _il.DefineLabel();
            if (clause.Kind is 0 or 1)
            {
                EmitTagTest(exception, clause.Tag);
                _il.Emit(OpCodes.Brfalse, next);
            }
            Control target = clause.Target;
            WasmKind[] kinds = target.BranchKinds;
            int payload = clause.Kind is 0 or 1 ? TagParams(clause.Tag).Length : 0;
            for (int j = 0; j < payload; j++) EmitPayloadToSlot(exception, j, kinds[j], target.Base + j);
            if (clause.Kind is 1 or 3)
            {
                _il.Emit(OpCodes.Ldloc, exception);
                _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.ExnRefOf)));
                _il.Emit(OpCodes.Stloc, _asm.Slot(target.Base + kinds.Length - 1, WasmKind.Ref));
            }
            if (target.Kind == ControlKind.Function)
            {
                // A catch to the function's label returns the values.
                List<VarState> saved = _asm.Snapshot();
                _asm.ResetTo(0, kinds);
                DoReturn();
                _asm.Restore(saved);
            }
            else
            {
                target.BranchedTo = true;
                _il.Emit(OpCodes.Leave, target.Label);
            }
            _il.MarkLabel(next);
        }
        _il.Emit(OpCodes.Rethrow);
    }

    // ---- Legacy exception handling ---------------------------------------------------

    void Try()
    {
        var (p, r) = ReadBlockType();
        Control c = PushControl(ControlKind.Try, p, r);
        if (!c.Reachable) return;
        c.Catches = [];
        c.Dispatch = _il.DefineLabel();
        BeginTryRegion(c);
    }

    /// <summary>Ends the try body or the previous catch body, and starts a catch body.</summary>
    void BeginCatchBody(Control c, int tag)
    {
        if (_reachable)
        {
            FallThruTo(c);
            _il.Emit(OpCodes.Leave, c.Label);
        }
        if (c.Kind == ControlKind.Try)
        {
            c.Exception = BeginHandler(c);
            _il.Emit(OpCodes.Br, c.Dispatch);
            c.Kind = ControlKind.TryCatch;
            c.InHandler = true;
        }
        Label body = _il.DefineLabel();
        _il.MarkLabel(body);
        c.Catches!.Add((tag, body));
        c.HandlerTags!.Add(tag);
        WasmKind[] payload = tag >= 0 ? TagParams(tag) : [];
        _asm.ResetTo(c.Base, []);
        for (int j = 0; j < payload.Length; j++)
        {
            EmitPayloadToSlot(c.Exception!, j, payload[j], c.Base + j);
            _asm.PushSlot(payload[j]);
        }
        _reachable = true;
    }

    void Catch(int tag)
    {
        Control c = ControlAt(0);
        if (!c.Reachable) return;
        BeginCatchBody(c, tag);
    }

    void CatchAll()
    {
        Control c = ControlAt(0);
        if (!c.Reachable) return;
        BeginCatchBody(c, -1);
    }

    void EmitLegacyDispatch(Control c)
    {
        _il.MarkLabel(c.Dispatch);
        foreach (var (tag, body) in c.Catches!)
        {
            if (tag < 0)
            {
                _il.Emit(OpCodes.Br, body);
                return;
            }
            EmitTagTest(c.Exception!, tag);
            _il.Emit(OpCodes.Brtrue, body);
        }
        _il.Emit(OpCodes.Rethrow);
    }

    void Delegate(int depth)
    {
        Control c = ControlAt(0);
        if (!c.Reachable)
        {
            _control.RemoveAt(_control.Count - 1);
            return;
        }
        if (_reachable)
        {
            FallThruTo(c);
            _il.Emit(OpCodes.Leave, c.Label);
        }
        // The handlers that take the exception: those of the try that the
        // label names (if it is in its body), else the next one out.
        int targetId = -1;
        for (int i = _control.Count - 2 - depth; i >= 0; i--)
        {
            Control t = _control[i];
            if (t.Kind == ControlKind.Try)
            {
                targetId = t.TryId;
                break;
            }
        }
        _il.BeginExceptFilterBlock();
        _il.Emit(OpCodes.Ldloc, _spLocal);
        _il.Emit(OpCodes.Ldc_I4, targetId);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.DelegateFilter)));
        _il.BeginCatchBlock(null!);
        _il.Emit(OpCodes.Rethrow);
        _il.EndExceptionBlock();
        _tryDepth--;
        if (c.BranchedTo) _il.MarkLabel(c.Label);
        else EmitUnreachableEnd();
        _control.RemoveAt(_control.Count - 1);
        _asm.ResetTo(c.Base, c.Results);
        _reachable = c.BranchedTo;
    }

    void Rethrow(int depth)
    {
        if (!_reachable) return;
        Control c = ControlAt(depth);
        if (c.Exception is null) Unsupported("rethrow outside a catch");
        _il.Emit(OpCodes.Ldloc, c.Exception!);
        EmitTrapCall(nameof(RuntimeWasm.Rethrow));
        _reachable = false;
    }

    void Throw(int tag)
    {
        if (!_reachable) return;
        WasmKind[] payload = TagParams(tag);
        int n = payload.Length;
        _asm.Settle(n);
        int first = _asm.Height - n;
        _il.Emit(OpCodes.Ldc_I4, n);
        _il.Emit(OpCodes.Newarr, typeof(Value));
        for (int i = 0; i < n; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _asm.LoadSettled(first + i);
            WasmValues.EmitToValue(_il, payload[i]);
            _il.Emit(OpCodes.Stelem, typeof(Value));
        }
        _asm.Drop(n);
        _il.Emit(OpCodes.Ldc_I4, tag);
        EmitTrapCall(nameof(RuntimeWasm.Throw));
        _reachable = false;
    }

    void ThrowRef()
    {
        if (!_reachable) return;
        _asm.PopToStack(1);
        EmitTrapCall(nameof(RuntimeWasm.ThrowRef));
        _reachable = false;
    }
}
