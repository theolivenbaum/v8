// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser.cc: synthetic functions, literal folding,
// intrinsics, the Parser constructor, program and function entry points,
// class member initializer reparsing.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Runtime;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public sealed partial class Parser
{
    private FunctionLiteral DefaultConstructor(AstRawString name, bool call_super, int pos)
    {
        int expected_property_count = 0;
        const int parameter_count = 0;

        FunctionKind kind = call_super ? FunctionKind.DefaultDerivedConstructor : FunctionKind.DefaultBaseConstructor;
        DeclarationScope function_scope = NewFunctionScope(kind);
        SetLanguageMode(function_scope, LanguageMode.Strict);
        // Set start and end position to the same value
        function_scope.set_start_position(pos);
        function_scope.set_end_position(pos);
        using ScopedPtrList<Statement> body = new(pointer_buffer());

        using (FunctionState function_state = FunctionState.New(this, function_scope))
        {
            // https://tc39.es/ecma262/#sec-runtime-semantics-classdefinitionevaluation
            //
            // 14.a
            //  ...
            //  iv. If F.[[ConstructorKind]] is DERIVED, then
            //    1. NOTE: This branch behaves similarly to constructor(...args) {
            //       super(...args); }. The most notable distinction is that while the
            //       aforementioned ECMAScript source text observably calls the
            //       @@iterator method on %Array.prototype%, this function does not.
            //    2. Let func be ! F.[[GetPrototypeOf]]().
            //    3. If IsConstructor(func) is false, throw a TypeError exception.
            //    4. Let result be ? Construct(func, args, NewTarget).
            //  ...
            if (call_super)
            {
                SuperCallReference super_call_ref = NewSuperCallReferenceNode(pos);
                Expression call = factory().NewSuperCallForwardArgs(super_call_ref, pos);
                body.Add(factory().NewReturnStatement(call, pos));
            }

            expected_property_count = function_state.expected_property_count();
        }

        FunctionLiteral function_literal = factory().NewFunctionLiteral(
            name, function_scope, body, expected_property_count, parameter_count, parameter_count,
            FunctionLiteral.ParameterFlag.kNoDuplicateParameters, FunctionSyntaxKind.AnonymousExpression,
            default_eager_compile_hint(), pos, true, GetNextInfoId());
        return function_literal;
    }

    private FunctionLiteral MakeAutoAccessorGetter(VariableProxy name_proxy, AstRawString name, bool is_static, int pos)
    {
        using ScopedPtrList<Statement> body = new(pointer_buffer());
        DeclarationScope function_scope =
            NewFunctionScope(is_static ? FunctionKind.GetterFunction : FunctionKind.StaticGetterFunction);
        SetLanguageMode(function_scope, LanguageMode.Strict);
        function_scope.set_start_position(pos);
        function_scope.set_end_position(pos);
        using (FunctionState function_state = FunctionState.New(this, function_scope))
        {
            body.Add(factory().NewAutoAccessorGetterBody(name_proxy, pos));
        }
        // TODO(42202709): Enable lazy compilation by adding custom handling in
        //                 `Parser::DoParseFunction`.
        function_scope.set_force_eager_compilation(true);
        FunctionLiteral getter = factory().NewFunctionLiteral(
            null, function_scope, body, 0, 0, 0, FunctionLiteral.ParameterFlag.kNoDuplicateParameters,
            FunctionSyntaxKind.AccessorOrMethod, FunctionLiteral.EagerCompileHint.kShouldEagerCompile, pos, true,
            GetNextInfoId());
        AstRawString prefix = name != null ? ast_value_factory().get_space_string() : null;
        SetFunctionName(getter, name, prefix);
        return getter;
    }

    private FunctionLiteral MakeAutoAccessorSetter(VariableProxy name_proxy, AstRawString name, bool is_static, int pos)
    {
        using ScopedPtrList<Statement> body = new(pointer_buffer());
        DeclarationScope function_scope =
            NewFunctionScope(is_static ? FunctionKind.SetterFunction : FunctionKind.StaticSetterFunction);
        SetLanguageMode(function_scope, LanguageMode.Strict);
        function_scope.set_start_position(pos);
        function_scope.set_end_position(pos);
        function_scope.DeclareParameter(ast_value_factory().empty_string(), VariableMode.Temporary, false, false,
                                        ast_value_factory(), kNoSourcePosition);
        using (FunctionState function_state = FunctionState.New(this, function_scope))
        {
            body.Add(factory().NewAutoAccessorSetterBody(name_proxy, pos));
        }
        // TODO(42202709): Enable lazy compilation by adding custom handling in
        //                 `Parser::DoParseFunction`.
        function_scope.set_force_eager_compilation(true);
        FunctionLiteral setter = factory().NewFunctionLiteral(
            null, function_scope, body, 0, 1, 0, FunctionLiteral.ParameterFlag.kNoDuplicateParameters,
            FunctionSyntaxKind.AccessorOrMethod, FunctionLiteral.EagerCompileHint.kShouldEagerCompile, pos, true,
            GetNextInfoId());
        AstRawString prefix = name != null ? ast_value_factory().set_space_string() : null;
        SetFunctionName(setter, name, prefix);
        return setter;
    }

    private AutoAccessorInfo NewAutoAccessorInfo(ClassScope scope, ClassInfo class_info, AstRawString name,
                                                 bool is_static, int pos)
    {
        VariableProxy accessor_storage_name_proxy = CreateSyntheticContextVariableProxy(
            scope, class_info, AutoAccessorVariableName(ast_value_factory(), class_info.autoaccessor_count++),
            is_static);
        // The property value position will match the beginning of the "accessor"
        // keyword, which can be the same as the start of the parent class scope, use
        // the position of the next two characters to distinguish them.
        FunctionLiteral getter = MakeAutoAccessorGetter(accessor_storage_name_proxy, name, is_static, pos + 1);
        FunctionLiteral setter = MakeAutoAccessorSetter(accessor_storage_name_proxy, name, is_static, pos + 2);
        return factory().NewAutoAccessorInfo(getter, setter, accessor_storage_name_proxy);
    }

    public override ClassLiteralProperty NewClassLiteralPropertyWithAccessorInfo(
        ClassScope scope, ClassInfo class_info, AstRawString name, Expression key, Expression value, bool is_static,
        bool is_computed_name, bool is_private, int pos)
    {
        AutoAccessorInfo accessor_info = NewAutoAccessorInfo(scope, class_info, name, is_static, pos);
        return factory().NewClassLiteralProperty(key, value, accessor_info, is_static, is_computed_name, is_private);
    }

    public override void ReportUnexpectedTokenAt(Scanner.Location location, Token token,
                                                 MessageTemplate message = MessageTemplate.UnexpectedToken)
    {
        string arg = null;
        switch (token)
        {
            case Token.Eos:
                message = MessageTemplate.UnexpectedEOS;
                break;
            case Token.Smi:
            case Token.Number:
            case Token.BigInt:
                message = MessageTemplate.UnexpectedTokenNumber;
                break;
            case Token.String:
                message = MessageTemplate.UnexpectedTokenString;
                break;
            case Token.PrivateName:
            case Token.Identifier:
                message = MessageTemplate.UnexpectedTokenIdentifier;
                // Use ReportMessageAt with the AstRawString parameter; skip the
                // ReportMessageAt below.
                ReportMessageAt(location, message, GetIdentifier());
                return;
            case Token.Await:
            case Token.Enum:
                message = MessageTemplate.UnexpectedReserved;
                break;
            case Token.Let:
            case Token.Static:
            case Token.Yield:
            case Token.FutureStrictReservedWord:
                message = is_strict(language_mode())
                    ? MessageTemplate.UnexpectedStrictReserved
                    : MessageTemplate.UnexpectedTokenIdentifier;
                arg = Token.StringOf(token);
                break;
            case Token.TemplateSpan:
            case Token.TemplateTail:
                message = MessageTemplate.UnexpectedTemplateString;
                break;
            case Token.EscapedStrictReservedWord:
            case Token.EscapedKeyword:
                message = MessageTemplate.InvalidEscapedReservedWord;
                break;
            case Token.Illegal:
                if (scanner().has_error())
                {
                    message = scanner().error();
                    location = scanner().error_location();
                }
                else
                {
                    message = MessageTemplate.InvalidOrUnexpectedToken;
                }
                break;
            case Token.RegExpLiteral:
                message = MessageTemplate.UnexpectedTokenRegExp;
                break;
            default:
                string name = Token.StringOf(token);
                arg = name;
                break;
        }
        ReportMessageAt(location, message, arg);
    }

    // ----------------------------------------------------------------------------
    // Implementation of Parser

    public override bool ShortcutLiteralBinaryExpression(ref Expression x, Expression y, Token op, int pos)
    {
        // Constant fold numeric operations.
        if (x.IsNumberLiteral() && y.IsNumberLiteral())
        {
            double x_val = x.AsLiteral().AsNumber();
            double y_val = y.AsLiteral().AsNumber();
            switch (op)
            {
                case Token.Add:
                    x = factory().NewNumberLiteral(x_val + y_val, pos);
                    return true;
                case Token.Sub:
                    x = factory().NewNumberLiteral(x_val - y_val, pos);
                    return true;
                case Token.Mul:
                    x = factory().NewNumberLiteral(x_val * y_val, pos);
                    return true;
                case Token.Div:
                    x = factory().NewNumberLiteral(x_val / y_val, pos);
                    return true;
                case Token.Mod:
                    x = factory().NewNumberLiteral(NumberConversions.Modulo(x_val, y_val), pos);
                    return true;
                case Token.BitOr:
                {
                    int value = NumberConversions.DoubleToInt32(x_val) | NumberConversions.DoubleToInt32(y_val);
                    x = factory().NewNumberLiteral(value, pos);
                    return true;
                }
                case Token.BitAnd:
                {
                    int value = NumberConversions.DoubleToInt32(x_val) & NumberConversions.DoubleToInt32(y_val);
                    x = factory().NewNumberLiteral(value, pos);
                    return true;
                }
                case Token.BitXor:
                {
                    int value = NumberConversions.DoubleToInt32(x_val) ^ NumberConversions.DoubleToInt32(y_val);
                    x = factory().NewNumberLiteral(value, pos);
                    return true;
                }
                case Token.Shl:
                {
                    // base::ShlWithWraparound: the shift count is masked to 5 bits.
                    int value = NumberConversions.DoubleToInt32(x_val) << (NumberConversions.DoubleToInt32(y_val) & 0x1F);
                    x = factory().NewNumberLiteral(value, pos);
                    return true;
                }
                case Token.Shr:
                {
                    uint shift = (uint)NumberConversions.DoubleToInt32(y_val) & 0x1F;
                    uint value = NumberConversions.DoubleToUint32(x_val) >> (int)shift;
                    x = factory().NewNumberLiteral(value, pos);
                    return true;
                }
                case Token.Sar:
                {
                    uint shift = (uint)NumberConversions.DoubleToInt32(y_val) & 0x1F;
                    int value = NumberConversions.DoubleToInt32(x_val) >> (int)shift;
                    x = factory().NewNumberLiteral(value, pos);
                    return true;
                }
                case Token.Exp:
                    x = factory().NewNumberLiteral(NumberConversions.Pow(x_val, y_val), pos);
                    return true;
                default:
                    break;
            }
        }

        // Constant fold string concatenation:
        //   "abc" + "def" -> "abcdef"
        // Note that this only works for folding into the LHS of a left-associative
        // binary expression. String concatenation folding on the RHS is handled by
        // `CollapseNaryExpression`, which can't re-use this method since non-string
        // literal concatenation is not commutative.
        if (op == Token.Add)
        {
            // TODO(leszeks): We could also eagerly convert other literals to string if
            // one side of the addition is a string.
            if (ShortcutStringLiteralAppendExpression(ref x, y))
            {
                return true;
            }
        }
        return false;
    }

    // Returns true if we have two string literals passed into an add. In that
    // case, *x will be changed to an expression which is the concatenated string.
    private bool ShortcutStringLiteralAppendExpression(ref Expression x, Expression y)
    {
        if (!y.IsStringLiteral()) return false;
        AstRawString y_val = y.AsLiteral().AsRawString();

        // Only consider string concatenation of two strings.
        // TODO(leszeks): We could also eagerly convert other literals to string if
        // one side of the addition is a string. We'd have to be careful around
        // associativity though, in case x is the RHS-most expression of an n-ary
        // addition.
        if (x.IsStringLiteral())
        {
            AstRawString x_val = x.AsLiteral().AsRawString();
            AstConsString cons = ast_value_factory().NewConsString(x_val, y_val);
            x = factory().NewConsStringLiteral(cons, x.position());
            return true;
        }
        if (x.IsConsStringLiteral())
        {
            x.AsLiteral().AsConsString().AddString(y_val);
            x.clear_parenthesized();
            return true;
        }
        return false;
    }

    public override bool CollapseConditionalChain(ref Expression x, Expression cond, Expression then_expression,
                                                  Expression else_expression, int pos, SourceRange then_range)
    {
        if (x != null && x.IsConditionalChain())
        {
            ConditionalChain conditional_chain = (ConditionalChain)x;
            if (then_expression != null)
            {
                conditional_chain.AddChainEntry(cond, then_expression, pos);
                AppendConditionalChainSourceRange(conditional_chain, then_range);
            }
            if (else_expression != null)
            {
                conditional_chain.set_else_expression(else_expression);
            }
            return true;
        }
        return false;
    }

    public override void AppendConditionalChainElse(ref Expression x, SourceRange else_range)
    {
        if (x != null && x.IsConditionalChain())
        {
            ConditionalChain conditional_chain = (ConditionalChain)x;
            AppendConditionalChainElseSourceRange(conditional_chain, else_range);
        }
    }

    // Returns true if we have a binary operation between a binary/n-ary
    // expression (with the same operation) and a value, which can be collapsed
    // into a single n-ary expression. In that case, *x will be changed to an
    // n-ary expression.
    public override bool CollapseNaryExpression(ref Expression x, Expression y, Token op, int pos, SourceRange range)
    {
        // Filter out unsupported ops.
        if (!Token.IsBinaryOp(op) || op == Token.Exp) return false;

        // Convert *x into an nary operation with the given op, returning false if
        // this is not possible.
        NaryOperation nary;
        if (x.IsBinaryOperation())
        {
            BinaryOperation binop = (BinaryOperation)x;
            if (binop.op() != op) return false;

            nary = factory().NewNaryOperation(op, binop.left(), 2);
            nary.AddSubsequent(binop.right(), binop.position());
            ConvertBinaryToNaryOperationSourceRange(binop, nary);
            x = nary;
        }
        else if (x.IsNaryOperation())
        {
            nary = (NaryOperation)x;
            if (nary.op() != op) return false;
        }
        else
        {
            return false;
        }

        Expression last = nary.last();
        // Try to shortcut sequential string literal appends:
        //  expr + "abc" + "def" -> expr + "abcdef"
        // Folding on the LHS of expr is handled by the more general
        // ShortcutLiteralBinaryExpression, this is a special case for RHS string
        // literal concatenation since this is commutative.
        if (op == Token.Add && ShortcutStringLiteralAppendExpression(ref last, y))
        {
            // Append our current expression to the nary operation.
            nary.UpdateLast(last);
        }
        else
        {
            // Otherwise append our current expression to the nary operation.
            nary.AddSubsequent(y, pos);
        }
        nary.clear_parenthesized();
        AppendNaryOperationSourceRange(nary, range);

        return true;
    }

    public override AstRawString GetBigIntAsSymbol()
    {
        ReadOnlySpan<char> literal = scanner().BigIntLiteral();
        if (literal[0] != '0' || literal.Length == 1)
        {
            return ast_value_factory().GetOneByteString(literal.ToString());
        }
        string decimal_ = NumberConversions.BigIntLiteralToDecimal(literal);
        return ast_value_factory().GetOneByteString(decimal_);
    }

    // Returns a UnaryExpression or, in one of the following cases, a Literal.
    // ! <literal> -> true / false
    // + <Number literal> -> <Number literal>
    // - <Number literal> -> <Number literal with value negated>
    // ~ <literal> -> true / false
    public override Expression BuildUnaryExpression(Expression expression, Token op, int pos)
    {
        Literal literal = expression.AsLiteral();
        if (literal != null)
        {
            if (op == Token.Not)
            {
                // Convert the literal to a boolean condition and negate it.
                return factory().NewBooleanLiteral(literal.ToBooleanIsFalse(), pos);
            }
            else if (literal.IsNumberLiteral())
            {
                // Compute some expressions involving only number literals.
                double value = literal.AsNumber();
                switch (op)
                {
                    case Token.Add:
                        return expression;
                    case Token.Sub:
                        return factory().NewNumberLiteral(-value, pos);
                    case Token.BitNot:
                        return factory().NewNumberLiteral(~NumberConversions.DoubleToInt32(value), pos);
                    default:
                        break;
                }
            }
        }
        return factory().NewUnaryOperation(op, expression, pos);
    }

    // Generic AST generator for throwing errors from compiled code.
    private Expression NewThrowError(FunctionId id, MessageTemplate message, AstRawString arg, int pos)
    {
        using ScopedPtrList<Expression> args = new(pointer_buffer());
        args.Add(factory().NewSmiLiteral((int)message, pos));
        args.Add(factory().NewStringLiteral(arg, pos));
        CallRuntime call_constructor = factory().NewCallRuntime(id, args, pos);
        return factory().NewThrow(call_constructor, pos);
    }

    public override Expression NewSuperPropertyReference(int pos)
    {
        AstRawString home_object_name;
        if (IsStatic(scope().GetReceiverScope().function_kind()))
        {
            home_object_name = ast_value_factory_.dot_static_home_object_string();
        }
        else
        {
            home_object_name = ast_value_factory_.dot_home_object_string();
        }

        VariableProxy proxy = NewUnresolved(home_object_name, pos);
        proxy.set_is_home_object();
        return factory().NewSuperPropertyReference(proxy, pos);
    }

    public override Expression NewSuperCallReference(int pos) => NewSuperCallReferenceNode(pos);

    private SuperCallReference NewSuperCallReferenceNode(int pos)
    {
        VariableProxy new_target_proxy = NewUnresolved(ast_value_factory().dot_new_target_string(), pos);
        VariableProxy this_function_proxy = NewUnresolved(ast_value_factory().dot_this_function_string(), pos);
        return factory().NewSuperCallReference(new_target_proxy, this_function_proxy, pos);
    }

    public override Expression NewTargetExpression(int pos)
    {
        VariableProxy proxy = NewUnresolved(ast_value_factory().dot_new_target_string(), pos);
        proxy.set_is_new_target();
        return proxy;
    }

    public override Expression ImportMetaExpression(int pos)
    {
        using ScopedPtrList<Expression> args = new(pointer_buffer());
        if (!has_module_in_scope_chain())
        {
            // When debugging, we permit import.meta invocations -- however, they will
            // never produce a non-undefined result outside of a module.
            return factory().NewUndefinedLiteral(pos);
        }
        return factory().NewCallRuntime(FunctionId.InlineGetImportMetaObject, args, pos);
    }

    public override Expression ExpressionFromLiteral(Token token, int pos)
    {
        switch (token)
        {
            case Token.NullLiteral:
                return factory().NewNullLiteral(pos);
            case Token.TrueLiteral:
                return factory().NewBooleanLiteral(true, pos);
            case Token.FalseLiteral:
                return factory().NewBooleanLiteral(false, pos);
            case Token.Smi:
            {
                uint value = scanner().smi_value();
                return factory().NewSmiLiteral((int)value, pos);
            }
            case Token.Number:
            {
                double value = scanner().DoubleValue();
                return factory().NewNumberLiteral(value, pos);
            }
            case Token.BigInt:
                return factory().NewBigIntLiteral(new AstBigInt(scanner().CurrentLiteralAsCString()), pos);
            case Token.String:
            {
                return factory().NewStringLiteral(GetSymbol(), pos);
            }
            default:
                break;
        }
        return FailureExpression();
    }

    public override Expression NewV8Intrinsic(AstRawString name, ScopedPtrList<Expression> args, int pos)
    {
        // Natives syntax is not allowed in extensions code but it might be useful
        // for debugging purposes provided that --allow-natives-syntax flag is
        // enabled.
        if (ParsingExtension())
        {
            // The extension structures are only accessible while parsing the
            // very first time, not when reparsing because of lazy compilation.
            GetClosureScope().ForceEagerCompilation();
        }

        if (!name.is_one_byte())
        {
            // There are no two-byte named intrinsics.
            ReportMessage(MessageTemplate.NotDefined, name);
            return FailureExpression();
        }

        RuntimeFunction function = Runtime.Runtime.FunctionForName(name.Value);

        // Be more permissive when fuzzing. Intrinsics are not supported.
        if (v8_flags().fuzzing)
        {
            return NewV8RuntimeFunctionForFuzzing(function, args, pos);
        }

        if (function == null)
        {
            ReportMessage(MessageTemplate.NotDefined, name);
            return FailureExpression();
        }

        // Check that the expected number of arguments are being passed.
        if (function.nargs != -1 && function.nargs != args.length())
        {
            ReportMessage(MessageTemplate.RuntimeWrongNumArgs);
            return FailureExpression();
        }

        return factory().NewCallRuntime(function, args, pos);
    }

    // More permissive runtime-function creation on fuzzers.
    private Expression NewV8RuntimeFunctionForFuzzing(RuntimeFunction function, ScopedPtrList<Expression> args,
                                                      int pos)
    {
        // Intrinsics are not supported for fuzzing. Only allow runtime functions
        // marked as fuzzing-safe. Also prevent later errors due to too few arguments
        // and just ignore this call.
        if (function == null || !RuntimeFuzzing.IsEnabledForFuzzing(function.function_id, v8_flags()) ||
            function.nargs > args.length())
        {
            return factory().NewUndefinedLiteral(kNoSourcePosition);
        }

        // Flexible number of arguments permitted.
        if (function.nargs == -1)
        {
            return factory().NewCallRuntime(function, args, pos);
        }

        // Otherwise ignore superfluous arguments.
        using ScopedPtrList<Expression> permissive_args = new(pointer_buffer());
        for (int i = 0; i < function.nargs; i++)
        {
            permissive_args.Add(args.at(i));
        }
        return factory().NewCallRuntime(function, permissive_args, pos);
    }

    public Parser(ParseInfo info)
        : this(info, new Scanner(info.character_stream(), info.flags(),
                                 info.v8_flags().enable_experimental_regexp_engine))
    {
    }

    private Parser(ParseInfo info, Scanner scanner)
        : base(scanner, info.ast_value_factory(), info.pending_error_handler(), info.flags(), info.v8_flags(),
               new AstNodeFactory(info.ast_value_factory()), new FuncNameInferrer(info.ast_value_factory()),
               info.flags().compile_hints_magic_enabled(),
               info.flags().compile_hints_per_function_magic_enabled())
    {
        info_ = info;
        scanner_ = scanner;
        reusable_preparser_ = null;
        mode_ = Mode.PARSE_EAGERLY; // Lazy mode must be set explicitly.
        source_range_map_ = info.source_range_map();
        total_preparse_skipped_ = 0;
        consumed_preparse_data_ = info.consumed_preparse_data();
        parameters_end_pos_ = info.parameters_end_pos();
        // Determine if functions can be lazily compiled. This is necessary to
        // allow some of our builtin JS files to be lazily compiled. These
        // builtins cannot be handled lazily by the parser, since we have to know
        // if a function uses the special natives syntax, which is something the
        // parser records.
        // If the debugger requests compilation for break points, we cannot be
        // aggressive about lazy compilation, because it might trigger compilation
        // of functions without an outer context when setting a breakpoint through
        // Debug::FindSharedFunctionInfoInScript
        // We also compile eagerly for kProduceExhaustiveCodeCache.
        bool can_compile_lazily = flags().allow_lazy_compile() && !flags().is_eager();

        set_default_eager_compile_hint(can_compile_lazily
                                           ? FunctionLiteral.EagerCompileHint.kShouldLazyCompile
                                           : FunctionLiteral.EagerCompileHint.kShouldEagerCompile);
        allow_lazy_ = flags().allow_lazy_compile() && flags().allow_lazy_parsing() && info.extension() == null &&
                      can_compile_lazily;
    }

    // Initializes an empty scope chain for top-level scripts, or scopes which
    // consist of only the native context.
    public void InitializeEmptyScopeChain(ParseInfo info)
    {
        DeclarationScope script_scope = NewScriptScope(flags().is_repl_mode() ? REPLMode.Yes : REPLMode.No);
        info.set_script_scope(script_scope);
        original_scope_ = script_scope;
    }

    // Deserialize the scope chain prior to parsing in which the script is going
    // to be executed. If the script is a top-level script, or the scope chain
    // consists of only a native context, maybe_outer_scope_info should be
    // null.
    //
    // This only deserializes the scope chain, but doesn't connect the scopes to
    // their corresponding scope infos. Therefore, looking up variables in the
    // deserialized scopes is not possible.
    public void DeserializeScopeChain(ParseInfo info, IScopeInfo maybe_outer_scope_info,
                                      Scope.DeserializationMode mode = Scope.DeserializationMode.kScopesOnly,
                                      IScriptEvalOrigin script = null)
    {
        InitializeEmptyScopeChain(info);
        IScopeInfo outer_scope_info = maybe_outer_scope_info;
        if (outer_scope_info != null)
        {
            IScriptEvalOrigin eval_from_script = script;
            IScopeInfo eval_from_scope_info = null;
            if (eval_from_script != null && eval_from_script.eval_from_scope_info() != null)
            {
                if (info.flags().is_eval())
                {
                    // If we're compiling the eval string itself, we skip the script created
                    // for the eval script. This eval scope will be created by the parser as
                    // an unresolved scope.
                    eval_from_script = eval_from_script.eval_from_shared_script();
                }
                if (eval_from_script != null && eval_from_script.eval_from_scope_info() != null)
                {
                    eval_from_scope_info = eval_from_script.eval_from_scope_info();
                }
            }

            original_scope_ = Scope.DeserializeScopeChain(info.scope_info_provider(), outer_scope_info,
                                                          info.script_scope(), ast_value_factory(), mode,
                                                          eval_from_script, eval_from_scope_info, info);

            DeclarationScope receiver_scope = original_scope_.GetReceiverScope();
            if (receiver_scope.HasReceiverToDeserialize())
            {
                receiver_scope.DeserializeReceiver(ast_value_factory());
            }
            if (info.has_module_in_scope_chain())
            {
                set_has_module_in_scope_chain();
            }
        }
    }

    private static void MaybeProcessSourceRanges(ParseInfo parse_info, Expression root)
    {
        if (parse_info.source_range_map() != null)
        {
            SourceRangeAstVisitor visitor = new(root, parse_info.source_range_map());
            visitor.Run();
        }
    }

    // Sets the literal on |info| if parsing succeeded.
    public void ParseProgram(IParsingScript script, ParseInfo info, IScopeInfo maybe_outer_scope_info)
    {
        // Initialize parser state.
        DeserializeScopeChain(info, maybe_outer_scope_info, Scope.DeserializationMode.kIncludingVariables, script);

        if (script != null && script.is_wrapped())
        {
            maybe_wrapped_arguments_ = script.wrapped_arguments();
        }

        scanner_.Initialize();
        FunctionLiteral result = DoParseProgram(info, script?.eval_from_position() ?? kNoSourcePosition);
        if (result == null) return;
        result.scope().set_is_hoisted_in_context(info.flags().is_hoisted_in_context());
        if (flags().allow_heap_allocation())
        {
            MaybeProcessSourceRanges(info, result);
        }
        PostProcessParseResult(info, result);
    }

    // Called by ParseProgram after setting up the scanner.
    private FunctionLiteral DoParseProgram(ParseInfo info, int eval_from_position)
    {
        using ModeScope mode_scope = new(this, allow_lazy_ ? Mode.PARSE_LAZILY : Mode.PARSE_EAGERLY);
        ResetInfoId(kFunctionLiteralIdTopLevel);

        FunctionLiteral result;
        {
            Scope outer = original_scope_;
            if (flags().is_eval())
            {
                DeclarationScope eval_scope = NewEvalScope(outer);
                eval_scope.set_eval_position(Math.Max(0, eval_from_position));
                outer = eval_scope;
            }
            else if (flags().is_module())
            {
                outer = NewModuleScope(info.script_scope());
            }

            DeclarationScope scope = outer.AsDeclarationScope();
            scope.set_start_position(0);

            using FunctionState function_state = FunctionState.New(this, scope);
            using ScopedPtrList<Statement> body = new(pointer_buffer());
            int beg_pos = scanner().location().beg_pos;
            if (flags().is_module())
            {
                PrepareGeneratorVariables();
                Expression initial_yield = BuildInitialYield(kNoSourcePosition, FunctionKind.GeneratorFunction);
                body.Add(factory().NewExpressionStatement(initial_yield, kNoSourcePosition));
                ParseModuleItemList(body);
                // Modules will always have an initial yield. If there are any
                // additional suspends, they are awaits, and we treat the module as a
                // ModuleWithTopLevelAwait.
                if (function_state.suspend_count() > 1)
                {
                    scope.set_module_has_toplevel_await();
                }
                if (!has_error() && !module().Validate(this.scope().AsModuleScope(), pending_error_handler(),
                                                       v8_flags()))
                {
                    scanner().set_parser_error();
                }
            }
            else if (info.is_wrapped_as_function())
            {
                ParseWrapped(info, body, scope);
            }
            else if (flags().is_repl_mode())
            {
                ParseREPLProgram(info, body, scope);
            }
            else
            {
                // Don't count the mode in the use counters--give the program a chance
                // to enable script-wide strict mode below.
                this.scope().SetLanguageMode(info.language_mode());
                ParseStatementList(body, Token.Eos);
            }

            // The parser will peek but not consume kEos.  Our scope logically goes all
            // the way to the kEos, though.
            scope.set_end_position(peek_position());

            if (is_strict(language_mode()))
            {
                CheckStrictOctalLiteral(beg_pos, end_position());
            }
            if (is_sloppy(language_mode()))
            {
                // TODO(littledan): Function bindings on the global object that modify
                // pre-existing bindings should be made writable, enumerable and
                // nonconfigurable if possible, whereas this code will leave attributes
                // unchanged if the property already exists.
                InsertSloppyBlockFunctionVarBindings(scope);
            }
            // Internalize the ast strings in the case of eval so we can check for
            // conflicting var declarations with outer scope-info-backed scopes.
            if (flags().allow_heap_allocation())
            {
                CheckConflictingVarDeclarations(scope);
            }

            // For sloppy eval though, we clear dynamic variables created for toplevel
            // var to avoid resolving to a variable when the variable and proxy are in
            // the same eval execution. The variable is not available on subsequent lazy
            // executions of functions in the eval, so this avoids inner functions from
            // looking up different variables during eager and lazy compilation.
            if (flags().is_eval()) outer.RemoveDynamic();

            if (flags().parse_restriction() == ParseRestriction.ONLY_SINGLE_FUNCTION_LITERAL)
            {
                if (body.length() != 1 || !body.at(0).IsExpressionStatement() ||
                    !((ExpressionStatement)body.at(0)).expression().IsFunctionLiteral())
                {
                    ReportMessage(MessageTemplate.SingleFunctionLiteral);
                }
            }

            int parameter_count = 0;
            result = factory().NewScriptOrEvalFunctionLiteral(scope, body, function_state.expected_property_count(),
                                                              parameter_count);
            result.set_suspend_count(function_state.suspend_count());
        }

        info.set_max_info_id(GetLastInfoId());

        if (has_error()) return null;

        RecordFunctionLiteralSourceRange(result);

        return result;
    }

    private void PostProcessParseResult(ParseInfo info, FunctionLiteral literal)
    {
        info.set_literal(literal);
        info.set_language_mode(literal.language_mode());
        if (info.flags().is_eval())
        {
            info.set_allow_eval_cache(allow_eval_cache());
        }

        {
            DeclarationScope scope = literal.scope().AsDeclarationScope();
            // Top-level variables in a script can be accessed by other scripts.
            if (scope.is_script_scope())
            {
                foreach (Variable var in scope.locals())
                {
                    var.set_is_used();
                    if (var.mode() != VariableMode.Const || flags().is_repl_mode())
                    {
                        var.SetMaybeAssigned();
                    }
                }
            }

            if (!Rewriter.Rewrite(info, out bool has_stack_overflow) || !DeclarationScope.Analyze(info))
            {
                // Null out the literal to indicate that something failed.
                info.set_literal(null);
                if (has_stack_overflow)
                {
                    // Propagate stack overflow state from Rewriter to parser.
                    set_stack_overflow();
                }
                return;
            }
        }
    }

    private List<AstRawString> PrepareWrappedArguments(ParseInfo info)
    {
        IReadOnlyList<string> arguments = maybe_wrapped_arguments_;
        int arguments_length = arguments.Count;
        List<AstRawString> arguments_for_wrapped_function = new(arguments_length);
        for (int i = 0; i < arguments_length; i++)
        {
            AstRawString argument_string = ast_value_factory().GetString(arguments[i]);
            arguments_for_wrapped_function.Add(argument_string);
        }
        return arguments_for_wrapped_function;
    }

    // Parse with the script as if the source is implicitly wrapped in a function.
    // We manually construct the AST and scopes for a top-level function and the
    // function wrapper.
    private void ParseWrapped(ParseInfo info, ScopedPtrList<Statement> body, DeclarationScope outer_scope)
    {
        using ModeScope mode_scope = new(this, Mode.PARSE_EAGERLY);

        // Set function and block state for the outer eval scope.
        using FunctionState function_state = FunctionState.New(this, outer_scope);

        AstRawString function_name = null;
        Scanner.Location location = new(0, 0);

        List<AstRawString> arguments_for_wrapped_function = PrepareWrappedArguments(info);

        FunctionLiteral function_literal = ParseFunctionLiteral(
            function_name, location, FunctionNameValidity.kSkipFunctionNameCheck, FunctionKind.NormalFunction,
            kNoSourcePosition, FunctionSyntaxKind.Wrapped, LanguageMode.Sloppy, arguments_for_wrapped_function);

        Statement return_statement = factory().NewReturnStatement(function_literal, kNoSourcePosition);
        body.Add(return_statement);
    }

    private void ParseREPLProgram(ParseInfo info, ScopedPtrList<Statement> body, DeclarationScope scope)
    {
        // REPL scripts are handled nearly the same way as the body of an async
        // function. The difference is the value used to resolve the async
        // promise.
        // For a REPL script this is the completion value of the
        // script instead of the expression of some "return" statement. The
        // completion value of the script is obtained by manually invoking
        // the {Rewriter} which will return a VariableProxy referencing the
        // result.
        this.scope().SetLanguageMode(info.language_mode());
        PrepareGeneratorVariables();

        Block block;
        {
            using ScopedPtrList<Statement> statements = new(pointer_buffer());
            ParseStatementList(statements, Token.Eos);
            block = factory().NewBlock(true, statements);
        }

        if (has_error()) return;

        if (!Rewriter.RewriteBody(info, scope, block.statements(), out bool has_stack_overflow,
                                  out VariableProxy maybe_result))
        {
            if (has_stack_overflow)
            {
                // Propagate stack overflow state from Rewriter to parser.
                set_stack_overflow();
            }
            return;
        }
        Expression result_value = maybe_result != null
            ? maybe_result
            : factory().NewUndefinedLiteral(kNoSourcePosition);
        Expression wrapped_result_value = WrapREPLResult(result_value);
        block.statements().Add(factory().NewAsyncReturnStatement(wrapped_result_value, kNoSourcePosition));
        body.Add(block);
    }

    private Expression WrapREPLResult(Expression value)
    {
        // REPL scripts additionally wrap the ".result" variable in an
        // object literal:
        //
        //     return %_AsyncFunctionResolve(
        //               .generator_object, {__proto__: null, .repl_result: .result});
        //
        // Should ".result" be a resolved promise itself, the async return
        // would chain the promises and return the resolve value instead of
        // the promise.

        Literal property_name = factory().NewStringLiteral(ast_value_factory().dot_repl_result_string(),
                                                           kNoSourcePosition);
        ObjectLiteralProperty property = factory().NewObjectLiteralProperty(property_name, value, true);

        Literal proto_name = factory().NewStringLiteral(ast_value_factory().proto_string(), kNoSourcePosition);
        ObjectLiteralProperty prototype =
            factory().NewObjectLiteralProperty(proto_name, factory().NewNullLiteral(kNoSourcePosition), false);

        using ScopedPtrList<ObjectLiteralProperty> properties = new(pointer_buffer());
        properties.Add(property);
        properties.Add(prototype);
        return factory().NewObjectLiteral(properties, 0, kNoSourcePosition, false);
    }

    // Sets the literal on |info| if parsing succeeded.
    public void ParseFunction(ParseInfo info, IParsingSharedFunctionInfo shared_info)
    {
        IScopeInfo maybe_outer_scope_info = null;
        if (shared_info.HasOuterScopeInfo())
        {
            maybe_outer_scope_info = shared_info.GetOuterScopeInfo();
        }
        int start_position = shared_info.StartPosition();
        int end_position = shared_info.EndPosition();

        IParsingScript script = shared_info.script();

        DeserializeScopeChain(info, maybe_outer_scope_info, Scope.DeserializationMode.kIncludingVariables, script);

        if (shared_info.is_wrapped())
        {
            maybe_wrapped_arguments_ = script.wrapped_arguments();
        }

        int function_literal_id = shared_info.function_literal_id();

        // Initialize parser state.
        info.set_function_name(ast_value_factory().GetString(shared_info.Name()));
        scanner_.Initialize();

        FunctionKind function_kind = flags().function_kind();
        FunctionLiteral result;
        if (IsClassInitializerFunction(function_kind))
        {
            // Reparsing of class member initializer functions has to be handled
            // specially because they require reparsing of the whole class body,
            // function start/end positions correspond to the class literal body
            // positions.
            result = ParseClassForMemberInitialization(function_kind, start_position, function_literal_id,
                                                       end_position, info.function_name());
            info.set_max_info_id(GetLastInfoId());
        }
        else if (shared_info.private_name_lookup_skips_outer_class() && original_scope_.is_class_scope())
        {
            // If the function skips the outer class and the outer scope is a class, the
            // function is in heritage position. Otherwise the function scope's skip bit
            // will be correctly inherited from the outer scope.
            using ClassScope.HeritageParsingScope heritage = new(original_scope_.AsClassScope());
            result = DoParseFunction(info, start_position, end_position, function_literal_id, info.function_name());
        }
        else
        {
            result = DoParseFunction(info, start_position, end_position, function_literal_id, info.function_name());
        }
        if (result == null) return;

        result.scope().set_is_hoisted_in_context(info.flags().is_hoisted_in_context());

        MaybeProcessSourceRanges(info, result);
        PostProcessParseResult(info, result);
    }

    private FunctionLiteral DoParseFunction(ParseInfo info, int start_position, int end_position,
                                            int function_literal_id, AstRawString raw_name)
    {
        fni_.PushEnclosingName(raw_name);

        ResetInfoId(function_literal_id - 1);

        using ModeScope mode_scope = new(this, Mode.PARSE_EAGERLY);

        // Place holder for the result.
        FunctionLiteral result = null;

        {
            // Parse the function literal.
            Scope outer = original_scope_;
            DeclarationScope outer_function = outer.GetClosureScope();
            using FunctionState function_state = FunctionState.New(this, outer_function);
            using BlockState block_state = new(this, outer);
            FunctionKind kind = flags().function_kind();

            if (IsArrowFunction(kind))
            {
                if (IsAsyncFunction(kind))
                {
                    if (!Check(Token.Async))
                    {
                        if (!stack_overflow()) throw new InvalidOperationException("CHECK(stack_overflow())");
                        return null;
                    }
                    if (!(peek_any_identifier() || peek() == Token.LeftParen))
                    {
                        if (!stack_overflow()) throw new InvalidOperationException("CHECK(stack_overflow())");
                        return null;
                    }
                }

                if (function_literal_id != GetNextInfoId())
                {
                    throw new InvalidOperationException("CHECK_EQ(function_literal_id, GetNextInfoId())");
                }

                // TODO(adamk): We should construct this scope from the ScopeInfo.
                DeclarationScope scope = NewFunctionScope(kind);
                scope.set_has_checked_syntax(true);

                // This bit only needs to be explicitly set because we're
                // not passing the ScopeInfo to the Scope constructor.
                SetLanguageMode(scope, info.language_mode());

                scope.set_start_position(start_position);
                ParserFormalParameters formals = new(scope);
                {
                    using ParameterDeclarationParsingScope formals_scope = ParameterDeclarationParsingScope.New(this);
                    // Parsing patterns as variable reference expression creates
                    // NewUnresolved references in current scope. Enter arrow function
                    // scope for formal parameter parsing.
                    using BlockState inner_block_state = new(this, scope);
                    if (Check(Token.LeftParen))
                    {
                        // '(' StrictFormalParameters ')'
                        ParseFormalParameterList(formals);
                        Expect(Token.RightParen);
                    }
                    else
                    {
                        // BindingIdentifier
                        using ParameterParsingScope parameter_parsing_scope = new(this, formals);
                        ParseFormalParameter(formals);
                        DeclareFormalParameters(formals);
                    }
                    formals.duplicate_loc = formals_scope.duplicate_location();
                }

                // It doesn't really matter what value we pass here for
                // could_be_immediately_invoked since we already introduced an eager
                // compilation scope above.
                bool could_be_immediately_invoked = false;
                Expression expression = ParseArrowFunctionLiteral(formals, function_literal_id,
                                                                  could_be_immediately_invoked);
                // Scanning must end at the same position that was recorded
                // previously. If not, parsing has been interrupted due to a stack
                // overflow, at which point the partially parsed arrow function
                // concise body happens to be a valid expression. This is a problem
                // only for arrow functions with single expression bodies, since there
                // is no end token such as "}" for normal functions.
                if (scanner().location().end_pos == end_position)
                {
                    // The pre-parser saw an arrow function here, so the full parser
                    // must produce a FunctionLiteral.
                    result = (FunctionLiteral)expression;
                }
            }
            else if (IsDefaultConstructor(kind))
            {
                result = DefaultConstructor(raw_name, IsDerivedConstructor(kind), start_position);
            }
            else
            {
                List<AstRawString> arguments_for_wrapped_function =
                    info.is_wrapped_as_function() ? PrepareWrappedArguments(info) : null;
                result = ParseFunctionLiteral(raw_name, Scanner.Location.invalid(),
                                              FunctionNameValidity.kSkipFunctionNameCheck, kind, kNoSourcePosition,
                                              flags().function_syntax_kind(), info.language_mode(),
                                              arguments_for_wrapped_function);
            }

            if (has_error()) return null;
            result.set_requires_instance_members_initializer(flags().requires_instance_members_initializer());
            result.set_class_scope_has_private_brand(flags().class_scope_has_private_brand());
            result.set_has_static_private_methods_or_accessors(flags().has_static_private_methods_or_accessors());
        }

        info.set_max_info_id(GetLastInfoId());

        return result;
    }

    private FunctionLiteral ParseClassForMemberInitialization(FunctionKind initializer_kind, int initializer_pos,
                                                              int initializer_id, int initializer_end_pos,
                                                              AstRawString class_name)
    {
        // When the function is a class members initializer function, we record the
        // source range of the entire class body as its positions in its SFI, so at
        // this point the scanner should be rewound to the position of the class
        // token.
        // Insert a FunctionState with the closest outer Declaration scope
        DeclarationScope nearest_decl_scope = original_scope_.GetDeclarationScope();
        using FunctionState function_state = FunctionState.New(this, nearest_decl_scope);

        // We preparse the class members that are not fields with initializers
        // in order to collect the function literal ids.
        using ModeScope mode_scope = new(this, Mode.PARSE_LAZILY);

        using ExpressionParsingScope no_expression_scope = ExpressionParsingScope.New(this);

        // Reparse the whole class body to build member initializer functions.
        FunctionLiteral initializer;
        {
            bool is_anonymous = IsEmptyIdentifier(class_name);
            using BlockState block_state = new(this, original_scope_);
            RaiseLanguageMode(LanguageMode.Strict);

            using BlockState object_literal_scope_state = new(this, null, object_literal_stack: true);

            ClassInfo class_info = new(this);
            class_info.is_anonymous = is_anonymous;

            // Create an arbitrary non-Null expression to indicate that the class
            // extends something. Doing so unconditionally is fine because:
            //  - the fact whether the class extends something affects parsing of
            //    'super' expressions which cause parse-time SyntaxError if the class
            //    is not a derived one. However, all such errors must have been
            //    reported during initial parse of the class declaration.
            //  - "extends" clause affects class constructor's FunctionKind, but here
            //    we are interested only in the member initializer functions and thus
            //    we can ignore the constructor function details.
            //
            // Given all the above we can simplify things and for the purpose of class
            // member initializers reparsing don't bother propagating the existence of
            // the "extends" clause through scope serialization/deserialization.
            class_info.extends = factory().NewNullLiteral(kNoSourcePosition);

            // Note that we don't recheck class_name for strict-reserved words or eval
            // because all such checks have already been done during initial paring and
            // respective SyntaxErrors must have been thrown if necessary.

            // Class initializers don't care about position of the class token.
            int class_token_pos = kNoSourcePosition;

            // Class members for instance and static fields can be intertwined. For
            // example, in `class { a; static {}; b; static {} }` (where `a` and `b` are
            // instance fields, and `static {}` are static initialization blocks), the
            // initial parse might assign function literal IDs as follows:
            //   - Instance members initializer (for `a` and `b`): ID 1
            //   - Static members initializer (for `static {}` blocks): ID 2
            //
            // When we reparse a specific initializer (e.g., the static members
            // initializer, ID 2), we must ensure that the scope for the "other" kind of
            // initializer (e.g., the instance members initializer, ID 1) is
            // pre-allocated if it lexically precedes the current one.
            //
            // If the instance scope (ID 1) is not pre-allocated:
            // 1. The parser processes the first `static {}` block. It allocates the
            //    static scope with the correct ID (ID 2), as that is the next available
            //    ID after skipping.
            // 2. The parser continues and encounters `b`. Since no instance scope
            //    exists yet, it lazily allocates one.
            // 3. This new instance scope takes the *next* available ID, which is ID 3.
            //    However, ID 3 might not exist (overrunning the range) or might belong
            //    to a subsequent function, causing a shift/mismatch.
            //
            // Pre-allocating the preceding scope (ID 1) ensures it exists before the
            // parser encounters `b`, preventing the incorrect allocation of a new ID.

            if (initializer_kind == FunctionKind.ClassMembersInitializerFunctionPrecededByStatic)
            {
                class_info.EnsureStaticElementsScope(this, kNoSourcePosition, -1);
            }
            else if (initializer_kind == FunctionKind.ClassStaticInitializerFunctionPrecededByMember)
            {
                class_info.EnsureInstanceMembersScope(this, kNoSourcePosition, -1);
            }
            ResetInfoId(initializer_id - 1);

            ParseClassLiteralBody(class_info, class_name, class_token_pos, Token.Eos);

            if (IsClassInstanceInitializerFunction(initializer_kind))
            {
                initializer = CreateInstanceMembersInitializer(class_name, class_info);
            }
            else
            {
                initializer = CreateStaticElementsInitializer(class_name, class_info);
            }
            initializer.scope().TakeUnresolvedReferencesFromParent();
        }

        if (has_error()) return null;

        no_expression_scope.ValidateExpression();

        return initializer;
    }
}
