// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/func-name-inferrer.h and func-name-inferrer.cc.

using System.Globalization;
using V8Sharp.Ast;

namespace V8Sharp.Parsing;

// FuncNameInferrer is a stateful class that is used to perform name
// inference for anonymous functions during static analysis of source code.
// Inference is performed in cases when an anonymous function is assigned
// to a variable or a property (see test-func-name-inference.cc for examples.)
//
// The basic idea is that during parsing of LHSs of certain expressions
// (assignments, declarations, object literals) we collect name strings,
// and during parsing of the RHS, a function literal can be collected. After
// parsing the RHS we can infer a name for function literals that do not have
// a name.
public sealed class FuncNameInferrer : IFuncNameInferrer
{
    private enum NameType : byte
    {
        kEnclosingConstructorName,
        kLiteralName,
        kVariableName,
    }

    private readonly struct Name(AstRawString name, NameType type)
    {
        public readonly AstRawString name = name;
        public readonly NameType type = type;
    }

    private readonly AstValueFactory _astValueFactory;
    private readonly List<Name> _namesStack = new(8);
    private readonly List<FunctionLiteral> _funcsToInfer = [];
    private int _scopeDepth;

    public FuncNameInferrer(AstValueFactory ast_value_factory) => _astValueFactory = ast_value_factory;

    // To enter function name inference state, put a FuncNameInferrer::State
    // on the stack (dispose it to leave).
    public readonly struct State : IDisposable
    {
        private readonly FuncNameInferrer _fni;
        private readonly int _top;

        public State(FuncNameInferrer fni)
        {
            _fni = fni;
            _top = fni._namesStack.Count;
            ++_fni._scopeDepth;
        }

        public void Dispose()
        {
            int count = _fni._namesStack.Count;
            if (count > _top) _fni._namesStack.RemoveRange(_top, count - _top);
            --_fni._scopeDepth;
        }
    }

    // FuncNameInferrer::State constructor / destructor, for ParserBase's
    // FuncNameInferrerState.
    public int EnterState()
    {
        ++_scopeDepth;
        return _namesStack.Count;
    }

    public void LeaveState(int top)
    {
        int count = _namesStack.Count;
        if (count > top) _namesStack.RemoveRange(top, count - top);
        --_scopeDepth;
    }

    // Returns whether we have entered name collection state.
    public bool IsOpen() => _scopeDepth > 0;

    // Pushes an enclosing the name of enclosing function onto names stack.
    public void PushEnclosingName(AstRawString name)
    {
        // Enclosing name is a name of a constructor function. To check
        // that it is really a constructor, we check that it is not empty
        // and starts with a capital letter.
        if (!name.IsEmpty() && CharUnicodeInfo.GetUnicodeCategory(name.FirstCharacter()) == UnicodeCategory.UppercaseLetter)
        {
            _namesStack.Add(new Name(name, NameType.kEnclosingConstructorName));
        }
    }

    // Pushes an encountered name onto names stack when in collection state.
    public void PushLiteralName(AstRawString name)
    {
        if (IsOpen() && name != _astValueFactory.prototype_string())
        {
            _namesStack.Add(new Name(name, NameType.kLiteralName));
        }
    }

    public void PushVariableName(AstRawString name)
    {
        if (IsOpen() && name != _astValueFactory.dot_result_string())
        {
            _namesStack.Add(new Name(name, NameType.kVariableName));
        }
    }

    // Adds a function to infer name for.
    public void AddFunction(FunctionLiteral func_to_infer)
    {
        if (IsOpen())
        {
            _funcsToInfer.Add(func_to_infer);
        }
    }

    public void RemoveLastFunction()
    {
        if (IsOpen() && _funcsToInfer.Count != 0) _funcsToInfer.RemoveAt(_funcsToInfer.Count - 1);
    }

    public void RemoveAsyncKeywordFromEnd()
    {
        if (IsOpen())
        {
            if (_namesStack.Count == 0 || !_namesStack[^1].name.IsOneByteEqualTo("async"))
                throw new InvalidOperationException("CHECK(names_stack_.back().name()->IsOneByteEqualTo(\"async\"))");
            _namesStack.RemoveAt(_namesStack.Count - 1);
        }
    }

    // Infers a function name and leaves names collection state.
    public void Infer()
    {
        if (_funcsToInfer.Count != 0) InferFunctionsNames();
    }

    // Constructs a full name in dotted notation from gathered names.
    private AstConsString MakeNameFromStack()
    {
        if (_namesStack.Count == 0)
        {
            return _astValueFactory.empty_cons_string();
        }
        AstConsString result = _astValueFactory.NewConsString();
        for (int it = 0; it < _namesStack.Count;)
        {
            // Advance the iterator to be able to peek the next value.
            Name current = _namesStack[it++];
            // Skip consecutive variable declarations.
            if (it != _namesStack.Count && current.type == NameType.kVariableName &&
                _namesStack[it].type == NameType.kVariableName)
            {
                continue;
            }
            // Add name. Separate names with ".".
            if (!result.IsEmpty())
            {
                result.AddString(_astValueFactory.dot_string());
            }
            result.AddString(current.name);
        }
        return result;
    }

    // Performs name inferring for added functions.
    private void InferFunctionsNames()
    {
        AstConsString func_name = MakeNameFromStack();
        foreach (FunctionLiteral func in _funcsToInfer)
        {
            func.set_raw_inferred_name(func_name);
        }
        _funcsToInfer.Clear();
    }
}
