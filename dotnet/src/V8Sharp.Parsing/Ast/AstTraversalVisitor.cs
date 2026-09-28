// Copyright 2016 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/ast-traversal-visitor.h.
//
// The traversal visitor:
// - fully traverses the entire AST.
//
// It invokes VisitNode on each AST node, before proceeding with its subtrees.
// It invokes VisitExpression (after VisitNode) on each AST node that is an
// expression, before proceeding with its subtrees.
// It proceeds with the subtrees only if these two methods return true.
// Sub-classes may override VisitNode and VisitExpressions, whose implementation
// is dummy here.  Or they may override the specific Visit* methods.

namespace V8Sharp.Ast;

public abstract class AstTraversalVisitor(AstNode? root = null) : AstVisitor
{
    private readonly AstNode? _root = root;
    private int _depth;

    public void Run() => Visit(_root!);

    protected virtual bool VisitNode(AstNode node) => true;
    protected virtual bool VisitExpression(Expression node) => true;

    protected int depth() => _depth;

    // Iteration left-to-right.
    public new void VisitDeclarations(ThreadedList<Declaration> decls)
    {
        foreach (Declaration decl in decls)
        {
            Visit(decl);
            if (HasStackOverflow()) return;
        }
    }

    public new void VisitStatements(List<Statement> stmts)
    {
        for (int i = 0; i < stmts.Count; ++i)
        {
            Statement stmt = stmts[i];
            Visit(stmt);
            if (HasStackOverflow()) return;
        }
    }

    // RECURSE_EXPRESSION: visit with depth accounting.
    private bool RecurseExpression(AstNode node)
    {
        ++_depth;
        Visit(node);
        --_depth;
        return !HasStackOverflow();
    }

    private bool Recurse(AstNode node)
    {
        Visit(node);
        return !HasStackOverflow();
    }

    private bool ProcessExpression(Expression node) => VisitNode(node) && VisitExpression(node);

    public override void VisitVariableDeclaration(VariableDeclaration decl)
    {
        if (!VisitNode(decl)) return;
    }

    public override void VisitFunctionDeclaration(FunctionDeclaration decl)
    {
        if (!VisitNode(decl)) return;
        Recurse(decl.fun());
    }

    public override void VisitBlock(Block stmt)
    {
        if (!VisitNode(stmt)) return;
        if (stmt.scope() != null)
        {
            ++_depth;
            VisitDeclarations(stmt.scope()!.declarations());
            --_depth;
            if (HasStackOverflow()) return;
        }
        VisitStatements(stmt.statements());
    }

    public override void VisitExpressionStatement(ExpressionStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        Recurse(stmt.expression());
    }

    public override void VisitEmptyStatement(EmptyStatement stmt) { }

    public override void VisitSloppyBlockFunctionStatement(SloppyBlockFunctionStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        Recurse(stmt.statement());
    }

    public override void VisitIfStatement(IfStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.condition())) return;
        if (!Recurse(stmt.then_statement())) return;
        Recurse(stmt.else_statement());
    }

    public override void VisitContinueStatement(ContinueStatement stmt)
    {
        if (!VisitNode(stmt)) return;
    }

    public override void VisitBreakStatement(BreakStatement stmt)
    {
        if (!VisitNode(stmt)) return;
    }

    public override void VisitReturnStatement(ReturnStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        Recurse(stmt.expression());
    }

    public override void VisitWithStatement(WithStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.expression())) return;
        Recurse(stmt.statement());
    }

    public override void VisitSwitchStatement(SwitchStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.tag())) return;
        List<CaseClause> clauses = stmt.cases();
        for (int i = 0; i < clauses.Count; ++i)
        {
            CaseClause clause = clauses[i];
            if (!clause.is_default())
            {
                Expression label = clause.label();
                if (!Recurse(label)) return;
            }
            List<Statement> stmts = clause.statements();
            VisitStatements(stmts);
            if (HasStackOverflow()) return;
        }
    }

    public override void VisitDoWhileStatement(DoWhileStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.body())) return;
        Recurse(stmt.cond());
    }

    public override void VisitWhileStatement(WhileStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.cond())) return;
        Recurse(stmt.body());
    }

    public override void VisitForStatement(ForStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (stmt.init() != null)
        {
            if (!Recurse(stmt.init()!)) return;
        }
        if (stmt.cond() != null)
        {
            if (!Recurse(stmt.cond()!)) return;
        }
        if (stmt.next() != null)
        {
            if (!Recurse(stmt.next()!)) return;
        }
        Recurse(stmt.body());
    }

    public override void VisitForInStatement(ForInStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.each())) return;
        if (!Recurse(stmt.subject())) return;
        Recurse(stmt.body());
    }

    public override void VisitForOfStatement(ForOfStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.each())) return;
        if (!Recurse(stmt.subject())) return;
        Recurse(stmt.body());
    }

    public override void VisitTryCatchStatement(TryCatchStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.try_block())) return;
        Recurse(stmt.catch_block());
    }

    public override void VisitTryFinallyStatement(TryFinallyStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        if (!Recurse(stmt.try_block())) return;
        Recurse(stmt.finally_block());
    }

    public override void VisitDebuggerStatement(DebuggerStatement stmt)
    {
        if (!VisitNode(stmt)) return;
    }

    public override void VisitFunctionLiteral(FunctionLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
        DeclarationScope scope = expr.scope();
        ++_depth;
        VisitDeclarations(scope.declarations());
        --_depth;
        if (HasStackOverflow()) return;
        // A lazily parsed function literal won't have a body.
        if (expr.scope().was_lazily_parsed()) return;
        ++_depth;
        VisitStatements(expr.body());
        --_depth;
    }

    public override void VisitNativeFunctionLiteral(NativeFunctionLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitConditionalChain(ConditionalChain expr)
    {
        if (!ProcessExpression(expr)) return;
        for (int i = 0; i < expr.conditional_chain_length(); ++i)
        {
            if (!RecurseExpression(expr.condition_at(i))) return;
            if (!RecurseExpression(expr.then_expression_at(i))) return;
        }
        Recurse(expr.else_expression());
    }

    public override void VisitConditional(Conditional expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.condition())) return;
        if (!RecurseExpression(expr.then_expression())) return;
        RecurseExpression(expr.else_expression());
    }

    public override void VisitVariableProxy(VariableProxy expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitLiteral(Literal expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitRegExpLiteral(RegExpLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitObjectLiteral(ObjectLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
        List<ObjectLiteralProperty> props = expr.properties();
        for (int i = 0; i < props.Count; ++i)
        {
            ObjectLiteralProperty prop = props[i];
            if (!RecurseExpression(prop.key())) return;
            if (!RecurseExpression(prop.value())) return;
        }
    }

    public override void VisitArrayLiteral(ArrayLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
        List<Expression> values = expr.values();
        for (int i = 0; i < values.Count; ++i)
        {
            Expression value = values[i];
            if (!RecurseExpression(value)) return;
        }
    }

    public override void VisitAssignment(Assignment expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.target())) return;
        RecurseExpression(expr.value());
    }

    public override void VisitCompoundAssignment(CompoundAssignment expr) => VisitAssignment(expr);

    public override void VisitYield(Yield expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitYieldStar(YieldStar expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitAwait(Await expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitThrow(Throw expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.exception());
    }

    public override void VisitOptionalChain(OptionalChain expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitProperty(Property expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.obj())) return;
        RecurseExpression(expr.key());
    }

    public override void VisitCall(Call expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.expression())) return;
        List<Expression> args = expr.arguments();
        for (int i = 0; i < args.Count; ++i)
        {
            Expression arg = args[i];
            if (!RecurseExpression(arg)) return;
        }
    }

    public override void VisitCallNew(CallNew expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.expression())) return;
        List<Expression> args = expr.arguments();
        for (int i = 0; i < args.Count; ++i)
        {
            Expression arg = args[i];
            if (!RecurseExpression(arg)) return;
        }
    }

    public override void VisitCallRuntime(CallRuntime expr)
    {
        if (!ProcessExpression(expr)) return;
        List<Expression> args = expr.arguments();
        for (int i = 0; i < args.Count; ++i)
        {
            Expression arg = args[i];
            if (!RecurseExpression(arg)) return;
        }
    }

    public override void VisitUnaryOperation(UnaryOperation expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitCountOperation(CountOperation expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitBinaryOperation(BinaryOperation expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.left())) return;
        RecurseExpression(expr.right());
    }

    public override void VisitNaryOperation(NaryOperation expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.first())) return;
        for (int i = 0; i < expr.subsequent_length(); ++i)
        {
            if (!RecurseExpression(expr.subsequent(i))) return;
        }
    }

    public override void VisitCompareOperation(CompareOperation expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.left())) return;
        RecurseExpression(expr.right());
    }

    public override void VisitThisExpression(ThisExpression expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitClassLiteral(ClassLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
        if (expr.extends() != null)
        {
            if (!RecurseExpression(expr.extends()!)) return;
        }
        if (!RecurseExpression(expr.constructor())) return;
        if (expr.static_initializer() != null)
        {
            if (!RecurseExpression(expr.static_initializer()!)) return;
        }
        if (expr.instance_members_initializer_function() != null)
        {
            if (!RecurseExpression(expr.instance_members_initializer_function()!)) return;
        }
        List<ClassLiteralProperty> private_members = expr.private_members()!;
        for (int i = 0; i < private_members.Count; ++i)
        {
            ClassLiteralProperty prop = private_members[i];
            if (!RecurseExpression(prop.value())) return;
        }
        List<ClassLiteralProperty> props = expr.public_members()!;
        for (int i = 0; i < props.Count; ++i)
        {
            ClassLiteralProperty prop = props[i];
            if (!prop.key().IsLiteral())
            {
                if (!RecurseExpression(prop.key())) return;
            }
            if (!RecurseExpression(prop.value())) return;
        }
    }

    public override void VisitInitializeClassMembersStatement(InitializeClassMembersStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        List<ClassLiteralProperty> props = stmt.fields();
        for (int i = 0; i < props.Count; ++i)
        {
            ClassLiteralProperty prop = props[i];
            if (!prop.key().IsLiteral())
            {
                if (!Recurse(prop.key())) return;
            }
            if (!Recurse(prop.value())) return;
            if (prop.is_auto_accessor())
            {
                // The generated getter and setter are created after the
                // ClassLiteralProperty value is created, so we visit them in
                // the same order.
                if (!Recurse(prop.auto_accessor_info().generated_getter())) return;
                if (!Recurse(prop.auto_accessor_info().generated_setter())) return;
            }
        }
    }

    public override void VisitInitializeClassStaticElementsStatement(InitializeClassStaticElementsStatement stmt)
    {
        if (!VisitNode(stmt)) return;
        List<ClassLiteralStaticElement> elements = stmt.elements();
        for (int i = 0; i < elements.Count; ++i)
        {
            ClassLiteralStaticElement element = elements[i];
            switch (element.kind())
            {
                case ClassLiteralStaticElement.Kind.PROPERTY:
                {
                    ClassLiteralProperty prop = element.property();
                    if (!prop.key().IsLiteral())
                    {
                        if (!Recurse(prop.key())) return;
                    }
                    if (!Recurse(prop.value())) return;
                    if (prop.is_auto_accessor())
                    {
                        // The generated getter and setter are created after the
                        // ClassLiteralProperty value is created, so we visit them in
                        // the same order.
                        if (!Recurse(prop.auto_accessor_info().generated_getter())) return;
                        if (!Recurse(prop.auto_accessor_info().generated_setter())) return;
                    }
                    break;
                }
                case ClassLiteralStaticElement.Kind.STATIC_BLOCK:
                    if (!Recurse(element.static_block())) return;
                    break;
            }
        }
    }

    public override void VisitAutoAccessorGetterBody(AutoAccessorGetterBody stmt)
    {
        if (!VisitNode(stmt)) return;
    }

    public override void VisitAutoAccessorSetterBody(AutoAccessorSetterBody stmt)
    {
        if (!VisitNode(stmt)) return;
    }

    public override void VisitSpread(Spread expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }

    public override void VisitEmptyParentheses(EmptyParentheses expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitGetTemplateObject(GetTemplateObject expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitTemplateLiteral(TemplateLiteral expr)
    {
        if (!ProcessExpression(expr)) return;
        foreach (Expression sub in expr.substitutions())
        {
            if (!RecurseExpression(sub)) return;
        }
    }

    public override void VisitImportCallExpression(ImportCallExpression expr)
    {
        if (!ProcessExpression(expr)) return;
        if (!RecurseExpression(expr.specifier())) return;
        if (expr.import_options() != null)
        {
            RecurseExpression(expr.import_options()!);
        }
    }

    public override void VisitSuperPropertyReference(SuperPropertyReference expr)
    {
        if (!ProcessExpression(expr)) return;
    }

    public override void VisitSuperCallReference(SuperCallReference expr)
    {
        if (!ProcessExpression(expr)) return;
        ++_depth;
        VisitVariableProxy(expr.new_target_var());
        --_depth;
        if (HasStackOverflow()) return;
        ++_depth;
        VisitVariableProxy(expr.this_function_var());
        --_depth;
    }

    public override void VisitSuperCallForwardArgs(SuperCallForwardArgs expr)
    {
        if (!ProcessExpression(expr)) return;
        RecurseExpression(expr.expression());
    }
}
