// The code of compiled wasm functions: V8's WasmCode and the code table of
// src/wasm/wasm-code-manager.{h,cc}, the per-instance data compiled code reads
// (WasmTrustedInstanceData, src/wasm/wasm-objects.h), and the call stubs
// between calling conventions (the lazy-compile stub of the jump table,
// src/wasm/jump-table-assembler and WasmCompileLazy; the interpreter entry
// and the import call stubs of src/wasm/wrappers.cc).
//
// V8 emits machine code into a code space and calls through a jump table
// that starts pointing at the lazy-compile builtin. V8Sharp emits IL
// (DynamicMethods, compiled by RyuJIT) and calls through typed delegates: a
// function's delegate is first the lazy stub, which compiles the function and
// then calls it; once compiled, every slot that holds the function's
// delegate is patched to the compiled code (deviations.md, "WebAssembly").
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;

namespace V8Sharp.Wasm;

/// <summary>How compiled code holds a wasm value (V8: the machine representation of a ValueType).</summary>
public enum WasmKind : byte
{
    I32,
    I64,
    F32,
    F64,
    /// <summary>v128, held as a <see cref="Value"/> (the interpreter's VecRef form).</summary>
    S128,
    /// <summary>Any reference, held as a <see cref="Value"/> (the interpreter's form).</summary>
    Ref,
}

public static class WasmKinds
{
    public static WasmKind Of(ValType type) => type switch
    {
        ValType.I32 => WasmKind.I32,
        ValType.I64 => WasmKind.I64,
        ValType.F32 => WasmKind.F32,
        ValType.F64 => WasmKind.F64,
        ValType.V128 => WasmKind.S128,
        ValType.I8 or ValType.I16 => WasmKind.I32,
        _ => WasmKind.Ref,
    };

    public static Type ClrType(WasmKind kind) => kind switch
    {
        WasmKind.I32 => typeof(int),
        WasmKind.I64 => typeof(long),
        WasmKind.F32 => typeof(float),
        WasmKind.F64 => typeof(double),
        _ => typeof(Value),
    };

    public static char Letter(WasmKind kind) => kind switch
    {
        WasmKind.I32 => 'i',
        WasmKind.I64 => 'l',
        WasmKind.F32 => 'f',
        WasmKind.F64 => 'd',
        WasmKind.S128 => 's',
        _ => 'r',
    };

    public static WasmKind[] Of(ValType[] types)
    {
        var kinds = new WasmKind[types.Length];
        for (int i = 0; i < types.Length; i++) kinds[i] = Of(types[i]);
        return kinds;
    }
}

/// <summary>Conversions between compiled code's representation and the interpreter's <see cref="Value"/>.</summary>
public static class WasmValues
{
    public static Value FromI32(int v) => new(v);
    public static Value FromI64(long v) => new(v);
    public static Value FromF32(float v) => new(v);
    public static Value FromF64(double v) => new(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToI32(Value v) => v.Data.Int32;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToI64(Value v) => v.Data.Int64;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToF32(Value v) => v.Data.Float32;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToF64(Value v) => v.Data.Float64;

    internal static readonly MethodInfo[] s_from =
    [
        typeof(WasmValues).GetMethod(nameof(FromI32))!, typeof(WasmValues).GetMethod(nameof(FromI64))!,
        typeof(WasmValues).GetMethod(nameof(FromF32))!, typeof(WasmValues).GetMethod(nameof(FromF64))!,
    ];

    internal static readonly MethodInfo[] s_to =
    [
        typeof(WasmValues).GetMethod(nameof(ToI32))!, typeof(WasmValues).GetMethod(nameof(ToI64))!,
        typeof(WasmValues).GetMethod(nameof(ToF32))!, typeof(WasmValues).GetMethod(nameof(ToF64))!,
    ];

    /// <summary>Emits the conversion of the value on the IL stack to a <see cref="Value"/>.</summary>
    internal static void EmitToValue(ILGenerator il, WasmKind kind)
    {
        if (kind < WasmKind.S128) il.Emit(OpCodes.Call, s_from[(int)kind]);
    }

    /// <summary>Emits the conversion of the <see cref="Value"/> on the IL stack to <paramref name="kind"/>.</summary>
    internal static void EmitFromValue(ILGenerator il, WasmKind kind)
    {
        if (kind < WasmKind.S128) il.Emit(OpCodes.Call, s_to[(int)kind]);
    }
}

/// <summary>
/// A function signature in compiled code's calling convention: the method
/// <c>R f(WasmCode code, P0 p0, ...)</c> (R: the first result, or void; further
/// results go through <see cref="CompiledFrames.Returns"/>) and the delegate
/// type <c>R (P0, ...)</c> of its closed delegates. Also builds the stubs of the
/// signature (V8: the wrappers and the lazy-compile entry of the jump table).
/// </summary>
public sealed class WasmSignature
{
    static readonly ConcurrentDictionary<string, WasmSignature> s_cache = new();

    public readonly WasmKind[] Params;
    public readonly WasmKind[] Results;
    public readonly Type[] ParamTypes;
    public readonly Type ReturnType;
    public readonly Type[] MethodParameterTypes;
    public readonly Type DelegateType;
    public readonly MethodInfo Invoke;
    readonly string _key;

    JSToWasmWrapper? _jsWrapper;
    bool _jsWrapperBuilt;

    /// <summary>The compiled JS-to-wasm wrapper, or null if the signature has none.</summary>
    public JSToWasmWrapper? JSWrapper
    {
        get
        {
            if (!_jsWrapperBuilt)
            {
                _jsWrapper = WasmJsFast.HasFastWrapper(this) ? WasmJsFast.Build(this) : null;
                _jsWrapperBuilt = true;
            }
            return _jsWrapper;
        }
    }

    DynamicMethod? _lazyStub;
    DynamicMethod? _valueStub;
    Action<WasmCode, ExecContext>? _interpreterEntry;

    WasmSignature(WasmKind[] parameters, WasmKind[] results, string key)
    {
        Params = parameters;
        Results = results;
        _key = key;
        ParamTypes = new Type[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) ParamTypes[i] = WasmKinds.ClrType(parameters[i]);
        ReturnType = results.Length == 0 ? typeof(void) : WasmKinds.ClrType(results[0]);
        MethodParameterTypes = [typeof(WasmCode), .. ParamTypes];
        DelegateType = System.Linq.Expressions.Expression.GetDelegateType([.. ParamTypes, ReturnType]);
        Invoke = DelegateType.GetMethod("Invoke")!;
    }

    public static WasmSignature Get(FunctionType type) =>
        Get(WasmKinds.Of(type.ParameterTypes.Types), WasmKinds.Of(type.ResultType.Types));

    public static WasmSignature Get(WasmKind[] parameters, WasmKind[] results)
    {
        var chars = new char[parameters.Length + 1 + results.Length];
        int n = 0;
        foreach (WasmKind k in parameters) chars[n++] = WasmKinds.Letter(k);
        chars[n++] = '_';
        foreach (WasmKind k in results) chars[n++] = WasmKinds.Letter(k);
        string key = new(chars);
        return s_cache.TryGetValue(key, out WasmSignature? sig) ? sig : s_cache.GetOrAdd(key, _ => new WasmSignature(parameters, results, key));
    }

    public override string ToString() => _key;

    static DynamicMethod NewStub(string name, Type ret, Type[] parameters) =>
        new(name, ret, parameters, typeof(WasmSignature).Module, skipVisibility: true);

    /// <summary>
    /// The lazy-compile stub (V8: the jump table's initial target,
    /// WasmCompileLazy): compiles the function, then calls it.
    /// </summary>
    public Delegate CreateLazyStub(WasmCode code)
    {
        DynamicMethod stub = _lazyStub ??= BuildForwardingStub("wasm-lazy:" + _key,
            typeof(WasmCode).GetMethod(nameof(WasmCode.CompileLazily))!);
        return stub.CreateDelegate(DelegateType, code);
    }

    DynamicMethod BuildForwardingStub(string name, MethodInfo resolve)
    {
        DynamicMethod m = NewStub(name, ReturnType, MethodParameterTypes);
        ILGenerator il = m.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, resolve);
        il.Emit(OpCodes.Castclass, DelegateType);
        for (int i = 0; i < ParamTypes.Length; i++) il.Emit(OpCodes.Ldarg, (short)(i + 1));
        il.Emit(OpCodes.Callvirt, Invoke);
        il.Emit(OpCodes.Ret);
        return m;
    }

    /// <summary>
    /// The stub of a function that compiled code calls with <see cref="Value"/>s:
    /// an import from the host (V8: the wasm-to-JS wrapper's entry) or a
    /// function the compiler left to the interpreter.
    /// </summary>
    public Delegate CreateValueStub(WasmCode code)
    {
        DynamicMethod stub = _valueStub ??= BuildValueStub();
        return stub.CreateDelegate(DelegateType, code);
    }

    DynamicMethod BuildValueStub()
    {
        DynamicMethod m = NewStub("wasm-to-values:" + _key, ReturnType, MethodParameterTypes);
        ILGenerator il = m.GetILGenerator();
        LocalBuilder args = il.DeclareLocal(typeof(Value[]));
        il.Emit(OpCodes.Ldc_I4, Params.Length);
        il.Emit(OpCodes.Newarr, typeof(Value));
        il.Emit(OpCodes.Stloc, args);
        for (int i = 0; i < Params.Length; i++)
        {
            il.Emit(OpCodes.Ldloc, args);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldarg, (short)(i + 1));
            WasmValues.EmitToValue(il, Params[i]);
            il.Emit(OpCodes.Stelem, typeof(Value));
        }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, args);
        il.Emit(OpCodes.Call, typeof(WasmCode).GetMethod(nameof(WasmCode.CallWithValues))!);
        if (Results.Length == 0)
        {
            il.Emit(OpCodes.Pop);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem, typeof(Value));
            WasmValues.EmitFromValue(il, Results[0]);
        }
        il.Emit(OpCodes.Ret);
        return m;
    }

    /// <summary>
    /// The interpreter's entry into compiled code: pops the arguments from the
    /// operand stack, calls the function's current code and pushes the results.
    /// </summary>
    public Action<WasmCode, ExecContext> InterpreterEntry => _interpreterEntry ??= BuildInterpreterEntry();

    Action<WasmCode, ExecContext> BuildInterpreterEntry()
    {
        DynamicMethod m = NewStub("wasm-entry:" + _key, typeof(void), [typeof(WasmCode), typeof(ExecContext)]);
        ILGenerator il = m.GetILGenerator();
        FieldInfo opStack = typeof(ExecContext).GetField(nameof(ExecContext.OpStack))!;
        var args = new LocalBuilder[Params.Length];
        for (int i = Params.Length - 1; i >= 0; i--)
        {
            args[i] = il.DeclareLocal(ParamTypes[i]);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldfld, opStack);
            il.Emit(OpCodes.Call, typeof(OpStack).GetMethod(Params[i] switch
            {
                WasmKind.I32 => nameof(OpStack.PopI32),
                WasmKind.I64 => nameof(OpStack.PopI64),
                WasmKind.F32 => nameof(OpStack.PopF32),
                WasmKind.F64 => nameof(OpStack.PopF64),
                _ => nameof(OpStack.PopAny),
            })!);
            il.Emit(OpCodes.Stloc, args[i]);
        }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, typeof(WasmCode).GetField(nameof(WasmCode.Entry))!);
        il.Emit(OpCodes.Castclass, DelegateType);
        for (int i = 0; i < Params.Length; i++) il.Emit(OpCodes.Ldloc, args[i]);
        il.Emit(OpCodes.Callvirt, Invoke);
        if (Results.Length > 0)
        {
            LocalBuilder r0 = il.DeclareLocal(ReturnType);
            il.Emit(OpCodes.Stloc, r0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldfld, opStack);
            il.Emit(OpCodes.Ldloc, r0);
            WasmValues.EmitToValue(il, Results[0]);
            il.Emit(OpCodes.Call, typeof(OpStack).GetMethod(nameof(OpStack.PushValue))!);
            for (int i = 1; i < Results.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, opStack);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, typeof(ExecContext).GetField(nameof(ExecContext.CompiledFrames))!);
                il.Emit(OpCodes.Ldfld, typeof(CompiledFrames).GetField(nameof(CompiledFrames.Returns))!);
                il.Emit(OpCodes.Ldc_I4, i - 1);
                il.Emit(OpCodes.Ldelem, typeof(Value));
                il.Emit(OpCodes.Call, typeof(OpStack).GetMethod(nameof(OpStack.PushValue))!);
            }
        }
        il.Emit(OpCodes.Ret);
        return m.CreateDelegate<Action<WasmCode, ExecContext>>();
    }
}

/// <summary>The state of a function's code (V8: the jump table slot's target).</summary>
public enum WasmCodeState
{
    /// <summary>Not compiled yet: the entry is the lazy stub.</summary>
    Lazy,
    /// <summary>Compiled to IL.</summary>
    Compiled,
    /// <summary>The compiler bailed out: the entry calls the interpreter.</summary>
    Interpreted,
    /// <summary>A host function (an import from JavaScript).</summary>
    Host,
}

/// <summary>
/// The code of one wasm function (V8: WasmCode, and its jump table slot):
/// the typed delegate that calls it in compiled code's convention.
/// </summary>
public sealed class WasmCode : ICompiledFunctionCode
{
    public readonly WasmEngine Engine;
    /// <summary>The instance the function belongs to (null for a host function).</summary>
    public readonly WasmInstanceData? Instance;
    public readonly IFunctionInstance Function;
    public readonly FuncAddr Address;
    /// <summary>The function index in its own module (-1 for a host function).</summary>
    public readonly int FunctionIndex;
    public readonly WasmSignature Signature;
    /// <summary>The function's defined type, for call_indirect's signature check.</summary>
    public readonly DefType? DefType;

    /// <summary>The current entry (a delegate of <see cref="WasmSignature.DelegateType"/>).</summary>
    public Delegate Entry = null!;

    public WasmCodeState State;

    /// <summary>The objects the compiled code reads (V8: the code's relocation targets).</summary>
    public object[] Constants = [];

    /// <summary>The compiled method (for direct calls from functions compiled later).</summary>
    public DynamicMethod? Method;

    /// <summary>The last type a call_indirect matched this function against (the check's cache).</summary>
    public DefType? LastMatchedType;

    /// <summary>The slots holding <see cref="Entry"/> (patched when the code changes).</summary>
    readonly List<(Delegate[] Slots, int Index)> _slots = [];

    /// <summary>Why the compiler left the function to the interpreter (V8: the Liftoff bailout reason).</summary>
    public string? BailoutReason;

    internal WasmCode(WasmEngine engine, WasmInstanceData? instance, IFunctionInstance function, FuncAddr address,
        int functionIndex)
    {
        Engine = engine;
        Instance = instance;
        Function = function;
        Address = address;
        FunctionIndex = functionIndex;
        Signature = WasmSignature.Get(function.Type);
        DefType = function switch
        {
            FunctionInstance f => f.DefType,
            HostFunction { DefType: { } d } => d,
            _ => null,
        };
        if (function is FunctionInstance && instance != null)
        {
            State = WasmCodeState.Lazy;
            Entry = Signature.CreateLazyStub(this);
        }
        else
        {
            // A host function, or a wasm function of an instance that is not
            // compiled (it runs in the interpreter).
            State = function is FunctionInstance ? WasmCodeState.Interpreted : WasmCodeState.Host;
            Entry = Signature.CreateValueStub(this);
        }
    }

    internal void AddSlot(Delegate[] slots, int index)
    {
        slots[index] = Entry;
        lock (_slots) _slots.Add((slots, index));
    }

    void SetEntry(Delegate entry)
    {
        Entry = entry;
        lock (_slots)
        {
            foreach (var (slots, index) in _slots) slots[index] = entry;
        }
    }

    /// <summary>
    /// WasmCompileLazy: compiles the function if it is not yet, and returns
    /// its entry (the compiled code, or the interpreter's stub on a bailout).
    /// </summary>
    public Delegate CompileLazily()
    {
        if (State == WasmCodeState.Lazy) Compile();
        return Entry;
    }

    /// <summary>Compiles the function (once).</summary>
    internal void Compile()
    {
        lock (this)
        {
            if (State != WasmCodeState.Lazy) return;
            var function = (FunctionInstance)Function;
            Delegate? compiled = Instance!.Compiler.CompileFunction(this, out string? bailout);
            if (compiled is null)
            {
                BailoutReason = bailout;
                State = WasmCodeState.Interpreted;
                function.NotCompilable = true;
                SetEntry(Signature.CreateValueStub(this));
                return;
            }
            State = WasmCodeState.Compiled;
            SetEntry(compiled);
            function.Compiled = this;
        }
    }

    /// <summary>
    /// Calls the function with <see cref="Value"/>s: a host function directly,
    /// a wasm function in the interpreter. Extra results go to the frames'
    /// return buffer, as compiled code returns them.
    /// </summary>
    public Value[] CallWithValues(Value[] args)
    {
        Value[] results = RuntimeWasm.CallWithValues(this, args);
        if (results.Length > 1)
        {
            Value[] returns = Engine.ExecContext.CompiledFrames.EnsureReturns(results.Length - 1);
            Array.Copy(results, 1, returns, 0, results.Length - 1);
        }
        return results.Length == 0 ? [default] : results;
    }

    /// <summary>The interpreter (or the embedder, through it) calls compiled code.</summary>
    public void InvokeFromInterpreter(ExecContext context)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new WasmRuntimeException("Runtime call stack exhausted", context.SnapshotCallStack())
            {
                CalleeFuncAddr = Address.Value,
            };
        }
        CompiledFrames frames = context.CompiledFrames;
        int sp = frames.Sp;
        int segments = frames.SegmentCount;
        frames.PushSegment(context.StackHeight, context.InstructionPointer);
        try
        {
            Signature.InterpreterEntry(this, context);
        }
        finally
        {
            frames.Sp = sp;
            frames.PopSegments(segments);
        }
    }

    public override string ToString() => $"WasmCode[{Address.Value} #{FunctionIndex} {Signature} {State}]";
}

/// <summary>
/// What compiled code of one instance reads (V8: WasmTrustedInstanceData):
/// the function table, memories, globals, tables and tags of the instance.
/// </summary>
public sealed class WasmInstanceData : ICompiledModule
{
    public readonly WasmEngine Engine;
    public readonly ModuleInstance Module;
    public readonly ExecContext Context;
    public readonly CompiledFrames Frames;
    public readonly StackGuard StackGuard;
    /// <summary>The module's wire bytes (the compiler decodes function bodies from them).</summary>
    public readonly byte[] WireBytes;
    /// <summary>The functions' entries by function index (V8: the jump table, and the imports' call targets).</summary>
    public readonly Delegate[] Functions;
    public readonly WasmCode[] Code;
    public readonly MemoryInstance[] Memories;
    public readonly GlobalInstance[] Globals;
    public readonly TableInstance[] Tables;
    public readonly TagAddr[] Tags;
    /// <summary>The interpreter frame an instruction runs with when compiled code delegates it to the interpreter.</summary>
    public readonly Frame InterpreterFrame;
    public readonly int ImportedFunctionCount;
    internal readonly WasmModuleCompiler Compiler;

    internal WasmInstanceData(WasmEngine engine, ModuleInstance module, byte[] wireBytes, WasmModuleCompiler compiler)
    {
        Engine = engine;
        Module = module;
        WireBytes = wireBytes;
        Compiler = compiler;
        Context = engine.Runtime.GetExecContext();
        Frames = Context.CompiledFrames;
        StackGuard = engine.Isolate.StackGuard;
        Store store = engine.Store;
        var funcs = new List<FuncAddr>();
        foreach (FuncAddr a in module.FuncAddrs) funcs.Add(a);
        ImportedFunctionCount = module.Repr.ImportedFunctions.Count;
        Functions = new Delegate[funcs.Count];
        Code = new WasmCode[funcs.Count];
        Memories = new MemoryInstance[module.MemAddrs.Count];
        for (int i = 0; i < Memories.Length; i++) Memories[i] = store[module.MemAddrs[(Wacs.Core.Types.MemIdx)i]];
        Globals = new GlobalInstance[module.GlobalAddrs.Count];
        for (int i = 0; i < Globals.Length; i++) Globals[i] = store[module.GlobalAddrs[(GlobalIdx)i]];
        Tables = new TableInstance[module.TableAddrs.Count];
        for (int i = 0; i < Tables.Length; i++) Tables[i] = store[module.TableAddrs[(TableIdx)i]];
        var tags = new List<TagAddr>();
        foreach (TagAddr t in module.TagAddrs) tags.Add(t);
        Tags = [.. tags];
        InterpreterFrame = new Frame { Module = module };
        // Register this instance's own functions first, so that imports of
        // them (from this instance) find them.
        for (int i = 0; i < funcs.Count; i++)
        {
            if (store[funcs[i]] is FunctionInstance f && f.Module == module)
            {
                var code = new WasmCode(engine, this, f, funcs[i], i);
                engine.RegisterCode(code);
                Code[i] = code;
            }
        }
        for (int i = 0; i < funcs.Count; i++)
        {
            Code[i] ??= engine.GetOrCreateCode(funcs[i]);
            Code[i].AddSlot(Functions, i);
        }
    }

    /// <summary>The compiled code of <paramref name="function"/> (compiled now if needed), or null to interpret it.</summary>
    public ICompiledFunctionCode? GetCode(FunctionInstance function)
    {
        WasmCode? code = Engine.FindCode(function.Address);
        if (code is null || code.Instance != this) return null;
        if (code.State == WasmCodeState.Lazy) code.Compile();
        return code.State == WasmCodeState.Compiled ? code : null;
    }
}
