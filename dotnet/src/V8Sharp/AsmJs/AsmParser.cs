// Port of src/asmjs/asm-parser.{h,cc} of V8 14.7.
//
// A custom parser + validator + wasm converter for asm.js:
// http://asmjs.org/spec/latest/
// This parser intentionally avoids the portion of JavaScript parsing
// that are not required to determine if code is valid asm.js code.
// * It is mostly one pass.
// * It bails out on unexpected input.
// * It assumes strict ordering insofar as permitted by asm.js validation rules.
// * It relies on a custom scanner that provides de-duped identifiers in two
//   scopes (local + module wide).
//
// V8's FAIL/EXPECT_TOKEN/RECURSE macros return from the current method; the
// C# methods do the same with explicit returns after Fail(), Expect() and
// Recurse(). RECURSE's stack check uses .NET's
// RuntimeHelpers.TryEnsureSufficientExecutionStack in place of V8's stack limit.
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Wasm;
using static V8Sharp.AsmJs.AsmToken;
using Op = V8Sharp.Wasm.WasmOpcodesConst;

namespace V8Sharp.AsmJs;

public sealed partial class AsmJsParser
{
    /// <summary>The standard library members a module uses (StandardMember).</summary>
    public enum StandardMember
    {
        kInfinity,
        kNaN,
        // STDLIB_MATH_FUNCTION_LIST
        kMathMin, kMathMax, kMathAbs, kMathFround, kMathAcos, kMathAsin, kMathAtan, kMathCos, kMathSin, kMathTan,
        kMathExp, kMathLog, kMathAtan2, kMathPow, kMathImul, kMathClz32, kMathCeil, kMathFloor, kMathSqrt,
        // STDLIB_MATH_VALUE_LIST
        kMathE, kMathLN10, kMathLN2, kMathLOG2E, kMathLOG10E, kMathPI, kMathSQRT1_2, kMathSQRT2,
        // STDLIB_ARRAY_TYPE_LIST
        kInt8Array, kUint8Array, kInt16Array, kUint16Array, kInt32Array, kUint32Array, kFloat32Array, kFloat64Array,
    }

    /// <summary>V8's StdlibSet (base::EnumSet&lt;StandardMember, uint64_t&gt;).</summary>
    public struct StdlibSet
    {
        ulong _bits;

        public readonly bool Contains(StandardMember m) => (_bits & (1UL << (int)m)) != 0;
        public void Add(StandardMember m) => _bits |= 1UL << (int)m;
        public void Remove(StandardMember m) => _bits &= ~(1UL << (int)m);
        public readonly bool Empty => _bits == 0;
        public readonly ulong ToIntegral() => _bits;
        public static StdlibSet FromIntegral(ulong bits) => new() { _bits = bits };
    }

    enum VarKind
    {
        kUnused,
        kLocal,
        kGlobal,
        kSpecial,
        kFunction,
        kTable,
        kImportedFunction,
        // STDLIB_MATH_FUNCTION_LIST
        kMathMin, kMathMax, kMathAbs, kMathFround, kMathAcos, kMathAsin, kMathAtan, kMathCos, kMathSin, kMathTan,
        kMathExp, kMathLog, kMathAtan2, kMathPow, kMathImul, kMathClz32, kMathCeil, kMathFloor, kMathSqrt,
        // STDLIB_MATH_VALUE_LIST
        kMathE, kMathLN10, kMathLN2, kMathLOG2E, kMathLOG10E, kMathPI, kMathSQRT1_2, kMathSQRT2,
    }

    // A single import in asm.js can require multiple imports in wasm, if the
    // function is used with different signatures. {cache} keeps the wasm
    // imports for the single asm.js import of name {function_name}.
    sealed class FunctionImportInfo(string name)
    {
        public readonly string FunctionName = name;
        public readonly Dictionary<FunctionSig, uint> Cache = [];
    }

    sealed class VarInfo
    {
        public AsmType Type = AsmType.None();
        public WasmFunctionBuilder? FunctionBuilder;
        public FunctionImportInfo? Import;
        public uint Mask;
        public uint Index;
        public VarKind Kind = VarKind.kUnused;
        public bool MutableVariable = true;
        public bool FunctionDefined;

        public void Reset()
        {
            Type = AsmType.None();
            FunctionBuilder = null;
            Import = null;
            Mask = 0;
            Index = 0;
            Kind = VarKind.kUnused;
            MutableVariable = true;
            FunctionDefined = false;
        }
    }

    readonly record struct GlobalImport(string ImportName, WasmValueType ValueType, VarInfo VarInfo);

    // Distinguish different kinds of blocks participating in {block_stack}. Each
    // entry on that stack represents one block in the wasm code, and determines
    // which block 'break' and 'continue' target in the current context:
    //  - kRegular: The target of a 'break' (with & without identifier).
    //              Pushed by an IterationStatement and a SwitchStatement.
    //  - kLoop   : The target of a 'continue' (with & without identifier).
    //              Pushed by an IterationStatement.
    //  - kNamed  : The target of a 'break' with a specific identifier.
    //              Pushed by a BlockStatement.
    //  - kOther  : Only used for internal blocks, can never be targeted.
    enum BlockKind { kRegular, kLoop, kNamed, kOther }

    // One entry in the {block_stack}, see {BlockKind} above for details. Blocks
    // without a label have {kTokenNone} set as their label.
    readonly record struct BlockInfo(BlockKind Kind, int Label);

    const int kTokenNone = 0;
    const int kNoHeapAccessShift = -1;

    readonly AsmJsScanner _scanner;
    readonly WasmModuleBuilder _moduleBuilder = new();
    WasmFunctionBuilder _currentFunctionBuilder = null!;
    AsmType? _returnType;
    StdlibSet _stdlibUses;
    readonly List<VarInfo> _globalVarInfo = [];
    readonly List<VarInfo> _localVarInfo = [];
    int _numGlobals;

    int _functionTempLocalsOffset;
    int _functionTempLocalsUsed;
    int _functionTempLocalsDepth;

    // Error Handling related
    bool _failed;
    string? _failureMessage;
    int _failureLocation = Common.Globals.kNoSourcePosition;

    // Module Related.
    int _stdlibName = kTokenNone;
    int _foreignName = kTokenNone;
    int _heapName = kTokenNone;

    // Track if parsing a heap assignment.
    bool _insideHeapAssignment;
    AsmType? _heapAccessType;

    readonly List<BlockInfo> _blockStack = [];

    // Types used for stdlib function and their set up.
    AsmType _stdlibDq2d = null!;
    AsmType _stdlibDqdq2d = null!;
    AsmType _stdlibI2s = null!;
    AsmType _stdlibIi2s = null!;
    AsmType _stdlibMinmax = null!;
    AsmType _stdlibAbs = null!;
    AsmType _stdlibCeilLike = null!;
    AsmType _stdlibFround = null!;

    // When making calls, the return type is needed to lookup signatures.
    // For `+callsite(..)` or `fround(callsite(..))` use this value to pass
    // along the coercion.
    AsmType? _callCoercion;

    // The source position associated with the above {call_coercion}.
    int _callCoercionPosition;

    // When making calls, the coercion can also appear in the source stream
    // syntactically "behind" the call site. For `callsite(..)|0` use this
    // value to flag that such a coercion must happen.
    AsmType? _callCoercionDeferred;

    // The source position at which requesting a deferred coercion via the
    // aforementioned {call_coercion_deferred} is allowed.
    int _callCoercionDeferredPosition;

    // The code position of the last heap access shift by an immediate value.
    // For `heap[expr >> value:NumericLiteral]` this indicates from where to
    // delete code when the expression is used as part of a valid heap access.
    // Will be set to {kNoHeapAccessShift} if heap access shift wasn't matched.
    int _heapAccessShiftPosition;
    uint _heapAccessShiftValue;

    // Used to track the last label we've seen so it can be matched to later
    // statements it's attached to.
    int _pendingLabel = kTokenNone;

    // Global imports. The list of imported variables that are copied during
    // module instantiation into a corresponding global variable.
    readonly List<GlobalImport> _globalImports = [];

    /// <summary>--trace-asm-parser (a debug-only flag in V8).</summary>
    readonly bool _traceParser;

    /// <summary>
    /// A parser of the asm.js module at <paramref name="startPosition"/>;
    /// <paramref name="endPosition"/> is where V8's character stream ends (the
    /// function's end for a lazy compile, else the end of the script).
    /// </summary>
    public AsmJsParser(string source, int startPosition, int endPosition, bool traceParser = false)
    {
        _scanner = new AsmJsScanner(source, startPosition, endPosition);
        _traceParser = traceParser;
        _moduleBuilder.AddMemory(0);
        InitializeStdlibTypes();
    }

    public bool Run()
    {
        ValidateModule();
        return !_failed;
    }

    public string? FailureMessage => _failureMessage;
    public int FailureLocation => _failureLocation;
    public WasmModuleBuilder ModuleBuilder => _moduleBuilder;
    public StdlibSet StdlibUses => _stdlibUses;

    void InitializeStdlibTypes()
    {
        AsmType d = AsmType.Double();
        AsmType dq = AsmType.DoubleQ();
        _stdlibDq2d = AsmType.Function(d);
        _stdlibDq2d.AsFunctionType()!.AddArgument(dq);

        _stdlibDqdq2d = AsmType.Function(d);
        _stdlibDqdq2d.AsFunctionType()!.AddArgument(dq);
        _stdlibDqdq2d.AsFunctionType()!.AddArgument(dq);

        AsmType f = AsmType.Float();
        AsmType fh = AsmType.Floatish();
        AsmType fq = AsmType.FloatQ();
        AsmType fq2fh = AsmType.Function(fh);
        fq2fh.AsFunctionType()!.AddArgument(fq);

        AsmType s = AsmType.Signed();
        AsmType u = AsmType.Unsigned();
        AsmType s2u = AsmType.Function(u);
        s2u.AsFunctionType()!.AddArgument(s);

        AsmType i = AsmType.Int();
        _stdlibI2s = AsmType.Function(s);
        _stdlibI2s.AsFunctionType()!.AddArgument(i);

        _stdlibIi2s = AsmType.Function(s);
        _stdlibIi2s.AsFunctionType()!.AddArgument(i);
        _stdlibIi2s.AsFunctionType()!.AddArgument(i);

        // The signatures in "9 Standard Library" of the spec draft are outdated and
        // have been superseded with the following by an errata:
        //  - Math.min/max : (signed, signed...) -> signed
        //                   (double, double...) -> double
        //                   (float, float...) -> float
        AsmType minmaxD = AsmType.MinMaxType(d, d);
        AsmType minmaxF = AsmType.MinMaxType(f, f);
        AsmType minmaxS = AsmType.MinMaxType(s, s);
        _stdlibMinmax = AsmType.OverloadedFunction();
        _stdlibMinmax.AsOverloadedFunctionType()!.AddOverload(minmaxS);
        _stdlibMinmax.AsOverloadedFunctionType()!.AddOverload(minmaxF);
        _stdlibMinmax.AsOverloadedFunctionType()!.AddOverload(minmaxD);

        // The signatures in "9 Standard Library" of the spec draft are outdated and
        // have been superseded with the following by an errata:
        //  - Math.abs : (signed) -> unsigned
        //               (double?) -> double
        //               (float?) -> floatish
        _stdlibAbs = AsmType.OverloadedFunction();
        _stdlibAbs.AsOverloadedFunctionType()!.AddOverload(s2u);
        _stdlibAbs.AsOverloadedFunctionType()!.AddOverload(_stdlibDq2d);
        _stdlibAbs.AsOverloadedFunctionType()!.AddOverload(fq2fh);

        // The signatures in "9 Standard Library" of the spec draft are outdated and
        // have been superseded with the following by an errata:
        //  - Math.ceil/floor/sqrt : (double?) -> double
        //                           (float?) -> floatish
        _stdlibCeilLike = AsmType.OverloadedFunction();
        _stdlibCeilLike.AsOverloadedFunctionType()!.AddOverload(_stdlibDq2d);
        _stdlibCeilLike.AsOverloadedFunctionType()!.AddOverload(fq2fh);

        _stdlibFround = AsmType.FroundType();
    }

    AsmType StdlibTypeOf(int token) => token switch
    {
        kToken_min or kToken_max => _stdlibMinmax,
        kToken_abs => _stdlibAbs,
        kToken_fround => _stdlibFround,
        kToken_acos or kToken_asin or kToken_atan or kToken_cos or kToken_sin or kToken_tan or kToken_exp
            or kToken_log => _stdlibDq2d,
        kToken_atan2 or kToken_pow => _stdlibDqdq2d,
        kToken_imul => _stdlibIi2s,
        kToken_clz32 => _stdlibI2s,
        _ => _stdlibCeilLike,
    };

    static FunctionSig ConvertSignature(AsmType returnType, List<AsmType> parameters)
    {
        var sigBuilder = new FunctionSig.Builder(!returnType.IsA(AsmType.Void()) ? 1 : 0, parameters.Count);
        foreach (AsmType param in parameters)
        {
            if (param.IsA(AsmType.Double())) sigBuilder.AddParam(WasmValueType.F64);
            else if (param.IsA(AsmType.Float())) sigBuilder.AddParam(WasmValueType.F32);
            else if (param.IsA(AsmType.Int())) sigBuilder.AddParam(WasmValueType.I32);
            else throw new InvalidOperationException("unreachable");
        }
        if (!returnType.IsA(AsmType.Void()))
        {
            if (returnType.IsA(AsmType.Double())) sigBuilder.AddReturn(WasmValueType.F64);
            else if (returnType.IsA(AsmType.Float())) sigBuilder.AddReturn(WasmValueType.F32);
            else if (returnType.IsA(AsmType.Signed())) sigBuilder.AddReturn(WasmValueType.I32);
            else throw new InvalidOperationException("unreachable");
        }
        return sigBuilder.Get();
    }

    // ---- Error handling (FAIL, EXPECT_TOKEN, RECURSE) ------------------------------

    void Fail(string msg, [CallerLineNumber] int line = 0)
    {
        _failed = true;
        _failureMessage = msg;
        _failureLocation = _scanner.Position;
        if (_traceParser)
        {
            Console.Out.Write($"[asm.js failure: {msg}, token: '{_scanner.Name(_scanner.Token)}', see: asm-parser.cs:{line}]\n");
        }
    }

    /// <summary>EXPECT_TOKEN: consumes <paramref name="token"/> or fails ("Unexpected token").</summary>
    bool Expect(int token)
    {
        if (_scanner.Token != token)
        {
            Fail("Unexpected token");
            return false;
        }
        _scanner.Next();
        return true;
    }

    /// <summary>The stack check of RECURSE: fails when the native stack is nearly exhausted.</summary>
    bool StackOk()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Fail("Stack overflow while parsing asm.js module.");
            return false;
        }
        return true;
    }

    // ---- Token helpers ----------------------------------------------------------------

    bool Peek(int token) => _scanner.Token == token;

    bool PeekForZero() => _scanner.IsUnsigned() && _scanner.AsUnsigned() == 0;

    bool Check(int token)
    {
        if (_scanner.Token == token)
        {
            _scanner.Next();
            return true;
        }
        return false;
    }

    bool CheckForZero()
    {
        if (_scanner.IsUnsigned() && _scanner.AsUnsigned() == 0)
        {
            _scanner.Next();
            return true;
        }
        return false;
    }

    bool CheckForDouble(out double value)
    {
        if (_scanner.IsDouble())
        {
            value = _scanner.AsDouble();
            _scanner.Next();
            return true;
        }
        value = 0;
        return false;
    }

    bool CheckForUnsigned(out uint value)
    {
        if (_scanner.IsUnsigned())
        {
            value = _scanner.AsUnsigned();
            _scanner.Next();
            return true;
        }
        value = 0;
        return false;
    }

    bool CheckForUnsignedBelow(uint limit, out uint value)
    {
        if (_scanner.IsUnsigned() && _scanner.AsUnsigned() < limit)
        {
            value = _scanner.AsUnsigned();
            _scanner.Next();
            return true;
        }
        value = 0;
        return false;
    }

    int Consume()
    {
        int ret = _scanner.Token;
        _scanner.Next();
        return ret;
    }

    void SkipSemicolon()
    {
        if (Check(';'))
        {
            // Had a semicolon.
        }
        else if (!Peek('}') && !_scanner.IsPrecededByNewline())
        {
            Fail("Expected ;");
        }
    }

    VarInfo GetVarInfo(int token)
    {
        bool isGlobal = AsmJsScanner.IsGlobal(token);
        List<VarInfo> varInfo = isGlobal ? _globalVarInfo : _localVarInfo;
        int index = isGlobal ? AsmJsScanner.GlobalIndex(token) : AsmJsScanner.LocalIndex(token);
        if (isGlobal && index + 1 > _numGlobals) _numGlobals = index + 1;
        while (varInfo.Count < index + 1) varInfo.Add(new VarInfo());
        return varInfo[index];
    }

    uint VarIndex(VarInfo info) => info.Index + (uint)_globalImports.Count;

    void AddGlobalImport(string name, AsmType type, WasmValueType vtype, bool mutableVariable, VarInfo info)
    {
        // Allocate a separate variable for the import.
        // TODO(asmjs): Consider using the imported global directly instead of
        // allocating a separate global variable for immutable (i.e. const) imports.
        DeclareGlobal(info, mutableVariable, type, vtype, WasmInitExpr.DefaultValue(vtype));

        // Record the need to initialize the global from the import.
        _globalImports.Add(new GlobalImport(name, vtype, info));
    }

    void DeclareGlobal(VarInfo info, bool mutableVariable, AsmType type, WasmValueType vtype, WasmInitExpr init)
    {
        info.Kind = VarKind.kGlobal;
        info.Type = type;
        info.Index = _moduleBuilder.AddGlobal(vtype, true, init);
        info.MutableVariable = mutableVariable;
    }

    static void DeclareStdlibFunc(VarInfo info, VarKind kind, AsmType type)
    {
        info.Kind = kind;
        info.Type = type;
        info.Index = 0;  // unused
        info.MutableVariable = false;
    }

    /// <summary>
    /// Allocates a temporary local variable. The given {index} is absolute within
    /// the function body, consider using {TemporaryVariableScope} when nesting.
    /// </summary>
    uint TempVariable(int index)
    {
        if (index + 1 > _functionTempLocalsUsed) _functionTempLocalsUsed = index + 1;
        return (uint)(_functionTempLocalsOffset + index);
    }

    // TemporaryVariableScope: the constructor takes a depth, the destructor
    // gives it back; get() is TempVariable(depth).
    int EnterTemporaryVariableScope() => _functionTempLocalsDepth++;
    void ExitTemporaryVariableScope() => _functionTempLocalsDepth--;

    string CopyCurrentIdentifierString() => _scanner.GetIdentifierString();

    // ---- Blocks -----------------------------------------------------------------------

    void Begin(int label = 0)
    {
        BareBegin(BlockKind.kRegular, label);
        _currentFunctionBuilder.EmitWithU8(Op.kExprBlock, Op.kVoidCode);
    }

    void Loop(int label = 0)
    {
        BareBegin(BlockKind.kLoop, label);
        int position = _scanner.Position;
        _currentFunctionBuilder.AddAsmWasmOffset(position, position);
        _currentFunctionBuilder.EmitWithU8(Op.kExprLoop, Op.kVoidCode);
    }

    void End()
    {
        BareEnd();
        _currentFunctionBuilder.Emit(Op.kExprEnd);
    }

    void BareBegin(BlockKind kind, int label = 0) => _blockStack.Add(new BlockInfo(kind, label));

    void BareEnd() => _blockStack.RemoveAt(_blockStack.Count - 1);

    int FindContinueLabelDepth(int label)
    {
        int count = 0;
        for (int i = _blockStack.Count - 1; i >= 0; i--)
        {
            BlockInfo info = _blockStack[i];
            // A 'continue' statement targets ...
            //  - The innermost {kLoop} block if no label is given.
            //  - The matching {kLoop} block (when a label is provided).
            if (info.Kind == BlockKind.kLoop && (label == kTokenNone || info.Label == label)) return count;
            ++count;
        }
        return -1;
    }

    int FindBreakLabelDepth(int label)
    {
        int count = 0;
        for (int i = _blockStack.Count - 1; i >= 0; i--)
        {
            BlockInfo info = _blockStack[i];
            // A 'break' statement targets ...
            //  - The innermost {kRegular} block if no label is given.
            //  - The matching {kRegular} or {kNamed} block (when a label is provided).
            if ((info.Kind == BlockKind.kRegular && (label == kTokenNone || info.Label == label)) ||
                (info.Kind == BlockKind.kNamed && info.Label == label))
            {
                return count;
            }
            ++count;
        }
        return -1;
    }

    // ---- 6.1 ValidateModule ----------------------------------------------------------------

    void ValidateModule()
    {
        if (!StackOk()) return;
        ValidateModuleParameters();
        if (_failed) return;
        if (!Expect('{')) return;
        if (!Expect(kToken_UseAsm)) return;
        SkipSemicolon();
        if (_failed) return;
        ValidateModuleVars();
        if (_failed) return;
        while (Peek(kToken_function))
        {
            ValidateFunction();
            if (_failed) return;
        }
        while (Peek(kToken_var))
        {
            ValidateFunctionTable();
            if (_failed) return;
        }
        ValidateExport();
        if (_failed) return;
        SkipSemicolon();
        if (_failed) return;
        if (!Expect('}')) return;

        // Check that all functions were eventually defined.
        for (int i = 0; i < _numGlobals; i++)
        {
            VarInfo info = _globalVarInfo[i];
            if (info.Kind == VarKind.kFunction && !info.FunctionDefined)
            {
                Fail("Undefined function");
                return;
            }
            if (info.Kind == VarKind.kTable && !info.FunctionDefined)
            {
                Fail("Undefined function table");
                return;
            }
            if (info.Kind == VarKind.kImportedFunction && !info.FunctionDefined)
            {
                // For imported functions without a single call site, we insert a dummy
                // import here to preserve the fact that there actually was an import.
                FunctionSig voidVoidSig = new FunctionSig.Builder(0, 0).Get();
                _moduleBuilder.AddImport(info.Import!.FunctionName, voidVoidSig);
            }
        }

        // Add start function to initialize things.
        WasmFunctionBuilder start = _moduleBuilder.AddFunction();
        _moduleBuilder.MarkStartFunction(start);
        foreach (GlobalImport globalImport in _globalImports)
        {
            uint importIndex = _moduleBuilder.AddGlobalImport(globalImport.ImportName, globalImport.ValueType, false);
            start.EmitWithU32V(Op.kExprGlobalGet, importIndex);
            start.EmitWithU32V(Op.kExprGlobalSet, VarIndex(globalImport.VarInfo));
        }
        start.Emit(Op.kExprEnd);
        start.SetSignature(new FunctionSig.Builder(0, 0).Get());
    }

    // 6.1 ValidateModule - parameters
    void ValidateModuleParameters()
    {
        if (!Expect('(')) return;
        _stdlibName = 0;
        _foreignName = 0;
        _heapName = 0;
        if (!Peek(')'))
        {
            if (!_scanner.IsGlobal())
            {
                Fail("Expected stdlib parameter");
                return;
            }
            _stdlibName = Consume();
            if (!Peek(')'))
            {
                if (!Expect(',')) return;
                if (!_scanner.IsGlobal())
                {
                    Fail("Expected foreign parameter");
                    return;
                }
                _foreignName = Consume();
                if (_stdlibName == _foreignName)
                {
                    Fail("Duplicate parameter name");
                    return;
                }
                if (!Peek(')'))
                {
                    if (!Expect(',')) return;
                    if (!_scanner.IsGlobal())
                    {
                        Fail("Expected heap parameter");
                        return;
                    }
                    _heapName = Consume();
                    if (_heapName == _stdlibName || _heapName == _foreignName)
                    {
                        Fail("Duplicate parameter name");
                        return;
                    }
                }
            }
        }
        Expect(')');
    }

    // 6.1 ValidateModule - variables
    void ValidateModuleVars()
    {
        while (!_failed && (Peek(kToken_var) || Peek(kToken_const)))
        {
            bool mutableVariable = true;
            if (Check(kToken_var))
            {
                // Had a var.
            }
            else
            {
                if (!Expect(kToken_const)) return;
                mutableVariable = false;
            }
            for (;;)
            {
                if (!StackOk()) return;
                ValidateModuleVar(mutableVariable);
                if (_failed) return;
                if (Check(',')) continue;
                break;
            }
            SkipSemicolon();
        }
    }

    // 6.1 ValidateModule - one variable
    void ValidateModuleVar(bool mutableVariable)
    {
        if (!_scanner.IsGlobal())
        {
            Fail("Expected identifier");
            return;
        }
        int identifier = Consume();
        if (identifier == _stdlibName || identifier == _foreignName || identifier == _heapName)
        {
            Fail("Cannot shadow parameters");
            return;
        }
        VarInfo info = GetVarInfo(identifier);
        if (info.Kind != VarKind.kUnused)
        {
            Fail("Redefinition of variable");
            return;
        }
        if (!Expect('=')) return;
        if (CheckForDouble(out double dvalue))
        {
            DeclareGlobal(info, mutableVariable, AsmType.Double(), WasmValueType.F64, WasmInitExpr.F64(dvalue));
        }
        else if (CheckForUnsigned(out uint uvalue))
        {
            if (uvalue > 0x7FFFFFFF)
            {
                Fail("Numeric literal out of range");
                return;
            }
            DeclareGlobal(info, mutableVariable, mutableVariable ? AsmType.Int() : AsmType.Signed(), WasmValueType.I32,
                WasmInitExpr.I32((int)uvalue));
        }
        else if (Check('-'))
        {
            if (CheckForDouble(out dvalue))
            {
                DeclareGlobal(info, mutableVariable, AsmType.Double(), WasmValueType.F64, WasmInitExpr.F64(-dvalue));
            }
            else if (CheckForUnsigned(out uvalue))
            {
                if (uvalue > 0x7FFFFFFF)
                {
                    Fail("Numeric literal out of range");
                    return;
                }
                if (uvalue == 0)
                {
                    // '-0' is treated as float.
                    DeclareGlobal(info, mutableVariable, AsmType.Float(), WasmValueType.F32, WasmInitExpr.F32(-0.0f));
                }
                else
                {
                    DeclareGlobal(info, mutableVariable, mutableVariable ? AsmType.Int() : AsmType.Signed(),
                        WasmValueType.I32, WasmInitExpr.I32(-(int)uvalue));
                }
            }
            else
            {
                Fail("Expected numeric literal");
            }
        }
        else if (Check(kToken_new))
        {
            if (!StackOk()) return;
            ValidateModuleVarNewStdlib(info);
        }
        else if (Check(_stdlibName))
        {
            if (!Expect('.')) return;
            if (!StackOk()) return;
            ValidateModuleVarStdlib(info);
        }
        else if (Peek(_foreignName) || Peek('+'))
        {
            if (!StackOk()) return;
            ValidateModuleVarImport(info, mutableVariable);
        }
        else if (_scanner.IsGlobal())
        {
            if (!StackOk()) return;
            ValidateModuleVarFromGlobal(info, mutableVariable);
        }
        else
        {
            Fail("Bad variable declaration");
        }
    }

    // 6.1 ValidateModule - global float declaration
    void ValidateModuleVarFromGlobal(VarInfo info, bool mutableVariable)
    {
        VarInfo srcInfo = GetVarInfo(Consume());
        if (!srcInfo.Type.IsA(_stdlibFround))
        {
            if (srcInfo.MutableVariable)
            {
                Fail("Can only use immutable variables in global definition");
                return;
            }
            if (mutableVariable)
            {
                Fail("Can only define immutable variables with other immutables");
                return;
            }
            if (!srcInfo.Type.IsA(AsmType.Int()) && !srcInfo.Type.IsA(AsmType.Float()) &&
                !srcInfo.Type.IsA(AsmType.Double()))
            {
                Fail("Expected int, float, double, or fround for global definition");
                return;
            }
            info.Kind = VarKind.kGlobal;
            info.Type = srcInfo.Type;
            info.Index = srcInfo.Index;
            info.MutableVariable = false;
            return;
        }
        if (!Expect('(')) return;
        bool negate = Check('-');
        if (CheckForDouble(out double dvalue))
        {
            if (negate) dvalue = -dvalue;
            DeclareGlobal(info, mutableVariable, AsmType.Float(), WasmValueType.F32,
                WasmInitExpr.F32(Conversions.DoubleToFloat32(dvalue)));
        }
        else if (CheckForUnsigned(out uint uvalue))
        {
            dvalue = uvalue;
            if (negate) dvalue = -dvalue;
            DeclareGlobal(info, mutableVariable, AsmType.Float(), WasmValueType.F32, WasmInitExpr.F32((float)dvalue));
        }
        else
        {
            Fail("Expected numeric literal");
            return;
        }
        Expect(')');
    }

    // 6.1 ValidateModule - foreign imports
    void ValidateModuleVarImport(VarInfo info, bool mutableVariable)
    {
        if (Check('+'))
        {
            if (!Expect(_foreignName)) return;
            if (!Expect('.')) return;
            string name = CopyCurrentIdentifierString();
            AddGlobalImport(name, AsmType.Double(), WasmValueType.F64, mutableVariable, info);
            _scanner.Next();
        }
        else
        {
            if (!Expect(_foreignName)) return;
            if (!Expect('.')) return;
            string name = CopyCurrentIdentifierString();
            _scanner.Next();
            if (Check('|'))
            {
                if (!CheckForZero())
                {
                    Fail("Expected |0 type annotation for foreign integer import");
                    return;
                }
                AddGlobalImport(name, AsmType.Int(), WasmValueType.I32, mutableVariable, info);
            }
            else
            {
                info.Kind = VarKind.kImportedFunction;
                info.Import = new FunctionImportInfo(name);
                info.MutableVariable = false;
            }
        }
    }

    // 6.1 ValidateModule - one variable
    // 9 - Standard Library - heap types
    void ValidateModuleVarNewStdlib(VarInfo info)
    {
        if (!Expect(_stdlibName)) return;
        if (!Expect('.')) return;
        switch (Consume())
        {
            case kToken_Int8Array: DeclareHeapView(info, AsmType.Int8Array(), StandardMember.kInt8Array); break;
            case kToken_Uint8Array: DeclareHeapView(info, AsmType.Uint8Array(), StandardMember.kUint8Array); break;
            case kToken_Int16Array: DeclareHeapView(info, AsmType.Int16Array(), StandardMember.kInt16Array); break;
            case kToken_Uint16Array: DeclareHeapView(info, AsmType.Uint16Array(), StandardMember.kUint16Array); break;
            case kToken_Int32Array: DeclareHeapView(info, AsmType.Int32Array(), StandardMember.kInt32Array); break;
            case kToken_Uint32Array: DeclareHeapView(info, AsmType.Uint32Array(), StandardMember.kUint32Array); break;
            case kToken_Float32Array: DeclareHeapView(info, AsmType.Float32Array(), StandardMember.kFloat32Array); break;
            case kToken_Float64Array: DeclareHeapView(info, AsmType.Float64Array(), StandardMember.kFloat64Array); break;
            default:
                Fail("Expected ArrayBuffer view");
                return;
        }
        if (!Expect('(')) return;
        if (!Expect(_heapName)) return;
        Expect(')');
    }

    void DeclareHeapView(VarInfo info, AsmType type, StandardMember member)
    {
        DeclareStdlibFunc(info, VarKind.kSpecial, type);
        _stdlibUses.Add(member);
    }

    // 6.1 ValidateModule - one variable
    // 9 - Standard Library
    void ValidateModuleVarStdlib(VarInfo info)
    {
        if (Check(kToken_Math))
        {
            if (!Expect('.')) return;
            int token = Consume();
            for (int i = 0; i < AsmNames.StdlibMathValues.Length; i++)
            {
                (_, int valueToken, double constValue) = AsmNames.StdlibMathValues[i];
                if (token != valueToken) continue;
                DeclareGlobal(info, false, AsmType.Double(), WasmValueType.F64, WasmInitExpr.F64(constValue));
                _stdlibUses.Add(StandardMember.kMathE + i);
                return;
            }
            for (int i = 0; i < AsmNames.StdlibMathFunctions.Length; i++)
            {
                (_, int functionToken) = AsmNames.StdlibMathFunctions[i];
                if (token != functionToken) continue;
                DeclareStdlibFunc(info, VarKind.kMathMin + i, StdlibTypeOf(token));
                _stdlibUses.Add(StandardMember.kMathMin + i);
                return;
            }
            Fail("Invalid member of stdlib.Math");
        }
        else if (Check(kToken_Infinity))
        {
            DeclareGlobal(info, false, AsmType.Double(), WasmValueType.F64, WasmInitExpr.F64(double.PositiveInfinity));
            _stdlibUses.Add(StandardMember.kInfinity);
        }
        else if (Check(kToken_NaN))
        {
            DeclareGlobal(info, false, AsmType.Double(), WasmValueType.F64, WasmInitExpr.F64(double.NaN));
            _stdlibUses.Add(StandardMember.kNaN);
        }
        else
        {
            Fail("Invalid member of stdlib");
        }
    }

    // 6.2 ValidateExport
    void ValidateExport()
    {
        if (!Expect(kToken_return)) return;
        if (Check('{'))
        {
            for (;;)
            {
                string name = CopyCurrentIdentifierString();
                if (!_scanner.IsGlobal() && !_scanner.IsLocal())
                {
                    Fail("Illegal export name");
                    return;
                }
                if (name == "__proto__")
                {
                    Fail("Illegal export name");
                    return;
                }
                Consume();
                if (!Expect(':')) return;
                if (!_scanner.IsGlobal())
                {
                    Fail("Expected function name");
                    return;
                }
                VarInfo info = GetVarInfo(Consume());
                if (info.Kind != VarKind.kFunction)
                {
                    Fail("Expected function");
                    return;
                }
                _moduleBuilder.AddExport(name, info.FunctionBuilder!);
                if (Check(','))
                {
                    if (!Peek('}')) continue;
                }
                break;
            }
            Expect('}');
        }
        else
        {
            if (!_scanner.IsGlobal())
            {
                Fail("Single function export must be a function name");
                return;
            }
            VarInfo info = GetVarInfo(Consume());
            if (info.Kind != VarKind.kFunction)
            {
                Fail("Single function export must be a function");
                return;
            }
            _moduleBuilder.AddExport(AsmJs.kSingleFunctionName, info.FunctionBuilder!);
        }
    }

    // 6.3 ValidateFunctionTable
    void ValidateFunctionTable()
    {
        if (!Expect(kToken_var)) return;
        if (!_scanner.IsGlobal())
        {
            Fail("Expected table name");
            return;
        }
        VarInfo tableInfo = GetVarInfo(Consume());
        if (tableInfo.Kind == VarKind.kTable)
        {
            if (tableInfo.FunctionDefined)
            {
                Fail("Function table redefined");
                return;
            }
            tableInfo.FunctionDefined = true;
        }
        else if (tableInfo.Kind != VarKind.kUnused)
        {
            Fail("Function table name collides");
            return;
        }
        if (!Expect('=')) return;
        if (!Expect('[')) return;
        ulong count = 0;
        for (;;)
        {
            if (!_scanner.IsGlobal())
            {
                Fail("Expected function name");
                return;
            }
            VarInfo info = GetVarInfo(Consume());
            if (info.Kind != VarKind.kFunction)
            {
                Fail("Expected function");
                return;
            }
            // Only store the function into a table if we used the table somewhere
            // (i.e. tables are first seen at their use sites and allocated there).
            if (tableInfo.Kind == VarKind.kTable)
            {
                if (count >= (ulong)tableInfo.Mask + 1)
                {
                    Fail("Exceeded function table size");
                    return;
                }
                if (!info.Type.IsA(tableInfo.Type))
                {
                    Fail("Function table definition doesn't match use");
                    return;
                }
                _moduleBuilder.SetIndirectFunction(0, (uint)(tableInfo.Index + count), info.Index,
                    WasmModuleBuilder.FunctionIndexingMode.kRelativeToDeclaredFunctions);
            }
            ++count;
            if (Check(','))
            {
                if (!Peek(']')) continue;
            }
            break;
        }
        if (!Expect(']')) return;
        if (tableInfo.Kind == VarKind.kTable && count != (ulong)tableInfo.Mask + 1)
        {
            Fail("Function table size does not match uses");
            return;
        }
        SkipSemicolon();
    }

    // 6.4 ValidateFunction
    void ValidateFunction()
    {
        // Remember position of the 'function' token as start position.
        int functionStartPosition = _scanner.Position;

        if (!Expect(kToken_function)) return;
        if (!_scanner.IsGlobal())
        {
            Fail("Expected function name");
            return;
        }

        string functionNameStr = CopyCurrentIdentifierString();
        int functionName = Consume();
        VarInfo functionInfo = GetVarInfo(functionName);
        if (functionInfo.Kind == VarKind.kUnused)
        {
            functionInfo.Kind = VarKind.kFunction;
            functionInfo.FunctionBuilder = _moduleBuilder.AddFunction();
            functionInfo.Index = functionInfo.FunctionBuilder.FuncIndex;
            functionInfo.MutableVariable = false;
        }
        else if (functionInfo.Kind != VarKind.kFunction)
        {
            Fail("Function name collides with variable");
            return;
        }
        else if (functionInfo.FunctionDefined)
        {
            Fail("Function redefined");
            return;
        }

        functionInfo.FunctionDefined = true;
        functionInfo.FunctionBuilder!.SetName(functionNameStr);
        _currentFunctionBuilder = functionInfo.FunctionBuilder;
        _returnType = null;

        // Record start of the function, used as position for the stack check.
        _currentFunctionBuilder.SetAsmFunctionStartPosition(functionStartPosition);

        var parameters = new List<AsmType>();
        // Not RECURSE in V8: a failure here does not end ValidateFunction.
        ValidateFunctionParams(parameters);

        // Check against limit on number of parameters.
        if (parameters.Count > AsmJs.kV8MaxWasmFunctionParams)
        {
            Fail("Number of parameters exceeds internal limit");
            return;
        }

        var locals = new List<WasmValueType>();
        ValidateFunctionLocals(parameters.Count, locals);

        _functionTempLocalsOffset = parameters.Count + locals.Count;
        _functionTempLocalsUsed = 0;
        _functionTempLocalsDepth = 0;

        bool lastStatementIsReturn = false;
        while (!_failed && !Peek('}'))
        {
            lastStatementIsReturn = Peek(kToken_return);
            if (!StackOk()) return;
            ValidateStatement();
            if (_failed) return;
        }

        int functionEndPosition = _scanner.Position + 1;

        if (!Expect('}')) return;

        if (!lastStatementIsReturn)
        {
            if (_returnType is null)
            {
                _returnType = AsmType.Void();
            }
            else if (!_returnType.IsA(AsmType.Void()))
            {
                Fail("Expected return at end of non-void function");
                return;
            }
        }

        // TODO(bradnelson): WasmModuleBuilder can't take this in the right order.
        //                   We should fix that so we can use it instead.
        FunctionSig sig = ConvertSignature(_returnType!, parameters);
        _currentFunctionBuilder.SetSignature(sig);
        foreach (WasmValueType local in locals) _currentFunctionBuilder.AddLocal(local);
        // Add bonus temps.
        for (int i = 0; i < _functionTempLocalsUsed; ++i) _currentFunctionBuilder.AddLocal(WasmValueType.I32);

        // Check against limit on number of local variables.
        if (locals.Count + _functionTempLocalsUsed + parameters.Count > AsmJs.kV8MaxWasmFunctionLocals)
        {
            Fail("Number of local variables exceeds internal limit");
            return;
        }

        // End function
        _currentFunctionBuilder.Emit(Op.kExprEnd);

        // Emit function end position as the last position for this function.
        _currentFunctionBuilder.AddAsmWasmOffset(functionEndPosition, functionEndPosition);

        if (_currentFunctionBuilder.GetPosition() > AsmJs.kV8MaxWasmFunctionSize)
        {
            Fail("Size of function body exceeds internal limit");
            return;
        }
        // Record (or validate) function type.
        AsmType functionType = AsmType.Function(_returnType!);
        foreach (AsmType t in parameters) functionType.AsFunctionType()!.AddArgument(t);
        functionInfo = GetVarInfo(functionName);
        if (functionInfo.Type.IsA(AsmType.None()))
        {
            functionInfo.Type = functionType;
        }
        else if (!functionType.IsA(functionInfo.Type))
        {
            // TODO(bradnelson): Should IsExactly be used here?
            Fail("Function definition doesn't match use");
            return;
        }

        _scanner.ResetLocals();
        foreach (VarInfo local in _localVarInfo) local.Reset();
    }

    // 6.4 ValidateFunction
    void ValidateFunctionParams(List<AsmType> parameters)
    {
        // TODO(bradnelson): Do this differently so that the scanner doesn't need to
        // have a state transition that needs knowledge of how the scanner works
        // inside.
        _scanner.EnterLocalScope();
        if (!Expect('(')) return;
        var functionParameters = new List<int>();
        while (!_failed && !Peek(')'))
        {
            if (!_scanner.IsLocal())
            {
                Fail("Expected parameter name");
                return;
            }
            functionParameters.Add(Consume());
            if (!Peek(')'))
            {
                if (!Expect(',')) return;
            }
        }
        if (!Expect(')')) return;
        _scanner.EnterGlobalScope();
        if (!Expect('{')) return;
        // 5.1 Parameter Type Annotations
        foreach (int p in functionParameters)
        {
            if (!Expect(p)) return;
            if (!Expect('=')) return;
            VarInfo info = GetVarInfo(p);
            if (info.Kind != VarKind.kUnused)
            {
                Fail("Duplicate parameter name");
                return;
            }
            if (Check(p))
            {
                if (!Expect('|')) return;
                if (!CheckForZero())
                {
                    Fail("Bad integer parameter annotation.");
                    return;
                }
                info.Kind = VarKind.kLocal;
                info.Type = AsmType.Int();
                info.Index = (uint)parameters.Count;
                parameters.Add(AsmType.Int());
            }
            else if (Check('+'))
            {
                if (!Expect(p)) return;
                info.Kind = VarKind.kLocal;
                info.Type = AsmType.Double();
                info.Index = (uint)parameters.Count;
                parameters.Add(AsmType.Double());
            }
            else
            {
                if (!_scanner.IsGlobal() || !GetVarInfo(Consume()).Type.IsA(_stdlibFround))
                {
                    Fail("Expected fround");
                    return;
                }
                if (!Expect('(')) return;
                if (!Expect(p)) return;
                if (!Expect(')')) return;
                info.Kind = VarKind.kLocal;
                info.Type = AsmType.Float();
                info.Index = (uint)parameters.Count;
                parameters.Add(AsmType.Float());
            }
            SkipSemicolon();
        }
    }

    // 6.4 ValidateFunction - locals
    void ValidateFunctionLocals(int paramCount, List<WasmValueType> locals)
    {
        // Local Variables.
        while (Peek(kToken_var))
        {
            _scanner.EnterLocalScope();
            if (!Expect(kToken_var)) return;
            _scanner.EnterGlobalScope();
            for (;;)
            {
                if (!_scanner.IsLocal())
                {
                    Fail("Expected local variable identifier");
                    return;
                }
                VarInfo info = GetVarInfo(Consume());
                if (info.Kind != VarKind.kUnused)
                {
                    Fail("Duplicate local variable name");
                    return;
                }
                // Store types.
                if (!Expect('=')) return;
                if (Check('-'))
                {
                    if (CheckForDouble(out double dvalue))
                    {
                        info.Kind = VarKind.kLocal;
                        info.Type = AsmType.Double();
                        info.Index = (uint)(paramCount + locals.Count);
                        locals.Add(WasmValueType.F64);
                        _currentFunctionBuilder.EmitF64Const(-dvalue);
                        _currentFunctionBuilder.EmitSetLocal(info.Index);
                    }
                    else if (CheckForUnsigned(out uint uvalue))
                    {
                        if (uvalue > 0x7FFFFFFF)
                        {
                            Fail("Numeric literal out of range");
                            return;
                        }
                        info.Kind = VarKind.kLocal;
                        info.Type = AsmType.Int();
                        info.Index = (uint)(paramCount + locals.Count);
                        locals.Add(WasmValueType.I32);
                        int value = -(int)uvalue;
                        _currentFunctionBuilder.EmitI32Const(value);
                        _currentFunctionBuilder.EmitSetLocal(info.Index);
                    }
                    else
                    {
                        Fail("Expected variable initial value");
                        return;
                    }
                }
                else if (_scanner.IsGlobal())
                {
                    VarInfo sinfo = GetVarInfo(Consume());
                    if (sinfo.Kind == VarKind.kGlobal)
                    {
                        if (sinfo.MutableVariable)
                        {
                            Fail("Initializing from global requires const variable");
                            return;
                        }
                        info.Kind = VarKind.kLocal;
                        info.Type = sinfo.Type;
                        info.Index = (uint)(paramCount + locals.Count);
                        if (sinfo.Type.IsA(AsmType.Int())) locals.Add(WasmValueType.I32);
                        else if (sinfo.Type.IsA(AsmType.Float())) locals.Add(WasmValueType.F32);
                        else if (sinfo.Type.IsA(AsmType.Double())) locals.Add(WasmValueType.F64);
                        else
                        {
                            Fail("Bad local variable definition");
                            return;
                        }
                        _currentFunctionBuilder.EmitWithU32V(Op.kExprGlobalGet, VarIndex(sinfo));
                        _currentFunctionBuilder.EmitSetLocal(info.Index);
                    }
                    else if (sinfo.Type.IsA(_stdlibFround))
                    {
                        if (!Expect('(')) return;
                        bool negate = Check('-');
                        if (CheckForDouble(out double dvalue))
                        {
                            info.Kind = VarKind.kLocal;
                            info.Type = AsmType.Float();
                            info.Index = (uint)(paramCount + locals.Count);
                            locals.Add(WasmValueType.F32);
                            if (negate) dvalue = -dvalue;
                            float fvalue = Conversions.DoubleToFloat32(dvalue);
                            _currentFunctionBuilder.EmitF32Const(fvalue);
                            _currentFunctionBuilder.EmitSetLocal(info.Index);
                        }
                        else if (CheckForUnsigned(out uint uvalue))
                        {
                            if (uvalue > 0x7FFFFFFF)
                            {
                                Fail("Numeric literal out of range");
                                return;
                            }
                            info.Kind = VarKind.kLocal;
                            info.Type = AsmType.Float();
                            info.Index = (uint)(paramCount + locals.Count);
                            locals.Add(WasmValueType.F32);
                            int value = (int)uvalue;
                            if (negate) value = -value;
                            float fvalue = value;
                            _currentFunctionBuilder.EmitF32Const(fvalue);
                            _currentFunctionBuilder.EmitSetLocal(info.Index);
                        }
                        else
                        {
                            Fail("Expected variable initial value");
                            return;
                        }
                        if (!Expect(')')) return;
                    }
                    else
                    {
                        Fail("expected fround or const global");
                        return;
                    }
                }
                else if (CheckForDouble(out double dvalue))
                {
                    info.Kind = VarKind.kLocal;
                    info.Type = AsmType.Double();
                    info.Index = (uint)(paramCount + locals.Count);
                    locals.Add(WasmValueType.F64);
                    _currentFunctionBuilder.EmitF64Const(dvalue);
                    _currentFunctionBuilder.EmitSetLocal(info.Index);
                }
                else if (CheckForUnsigned(out uint uvalue))
                {
                    info.Kind = VarKind.kLocal;
                    info.Type = AsmType.Int();
                    info.Index = (uint)(paramCount + locals.Count);
                    locals.Add(WasmValueType.I32);
                    int value = (int)uvalue;
                    _currentFunctionBuilder.EmitI32Const(value);
                    _currentFunctionBuilder.EmitSetLocal(info.Index);
                }
                else
                {
                    Fail("Expected variable initial value");
                    return;
                }
                if (!Peek(',')) break;
                _scanner.EnterLocalScope();
                if (!Expect(',')) return;
                _scanner.EnterGlobalScope();
            }
            SkipSemicolon();
        }
    }
}
