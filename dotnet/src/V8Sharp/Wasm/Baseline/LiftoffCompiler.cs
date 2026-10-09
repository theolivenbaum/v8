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
// the value stack as described in LiftoffAssembler.cs.
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
    readonly int _linkedOffset;

    readonly DynamicMethod _method;
    readonly ILGenerator _il;
    readonly LiftoffAssembler _asm;
    readonly List<object> _constants = [];
    readonly List<Control> _control = [];

    WasmKind[] _localKinds = [];
    int _instIndex;
    int _pc;
    bool _reachable = true;
    int _tryDepth;
    int _nextTryId;

    // Prologue locals.
    LocalBuilder _dataLocal = null!;
    LocalBuilder _framesLocal = null!;
    LocalBuilder _spLocal = null!;
    LocalBuilder _pcsLocal = null!;
    LocalBuilder? _stackGuardLocal;
    // The cached memories (array and size) of memories that are not shared.
    LocalBuilder?[] _memArray = [];
    LocalBuilder?[] _memSize = [];
    bool _cacheMemories;

    // The return label for returns from inside exception regions.
    Label _returnLabel;
    bool _returnLabelUsed;
    LocalBuilder? _returnValue;

    LiftoffCompiler(WasmCode code)
    {
        _code = code;
        _data = code.Instance!;
        _function = (FunctionInstance)code.Function;
        _module = _function.Module;
        _bytes = _data.WireBytes;
        _offsets = _function.Definition.InstructionOffsets;
        _instructions = [.. _function.Body.Instructions.Flatten()];
        _linkedOffset = _function.LinkedOffset;
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
    public static Delegate? Compile(WasmCode code, out string? bailout)
    {
        var compiler = new LiftoffCompiler(code);
        try
        {
            compiler.CompileFunction();
        }
        catch (LiftoffBailout e)
        {
            bailout = e.Message;
            return null;
        }
        bailout = null;
        code.Constants = [.. compiler._constants];
        code.Method = compiler._method;
        return compiler._method.CreateDelegate(code.Signature.DelegateType, code);
    }

    static void Unsupported(string reason) => throw new LiftoffBailout(reason);

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

    void CompileFunction()
    {
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

        var function = new Control
        {
            Kind = ControlKind.Function,
            Results = WasmKinds.Of(type.ResultType.Types),
            Base = 0,
            Label = _il.DefineLabel(),
            Reachable = true,
        };
        _control.Add(function);

        while (_control.Count > 0)
        {
            if (_instIndex >= _offsets.Length || _offsets[_instIndex] != _pos)
            {
                Unsupported("instruction offsets do not match the interpreter's");
            }
            _pc = _linkedOffset + _instIndex;
            DecodeInstruction();
            _instIndex++;
            _asm.ReleaseTemps();
        }
        if (_instIndex != _offsets.Length) Unsupported("instruction count does not match the interpreter's");
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

    /// <summary>Looks at the instructions before emitting: which memories the function accesses.</summary>
    void ScanFunction()
    {
        bool accessesMemory = false;
        foreach (InstructionBase inst in _instructions)
        {
            byte op = (byte)inst.Op.x00;
            if (op is >= 0x28 and <= 0x40)
            {
                accessesMemory = true;
                break;
            }
        }
        _cacheMemories = accessesMemory && _data.Memories.Length is > 0 and <= 4;
    }

    static readonly FieldInfo s_codeInstance = typeof(WasmCode).GetField(nameof(WasmCode.Instance))!;
    static readonly FieldInfo s_codeConstants = typeof(WasmCode).GetField(nameof(WasmCode.Constants))!;
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
        if (_localKinds.Length < 64)
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
        il.Emit(OpCodes.Ldc_I4, _code.Address.Value);
        il.Emit(OpCodes.Stelem_I4);
        il.Emit(OpCodes.Ldloc, _framesLocal);
        il.Emit(OpCodes.Ldfld, s_framesPc);
        il.Emit(OpCodes.Stloc, _pcsLocal);

        _memArray = new LocalBuilder?[_data.Memories.Length];
        _memSize = new LocalBuilder?[_data.Memories.Length];
        if (_cacheMemories)
        {
            for (int m = 0; m < _data.Memories.Length; m++)
            {
                if (_data.Memories[m].Type.Limits.Shared) continue;
                _memArray[m] = il.DeclareLocal(typeof(byte[]));
                _memSize[m] = il.DeclareLocal(typeof(long));
            }
            ReloadMemories();
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

    /// <summary>Loads constants[k] as an object of <paramref name="type"/>.</summary>
    void EmitLoadConstant(int k, Type type)
    {
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, s_codeConstants);
        _il.Emit(OpCodes.Ldc_I4, k);
        _il.Emit(OpCodes.Ldelem_Ref);
        _il.Emit(OpCodes.Castclass, type);
    }

    /// <summary>Re-reads the cached memories (after anything that can grow them).</summary>
    void ReloadMemories()
    {
        if (!_cacheMemories) return;
        for (int m = 0; m < _memArray.Length; m++)
        {
            if (_memArray[m] is not { } array) continue;
            _il.Emit(OpCodes.Ldloc, _dataLocal);
            _il.Emit(OpCodes.Ldfld, s_dataMemories);
            _il.Emit(OpCodes.Ldc_I4, m);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldfld, s_memoryData);
            _il.Emit(OpCodes.Stloc, array);
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
        if (_stackGuardLocal is null)
        {
            // Declared lazily; loaded where it is used first, which may be
            // inside a loop: load it at each check instead.
        }
        Label skip = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, _dataLocal);
        _il.Emit(OpCodes.Ldfld, s_dataStackGuard);
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
        var (p, r) = ReadBlockType();
        Control c = PushControl(ControlKind.Loop, p, r);
        if (!c.Reachable) return;
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
        if (!_asm.NeedsTransfer(kinds.Length, target.Base) && _tryDepth == target.TryDepth)
        {
            target.BranchedTo = true;
            _il.Emit(OpCodes.Brtrue, target.Label);
            return;
        }
        Label fallThrough = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse, fallThrough);
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
        WasmKind[] results = ControlAt(_control.Count - 1).Results;
        int k = results.Length;
        _asm.SpillStackEntries(0);
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
