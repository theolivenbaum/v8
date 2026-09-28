// Copyright 2019 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/expression-scope.h.
//
// The ExpressionScope family is templated on ParserTypes in V8; here the
// classes are nested in ParserBase and share its type parameters. The C++
// destructors are Dispose().

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
{
    // ExpressionScope is used in a stack fashion, and is used to specialize
    // expression parsing for the task at hand. It allows the parser to reuse the
    // same code to parse destructuring declarations, assignment patterns,
    // expressions, and (async) arrow function heads.
    //
    // One of the specific subclasses needs to be instantiated to tell the parser
    // the meaning of the expression it will parse next. The parser then calls
    // Record* on the expression_scope() to indicate errors. The expression_scope
    // will either discard those errors, immediately report those errors, or
    // classify the errors for later validation.
    // TODO(verwaest): Record is a slightly odd name since it will directly throw
    // for unambiguous scopes.
    public abstract class ExpressionScope : IDisposable
    {
        public VariableProxy NewVariable(AstRawString name, int pos = kNoSourcePosition)
        {
            VariableProxy result = parser_.NewRawVariable(name, pos);
            if (CanBeExpression())
            {
                AsExpressionParsingScope().TrackVariable(result);
            }
            else
            {
                Variable var = Declare(name, pos);
                if (IsVarDeclaration())
                {
                    bool passed_through_with = false;
                    for (Scope scope = parser().scope(); !scope.is_declaration_scope(); scope = scope.outer_scope())
                    {
                        if (scope.is_with_scope())
                        {
                            passed_through_with = true;
                        }
                        else if (scope.is_catch_scope())
                        {
                            Variable masking_var = scope.LookupLocal(name);
                            // If a variable is declared in a catch scope with a masking
                            // catch-declared variable, the initializing assignment is an
                            // assignment to the catch-declared variable instead.
                            // https://tc39.es/ecma262/#sec-variablestatements-in-catch-blocks
                            if (masking_var != null)
                            {
                                result.set_is_assigned();
                                if (passed_through_with) break;
                                result.BindTo(masking_var);
                                masking_var.SetMaybeAssigned();
                                return result;
                            }
                        }
                    }
                    if (passed_through_with)
                    {
                        // If a variable is declared in a with scope, the initializing
                        // assignment might target a with-declared variable instead.
                        parser().scope().AddUnresolved(result);
                        return result;
                    }
                }
                // When declaring a parameter, there is no use yet. While there are other
                // cases that this is not true, we need the use marked to ensure variable
                // allocation, whereas parameters are always allocated.
                // TODO(dcarney): expand the scope of the marking.
                VariableProxy.BindingMode binding_mode = type_ == ScopeType.kParameterDeclaration
                    ? VariableProxy.BindingMode.kNoMarkUse
                    : VariableProxy.BindingMode.kMarkUse;
                result.BindTo(var, binding_mode);
            }
            return result;
        }

        public void MergeVariableList(ScopedList<(VariableProxy, int)> variable_list)
        {
            if (!CanBeExpression()) return;
            // Merged variables come from a CanBeDeclaration expression scope, and
            // weren't added as unresolved references to the variable scope yet. Add
            // them to the variable scope on the boundary where it becomes clear they
            // aren't declarations. We explicitly delay declaring the variables up to
            // that point to avoid trying to add them to the unresolved list multiple
            // times, e.g., for (((a))).
            if (!CanBeDeclaration())
            {
                for (int i = 0; i < variable_list.length(); i++)
                {
                    VariableProxy proxy = variable_list.at(i).Item1;
                    parser().scope().AddUnresolved(proxy);
                }
            }
            variable_list.MergeInto(AsExpressionParsingScope().variable_list());
        }

        public Variable Declare(AstRawString name, int pos = kNoSourcePosition)
        {
            if (type_ == ScopeType.kParameterDeclaration)
            {
                return AsParameterDeclarationParsingScope().Declare(name, pos);
            }
            return AsVariableDeclarationParsingScope().Declare(name, pos);
        }

        public void MarkIdentifierAsAssigned()
        {
            if (!CanBeExpression()) return;
            AsExpressionParsingScope().MarkIdentifierAsAssigned();
        }

        public void ValidateAsPattern(TExpression expression, int begin, int end)
        {
            if (!CanBeExpression()) return;
            AsExpressionParsingScope().ValidatePattern(expression, begin, end);
            AsExpressionParsingScope().ClearExpressionError();
        }

        public void ValidateAsExpression()
        {
            if (!CanBeExpression()) return;
            AsExpressionParsingScope().ValidateExpression();
            AsExpressionParsingScope().ClearPatternError();
        }

        // Record async arrow parameters errors in all ambiguous async arrow scopes in
        // the chain up to the first unambiguous scope.
        public void RecordAsyncArrowParametersError(Scanner.Location loc, MessageTemplate message)
        {
            // Only ambiguous scopes (ExpressionParsingScope, *ArrowHeadParsingScope)
            // need to propagate errors to a possible kAsyncArrowHeadParsingScope, so
            // immediately return if the current scope is not ambiguous.
            if (!CanBeExpression()) return;
            AsExpressionParsingScope().RecordAsyncArrowParametersError(loc, message);
        }

        // Record initializer errors in all scopes that can turn into parameter scopes
        // (ArrowHeadParsingScopes) up to the first known unambiguous parameter scope.
        public void RecordParameterInitializerError(Scanner.Location loc, MessageTemplate message)
        {
            ExpressionScope scope = this;
            while (!scope.IsCertainlyParameterDeclaration())
            {
                if (!has_possible_parameter_in_scope_chain_) return;
                if (scope.CanBeParameterDeclaration())
                {
                    scope.AsArrowHeadParsingScope().RecordDeclarationError(loc, message);
                }
                scope = scope.parent();
                if (scope == null) return;
            }
            Report(loc, message);
        }

        public void RecordThisUse()
        {
            ExpressionScope scope = this;
            do
            {
                if (scope.IsArrowHeadParsingScope())
                {
                    scope.AsArrowHeadParsingScope().RecordThisUse();
                }
                scope = scope.parent();
            } while (scope != null);
        }

        public void RecordPatternError(Scanner.Location loc, MessageTemplate message)
        {
            // TODO(verwaest): Non-assigning expression?
            if (IsCertainlyPattern())
            {
                Report(loc, message);
            }
            else
            {
                AsExpressionParsingScope().RecordPatternError(loc, message);
            }
        }

        public void RecordStrictModeParameterError(Scanner.Location loc, MessageTemplate message)
        {
            if (!CanBeParameterDeclaration()) return;
            if (IsCertainlyParameterDeclaration())
            {
                if (is_strict(parser_.language_mode()))
                {
                    Report(loc, message);
                }
                else
                {
                    parser_.parameters_.set_strict_parameter_error(loc, message);
                }
            }
            else
            {
                parser_.next_arrow_function_info_.strict_parameter_error_location = loc;
                parser_.next_arrow_function_info_.strict_parameter_error_message = message;
            }
        }

        public void RecordDeclarationError(Scanner.Location loc, MessageTemplate message)
        {
            if (!CanBeDeclaration()) return;
            if (IsCertainlyDeclaration())
            {
                Report(loc, message);
            }
            else
            {
                AsArrowHeadParsingScope().RecordDeclarationError(loc, message);
            }
        }

        public void RecordExpressionError(Scanner.Location loc, MessageTemplate message)
        {
            if (!CanBeExpression()) return;
            // TODO(verwaest): Non-assigning expression?
            // if (IsCertainlyExpression()) Report(loc, message);
            AsExpressionParsingScope().RecordExpressionError(loc, message);
        }

        public void RecordNonSimpleParameter()
        {
            if (!IsArrowHeadParsingScope()) return;
            AsArrowHeadParsingScope().RecordNonSimpleParameter();
        }

        public bool IsCertainlyDeclaration()
            => type_ is >= ScopeType.kParameterDeclaration and <= ScopeType.kLexicalDeclaration;

        public int SetInitializers(int variable_index, int peek_position)
        {
            if (CanBeExpression())
            {
                return AsExpressionParsingScope().SetInitializers(variable_index, peek_position);
            }
            return variable_index;
        }

        public bool has_possible_arrow_parameter_in_scope_chain() => has_possible_arrow_parameter_in_scope_chain_;

        public enum ScopeType : byte
        {
            // Expression or assignment target.
            kExpression,

            // Declaration or expression or assignment target.
            kMaybeArrowParameterDeclaration,
            kMaybeAsyncArrowParameterDeclaration,

            // Declarations.
            kParameterDeclaration,
            kVarDeclaration,
            kLexicalDeclaration,
        }

        protected TImpl parser() => parser_;
        protected internal ExpressionScope parent() => parent_;

        protected void Report(Scanner.Location loc, MessageTemplate message) => parser_.ReportMessageAt(loc, message);

        protected ExpressionScope(TImpl parser, ScopeType type)
        {
            parser_ = parser;
            parent_ = parser.expression_scope_;
            type_ = type;
            has_possible_parameter_in_scope_chain_ =
                CanBeParameterDeclaration() || (parent_ != null && parent_.has_possible_parameter_in_scope_chain_);
            has_possible_arrow_parameter_in_scope_chain_ =
                CanBeArrowParameterDeclaration() ||
                (parent_ != null && parent_.has_possible_arrow_parameter_in_scope_chain_);
            parser.expression_scope_ = this;
        }

        public virtual void Dispose()
        {
            parser_.expression_scope_ = parent_;
        }

        protected internal ExpressionParsingScope AsExpressionParsingScope() => (ExpressionParsingScope)this;

        protected internal bool CanBeExpression()
            => type_ is >= ScopeType.kExpression and <= ScopeType.kMaybeAsyncArrowParameterDeclaration;

        protected internal bool CanBeDeclaration()
            => type_ is >= ScopeType.kMaybeArrowParameterDeclaration and <= ScopeType.kLexicalDeclaration;

        protected bool IsVariableDeclaration()
            => type_ is >= ScopeType.kVarDeclaration and <= ScopeType.kLexicalDeclaration;

        protected bool IsLexicalDeclaration() => type_ == ScopeType.kLexicalDeclaration;
        protected bool IsAsyncArrowHeadParsingScope() => type_ == ScopeType.kMaybeAsyncArrowParameterDeclaration;
        protected bool IsVarDeclaration() => type_ == ScopeType.kVarDeclaration;

        protected internal ArrowHeadParsingScope AsArrowHeadParsingScope() => (ArrowHeadParsingScope)this;

        private ParameterDeclarationParsingScope AsParameterDeclarationParsingScope()
            => (ParameterDeclarationParsingScope)this;

        private VariableDeclarationParsingScope AsVariableDeclarationParsingScope()
            => (VariableDeclarationParsingScope)this;

        private bool IsArrowHeadParsingScope()
            => type_ is >= ScopeType.kMaybeArrowParameterDeclaration
                and <= ScopeType.kMaybeAsyncArrowParameterDeclaration;

        private bool IsCertainlyPattern() => IsCertainlyDeclaration();

        private bool CanBeParameterDeclaration()
            => type_ is >= ScopeType.kMaybeArrowParameterDeclaration and <= ScopeType.kParameterDeclaration;

        private bool CanBeArrowParameterDeclaration()
            => type_ is >= ScopeType.kMaybeArrowParameterDeclaration
                and <= ScopeType.kMaybeAsyncArrowParameterDeclaration;

        private bool IsCertainlyParameterDeclaration() => type_ == ScopeType.kParameterDeclaration;

        private readonly TImpl parser_;
        private readonly ExpressionScope parent_;
        protected internal readonly ScopeType type_;
        private readonly bool has_possible_parameter_in_scope_chain_;
        private readonly bool has_possible_arrow_parameter_in_scope_chain_;
    }

    // Used to unambiguously parse var, let, const declarations.
    public sealed class VariableDeclarationParsingScope : ExpressionScope
    {
        public VariableDeclarationParsingScope(TImpl parser, VariableMode mode, List<AstRawString> names)
            : base(parser, IsLexicalVariableMode(mode) ? ScopeType.kLexicalDeclaration : ScopeType.kVarDeclaration)
        {
            mode_ = mode;
            names_ = names;
            scope_ = parser.scope();
        }

        public new Variable Declare(AstRawString name, int pos)
        {
            VariableKind kind = VariableKind.NORMAL_VARIABLE;
            bool was_added;
            Variable var = parser().DeclareVariable(name, kind, mode_, Variable.DefaultInitializationFlag(mode_),
                                                    scope_, out was_added, pos);
            if (was_added && scope_.num_var() > kMaxNumFunctionLocals)
            {
                parser().ReportMessage(MessageTemplate.TooManyVariables);
            }
            names_?.Add(name);
            if (IsLexicalDeclaration())
            {
                if (parser().IsLet(name))
                {
                    parser().ReportMessageAt(new Scanner.Location(pos, pos + name.length()),
                                             MessageTemplate.LetInLexicalBinding);
                }
            }
            else
            {
                if (parser().loop_nesting_depth() > 0)
                {
                    // Due to hoisting, the value of a 'var'-declared variable may actually
                    // change even if the code contains only the "initial" assignment,
                    // namely when that assignment occurs inside a loop.  For example:
                    //
                    //   let i = 10;
                    //   do { var x = i } while (i--):
                    //
                    // Note that non-lexical variables include temporaries, which may also
                    // get assigned inside a loop due to the various rewritings that the
                    // parser performs.
                    //
                    // Pessimistically mark all vars in loops as assigned. This
                    // overapproximates the actual assigned vars due to unassigned var
                    // without initializer, but that's unlikely anyway.
                    //
                    // This also handles marking of loop variables in for-in and for-of
                    // loops, as determined by loop-nesting-depth.
                    var.SetMaybeAssigned();
                }
            }
            return var;
        }

        // Limit the allowed number of local variables in a function. The hard limit
        // in Ignition is 2^31-1 due to the size of register operands. We limit it to
        // a more reasonable lower up-limit.
        private const int kMaxNumFunctionLocals = (1 << 23) - 1;

        private readonly VariableMode mode_;
        private readonly List<AstRawString> names_;
        private readonly Scope scope_;
    }

    public sealed class ParameterDeclarationParsingScope(TImpl parser)
        : ExpressionScope(parser, ScopeType.kParameterDeclaration)
    {
        public new Variable Declare(AstRawString name, int pos)
        {
            VariableKind kind = VariableKind.PARAMETER_VARIABLE;
            VariableMode mode = VariableMode.Var;
            bool was_added;
            Variable var = parser().DeclareVariable(name, kind, mode, Variable.DefaultInitializationFlag(mode),
                                                    parser().scope(), out was_added, pos);
            if (!has_duplicate() && !was_added)
            {
                duplicate_loc_ = new Scanner.Location(pos, pos + name.length());
            }
            return var;
        }

        public bool has_duplicate() => duplicate_loc_.IsValid();

        public Scanner.Location duplicate_location() => duplicate_loc_;

        private Scanner.Location duplicate_loc_ = Scanner.Location.invalid();
    }

    // Parsing expressions is always ambiguous between at least left-hand-side and
    // right-hand-side of assignments. This class is used to keep track of errors
    // relevant for either side until it is clear what was being parsed.
    // The class also keeps track of all variable proxies that are created while the
    // scope was active. If the scope is an expression, the variable proxies will be
    // added to the unresolved list. Otherwise they are declarations and aren't
    // added. The list is also used to mark the variables as assigned in case we are
    // parsing an assignment expression.
    public class ExpressionParsingScope : ExpressionScope
    {
        public ExpressionParsingScope(TImpl parser, ScopeType type = ScopeType.kExpression)
            : base(parser, type)
        {
            variable_list_ = new ScopedList<(VariableProxy, int)>(parser.variable_buffer());
            has_async_arrow_in_scope_chain_ =
                type == ScopeType.kMaybeAsyncArrowParameterDeclaration ||
                (parent() != null && parent().CanBeExpression() &&
                 parent().AsExpressionParsingScope().has_async_arrow_in_scope_chain_);
            clear(kExpressionIndex);
            clear(kPatternIndex);
        }

        public new void RecordAsyncArrowParametersError(Scanner.Location loc, MessageTemplate message)
        {
            for (ExpressionScope scope = this; scope != null; scope = scope.parent())
            {
                if (!has_async_arrow_in_scope_chain_) break;
                if (scope.type_ == ScopeType.kMaybeAsyncArrowParameterDeclaration)
                {
                    scope.AsArrowHeadParsingScope().RecordDeclarationError(loc, message);
                }
            }
        }

        public override void Dispose()
        {
            variable_list_.Dispose();
            base.Dispose();
        }

        public TExpression ValidateAndRewriteReference(TExpression expression, int beg_pos, int end_pos)
        {
            if (parser().IsAssignableIdentifier(expression))
            {
                MarkIdentifierAsAssigned();
                mark_verified();
                return expression;
            }
            else if (expression.IsProperty())
            {
                ValidateExpression();
                return expression;
            }
            mark_verified();
            const bool early_error = false;
            return parser().RewriteInvalidReferenceExpression(expression, beg_pos, end_pos,
                                                              MessageTemplate.InvalidLhsInFor, early_error);
        }

        public new void RecordExpressionError(Scanner.Location loc, MessageTemplate message)
            => Record(kExpressionIndex, loc, message);

        public new void RecordPatternError(Scanner.Location loc, MessageTemplate message)
            => Record(kPatternIndex, loc, message);

        public void ValidateExpression() => Validate(kExpressionIndex);

        public void ValidatePattern(TExpression expression, int begin, int end)
        {
            Validate(kPatternIndex);
            if (expression.is_parenthesized())
            {
                Report(new Scanner.Location(begin, end), MessageTemplate.InvalidDestructuringTarget);
            }
            for (int i = 0; i < variable_list_.length(); i++)
            {
                variable_list_.at(i).Item1.set_is_assigned();
            }
        }

        public void ClearExpressionError() => clear(kExpressionIndex);

        public void ClearPatternError() => clear(kPatternIndex);

        public void TrackVariable(VariableProxy variable)
        {
            if (!CanBeDeclaration())
            {
                parser().scope().AddUnresolved(variable);
            }
            variable_list_.Add((variable, kNoSourcePosition));
        }

        public new void MarkIdentifierAsAssigned()
        {
            // It's possible we're parsing a syntax error. In that case it's not
            // guaranteed that there's a variable in the list.
            if (variable_list_.length() == 0) return;
            variable_list_.at(variable_list_.length() - 1).Item1.set_is_assigned();
        }

        public new int SetInitializers(int first_variable_index, int position)
        {
            int len = variable_list_.length();
            if (len == 0) return 0;

            int end = len - 1;
            // Loop backwards and abort as soon as we see one that's already set to
            // avoid a loop on expressions like a,b,c,d,e,f,g (outside of an arrowhead).
            // TODO(delphick): Look into removing this loop.
            for (int i = end; i >= first_variable_index && variable_list_.at(i).Item2 == kNoSourcePosition; --i)
            {
                variable_list_.at(i).Item2 = position;
            }
            return end;
        }

        public ScopedList<(VariableProxy, int)> variable_list() => variable_list_;

        protected bool is_verified() => false;

        protected void ValidatePattern() => Validate(kPatternIndex);

        internal const int kExpressionIndex = 0;
        internal const int kPatternIndex = 1;
        internal const int kNumberOfErrors = 2;

        internal void clear(int index)
        {
            messages_[index] = MessageTemplate.None;
            locations_[index] = Scanner.Location.invalid();
        }

        private bool is_valid(int index) => !locations_[index].IsValid();

        private void Record(int index, Scanner.Location loc, MessageTemplate message)
        {
            if (!is_valid(index)) return;
            messages_[index] = message;
            locations_[index] = loc;
        }

        private void Validate(int index)
        {
            if (!is_valid(index)) Report(index);
            mark_verified();
        }

        private void Report(int index) => Report(locations_[index], messages_[index]);

        // Debug verification to make sure every scope is validated exactly once.
        private static void mark_verified() { }

        private readonly ScopedList<(VariableProxy, int)> variable_list_;
        internal readonly MessageTemplate[] messages_ = new MessageTemplate[kNumberOfErrors];
        internal readonly Scanner.Location[] locations_ = new Scanner.Location[kNumberOfErrors];
        private readonly bool has_async_arrow_in_scope_chain_;
    }

    // This class is used to parse multiple ambiguous expressions and declarations
    // in the same scope. E.g., in async(X,Y,Z) or [X,Y,Z], X and Y and Z will all
    // be parsed in the respective outer ArrowHeadParsingScope and
    // ExpressionParsingScope. It provides a clean error state in the underlying
    // scope to parse the individual expressions, while keeping track of the
    // expression and pattern errors since the start. The AccumulationScope is only
    // used to keep track of the errors so far, and the underlying ExpressionScope
    // keeps being used as the expression_scope(). If the expression_scope() isn't
    // ambiguous, this class does not do anything.
    public sealed class AccumulationScope : IDisposable
    {
        private const int kNumberOfErrors = ExpressionParsingScope.kNumberOfErrors;

        public AccumulationScope(ExpressionScope scope)
        {
            scope_ = null;
            if (!scope.CanBeExpression()) return;
            scope_ = scope.AsExpressionParsingScope();
            for (int i = 0; i < kNumberOfErrors; i++)
            {
                copy(i);
                scope_.clear(i);
            }
        }

        // Merge errors from the underlying ExpressionParsingScope into this scope.
        // Only keeps the first error across all accumulate calls, and removes the
        // error from the underlying scope.
        public void Accumulate()
        {
            if (scope_ == null) return;
            for (int i = 0; i < kNumberOfErrors; i++)
            {
                if (!locations_[i].IsValid()) copy(i);
                scope_.clear(i);
            }
        }

        // This is called instead of Accumulate in case the parsed member is already
        // known to be an expression. In that case we don't need to accumulate the
        // expression but rather validate it immediately. We also ignore the pattern
        // error since the parsed member is known to not be a pattern. This is
        // necessary for "{x:1}.y" parsed as part of an assignment pattern. {x:1} will
        // record a pattern error, but "{x:1}.y" is actually a valid as part of an
        // assignment pattern since it's a property access.
        public void ValidateExpression()
        {
            if (scope_ == null) return;
            scope_.ValidateExpression();
            scope_.clear(ExpressionParsingScope.kPatternIndex);
        }

        public void Dispose()
        {
            if (scope_ == null) return;
            Accumulate();
            for (int i = 0; i < kNumberOfErrors; i++) copy_back(i);
        }

        private void copy(int entry)
        {
            messages_[entry] = scope_.messages_[entry];
            locations_[entry] = scope_.locations_[entry];
        }

        private void copy_back(int entry)
        {
            if (!locations_[entry].IsValid()) return;
            scope_.messages_[entry] = messages_[entry];
            scope_.locations_[entry] = locations_[entry];
        }

        private readonly ExpressionParsingScope scope_;
        private readonly MessageTemplate[] messages_ = new MessageTemplate[2];
        private readonly Scanner.Location[] locations_ = new Scanner.Location[2];
    }

    // The head of an arrow function is ambiguous between expression, assignment
    // pattern and declaration. This keeps track of the additional declaration
    // error and allows the scope to be validated as a declaration rather than an
    // expression or a pattern.
    public sealed class ArrowHeadParsingScope : ExpressionParsingScope
    {
        public ArrowHeadParsingScope(TImpl parser, FunctionKind kind, int function_literal_id)
            : base(parser, kind == FunctionKind.ArrowFunction
                ? ScopeType.kMaybeArrowParameterDeclaration
                : ScopeType.kMaybeAsyncArrowParameterDeclaration)
        {
            function_literal_id_ = function_literal_id;
            allow_reindex_scope_ = new AllowReindexScope(parser.max_drift_);
            // clear last next_arrow_function_info tracked strict parameters error.
            parser.next_arrow_function_info_.ClearStrictParameterError();
        }

        public override void Dispose()
        {
            allow_reindex_scope_.Dispose();
            base.Dispose();
        }

        public new void ValidateExpression()
        {
            // Turns out this is not an arrow head. Clear any possible tracked strict
            // parameter errors, and reinterpret tracked variables as unresolved
            // references.
            parser().next_arrow_function_info_.ClearStrictParameterError();
            base.ValidateExpression();
            parent().MergeVariableList(variable_list());
        }

        public DeclarationScope ValidateAndCreateScope()
        {
            DeclarationScope result = parser().NewFunctionScope(kind());
            if (declaration_error_location.IsValid())
            {
                Report(declaration_error_location, declaration_error_message);
                return result;
            }
            ValidatePattern();

            if (!has_simple_parameter_list_) result.SetHasNonSimpleParameters();
            VariableKind kind_ = VariableKind.PARAMETER_VARIABLE;
            VariableMode mode = has_simple_parameter_list_ ? VariableMode.Var : VariableMode.Let;
            ScopedList<(VariableProxy, int)> list = variable_list();
            for (int i = 0; i < list.length(); i++)
            {
                VariableProxy proxy = list.at(i).Item1;
                int initializer_position = list.at(i).Item2;
                // Default values for parameters will have been parsed as assignments so
                // clear the is_assigned bit as they are not actually assignments.
                proxy.clear_is_assigned();
                bool was_added;
                // Simple parameters will not have a use on bind.
                VariableProxy.BindingMode binding_mode = has_simple_parameter_list_
                    ? VariableProxy.BindingMode.kNoMarkUse
                    : VariableProxy.BindingMode.kMarkUse;
                parser().DeclareAndBindVariable(proxy, kind_, mode, result, out was_added, initializer_position,
                                                binding_mode);
                if (!was_added)
                {
                    Report(proxy.location(), MessageTemplate.ParamDupe);
                }
            }

            if (uses_this_) result.UsesThis();
            return result;
        }

        public void RecordDeclarationError(Scanner.Location loc, MessageTemplate message)
        {
            declaration_error_location = loc;
            declaration_error_message = message;
        }

        public void RecordNonSimpleParameter() => has_simple_parameter_list_ = false;
        public void RecordThisUse() => uses_this_ = true;
        public int function_literal_id() => function_literal_id_;

        private FunctionKind kind()
            => IsAsyncArrowHeadParsingScope() ? FunctionKind.AsyncArrowFunction : FunctionKind.ArrowFunction;

        private Scanner.Location declaration_error_location = Scanner.Location.invalid();
        private MessageTemplate declaration_error_message = MessageTemplate.None;
        private readonly int function_literal_id_;
        private bool has_simple_parameter_list_ = true;
        private bool uses_this_;
        private readonly AllowReindexScope allow_reindex_scope_;
    }
}
