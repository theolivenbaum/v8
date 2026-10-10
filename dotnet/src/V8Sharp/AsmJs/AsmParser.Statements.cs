// Port of src/asmjs/asm-parser.cc of V8 14.7, continued: statements (6.5-6.7),
// expressions (6.8), calls (6.9), heap accesses (6.10) and float coercions
// (6.11).
//
// The control flow follows V8's macros exactly: FAIL and EXPECT_TOKEN return,
// RECURSE returns after a failed call, and a direct call does not, so a
// failure can be followed by more parsing and a later failure replaces the
// message (V8's messages depend on this).
using V8Sharp.Wasm;
using static V8Sharp.AsmJs.AsmToken;
using Op = V8Sharp.Wasm.WasmOpcodesConst;

namespace V8Sharp.AsmJs;

public sealed partial class AsmJsParser
{
    // The opcodes the translator emits besides WasmOpcodesConst (wasm-opcodes.h).
    const byte kExprI32Eqz = 0x45, kExprI32Eq = 0x46, kExprI32Ne = 0x47;
    const byte kExprI32LtS = 0x48, kExprI32LtU = 0x49, kExprI32GtS = 0x4a, kExprI32GtU = 0x4b;
    const byte kExprI32LeS = 0x4c, kExprI32LeU = 0x4d, kExprI32GeS = 0x4e, kExprI32GeU = 0x4f;
    const byte kExprF32Eq = 0x5b, kExprF32Ne = 0x5c, kExprF32Lt = 0x5d, kExprF32Gt = 0x5e, kExprF32Le = 0x5f, kExprF32Ge = 0x60;
    const byte kExprF64Eq = 0x61, kExprF64Ne = 0x62, kExprF64Lt = 0x63, kExprF64Gt = 0x64, kExprF64Le = 0x65, kExprF64Ge = 0x66;
    const byte kExprI32Clz = 0x67, kExprI32Add = 0x6a, kExprI32Sub = 0x6b, kExprI32Mul = 0x6c;
    const byte kExprI32And = 0x71, kExprI32Ior = 0x72, kExprI32Xor = 0x73;
    const byte kExprI32Shl = 0x74, kExprI32ShrS = 0x75, kExprI32ShrU = 0x76;
    const byte kExprF32Abs = 0x8b, kExprF32Neg = 0x8c, kExprF32Ceil = 0x8d, kExprF32Floor = 0x8e, kExprF32Sqrt = 0x91;
    const byte kExprF32Add = 0x92, kExprF32Sub = 0x93, kExprF32Mul = 0x94, kExprF32Div = 0x95;
    const byte kExprF32Min = 0x96, kExprF32Max = 0x97;
    const byte kExprF64Abs = 0x99, kExprF64Neg = 0x9a, kExprF64Ceil = 0x9b, kExprF64Floor = 0x9c, kExprF64Sqrt = 0x9f;
    const byte kExprF64Add = 0xa0, kExprF64Sub = 0xa1, kExprF64Mul = 0xa2, kExprF64Div = 0xa3;
    const byte kExprF64Min = 0xa4, kExprF64Max = 0xa5;
    const byte kExprF32SConvertI32 = 0xb2, kExprF32UConvertI32 = 0xb3, kExprF32ConvertF64 = 0xb6;
    const byte kExprF64SConvertI32 = 0xb7, kExprF64UConvertI32 = 0xb8, kExprF64ConvertF32 = 0xbb;
    const byte kI32Code = 0x7f, kF32Code = 0x7d, kF64Code = 0x7c;

    // FOREACH_ASMJS_COMPAT_OPCODE (prefixed: 0xfa << 8 | index).
    const int kExprF64Acos = 0xfa3c, kExprF64Asin = 0xfa3d, kExprF64Atan = 0xfa3e, kExprF64Cos = 0xfa3f;
    const int kExprF64Sin = 0xfa40, kExprF64Tan = 0xfa41, kExprF64Exp = 0xfa42, kExprF64Log = 0xfa43;
    const int kExprF64Atan2 = 0xfa44, kExprF64Pow = 0xfa45, kExprF64Mod = 0xfa46;
    const int kExprI32AsmjsDivS = 0xfa47, kExprI32AsmjsDivU = 0xfa48, kExprI32AsmjsRemS = 0xfa49, kExprI32AsmjsRemU = 0xfa4a;
    const int kExprI32AsmjsLoadMem8S = 0xfa4b, kExprI32AsmjsLoadMem8U = 0xfa4c;
    const int kExprI32AsmjsLoadMem16S = 0xfa4d, kExprI32AsmjsLoadMem16U = 0xfa4e;
    const int kExprI32AsmjsLoadMem = 0xfa4f, kExprF32AsmjsLoadMem = 0xfa50, kExprF64AsmjsLoadMem = 0xfa51;
    const int kExprI32AsmjsStoreMem8 = 0xfa52, kExprI32AsmjsStoreMem16 = 0xfa53, kExprI32AsmjsStoreMem = 0xfa54;
    const int kExprF32AsmjsStoreMem = 0xfa55, kExprF64AsmjsStoreMem = 0xfa56;
    const int kExprI32AsmjsSConvertF32 = 0xfa57, kExprI32AsmjsSConvertF64 = 0xfa59;

    // ---- 6.5 ValidateStatement ---------------------------------------------------------

    void ValidateStatement()
    {
        _callCoercion = null;
        if (Peek('{'))
        {
            if (!StackOk()) return;
            Block();
        }
        else if (Peek(';'))
        {
            if (!StackOk()) return;
            EmptyStatement();
        }
        else if (Peek(kToken_if))
        {
            if (!StackOk()) return;
            IfStatement();
        }
        else if (Peek(kToken_return))
        {
            if (!StackOk()) return;
            ReturnStatement();
        }
        else if (IterationStatement())
        {
            // Handled in IterationStatement.
        }
        else if (Peek(kToken_break))
        {
            if (!StackOk()) return;
            BreakStatement();
        }
        else if (Peek(kToken_continue))
        {
            if (!StackOk()) return;
            ContinueStatement();
        }
        else if (Peek(kToken_switch))
        {
            if (!StackOk()) return;
            SwitchStatement();
        }
        else
        {
            if (!StackOk()) return;
            ExpressionStatement();
        }
    }

    // 6.5.1 Block
    void Block()
    {
        bool canBreakToBlock = _pendingLabel != 0;
        if (canBreakToBlock)
        {
            BareBegin(BlockKind.kNamed, _pendingLabel);
            _currentFunctionBuilder.EmitWithU8(Op.kExprBlock, Op.kVoidCode);
        }
        _pendingLabel = 0;
        if (!Expect('{')) return;
        while (!_failed && !Peek('}'))
        {
            if (!StackOk()) return;
            ValidateStatement();
            if (_failed) return;
        }
        if (!Expect('}')) return;
        if (canBreakToBlock) End();
    }

    // 6.5.2 ExpressionStatement
    void ExpressionStatement()
    {
        if (_scanner.IsGlobal() || _scanner.IsLocal())
        {
            // NOTE: Both global or local identifiers can also be used as labels.
            _scanner.Next();
            if (Peek(':'))
            {
                _scanner.Rewind();
                if (!StackOk()) return;
                LabelledStatement();
                return;
            }
            _scanner.Rewind();
        }
        if (!StackOk()) return;
        AsmType? ret = ValidateExpression();
        if (_failed) return;
        if (!ret!.IsA(AsmType.Void())) _currentFunctionBuilder.Emit(Op.kExprDrop);
        SkipSemicolon();
    }

    // 6.5.3 EmptyStatement
    void EmptyStatement() => Expect(';');

    // 6.5.4 IfStatement
    void IfStatement()
    {
        if (!Expect(kToken_if)) return;
        if (!Expect('(')) return;
        if (!StackOk()) return;
        Expression(AsmType.Int());
        if (_failed) return;
        if (!Expect(')')) return;
        BareBegin(BlockKind.kOther);
        _currentFunctionBuilder.EmitWithU8(Op.kExprIf, Op.kVoidCode);
        if (!StackOk()) return;
        ValidateStatement();
        if (_failed) return;
        if (Check(kToken_else))
        {
            _currentFunctionBuilder.Emit(Op.kExprElse);
            if (!StackOk()) return;
            ValidateStatement();
            if (_failed) return;
        }
        _currentFunctionBuilder.Emit(Op.kExprEnd);
        BareEnd();
    }

    // 6.5.5 ReturnStatement
    void ReturnStatement()
    {
        if (!Expect(kToken_return)) return;
        if (!Peek(';') && !Peek('}'))
        {
            // TODO(bradnelson): See if this can be factored out.
            if (!StackOk()) return;
            AsmType? ret = Expression(_returnType);
            if (_failed) return;
            if (ret!.IsA(AsmType.Double())) _returnType = AsmType.Double();
            else if (ret.IsA(AsmType.Float())) _returnType = AsmType.Float();
            else if (ret.IsA(AsmType.Signed())) _returnType = AsmType.Signed();
            else
            {
                Fail("Invalid return type");
                return;
            }
        }
        else if (_returnType is null)
        {
            _returnType = AsmType.Void();
        }
        else if (!_returnType.IsA(AsmType.Void()))
        {
            Fail("Invalid void return type");
            return;
        }
        _currentFunctionBuilder.Emit(Op.kExprReturn);
        SkipSemicolon();
    }

    // 6.5.6 IterationStatement
    bool IterationStatement()
    {
        if (Peek(kToken_while)) WhileStatement();
        else if (Peek(kToken_do)) DoStatement();
        else if (Peek(kToken_for)) ForStatement();
        else return false;
        return true;
    }

    // 6.5.6 IterationStatement - while
    void WhileStatement()
    {
        // a: block {
        Begin(_pendingLabel);
        //   b: loop {
        Loop(_pendingLabel);
        _pendingLabel = 0;
        if (!Expect(kToken_while)) return;
        if (!Expect('(')) return;
        if (!StackOk()) return;
        Expression(AsmType.Int());
        if (_failed) return;
        if (!Expect(')')) return;
        //     if (!CONDITION) break a;
        _currentFunctionBuilder.Emit(kExprI32Eqz);
        _currentFunctionBuilder.EmitWithU8(Op.kExprBrIf, 1);
        //     BODY
        if (!StackOk()) return;
        ValidateStatement();
        if (_failed) return;
        //     continue b;
        _currentFunctionBuilder.EmitWithU8(Op.kExprBr, 0);
        End();
        //   }
        // }
        End();
    }

    // 6.5.6 IterationStatement - do
    void DoStatement()
    {
        // a: block {
        Begin(_pendingLabel);
        //   b: loop {
        Loop();
        //     c: block {  // but treated like loop so continue works
        BareBegin(BlockKind.kLoop, _pendingLabel);
        _currentFunctionBuilder.EmitWithU8(Op.kExprBlock, Op.kVoidCode);
        _pendingLabel = 0;
        if (!Expect(kToken_do)) return;
        //       BODY
        if (!StackOk()) return;
        ValidateStatement();
        if (_failed) return;
        if (!Expect(kToken_while)) return;
        End();
        //     }  // end c
        if (!Expect('(')) return;
        if (!StackOk()) return;
        Expression(AsmType.Int());
        if (_failed) return;
        //     if (!CONDITION) break a;
        _currentFunctionBuilder.Emit(kExprI32Eqz);
        _currentFunctionBuilder.EmitWithU8(Op.kExprBrIf, 1);
        //     continue b;
        _currentFunctionBuilder.EmitWithU8(Op.kExprBr, 0);
        if (!Expect(')')) return;
        //   }  // end b
        End();
        // }  // end a
        End();
        SkipSemicolon();
    }

    // 6.5.6 IterationStatement - for
    void ForStatement()
    {
        if (!Expect(kToken_for)) return;
        if (!Expect('(')) return;
        if (!Peek(';'))
        {
            if (!StackOk()) return;
            AsmType? ret = Expression(null);
            if (_failed) return;
            if (!ret!.IsA(AsmType.Void())) _currentFunctionBuilder.Emit(Op.kExprDrop);
        }
        if (!Expect(';')) return;
        // a: block {
        Begin(_pendingLabel);
        //   b: loop {
        Loop();
        //     c: block {  // but treated like loop so continue works
        BareBegin(BlockKind.kLoop, _pendingLabel);
        _currentFunctionBuilder.EmitWithU8(Op.kExprBlock, Op.kVoidCode);
        _pendingLabel = 0;
        if (!Peek(';'))
        {
            //       if (!CONDITION) break a;
            if (!StackOk()) return;
            Expression(AsmType.Int());
            if (_failed) return;
            _currentFunctionBuilder.Emit(kExprI32Eqz);
            _currentFunctionBuilder.EmitWithU8(Op.kExprBrIf, 2);
        }
        if (!Expect(';')) return;
        // Race past INCREMENT
        int incrementPosition = _scanner.Position;
        ScanToClosingParenthesis();
        if (!Expect(')')) return;
        //       BODY
        if (!StackOk()) return;
        ValidateStatement();
        if (_failed) return;
        //     }  // end c
        End();
        //     INCREMENT
        int endPosition = _scanner.Position;
        _scanner.Seek(incrementPosition);
        if (!Peek(')'))
        {
            if (!StackOk()) return;
            Expression(null);
            if (_failed) return;
            // NOTE: No explicit drop because below break is an implicit drop.
        }
        //     continue b;
        _currentFunctionBuilder.EmitWithU8(Op.kExprBr, 0);
        _scanner.Seek(endPosition);
        //   }  // end b
        End();
        // }  // end a
        End();
    }

    // 6.5.7 BreakStatement
    void BreakStatement()
    {
        if (!Expect(kToken_break)) return;
        int labelName = kTokenNone;
        if (_scanner.IsGlobal() || _scanner.IsLocal())
        {
            // NOTE: Currently using globals/locals for labels too.
            labelName = Consume();
        }
        int depth = FindBreakLabelDepth(labelName);
        if (depth < 0)
        {
            Fail("Illegal break");
            return;
        }
        _currentFunctionBuilder.EmitWithU32V(Op.kExprBr, (uint)depth);
        SkipSemicolon();
    }

    // 6.5.8 ContinueStatement
    void ContinueStatement()
    {
        if (!Expect(kToken_continue)) return;
        int labelName = kTokenNone;
        if (_scanner.IsGlobal() || _scanner.IsLocal())
        {
            // NOTE: Currently using globals/locals for labels too.
            labelName = Consume();
        }
        int depth = FindContinueLabelDepth(labelName);
        if (depth < 0)
        {
            Fail("Illegal continue");
            return;
        }
        _currentFunctionBuilder.EmitWithU32V(Op.kExprBr, (uint)depth);
        SkipSemicolon();
    }

    // 6.5.9 LabelledStatement
    void LabelledStatement()
    {
        // NOTE: Currently using globals/locals for labels too.
        if (_pendingLabel != 0)
        {
            Fail("Double label unsupported");
            return;
        }
        _pendingLabel = _scanner.Token;
        _scanner.Next();
        if (!Expect(':')) return;
        if (!StackOk()) return;
        ValidateStatement();
    }

    // 6.5.10 SwitchStatement
    void SwitchStatement()
    {
        if (!Expect(kToken_switch)) return;
        if (!Expect('(')) return;
        if (!StackOk()) return;
        AsmType? test = Expression(null);
        if (_failed) return;
        if (!test!.IsA(AsmType.Signed()))
        {
            Fail("Expected signed for switch value");
            return;
        }
        if (!Expect(')')) return;
        uint tmp = TempVariable(0);
        _currentFunctionBuilder.EmitSetLocal(tmp);
        Begin(_pendingLabel);
        _pendingLabel = 0;
        // TODO(bradnelson): Make less weird.
        var cases = new List<int>();
        GatherCases(cases);
        if (!Expect('{')) return;
        int count = cases.Count + 1;
        for (int i = 0; i < count; ++i)
        {
            BareBegin(BlockKind.kOther);
            _currentFunctionBuilder.EmitWithU8(Op.kExprBlock, Op.kVoidCode);
        }
        uint tablePos = 0;
        foreach (int c in cases)
        {
            _currentFunctionBuilder.EmitGetLocal(tmp);
            _currentFunctionBuilder.EmitI32Const(c);
            _currentFunctionBuilder.Emit(kExprI32Eq);
            _currentFunctionBuilder.EmitWithU32V(Op.kExprBrIf, tablePos++);
        }
        _currentFunctionBuilder.EmitWithU32V(Op.kExprBr, tablePos++);
        while (!_failed && Peek(kToken_case))
        {
            _currentFunctionBuilder.Emit(Op.kExprEnd);
            BareEnd();
            if (!StackOk()) return;
            ValidateCase();
            if (_failed) return;
        }
        _currentFunctionBuilder.Emit(Op.kExprEnd);
        BareEnd();
        if (Peek(kToken_default))
        {
            if (!StackOk()) return;
            ValidateDefault();
            if (_failed) return;
        }
        if (!Expect('}')) return;
        End();
    }

    // 6.6. ValidateCase
    void ValidateCase()
    {
        if (!Expect(kToken_case)) return;
        bool negate = Check('-');
        if (!CheckForUnsigned(out uint uvalue))
        {
            Fail("Expected numeric literal");
            return;
        }
        // TODO(bradnelson): Share negation plumbing.
        if ((negate && uvalue > 0x80000000) || (!negate && uvalue > 0x7FFFFFFF))
        {
            Fail("Numeric literal out of range");
            return;
        }
        if (!Expect(':')) return;
        while (!_failed && !Peek('}') && !Peek(kToken_case) && !Peek(kToken_default))
        {
            if (!StackOk()) return;
            ValidateStatement();
            if (_failed) return;
        }
    }

    // 6.7 ValidateDefault
    void ValidateDefault()
    {
        if (!Expect(kToken_default)) return;
        if (!Expect(':')) return;
        while (!_failed && !Peek('}'))
        {
            if (!StackOk()) return;
            ValidateStatement();
            if (_failed) return;
        }
    }

    // ---- 6.8 ValidateExpression ------------------------------------------------------------

    AsmType? ValidateExpression()
    {
        if (!StackOk()) return null;
        AsmType? ret = Expression(null);
        if (_failed) return null;
        return ret;
    }

    // 6.8.1 Expression
    AsmType? Expression(AsmType? expected)
    {
        AsmType? a;
        for (;;)
        {
            if (!StackOk()) return null;
            a = AssignmentExpression();
            if (_failed) return null;
            if (Peek(','))
            {
                if (a!.IsA(AsmType.None()))
                {
                    Fail("Expected actual type");
                    return null;
                }
                if (!a.IsA(AsmType.Void())) _currentFunctionBuilder.Emit(Op.kExprDrop);
                if (!Expect(',')) return null;
                continue;
            }
            break;
        }
        if (expected is not null && !a!.IsA(expected))
        {
            Fail("Unexpected type");
            return null;
        }
        return a;
    }

    // 6.8.2 NumericLiteral
    AsmType? NumericLiteral()
    {
        _callCoercion = null;
        if (CheckForDouble(out double dvalue))
        {
            _currentFunctionBuilder.EmitF64Const(dvalue);
            return AsmType.Double();
        }
        if (CheckForUnsigned(out uint uvalue))
        {
            if (uvalue <= 0x7FFFFFFF)
            {
                _currentFunctionBuilder.EmitI32Const((int)uvalue);
                return AsmType.FixNum();
            }
            _currentFunctionBuilder.EmitI32Const((int)uvalue);
            return AsmType.Unsigned();
        }
        Fail("Expected numeric literal.");
        return null;
    }

    // 6.8.3 Identifier
    AsmType? Identifier()
    {
        _callCoercion = null;
        if (_scanner.IsLocal())
        {
            VarInfo info = GetVarInfo(Consume());
            if (info.Kind != VarKind.kLocal)
            {
                Fail("Undefined local variable");
                return null;
            }
            _currentFunctionBuilder.EmitGetLocal(info.Index);
            return info.Type;
        }
        else
        {
            VarInfo info = GetVarInfo(Consume());
            if (info.Kind != VarKind.kGlobal)
            {
                Fail("Undefined global variable");
                return null;
            }
            _currentFunctionBuilder.EmitWithU32V(Op.kExprGlobalGet, VarIndex(info));
            return info.Type;
        }
    }

    // 6.8.4 CallExpression
    AsmType? CallExpression()
    {
        AsmType? ret;
        if (_scanner.IsGlobal() && GetVarInfo(_scanner.Token).Type.IsA(_stdlibFround))
        {
            ValidateFloatCoercion();
            return AsmType.Float();
        }
        else if (_scanner.IsGlobal() && GetVarInfo(_scanner.Token).Type.IsA(AsmType.Heap()))
        {
            if (!StackOk()) return null;
            ret = MemberExpression();
        }
        else if (Peek('('))
        {
            if (!StackOk()) return null;
            ret = ParenthesizedExpression();
        }
        else if (PeekCall())
        {
            if (!StackOk()) return null;
            ret = ValidateCall();
        }
        else if (_scanner.IsLocal() || _scanner.IsGlobal())
        {
            if (!StackOk()) return null;
            ret = Identifier();
        }
        else
        {
            if (!StackOk()) return null;
            ret = NumericLiteral();
        }
        if (_failed) return null;
        return ret;
    }

    static int HeapLoadOpcode(AsmType heapType) =>
        heapType.IsA(AsmType.Int8Array()) ? kExprI32AsmjsLoadMem8S :
        heapType.IsA(AsmType.Uint8Array()) ? kExprI32AsmjsLoadMem8U :
        heapType.IsA(AsmType.Int16Array()) ? kExprI32AsmjsLoadMem16S :
        heapType.IsA(AsmType.Uint16Array()) ? kExprI32AsmjsLoadMem16U :
        heapType.IsA(AsmType.Int32Array()) ? kExprI32AsmjsLoadMem :
        heapType.IsA(AsmType.Uint32Array()) ? kExprI32AsmjsLoadMem :
        heapType.IsA(AsmType.Float32Array()) ? kExprF32AsmjsLoadMem :
        heapType.IsA(AsmType.Float64Array()) ? kExprF64AsmjsLoadMem : -1;

    static int HeapStoreOpcode(AsmType heapType) =>
        heapType.IsA(AsmType.Int8Array()) ? kExprI32AsmjsStoreMem8 :
        heapType.IsA(AsmType.Uint8Array()) ? kExprI32AsmjsStoreMem8 :
        heapType.IsA(AsmType.Int16Array()) ? kExprI32AsmjsStoreMem16 :
        heapType.IsA(AsmType.Uint16Array()) ? kExprI32AsmjsStoreMem16 :
        heapType.IsA(AsmType.Int32Array()) ? kExprI32AsmjsStoreMem :
        heapType.IsA(AsmType.Uint32Array()) ? kExprI32AsmjsStoreMem :
        heapType.IsA(AsmType.Float32Array()) ? kExprF32AsmjsStoreMem :
        heapType.IsA(AsmType.Float64Array()) ? kExprF64AsmjsStoreMem : -1;

    // 6.8.5 MemberExpression
    AsmType? MemberExpression()
    {
        _callCoercion = null;
        if (!StackOk()) return null;
        ValidateHeapAccess();
        if (_failed) return null;
        if (Peek('='))
        {
            _insideHeapAssignment = true;
            return _heapAccessType!.StoreType();
        }
        int load = HeapLoadOpcode(_heapAccessType!);
        if (load >= 0)
        {
            _currentFunctionBuilder.EmitWithPrefix(load);
            return _heapAccessType!.LoadType();
        }
        Fail("Expected valid heap load");
        return null;
    }

    // 6.8.6 AssignmentExpression
    AsmType? AssignmentExpression()
    {
        AsmType? ret;
        if (_scanner.IsGlobal() && GetVarInfo(_scanner.Token).Type.IsA(AsmType.Heap()))
        {
            if (!StackOk()) return null;
            ret = ConditionalExpression();
            if (_failed) return null;
            if (Peek('='))
            {
                if (!_insideHeapAssignment)
                {
                    Fail("Invalid assignment target");
                    return null;
                }
                _insideHeapAssignment = false;
                AsmType heapType = _heapAccessType!;
                if (!Expect('=')) return null;
                if (!StackOk()) return null;
                AsmType? value = AssignmentExpression();
                if (_failed) return null;
                if (!value!.IsA(ret!))
                {
                    Fail("Illegal type stored to heap view");
                    return null;
                }
                ret = value;
                if (heapType.IsA(AsmType.Float32Array()) && value.IsA(AsmType.DoubleQ()))
                {
                    // Assignment to a float32 heap can be used to convert doubles.
                    _currentFunctionBuilder.Emit(kExprF32ConvertF64);
                    ret = AsmType.FloatQ();
                }
                if (heapType.IsA(AsmType.Float64Array()) && value.IsA(AsmType.FloatQ()))
                {
                    // Assignment to a float64 heap can be used to convert floats.
                    _currentFunctionBuilder.Emit(kExprF64ConvertF32);
                    ret = AsmType.DoubleQ();
                }
                int store = HeapStoreOpcode(heapType);
                if (store >= 0)
                {
                    _currentFunctionBuilder.EmitWithPrefix(store);
                    return ret;
                }
            }
        }
        else if (_scanner.IsLocal() || _scanner.IsGlobal())
        {
            VarInfo info = GetVarInfo(_scanner.Token);
            ret = info.Type;
            _scanner.Next();
            if (Check('='))
            {
                // NOTE: Before this point, this might have been VarKind::kUnused even in
                // valid code, as it might be a label.
                if (info.Kind == VarKind.kUnused)
                {
                    Fail("Undeclared assignment target");
                    return null;
                }
                if (!info.MutableVariable)
                {
                    Fail("Expected mutable variable in assignment");
                    return null;
                }
                if (!StackOk()) return null;
                AsmType? value = AssignmentExpression();
                if (_failed) return null;
                if (!value!.IsA(ret))
                {
                    Fail("Type mismatch in assignment");
                    return null;
                }
                if (info.Kind == VarKind.kLocal)
                {
                    _currentFunctionBuilder.EmitTeeLocal(info.Index);
                }
                else
                {
                    _currentFunctionBuilder.EmitWithU32V(Op.kExprGlobalSet, VarIndex(info));
                    _currentFunctionBuilder.EmitWithU32V(Op.kExprGlobalGet, VarIndex(info));
                }
                return ret;
            }
            _scanner.Rewind();
            if (!StackOk()) return null;
            ret = ConditionalExpression();
            if (_failed) return null;
        }
        else
        {
            if (!StackOk()) return null;
            ret = ConditionalExpression();
            if (_failed) return null;
        }
        return ret;
    }

    // 6.8.7 UnaryExpression
    AsmType? UnaryExpression()
    {
        AsmType? ret;
        if (Check('-'))
        {
            if (CheckForUnsigned(out uint uvalue))
            {
                if (uvalue == 0)
                {
                    _currentFunctionBuilder.EmitF64Const(-0.0);
                    ret = AsmType.Double();
                }
                else if (uvalue <= 0x80000000)
                {
                    // TODO(bradnelson): was supposed to be 0x7FFFFFFF, check errata.
                    _currentFunctionBuilder.EmitI32Const(unchecked(-(int)uvalue));
                    ret = AsmType.Signed();
                }
                else
                {
                    Fail("Integer numeric literal out of range.");
                    return null;
                }
            }
            else
            {
                if (!StackOk()) return null;
                ret = UnaryExpression();
                if (_failed) return null;
                if (ret!.IsA(AsmType.Int()))
                {
                    int depth = EnterTemporaryVariableScope();
                    uint tmp = TempVariable(depth);
                    _currentFunctionBuilder.EmitSetLocal(tmp);
                    _currentFunctionBuilder.EmitI32Const(0);
                    _currentFunctionBuilder.EmitGetLocal(tmp);
                    _currentFunctionBuilder.Emit(kExprI32Sub);
                    ret = AsmType.Intish();
                    ExitTemporaryVariableScope();
                }
                else if (ret.IsA(AsmType.DoubleQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF64Neg);
                    ret = AsmType.Double();
                }
                else if (ret.IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF32Neg);
                    ret = AsmType.Floatish();
                }
                else
                {
                    Fail("expected int/double?/float?");
                    return null;
                }
            }
        }
        else if (Peek('+'))
        {
            _callCoercion = AsmType.Double();
            _callCoercionPosition = _scanner.Position;
            _scanner.Next();  // Done late for correct position.
            if (!StackOk()) return null;
            ret = UnaryExpression();
            if (_failed) return null;
            // TODO(bradnelson): Generalize.
            if (ret!.IsA(AsmType.Signed()))
            {
                _currentFunctionBuilder.Emit(kExprF64SConvertI32);
                ret = AsmType.Double();
            }
            else if (ret.IsA(AsmType.Unsigned()))
            {
                _currentFunctionBuilder.Emit(kExprF64UConvertI32);
                ret = AsmType.Double();
            }
            else if (ret.IsA(AsmType.DoubleQ()))
            {
                ret = AsmType.Double();
            }
            else if (ret.IsA(AsmType.FloatQ()))
            {
                _currentFunctionBuilder.Emit(kExprF64ConvertF32);
                ret = AsmType.Double();
            }
            else
            {
                Fail("expected signed/unsigned/double?/float?");
                return null;
            }
        }
        else if (Check('!'))
        {
            if (!StackOk()) return null;
            ret = UnaryExpression();
            if (_failed) return null;
            if (!ret!.IsA(AsmType.Int()))
            {
                Fail("expected int");
                return null;
            }
            _currentFunctionBuilder.Emit(kExprI32Eqz);
        }
        else if (Check('~'))
        {
            if (Check('~'))
            {
                if (!StackOk()) return null;
                ret = UnaryExpression();
                if (_failed) return null;
                if (ret!.IsA(AsmType.Double()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprI32AsmjsSConvertF64);
                }
                else if (ret.IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprI32AsmjsSConvertF32);
                }
                else
                {
                    Fail("expected double or float?");
                    return null;
                }
                ret = AsmType.Signed();
            }
            else
            {
                if (!StackOk()) return null;
                ret = UnaryExpression();
                if (_failed) return null;
                if (!ret!.IsA(AsmType.Intish()))
                {
                    Fail("operator ~ expects intish");
                    return null;
                }
                _currentFunctionBuilder.EmitI32Const(unchecked((int)0xFFFFFFFF));
                _currentFunctionBuilder.Emit(kExprI32Xor);
                ret = AsmType.Signed();
            }
        }
        else
        {
            if (!StackOk()) return null;
            ret = CallExpression();
            if (_failed) return null;
        }
        return ret;
    }

    // 6.8.8 MultiplicativeExpression
    AsmType? MultiplicativeExpression()
    {
        AsmType? a;
        uint uvalue;
        if (CheckForUnsignedBelow(0x100000, out uvalue))
        {
            if (Check('*'))
            {
                if (!StackOk()) return null;
                AsmType? type = UnaryExpression();
                if (_failed) return null;
                if (!type!.IsA(AsmType.Int()))
                {
                    Fail("Expected int");
                    return null;
                }
                int value = (int)uvalue;
                _currentFunctionBuilder.EmitI32Const(value);
                _currentFunctionBuilder.Emit(kExprI32Mul);
                return AsmType.Intish();
            }
            _scanner.Rewind();
            if (!StackOk()) return null;
            a = UnaryExpression();
            if (_failed) return null;
        }
        else if (Check('-'))
        {
            if (!PeekForZero() && CheckForUnsignedBelow(0x100000, out uvalue))
            {
                int value = -(int)uvalue;
                _currentFunctionBuilder.EmitI32Const(value);
                if (Check('*'))
                {
                    if (!StackOk()) return null;
                    AsmType? type = UnaryExpression();
                    if (_failed) return null;
                    if (!type!.IsA(AsmType.Int()))
                    {
                        Fail("Expected int");
                        return null;
                    }
                    _currentFunctionBuilder.Emit(kExprI32Mul);
                    return AsmType.Intish();
                }
                a = AsmType.Signed();
            }
            else
            {
                _scanner.Rewind();
                if (!StackOk()) return null;
                a = UnaryExpression();
                if (_failed) return null;
            }
        }
        else
        {
            if (!StackOk()) return null;
            a = UnaryExpression();
            if (_failed) return null;
        }
        for (;;)
        {
            if (Check('*'))
            {
                if (Check('-'))
                {
                    if (!PeekForZero() && CheckForUnsigned(out uvalue))
                    {
                        if (uvalue >= 0x100000)
                        {
                            Fail("Constant multiple out of range");
                            return null;
                        }
                        if (!a!.IsA(AsmType.Int()))
                        {
                            Fail("Integer multiply of expects int");
                            return null;
                        }
                        int value = -(int)uvalue;
                        _currentFunctionBuilder.EmitI32Const(value);
                        _currentFunctionBuilder.Emit(kExprI32Mul);
                        return AsmType.Intish();
                    }
                    _scanner.Rewind();
                }
                else if (CheckForUnsigned(out uvalue))
                {
                    if (uvalue >= 0x100000)
                    {
                        Fail("Constant multiple out of range");
                        return null;
                    }
                    if (!a!.IsA(AsmType.Int()))
                    {
                        Fail("Integer multiply of expects int");
                        return null;
                    }
                    int value = (int)uvalue;
                    _currentFunctionBuilder.EmitI32Const(value);
                    _currentFunctionBuilder.Emit(kExprI32Mul);
                    return AsmType.Intish();
                }
                if (!StackOk()) return null;
                AsmType? b = UnaryExpression();
                if (_failed) return null;
                if (a!.IsA(AsmType.DoubleQ()) && b!.IsA(AsmType.DoubleQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF64Mul);
                    a = AsmType.Double();
                }
                else if (a.IsA(AsmType.FloatQ()) && b!.IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF32Mul);
                    a = AsmType.Floatish();
                }
                else
                {
                    Fail("expected doubles or floats");
                    return null;
                }
            }
            else if (Check('/'))
            {
                if (!StackOk()) return null;
                AsmType? b = UnaryExpression();
                if (_failed) return null;
                if (a!.IsA(AsmType.DoubleQ()) && b!.IsA(AsmType.DoubleQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF64Div);
                    a = AsmType.Double();
                }
                else if (a.IsA(AsmType.FloatQ()) && b!.IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF32Div);
                    a = AsmType.Floatish();
                }
                else if (a.IsA(AsmType.Signed()) && b!.IsA(AsmType.Signed()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprI32AsmjsDivS);
                    a = AsmType.Intish();
                }
                else if (a.IsA(AsmType.Unsigned()) && b!.IsA(AsmType.Unsigned()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprI32AsmjsDivU);
                    a = AsmType.Intish();
                }
                else
                {
                    Fail("expected doubles or floats");
                    return null;
                }
            }
            else if (Check('%'))
            {
                if (!StackOk()) return null;
                AsmType? b = UnaryExpression();
                if (_failed) return null;
                if (a!.IsA(AsmType.DoubleQ()) && b!.IsA(AsmType.DoubleQ()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprF64Mod);
                    a = AsmType.Double();
                }
                else if (a.IsA(AsmType.Signed()) && b!.IsA(AsmType.Signed()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprI32AsmjsRemS);
                    a = AsmType.Intish();
                }
                else if (a.IsA(AsmType.Unsigned()) && b!.IsA(AsmType.Unsigned()))
                {
                    _currentFunctionBuilder.EmitWithPrefix(kExprI32AsmjsRemU);
                    a = AsmType.Intish();
                }
                else
                {
                    Fail("expected doubles or floats");
                    return null;
                }
            }
            else
            {
                break;
            }
        }
        return a;
    }

    // 6.8.9 AdditiveExpression
    AsmType? AdditiveExpression()
    {
        if (!StackOk()) return null;
        AsmType? a = MultiplicativeExpression();
        if (_failed) return null;
        int n = 0;
        for (;;)
        {
            if (Check('+'))
            {
                if (!StackOk()) return null;
                AsmType? b = MultiplicativeExpression();
                if (_failed) return null;
                if (a!.IsA(AsmType.Double()) && b!.IsA(AsmType.Double()))
                {
                    _currentFunctionBuilder.Emit(kExprF64Add);
                    a = AsmType.Double();
                }
                else if (a.IsA(AsmType.FloatQ()) && b!.IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF32Add);
                    a = AsmType.Floatish();
                }
                else if (a.IsA(AsmType.Int()) && b!.IsA(AsmType.Int()))
                {
                    _currentFunctionBuilder.Emit(kExprI32Add);
                    a = AsmType.Intish();
                    n = 2;
                }
                else if (a.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish()))
                {
                    // TODO(bradnelson): b should really only be Int.
                    // specialize intish to capture count.
                    ++n;
                    if (n > (1 << 20))
                    {
                        Fail("more than 2^20 additive values");
                        return null;
                    }
                    _currentFunctionBuilder.Emit(kExprI32Add);
                }
                else
                {
                    Fail("illegal types for +");
                    return null;
                }
            }
            else if (Check('-'))
            {
                if (!StackOk()) return null;
                AsmType? b = MultiplicativeExpression();
                if (_failed) return null;
                if (a!.IsA(AsmType.Double()) && b!.IsA(AsmType.Double()))
                {
                    _currentFunctionBuilder.Emit(kExprF64Sub);
                    a = AsmType.Double();
                }
                else if (a.IsA(AsmType.FloatQ()) && b!.IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF32Sub);
                    a = AsmType.Floatish();
                }
                else if (a.IsA(AsmType.Int()) && b!.IsA(AsmType.Int()))
                {
                    _currentFunctionBuilder.Emit(kExprI32Sub);
                    a = AsmType.Intish();
                    n = 2;
                }
                else if (a.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish()))
                {
                    // TODO(bradnelson): b should really only be Int.
                    // specialize intish to capture count.
                    ++n;
                    if (n > (1 << 20))
                    {
                        Fail("more than 2^20 additive values");
                        return null;
                    }
                    _currentFunctionBuilder.Emit(kExprI32Sub);
                }
                else
                {
                    Fail("illegal types for +");
                    return null;
                }
            }
            else
            {
                break;
            }
        }
        return a;
    }

    // 6.8.10 ShiftExpression
    AsmType? ShiftExpression()
    {
        if (!StackOk()) return null;
        AsmType? a = AdditiveExpression();
        if (_failed) return null;
        _heapAccessShiftPosition = kNoHeapAccessShift;
        // TODO(bradnelson): Implement backtracking to avoid emitting code
        // for the x >>> 0 case (similar to what's there for |0).
        for (;;)
        {
            switch (_scanner.Token)
            {
                case kToken_SAR:
                {
                    if (!Expect(kToken_SAR)) return null;
                    // Remember position allowing this shift-expression to be used as part
                    // of a heap access operation expecting `a >> n:NumericLiteral`.
                    bool imm = false;
                    int oldPos = 0;
                    int oldCode = 0;
                    uint shiftImm = 0;
                    if (a!.IsA(AsmType.Intish()) && CheckForUnsigned(out shiftImm))
                    {
                        oldPos = _scanner.Position;
                        oldCode = _currentFunctionBuilder.GetPosition();
                        _scanner.Rewind();
                        imm = true;
                    }
                    if (!StackOk()) return null;
                    AsmType? b = AdditiveExpression();
                    if (_failed) return null;
                    // Check for `a >> n:NumericLiteral` pattern.
                    if (imm && oldPos == _scanner.Position)
                    {
                        _heapAccessShiftPosition = oldCode;
                        _heapAccessShiftValue = shiftImm;
                    }
                    else
                    {
                        _heapAccessShiftPosition = kNoHeapAccessShift;
                    }

                    if (!(a.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish())))
                    {
                        Fail("Expected intish for operator >>.");
                        return null;
                    }
                    _currentFunctionBuilder.Emit(kExprI32ShrS);
                    a = AsmType.Signed();
                    continue;
                }
                case kToken_SHL:
                {
                    if (!Expect(kToken_SHL)) return null;
                    if (!StackOk()) return null;
                    AsmType? b = AdditiveExpression();
                    if (_failed) return null;
                    if (!(a!.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish())))
                    {
                        Fail("Expected intish for operator \"<<\".");
                        return null;
                    }
                    _currentFunctionBuilder.Emit(kExprI32Shl);
                    a = AsmType.Signed();
                    // Must happen after the RECURSE call to unset its state!
                    _heapAccessShiftPosition = kNoHeapAccessShift;
                    continue;
                }
                case kToken_SHR:
                {
                    if (!Expect(kToken_SHR)) return null;
                    if (!StackOk()) return null;
                    AsmType? b = AdditiveExpression();
                    if (_failed) return null;
                    if (!(a!.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish())))
                    {
                        Fail("Expected intish for operator \">>>\".");
                        return null;
                    }
                    _currentFunctionBuilder.Emit(kExprI32ShrU);
                    a = AsmType.Unsigned();
                    // Must happen after the RECURSE call to unset its state!
                    _heapAccessShiftPosition = kNoHeapAccessShift;
                    continue;
                }
                default:
                    return a;
            }
        }
    }

    /// <summary>The HANDLE_CASE of RelationalExpression and EqualityExpression.</summary>
    bool CompareOperands(AsmType a, AsmType b, byte sop, byte uop, byte dop, byte fop, string name)
    {
        if (a.IsA(AsmType.Signed()) && b.IsA(AsmType.Signed())) _currentFunctionBuilder.Emit(sop);
        else if (a.IsA(AsmType.Unsigned()) && b.IsA(AsmType.Unsigned())) _currentFunctionBuilder.Emit(uop);
        else if (a.IsA(AsmType.Double()) && b.IsA(AsmType.Double())) _currentFunctionBuilder.Emit(dop);
        else if (a.IsA(AsmType.Float()) && b.IsA(AsmType.Float())) _currentFunctionBuilder.Emit(fop);
        else
        {
            Fail("Expected signed, unsigned, double, or float for operator \"" + name + "\".");
            return false;
        }
        return true;
    }

    // 6.8.11 RelationalExpression
    AsmType? RelationalExpression()
    {
        if (!StackOk()) return null;
        AsmType? a = ShiftExpression();
        if (_failed) return null;
        for (;;)
        {
            int op = _scanner.Token;
            (byte sop, byte uop, byte dop, byte fop, string name) = op switch
            {
                '<' => (kExprI32LtS, kExprI32LtU, kExprF64Lt, kExprF32Lt, "<"),
                kToken_LE => (kExprI32LeS, kExprI32LeU, kExprF64Le, kExprF32Le, "<="),
                '>' => (kExprI32GtS, kExprI32GtU, kExprF64Gt, kExprF32Gt, ">"),
                kToken_GE => (kExprI32GeS, kExprI32GeU, kExprF64Ge, kExprF32Ge, ">="),
                _ => ((byte)0, (byte)0, (byte)0, (byte)0, ""),
            };
            if (name.Length == 0) return a;
            if (!Expect(op)) return null;
            if (!StackOk()) return null;
            AsmType? b = ShiftExpression();
            if (_failed) return null;
            if (!CompareOperands(a!, b!, sop, uop, dop, fop, name)) return null;
            a = AsmType.Int();
        }
    }

    // 6.8.12 EqualityExpression
    AsmType? EqualityExpression()
    {
        if (!StackOk()) return null;
        AsmType? a = RelationalExpression();
        if (_failed) return null;
        for (;;)
        {
            int op = _scanner.Token;
            (byte sop, byte uop, byte dop, byte fop, string name) = op switch
            {
                kToken_EQ => (kExprI32Eq, kExprI32Eq, kExprF64Eq, kExprF32Eq, "=="),
                kToken_NE => (kExprI32Ne, kExprI32Ne, kExprF64Ne, kExprF32Ne, "!="),
                _ => ((byte)0, (byte)0, (byte)0, (byte)0, ""),
            };
            if (name.Length == 0) return a;
            if (!Expect(op)) return null;
            if (!StackOk()) return null;
            AsmType? b = RelationalExpression();
            if (_failed) return null;
            if (!CompareOperands(a!, b!, sop, uop, dop, fop, name)) return null;
            a = AsmType.Int();
        }
    }

    // 6.8.13 BitwiseANDExpression
    AsmType? BitwiseANDExpression()
    {
        if (!StackOk()) return null;
        AsmType? a = EqualityExpression();
        if (_failed) return null;
        while (Check('&'))
        {
            if (!StackOk()) return null;
            AsmType? b = EqualityExpression();
            if (_failed) return null;
            if (a!.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish()))
            {
                _currentFunctionBuilder.Emit(kExprI32And);
                a = AsmType.Signed();
            }
            else
            {
                Fail("Expected intish for operator &.");
                return null;
            }
        }
        return a;
    }

    // 6.8.14 BitwiseXORExpression
    AsmType? BitwiseXORExpression()
    {
        if (!StackOk()) return null;
        AsmType? a = BitwiseANDExpression();
        if (_failed) return null;
        while (Check('^'))
        {
            if (!StackOk()) return null;
            AsmType? b = BitwiseANDExpression();
            if (_failed) return null;
            if (a!.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish()))
            {
                _currentFunctionBuilder.Emit(kExprI32Xor);
                a = AsmType.Signed();
            }
            else
            {
                Fail("Expected intish for operator &.");
                return null;
            }
        }
        return a;
    }

    // 6.8.15 BitwiseORExpression
    AsmType? BitwiseORExpression()
    {
        _callCoercionDeferredPosition = _scanner.Position;
        if (!StackOk()) return null;
        AsmType? a = BitwiseXORExpression();
        if (_failed) return null;
        while (Check('|'))
        {
            // Remember whether the first operand to this OR-expression has requested
            // deferred validation of the |0 annotation.
            // NOTE: This has to happen here to work recursively.
            bool requiresZero = AsmType.IsExactly(_callCoercionDeferred, AsmType.Signed());
            _callCoercionDeferred = null;
            // TODO(bradnelson): Make it prettier.
            bool zero = false;
            int oldPos = 0;
            int oldCode = 0;
            if (a!.IsA(AsmType.Intish()) && CheckForZero())
            {
                oldPos = _scanner.Position;
                oldCode = _currentFunctionBuilder.GetPosition();
                _scanner.Rewind();
                zero = true;
            }
            if (!StackOk()) return null;
            AsmType? b = BitwiseXORExpression();
            if (_failed) return null;
            // Handle |0 specially.
            if (zero && oldPos == _scanner.Position)
            {
                _currentFunctionBuilder.DeleteCodeAfter(oldCode);
                a = AsmType.Signed();
                continue;
            }
            // Anything not matching |0 breaks the lookahead in {ValidateCall}.
            if (requiresZero)
            {
                Fail("Expected |0 type annotation for call");
                return null;
            }
            if (a.IsA(AsmType.Intish()) && b!.IsA(AsmType.Intish()))
            {
                _currentFunctionBuilder.Emit(kExprI32Ior);
                a = AsmType.Signed();
            }
            else
            {
                Fail("Expected intish for operator |.");
                return null;
            }
        }
        return a;
    }

    // 6.8.16 ConditionalExpression
    AsmType? ConditionalExpression()
    {
        if (!StackOk()) return null;
        AsmType? test = BitwiseORExpression();
        if (_failed) return null;
        if (Check('?'))
        {
            if (!test!.IsA(AsmType.Int()))
            {
                Fail("Expected int in condition of ternary operator.");
                return null;
            }
            _currentFunctionBuilder.EmitWithU8(Op.kExprIf, kI32Code);
            int fixup = _currentFunctionBuilder.GetPosition() - 1;  // Assumes encoding knowledge.
            if (!StackOk()) return null;
            AsmType? cons = AssignmentExpression();
            if (_failed) return null;
            _currentFunctionBuilder.Emit(Op.kExprElse);
            if (!Expect(':')) return null;
            if (!StackOk()) return null;
            AsmType? alt = AssignmentExpression();
            if (_failed) return null;
            _currentFunctionBuilder.Emit(Op.kExprEnd);
            if (cons!.IsA(AsmType.Int()) && alt!.IsA(AsmType.Int()))
            {
                _currentFunctionBuilder.FixupByte(fixup, kI32Code);
                return AsmType.Int();
            }
            if (cons.IsA(AsmType.Double()) && alt!.IsA(AsmType.Double()))
            {
                _currentFunctionBuilder.FixupByte(fixup, kF64Code);
                return AsmType.Double();
            }
            if (cons.IsA(AsmType.Float()) && alt!.IsA(AsmType.Float()))
            {
                _currentFunctionBuilder.FixupByte(fixup, kF32Code);
                return AsmType.Float();
            }
            Fail("Type mismatch in ternary operator.");
            return null;
        }
        return test;
    }

    // 6.8.17 ParenthesiedExpression
    AsmType? ParenthesizedExpression()
    {
        _callCoercion = null;
        if (!Expect('(')) return null;
        if (!StackOk()) return null;
        AsmType? ret = Expression(null);
        if (_failed) return null;
        if (!Expect(')')) return null;
        return ret;
    }
}
