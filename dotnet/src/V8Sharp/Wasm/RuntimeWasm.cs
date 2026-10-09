// The runtime functions and builtins compiled wasm code calls: the parts of
// src/runtime/runtime-wasm.cc (Runtime_ThrowWasmError, Runtime_WasmStackGuard,
// Runtime_WasmMemoryGrow, Runtime_WasmThrow, Runtime_WasmReThrow,
// Runtime_WasmFunctionTableGet...), the builtins of builtins-wasm-gen.cc and
// wasm.tq that Liftoff calls (the conversions with traps, memory.copy and
// memory.fill), and the C fallbacks of src/wasm/wasm-external-refs.cc
// (float rounding, 64-bit division).
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Wacs.Core.Instructions;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;

namespace V8Sharp.Wasm;

public static partial class RuntimeWasm
{
    static readonly Dictionary<string, MethodInfo> s_methods = BuildMethods();

    static Dictionary<string, MethodInfo> BuildMethods()
    {
        var methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (MethodInfo m in typeof(RuntimeWasm).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            methods.TryAdd(m.Name, m);
        }
        return methods;
    }

    internal static MethodInfo Method(string name) => s_methods[name];

    // ---- Frames, stack and interrupts --------------------------------------------------

    /// <summary>The frames of the current wasm call stack, with this frame at <paramref name="pc"/>.</summary>
    static WasmStackFrame[] SnapshotAt(WasmCode code, int pc, InstructionBase? instruction = null)
    {
        WasmInstanceData data = code.Instance!;
        CompiledFrames frames = data.Frames;
        if (frames.Sp > 0) frames.Pc[frames.Sp - 1] = pc;
        WasmStackFrame[] snapshot = data.Context.SnapshotCallStack();
        if (instruction != null && snapshot.Length > 0)
        {
            WasmStackFrame top = snapshot[0];
            snapshot[0] = new WasmStackFrame(top.FuncAddr, instruction, top.ResumeContinuationAddress, top.Pc);
        }
        return snapshot;
    }

    /// <summary>
    /// The stack check of a function's prologue (Runtime_WasmStackGuard): the
    /// frame count reached its limit, or it is time to check the native stack.
    /// </summary>
    public static void StackCheck(WasmCode code, int sp)
    {
        WasmInstanceData data = code.Instance!;
        if ((uint)sp >= (uint)data.Frames.Limit || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            // V8 reports the overflow at the entry of the function that
            // could not be entered (WasmRuntimeException.CalleeFuncAddr).
            throw new WasmRuntimeException("Runtime call stack exhausted", data.Context.SnapshotCallStack())
            {
                CalleeFuncAddr = code.Address.Value,
            };
        }
    }

    /// <summary>The interrupt check of loops (Runtime_WasmStackGuard): serves pending interrupts.</summary>
    public static void HandleInterrupts(WasmCode code, int pc)
    {
        WasmInstanceData data = code.Instance!;
        data.Frames.Pc[data.Frames.Sp - 1] = pc;
        data.StackGuard.HandleInterrupts();
    }

    // ---- Traps ----------------------------------------------------------------------

    /// <summary>Runtime_ThrowWasmError: a trap with V8's message.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowTrap(int template, WasmCode code, int pc)
    {
        var trap = new WasmTemplateTrapException((MessageTemplate)template, MessageFormatter.TemplateString((MessageTemplate)template))
        {
            WasmFrames = SnapshotAt(code, pc),
        };
        throw trap;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Trap(MessageTemplate template, WasmCode code, int pc) => ThrowTrap((int)template, code, pc);

    // ---- Memory -------------------------------------------------------------------------

    /// <summary>The effective address of a memory64 access, or a trap.</summary>
    public static long EffectiveAddress64(long index, long offset, int size, long memorySize, WasmCode code, int pc)
    {
        ulong ea = (ulong)index + (ulong)offset;
        if (ea < (ulong)index || ea > (ulong)memorySize || (ulong)memorySize - ea < (ulong)size)
        {
            Trap(MessageTemplate.WasmTrapMemOutOfBounds, code, pc);
        }
        return (long)ea;
    }

    /// <summary>Runtime_WasmMemoryGrow: the old size in pages, or -1.</summary>
    public static long MemoryGrow(long delta, int memory, WasmCode code, int pc)
    {
        MemoryInstance mem = code.Instance!.Memories[memory];
        if (delta < 0) return -1;
        try
        {
            return mem.GrowReturningOldSize(delta);
        }
        catch (OutOfMemoryException)
        {
            return -1;
        }
    }

    /// <summary>memory.copy (Liftoff: the MemoryCopy builtin).</summary>
    public static void MemoryCopy(long dst, long src, long n, int dstMemory, int srcMemory, WasmCode code, int pc)
    {
        WasmInstanceData data = code.Instance!;
        MemoryInstance d = data.Memories[dstMemory], s = data.Memories[srcMemory];
        ulong dLen = d.ByteLength, sLen = s.ByteLength;
        if ((ulong)dst > dLen || dLen - (ulong)dst < (ulong)n || (ulong)src > sLen || sLen - (ulong)src < (ulong)n)
        {
            Trap(MessageTemplate.WasmTrapMemOutOfBounds, code, pc);
        }
        if (n == 0) return;
        Buffer.BlockCopy(s.Data, (int)src, d.Data, (int)dst, (int)n);
    }

    /// <summary>memory.fill (Liftoff: the MemoryFill builtin).</summary>
    public static void MemoryFill(long dst, int value, long n, int memory, WasmCode code, int pc)
    {
        MemoryInstance m = code.Instance!.Memories[memory];
        ulong len = m.ByteLength;
        if ((ulong)dst > len || len - (ulong)dst < (ulong)n)
        {
            Trap(MessageTemplate.WasmTrapMemOutOfBounds, code, pc);
        }
        if (n == 0) return;
        m.Data.AsSpan((int)dst, (int)n).Fill((byte)value);
    }

    // ---- Calls ---------------------------------------------------------------------------

    /// <summary>
    /// The call_indirect dispatch (V8: the dispatch table load and the
    /// signature check of LiftoffCompiler::CallIndirectImpl).
    /// </summary>
    public static Delegate ResolveIndirect(long index, int table, int expectedConstant, WasmCode code, int pc)
    {
        WasmInstanceData data = code.Instance!;
        List<Value> elements = data.Tables[table].Elements;
        if ((ulong)index >= (ulong)elements.Count)
        {
            Trap(MessageTemplate.WasmTrapTableOutOfBounds, code, pc);
        }
        Value r = elements[(int)index];
        if (r.IsNullRef)
        {
            Trap(MessageTemplate.WasmTrapNullFunc, code, pc);
        }
        WasmCode target = data.Engine.CodeAt(r.GetFuncAddr(data.Module.Types));
        var expected = (DefType)code.Constants[expectedConstant];
        if (!ReferenceEquals(target.LastMatchedType, expected))
        {
            bool matches = target.DefType is { } actual
                ? actual.Matches(expected, data.Module.Types)
                : target.Function.Type.Matches(expected.Unroll.Body, data.Module.Types);
            if (!matches) Trap(MessageTemplate.WasmTrapFuncSigMismatch, code, pc);
            target.LastMatchedType = expected;
        }
        return target.Entry;
    }

    /// <summary>call_ref's target.</summary>
    public static Delegate ResolveRef(Value function, WasmCode code, int pc)
    {
        if (function.IsNullRef)
        {
            Trap(MessageTemplate.WasmTrapNullDereference, code, pc);
        }
        WasmInstanceData data = code.Instance!;
        return data.Engine.CodeAt(function.GetFuncAddr(data.Module.Types)).Entry;
    }

    /// <summary>
    /// Calls a function with <see cref="Value"/>s: a host function directly,
    /// a wasm function in the interpreter (the compiler bailed out on it).
    /// </summary>
    internal static Value[] CallWithValues(WasmCode code, Value[] args)
    {
        WasmEngine engine = code.Engine;
        // The interpreter's frames are deep on the native stack: check it here,
        // between compiled code and the interpreter.
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new WasmRuntimeException("Runtime call stack exhausted", engine.ExecContext.SnapshotCallStack())
            {
                CalleeFuncAddr = code.Address.Value,
            };
        }
        if (code.Function is HostFunction { Raw: { } raw } host)
        {
            var results = new Value[host.Type.ResultType.Arity];
            raw(engine.ExecContext, args, results);
            return results;
        }
        try
        {
            return engine.Runtime.Invoke(code.Address, args);
        }
        catch (UnhandledWasmException e)
        {
            throw new WasmHostException(e.ExnRef);
        }
    }

    // ---- References -----------------------------------------------------------------------

    public static int RefIsNull(Value value) => value.IsNullRef ? 1 : 0;

    public static bool RefIsNullBool(Value value) => value.IsNullRef;

    public static Value RefAsNonNull(Value value, WasmCode code, int pc)
    {
        if (value.IsNullRef) Trap(MessageTemplate.WasmTrapNullDereference, code, pc);
        return value;
    }

    /// <summary>br_on_cast's test: the value matches the target type (constants[k]).</summary>
    public static bool RefMatches(Value value, WasmCode code, int k) =>
        ((ValType)code.Constants[k]).Matches(value, code.Instance!.Module.Types);

    public static Value V128Zero() => new(new V128(0, 0, 0, 0));

    // ---- Instructions run by the interpreter ------------------------------------------------

    /// <summary>
    /// Runs instruction constants[k] in the interpreter, with its operands on
    /// the operand stack (see LiftoffCompiler.Generic.cs).
    /// </summary>
    public static void ExecuteInstruction(int k, WasmCode code, int pc)
    {
        WasmInstanceData data = code.Instance!;
        ExecContext ctx = data.Context;
        var instruction = (InstructionBase)code.Constants[k];
        Frame saved = ctx.Frame;
        ctx.Frame = data.InterpreterFrame;
        try
        {
            instruction.Execute(ctx);
        }
        catch (TrapException e) when (e.WasmFrames == null)
        {
            ctx.Frame = saved;
            e.WasmFrames = SnapshotAt(code, pc, instruction);
            throw;
        }
        catch (WasmRuntimeException e) when (e.WasmFrames == null)
        {
            ctx.Frame = saved;
            e.WasmFrames = SnapshotAt(code, pc, instruction);
            throw;
        }
        finally
        {
            ctx.Frame = saved;
        }
    }

    // ---- Exceptions -------------------------------------------------------------------------

    /// <summary>Runtime_WasmThrow: throws a new exception of tag <paramref name="tag"/>.</summary>
    [DoesNotReturn]
    public static void Throw(Value[] payload, int tag, WasmCode code, int pc)
    {
        WasmInstanceData data = code.Instance!;
        data.Frames.Pc[data.Frames.Sp - 1] = pc;
        var fields = new Stack<Value>(payload.Length);
        for (int i = payload.Length - 1; i >= 0; i--) fields.Push(payload[i]);
        Store store = data.Engine.Store;
        ExnAddr address = store.AllocateExn(data.Tags[tag], fields);
        throw new WasmHostException(new Value(ValType.Exn, store[address]));
    }

    /// <summary>throw_ref: rethrows an exnref (traps on null).</summary>
    [DoesNotReturn]
    public static void ThrowRef(Value exnref, WasmCode code, int pc)
    {
        if (exnref.IsNullRef || exnref.GcRef is not ExnInstance)
        {
            Trap(MessageTemplate.WasmTrapRethrowNull, code, pc);
        }
        throw new WasmHostException(exnref);
    }

    /// <summary>Runtime_WasmReThrow: the legacy rethrow of a caught exception.</summary>
    [DoesNotReturn]
    public static void Rethrow(WasmHostException exception, WasmCode code, int pc) =>
        throw new WasmHostException(exception.ExnRef);

    public static bool ExnTagIs(WasmHostException exception, WasmInstanceData data, int tag) =>
        exception.ExnRef.GcRef is ExnInstance exn && exn.Tag.Equals(data.Tags[tag]);

    /// <summary>Field <paramref name="index"/> of the exception's payload.</summary>
    public static Value ExnField(WasmHostException exception, int index)
    {
        var exn = (ExnInstance)exception.ExnRef.GcRef!;
        int i = 0;
        foreach (Value v in exn.Fields)
        {
            if (i++ == index) return v;
        }
        throw new InvalidOperationException("exception field " + index);
    }

    public static Value ExnRefOf(WasmHostException exception) => exception.ExnRef;

    /// <summary>
    /// The filter of a try or try_table handler: whether it takes
    /// <paramref name="exception"/> (a wasm exception one of its catches
    /// takes, not passed on by a delegate, not thrown by a callee this frame
    /// tail-called).
    /// </summary>
    public static bool HandlerFilter(object exception, WasmCode code, int tagsConstant, int sp, int tryId, bool tailCall)
    {
        if (tailCall || exception is not WasmHostException e || SkipsHandler(e, sp, tryId)) return false;
        WasmInstanceData data = code.Instance!;
        foreach (int tag in (List<int>)code.Constants[tagsConstant])
        {
            if (tag < 0 || ExnTagIs(e, data, tag)) return true;
        }
        return false;
    }

    /// <summary>The filter of a legacy delegate: marks the exception for its target and declines it.</summary>
    public static bool DelegateFilter(object exception, int sp, int targetTryId)
    {
        if (exception is WasmHostException e) MarkDelegated(e, sp, targetTryId);
        return false;
    }

    /// <summary>
    /// Whether a handler (try <paramref name="tryId"/> of the frame at
    /// <paramref name="sp"/>) must pass the exception on: a delegate in this
    /// frame sent it to another try. The target clears the mark.
    /// </summary>
    public static bool SkipsHandler(WasmHostException exception, int sp, int tryId)
    {
        if (exception.DelegateFrame != sp) return false;
        if (exception.DelegateTarget == tryId)
        {
            exception.DelegateFrame = -1;
            return false;
        }
        return true;
    }

    /// <summary>A legacy delegate: marks the exception for the handlers of try <paramref name="targetTryId"/> (-1: the caller).</summary>
    public static void MarkDelegated(WasmHostException exception, int sp, int targetTryId)
    {
        exception.DelegateFrame = sp;
        exception.DelegateTarget = targetTryId;
    }
}
