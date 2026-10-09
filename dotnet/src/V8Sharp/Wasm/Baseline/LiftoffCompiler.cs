// Port of src/wasm/baseline/liftoff-compiler.cc (the LiftoffCompiler
// interface of the function body decoder) together with the decoding loop
// of src/wasm/function-body-decoder-impl.h (WasmFullDecoder: immediates, the
// control stack, the dispatch on opcodes), emitting IL instead of machine
// code.
//
// The module was validated when it was compiled (WasmEngine.Compile), so the
// decoder does not validate again: it decodes, tracks the value kinds and
// the control stack, and emits. Unreachable code is decoded and not emitted,
// as in Liftoff. A function that uses something the compiler does not
// support bails out (V8: Liftoff's bailout to TurboFan) and stays in the
// interpreter.
//
// The generated method is
//   R f(WasmCode code, P0 p0, ...)
// with the wasm parameters as IL arguments, the wasm locals as IL locals and
// the value stack as described in LiftoffAssembler.cs. Callees chosen by
// WasmInliningTree are decoded into the same method by a LiftoffCompiler of
// their own that shares the method's IL (LiftoffCompiler.Inlining.cs).
using System.Reflection;
using System.Reflection.Emit;
using Wacs.Core.Instructions;
using Wacs.Core.Instructions.Reference;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using WasmModule = Wacs.Core.Module;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

/// <summary>Thrown to abandon compilation (V8: LiftoffCompiler::unsupported).</summary>
internal sealed class LiftoffBailout(string reason) : Exception(reason);

internal sealed partial class LiftoffCompiler
{
    enum ControlKind : byte { Function, Block, Loop, If, TryTable, Try, TryCatch }

    sealed class Control
    {
        public ControlKind Kind;
        public WasmKind[] Params = [];
        public WasmKind[] Results = [];
        /// <summary>The value-stack height below the block's parameters.</summary>
        public int Base;
        /// <summary>The branch target: a loop's header, otherwise the end.</summary>
        public Label Label;
        public Label ElseLabel;
        public bool ElseSeen;
        /// <summary>Whether the block began in reachable code (else: the whole block is skipped).</summary>
        public bool Reachable;
        public bool BranchedTo;
        /// <summary>The IL exception-region depth at the label (a branch from deeper needs leave).</summary>
        public int TryDepth;
        public List<VarState>? EntryState;
        // Exception handling.
        public int TryId;
        public LocalBuilder? Exception;
        public Label Dispatch;
        public List<(int Tag, Label Body)>? Catches;
        /// <summary>The tags the handler takes (-1: catch_all), read by its filter.</summary>
        public List<int>? HandlerTags;
        public int HandlerTagsConstant;
        public bool InHandler;
        /// <summary>A loop's wire-byte offset (the tier-up check's jump distance).</summary>
        public int StartOffset;

        public WasmKind[] BranchKinds => Kind == ControlKind.Loop ? Params : Results;
    }

    readonly WasmCode _code;
    readonly WasmInstanceData _data;
    readonly FunctionInstance _function;
    readonly ModuleInstance _module;
    readonly byte[] _bytes;
    int _pos;
    readonly uint[] _offsets;
    readonly InstructionBase[] _instructions;

    readonly DynamicMethod _method;
    readonly ILGenerator _il;
    readonly LiftoffAssembler _asm;
    readonly List<object> _constants;
    readonly List<Control> _control = [];

    /// <summary>The function the method is for (this compiler's function unless it is inlined).</summary>
    readonly WasmCode _rootCode;
    /// <summary>Set when this compiler decodes a callee inlined into the method.</summary>
    readonly InlineFrame? _inline;
    /// <summary>The inlining decisions of this frame (null: no inlining).</summary>
    WasmInliningTree? _plan;
    /// <summary>The tier-up compile: speculative inlining from feedback.</summary>
    readonly bool _tierUp;
    /// <summary>The code collects feedback at its call_indirect/call_ref sites and counts down its tiering budget.</summary>
    bool _collectFeedback;
    /// <summary>The number of calls decoded so far (V8: the feedback slot of the next call).</summary>
    int _callIndex;
    /// <summary>The calls inlined into the method (counted by the method's own compiler).</summary>
    int _inlinedCalls;
    /// <summary>The method's wasm locals, inlined frames' included (the stack check's frame size).</summary>
    int _totalLocals;

    WasmKind[] _localKinds = [];
    int _instIndex;
    /// <summary>Instructions run by the interpreter's instruction objects (coverage statistics).</summary>
    int _genericCount;
    bool _reachable = true;
    int _tryDepth;
    int _nextTryId;

    // Prologue locals.
    LocalBuilder _dataLocal = null!;
    LocalBuilder _framesLocal = null!;
    LocalBuilder _spLocal = null!;
    LocalBuilder _pcsLocal = null!;
    LocalBuilder? _stackGuardLocal;
    /// <summary>Whether the method (with its inlined callees) has loops.</summary>
    bool _hasLoops;
    // The cached memories (array and size) of memories that are not shared.
    LocalBuilder?[] _memArray = [];
    LocalBuilder?[] _memSize = [];
    // A managed pointer to each cached memory's first byte (accesses add the
    // address to it: no array load or null check per access).
    LocalBuilder?[] _memBase = [];
    // An asm.js instance's memory is its heap buffer, which never grows or
    // changes: the cached memory is read once, at entry.
    bool _memoriesFixed;
    bool _cacheMemories;

    // The call_indirect/call_ref sites of the method (its own and its
    // inlined callees', by function and instruction index), each held in an
    // IL local loaded on entry: RyuJIT cannot know the constants do not
    // change, so it would load and cast them at every use in a loop.
    Dictionary<(int Function, int Inst), (LocalBuilder Local, WasmCallSite Site)> _callSites = [];
    // The elements of the tables call_indirect reads, by table (the
    // instance's: the code serves every instance).
    Dictionary<int, LocalBuilder> _tableElements = [];

    // The return label for returns from inside exception regions.
    Label _returnLabel;
    bool _returnLabelUsed;
    LocalBuilder? _returnValue;

    LiftoffCompiler(WasmCode code, bool tierUp)
    {
        _code = code;
        _rootCode = code;
        _tierUp = tierUp;
        // A tier-up keeps the constants of the code it replaces at their
        // indices: activations of that code still read them.
        _constants = tierUp ? [.. code.Constants] : [];
        _data = code.Instance!;
        _function = (FunctionInstance)code.Function;
        _module = _function.Module;
        _bytes = _data.WireBytes;
        _offsets = _function.Definition.InstructionOffsets;
        _instructions = [.. _function.Body.Instructions.Flatten()];
        WasmSignature sig = code.Signature;
        string name = "wasm-function[" + code.FunctionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
        _method = new DynamicMethod(name, sig.ReturnType, sig.MethodParameterTypes, typeof(LiftoffCompiler).Module, skipVisibility: true);
        _il = _method.GetILGenerator(Math.Max(256, _offsets.Length * 24));
        _asm = new LiftoffAssembler(_il);
        _asm.LoadNaN = (kind, bits) =>
        {
            object boxed = kind == WasmKind.F32 ? (object)BitConverter.Int32BitsToSingle((int)bits) : BitConverter.Int64BitsToDouble(bits);
            int k = AddConstant(boxed);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, s_codeConstants);
            _il.Emit(OpCodes.Ldc_I4, k);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Unbox_Any, WasmKinds.ClrType(kind));
        };
    }

    /// <summary>
    /// Compiles <paramref name="code"/>'s function (V8: ExecuteLiftoffCompilation).
    /// Returns its entry, or null with the reason if the compiler bailed out.
    /// </summary>
    public static Delegate? Compile(WasmCode code, out string? bailout) => Compile(code, false, out bailout, out _, out _);

    /// <summary>
    /// <see cref="Compile(WasmCode, out string?)"/>, also counting the
    /// function's instructions and those left to the interpreter's
    /// instruction objects.
    /// </summary>
    public static Delegate? Compile(WasmCode code, bool tierUp, out string? bailout, out int instructions, out int generic)
    {
        var compiler = new LiftoffCompiler(code, tierUp);
        instructions = compiler._offsets.Length;
        generic = 0;
        bool inlining = InliningEnabled(code.Instance!);
        try
        {
            compiler.CompileFunction(inlining);
        }
        catch (LiftoffBailout e)
        {
            if (!inlining || compiler._plan is null || compiler._plan.InlinedCount == 0)
            {
                bailout = e.Message;
                return null;
            }
            // An inlined callee bailed out: compile without inlining (its
            // calls then run it in the interpreter, as V8 calls code it
            // could not inline).
            compiler = new LiftoffCompiler(code, tierUp);
            try
            {
                compiler.CompileFunction(inlining: false);
            }
            catch (LiftoffBailout e2)
            {
                bailout = e2.Message;
                return null;
            }
        }
        bailout = null;
        generic = compiler._genericCount;
        code.Constants = [.. compiler._constants];
        code.Method = compiler._method;
        code.InlinedCalls = compiler._inlinedCalls;
        if (compiler._collectFeedback)
        {
            code.MayTierUp = true;
            code.TieringBudget = code.Instance!.Engine.Isolate.Flags.wasm_tiering_budget;
        }
        if (compiler._plan is not null && compiler.Trace)
        {
            // V8 prints the node count of the TurboFan graph; the IL size stands in.
            Console.Out.Write($"[function {code.FunctionIndex}: emitted {compiler._il.ILOffset} nodes]\n");
        }
        // Code that collects feedback or was compiled from it (speculative
        // targets are this instance's functions) stays with its instance.
        code.Shareable = !compiler._instanceSpecific && !compiler._collectFeedback && !tierUp;
        code.ILSize = compiler._il.ILOffset;
        return compiler._method.CreateDelegate(code.Signature.DelegateType, code);
    }

    /// <summary>Whether to print V8's --trace-wasm-inlining (for the tier-up compile, V8's TurboFan compile).</summary>
    bool Trace => _tierUp && _data.Engine.Isolate.Flags.trace_wasm_inlining;

    /// <summary>Whether functions are inlined (--wasm-inlining; V8 also turns it off for --trace-wasm and code coverage).</summary>
    static bool InliningEnabled(WasmInstanceData data) => data.Engine.Isolate.Flags.wasm_inlining;

    static void Unsupported(string reason) => throw new LiftoffBailout(reason);

    /// <summary>
    /// Set when the code depends on its instance (a constant that is the
    /// instance's: an interpreter instruction object, a function reference,
    /// a catch's tags; or a direct call to code that is not shared), so it
    /// cannot serve the module's other instances.
    /// </summary>
    bool _instanceSpecific;

    int AddConstant(object value)
    {
        _constants.Add(value);
        return _constants.Count - 1;
    }

    // ---- Decoding --------------------------------------------------------------

    byte ReadU8() => _bytes[_pos++];

    uint ReadU32()
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _bytes[_pos++];
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    ulong ReadU64()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _bytes[_pos++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    int ReadI32() => (int)ReadSigned(32);

    long ReadI64() => ReadSigned(64);

    long ReadSigned(int bits)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = _bytes[_pos++];
            result |= (long)(b & 0x7f) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0) result |= -1L << shift;
        return result;
    }

    /// <summary>A heap type (s33): an abstract type (negative) or a type index.</summary>
    long ReadHeapType() => ReadSigned(33);

    /// <summary>A value type, as the kind compiled code holds it in.</summary>
    WasmKind ReadValueKind()
    {
        byte b = ReadU8();
        switch (b)
        {
            case 0x7f: return WasmKind.I32;
            case 0x7e: return WasmKind.I64;
            case 0x7d: return WasmKind.F32;
            case 0x7c: return WasmKind.F64;
            case 0x7b: return WasmKind.S128;
            case 0x63:
            case 0x64:
                ReadHeapType();
                return WasmKind.Ref;
            default:
                // A reference type shorthand.
                return WasmKind.Ref;
        }
    }

    /// <summary>A block type: its parameters and results.</summary>
    (WasmKind[] Params, WasmKind[] Results) ReadBlockType()
    {
        byte b = _bytes[_pos];
        if (b == 0x40)
        {
            _pos++;
            return ([], []);
        }
        if (b is 0x63 or 0x64 || (b >= 0x40 && b < 0x80))
        {
            return ([], [ReadValueKind()]);
        }
        long index = ReadSigned(33);
        var type = (FunctionType)_module.Types[(TypeIdx)(uint)index].Expansion;
        return (WasmKinds.Of(type.ParameterTypes.Types), WasmKinds.Of(type.ResultType.Types));
    }

    // ---- The function --------------------------------------------------------------

    void CompileFunction(bool inlining)
    {
        // A function whose call_indirect/call_ref targets can be inlined
        // once there is feedback collects it and tiers up (V8: Liftoff code
        // with feedback, then TurboFan).
        _collectFeedback = !_tierUp && inlining && _data.Engine.Isolate.Flags.wasm_inlining_call_indirect &&
                           HasIndirectCalls(_function);
        if (_collectFeedback) _code.Feedback ??= new WasmFunctionFeedback();
        if (inlining)
        {
            // The trace is V8's of the optimizing compile: the tier-up.
            _plan = WasmInliningTree.CreateRoot(_data, _code.FunctionIndex, Trace);
        }
        _pos = (int)_function.Definition.BodyOffset;
        // The locals declarations (decoded by WACS: Definition.Locals).
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
        _asm.ParamCount = parameters.Length;
        _asm.Locals = new LocalBuilder?[_localKinds.Length];
        for (int i = parameters.Length; i < _localKinds.Length; i++)
        {
            _asm.Locals[i] = _il.DeclareLocal(WasmKinds.ClrType(_localKinds[i]));
        }

        ScanFunction();
        EmitPrologue(locals, parameters.Length);
        if (_collectFeedback)
        {
            // FunctionTypeFeedback::num_invocations.
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, s_codeFeedback);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldfld, s_feedbackInvocations);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stfld, s_feedbackInvocations);
        }

        PrepareCallSites();

        DecodeBody(WasmKinds.Of(type.ResultType.Types));
        // The end of the body is not reached by falling through (every path
        // returned or threw); IL must not fall off the end.
        EmitUnreachableEnd();
        if (_returnLabelUsed)
        {
            _il.MarkLabel(_returnLabel);
            if (_returnValue != null) _il.Emit(OpCodes.Ldloc, _returnValue);
            EmitLeaveFrame();
            _il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// The decoding loop (WasmFullDecoder::DecodeFunctionBody) over the
    /// function's body, from the first instruction to the end of the function.
    /// </summary>
    void DecodeBody(WasmKind[] results)
    {
        var function = new Control
        {
            Kind = ControlKind.Function,
            Results = results,
            Base = 0,
            Label = _il.DefineLabel(),
            Reachable = true,
            TryDepth = _tryDepth,
        };
        _control.Add(function);

        while (_control.Count > 0)
        {
            if (_instIndex >= _offsets.Length || _offsets[_instIndex] != _pos)
            {
                Unsupported("instruction offsets do not match the interpreter's");
            }
            DecodeInstruction();
            _instIndex++;
            _asm.ReleaseTemps();
        }
        if (_instIndex != _offsets.Length) Unsupported("instruction count does not match the interpreter's");
    }

    /// <summary>Whether a function has call_indirect, call_ref or their tail-call forms.</summary>
    bool HasIndirectCalls(FunctionInstance function)
    {
        foreach (uint offset in function.Definition.InstructionOffsets)
        {
            if (_bytes[offset] is 0x11 or 0x13 or 0x14 or 0x15) return true;
        }
        return false;
    }

    /// <summary>
    /// Looks at the instructions before emitting: which memories the function
    /// and the functions inlined into it access, and how many locals they have.
    /// </summary>
    void ScanFunction()
    {
        bool accessesMemory = false;
        _totalLocals = _localKinds.Length;
        foreach (InstructionBase inst in _instructions)
        {
            byte op = (byte)inst.Op.x00;
            if (op is >= 0x28 and <= 0x40 || inst is InstAsmJs asm && InstAsmJs.AccessSize(asm.Code) != 0)
            {
                accessesMemory = true;
            }
            else if (op == 0x03)
            {
                _hasLoops = true;
            }
        }
        if (_plan is not null)
        {
            foreach (WasmInliningTree node in _plan.InlinedNodes())
            {
                if (node == _plan) continue;
                var f = (FunctionInstance)_data.Code[node.FunctionIndex].Function;
                _totalLocals += f.Type.ParameterTypes.Arity + f.Locals.Length;
                foreach (uint offset in f.Definition.InstructionOffsets)
                {
                    byte op = _bytes[offset];
                    if (op is >= 0x28 and <= 0x40 or WasmOpcodes.kAsmJsPrefix) accessesMemory = true;
                    else if (op == 0x03) _hasLoops = true;
                }
            }
        }
        _cacheMemories = accessesMemory && _data.Memories.Length is > 0 and <= 4;
    }

    static readonly FieldInfo s_codeInstance = typeof(WasmCode).GetField(nameof(WasmCode.Instance))!;
    static readonly FieldInfo s_dataTables = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Tables))!;
    static readonly FieldInfo s_tableElements = typeof(TableInstance).GetField(nameof(TableInstance.Elements))!;
    static readonly FieldInfo s_codeConstants = typeof(WasmCode).GetField(nameof(WasmCode.Constants))!;
    static readonly FieldInfo s_codeFeedback = typeof(WasmCode).GetField(nameof(WasmCode.Feedback))!;
    static readonly FieldInfo s_feedbackInvocations = typeof(WasmFunctionFeedback).GetField(nameof(WasmFunctionFeedback.Invocations))!;
    static readonly FieldInfo s_codeTieringBudget = typeof(WasmCode).GetField(nameof(WasmCode.TieringBudget))!;
    static readonly FieldInfo s_codeAddress = typeof(WasmCode).GetField(nameof(WasmCode.Address))!;
    static readonly FieldInfo s_funcAddrValue = typeof(FuncAddr).GetField(nameof(FuncAddr.Value))!;
    static readonly FieldInfo s_dataFrames = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Frames))!;
    static readonly FieldInfo s_dataStackGuard = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.StackGuard))!;
    static readonly FieldInfo s_dataMemories = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Memories))!;
    static readonly FieldInfo s_dataFunctions = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Functions))!;
    static readonly FieldInfo s_dataCode = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Code))!;
    static readonly FieldInfo s_dataGlobals = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Globals))!;
    static readonly FieldInfo s_framesSp = typeof(CompiledFrames).GetField(nameof(CompiledFrames.Sp))!;
    static readonly FieldInfo s_framesLimit = typeof(CompiledFrames).GetField(nameof(CompiledFrames.Limit))!;
    static readonly FieldInfo s_framesFunc = typeof(CompiledFrames).GetField(nameof(CompiledFrames.Func))!;
    static readonly FieldInfo s_framesPc = typeof(CompiledFrames).GetField(nameof(CompiledFrames.Pc))!;
    static readonly FieldInfo s_framesReturns = typeof(CompiledFrames).GetField(nameof(CompiledFrames.Returns))!;
    static readonly FieldInfo s_memoryData = typeof(MemoryInstance).GetField(nameof(MemoryInstance.Data))!;
    static readonly MethodInfo s_memoryByteLength = typeof(MemoryInstance).GetProperty(nameof(MemoryInstance.ByteLength))!.GetMethod!;
    static readonly MethodInfo s_hasPendingInterrupts = typeof(StackGuard).GetProperty(nameof(StackGuard.HasPendingInterrupts))!.GetMethod!;

    /// <summary>
    /// The frame setup (LiftoffCompiler::StartFunction and the stack check of
    /// StartFunctionBody): records the frame, checks the stack, caches the
    /// memories and initializes the locals that are not zero by default.
    /// </summary>
    void EmitPrologue(WasmKind[] locals, int paramCount)
    {
        ILGenerator il = _il;
        _dataLocal = il.DeclareLocal(typeof(WasmInstanceData));
        _framesLocal = il.DeclareLocal(typeof(CompiledFrames));
        _spLocal = il.DeclareLocal(typeof(int));
        _pcsLocal = il.DeclareLocal(typeof(int[]));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, s_codeInstance);
        il.Emit(OpCodes.Stloc, _dataLocal);
        il.Emit(OpCodes.Ldloc, _dataLocal);
        il.Emit(OpCodes.Ldfld, s_dataFrames);
        il.Emit(OpCodes.Stloc, _framesLocal);
        il.Emit(OpCodes.Ldloc, _framesLocal);
        il.Emit(OpCodes.Ldfld, s_framesSp);
        il.Emit(OpCodes.Stloc, _spLocal);
        // The stack check: the frame count against the limit, and every few
        // frames (every frame for big frames) the native stack.
        Label overflow = il.DefineLabel();
        Label checkedStack = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, _spLocal);
        il.Emit(OpCodes.Ldloc, _framesLocal);
        il.Emit(OpCodes.Ldfld, s_framesLimit);
        il.Emit(OpCodes.Bge_Un, overflow);
        if (_totalLocals < 64)
        {
            il.Emit(OpCodes.Ldloc, _spLocal);
            il.Emit(OpCodes.Ldc_I4, 15);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Brtrue, checkedStack);
        }
        il.MarkLabel(overflow);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, _spLocal);
        il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.StackCheck)));
        il.MarkLabel(checkedStack);
        // frames.Sp = sp + 1; frames.Func[sp] = address.
        il.Emit(OpCodes.Ldloc, _framesLocal);
        il.Emit(OpCodes.Ldloc, _spLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, s_framesSp);
        il.Emit(OpCodes.Ldloc, _framesLocal);
        il.Emit(OpCodes.Ldfld, s_framesFunc);
        il.Emit(OpCodes.Ldloc, _spLocal);
        // The function's address is read from its code, so that the method
        // serves every instance of the module (WasmSharedCode).
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, s_codeAddress);
        il.Emit(OpCodes.Ldfld, s_funcAddrValue);
        il.Emit(OpCodes.Stelem_I4);
        il.Emit(OpCodes.Ldloc, _framesLocal);
        il.Emit(OpCodes.Ldfld, s_framesPc);
        il.Emit(OpCodes.Stloc, _pcsLocal);
        if (_hasLoops)
        {
            // The loops' interrupt checks read the stack guard.
            _stackGuardLocal = il.DeclareLocal(typeof(StackGuard));
            il.Emit(OpCodes.Ldloc, _dataLocal);
            il.Emit(OpCodes.Ldfld, s_dataStackGuard);
            il.Emit(OpCodes.Stloc, _stackGuardLocal);
        }

        _memArray = new LocalBuilder?[_data.Memories.Length];
        _memSize = new LocalBuilder?[_data.Memories.Length];
        _memBase = new LocalBuilder?[_data.Memories.Length];
        _memoriesFixed = _data.AsmJs;
        if (_cacheMemories)
        {
            for (int m = 0; m < _data.Memories.Length; m++)
            {
                if (_data.Memories[m].Type.Limits.Shared) continue;
                _memArray[m] = il.DeclareLocal(typeof(byte[]));
                _memSize[m] = il.DeclareLocal(typeof(long));
                _memBase[m] = il.DeclareLocal(typeof(byte).MakeByRefType());
            }
            ReloadMemories(entry: true);
        }

        // Locals of reference and vector types start as their default values
        // (numeric ones are zero, as IL locals are).
        for (int i = 0; i < locals.Length; i++)
        {
            WasmKind kind = locals[i];
            // Numeric and vector locals are zero, as IL locals are.
            if (kind != WasmKind.Ref) continue;
            ValType type = _function.Locals[i];
            EmitLoadConstantValue(AddConstant(new Value(type)));
            il.Emit(OpCodes.Stloc, _asm.Locals[paramCount + i]!);
        }
    }

    /// <summary>Loads constants[k], a boxed <see cref="Value"/>.</summary>
    void EmitLoadConstantValue(int k)
    {
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, s_codeConstants);
        _il.Emit(OpCodes.Ldc_I4, k);
        _il.Emit(OpCodes.Ldelem_Ref);
        _il.Emit(OpCodes.Unbox_Any, typeof(Value));
    }

    /// <summary>
    /// Creates the call sites of the method's call_indirect and call_ref
    /// instructions (in the function and in the callees inlined into it) and
    /// loads them into IL locals (part of the prologue).
    /// </summary>
    void PrepareCallSites()
    {
        var functions = new List<FunctionInstance> { _function };
        if (_plan is not null)
        {
            foreach (WasmInliningTree node in _plan.InlinedNodes())
            {
                if (node != _plan) functions.Add((FunctionInstance)_data.Code[node.FunctionIndex].Function);
            }
        }
        foreach (FunctionInstance f in functions)
        {
            int functionIndex = (int)f.Index.Value;
            uint[] offsets = f.Definition.InstructionOffsets;
            int callIndex = 0;
            for (int i = 0; i < offsets.Length; i++)
            {
                int pos = (int)offsets[i];
                byte op = _bytes[pos];
                if (op is < 0x10 or > 0x15) continue;
                int slot = callIndex++;
                if (op is 0x10 or 0x12 || _callSites.ContainsKey((functionIndex, i))) continue;
                int typeIndex = -1;
                if (op is 0x11 or 0x13)
                {
                    int p = pos + 1;
                    typeIndex = (int)ReadLeb(ref p);
                    int table = (int)ReadLeb(ref p);
                    if (!_tableElements.ContainsKey(table))
                    {
                        LocalBuilder elements = _il.DeclareLocal(typeof(List<Value>));
                        _il.Emit(OpCodes.Ldloc, _dataLocal);
                        _il.Emit(OpCodes.Ldfld, s_dataTables);
                        _il.Emit(OpCodes.Ldc_I4, table);
                        _il.Emit(OpCodes.Ldelem_Ref);
                        _il.Emit(OpCodes.Ldfld, s_tableElements);
                        _il.Emit(OpCodes.Stloc, elements);
                        _tableElements[table] = elements;
                    }
                }
                var site = new WasmCallSite(slot, typeIndex);
                if (f == _function && _collectFeedback) _code.Feedback!.Sites[i] = site;
                LocalBuilder local = _il.DeclareLocal(typeof(WasmCallSite));
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, s_codeConstants);
                _il.Emit(OpCodes.Ldc_I4, AddConstant(site));
                _il.Emit(OpCodes.Ldelem_Ref);
                _il.Emit(OpCodes.Castclass, typeof(WasmCallSite));
                _il.Emit(OpCodes.Stloc, local);
                _callSites[(functionIndex, i)] = (local, site);
            }
        }
    }

    uint ReadLeb(ref int p)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _bytes[p++];
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    /// <summary>The call site of the current instruction and the IL local holding it.</summary>
    (LocalBuilder Local, WasmCallSite Site) CallSite() => _callSites[((int)_function.Index.Value, _instIndex)];

    /// <summary>Re-reads the cached memories (after anything that can grow them).</summary>
    void ReloadMemories(bool entry = false)
    {
        if (!_cacheMemories || (_memoriesFixed && !entry)) return;
        for (int m = 0; m < _memArray.Length; m++)
        {
            if (_memArray[m] is not { } array) continue;
            _il.Emit(OpCodes.Ldloc, _dataLocal);
            _il.Emit(OpCodes.Ldfld, s_dataMemories);
            _il.Emit(OpCodes.Ldc_I4, m);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldfld, s_memoryData);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Stloc, array);
            _il.Emit(OpCodes.Call, s_arrayDataReference);
            _il.Emit(OpCodes.Stloc, _memBase[m]!);
            _il.Emit(OpCodes.Call, s_memoryByteLength);
            _il.Emit(OpCodes.Conv_U8);
            _il.Emit(OpCodes.Stloc, _memSize[m]!);
        }
    }

    /// <summary>Pops this frame from the compiled frames (frames.Sp = sp).</summary>
    void EmitLeaveFrame()
    {
        _il.Emit(OpCodes.Ldloc, _framesLocal);
        _il.Emit(OpCodes.Ldloc, _spLocal);
        _il.Emit(OpCodes.Stfld, s_framesSp);
    }

    /// <summary>
    /// The position of the current instruction as the frame records it: its
    /// index in the function (the code serves every instance, and each
    /// instance links the function at its own offset: ExecContext.SnapshotFrames
    /// adds it), or in an inlined callee the inlined position that names it
    /// and the frames it is inlined into.
    /// </summary>
    int _pc => _inline is null ? _instIndex : InlinedPc();

    /// <summary>Records this frame's position (before a call or a runtime call that can throw).</summary>
    void EmitStorePc()
    {
        _il.Emit(OpCodes.Ldloc, _pcsLocal);
        _il.Emit(OpCodes.Ldloc, _spLocal);
        _il.Emit(OpCodes.Ldc_I4, _pc);
        _il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>Emits a call to a runtime function that takes (code, pc) last and never returns.</summary>
    void EmitTrapCall(string runtimeFunction)
    {
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, _pc);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(runtimeFunction));
        // The helper throws; this tells RyuJIT the path ends (and keeps the
        // IL stack consistent for the next label).
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Throw);
    }

    /// <summary>Ends IL that is never reached (it must not fall through to what follows).</summary>
    void EmitUnreachableEnd()
    {
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Throw);
    }

    /// <summary>Emits a trap with <paramref name="template"/> (the runtime throws).</summary>
    void EmitTrap(MessageTemplate template)
    {
        _il.Emit(OpCodes.Ldc_I4, (int)template);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, _pc);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.ThrowTrap)));
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Throw);
    }

    /// <summary>Calls a runtime helper whose last two parameters are (code, pc).</summary>
    void EmitRuntimeCall(string name)
    {
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, _pc);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(name));
    }

    void EmitInterruptCheck()
    {
        Label skip = _il.DefineLabel();
        if (_stackGuardLocal is { } guard)
        {
            _il.Emit(OpCodes.Ldloc, guard);
        }
        else
        {
            _il.Emit(OpCodes.Ldloc, _dataLocal);
            _il.Emit(OpCodes.Ldfld, s_dataStackGuard);
        }
        _il.Emit(OpCodes.Call, s_hasPendingInterrupts);
        _il.Emit(OpCodes.Brfalse, skip);
        EmitRuntimeCall(nameof(RuntimeWasm.HandleInterrupts));
        ReloadMemories();
        _il.MarkLabel(skip);
    }

    // ---- Control flow -------------------------------------------------------------

    Control ControlAt(int depth) => _control[_control.Count - 1 - depth];

    Control PushControl(ControlKind kind, WasmKind[] parameters, WasmKind[] results)
    {
        var c = new Control
        {
            Kind = kind,
            Params = parameters,
            Results = results,
            Reachable = _reachable,
            TryDepth = _tryDepth,
        };
        if (_reachable)
        {
            // Values below the block stay where they are inside it: no IL
            // stack, no references to locals the block may change, and the
            // parameters in their slots.
            _asm.SpillAll(_asm.Height - parameters.Length);
            c.Base = _asm.Height - parameters.Length;
        }
        _control.Add(c);
        return c;
    }

    /// <summary>Emits a jump to <paramref name="target"/> (br, or leave out of exception regions).</summary>
    void EmitJump(Control target)
    {
        target.BranchedTo = true;
        if (target.Kind == ControlKind.Function)
        {
            throw new InvalidOperationException("branch to the function");
        }
        _il.Emit(_tryDepth > target.TryDepth ? OpCodes.Leave : OpCodes.Br, target.Label);
    }

    /// <summary>LiftoffCompiler::BrImpl / BrOrRet: an unconditional branch to depth.</summary>
    void BrOrRet(int depth)
    {
        Control target = ControlAt(depth);
        if (target.Kind == ControlKind.Function)
        {
            DoReturn();
            return;
        }
        WasmKind[] kinds = target.BranchKinds;
        _asm.SpillStackEntries(0);
        if (target.Kind == ControlKind.Loop) EmitBackEdgeTierUpCheck(target);
        _asm.Transfer(kinds.Length, target.Base);
        EmitJump(target);
    }

    void Block()
    {
        var (p, r) = ReadBlockType();
        Control c = PushControl(ControlKind.Block, p, r);
        if (c.Reachable) c.Label = _il.DefineLabel();
    }

    void Loop()
    {
        int start = _pos - 1;
        var (p, r) = ReadBlockType();
        Control c = PushControl(ControlKind.Loop, p, r);
        if (!c.Reachable) return;
        c.StartOffset = start;
        c.Label = _il.DefineLabel();
        _il.MarkLabel(c.Label);
        // The stack check of loop headers (Liftoff: StackCheck at each loop).
        EmitInterruptCheck();
    }

    void If()
    {
        var (p, r) = ReadBlockType();
        if (!_reachable)
        {
            PushControl(ControlKind.If, p, r);
            return;
        }
        // The condition is the top value; the parameters are below it.
        _asm.SpillAll(_asm.Height - 1 - p.Length);
        _asm.PopToStack(1);
        Control c = PushControl(ControlKind.If, p, r);
        c.Label = _il.DefineLabel();
        c.ElseLabel = _il.DefineLabel();
        c.EntryState = _asm.Snapshot();
        _il.Emit(OpCodes.Brfalse, c.ElseLabel);
    }

    void Else()
    {
        Control c = ControlAt(0);
        if (!c.Reachable) return;
        if (_reachable)
        {
            FallThruTo(c);
            _il.Emit(OpCodes.Br, c.Label);
        }
        c.ElseSeen = true;
        _il.MarkLabel(c.ElseLabel);
        _asm.Restore(c.EntryState!);
        _reachable = true;
    }

    /// <summary>LiftoffCompiler::FallThruTo: the block's results to its slots at its end.</summary>
    void FallThruTo(Control c)
    {
        _asm.SpillStackEntries(0);
        _asm.Transfer(c.Results.Length, c.Base);
        c.BranchedTo = true;
    }

    void End()
    {
        Control c = ControlAt(0);
        if (c.Kind == ControlKind.Function)
        {
            if (_reachable) DoReturn();
            _control.RemoveAt(_control.Count - 1);
            return;
        }
        if (!c.Reachable)
        {
            _control.RemoveAt(_control.Count - 1);
            return;
        }
        switch (c.Kind)
        {
            case ControlKind.TryTable:
            case ControlKind.Try:
            case ControlKind.TryCatch:
                EndTry(c);
                break;
            case ControlKind.Loop:
                // Nothing branches to a loop's end: it is reached by falling through.
                if (_reachable) FallThruTo(c);
                c.BranchedTo = _reachable;
                break;
            default:
                if (_reachable) FallThruTo(c);
                if (c.Kind == ControlKind.If && !c.ElseSeen)
                {
                    // The false branch passes the parameters through: they are
                    // in the result slots already.
                    if (_reachable) _il.Emit(OpCodes.Br, c.Label);
                    _il.MarkLabel(c.ElseLabel);
                    c.BranchedTo = true;
                }
                if (c.BranchedTo) _il.MarkLabel(c.Label);
                break;
        }
        _control.RemoveAt(_control.Count - 1);
        _asm.ResetTo(c.Base, c.Results);
        _reachable = c.BranchedTo;
    }

    void BrIf(int depth)
    {
        Control target = ControlAt(depth);
        _asm.SpillStackEntries(0);
        _asm.PopToStack(1);
        if (target.Kind == ControlKind.Function)
        {
            Label skip = _il.DefineLabel();
            _il.Emit(OpCodes.Brfalse, skip);
            DoReturnKeepingStack();
            _il.MarkLabel(skip);
            return;
        }
        WasmKind[] kinds = target.BranchKinds;
        bool tierUpCheck = _collectFeedback && target.Kind == ControlKind.Loop;
        if (!tierUpCheck && !_asm.NeedsTransfer(kinds.Length, target.Base) && _tryDepth == target.TryDepth)
        {
            target.BranchedTo = true;
            _il.Emit(OpCodes.Brtrue, target.Label);
            return;
        }
        Label fallThrough = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse, fallThrough);
        if (tierUpCheck) EmitBackEdgeTierUpCheck(target);
        _asm.Transfer(kinds.Length, target.Base);
        EmitJump(target);
        _il.MarkLabel(fallThrough);
    }

    void BrTable()
    {
        uint count = ReadU32();
        var depths = new int[count + 1];
        for (int i = 0; i <= count; i++) depths[i] = (int)ReadU32();
        if (!_reachable) return;
        _asm.SpillStackEntries(0);
        _asm.PopToStack(1);
        // One stub per distinct target that needs moves, a leave or a return;
        // the others are switch targets directly.
        var stubs = new Dictionary<int, Label>();
        var labels = new Label[count];
        Label LabelFor(int depth)
        {
            if (stubs.TryGetValue(depth, out Label l)) return l;
            Control t = ControlAt(depth);
            if (t.Kind != ControlKind.Function && !_asm.NeedsTransfer(t.BranchKinds.Length, t.Base) && _tryDepth == t.TryDepth)
            {
                t.BranchedTo = true;
                l = t.Label;
            }
            else
            {
                l = _il.DefineLabel();
            }
            stubs[depth] = l;
            return l;
        }
        for (int i = 0; i < count; i++) labels[i] = LabelFor(depths[i]);
        Label defaultLabel = LabelFor(depths[count]);
        if (count > 0) _il.Emit(OpCodes.Switch, labels);
        else _il.Emit(OpCodes.Pop);
        _il.Emit(OpCodes.Br, defaultLabel);
        foreach (var (depth, label) in stubs)
        {
            Control t = ControlAt(depth);
            if (t.Kind != ControlKind.Function && label.Equals(t.Label)) continue;
            _il.MarkLabel(label);
            if (t.Kind == ControlKind.Function)
            {
                DoReturnKeepingStack();
            }
            else
            {
                _asm.Transfer(t.BranchKinds.Length, t.Base);
                EmitJump(t);
            }
        }
        _reachable = false;
    }

    /// <summary>A return that leaves the abstract stack as it was (a conditional or table branch's return path).</summary>
    void DoReturnKeepingStack()
    {
        List<VarState> saved = _asm.Snapshot();
        DoReturn();
        _asm.Restore(saved);
        _reachable = true;
    }

    /// <summary>LiftoffCompiler::DoReturn / ReturnImpl.</summary>
    void DoReturn()
    {
        if (_inline is not null)
        {
            InlinedReturn();
            return;
        }
        WasmKind[] results = ControlAt(_control.Count - 1).Results;
        int k = results.Length;
        _asm.SpillStackEntries(0);
        if (_collectFeedback)
        {
            const int kTierUpCostForFunctionEntry = 40;
            EmitTierUpCheck(WasmInliningTree.WireByteSize(_function) + kTierUpCostForFunctionEntry);
        }
        if (k > 1)
        {
            _asm.Settle(k);
            int first = _asm.Height - k;
            _data.Frames.EnsureReturns(k - 1);
            for (int i = 1; i < k; i++)
            {
                _il.Emit(OpCodes.Ldloc, _framesLocal);
                _il.Emit(OpCodes.Ldfld, s_framesReturns);
                _il.Emit(OpCodes.Ldc_I4, i - 1);
                _asm.LoadSettled(first + i);
                WasmValues.EmitToValue(_il, results[i]);
                _il.Emit(OpCodes.Stelem, typeof(Value));
            }
            _asm.LoadSettled(first);
            _asm.Drop(k);
        }
        else if (k == 1)
        {
            _asm.PopToStack(1);
        }
        if (_tryDepth > 0)
        {
            if (!_returnLabelUsed)
            {
                _returnLabelUsed = true;
                _returnLabel = _il.DefineLabel();
                if (k > 0) _returnValue = _il.DeclareLocal(WasmKinds.ClrType(results[0]));
            }
            if (k > 0) _il.Emit(OpCodes.Stloc, _returnValue!);
            _il.Emit(OpCodes.Leave, _returnLabel);
        }
        else
        {
            EmitLeaveFrame();
            _il.Emit(OpCodes.Ret);
        }
        _reachable = false;
    }

    // ---- The decoder's dispatch --------------------------------------------------

    void DecodeInstruction()
    {
        byte opcode = ReadU8();
        switch (opcode)
        {
            case 0x00: // unreachable
                if (_reachable)
                {
                    EmitTrap(MessageTemplate.WasmTrapUnreachable);
                    _reachable = false;
                }
                return;
            case 0x01: // nop
            case 0x16: // nop_for_testing
                return;
            case 0x02:
                Block();
                return;
            case 0x03:
                Loop();
                return;
            case 0x04:
                If();
                return;
            case 0x05:
                Else();
                return;
            case 0x06:
                Try();
                return;
            case 0x07:
                Catch((int)ReadU32());
                return;
            case 0x08:
                Throw((int)ReadU32());
                return;
            case 0x09:
                Rethrow((int)ReadU32());
                return;
            case 0x0a:
                ThrowRef();
                return;
            case 0x0b:
                End();
                return;
            case 0x0c:
            {
                int depth = (int)ReadU32();
                if (!_reachable) return;
                BrOrRet(depth);
                _reachable = false;
                return;
            }
            case 0x0d:
            {
                int depth = (int)ReadU32();
                if (_reachable) BrIf(depth);
                return;
            }
            case 0x0e:
                BrTable();
                return;
            case 0x0f:
                if (_reachable) DoReturn();
                return;
            case 0x10:
                CallDirect((int)ReadU32(), tail: false);
                return;
            case 0x11:
            {
                int type = (int)ReadU32();
                int table = (int)ReadU32();
                CallIndirect(type, table, tail: false);
                return;
            }
            case 0x12:
                CallDirect((int)ReadU32(), tail: true);
                return;
            case 0x13:
            {
                int type = (int)ReadU32();
                int table = (int)ReadU32();
                CallIndirect(type, table, tail: true);
                return;
            }
            case 0x14:
                CallRef((int)ReadU32(), tail: false);
                return;
            case 0x15:
                CallRef((int)ReadU32(), tail: true);
                return;
            case 0x18:
                Delegate((int)ReadU32());
                return;
            case 0x19:
                CatchAll();
                return;
            case 0x1a:
                if (_reachable) _asm.Drop();
                return;
            case 0x1b:
                Select();
                return;
            case 0x1c:
            {
                uint n = ReadU32();
                for (uint i = 0; i < n; i++) ReadValueKind();
                Select();
                return;
            }
            case 0x1f:
                TryTable();
                return;
            case 0x20:
            {
                int index = (int)ReadU32();
                if (_reachable) _asm.PushLocal(_localKinds[index], index);
                return;
            }
            case 0x21:
            {
                int index = (int)ReadU32();
                if (!_reachable) return;
                _asm.SpillLocal(index);
                _asm.PopToStack(1);
                _asm.StoreLocal(index);
                return;
            }
            case 0x22:
            {
                int index = (int)ReadU32();
                if (!_reachable) return;
                _asm.SpillLocal(index);
                _asm.PopToStack(1);
                _asm.StoreLocal(index);
                _asm.PushLocal(_localKinds[index], index);
                return;
            }
            case 0x23:
                GlobalGet((int)ReadU32());
                return;
            case 0x24:
                GlobalSet((int)ReadU32());
                return;
            case 0x25:
            case 0x26:
            {
                int table = (int)ReadU32();
                TableGetSet(opcode == 0x25, table);
                return;
            }
            case >= 0x28 and <= 0x3e:
                LoadStore(opcode);
                return;
            case 0x3f:
                MemorySize((int)ReadU32());
                return;
            case 0x40:
                MemoryGrow((int)ReadU32());
                return;
            case 0x41:
            {
                int value = ReadI32();
                if (_reachable) _asm.PushConst(WasmKind.I32, value);
                return;
            }
            case 0x42:
            {
                long value = ReadI64();
                if (_reachable) _asm.PushConst(WasmKind.I64, value);
                return;
            }
            case 0x43:
            {
                int bits = BitConverter.ToInt32(_bytes, _pos);
                _pos += 4;
                if (_reachable) _asm.PushConst(WasmKind.F32, bits);
                return;
            }
            case 0x44:
            {
                long bits = BitConverter.ToInt64(_bytes, _pos);
                _pos += 8;
                if (_reachable) _asm.PushConst(WasmKind.F64, bits);
                return;
            }
            case >= 0x45 and <= 0xc4:
                if (_reachable) NumericOp(opcode);
                return;
            case 0xd0:
                ReadHeapType();
                RefNull();
                return;
            case 0xd1:
                if (!_reachable) return;
                _asm.PopToStack(1);
                _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.RefIsNull)));
                _asm.PushStack(WasmKind.I32);
                return;
            case 0xd2:
                ReadU32();
                RefFunc();
                return;
            case 0xd3: // ref.eq
                GenericInstruction([WasmKind.Ref, WasmKind.Ref], [WasmKind.I32]);
                return;
            case 0xd4:
                if (!_reachable) return;
                _asm.PopToStack(1);
                EmitRuntimeCall(nameof(RuntimeWasm.RefAsNonNull));
                _asm.PushStack(WasmKind.Ref);
                return;
            case 0xd5:
                BrOnNull((int)ReadU32(), onNull: true);
                return;
            case 0xd6:
                BrOnNull((int)ReadU32(), onNull: false);
                return;
            case WasmOpcodes.kAsmJsPrefix:
                AsmJsOp(ReadU32());
                return;
            case WasmOpcodes.kGCPrefix:
                GCOp(ReadU32());
                return;
            case WasmOpcodes.kNumericPrefix:
                NumericPrefixedOp(ReadU32());
                return;
            case WasmOpcodes.kSimdPrefix:
                SimdOp(ReadU32());
                return;
            case WasmOpcodes.kAtomicPrefix:
                AtomicOp(ReadU32());
                return;
            default:
                Unsupported("opcode 0x" + opcode.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return;
        }
    }

    void Select()
    {
        if (!_reachable) return;
        WasmKind kind = _asm.PeekKind(1);
        _asm.PopToStack(3);
        LocalBuilder cond = _asm.Temp(WasmKind.I32);
        LocalBuilder ifFalse = _asm.Temp(kind);
        LocalBuilder ifTrue = _asm.Temp(kind);
        _il.Emit(OpCodes.Stloc, cond);
        _il.Emit(OpCodes.Stloc, ifFalse);
        _il.Emit(OpCodes.Stloc, ifTrue);
        Label takeTrue = _il.DefineLabel();
        Label done = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, cond);
        _il.Emit(OpCodes.Brtrue, takeTrue);
        _il.Emit(OpCodes.Ldloc, ifFalse);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(takeTrue);
        _il.Emit(OpCodes.Ldloc, ifTrue);
        _il.MarkLabel(done);
        _asm.PushStack(kind);
    }

    /// <summary>ref.null: the null of the instruction's type (a constant of the function).</summary>
    void RefNull()
    {
        if (!_reachable) return;
        var inst = (InstRefNull)_instructions[_instIndex];
        int k = AddConstant(Value.Null(inst.RefType));
        EmitLoadConstantValue(k);
        _asm.PushStack(WasmKind.Ref);
    }

    /// <summary>ref.func: the function reference, computed by the interpreter's instruction once.</summary>
    void RefFunc()
    {
        if (!_reachable) return;
        ExecContext ctx = _data.Context;
        Frame saved = ctx.Frame;
        int height = ctx.OpStack.Count;
        ctx.Frame = _data.InterpreterFrame;
        _instanceSpecific = true;
        Value value;
        try
        {
            _instructions[_instIndex].Execute(ctx);
            value = ctx.OpStack.PopAny();
        }
        finally
        {
            ctx.Frame = saved;
            ctx.OpStack.Count = height;
        }
        EmitLoadConstantValue(AddConstant(value));
        _asm.PushStack(WasmKind.Ref);
    }

    /// <summary>br_on_null / br_on_non_null.</summary>
    void BrOnNull(int depth, bool onNull)
    {
        if (!_reachable) return;
        Control target = ControlAt(depth);
        _asm.SpillAll(_asm.Height - 1);
        // The reference is in its slot now; test it.
        _asm.LoadSettled(_asm.Height - 1);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.RefIsNullBool)));
        Label fallThrough = _il.DefineLabel();
        _il.Emit(onNull ? OpCodes.Brfalse : OpCodes.Brtrue, fallThrough);
        if (onNull)
        {
            // Branch without the reference.
            VarState reference = _asm.Stack[^1];
            _asm.Stack.RemoveAt(_asm.Stack.Count - 1);
            EmitBranchFromSettled(target);
            _asm.Stack.Add(reference);
        }
        else
        {
            EmitBranchFromSettled(target);
        }
        _il.MarkLabel(fallThrough);
        if (!onNull) _asm.Drop();
    }

    /// <summary>A branch (or return) to <paramref name="target"/> with the IL stack empty.</summary>
    void EmitBranchFromSettled(Control target)
    {
        if (target.Kind == ControlKind.Function)
        {
            DoReturnKeepingStack();
            return;
        }
        _asm.Transfer(target.BranchKinds.Length, target.Base);
        EmitJump(target);
    }
}
