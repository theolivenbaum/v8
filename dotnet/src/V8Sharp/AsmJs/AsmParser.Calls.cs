// Port of src/asmjs/asm-parser.cc of V8 14.7, continued: 6.9 ValidateCall,
// 6.10 ValidateHeapAccess, 6.11 ValidateFloatCoercion and the two lookahead
// helpers (ScanToClosingParenthesis, GatherCases).
using V8Sharp.Wasm;
using static V8Sharp.AsmJs.AsmToken;
using Op = V8Sharp.Wasm.WasmOpcodesConst;

namespace V8Sharp.AsmJs;

public sealed partial class AsmJsParser
{
    // 6.9 ValidateCall
    AsmType? ValidateCall()
    {
        AsmType? returnType = _callCoercion;
        _callCoercion = null;
        int callPos = _scanner.Position;
        int toNumberPos = _callCoercionPosition;
        bool allowPeek = _callCoercionDeferredPosition == _scanner.Position;
        int functionName = Consume();

        // Distinguish between ordinary function calls and function table calls. In
        // both cases we might be seeing the {function_name} for the first time and
        // hence allocate a {VarInfo} here, all subsequent uses of the same name then
        // need to match the information stored at this point.
        // (V8: std::optional<TemporaryVariableScope> tmp_scope, which lives to the
        // end of the method.)
        int tmpScopeDepth = -1;
        try
        {
            if (Check('['))
            {
                if (!StackOk()) return null;
                AsmType? index = EqualityExpression();
                if (_failed) return null;
                if (!index!.IsA(AsmType.Intish()))
                {
                    Fail("Expected intish index");
                    return null;
                }
                if (!Expect('&')) return null;
                if (!CheckForUnsigned(out uint mask))
                {
                    Fail("Expected mask literal");
                    return null;
                }
                if (!System.Numerics.BitOperations.IsPow2((ulong)mask + 1))
                {
                    Fail("Expected power of 2 mask");
                    return null;
                }
                _currentFunctionBuilder.EmitI32Const((int)mask);
                _currentFunctionBuilder.Emit(kExprI32And);
                if (!Expect(']')) return null;
                VarInfo tableInfo = GetVarInfo(functionName);
                if (tableInfo.Kind == VarKind.kUnused)
                {
                    if (_moduleBuilder.NumTables == 0) _moduleBuilder.AddTable(WasmValueType.FuncRef, 0);
                    uint funcIndex = _moduleBuilder.IncreaseTableMinSize(0, mask + 1);
                    if (funcIndex == uint.MaxValue)
                    {
                        Fail("Exceeded maximum function table size");
                        return null;
                    }
                    tableInfo.Kind = VarKind.kTable;
                    tableInfo.Mask = mask;
                    tableInfo.Index = funcIndex;
                    tableInfo.MutableVariable = false;
                }
                else
                {
                    if (tableInfo.Kind != VarKind.kTable)
                    {
                        Fail("Expected call table");
                        return null;
                    }
                    if (tableInfo.Mask != mask)
                    {
                        Fail("Mask size mismatch");
                        return null;
                    }
                }
                _currentFunctionBuilder.EmitI32Const((int)tableInfo.Index);
                _currentFunctionBuilder.Emit(kExprI32Add);
                // We have to use a temporary for the correct order of evaluation.
                tmpScopeDepth = EnterTemporaryVariableScope();
                _currentFunctionBuilder.EmitSetLocal(TempVariable(tmpScopeDepth));
                // The position of function table calls is after the table lookup.
                callPos = _scanner.Position;
            }
            else
            {
                VarInfo info = GetVarInfo(functionName);
                if (info.Kind == VarKind.kUnused)
                {
                    info.Kind = VarKind.kFunction;
                    info.FunctionBuilder = _moduleBuilder.AddFunction();
                    info.Index = info.FunctionBuilder.FuncIndex;
                    info.MutableVariable = false;
                }
                else
                {
                    if (info.Kind != VarKind.kFunction && info.Kind < VarKind.kImportedFunction)
                    {
                        Fail("Expected function as call target");
                        return null;
                    }
                }
            }

            // Parse argument list and gather types.
            var paramTypes = new List<AsmType>();
            var paramSpecificTypes = new List<AsmType>();
            if (!Expect('(')) return null;
            while (!_failed && !Peek(')'))
            {
                if (!StackOk()) return null;
                AsmType? t = AssignmentExpression();
                if (_failed) return null;
                paramSpecificTypes.Add(t!);
                if (t!.IsA(AsmType.Int())) paramTypes.Add(AsmType.Int());
                else if (t.IsA(AsmType.Float())) paramTypes.Add(AsmType.Float());
                else if (t.IsA(AsmType.Double())) paramTypes.Add(AsmType.Double());
                else
                {
                    Fail("Bad function argument type");
                    return null;
                }
                if (!Peek(')'))
                {
                    if (!Expect(',')) return null;
                }
            }
            if (!Expect(')')) return null;

            // Reload {VarInfo} after parsing arguments as table might have grown.
            VarInfo functionInfo = GetVarInfo(functionName);

            // We potentially use lookahead in order to determine the return type in case
            // it is not yet clear from the call context. Special care has to be taken to
            // ensure the non-contextual lookahead is valid. The following restrictions
            // substantiate the validity of the lookahead implemented below:
            //  - All calls (except stdlib calls) require some sort of type annotation.
            //  - The coercion to "signed" is part of the {BitwiseORExpression}, any
            //    intermittent expressions like parenthesis in `(callsite(..))|0` are
            //    syntactically not considered coercions.
            //  - The coercion to "double" as part of the {UnaryExpression} has higher
            //    precedence and wins in `+callsite(..)|0` cases. Only "float" return
            //    types are overridden in `fround(callsite(..)|0)` expressions.
            //  - Expected coercions to "signed" are flagged via {call_coercion_deferred}
            //    and later on validated as part of {BitwiseORExpression} to ensure they
            //    indeed apply to the current call expression.
            //  - The deferred validation is only allowed if {BitwiseORExpression} did
            //    promise to fulfill the request via {call_coercion_deferred_position}.
            if (allowPeek && Peek('|') && functionInfo.Kind <= VarKind.kImportedFunction &&
                (returnType is null || returnType.IsA(AsmType.Float())))
            {
                _callCoercionDeferred = AsmType.Signed();
                toNumberPos = _scanner.Position;
                returnType = AsmType.Signed();
            }
            else if (returnType is null)
            {
                toNumberPos = callPos;  // No conversion.
                returnType = AsmType.Void();
            }

            // Compute function type and signature based on gathered types.
            AsmType functionType = AsmType.Function(returnType);
            foreach (AsmType t in paramTypes) functionType.AsFunctionType()!.AddArgument(t);
            FunctionSig sig = ConvertSignature(returnType, paramTypes);
            uint signatureIndex = _moduleBuilder.AddSignature(sig);

            // Emit actual function invocation depending on the kind. At this point we
            // also determined the complete function type and can perform checking against
            // the expected type or update the expected type in case of first occurrence.
            if (functionInfo.Kind == VarKind.kImportedFunction)
            {
                if (paramTypes.Count > AsmJs.kV8MaxWasmFunctionParams)
                {
                    Fail("Number of parameters exceeds internal limit");
                    return null;
                }
                foreach (AsmType t in paramSpecificTypes)
                {
                    if (!t.IsA(AsmType.Extern()))
                    {
                        Fail("Imported function args must be type extern");
                        return null;
                    }
                }
                if (returnType.IsA(AsmType.Float()))
                {
                    Fail("Imported function can't be called as float");
                    return null;
                }
                // TODO(bradnelson): Factor out.
                if (!functionInfo.Import!.Cache.TryGetValue(sig, out uint index))
                {
                    index = _moduleBuilder.AddImport(functionInfo.Import.FunctionName, sig);
                    functionInfo.Import.Cache[sig] = index;
                    functionInfo.FunctionDefined = true;
                }
                _currentFunctionBuilder.AddAsmWasmOffset(callPos, toNumberPos);
                _currentFunctionBuilder.EmitWithU32V(Op.kExprCallFunction, index);
            }
            else if (functionInfo.Kind > VarKind.kImportedFunction)
            {
                if (functionInfo.Type.AsCallableType() is not { } callable)
                {
                    Fail("Expected callable function");
                    return null;
                }
                // TODO(bradnelson): Refactor AsmType to not need this.
                if (callable.CanBeInvokedWith(returnType, paramSpecificTypes))
                {
                    // Return type ok.
                }
                else if (callable.CanBeInvokedWith(AsmType.Float(), paramSpecificTypes))
                {
                    returnType = AsmType.Float();
                }
                else if (callable.CanBeInvokedWith(AsmType.Floatish(), paramSpecificTypes))
                {
                    returnType = AsmType.Floatish();
                }
                else if (callable.CanBeInvokedWith(AsmType.Double(), paramSpecificTypes))
                {
                    returnType = AsmType.Double();
                }
                else if (callable.CanBeInvokedWith(AsmType.Signed(), paramSpecificTypes))
                {
                    returnType = AsmType.Signed();
                }
                else if (callable.CanBeInvokedWith(AsmType.Unsigned(), paramSpecificTypes))
                {
                    returnType = AsmType.Unsigned();
                }
                else
                {
                    Fail("Function use doesn't match definition");
                    return null;
                }
                if (!EmitStdlibCall(functionInfo.Kind, paramSpecificTypes)) return null;
            }
            else
            {
                if (functionInfo.Type.IsA(AsmType.None()))
                {
                    functionInfo.Type = functionType;
                }
                else
                {
                    AsmCallableType? callable = functionInfo.Type.AsCallableType();
                    if (callable is null || !callable.CanBeInvokedWith(returnType, paramSpecificTypes))
                    {
                        Fail("Function use doesn't match definition");
                        return null;
                    }
                }
                if (functionInfo.Kind == VarKind.kTable)
                {
                    _currentFunctionBuilder.EmitGetLocal(TempVariable(tmpScopeDepth));
                    _currentFunctionBuilder.AddAsmWasmOffset(callPos, toNumberPos);
                    _currentFunctionBuilder.Emit(Op.kExprCallIndirect);
                    _currentFunctionBuilder.EmitU32V(signatureIndex);
                    _currentFunctionBuilder.EmitU32V(0);  // table index
                }
                else
                {
                    _currentFunctionBuilder.AddAsmWasmOffset(callPos, toNumberPos);
                    _currentFunctionBuilder.Emit(Op.kExprCallFunction);
                    _currentFunctionBuilder.EmitDirectCallIndex(functionInfo.Index);
                }
            }

            return returnType;
        }
        finally
        {
            if (tmpScopeDepth >= 0) ExitTemporaryVariableScope();
        }
    }

    /// <summary>The stdlib call cases of ValidateCall (switch (function_info->kind)).</summary>
    bool EmitStdlibCall(VarKind kind, List<AsmType> paramSpecificTypes)
    {
        switch (kind)
        {
            // STDLIB_MATH_FUNCTION_MONOMORPHIC_LIST
            case VarKind.kMathAcos: _currentFunctionBuilder.EmitWithPrefix(kExprF64Acos); break;
            case VarKind.kMathAsin: _currentFunctionBuilder.EmitWithPrefix(kExprF64Asin); break;
            case VarKind.kMathAtan: _currentFunctionBuilder.EmitWithPrefix(kExprF64Atan); break;
            case VarKind.kMathCos: _currentFunctionBuilder.EmitWithPrefix(kExprF64Cos); break;
            case VarKind.kMathSin: _currentFunctionBuilder.EmitWithPrefix(kExprF64Sin); break;
            case VarKind.kMathTan: _currentFunctionBuilder.EmitWithPrefix(kExprF64Tan); break;
            case VarKind.kMathExp: _currentFunctionBuilder.EmitWithPrefix(kExprF64Exp); break;
            case VarKind.kMathLog: _currentFunctionBuilder.EmitWithPrefix(kExprF64Log); break;
            case VarKind.kMathAtan2: _currentFunctionBuilder.EmitWithPrefix(kExprF64Atan2); break;
            case VarKind.kMathPow: _currentFunctionBuilder.EmitWithPrefix(kExprF64Pow); break;
            case VarKind.kMathImul: _currentFunctionBuilder.Emit(kExprI32Mul); break;
            case VarKind.kMathClz32: _currentFunctionBuilder.Emit(kExprI32Clz); break;
            // STDLIB_MATH_FUNCTION_CEIL_LIKE_LIST
            case VarKind.kMathCeil:
            case VarKind.kMathFloor:
            case VarKind.kMathSqrt:
            {
                bool isDouble = paramSpecificTypes[0].IsA(AsmType.DoubleQ());
                if (!isDouble && !paramSpecificTypes[0].IsA(AsmType.FloatQ())) throw new InvalidOperationException("unreachable");
                _currentFunctionBuilder.Emit(kind switch
                {
                    VarKind.kMathCeil => isDouble ? kExprF64Ceil : kExprF32Ceil,
                    VarKind.kMathFloor => isDouble ? kExprF64Floor : kExprF32Floor,
                    _ => isDouble ? kExprF64Sqrt : kExprF32Sqrt,
                });
                break;
            }
            case VarKind.kMathMin:
            case VarKind.kMathMax:
                if (paramSpecificTypes[0].IsA(AsmType.Double()))
                {
                    for (int i = 1; i < paramSpecificTypes.Count; ++i)
                    {
                        _currentFunctionBuilder.Emit(kind == VarKind.kMathMin ? kExprF64Min : kExprF64Max);
                    }
                }
                else if (paramSpecificTypes[0].IsA(AsmType.Float()))
                {
                    // NOTE: Not technically part of the asm.js spec, but Firefox
                    // accepts it.
                    for (int i = 1; i < paramSpecificTypes.Count; ++i)
                    {
                        _currentFunctionBuilder.Emit(kind == VarKind.kMathMin ? kExprF32Min : kExprF32Max);
                    }
                }
                else if (paramSpecificTypes[0].IsA(AsmType.Signed()))
                {
                    int tmpX = EnterTemporaryVariableScope();
                    int tmpY = EnterTemporaryVariableScope();
                    for (int i = 1; i < paramSpecificTypes.Count; ++i)
                    {
                        _currentFunctionBuilder.EmitSetLocal(TempVariable(tmpX));
                        _currentFunctionBuilder.EmitTeeLocal(TempVariable(tmpY));
                        _currentFunctionBuilder.EmitGetLocal(TempVariable(tmpX));
                        _currentFunctionBuilder.Emit(kind == VarKind.kMathMin ? kExprI32GeS : kExprI32LeS);
                        _currentFunctionBuilder.EmitWithU8(Op.kExprIf, kI32Code);
                        _currentFunctionBuilder.EmitGetLocal(TempVariable(tmpX));
                        _currentFunctionBuilder.Emit(Op.kExprElse);
                        _currentFunctionBuilder.EmitGetLocal(TempVariable(tmpY));
                        _currentFunctionBuilder.Emit(Op.kExprEnd);
                    }
                    ExitTemporaryVariableScope();
                    ExitTemporaryVariableScope();
                }
                else
                {
                    throw new InvalidOperationException("unreachable");
                }
                break;

            case VarKind.kMathAbs:
                if (paramSpecificTypes[0].IsA(AsmType.Signed()))
                {
                    int tmp = EnterTemporaryVariableScope();
                    _currentFunctionBuilder.EmitTeeLocal(TempVariable(tmp));
                    _currentFunctionBuilder.EmitGetLocal(TempVariable(tmp));
                    _currentFunctionBuilder.EmitI32Const(31);
                    _currentFunctionBuilder.Emit(kExprI32ShrS);
                    _currentFunctionBuilder.EmitTeeLocal(TempVariable(tmp));
                    _currentFunctionBuilder.Emit(kExprI32Xor);
                    _currentFunctionBuilder.EmitGetLocal(TempVariable(tmp));
                    _currentFunctionBuilder.Emit(kExprI32Sub);
                    ExitTemporaryVariableScope();
                }
                else if (paramSpecificTypes[0].IsA(AsmType.DoubleQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF64Abs);
                }
                else if (paramSpecificTypes[0].IsA(AsmType.FloatQ()))
                {
                    _currentFunctionBuilder.Emit(kExprF32Abs);
                }
                else
                {
                    throw new InvalidOperationException("unreachable");
                }
                break;

            default:
                // kMathFround is handled in {AsmJsParser::CallExpression} specially and
                // treated as a coercion to "float" type. Cannot be reached as a call here.
                throw new InvalidOperationException("unreachable");
        }
        return true;
    }

    // 6.9 ValidateCall - helper
    bool PeekCall()
    {
        if (!_scanner.IsGlobal()) return false;
        if (GetVarInfo(_scanner.Token).Kind == VarKind.kFunction) return true;
        if (GetVarInfo(_scanner.Token).Kind >= VarKind.kImportedFunction) return true;
        if (GetVarInfo(_scanner.Token).Kind == VarKind.kUnused || GetVarInfo(_scanner.Token).Kind == VarKind.kTable)
        {
            _scanner.Next();
            if (Peek('(') || Peek('['))
            {
                _scanner.Rewind();
                return true;
            }
            _scanner.Rewind();
        }
        return false;
    }

    // 6.10 ValidateHeapAccess
    void ValidateHeapAccess()
    {
        VarInfo info = GetVarInfo(Consume());
        int size = info.Type.ElementSizeInBytes();
        if (!Expect('[')) return;
        if (CheckForUnsigned(out uint offset))
        {
            // TODO(bradnelson): Check more things.
            // TODO(asmjs): Clarify and explain where this limit is coming from,
            // as it is not mandated by the spec directly.
            if (offset > 0x7FFFFFFF || (ulong)offset * (ulong)size > 0x7FFFFFFF)
            {
                Fail("Heap access out of range");
                return;
            }
            if (Check(']'))
            {
                _currentFunctionBuilder.EmitI32Const((int)(offset * (uint)size));
                // NOTE: This has to happen here to work recursively.
                _heapAccessType = info.Type;
                return;
            }
            _scanner.Rewind();
        }
        AsmType? indexType;
        if (info.Type.IsA(AsmType.Int8Array()) || info.Type.IsA(AsmType.Uint8Array()))
        {
            if (!StackOk()) return;
            indexType = Expression(null);
            if (_failed) return;
        }
        else
        {
            if (!StackOk()) return;
            indexType = ShiftExpression();
            if (_failed) return;
            if (_heapAccessShiftPosition == kNoHeapAccessShift)
            {
                Fail("Expected shift of word size");
                return;
            }
            if (_heapAccessShiftValue > 3)
            {
                Fail("Expected valid heap access shift");
                return;
            }
            if ((1 << (int)_heapAccessShiftValue) != size)
            {
                Fail("Expected heap access shift to match heap view");
                return;
            }
            // Delete the code of the actual shift operation.
            _currentFunctionBuilder.DeleteCodeAfter(_heapAccessShiftPosition);
            // Mask bottom bits to match asm.js behavior.
            _currentFunctionBuilder.EmitI32Const(~(size - 1));
            _currentFunctionBuilder.Emit(kExprI32And);
        }
        if (!indexType!.IsA(AsmType.Intish()))
        {
            Fail("Expected intish index");
            return;
        }
        if (!Expect(']')) return;
        // NOTE: This has to happen here to work recursively.
        _heapAccessType = info.Type;
    }

    // 6.11 ValidateFloatCoercion
    void ValidateFloatCoercion()
    {
        if (!_scanner.IsGlobal() || !GetVarInfo(_scanner.Token).Type.IsA(_stdlibFround))
        {
            Fail("Expected fround");
            return;
        }
        _scanner.Next();
        if (!Expect('(')) return;
        _callCoercion = AsmType.Float();
        // NOTE: The coercion position to float is not observable from JavaScript,
        // because imported functions are not allowed to have float return type.
        _callCoercionPosition = _scanner.Position;
        if (!StackOk()) return;
        AsmType? ret = AssignmentExpression();
        if (_failed) return;
        if (ret!.IsA(AsmType.Floatish()))
        {
            // Do nothing, as already a float.
        }
        else if (ret.IsA(AsmType.DoubleQ()))
        {
            _currentFunctionBuilder.Emit(kExprF32ConvertF64);
        }
        else if (ret.IsA(AsmType.Signed()))
        {
            _currentFunctionBuilder.Emit(kExprF32SConvertI32);
        }
        else if (ret.IsA(AsmType.Unsigned()))
        {
            _currentFunctionBuilder.Emit(kExprF32UConvertI32);
        }
        else
        {
            Fail("Illegal conversion to float");
            return;
        }
        Expect(')');
    }

    /// <summary>
    /// Used as part of {ForStatement}. Scans forward to the next `)` in order to
    /// skip over the third expression in a for-statement. This is one piece that
    /// makes this parser not be a pure single-pass.
    /// </summary>
    void ScanToClosingParenthesis()
    {
        int depth = 0;
        for (;;)
        {
            if (Peek('('))
            {
                ++depth;
            }
            else if (Peek(')'))
            {
                --depth;
                if (depth < 0) break;
            }
            else if (Peek(kEndOfInput))
            {
                break;
            }
            _scanner.Next();
        }
    }

    /// <summary>
    /// Used as part of {SwitchStatement}. Collects all case labels in the current
    /// switch-statement, then resets the scanner position. This is one piece that
    /// makes this parser not be a pure single-pass.
    /// </summary>
    void GatherCases(List<int> cases)
    {
        int start = _scanner.Position;
        int depth = 0;
        for (;;)
        {
            if (Peek('{'))
            {
                ++depth;
            }
            else if (Peek('}'))
            {
                --depth;
                if (depth <= 0) break;
            }
            else if (depth == 1 && Peek(kToken_case))
            {
                _scanner.Next();
                bool negate = Check('-');
                if (!CheckForUnsigned(out uint uvalue)) break;
                int value = (int)uvalue;
                if (negate && value != int.MinValue) value = -value;
                cases.Add(value);
            }
            else if (Peek(kEndOfInput) || Peek(kParseError))
            {
                break;
            }
            _scanner.Next();
        }
        _scanner.Seek(start);
    }
}
