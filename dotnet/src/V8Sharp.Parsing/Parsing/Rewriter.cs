// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/rewriter.h and rewriter.cc.

#nullable disable

using V8Sharp.Ast;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

internal sealed class Processor : AstVisitor
{
    public Processor(DeclarationScope closure_scope, Variable result, AstValueFactory ast_value_factory)
    {
        result_ = result;
        replacement_ = null;
        closure_scope_ = closure_scope;
        factory_ = new AstNodeFactory(ast_value_factory);
        result_assigned_ = false;
        is_set_ = false;
        breakable_ = false;
    }

    public void Process(List<Statement> statements)
    {
        // If we're in a breakable scope (named block, iteration, or switch), we walk
        // all statements. The last value producing statement before the break needs
        // to assign to .result. If we're not in a breakable scope, only the last
        // value producing statement in the block assigns to .result, so we can stop
        // early.
        for (int i = statements.Count - 1; i >= 0 && (breakable_ || !is_set_); --i)
        {
            Visit(statements[i]);
            if (CheckStackOverflow()) return;
            statements[i] = replacement_;
        }
    }

    public bool result_assigned() => result_assigned_;

    public DeclarationScope closure_scope() => closure_scope_;
    public AstNodeFactory factory() => factory_;

    // Returns ".result = value"
    public Expression SetResult(Expression value)
    {
        result_assigned_ = true;
        VariableProxy result_proxy = factory().NewVariableProxy(result_);
        return factory().NewAssignment(Token.Assign, result_proxy, value, kNoSourcePosition);
    }

    // Inserts '.result = undefined' in front of the given statement.
    public Statement AssignUndefinedBefore(Statement s)
    {
        Expression undef = factory().NewUndefinedLiteral(kNoSourcePosition);
        Expression assignment = SetResult(undef);
        Block b = factory().NewBlock(2, false);
        b.statements().Add(factory().NewExpressionStatement(assignment, kNoSourcePosition));
        b.statements().Add(s);
        return b;
    }

    private readonly Variable result_;

    // When visiting a node, we "return" a replacement for that node in
    // [replacement_].  In many cases this will just be the original node.
    private Statement replacement_;

    private readonly struct BreakableScope : IDisposable
    {
        private readonly Processor _processor;
        private readonly bool _previous;

        public BreakableScope(Processor processor, bool breakable = true)
        {
            _processor = processor;
            _previous = processor.breakable_;
            processor.breakable_ = processor.breakable_ || breakable;
        }

        public void Dispose() => _processor.breakable_ = _previous;
    }

    private readonly DeclarationScope closure_scope_;
    private readonly AstNodeFactory factory_;

    // We are not tracking result usage via the result_'s use
    // counts (we leave the accurate computation to the
    // usage analyzer). Instead we simple remember if
    // there was ever an assignment to result_.
    private bool result_assigned_;

    // To avoid storing to .result all the time, we eliminate some of
    // the stores by keeping track of whether or not we're sure .result
    // will be overwritten anyway. This is a bit more tricky than what I
    // was hoping for.
    private bool is_set_;

    private bool breakable_;

    public override void VisitBlock(Block node)
    {
        // An initializer block is the rewritten form of a variable declaration
        // with initialization expressions. The initializer block contains the
        // list of assignments corresponding to the initialization expressions.
        // While unclear from the spec (ECMA-262, 3rd., 12.2), the value of
        // a variable declaration with initialization expression is 'undefined'
        // with some JS VMs: For instance, using smjs, print(eval('var x = 7'))
        // returns 'undefined'. To obtain the same behavior with v8, we need
        // to prevent rewriting in that case.
        if (!node.ignore_completion_value())
        {
            using BreakableScope scope = new(this, node.is_breakable());
            Process(node.statements());
            if (CheckStackOverflow()) return;
        }
        replacement_ = node;
    }

    public override void VisitExpressionStatement(ExpressionStatement node)
    {
        // Rewrite : <x>; -> .result = <x>;
        if (!is_set_)
        {
            node.set_expression(SetResult(node.expression()));
            is_set_ = true;
        }
        replacement_ = node;
    }

    public override void VisitIfStatement(IfStatement node)
    {
        // Rewrite both branches.
        bool set_after = is_set_;

        Visit(node.then_statement());
        if (CheckStackOverflow()) return;
        node.set_then_statement(replacement_);
        bool set_in_then = is_set_;

        is_set_ = set_after;
        Visit(node.else_statement());
        if (CheckStackOverflow()) return;
        node.set_else_statement(replacement_);

        replacement_ = set_in_then && is_set_ ? node : AssignUndefinedBefore(node);
        is_set_ = true;
    }

    private void VisitIterationStatement(IterationStatement node)
    {
        // The statement may have to produce a value, so always assign undefined
        // before.
        // TODO(verwaest): Omit it if we know that there's no break/continue leaving
        // it early.
        using BreakableScope scope = new(this);

        Visit(node.body());
        if (CheckStackOverflow()) return;
        node.set_body(replacement_);

        replacement_ = AssignUndefinedBefore(node);
        is_set_ = true;
    }

    public override void VisitDoWhileStatement(DoWhileStatement node) => VisitIterationStatement(node);
    public override void VisitWhileStatement(WhileStatement node) => VisitIterationStatement(node);
    public override void VisitForStatement(ForStatement node) => VisitIterationStatement(node);
    public override void VisitForInStatement(ForInStatement node) => VisitIterationStatement(node);
    public override void VisitForOfStatement(ForOfStatement node) => VisitIterationStatement(node);

    public override void VisitTryCatchStatement(TryCatchStatement node)
    {
        // Rewrite both try and catch block.
        bool set_after = is_set_;

        Visit(node.try_block());
        if (CheckStackOverflow()) return;
        node.set_try_block((Block)replacement_);
        bool set_in_try = is_set_;

        is_set_ = set_after;
        Visit(node.catch_block());
        if (CheckStackOverflow()) return;
        node.set_catch_block((Block)replacement_);

        replacement_ = is_set_ && set_in_try ? node : AssignUndefinedBefore(node);
        is_set_ = true;
    }

    public override void VisitTryFinallyStatement(TryFinallyStatement node)
    {
        // Only rewrite finally if it could contain 'break' or 'continue'. Always
        // rewrite try.
        if (breakable_)
        {
            // Only set result before a 'break' or 'continue'.
            is_set_ = true;
            Visit(node.finally_block());
            if (CheckStackOverflow()) return;
            node.set_finally_block((Block)replacement_);
            if (is_set_)
            {
                // Save .result value at the beginning of the finally block and restore it
                // at the end again: ".backup = .result; ...; .result = .backup" This is
                // necessary because the finally block does not normally contribute to the
                // completion value.
                Variable backup = closure_scope().NewTemporary(factory().ast_value_factory().dot_result_string());
                Expression backup_proxy = factory().NewVariableProxy(backup);
                Expression result_proxy = factory().NewVariableProxy(result_);
                Expression save = factory().NewAssignment(Token.Assign, backup_proxy, result_proxy,
                                                          kNoSourcePosition);
                Expression restore = factory().NewAssignment(Token.Assign, result_proxy, backup_proxy,
                                                             kNoSourcePosition);
                node.finally_block().statements().Insert(
                    0, factory().NewExpressionStatement(save, kNoSourcePosition));
                node.finally_block().statements().Add(factory().NewExpressionStatement(restore, kNoSourcePosition));
            }
            else
            {
                // If is_set_ is false, it means the finally block has a 'break' or a
                // 'continue' and was not preceded by a statement that assigned to
                // .result. Try-finally statements return the abrupt completions from the
                // finally block, meaning this case should get an undefined.
                //
                // Since the finally block will definitely result in an abrupt completion,
                // there's no need to save and restore the .result.
                Expression undef = factory().NewUndefinedLiteral(kNoSourcePosition);
                Expression assignment = SetResult(undef);
                node.finally_block().statements().Insert(
                    0, factory().NewExpressionStatement(assignment, kNoSourcePosition));
            }
            // We can't tell whether the finally-block is guaranteed to set .result, so
            // reset is_set_ before visiting the try-block.
            is_set_ = false;
        }
        Visit(node.try_block());
        if (CheckStackOverflow()) return;
        node.set_try_block((Block)replacement_);

        replacement_ = is_set_ ? node : AssignUndefinedBefore(node);
        is_set_ = true;
    }

    public override void VisitSwitchStatement(SwitchStatement node)
    {
        // The statement may have to produce a value, so always assign undefined
        // before.
        // TODO(verwaest): Omit it if we know that there's no break/continue leaving
        // it early.
        using BreakableScope scope = new(this);
        // Rewrite statements in all case clauses.
        List<CaseClause> clauses = node.cases();
        for (int i = clauses.Count - 1; i >= 0; --i)
        {
            CaseClause clause = clauses[i];
            Process(clause.statements());
            if (CheckStackOverflow()) return;
        }

        replacement_ = AssignUndefinedBefore(node);
        is_set_ = true;
    }

    public override void VisitContinueStatement(ContinueStatement node)
    {
        is_set_ = false;
        replacement_ = node;
    }

    public override void VisitBreakStatement(BreakStatement node)
    {
        is_set_ = false;
        replacement_ = node;
    }

    public override void VisitWithStatement(WithStatement node)
    {
        Visit(node.statement());
        if (CheckStackOverflow()) return;
        node.set_statement(replacement_);

        replacement_ = is_set_ ? node : AssignUndefinedBefore(node);
        is_set_ = true;
    }

    public override void VisitSloppyBlockFunctionStatement(SloppyBlockFunctionStatement node)
    {
        Visit(node.statement());
        if (CheckStackOverflow()) return;
        node.set_statement(replacement_);
        replacement_ = node;
    }

    public override void VisitEmptyStatement(EmptyStatement node) => replacement_ = node;

    public override void VisitReturnStatement(ReturnStatement node)
    {
        is_set_ = true;
        replacement_ = node;
    }

    public override void VisitDebuggerStatement(DebuggerStatement node) => replacement_ = node;

    public override void VisitInitializeClassMembersStatement(InitializeClassMembersStatement node)
        => replacement_ = node;

    public override void VisitInitializeClassStaticElementsStatement(InitializeClassStaticElementsStatement node)
        => replacement_ = node;

    public override void VisitAutoAccessorGetterBody(AutoAccessorGetterBody node) => replacement_ = node;

    public override void VisitAutoAccessorSetterBody(AutoAccessorSetterBody node) => replacement_ = node;

    // Expressions are never visited.
    private static void Unreachable() => throw new InvalidOperationException("UNREACHABLE");

    public override void VisitRegExpLiteral(RegExpLiteral node) => Unreachable();
    public override void VisitObjectLiteral(ObjectLiteral node) => Unreachable();
    public override void VisitArrayLiteral(ArrayLiteral node) => Unreachable();
    public override void VisitAssignment(Assignment node) => Unreachable();
    public override void VisitAwait(Await node) => Unreachable();
    public override void VisitBinaryOperation(BinaryOperation node) => Unreachable();
    public override void VisitNaryOperation(NaryOperation node) => Unreachable();
    public override void VisitCall(Call node) => Unreachable();
    public override void VisitSuperCallForwardArgs(SuperCallForwardArgs node) => Unreachable();
    public override void VisitCallNew(CallNew node) => Unreachable();
    public override void VisitCallRuntime(CallRuntime node) => Unreachable();
    public override void VisitClassLiteral(ClassLiteral node) => Unreachable();
    public override void VisitCompareOperation(CompareOperation node) => Unreachable();
    public override void VisitCompoundAssignment(CompoundAssignment node) => Unreachable();
    public override void VisitConditionalChain(ConditionalChain node) => Unreachable();
    public override void VisitConditional(Conditional node) => Unreachable();
    public override void VisitCountOperation(CountOperation node) => Unreachable();
    public override void VisitEmptyParentheses(EmptyParentheses node) => Unreachable();
    public override void VisitFunctionLiteral(FunctionLiteral node) => Unreachable();
    public override void VisitGetTemplateObject(GetTemplateObject node) => Unreachable();
    public override void VisitImportCallExpression(ImportCallExpression node) => Unreachable();
    public override void VisitLiteral(Literal node) => Unreachable();
    public override void VisitNativeFunctionLiteral(NativeFunctionLiteral node) => Unreachable();
    public override void VisitOptionalChain(OptionalChain node) => Unreachable();
    public override void VisitProperty(Property node) => Unreachable();
    public override void VisitSpread(Spread node) => Unreachable();
    public override void VisitSuperCallReference(SuperCallReference node) => Unreachable();
    public override void VisitSuperPropertyReference(SuperPropertyReference node) => Unreachable();
    public override void VisitTemplateLiteral(TemplateLiteral node) => Unreachable();
    public override void VisitThisExpression(ThisExpression node) => Unreachable();
    public override void VisitThrow(Throw node) => Unreachable();
    public override void VisitUnaryOperation(UnaryOperation node) => Unreachable();
    public override void VisitVariableProxy(VariableProxy node) => Unreachable();
    public override void VisitYield(Yield node) => Unreachable();
    public override void VisitYieldStar(YieldStar node) => Unreachable();

    // Declarations are never visited.
    public override void VisitVariableDeclaration(VariableDeclaration node) => Unreachable();
    public override void VisitFunctionDeclaration(FunctionDeclaration node) => Unreachable();
}

public static class Rewriter
{
    // Rewrite top-level code (ECMA 262 "programs") so as to conservatively
    // include an assignment of the value of the last statement in the code to
    // a compiler-generated temporary variable wherever needed.
    //
    // Assumes code has been parsed and scopes have been analyzed.  Mutates the
    // AST, so the AST should not continue to be used in the case of failure.
    public static bool Rewrite(ParseInfo info, out bool out_has_stack_overflow)
    {
        out_has_stack_overflow = false;
        FunctionLiteral function = info.literal();
        Scope scope = function.scope();

        if (scope.is_repl_mode_scope() || !(scope.is_script_scope() || scope.is_eval_scope()))
        {
            return true;
        }

        List<Statement> body = function.body();
        return RewriteBody(info, scope, body, out out_has_stack_overflow, out _);
    }

    // Helper that does the actual re-writing. Extracted so REPL scripts can
    // rewrite the body but then use the ".result" VariableProxy to resolve
    // the async promise that is the result of running a REPL script.
    // Returns false (V8: std::nullopt) in case something went wrong; result is
    // the ".result" proxy or null.
    public static bool RewriteBody(ParseInfo info, Scope scope, List<Statement> body, out bool out_has_stack_overflow,
                                   out VariableProxy result_value)
    {
        out_has_stack_overflow = false;
        result_value = null;
        if (body.Count != 0)
        {
            Variable result = scope.AsDeclarationScope().NewTemporary(info.ast_value_factory().dot_result_string());
            Processor processor = new(scope.AsDeclarationScope(), result, info.ast_value_factory());
            processor.Process(body);

            if (processor.HasStackOverflow())
            {
                out_has_stack_overflow = true;
                return false;
            }

            if (processor.result_assigned())
            {
                int pos = kNoSourcePosition;
                result_value = processor.factory().NewVariableProxy(result, pos);
                if (!info.flags().is_repl_mode())
                {
                    Statement result_statement = processor.factory().NewReturnStatement(result_value, pos);
                    body.Add(result_statement);
                }
                return true;
            }
        }
        return true;
    }
}
