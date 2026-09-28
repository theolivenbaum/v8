// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/ast.h and src/ast/ast.cc.
//
// V8 packs node flags into bit_field_; here they are plain fields. Node kinds,
// class names, accessors and the AstNodeFactory API are V8's. Heap-allocating
// operations (Literal::BuildValue, BuildBoilerplateDescription,
// GetOrBuildDescription) belong to the engine; everything they compute from
// the AST (depth, flags, boilerplate property counts, elements kinds,
// emit_store / last_instance_index) is here.

using System.Runtime.CompilerServices;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Ast;

// The abstract syntax tree is an intermediate, light-weight
// representation of the parsed JavaScript code suitable for
// compilation to native code.

public enum NodeType : byte
{
    // DECLARATION_NODE_LIST
    VariableDeclaration,
    FunctionDeclaration,
    // ITERATION_NODE_LIST
    DoWhileStatement,
    WhileStatement,
    ForStatement,
    ForInStatement,
    ForOfStatement,
    // BREAKABLE_NODE_LIST
    Block,
    SwitchStatement,
    // STATEMENT_NODE_LIST
    ExpressionStatement,
    EmptyStatement,
    SloppyBlockFunctionStatement,
    IfStatement,
    ContinueStatement,
    BreakStatement,
    ReturnStatement,
    WithStatement,
    TryCatchStatement,
    TryFinallyStatement,
    DebuggerStatement,
    InitializeClassMembersStatement,
    InitializeClassStaticElementsStatement,
    AutoAccessorGetterBody,
    AutoAccessorSetterBody,
    // LITERAL_NODE_LIST
    RegExpLiteral,
    ObjectLiteral,
    ArrayLiteral,
    // EXPRESSION_NODE_LIST
    Assignment,
    Await,
    BinaryOperation,
    NaryOperation,
    Call,
    SuperCallForwardArgs,
    CallNew,
    CallRuntime,
    ClassLiteral,
    CompareOperation,
    CompoundAssignment,
    ConditionalChain,
    Conditional,
    CountOperation,
    EmptyParentheses,
    FunctionLiteral,
    GetTemplateObject,
    ImportCallExpression,
    Literal,
    NativeFunctionLiteral,
    OptionalChain,
    Property,
    Spread,
    SuperCallReference,
    SuperPropertyReference,
    TemplateLiteral,
    ThisExpression,
    Throw,
    UnaryOperation,
    VariableProxy,
    Yield,
    YieldStar,
    // FAILURE_NODE_LIST
    FailureExpression,
}

// HandlerTable::CatchPrediction (src/codegen/handler-table.h).
public enum CatchPrediction
{
    UNCAUGHT,     // The handler will (likely) rethrow the exception.
    CAUGHT,       // The exception will be caught by the handler.
    PROMISE,      // The exception will be caught and cause a promise rejection.
    ASYNC_AWAIT,  // The exception will be caught and cause a promise rejection
                  // in the desugaring of an async function.
    UNCAUGHT_ASYNC_AWAIT, // The exception will be caught and cause a promise
                          // rejection in the desugaring of an async REPL script.
}

// src/common/globals.h
public enum LookupHoistingMode { kNormal, kLegacySloppy }

public enum CreateArgumentsType : byte { kMappedArguments, kUnmappedArguments, kRestParameter }

// include/v8-callbacks.h
public enum ModuleImportPhase { kSource, kDefer, kEvaluation }

// src/objects/elements-kind.h (the part the AST uses).
public enum ElementsKind : byte
{
    PACKED_SMI_ELEMENTS,
    HOLEY_SMI_ELEMENTS,
    PACKED_ELEMENTS,
    HOLEY_ELEMENTS,
    PACKED_DOUBLE_ELEMENTS,
    HOLEY_DOUBLE_ELEMENTS,

    FIRST_FAST_ELEMENTS_KIND = PACKED_SMI_ELEMENTS,
    LAST_FAST_ELEMENTS_KIND = HOLEY_DOUBLE_ELEMENTS,
}

public static class ElementsKinds
{
    public static bool IsDoubleElementsKind(ElementsKind kind) => kind == ElementsKind.PACKED_DOUBLE_ELEMENTS || kind == ElementsKind.HOLEY_DOUBLE_ELEMENTS;
    public static bool IsHoleyElementsKind(ElementsKind kind) => ((int)kind & 1) != 0;
    public static bool IsSmiOrObjectElementsKind(ElementsKind kind) => kind <= ElementsKind.HOLEY_ELEMENTS;
    public static ElementsKind GetHoleyElementsKind(ElementsKind packed) => (ElementsKind)((int)packed | 1);
}

public abstract class AstNode
{
    private readonly int _position;
    private readonly NodeType _nodeType;

    protected AstNode(int position, NodeType type)
    {
        _position = position;
        _nodeType = type;
    }

    public NodeType node_type() => _nodeType;
    public int position() => _position;

    public bool IsVariableDeclaration() => _nodeType == NodeType.VariableDeclaration;
    public bool IsFunctionDeclaration() => _nodeType == NodeType.FunctionDeclaration;
    public bool IsDoWhileStatement() => _nodeType == NodeType.DoWhileStatement;
    public bool IsWhileStatement() => _nodeType == NodeType.WhileStatement;
    public bool IsForStatement() => _nodeType == NodeType.ForStatement;
    public bool IsForInStatement() => _nodeType == NodeType.ForInStatement;
    public bool IsForOfStatement() => _nodeType == NodeType.ForOfStatement;
    public bool IsBlock() => _nodeType == NodeType.Block;
    public bool IsSwitchStatement() => _nodeType == NodeType.SwitchStatement;
    public bool IsExpressionStatement() => _nodeType == NodeType.ExpressionStatement;
    public bool IsEmptyStatement() => _nodeType == NodeType.EmptyStatement;
    public bool IsSloppyBlockFunctionStatement() => _nodeType == NodeType.SloppyBlockFunctionStatement;
    public bool IsIfStatement() => _nodeType == NodeType.IfStatement;
    public bool IsContinueStatement() => _nodeType == NodeType.ContinueStatement;
    public bool IsBreakStatement() => _nodeType == NodeType.BreakStatement;
    public bool IsReturnStatement() => _nodeType == NodeType.ReturnStatement;
    public bool IsWithStatement() => _nodeType == NodeType.WithStatement;
    public bool IsTryCatchStatement() => _nodeType == NodeType.TryCatchStatement;
    public bool IsTryFinallyStatement() => _nodeType == NodeType.TryFinallyStatement;
    public bool IsDebuggerStatement() => _nodeType == NodeType.DebuggerStatement;
    public bool IsInitializeClassMembersStatement() => _nodeType == NodeType.InitializeClassMembersStatement;
    public bool IsInitializeClassStaticElementsStatement() => _nodeType == NodeType.InitializeClassStaticElementsStatement;
    public bool IsAutoAccessorGetterBody() => _nodeType == NodeType.AutoAccessorGetterBody;
    public bool IsAutoAccessorSetterBody() => _nodeType == NodeType.AutoAccessorSetterBody;
    public bool IsRegExpLiteral() => _nodeType == NodeType.RegExpLiteral;
    public bool IsObjectLiteral() => _nodeType == NodeType.ObjectLiteral;
    public bool IsArrayLiteral() => _nodeType == NodeType.ArrayLiteral;
    public bool IsAssignment() => _nodeType == NodeType.Assignment;
    public bool IsAwait() => _nodeType == NodeType.Await;
    public bool IsBinaryOperation() => _nodeType == NodeType.BinaryOperation;
    public bool IsNaryOperation() => _nodeType == NodeType.NaryOperation;
    public bool IsCall() => _nodeType == NodeType.Call;
    public bool IsSuperCallForwardArgs() => _nodeType == NodeType.SuperCallForwardArgs;
    public bool IsCallNew() => _nodeType == NodeType.CallNew;
    public bool IsCallRuntime() => _nodeType == NodeType.CallRuntime;
    public bool IsClassLiteral() => _nodeType == NodeType.ClassLiteral;
    public bool IsCompareOperation() => _nodeType == NodeType.CompareOperation;
    public bool IsCompoundAssignment() => _nodeType == NodeType.CompoundAssignment;
    public bool IsConditionalChain() => _nodeType == NodeType.ConditionalChain;
    public bool IsConditional() => _nodeType == NodeType.Conditional;
    public bool IsCountOperation() => _nodeType == NodeType.CountOperation;
    public bool IsEmptyParentheses() => _nodeType == NodeType.EmptyParentheses;
    public bool IsFunctionLiteral() => _nodeType == NodeType.FunctionLiteral;
    public bool IsGetTemplateObject() => _nodeType == NodeType.GetTemplateObject;
    public bool IsImportCallExpression() => _nodeType == NodeType.ImportCallExpression;
    public bool IsLiteral() => _nodeType == NodeType.Literal;
    public bool IsNativeFunctionLiteral() => _nodeType == NodeType.NativeFunctionLiteral;
    public bool IsOptionalChain() => _nodeType == NodeType.OptionalChain;
    public bool IsProperty() => _nodeType == NodeType.Property;
    public bool IsSpread() => _nodeType == NodeType.Spread;
    public bool IsSuperCallReference() => _nodeType == NodeType.SuperCallReference;
    public bool IsSuperPropertyReference() => _nodeType == NodeType.SuperPropertyReference;
    public bool IsTemplateLiteral() => _nodeType == NodeType.TemplateLiteral;
    public bool IsThisExpression() => _nodeType == NodeType.ThisExpression;
    public bool IsThrow() => _nodeType == NodeType.Throw;
    public bool IsUnaryOperation() => _nodeType == NodeType.UnaryOperation;
    public bool IsVariableProxy() => _nodeType == NodeType.VariableProxy;
    public bool IsYield() => _nodeType == NodeType.Yield;
    public bool IsYieldStar() => _nodeType == NodeType.YieldStar;
    public bool IsFailureExpression() => _nodeType == NodeType.FailureExpression;

    public VariableDeclaration? AsVariableDeclaration() => this as VariableDeclaration;
    public FunctionDeclaration? AsFunctionDeclaration() => this as FunctionDeclaration;
    public DoWhileStatement? AsDoWhileStatement() => this as DoWhileStatement;
    public WhileStatement? AsWhileStatement() => this as WhileStatement;
    public ForStatement? AsForStatement() => this as ForStatement;
    public ForInStatement? AsForInStatement() => this as ForInStatement;
    public ForOfStatement? AsForOfStatement() => this as ForOfStatement;
    public Block? AsBlock() => this as Block;
    public SwitchStatement? AsSwitchStatement() => this as SwitchStatement;
    public ExpressionStatement? AsExpressionStatement() => this as ExpressionStatement;
    public EmptyStatement? AsEmptyStatement() => this as EmptyStatement;
    public SloppyBlockFunctionStatement? AsSloppyBlockFunctionStatement() => this as SloppyBlockFunctionStatement;
    public IfStatement? AsIfStatement() => this as IfStatement;
    public ContinueStatement? AsContinueStatement() => this as ContinueStatement;
    public BreakStatement? AsBreakStatement() => this as BreakStatement;
    public ReturnStatement? AsReturnStatement() => this as ReturnStatement;
    public WithStatement? AsWithStatement() => this as WithStatement;
    public TryCatchStatement? AsTryCatchStatement() => this as TryCatchStatement;
    public TryFinallyStatement? AsTryFinallyStatement() => this as TryFinallyStatement;
    public DebuggerStatement? AsDebuggerStatement() => this as DebuggerStatement;
    public InitializeClassMembersStatement? AsInitializeClassMembersStatement() => this as InitializeClassMembersStatement;
    public InitializeClassStaticElementsStatement? AsInitializeClassStaticElementsStatement() => this as InitializeClassStaticElementsStatement;
    public AutoAccessorGetterBody? AsAutoAccessorGetterBody() => this as AutoAccessorGetterBody;
    public AutoAccessorSetterBody? AsAutoAccessorSetterBody() => this as AutoAccessorSetterBody;
    public RegExpLiteral? AsRegExpLiteral() => this as RegExpLiteral;
    public ObjectLiteral? AsObjectLiteral() => this as ObjectLiteral;
    public ArrayLiteral? AsArrayLiteral() => this as ArrayLiteral;
    // Assignment is also the base of CompoundAssignment; V8's AsAssignment
    // only matches kAssignment.
    public Assignment? AsAssignment() => _nodeType == NodeType.Assignment ? (Assignment)this : null;
    public Await? AsAwait() => this as Await;
    public BinaryOperation? AsBinaryOperation() => this as BinaryOperation;
    public NaryOperation? AsNaryOperation() => this as NaryOperation;
    public Call? AsCall() => this as Call;
    public SuperCallForwardArgs? AsSuperCallForwardArgs() => this as SuperCallForwardArgs;
    public CallNew? AsCallNew() => this as CallNew;
    public CallRuntime? AsCallRuntime() => this as CallRuntime;
    public ClassLiteral? AsClassLiteral() => this as ClassLiteral;
    public CompareOperation? AsCompareOperation() => this as CompareOperation;
    public CompoundAssignment? AsCompoundAssignment() => this as CompoundAssignment;
    public ConditionalChain? AsConditionalChain() => this as ConditionalChain;
    public Conditional? AsConditional() => this as Conditional;
    public CountOperation? AsCountOperation() => this as CountOperation;
    public EmptyParentheses? AsEmptyParentheses() => this as EmptyParentheses;
    public FunctionLiteral? AsFunctionLiteral() => this as FunctionLiteral;
    public GetTemplateObject? AsGetTemplateObject() => this as GetTemplateObject;
    public ImportCallExpression? AsImportCallExpression() => this as ImportCallExpression;
    public Literal? AsLiteral() => this as Literal;
    public NativeFunctionLiteral? AsNativeFunctionLiteral() => this as NativeFunctionLiteral;
    public OptionalChain? AsOptionalChain() => this as OptionalChain;
    public Property? AsProperty() => this as Property;
    public Spread? AsSpread() => this as Spread;
    public SuperCallReference? AsSuperCallReference() => this as SuperCallReference;
    public SuperPropertyReference? AsSuperPropertyReference() => this as SuperPropertyReference;
    public TemplateLiteral? AsTemplateLiteral() => this as TemplateLiteral;
    public ThisExpression? AsThisExpression() => this as ThisExpression;
    public Throw? AsThrow() => this as Throw;
    public UnaryOperation? AsUnaryOperation() => this as UnaryOperation;
    public VariableProxy? AsVariableProxy() => this as VariableProxy;
    public Yield? AsYield() => this as Yield;
    public YieldStar? AsYieldStar() => this as YieldStar;
    public FailureExpression? AsFailureExpression() => this as FailureExpression;

    public IterationStatement? AsIterationStatement() => this as IterationStatement;
    public MaterializedLiteral? AsMaterializedLiteral() => this as MaterializedLiteral;
}

public abstract class Statement(int position, NodeType type) : AstNode(position, type), V8Sharp.Parsing.IParserStatement
{
}

public abstract class Expression(int pos, NodeType type) : AstNode(pos, type), V8Sharp.Parsing.IParserExpression
{
    private bool _isParenthesized;

    // True iff the expression is a valid reference expression.
    public bool IsValidReferenceExpression() =>
        IsProperty() || (IsVariableProxy() && ((VariableProxy)this).IsValidReferenceExpression());

    // True iff the expression is a private name.
    public bool IsPrivateName() => IsVariableProxy() && ((VariableProxy)this).IsPrivateName();

    // Helpers for ToBoolean conversion.
    public bool ToBooleanIsTrue() => IsLiteral() && ((Literal)this).ToBooleanIsTrue();
    public bool ToBooleanIsFalse() => IsLiteral() && ((Literal)this).ToBooleanIsFalse();

    // Symbols that cannot be parsed as array indices are considered property
    // names.  We do not treat symbols that can be array indexes as property
    // names because [] for string objects is handled only by keyed ICs.
    public bool IsPropertyName() => IsLiteral() && ((Literal)this).IsPropertyName();

    // True iff the expression is a class or function expression without
    // a syntactic name.
    public bool IsAnonymousFunctionDefinition() =>
        (IsFunctionLiteral() && ((FunctionLiteral)this).IsAnonymousFunctionDefinition()) ||
        (IsClassLiteral() && ((ClassLiteral)this).IsAnonymousFunctionDefinition());

    // True iff the expression is a concise method definition.
    public bool IsConciseMethodDefinition() => IsFunctionLiteral() && IsConciseMethod(((FunctionLiteral)this).kind());

    // True iff the expression is an accessor function definition.
    public bool IsAccessorFunctionDefinition() => IsFunctionLiteral() && IsAccessorFunction(((FunctionLiteral)this).kind());

    // True iff the expression is a literal represented as a smi.
    public bool IsSmiLiteral() => IsLiteral() && ((Literal)this).type() == Literal.Type.kSmi;

    // True iff the expression is a literal represented as a number.
    public bool IsNumberLiteral() => IsLiteral() && ((Literal)this).IsNumber();

    // True iff the expression is a string literal.
    public bool IsStringLiteral() => IsLiteral() && ((Literal)this).type() == Literal.Type.kString;

    // True iff the expression is a cons string literal.
    public bool IsConsStringLiteral() => IsLiteral() && ((Literal)this).type() == Literal.Type.kConsString;

    // True iff the expression is the null literal.
    public bool IsNullLiteral() => IsLiteral() && ((Literal)this).type() == Literal.Type.kNull;

    public bool IsBooleanLiteral() => IsLiteral() && ((Literal)this).type() == Literal.Type.kBoolean;

    // True iff the expression is the hole literal.
    public bool IsTheHoleLiteral() => IsLiteral() && ((Literal)this).type() == Literal.Type.kTheHole;

    // True if we can prove that the expression is the undefined literal. Note
    // that this also checks for loads of the global "undefined" variable.
    public bool IsUndefinedLiteral()
    {
        if (IsLiteral() && ((Literal)this).type() == Literal.Type.kUndefined) return true;

        if (this is not VariableProxy var_proxy) return false;
        Variable? var = var_proxy.is_resolved() ? var_proxy.var() : null;
        // The global identifier "undefined" is immutable. Everything
        // else could be reassigned.
        return var != null && var.IsUnallocated() && var_proxy.raw_name().IsOneByteEqualTo("undefined");
    }

    // True if either null literal or undefined literal.
    public bool IsNullOrUndefinedLiteral() => IsNullLiteral() || IsUndefinedLiteral();

    // True if a literal and not null or undefined.
    public bool IsLiteralButNotNullOrUndefined() => IsLiteral() && !IsNullOrUndefinedLiteral();

    public bool IsCompileTimeValue()
    {
        if (IsLiteral()) return true;
        MaterializedLiteral? literal = AsMaterializedLiteral();
        if (literal == null) return false;
        return literal.IsSimple();
    }

    public bool IsPattern() => node_type() is NodeType.ObjectLiteral or NodeType.ArrayLiteral;

    public bool is_parenthesized() => _isParenthesized;
    public void mark_parenthesized() => _isParenthesized = true;
    public void clear_parenthesized() => _isParenthesized = false;
}

public sealed class FailureExpression : Expression
{
    internal FailureExpression() : base(kNoSourcePosition, NodeType.FailureExpression) { }
}

// V8's notion of BreakableStatement does not correspond to the notion of
// BreakableStatement in ECMAScript. In V8, the idea is that a
// BreakableStatement is a statement that can be the target of a break
// statement.
public abstract class BreakableStatement(int position, NodeType type) : Statement(position, type)
{
}

public sealed class Block : BreakableStatement, V8Sharp.Parsing.IParserBlock<Statement, V8Sharp.Parsing.ScopedPtrList<Statement>>
{
    private List<Statement> _statements;
    private Scope? _scope;
    private readonly bool _ignoreCompletionValue;
    private readonly bool _isBreakable;
    private readonly bool _isInitializationBlockForParameters;

    internal Block(int capacity, bool ignore_completion_value, bool is_breakable,
                   bool is_initialization_block_for_parameters)
        : base(kNoSourcePosition, NodeType.Block)
    {
        _statements = new List<Statement>(capacity);
        _ignoreCompletionValue = ignore_completion_value;
        _isBreakable = is_breakable;
        _isInitializationBlockForParameters = is_initialization_block_for_parameters;
    }

    public List<Statement> statements() => _statements;
    public bool ignore_completion_value() => _ignoreCompletionValue;
    public bool is_breakable() => _isBreakable;
    public bool is_initialization_block_for_parameters() => _isInitializationBlockForParameters;

    public Scope? scope() => _scope;
    public void set_scope(Scope? scope) => _scope = scope;

    public void InitializeStatements(IReadOnlyList<Statement> statements)
    {
        _statements = new List<Statement>(statements.Count);
        for (int i = 0; i < statements.Count; i++) _statements.Add(statements[i]);
    }

    void V8Sharp.Parsing.IParserBlock<Statement, V8Sharp.Parsing.ScopedPtrList<Statement>>.InitializeStatements(
        V8Sharp.Parsing.ScopedPtrList<Statement> statements) => InitializeStatements(statements);

    // block->statements()->Add(statement, zone).
    public void AddStatement(Statement statement) => _statements.Add(statement);
}

public abstract class Declaration : AstNode, IThreadedListNode<Declaration>
{
    private Variable? _var;
    private Declaration? _next;

    protected Declaration(int pos, NodeType type) : base(pos, type) { }

    public Variable? var() => _var;
    public void set_var(Variable? var) => _var = var;

    Declaration? IThreadedListNode<Declaration>.NextNode { get => _next; set => _next = value; }
}

public class VariableDeclaration : Declaration
{
    private readonly bool _isNested;

    internal VariableDeclaration(int pos, bool is_nested = false) : base(pos, NodeType.VariableDeclaration)
    {
        _isNested = is_nested;
    }

    public NestedVariableDeclaration? AsNested() => _isNested ? (NestedVariableDeclaration)this : null;
}

// For var declarations that appear in a block scope.
// Only distinguished from VariableDeclaration during Scope analysis,
// so it doesn't get its own NodeType.
public sealed class NestedVariableDeclaration : VariableDeclaration
{
    // Nested scope from which the declaration originated.
    private readonly Scope _scope;

    internal NestedVariableDeclaration(Scope scope, int pos) : base(pos, true) => _scope = scope;

    public Scope scope() => _scope;
}

public sealed class FunctionDeclaration : Declaration
{
    private readonly FunctionLiteral _fun;

    internal FunctionDeclaration(FunctionLiteral fun, int pos) : base(pos, NodeType.FunctionDeclaration) => _fun = fun;

    public FunctionLiteral fun() => _fun;
}

public abstract class IterationStatement(int pos, NodeType type) : BreakableStatement(pos, type)
{
    private Statement? _body;

    public Statement body() => _body!;
    public void set_body(Statement s) => _body = s;
    protected void Initialize(Statement body) => _body = body;
}

public sealed class DoWhileStatement : IterationStatement
{
    private Expression? _cond;

    internal DoWhileStatement(int pos) : base(pos, NodeType.DoWhileStatement) { }

    public void Initialize(Expression cond, Statement body)
    {
        base.Initialize(body);
        _cond = cond;
    }

    public Expression cond() => _cond!;
}

public sealed class WhileStatement : IterationStatement
{
    private Expression? _cond;

    internal WhileStatement(int pos) : base(pos, NodeType.WhileStatement) { }

    public void Initialize(Expression cond, Statement body)
    {
        base.Initialize(body);
        _cond = cond;
    }

    public Expression cond() => _cond!;
}

public sealed class ForStatement : IterationStatement
{
    private Statement? _init;
    private Expression? _cond;
    private Statement? _next;

    internal ForStatement(int pos) : base(pos, NodeType.ForStatement) { }

    public void Initialize(Statement? init, Expression? cond, Statement? next, Statement body)
    {
        base.Initialize(body);
        _init = init;
        _cond = cond;
        _next = next;
    }

    public Statement? init() => _init;
    public Expression? cond() => _cond;
    public Statement? next() => _next;
}

// Shared class for for-in and for-of statements.
public abstract class ForEachStatement : IterationStatement
{
    public enum VisitMode
    {
        ENUMERATE, // for (each in subject) body;
        ITERATE,   // for (each of subject) body;
    }

    private Expression? _each;
    private Expression? _subject;
    private Scope? _subjectScope;

    protected ForEachStatement(int pos, NodeType type) : base(pos, type) { }

    public static string VisitModeString(VisitMode mode) => mode == VisitMode.ITERATE ? "for-of" : "for-in";

    public void Initialize(Expression each, Expression subject, Statement body, Scope? subject_scope)
    {
        base.Initialize(body);
        _each = each;
        _subject = subject;
        _subjectScope = subject_scope;
    }

    public new void Initialize(Statement body) => base.Initialize(body);

    public Expression each() => _each!;
    public Expression subject() => _subject!;

    // The parser wraps the `subject` expression into a hidden block scope
    // in some cases. Otherwise the debugger gets confused when pausing in the
    // `subject` expression.
    public Scope? subject_scope() => _subjectScope;
}

public sealed class ForInStatement : ForEachStatement
{
    internal ForInStatement(int pos) : base(pos, NodeType.ForInStatement) { }
}

public enum IteratorType { kNormal, kAsync }

public sealed class ForOfStatement : ForEachStatement
{
    private readonly IteratorType _type;

    internal ForOfStatement(int pos, IteratorType type) : base(pos, NodeType.ForOfStatement) => _type = type;

    public IteratorType type() => _type;
}

public sealed class ExpressionStatement : Statement
{
    private Expression _expression;

    internal ExpressionStatement(Expression expression, int pos) : base(pos, NodeType.ExpressionStatement) => _expression = expression;

    public void set_expression(Expression e) => _expression = e;
    public Expression expression() => _expression;
}

public abstract class JumpStatement(int pos, NodeType type) : Statement(pos, type)
{
}

public sealed class ContinueStatement : JumpStatement
{
    private readonly IterationStatement _target;

    internal ContinueStatement(IterationStatement target, int pos) : base(pos, NodeType.ContinueStatement) => _target = target;

    public IterationStatement target() => _target;
}

public sealed class BreakStatement : JumpStatement
{
    private readonly BreakableStatement _target;

    internal BreakStatement(BreakableStatement target, int pos) : base(pos, NodeType.BreakStatement) => _target = target;

    public BreakableStatement target() => _target;
}

public sealed class ReturnStatement : JumpStatement
{
    public enum Type { kNormal, kAsyncReturn, kSyntheticAsyncReturn }

    // This constant is used to indicate that the return position
    // from the FunctionLiteral should be used when emitting code.
    public const int kFunctionLiteralReturnPosition = -2;

    private readonly Expression _expression;
    private readonly int _endPosition;
    private readonly Type _type;

    internal ReturnStatement(Expression expression, Type type, int pos, int end_position)
        : base(pos, NodeType.ReturnStatement)
    {
        _expression = expression;
        _endPosition = end_position;
        _type = type;
    }

    public Expression expression() => _expression;
    public Type type() => _type;
    public bool is_async_return() => _type != Type.kNormal;
    public bool is_synthetic_async_return() => _type == Type.kSyntheticAsyncReturn;
    public int end_position() => _endPosition;
}

public sealed class WithStatement : Statement
{
    private readonly Scope _scope;
    private readonly Expression _expression;
    private Statement _statement;

    internal WithStatement(Scope scope, Expression expression, Statement statement, int pos)
        : base(pos, NodeType.WithStatement)
    {
        _scope = scope;
        _expression = expression;
        _statement = statement;
    }

    public Scope scope() => _scope;
    public Expression expression() => _expression;
    public Statement statement() => _statement;
    public void set_statement(Statement s) => _statement = s;
}

public sealed class CaseClause
{
    private readonly Expression? _label;
    private readonly List<Statement> _statements;

    internal CaseClause(Expression? label, IReadOnlyList<Statement> statements)
    {
        _label = label;
        _statements = new List<Statement>(statements.Count);
        for (int i = 0; i < statements.Count; i++) _statements.Add(statements[i]);
    }

    public bool is_default() => _label == null;
    public Expression label() => _label!;
    public List<Statement> statements() => _statements;
}

public sealed class SwitchStatement : BreakableStatement
{
    private Expression _tag;
    private readonly List<CaseClause> _cases = new(4);

    internal SwitchStatement(Expression tag, int pos) : base(pos, NodeType.SwitchStatement) => _tag = tag;

    public Expression tag() => _tag;
    public void set_tag(Expression t) => _tag = t;
    public List<CaseClause> cases() => _cases;
}

// If-statements always have non-null references to their then- and
// else-parts. When parsing if-statements with no explicit else-part,
// the parser implicitly creates an empty statement. Use the
// HasThenStatement() and HasElseStatement() functions to check if a
// given if-statement has a then- or an else-part containing code.
public sealed class IfStatement : Statement
{
    private readonly Expression _condition;
    private Statement _thenStatement;
    private Statement _elseStatement;

    internal IfStatement(Expression condition, Statement then_statement, Statement else_statement, int pos)
        : base(pos, NodeType.IfStatement)
    {
        _condition = condition;
        _thenStatement = then_statement;
        _elseStatement = else_statement;
    }

    public bool HasThenStatement() => !_thenStatement.IsEmptyStatement();
    public bool HasElseStatement() => !_elseStatement.IsEmptyStatement();

    public Expression condition() => _condition;
    public Statement then_statement() => _thenStatement;
    public Statement else_statement() => _elseStatement;

    public void set_then_statement(Statement s) => _thenStatement = s;
    public void set_else_statement(Statement s) => _elseStatement = s;
}

public abstract class TryStatement : Statement
{
    private Block _tryBlock;

    protected TryStatement(Block try_block, int pos, NodeType type) : base(pos, type) => _tryBlock = try_block;

    public Block try_block() => _tryBlock;
    public void set_try_block(Block b) => _tryBlock = b;
}

public sealed class TryCatchStatement : TryStatement
{
    private readonly Scope? _scope;
    private Block _catchBlock;
    private readonly CatchPrediction _catchPrediction;

    internal TryCatchStatement(Block try_block, Scope? scope, Block catch_block, CatchPrediction catch_prediction, int pos)
        : base(try_block, pos, NodeType.TryCatchStatement)
    {
        _scope = scope;
        _catchBlock = catch_block;
        _catchPrediction = catch_prediction;
    }

    public Scope? scope() => _scope;
    public Block catch_block() => _catchBlock;
    public void set_catch_block(Block b) => _catchBlock = b;

    // Prediction of whether exceptions thrown into the handler for this try block
    // will be caught.
    //
    // If this try/catch statement is meant to rethrow (HandlerTable::UNCAUGHT),
    // the catch prediction value is set to the same value as the surrounding
    // catch prediction.
    public CatchPrediction GetCatchPrediction(CatchPrediction outer_catch_prediction)
    {
        if (_catchPrediction == CatchPrediction.UNCAUGHT)
        {
            return outer_catch_prediction;
        }
        return _catchPrediction;
    }

    // Indicates whether or not code should be generated to clear the pending
    // exception. The exception is cleared for cases where the exception
    // is not guaranteed to be rethrown, indicated by the value
    // HandlerTable::UNCAUGHT. If both the current and surrounding catch handler's
    // are predicted uncaught, the exception is not cleared.
    //
    // For scripts in repl mode there is exactly one catch block with
    // UNCAUGHT_ASYNC_AWAIT prediction. This catch block needs to preserve
    // the exception so it can be reused later by the inspector.
    public bool ShouldClearException(CatchPrediction outer_catch_prediction)
    {
        if (_catchPrediction == CatchPrediction.UNCAUGHT_ASYNC_AWAIT)
        {
            return false;
        }

        return _catchPrediction != CatchPrediction.UNCAUGHT || outer_catch_prediction != CatchPrediction.UNCAUGHT;
    }

    public bool is_try_catch_for_async() => _catchPrediction == CatchPrediction.ASYNC_AWAIT;

    public CatchPrediction catch_prediction() => _catchPrediction;
}

public sealed class TryFinallyStatement : TryStatement
{
    private Block _finallyBlock;

    internal TryFinallyStatement(Block try_block, Block finally_block, int pos)
        : base(try_block, pos, NodeType.TryFinallyStatement) => _finallyBlock = finally_block;

    public Block finally_block() => _finallyBlock;
    public void set_finally_block(Block b) => _finallyBlock = b;
}

public sealed class DebuggerStatement : Statement
{
    internal DebuggerStatement(int pos) : base(pos, NodeType.DebuggerStatement) { }
}

public sealed class EmptyStatement : Statement
{
    internal EmptyStatement() : base(kNoSourcePosition, NodeType.EmptyStatement) { }
}

// Delegates to another statement, which may be overwritten.
// This was introduced to implement ES2015 Annex B3.3 for conditionally making
// sloppy-mode block-scoped functions have a var binding, which is changed
// from one statement to another during parsing.
public sealed class SloppyBlockFunctionStatement : Statement, IThreadedListNode<SloppyBlockFunctionStatement>
{
    private readonly Variable _var;
    private Statement _statement;
    private SloppyBlockFunctionStatement? _next;
    private readonly Token _init;

    internal SloppyBlockFunctionStatement(int pos, Variable var, Token init, Statement statement)
        : base(pos, NodeType.SloppyBlockFunctionStatement)
    {
        _var = var;
        _statement = statement;
        _init = init;
    }

    public Statement statement() => _statement;
    public void set_statement(Statement statement) => _statement = statement;
    public Scope scope() => _var.scope()!;
    public Variable var() => _var;
    public Token init() => _init;
    public AstRawString name() => _var.raw_name();

    SloppyBlockFunctionStatement? IThreadedListNode<SloppyBlockFunctionStatement>.NextNode { get => _next; set => _next = value; }
}

public sealed class Literal : Expression
{
    public enum Type
    {
        kSmi,
        kHeapNumber,
        kBigInt,
        kString,
        kConsString,
        kBoolean,
        kUndefined,
        kNull,
        kTheHole,
    }

    private readonly Type _type;
    private readonly AstRawString? _string;
    private readonly AstConsString? _consString;
    private readonly int _smi;
    private readonly double _number;
    private readonly AstBigInt _bigint;
    private readonly bool _boolean;

    internal Literal(int smi, int position) : base(position, NodeType.Literal)
    {
        _type = Type.kSmi;
        _smi = smi;
    }

    internal Literal(double number, int position) : base(position, NodeType.Literal)
    {
        _type = Type.kHeapNumber;
        _number = number;
    }

    internal Literal(AstBigInt bigint, int position) : base(position, NodeType.Literal)
    {
        _type = Type.kBigInt;
        _bigint = bigint;
    }

    internal Literal(AstRawString str, int position) : base(position, NodeType.Literal)
    {
        _type = Type.kString;
        _string = str;
    }

    internal Literal(AstConsString str, int position) : base(position, NodeType.Literal)
    {
        _type = Type.kConsString;
        _consString = str;
    }

    internal Literal(bool boolean, int position) : base(position, NodeType.Literal)
    {
        _type = Type.kBoolean;
        _boolean = boolean;
    }

    internal Literal(Type type, int position) : base(position, NodeType.Literal)
    {
        System.Diagnostics.Debug.Assert(type == Type.kNull || type == Type.kUndefined || type == Type.kTheHole);
        _type = type;
    }

    public Type type() => _type;

    // Returns true if literal represents a property name (i.e. cannot be parsed
    // as array indices).
    public new bool IsPropertyName()
    {
        if (_type != Type.kString) return false;
        return !_string!.AsArrayIndex(out _);
    }

    // Returns true if literal represents an array index.
    public bool AsArrayIndex(out uint value) => ToUint32(out value) && value != kMaxUInt32;

    public AstRawString AsRawPropertyName() => _string!;

    public int AsSmiLiteral() => _smi;

    public bool AsBooleanLiteral() => _boolean;

    // Returns true if literal represents a Number.
    public bool IsNumber() => _type == Type.kHeapNumber || _type == Type.kSmi;

    public double AsNumber() => _type switch
    {
        Type.kSmi => _smi,
        Type.kHeapNumber => _number,
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public AstBigInt AsBigInt() => _bigint;

    public bool IsRawString() => _type == Type.kString;
    public AstRawString AsRawString() => _string!;

    public bool IsConsString() => _type == Type.kConsString;
    public AstConsString AsConsString() => _consString!;

    public new bool ToBooleanIsTrue()
    {
        switch (_type)
        {
            case Type.kSmi:
                return _smi != 0;
            case Type.kHeapNumber:
                // DoubleToBoolean: false for NaN, +0 and -0.
                return !double.IsNaN(_number) && _number != 0;
            case Type.kString:
                return !_string!.IsEmpty();
            case Type.kConsString:
                return !_consString!.IsEmpty();
            case Type.kNull:
            case Type.kUndefined:
                return false;
            case Type.kBoolean:
                return _boolean;
            case Type.kBigInt:
            {
                string bigint_str = _bigint.c_str();
                int length = bigint_str.Length;
                if (length == 1 && bigint_str[0] == '0') return false;
                // Skip over any radix prefix; BigInts with length > 1 only
                // begin with zero if they include a radix.
                for (int i = (bigint_str[0] == '0') ? 2 : 0; i < length; ++i)
                {
                    if (bigint_str[i] != '0') return true;
                }
                return false;
            }
            default:
                throw new InvalidOperationException("UNREACHABLE");
        }
    }

    public new bool ToBooleanIsFalse() => !ToBooleanIsTrue();

    public bool ToUint32(out uint value)
    {
        switch (_type)
        {
            case Type.kString:
                return _string!.AsArrayIndex(out value);
            case Type.kSmi:
                if (_smi < 0)
                {
                    value = 0;
                    return false;
                }
                value = (uint)_smi;
                return true;
            case Type.kHeapNumber:
                return DoubleToUint32IfEqualToSelf(AsNumber(), out value);
            default:
                value = 0;
                return false;
        }
    }

    // DoubleToUint32IfEqualToSelf (src/numbers/conversions-inl.h).
    private static bool DoubleToUint32IfEqualToSelf(double value, out uint uint32_value)
    {
        const double k2Pow52 = 4503599627370496.0;
        const uint kValidTopBits = 0x43300000;
        const ulong kBottomBitMask = 0x0000_0000_FFFF_FFFF;

        // Add 2^52 to the double, to place valid uint32 values in the low-significant
        // bits of the exponent, by effectively setting the (implicit) top bit of the
        // significand. Note that this addition also normalises 0.0 and -0.0.
        double shifted_value = value + k2Pow52;

        // At this point, shifted_value > 2^52 iff value >= 0. Since we
        // don't care about the high bits of the uint32, we can just check
        // shifted_value against 2^52 + 2^32 -- this doesn't matter for values
        // within the uint32 range.
        ulong result = (ulong)BitConverter.DoubleToInt64Bits(shifted_value);
        if ((result >> 32) == kValidTopBits)
        {
            uint32_value = (uint)(result & kBottomBitMask);
            return uint32_value == value;
        }
        uint32_value = 0;
        return false;
    }

    // Support for using Literal as a HashMap key. NOTE: Currently, this works
    // only for string and number literals!
    public uint Hash()
    {
        if (AsArrayIndex(out uint index))
        {
            // Treat array indices as numbers, so that array indices are de-duped
            // correctly even if one of them is a string and the other is a number.
            return (uint)Hash64(index);
        }
        return IsRawString() ? AsRawString().Hash() : (uint)Hash64((ulong)BitConverter.DoubleToInt64Bits(AsNumber()));
    }

    private static ulong Hash64(ulong key)
    {
        // base::hash64 (a mix function); only used for bucket placement.
        key = ~key + (key << 21);
        key ^= key >> 24;
        key = key + (key << 3) + (key << 8);
        key ^= key >> 14;
        key = key + (key << 2) + (key << 4);
        key ^= key >> 28;
        key += key << 31;
        return key;
    }

    public static bool Match(Literal x, Literal y)
    {
        if (x.AsArrayIndex(out uint index_x))
        {
            return y.AsArrayIndex(out uint index_y) && index_x == index_y;
        }
        return (x.IsRawString() && y.IsRawString() && x.AsRawString() == y.AsRawString()) ||
               (x.IsNumber() && y.IsNumber() && x.AsNumber() == y.AsNumber());
    }
}

// Base class for literals that need space in the type feedback vector.
public abstract class MaterializedLiteral(int pos, NodeType type) : Expression(pos, type)
{
    // A Materializedliteral is simple if the values consist of only
    // constants and simple object and array literals.
    public bool IsSimple()
    {
        if (IsArrayLiteral()) return ((ArrayLiteral)this).builder().is_simple();
        if (IsObjectLiteral()) return ((ObjectLiteral)this).builder().is_simple();
        return false;
    }

    internal bool NeedsInitialAllocationSite()
    {
        if (IsArrayLiteral()) return ((ArrayLiteral)this).builder().needs_initial_allocation_site();
        if (IsObjectLiteral()) return ((ObjectLiteral)this).builder().needs_initial_allocation_site();
        return false;
    }
}

// Node for capturing a regexp literal.
public sealed class RegExpLiteral : MaterializedLiteral
{
    private readonly int _flags;
    private readonly AstRawString _pattern;

    internal RegExpLiteral(AstRawString pattern, int flags, int pos) : base(pos, NodeType.RegExpLiteral)
    {
        _flags = flags;
        _pattern = pattern;
    }

    public AstRawString raw_pattern() => _pattern;
    public int flags() => _flags;
}

// Base class for Array and Object literals
public abstract class AggregateLiteral(int pos, NodeType type) : MaterializedLiteral(pos, type)
{
    [Flags]
    public enum Flags
    {
        kNoFlags = 0,
        kIsShallow = 1,
        kDisableMementos = 1 << 1,
        kNeedsInitialAllocationSite = 1 << 2,
        kIsShallowAndDisableMementos = kIsShallow | kDisableMementos,
    }
}

// Base class for build literal boilerplate, providing common code for handling
// nested subliterals.
public abstract class LiteralBoilerplateBuilder
{
    public enum DepthKind { kUninitialized, kShallow, kNotShallow }

    public const int kDepthKindBits = 2;

    private DepthKind _depth = DepthKind.kUninitialized;
    private bool _needsInitialAllocationSite;
    private bool _isSimple;
    private ElementsKind _boilerplateDescriptorKind = ElementsKind.FIRST_FAST_ELEMENTS_KIND;

    // The engine's boilerplate description (V8: boilerplate_description_).
    public object? boilerplate_description_ { get; set; }

    public bool is_initialized() => _depth != DepthKind.kUninitialized;
    public DepthKind depth() => _depth;

    public bool is_shallow() => depth() == DepthKind.kShallow;
    public bool needs_initial_allocation_site() => _needsInitialAllocationSite;

    public int ComputeFlagsBase(bool disable_mementos)
    {
        int flags = (int)AggregateLiteral.Flags.kNoFlags;
        if (is_shallow()) flags |= (int)AggregateLiteral.Flags.kIsShallow;
        if (disable_mementos) flags |= (int)AggregateLiteral.Flags.kDisableMementos;
        if (needs_initial_allocation_site())
        {
            flags |= (int)AggregateLiteral.Flags.kNeedsInitialAllocationSite;
        }
        return flags;
    }

    // An AggregateLiteral is simple if the values consist of only
    // constants and simple object and array literals.
    public bool is_simple() => _isSimple;

    public ElementsKind boilerplate_descriptor_kind() => _boilerplateDescriptorKind;

    protected void set_is_simple(bool is_simple) => _isSimple = is_simple;
    protected void set_boilerplate_descriptor_kind(ElementsKind kind) => _boilerplateDescriptorKind = kind;
    protected void set_depth(DepthKind depth) => _depth = depth;
    protected void set_needs_initial_allocation_site(bool required) => _needsInitialAllocationSite = required;

    // Populate the depth field and any flags the literal builder has
    public static void InitDepthAndFlags(MaterializedLiteral expr)
    {
        if (expr.IsArrayLiteral())
        {
            ((ArrayLiteral)expr).builder().InitDepthAndFlags();
            return;
        }
        if (expr.IsObjectLiteral())
        {
            ((ObjectLiteral)expr).builder().InitDepthAndFlags();
        }
    }

    // What GetBoilerplateValue(expression, isolate) produces, without the heap:
    // the literal's value, a nested boilerplate description, or uninitialized.
    public enum BoilerplateValueKind { Literal, ObjectBoilerplate, ArrayBoilerplate, Uninitialized }

    public static BoilerplateValueKind GetBoilerplateValueKind(Expression expression)
    {
        if (expression.IsLiteral()) return BoilerplateValueKind.Literal;
        if (expression.IsCompileTimeValue())
        {
            return expression.IsObjectLiteral() ? BoilerplateValueKind.ObjectBoilerplate : BoilerplateValueKind.ArrayBoilerplate;
        }
        return BoilerplateValueKind.Uninitialized;
    }
}

// Common supertype for ObjectLiteralProperty and ClassLiteralProperty
public abstract class LiteralProperty(Expression key, Expression value, bool is_computed_name)
{
    protected Expression _key = key;
    protected Expression _value = value;
    private readonly bool _isComputedName = is_computed_name;

    public Expression key() => _key;
    public Expression value() => _value;

    public bool is_computed_name() => _isComputedName;

    public bool NeedsSetFunctionName() =>
        is_computed_name() && (_value.IsAnonymousFunctionDefinition() ||
                               _value.IsConciseMethodDefinition() ||
                               _value.IsAccessorFunctionDefinition());
}

// Property is used for passing information
// about an object literal's properties from the parser
// to the code generator.
public sealed class ObjectLiteralProperty : LiteralProperty
{
    public enum Kind : byte
    {
        CONSTANT,             // Property with constant value (compile time).
        COMPUTED,             // Property with computed value (execution time).
        MATERIALIZED_LITERAL, // Property value is a materialized literal.
        GETTER,
        SETTER,               // Property is an accessor function.
        PROTOTYPE,            // Property is __proto__.
        SPREAD,
    }

    private readonly Kind _kind;
    private bool _emitStore = true;
    private bool _isFirstInstanceOfKey = true;
    private bool _isComplementaryAccessorCandidate;
    private int _valueIndex = -1;

    internal ObjectLiteralProperty(Expression key, Expression value, Kind kind, bool is_computed_name)
        : base(key, value, is_computed_name) => _kind = kind;

    internal ObjectLiteralProperty(AstValueFactory ast_value_factory, Expression key, Expression value, bool is_computed_name)
        : base(key, value, is_computed_name)
    {
        if (!is_computed_name && key.AsLiteral()!.IsRawString() &&
            key.AsLiteral()!.AsRawString() == ast_value_factory.proto_string())
        {
            _kind = Kind.PROTOTYPE;
        }
        else if (_value.AsMaterializedLiteral() != null)
        {
            _kind = Kind.MATERIALIZED_LITERAL;
        }
        else if (_value.IsLiteral())
        {
            _kind = Kind.CONSTANT;
        }
        else
        {
            _kind = Kind.COMPUTED;
        }
    }

    public Kind kind() => _kind;

    public bool IsCompileTimeValue() =>
        _kind == Kind.CONSTANT || (_kind == Kind.MATERIALIZED_LITERAL && _value.IsCompileTimeValue());

    public void set_emit_store(bool emit_store) => _emitStore = emit_store;
    public bool emit_store() => _emitStore;

    // For the first instance of a property name in an object literal (by
    // insertion order), this is the index of the last instance. The last
    // instance is the one that provides the value actually serialized into the
    // boilerplate.
    public void set_last_instance_index(int index) => _valueIndex = index;
    public int last_instance_index() => _valueIndex;

    public void set_is_first_instance_of_key(bool is_first) => _isFirstInstanceOfKey = is_first;
    public bool is_first_instance_of_key() => _isFirstInstanceOfKey;

    // For an instance of a property name in an object literal, this indicates
    // whether the last instance of this property name is an accessor that can
    // still be paired with a complementary accessor earlier in the object
    // literal.
    public void set_is_complementary_accessor_candidate(bool is_candidate) => _isComplementaryAccessorCandidate = is_candidate;
    public bool is_complementary_accessor_candidate() => _isComplementaryAccessorCandidate;

    public bool IsNullPrototype() => IsPrototype() && value().IsNullLiteral();
    public bool IsPrototype() => kind() == Kind.PROTOTYPE;
}

// class for build object boilerplate
public sealed class ObjectLiteralBoilerplateBuilder : LiteralBoilerplateBuilder
{
    // Maximum number of properties in copied object so that the properties store
    // will fit into new-space (ConstructorBuiltins::kMaximumClonedShallowObjectProperties
    // = NameDictionary::kMaxRegularCapacity / 3 * 2, kMaxRegularCapacity =
    // kMaxRegularHeapObjectSize / 32, kMaxRegularHeapObjectSize = 1 << 17).
    public const int kMaximumClonedShallowObjectProperties = (1 << 17) / 32 / 3 * 2;

    private readonly List<ObjectLiteralProperty> _properties;
    private readonly uint _boilerplateProperties;
    private bool _hasElements;
    private readonly bool _hasRestProperty;
    private bool _fastElements;
    private bool _hasNullPrototype;

    internal ObjectLiteralBoilerplateBuilder(List<ObjectLiteralProperty> properties, uint boilerplate_properties, bool has_rest_property)
    {
        _properties = properties;
        _boilerplateProperties = boilerplate_properties;
        _hasRestProperty = has_rest_property;
    }

    // Determines whether the {CreateShallowArrayLiteral} builtin can be used.
    public bool IsFastCloningSupported() =>
        // The CreateShallowObjectLiteratal builtin doesn't copy elements, and object
        // literals don't support copy-on-write (COW) elements for now.
        fast_elements() && is_shallow() && properties_count() <= kMaximumClonedShallowObjectProperties;

    public int properties_count() => (int)_boilerplateProperties;
    public List<ObjectLiteralProperty> properties() => _properties;
    public bool has_elements() => _hasElements;
    public bool has_rest_property() => _hasRestProperty;
    public bool fast_elements() => _fastElements;
    public bool has_null_prototype() => _hasNullPrototype;

    public bool is_empty() => !has_elements() && properties_count() == 0 && properties().Count == 0;

    // Assemble bitfield of flags for the CreateObjectLiteral helper.
    public int ComputeFlags(bool disable_mementos = false)
    {
        int flags = ComputeFlagsBase(disable_mementos);
        if (fast_elements()) flags |= (int)ObjectLiteral.Flags.kFastElements;
        if (has_null_prototype()) flags |= (int)ObjectLiteral.Flags.kHasNullPrototype;
        return flags;
    }

    public bool IsEmptyObjectLiteral() => is_empty() && !has_null_prototype();

    public int EncodeLiteralType()
    {
        int flags = (int)AggregateLiteral.Flags.kNoFlags;
        if (fast_elements()) flags |= (int)ObjectLiteral.Flags.kFastElements;
        if (has_null_prototype()) flags |= (int)ObjectLiteral.Flags.kHasNullPrototype;
        return flags;
    }

    private void InitFlagsForPendingNullPrototype(int i)
    {
        // We still check for __proto__:null after computed property names.
        for (; i < properties().Count; i++)
        {
            if (properties()[i].IsNullPrototype())
            {
                _hasNullPrototype = true;
                break;
            }
        }
    }

    // Populate the depth field and flags, returns the depth.
    public void InitDepthAndFlags()
    {
        if (is_initialized()) return;
        bool is_simple = true;
        bool has_seen_prototype = false;
        bool needs_initial_allocation_site = false;
        DepthKind depth_acc = DepthKind.kShallow;
        uint nof_properties = 0;
        uint elements = 0;
        uint max_element_index = 0;
        for (int i = 0; i < properties().Count; i++)
        {
            ObjectLiteralProperty property = properties()[i];
            if (property.IsPrototype())
            {
                has_seen_prototype = true;
                // __proto__:null has no side-effects and is set directly on the
                // boilerplate.
                if (property.IsNullPrototype())
                {
                    _hasNullPrototype = true;
                    continue;
                }
                is_simple = false;
                continue;
            }
            if (nof_properties == _boilerplateProperties)
            {
                is_simple = false;
                if (!has_seen_prototype) InitFlagsForPendingNullPrototype(i);
                break;
            }

            MaterializedLiteral? literal = property.value().AsMaterializedLiteral();
            if (literal != null)
            {
                LiteralBoilerplateBuilder.InitDepthAndFlags(literal);
                depth_acc = DepthKind.kNotShallow;
                needs_initial_allocation_site |= literal.NeedsInitialAllocationSite();
            }

            Literal key = property.key().AsLiteral()!;
            Expression value = property.value();

            bool is_compile_time_value = value.IsCompileTimeValue();
            is_simple = is_simple && is_compile_time_value;

            // Keep track of the number of elements in the object literal and
            // the largest element index.  If the largest element index is
            // much larger than the number of elements, creating an object
            // literal with fast elements will be a waste of space.
            if (key.AsArrayIndex(out uint element_index))
            {
                max_element_index = Math.Max(element_index, max_element_index);
                elements++;
            }

            nof_properties++;
        }

        set_depth(depth_acc);
        set_is_simple(is_simple);
        set_needs_initial_allocation_site(needs_initial_allocation_site);
        _hasElements = elements > 0;
        _fastElements = (max_element_index <= 32) || ((2 * (ulong)elements) >= max_element_index);
    }

    // The sizes BuildBoilerplateDescription allocates (boilerplate property
    // count and backing store size), computed as V8 does.
    public void ComputeBoilerplateDescriptionSizes(out int boilerplate_property_count, out int backing_store_size)
    {
        backing_store_size = 0;
        bool saw_computed_name = false;
        boilerplate_property_count = 0;

        for (int i = 0; i < properties().Count; i++)
        {
            ObjectLiteralProperty property = properties()[i];
            if (property.IsPrototype()) continue;

            if (property.is_computed_name())
            {
                saw_computed_name = true;
                backing_store_size++;
                continue;
            }

            Literal key = property.key().AsLiteral()!;
            if (saw_computed_name)
            {
                if (key.IsPropertyName()) backing_store_size++;
                continue;
            }

            if (property.is_first_instance_of_key())
            {
                boilerplate_property_count++;
                if (key.IsPropertyName()) backing_store_size++;
            }
        }
    }

    // The properties BuildBoilerplateDescription serializes, in order: for each
    // first instance of a key before the first computed name, the property
    // whose value is used (the last instance of that key).
    public List<ObjectLiteralProperty> BoilerplateProperties()
    {
        var result = new List<ObjectLiteralProperty>();
        for (int i = 0; i < properties().Count; i++)
        {
            ObjectLiteralProperty property = properties()[i];
            if (property.IsPrototype()) continue;
            if (property.is_computed_name()) break;
            if (property.emit_store())
            {
                if (!property.is_first_instance_of_key()) continue;
            }
            else
            {
                if (!property.is_first_instance_of_key()) continue;
                property = properties()[property.last_instance_index()];
            }
            result.Add(property);
        }
        return result;
    }
}

// An object literal has a boilerplate object that is used
// for minimizing the work when constructing it at runtime.
public sealed class ObjectLiteral : AggregateLiteral
{
    public new enum Flags
    {
        kFastElements = 1 << 3,
        kHasNullPrototype = 1 << 4,
    }

    private readonly List<ObjectLiteralProperty> _properties;
    private readonly Variable? _homeObject;
    private readonly ObjectLiteralBoilerplateBuilder _builder;

    internal ObjectLiteral(IReadOnlyList<ObjectLiteralProperty> properties, uint boilerplate_properties, int pos,
                           bool has_rest_property, Variable? home_object)
        : base(pos, NodeType.ObjectLiteral)
    {
        _properties = new List<ObjectLiteralProperty>(properties.Count);
        for (int i = 0; i < properties.Count; i++) _properties.Add(properties[i]);
        _homeObject = home_object;
        _builder = new ObjectLiteralBoilerplateBuilder(_properties, boilerplate_properties, has_rest_property);
    }

    public List<ObjectLiteralProperty> properties() => _properties;
    public ObjectLiteralBoilerplateBuilder builder() => _builder;
    public Variable? home_object() => _homeObject;

    // Mark all computed expressions that are bound to a key that
    // is shadowed by a later occurrence of the same key. For the
    // marked expressions, no store code is emitted.
    public void CalculateEmitStore()
    {
        const ObjectLiteralProperty.Kind GETTER = ObjectLiteralProperty.Kind.GETTER;
        const ObjectLiteralProperty.Kind SETTER = ObjectLiteralProperty.Kind.SETTER;

        var table = new Dictionary<LiteralKey, int>();

        // We iterate backwards, so the first property we see is the last one in
        // source order.
        for (int i = properties().Count - 1; i >= 0; i--)
        {
            ObjectLiteralProperty property = properties()[i];
            if (property.is_computed_name()) continue;
            if (property.IsPrototype()) continue;
            Literal literal = property.key().AsLiteral()!;

            var key = new LiteralKey(literal);
            if (!table.TryGetValue(key, out int previous_index))
            {
                // First time we see this key (it's the last property in the literal).
                table[key] = i;
                property.set_last_instance_index(i);
            }
            else
            {
                ObjectLiteralProperty previous_prop = properties()[previous_index];

                // Properties are deduplicated preserving source order. For a given key,
                // we only keep the last-occurring instance in the boilerplate. Earlier
                // instances must have their stores eliminated, unless they are a
                // complementary accessor to the last-occurring instance.
                int last_index = previous_prop.last_instance_index();
                bool is_candidate;
                if (last_index == previous_index)
                {
                    // This is the first duplicate we've found for this key. previous_prop
                    // is the absolute last instance in source order.
                    is_candidate = previous_prop.kind() == GETTER || previous_prop.kind() == SETTER;
                }
                else
                {
                    is_candidate = previous_prop.is_complementary_accessor_candidate();
                }

                if (is_candidate)
                {
                    ObjectLiteralProperty last_prop = properties()[last_index];
                    var last_kind = last_prop.kind();
                    bool complementary_accessors =
                        (property.kind() == GETTER && last_kind == SETTER) ||
                        (property.kind() == SETTER && last_kind == GETTER);

                    if (!complementary_accessors)
                    {
                        property.set_emit_store(false);
                        // If this duplicate is the same kind of accessor as the last one,
                        // it doesn't shield earlier properties, so we propagate the bit.
                        if (property.kind() == last_kind)
                        {
                            property.set_is_complementary_accessor_candidate(true);
                        }
                    }
                }
                else
                {
                    // No accessor candidate, so this duplicate definitely doesn't need to
                    // emit a store.
                    property.set_emit_store(false);
                }

                // Transition the previous instance. It's no longer the first instance.
                previous_prop.set_is_first_instance_of_key(false);

                // The current property (at index i) is now the earliest instance of this
                // key we've seen so far.
                property.set_last_instance_index(last_index);
                table[key] = i;
            }
        }
    }

    // Literal as a hash map key, with Literal::Hash / Literal::Match semantics.
    private readonly struct LiteralKey(Literal literal) : IEquatable<LiteralKey>
    {
        private readonly Literal _literal = literal;
        public bool Equals(LiteralKey other) => Literal.Match(_literal, other._literal);
        public override bool Equals(object? obj) => obj is LiteralKey k && Equals(k);
        public override int GetHashCode() => (int)_literal.Hash();
    }
}

// class for build boilerplate for array literal, including
// array_literal, spread call elements
public sealed class ArrayLiteralBoilerplateBuilder : LiteralBoilerplateBuilder
{
    // ConstructorBuiltins::kMaximumClonedShallowArrayElements =
    // JSArray::kInitialMaxFastElementArray = (kMaxRegularHeapObjectSize -
    // sizeof(FixedArray) - JSArray::kHeaderSize - sizeof(AllocationMemento))
    // >> kDoubleSizeLog2, with pointer compression: (131072 - 8 - 16 - 8) >> 3.
    public const int kMaximumClonedShallowArrayElements = ((1 << 17) - 8 - 16 - 8) >> 3;

    private readonly List<Expression> _values;
    private readonly int _firstSpreadIndex;

    // Public: the bytecode generator builds one for spread calls (V8's
    // zone()->New<ArrayLiteralBoilerplateBuilder>(elements, first_spread_index)).
    public ArrayLiteralBoilerplateBuilder(List<Expression> values, int first_spread_index)
    {
        _values = values;
        _firstSpreadIndex = first_spread_index;
    }

    public List<Expression> values() => _values;

    // Determines whether the {CreateShallowArrayLiteral} builtin can be used.
    public bool IsFastCloningSupported() =>
        depth() <= DepthKind.kShallow && _values.Count <= kMaximumClonedShallowArrayElements;

    // Assemble bitfield of flags for the CreateArrayLiteral helper.
    public int ComputeFlags(bool disable_mementos = false) => ComputeFlagsBase(disable_mementos);

    public int first_spread_index() => _firstSpreadIndex;

    // The number of leading elements that go into the boilerplate.
    public int constants_length() => _firstSpreadIndex >= 0 ? _firstSpreadIndex : _values.Count;

    // Populate the depth field and flags
    public void InitDepthAndFlags()
    {
        if (is_initialized()) return;

        int constants_length = _firstSpreadIndex >= 0 ? _firstSpreadIndex : _values.Count;

        // Fill in the literals.
        bool is_simple = _firstSpreadIndex < 0;
        bool is_holey = false;
        ElementsKind kind = ElementsKind.FIRST_FAST_ELEMENTS_KIND;
        DepthKind depth_acc = DepthKind.kShallow;
        int array_index = 0;
        for (; array_index < constants_length; array_index++)
        {
            Expression element = _values[array_index];
            MaterializedLiteral? materialized_literal = element.AsMaterializedLiteral();
            if (materialized_literal != null)
            {
                LiteralBoilerplateBuilder.InitDepthAndFlags(materialized_literal);
                depth_acc = DepthKind.kNotShallow;
            }

            if (!element.IsCompileTimeValue())
            {
                is_simple = false;

                // Don't change kind here: non-compile time values resolve to an unknown
                // elements kind, so we allow them to be considered as any one of them.
            }
            else
            {
                Literal? literal = element.AsLiteral();

                if (literal == null)
                {
                    // Only arrays and objects are compile-time values but not (primitive)
                    // literals.
                    kind = ElementsKind.PACKED_ELEMENTS;
                }
                else
                {
                    switch (literal.type())
                    {
                        case Literal.Type.kTheHole:
                            is_holey = true;
                            // The hole is allowed in holey double arrays (and holey Smi
                            // arrays), so ignore it as far as is_all_number is concerned.
                            break;
                        case Literal.Type.kHeapNumber:
                            if (kind == ElementsKind.PACKED_SMI_ELEMENTS) kind = ElementsKind.PACKED_DOUBLE_ELEMENTS;
                            break;
                        case Literal.Type.kSmi:
                            break;
                        case Literal.Type.kBigInt:
                        case Literal.Type.kString:
                        case Literal.Type.kConsString:
                        case Literal.Type.kBoolean:
                        case Literal.Type.kUndefined:
                        case Literal.Type.kNull:
                            kind = ElementsKind.PACKED_ELEMENTS;
                            break;
                    }
                }
            }
        }

        if (is_holey)
        {
            kind = ElementsKinds.GetHoleyElementsKind(kind);
        }

        set_depth(depth_acc);
        set_is_simple(is_simple);
        set_boilerplate_descriptor_kind(kind);

        // Array literals always need an initial allocation site to properly track
        // elements transitions.
        set_needs_initial_allocation_site(true);
    }
}

// An array literal has a literals object that is used
// for minimizing the work when constructing it at runtime.
public sealed class ArrayLiteral : AggregateLiteral
{
    private readonly List<Expression> _values;
    private readonly ArrayLiteralBoilerplateBuilder _builder;

    internal ArrayLiteral(IReadOnlyList<Expression> values, int first_spread_index, int pos) : base(pos, NodeType.ArrayLiteral)
    {
        _values = new List<Expression>(values.Count);
        for (int i = 0; i < values.Count; i++) _values.Add(values[i]);
        _builder = new ArrayLiteralBoilerplateBuilder(_values, first_spread_index);
    }

    public List<Expression> values() => _values;
    public ArrayLiteralBoilerplateBuilder builder() => _builder;
}

public enum HoleCheckMode { kRequired, kElided }

public sealed class ThisExpression : Expression
{
    internal ThisExpression(int pos) : base(pos, NodeType.ThisExpression) { }
}

public sealed class VariableProxy : Expression, IThreadedListNode<VariableProxy>
{
    private AstRawString _rawName;
    private Variable? _var;
    private VariableProxy? _nextUnresolved;

    private bool _isAssigned;
    private bool _isResolved;
    private bool _isRemovedFromUnresolved;
    private bool _isNewTarget;
    private bool _isHomeObject;
    private HoleCheckMode _holeCheckMode;

    internal VariableProxy(Variable var, int start_position) : base(start_position, NodeType.VariableProxy)
    {
        _rawName = var.raw_name();
        _holeCheckMode = HoleCheckMode.kElided;
        BindTo(var);
    }

    internal VariableProxy(AstRawString name, VariableKind variable_kind, int start_position)
        : base(start_position, NodeType.VariableProxy)
    {
        _rawName = name;
        _holeCheckMode = HoleCheckMode.kElided;
    }

    internal VariableProxy(VariableProxy copy_from) : base(copy_from.position(), NodeType.VariableProxy)
    {
        _isAssigned = copy_from._isAssigned;
        _isResolved = copy_from._isResolved;
        _isRemovedFromUnresolved = copy_from._isRemovedFromUnresolved;
        _isNewTarget = copy_from._isNewTarget;
        _isHomeObject = copy_from._isHomeObject;
        _holeCheckMode = copy_from._holeCheckMode;
        if (copy_from.is_parenthesized()) mark_parenthesized();
        _rawName = copy_from._rawName;
    }

    VariableProxy? IThreadedListNode<VariableProxy>.NextNode { get => _nextUnresolved; set => _nextUnresolved = value; }

    public new bool IsValidReferenceExpression() => !is_new_target();

    public AstRawString raw_name() => _isResolved ? _var!.raw_name() : _rawName;

    public Variable var() => _var!;

    public void set_var(Variable v) => _var = v;

    public Scanner.Location location() => new(position(), position() + raw_name().length());

    public bool is_assigned() => _isAssigned;

    public void set_is_assigned()
    {
        _isAssigned = true;
        if (is_resolved())
        {
            var().SetMaybeAssigned();
        }
    }

    public void clear_is_assigned() => _isAssigned = false;

    public bool is_resolved() => _isResolved;
    public void set_is_resolved() => _isResolved = true;

    public bool is_new_target() => _isNewTarget;
    public void set_is_new_target() => _isNewTarget = true;

    public HoleCheckMode hole_check_mode() => _holeCheckMode;
    public void set_needs_hole_check() => _holeCheckMode = HoleCheckMode.kRequired;
    public void clear_needs_hole_check(Variable var) => _holeCheckMode = HoleCheckMode.kElided;

    public new bool IsPrivateName() => raw_name().IsPrivateName();

    public enum BindingMode
    {
        kMarkUse,
        kNoMarkUse,
    }

    // Bind this proxy to the variable var.
    public void BindTo(Variable var, BindingMode mode = BindingMode.kMarkUse)
    {
        set_var(var);
        set_is_resolved();
        if (mode == BindingMode.kMarkUse)
        {
            var.set_is_used();
        }
        if (is_assigned()) var.SetMaybeAssigned();
    }

    public VariableProxy? next_unresolved() => _nextUnresolved;
    public bool is_removed_from_unresolved() => _isRemovedFromUnresolved;
    public void mark_removed_from_unresolved() => _isRemovedFromUnresolved = true;

    public bool is_home_object() => _isHomeObject;
    public void set_is_home_object() => _isHomeObject = true;

    public override string ToString() => raw_name().ToString();
}

// Wraps an optional chain to provide a wrapper for jump labels.
public sealed class OptionalChain : Expression
{
    private readonly Expression _expression;

    internal OptionalChain(Expression expression) : base(0, NodeType.OptionalChain) => _expression = expression;

    public Expression expression() => _expression;
}

// Assignments to a property will use one of several types of property access.
// Otherwise, the assignment is to a non-property (a global, a local slot, a
// parameter slot, or a destructuring pattern).
public enum AssignType
{
    NON_PROPERTY,              // destructuring
    NAMED_PROPERTY,            // obj.key
    KEYED_PROPERTY,            // obj[key] and obj.#key when #key is a private field
    NAMED_SUPER_PROPERTY,      // super.key
    KEYED_SUPER_PROPERTY,      // super[key]
    PRIVATE_METHOD,            // obj.#key: #key is a private method
    PRIVATE_GETTER_ONLY,       // obj.#key: #key only has a getter defined
    PRIVATE_SETTER_ONLY,       // obj.#key: #key only has a setter defined
    PRIVATE_GETTER_AND_SETTER, // obj.#key: #key has both accessors defined
    PRIVATE_DEBUG_DYNAMIC,     // obj.#key: #key is private that requires dynamic
                               // lookup in debug-evaluate.
}

public sealed class Property : Expression
{
    private readonly Expression _obj;
    private readonly Expression _key;
    private readonly bool _isOptionalChainLink;

    internal Property(Expression obj, Expression key, int pos, bool optional_chain) : base(pos, NodeType.Property)
    {
        _obj = obj;
        _key = key;
        _isOptionalChainLink = optional_chain;
    }

    public bool is_optional_chain_link() => _isOptionalChainLink;

    public new bool IsValidReferenceExpression() => true;

    public Expression obj() => _obj;
    public Expression key() => _key;

    public bool IsSuperAccess() => obj().IsSuperPropertyReference();
    public bool IsPrivateReference() => key().IsPrivateName();

    // Returns the properties assign type.
    public static AssignType GetAssignType(Property? property)
    {
        if (property == null) return AssignType.NON_PROPERTY;
        if (property.IsPrivateReference())
        {
            VariableProxy proxy = property.key().AsVariableProxy()!;
            Variable var = proxy.var();

            switch (var.mode())
            {
                case VariableMode.PrivateMethod:
                    return AssignType.PRIVATE_METHOD;
                case VariableMode.Const:
                    return AssignType.KEYED_PROPERTY; // Use KEYED_PROPERTY for private fields.
                case VariableMode.PrivateGetterOnly:
                    return AssignType.PRIVATE_GETTER_ONLY;
                case VariableMode.PrivateSetterOnly:
                    return AssignType.PRIVATE_SETTER_ONLY;
                case VariableMode.PrivateGetterAndSetter:
                    return AssignType.PRIVATE_GETTER_AND_SETTER;
                case VariableMode.Dynamic:
                    // From debug-evaluate.
                    return AssignType.PRIVATE_DEBUG_DYNAMIC;
                default:
                    throw new InvalidOperationException("UNREACHABLE");
            }
        }
        bool super_access = property.IsSuperAccess();
        return property.key().IsPropertyName()
            ? (super_access ? AssignType.NAMED_SUPER_PROPERTY : AssignType.NAMED_PROPERTY)
            : (super_access ? AssignType.KEYED_SUPER_PROPERTY : AssignType.KEYED_PROPERTY);
    }
}

public abstract class CallBase : Expression
{
    public enum SpreadPosition { kNoSpread, kHasFinalSpread, kHasNonFinalSpread }

    protected readonly Expression _expression;
    protected readonly List<Expression> _arguments;
    private readonly SpreadPosition _spreadPosition;

    protected CallBase(NodeType type, Expression expression, IReadOnlyList<Expression> arguments, int pos, bool has_spread)
        : base(pos, type)
    {
        _expression = expression;
        _arguments = new List<Expression>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++) _arguments.Add(arguments[i]);
        _spreadPosition = has_spread ? ComputeSpreadPosition() : SpreadPosition.kNoSpread;
    }

    public Expression expression() => _expression;
    public List<Expression> arguments() => _arguments;

    public SpreadPosition spread_position() => _spreadPosition;

    // Only valid to be called if there is a spread in arguments_.
    private SpreadPosition ComputeSpreadPosition()
    {
        int arguments_length = _arguments.Count;
        int first_spread_index = 0;
        for (; first_spread_index < arguments_length; first_spread_index++)
        {
            if (_arguments[first_spread_index].IsSpread()) break;
        }
        return first_spread_index == arguments_length - 1 ? SpreadPosition.kHasFinalSpread : SpreadPosition.kHasNonFinalSpread;
    }
}

public sealed class Call : CallBase
{
    public enum CallType
    {
        GLOBAL_CALL,
        WITH_CALL,
        NAMED_PROPERTY_CALL,
        KEYED_PROPERTY_CALL,
        NAMED_OPTIONAL_CHAIN_PROPERTY_CALL,
        KEYED_OPTIONAL_CHAIN_PROPERTY_CALL,
        NAMED_SUPER_PROPERTY_CALL,
        KEYED_SUPER_PROPERTY_CALL,
        PRIVATE_CALL,
        PRIVATE_OPTIONAL_CHAIN_CALL,
        SUPER_CALL,
        OTHER_CALL,
    }

    public enum TaggedTemplateTag { kTrue }

    // EvalScopeInfoIndexField is 20 bits wide.
    public const int kEvalScopeInfoIndexBits = 20;
    public static bool EvalScopeInfoIndexFieldIsValid(int value) => (uint)value < (1u << kEvalScopeInfoIndexBits);

    private readonly bool _isTaggedTemplate;
    private readonly bool _isOptionalChainLink;
    private uint _evalScopeInfoIndex;

    internal Call(Expression expression, IReadOnlyList<Expression> arguments, int pos, bool has_spread,
                  int eval_scope_info_index, bool optional_chain)
        : base(NodeType.Call, expression, arguments, pos, has_spread)
    {
        _isTaggedTemplate = false;
        _isOptionalChainLink = optional_chain;
        _evalScopeInfoIndex = (uint)eval_scope_info_index & ((1u << kEvalScopeInfoIndexBits) - 1);
    }

    internal Call(Expression expression, IReadOnlyList<Expression> arguments, int pos, TaggedTemplateTag tag)
        : base(NodeType.Call, expression, arguments, pos, false)
    {
        _isTaggedTemplate = true;
        _isOptionalChainLink = false;
        _evalScopeInfoIndex = 0;
    }

    public bool is_possibly_eval() => _evalScopeInfoIndex > 0;
    public bool is_tagged_template() => _isTaggedTemplate;
    public bool is_optional_chain_link() => _isOptionalChainLink;
    public uint eval_scope_info_index() => _evalScopeInfoIndex;

    public void adjust_eval_scope_info_index(int delta) =>
        _evalScopeInfoIndex = (uint)(eval_scope_info_index() + delta) & ((1u << kEvalScopeInfoIndexBits) - 1);

    // Helpers to determine how to handle the call.
    public CallType GetCallType()
    {
        VariableProxy? proxy = expression().AsVariableProxy();
        if (proxy != null)
        {
            if (proxy.var().IsUnallocated())
            {
                return CallType.GLOBAL_CALL;
            }
            else if (proxy.var().IsLookupSlot())
            {
                // Calls going through 'with' always use VariableMode::kDynamic rather
                // than VariableMode::kDynamicLocal or VariableMode::kDynamicGlobal.
                return proxy.var().mode() == VariableMode.Dynamic ? CallType.WITH_CALL : CallType.OTHER_CALL;
            }
        }

        if (expression().IsSuperCallReference()) return CallType.SUPER_CALL;

        Property? property = expression().AsProperty();
        bool is_optional_chain = false;
        if (property == null && expression().IsOptionalChain())
        {
            is_optional_chain = true;
            property = expression().AsOptionalChain()!.expression().AsProperty();
        }
        if (property != null)
        {
            if (property.IsPrivateReference())
            {
                if (is_optional_chain) return CallType.PRIVATE_OPTIONAL_CHAIN_CALL;
                return CallType.PRIVATE_CALL;
            }
            bool is_super = property.IsSuperAccess();
            // `super?.` is not syntactically valid, so a property load cannot be both
            // super and an optional chain.
            if (property.key().IsPropertyName())
            {
                if (is_super) return CallType.NAMED_SUPER_PROPERTY_CALL;
                if (is_optional_chain) return CallType.NAMED_OPTIONAL_CHAIN_PROPERTY_CALL;
                return CallType.NAMED_PROPERTY_CALL;
            }
            else
            {
                if (is_super) return CallType.KEYED_SUPER_PROPERTY_CALL;
                if (is_optional_chain) return CallType.KEYED_OPTIONAL_CHAIN_PROPERTY_CALL;
                return CallType.KEYED_PROPERTY_CALL;
            }
        }

        return CallType.OTHER_CALL;
    }
}

public sealed class CallNew : CallBase
{
    internal CallNew(Expression expression, IReadOnlyList<Expression> arguments, int pos, bool has_spread)
        : base(NodeType.CallNew, expression, arguments, pos, has_spread) { }
}

// SuperCallForwardArgs is not utterable in JavaScript. It is used to
// implement the default derived constructor, which forwards all arguments to
// the super constructor without going through the user-visible spread
// machinery.
public sealed class SuperCallForwardArgs : Expression
{
    private readonly SuperCallReference _expression;

    internal SuperCallForwardArgs(SuperCallReference expression, int pos) : base(pos, NodeType.SuperCallForwardArgs) => _expression = expression;

    public SuperCallReference expression() => _expression;
}

// The CallRuntime class does not represent any official JavaScript
// language construct. Instead it is used to call a runtime function
// with a set of arguments.
public sealed class CallRuntime : Expression
{
    private readonly RuntimeFunction _function;
    private readonly List<Expression> _arguments;

    internal CallRuntime(RuntimeFunction function, IReadOnlyList<Expression> arguments, int pos) : base(pos, NodeType.CallRuntime)
    {
        _function = function;
        _arguments = new List<Expression>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++) _arguments.Add(arguments[i]);
    }

    public List<Expression> arguments() => _arguments;
    public RuntimeFunction function() => _function;
}

public sealed class UnaryOperation : Expression
{
    private readonly Token _op;
    private readonly Expression _expression;

    internal UnaryOperation(Token op, Expression expression, int pos) : base(pos, NodeType.UnaryOperation)
    {
        _op = op;
        _expression = expression;
    }

    public Token op() => _op;
    public Expression expression() => _expression;
}

public sealed class BinaryOperation : Expression
{
    private readonly Token _op;
    private readonly Expression _left;
    private Expression _right;

    internal BinaryOperation(Token op, Expression left, Expression right, int pos) : base(pos, NodeType.BinaryOperation)
    {
        _op = op;
        _left = left;
        _right = right;
    }

    public Token op() => _op;
    public Expression left() => _left;
    public Expression right() => _right;

    public void UpdateRight(Expression expr) => _right = expr;

    private static bool IsCommutativeOperationWithSmiLiteral(Token op) =>
        // Add is not commutative due to potential for string addition.
        op == Token.Mul || op == Token.BitAnd || op == Token.BitOr || op == Token.BitXor;

    // Check for the pattern: x + 1.
    private static bool MatchSmiLiteralOperation(Expression left, Expression right, out Expression? expr, out int literal)
    {
        if (right.IsSmiLiteral())
        {
            expr = left;
            literal = right.AsLiteral()!.AsSmiLiteral();
            return true;
        }
        expr = null;
        literal = 0;
        return false;
    }

    // Returns true if one side is a Smi literal, returning the other side's
    // sub-expression in |subexpr| and the literal Smi in |literal|.
    public bool IsSmiLiteralOperation(out Expression? subexpr, out int literal) =>
        MatchSmiLiteralOperation(_left, _right, out subexpr, out literal) ||
        (IsCommutativeOperationWithSmiLiteral(op()) && MatchSmiLiteralOperation(_right, _left, out subexpr, out literal));
}

public sealed class NaryOperation : Expression
{
    private readonly Token _op;
    private readonly Expression _first;

    // Nary operations store the first (lhs) child expression inline, and the
    // child expressions (rhs of each op) are stored out-of-line, along with
    // their operation's position. Note that the Nary operation expression's
    // position has no meaning.
    private struct NaryOperationEntry(Expression e, int pos)
    {
        public Expression expression = e;
        public readonly int op_position = pos;
    }

    private readonly List<NaryOperationEntry> _subsequent;

    internal NaryOperation(Token op, Expression first, int initial_subsequent_size)
        : base(first.position(), NodeType.NaryOperation)
    {
        _op = op;
        _first = first;
        _subsequent = new List<NaryOperationEntry>(initial_subsequent_size);
    }

    public Token op() => _op;
    public Expression first() => _first;
    public Expression subsequent(int index) => _subsequent[index].expression;
    public int subsequent_length() => _subsequent.Count;
    public int subsequent_op_position(int index) => _subsequent[index].op_position;

    public void AddSubsequent(Expression expr, int pos) => _subsequent.Add(new NaryOperationEntry(expr, pos));

    public Expression last() => _subsequent[^1].expression;

    public void UpdateLast(Expression expr)
    {
        var e = _subsequent[^1];
        e.expression = expr;
        _subsequent[^1] = e;
    }
}

public sealed class CountOperation : Expression
{
    private readonly bool _isPrefix;
    private readonly Token _op;
    private readonly Expression _expression;

    internal CountOperation(Token op, bool is_prefix, Expression expr, int pos) : base(pos, NodeType.CountOperation)
    {
        _isPrefix = is_prefix;
        _op = op;
        _expression = expr;
    }

    public bool is_prefix() => _isPrefix;
    public bool is_postfix() => !is_prefix();
    public Token op() => _op;
    public Expression expression() => _expression;
}

public sealed class CompareOperation : Expression
{
    private readonly Token _op;
    private readonly Expression _left;
    private readonly Expression _right;

    internal CompareOperation(Token op, Expression left, Expression right, int pos) : base(pos, NodeType.CompareOperation)
    {
        _op = op;
        _left = left;
        _right = right;
    }

    public Token op() => _op;
    public Expression left() => _left;
    public Expression right() => _right;

    private static bool IsVoidOfLiteral(Expression expr)
    {
        UnaryOperation? maybe_unary = expr.AsUnaryOperation();
        return maybe_unary != null && maybe_unary.op() == Token.Void && maybe_unary.expression().IsLiteral();
    }

    private static bool MatchLiteralStrictCompareBoolean(Expression left, Token op, Expression right, ref Expression? expr, ref Literal? literal)
    {
        if (left.IsBooleanLiteral() && op == Token.EqStrict)
        {
            expr = right;
            literal = left.AsLiteral();
            return true;
        }
        return false;
    }

    // Match special cases.
    public bool IsLiteralStrictCompareBoolean(out Expression? expr, out Literal? literal)
    {
        expr = null;
        literal = null;
        return MatchLiteralStrictCompareBoolean(_left, op(), _right, ref expr, ref literal) ||
               MatchLiteralStrictCompareBoolean(_right, op(), _left, ref expr, ref literal);
    }

    // Check for the pattern: void <literal> equals <expression> or
    // undefined equals <expression>
    private static bool MatchLiteralCompareUndefined(Expression left, Token op, Expression right, ref Expression? expr)
    {
        if (IsVoidOfLiteral(left) && Token.IsEqualityOp(op))
        {
            expr = right;
            return true;
        }
        if (left.IsUndefinedLiteral() && Token.IsEqualityOp(op))
        {
            expr = right;
            return true;
        }
        return false;
    }

    public bool IsLiteralCompareUndefined(out Expression? expr)
    {
        expr = null;
        return MatchLiteralCompareUndefined(_left, op(), _right, ref expr) ||
               MatchLiteralCompareUndefined(_right, op(), _left, ref expr);
    }

    // Check for the pattern: null equals <expression>
    private static bool MatchLiteralCompareNull(Expression left, Token op, Expression right, ref Expression? expr)
    {
        if (left.IsNullLiteral() && Token.IsEqualityOp(op))
        {
            expr = right;
            return true;
        }
        return false;
    }

    public bool IsLiteralCompareNull(out Expression? expr)
    {
        expr = null;
        return MatchLiteralCompareNull(_left, op(), _right, ref expr) ||
               MatchLiteralCompareNull(_right, op(), _left, ref expr);
    }

    private static bool MatchLiteralCompareEqualVariable(Expression left, Token op, Expression right, ref Expression? expr, ref Literal? literal)
    {
        if (Token.IsEqualityOp(op) && left.AsVariableProxy() != null && right.IsStringLiteral())
        {
            expr = left.AsVariableProxy();
            literal = right.AsLiteral();
            return true;
        }
        return false;
    }

    public bool IsLiteralCompareEqualVariable(out Expression? expr, out Literal? literal)
    {
        expr = null;
        literal = null;
        return MatchLiteralCompareEqualVariable(_left, op(), _right, ref expr, ref literal) ||
               MatchLiteralCompareEqualVariable(_right, op(), _left, ref expr, ref literal);
    }
}

public sealed class Spread : Expression
{
    private readonly int _exprPos;
    private readonly Expression _expression;

    internal Spread(Expression expression, int pos, int expr_pos) : base(pos, NodeType.Spread)
    {
        _exprPos = expr_pos;
        _expression = expression;
    }

    public Expression expression() => _expression;
    public int expression_position() => _exprPos;
}

public sealed class ConditionalChain : Expression
{
    // Conditional Chain Expression stores the conditional chain entries out of
    // line, along with their operation's position. The else expression is stored
    // inline. This Expression is reserved for ternary operations that have more
    // than one conditional chain entry. For ternary operations with only one
    // conditional chain entry, the Conditional Expression is used instead.
    private readonly struct ConditionalChainEntry(Expression cond, Expression then, int pos)
    {
        public readonly Expression condition = cond;
        public readonly Expression then_expression = then;
        public readonly int condition_position = pos;
    }

    private readonly List<ConditionalChainEntry> _entries;
    private Expression? _elseExpression;

    internal ConditionalChain(int initial_size, int pos) : base(pos, NodeType.ConditionalChain)
    {
        _entries = new List<ConditionalChainEntry>(initial_size);
    }

    public Expression condition_at(int index) => _entries[index].condition;
    public Expression then_expression_at(int index) => _entries[index].then_expression;
    public int condition_position_at(int index) => _entries[index].condition_position;
    public int conditional_chain_length() => _entries.Count;
    public Expression else_expression() => _elseExpression!;
    public void set_else_expression(Expression s) => _elseExpression = s;

    public void AddChainEntry(Expression cond, Expression then, int pos) => _entries.Add(new ConditionalChainEntry(cond, then, pos));
}

public sealed class Conditional : Expression
{
    private readonly Expression _condition;
    private readonly Expression _thenExpression;
    private readonly Expression _elseExpression;

    internal Conditional(Expression condition, Expression then_expression, Expression else_expression, int position)
        : base(position, NodeType.Conditional)
    {
        _condition = condition;
        _thenExpression = then_expression;
        _elseExpression = else_expression;
    }

    public Expression condition() => _condition;
    public Expression then_expression() => _thenExpression;
    public Expression else_expression() => _elseExpression;
}

public class Assignment : Expression
{
    private readonly Token _op;
    private readonly Expression _target;
    private readonly Expression _value;
    private LookupHoistingMode _lookupHoistingMode;

    internal Assignment(NodeType node_type, Token op, Expression target, Expression value, int pos) : base(pos, node_type)
    {
        _op = op;
        _target = target;
        _value = value;
    }

    public Token op() => _op;
    public Expression target() => _target;
    public Expression value() => _value;

    // The assignment was generated as part of block-scoped sloppy-mode
    // function hoisting, see
    // https://tc39.es/ecma262/#sec-block-level-function-declarations-web-legacy-compatibility-semantics
    public LookupHoistingMode lookup_hoisting_mode() => _lookupHoistingMode;
    public void set_lookup_hoisting_mode(LookupHoistingMode mode) => _lookupHoistingMode = mode;
}

public sealed class CompoundAssignment : Assignment
{
    private readonly BinaryOperation _binaryOperation;

    internal CompoundAssignment(Token op, Expression target, Expression value, int pos, BinaryOperation binary_operation)
        : base(NodeType.CompoundAssignment, op, target, value, pos) => _binaryOperation = binary_operation;

    public BinaryOperation binary_operation() => _binaryOperation;
}

// There are several types of Suspend node:
//
// Yield
// YieldStar
// Await
//
// Our Yield is different from the JS yield in that it "returns" its argument as
// is, without wrapping it in an iterator result object.  Such wrapping, if
// desired, must be done beforehand (see the parser).
public abstract class Suspend : Expression
{
    // With {kNoControl}, the {Suspend} behaves like yield, except that it never
    // throws and never causes the current generator to return. This is used to
    // desugar yield*.
    public enum OnAbruptResume { kOnExceptionThrow, kNoControl }

    private readonly Expression _expression;
    private readonly OnAbruptResume _onAbruptResume;

    private protected Suspend(NodeType node_type, Expression expression, int pos, OnAbruptResume on_abrupt_resume)
        : base(pos, node_type)
    {
        _expression = expression;
        _onAbruptResume = on_abrupt_resume;
    }

    public Expression expression() => _expression;
    public OnAbruptResume on_abrupt_resume() => _onAbruptResume;
}

public sealed class Yield : Suspend
{
    internal Yield(Expression expression, int pos, OnAbruptResume on_abrupt_resume)
        : base(NodeType.Yield, expression, pos, on_abrupt_resume) { }
}

public sealed class YieldStar : Suspend
{
    internal YieldStar(Expression expression, int pos)
        : base(NodeType.YieldStar, expression, pos, OnAbruptResume.kNoControl) { }
}

public sealed class Await : Suspend
{
    internal Await(Expression expression, int pos)
        : base(NodeType.Await, expression, pos, OnAbruptResume.kOnExceptionThrow) { }
}

public sealed class Throw : Expression
{
    private readonly Expression _exception;

    internal Throw(Expression exception, int pos) : base(pos, NodeType.Throw) => _exception = exception;

    public Expression exception() => _exception;
}

public sealed class FunctionLiteral : Expression, V8Sharp.Parsing.IParserFunctionLiteral
{
    public enum ParameterFlag : byte
    {
        kNoDuplicateParameters,
        kHasDuplicateParameters,
    }

    public enum EagerCompileHint : byte { kShouldEagerCompile, kShouldLazyCompile }

    // expected_property_count_ is the sum of instance fields and properties.
    // It can vary depending on whether a function is lazily or eagerly parsed.
    private int _expectedPropertyCount;
    private readonly int _parameterCount;
    private readonly int _functionLength;
    private int _functionTokenPosition;
    private int _suspendCount;
    private int _functionLiteralId;

    private AstConsString? _rawName;
    private readonly DeclarationScope _scope;
    private readonly List<Statement> _body;
    private AstConsString? _rawInferredName;
    private readonly ProducedPreparseData? _producedPreparseData;

    private readonly FunctionSyntaxKind _syntaxKind;
    private bool _pretenure;
    private readonly bool _hasDuplicateParameters;
    private bool _requiresInstanceMembersInitializer;
    private bool _hasStaticPrivateMethodsOrAccessors;
    private readonly bool _hasBraces;
    private bool _shouldParallelCompile;

    // The engine's SharedFunctionInfo, once created (V8: shared_function_info_).
    public object? shared_function_info_ { get; private set; }

    internal FunctionLiteral(AstConsString? name, AstValueFactory ast_value_factory, DeclarationScope scope,
                             IReadOnlyList<Statement> body, int expected_property_count, int parameter_count,
                             int function_length, FunctionSyntaxKind function_syntax_kind,
                             ParameterFlag has_duplicate_parameters, EagerCompileHint eager_compile_hint,
                             int position, bool has_braces, int function_literal_id,
                             ProducedPreparseData? produced_preparse_data = null)
        : base(position, NodeType.FunctionLiteral)
    {
        _expectedPropertyCount = expected_property_count;
        _parameterCount = parameter_count;
        _functionLength = function_length;
        _functionTokenPosition = kNoSourcePosition;
        _suspendCount = 0;
        _functionLiteralId = function_literal_id;
        _rawName = name;
        _scope = scope;
        _body = new List<Statement>(body.Count);
        for (int i = 0; i < body.Count; i++) _body.Add(body[i]);
        _rawInferredName = ast_value_factory.empty_cons_string();
        _producedPreparseData = produced_preparse_data;
        _syntaxKind = function_syntax_kind;
        _hasDuplicateParameters = has_duplicate_parameters == ParameterFlag.kHasDuplicateParameters;
        _hasBraces = has_braces;
        if (eager_compile_hint == EagerCompileHint.kShouldEagerCompile) SetShouldEagerCompile();
    }

    // A null name means that the function does not have a shared name (i.e.
    // the name will be set dynamically after creation of the function closure).
    public bool has_shared_name() => _rawName != null;
    public AstConsString? raw_name() => _rawName;
    public void set_raw_name(AstConsString? name) => _rawName = name;
    public DeclarationScope scope() => _scope;
    public bool is_hoisted_in_context() => _scope.is_hoisted_in_context();
    public List<Statement> body() => _body;
    public void set_function_token_position(int pos) => _functionTokenPosition = pos;
    public int function_token_position() => _functionTokenPosition;
    public int start_position() => scope().start_position();
    public int end_position() => scope().end_position();
    public bool is_anonymous_expression() => syntax_kind() == FunctionSyntaxKind.AnonymousExpression;

    public bool is_toplevel() => function_literal_id() == kFunctionLiteralIdTopLevel;
    public LanguageMode language_mode() => scope().language_mode();

    public void add_expected_properties(int number_properties) => _expectedPropertyCount += number_properties;
    public int expected_property_count() => _expectedPropertyCount;
    public int parameter_count() => _parameterCount;
    public int function_length() => _functionLength;

    public bool AllowsLazyCompilation() => scope().AllowsLazyCompilation();

    public bool CanSuspend() => suspend_count() > 0;

    // Returns either name or inferred name as a string.
    public string GetDebugName()
    {
        AstConsString? cons_string;
        if (_rawName != null && !_rawName.IsEmpty())
        {
            cons_string = _rawName;
        }
        else if (_rawInferredName != null && !_rawInferredName.IsEmpty())
        {
            cons_string = _rawInferredName;
        }
        else
        {
            return string.Empty;
        }

        var result = new System.Text.StringBuilder();
        foreach (AstRawString s in cons_string.ToRawStrings())
        {
            // TODO(rmcilroy): Deal with two-character strings.
            if (!s.is_one_byte()) break;
            result.Append(s.Value);
        }
        return result.ToString();
    }

    public void set_shared_function_info(object shared_function_info) => shared_function_info_ = shared_function_info;

    public AstConsString? raw_inferred_name() => _rawInferredName;

    // This should only be called if we don't have a shared function info yet.
    public void set_raw_inferred_name(AstConsString raw_inferred_name)
    {
        _rawInferredName = raw_inferred_name;
        scope().set_has_inferred_function_name(true);
    }

    public bool pretenure() => _pretenure;
    public void set_pretenure() => _pretenure = true;

    public bool has_duplicate_parameters() => _hasDuplicateParameters;

    public bool should_parallel_compile() => _shouldParallelCompile;
    public void set_should_parallel_compile() => _shouldParallelCompile = true;

    // This is used as a heuristic on when to eagerly compile a function
    // literal. We consider the following constructs as hints that the
    // function will be called immediately:
    // - (function() { ... })();
    // - var x = function() { ... }();
    public bool ShouldEagerCompile() => scope().ShouldEagerCompile();
    public void SetShouldEagerCompile() => scope().set_should_eager_compile();

    public FunctionSyntaxKind syntax_kind() => _syntaxKind;
    public FunctionKind kind() => scope().function_kind();

    public new bool IsAnonymousFunctionDefinition() => is_anonymous_expression();

    public int suspend_count() => _suspendCount;
    public void set_suspend_count(int suspend_count) => _suspendCount = suspend_count;

    public int return_position() => Math.Max(start_position(), end_position() - (_hasBraces ? 1 : 0));

    public int function_literal_id() => _functionLiteralId;
    public void set_function_literal_id(int function_literal_id) => _functionLiteralId = function_literal_id;

    public void set_requires_instance_members_initializer(bool value) => _requiresInstanceMembersInitializer = value;
    public bool requires_instance_members_initializer() => _requiresInstanceMembersInitializer;

    public void set_has_static_private_methods_or_accessors(bool value) => _hasStaticPrivateMethodsOrAccessors = value;
    public bool has_static_private_methods_or_accessors() => _hasStaticPrivateMethodsOrAccessors;

    public void set_class_scope_has_private_brand(bool value) => scope().set_class_scope_has_private_brand(value);
    public bool class_scope_has_private_brand() => scope().class_scope_has_private_brand();

    public bool private_name_lookup_skips_outer_class() => scope().private_name_lookup_skips_outer_class();

    public ProducedPreparseData? produced_preparse_data() => _producedPreparseData;

    public bool has_braces() => _hasBraces;
}

public sealed class AutoAccessorInfo
{
    private readonly FunctionLiteral _generatedGetter;
    private readonly FunctionLiteral _generatedSetter;
    // `accessor_storage_name_proxy_` is used to store the internal name of the
    // backing storage property associated with the generated getter/setters.
    private readonly VariableProxy _accessorStorageNameProxy;
    // `property_private_name_proxy_` only has a value if the accessor keyword
    // was applied to a private field.
    private VariableProxy? _propertyPrivateNameProxy;

    internal AutoAccessorInfo(FunctionLiteral generated_getter, FunctionLiteral generated_setter,
                              VariableProxy accessor_storage_name_proxy)
    {
        _generatedGetter = generated_getter;
        _generatedSetter = generated_setter;
        _accessorStorageNameProxy = accessor_storage_name_proxy;
    }

    public FunctionLiteral generated_getter() => _generatedGetter;
    public FunctionLiteral generated_setter() => _generatedSetter;
    public VariableProxy accessor_storage_name_proxy() => _accessorStorageNameProxy;
    public VariableProxy property_private_name_proxy() => _propertyPrivateNameProxy!;

    public void set_property_private_name_proxy(VariableProxy property_private_name_proxy) =>
        _propertyPrivateNameProxy = property_private_name_proxy;
}

// Property is used for passing information
// about a class literal's properties from the parser to the code generator.
public sealed class ClassLiteralProperty : LiteralProperty
{
    public enum Kind : byte { METHOD, GETTER, SETTER, FIELD, AUTO_ACCESSOR }

    private readonly Kind _kind;
    private readonly bool _isStatic;
    private readonly bool _isPrivate;
    private VariableProxy? _privateOrComputedNameProxy;
    private readonly AutoAccessorInfo? _autoAccessorInfo;

    internal ClassLiteralProperty(Expression key, Expression value, Kind kind, bool is_static, bool is_computed_name, bool is_private)
        : base(key, value, is_computed_name)
    {
        _kind = kind;
        _isStatic = is_static;
        _isPrivate = is_private;
    }

    internal ClassLiteralProperty(Expression key, Expression value, AutoAccessorInfo info, bool is_static, bool is_computed_name, bool is_private)
        : base(key, value, is_computed_name)
    {
        _kind = Kind.AUTO_ACCESSOR;
        _isStatic = is_static;
        _isPrivate = is_private;
        _autoAccessorInfo = info;
    }

    public Kind kind() => _kind;
    public bool is_static() => _isStatic;
    public bool is_private() => _isPrivate;
    public bool is_auto_accessor() => kind() == Kind.AUTO_ACCESSOR;

    public void set_computed_name_proxy(VariableProxy proxy) => _privateOrComputedNameProxy = proxy;

    public Variable computed_name_var() => _privateOrComputedNameProxy!.var();

    public void SetPrivateNameProxy(VariableProxy proxy)
    {
        if (is_auto_accessor())
        {
            auto_accessor_info().set_property_private_name_proxy(proxy);
            return;
        }
        _privateOrComputedNameProxy = proxy;
    }

    public Variable private_name_var() => _privateOrComputedNameProxy!.var();

    public AutoAccessorInfo auto_accessor_info() => _autoAccessorInfo!;

    // Access to the key/value for desugaring (V8 mutates via friends).
    internal void set_key(Expression key) => _key = key;
}

public sealed class ClassLiteralStaticElement
{
    public enum Kind : byte { PROPERTY, STATIC_BLOCK }

    private readonly Kind _kind;
    private readonly ClassLiteralProperty? _property;
    private readonly Block? _staticBlock;

    internal ClassLiteralStaticElement(ClassLiteralProperty property)
    {
        _kind = Kind.PROPERTY;
        _property = property;
    }

    internal ClassLiteralStaticElement(Block static_block)
    {
        _kind = Kind.STATIC_BLOCK;
        _staticBlock = static_block;
    }

    public Kind kind() => _kind;
    public ClassLiteralProperty property() => _property!;
    public Block static_block() => _staticBlock!;
}

public sealed class InitializeClassMembersStatement : Statement
{
    private readonly List<ClassLiteralProperty> _fields;

    internal InitializeClassMembersStatement(List<ClassLiteralProperty> fields, int pos)
        : base(pos, NodeType.InitializeClassMembersStatement) => _fields = fields;

    public List<ClassLiteralProperty> fields() => _fields;
}

public sealed class InitializeClassStaticElementsStatement : Statement
{
    private readonly List<ClassLiteralStaticElement> _elements;

    internal InitializeClassStaticElementsStatement(List<ClassLiteralStaticElement> elements, int pos)
        : base(pos, NodeType.InitializeClassStaticElementsStatement) => _elements = elements;

    public List<ClassLiteralStaticElement> elements() => _elements;
}

public sealed class AutoAccessorGetterBody : Statement
{
    private readonly VariableProxy _nameProxy;

    internal AutoAccessorGetterBody(VariableProxy name_proxy, int pos) : base(pos, NodeType.AutoAccessorGetterBody) => _nameProxy = name_proxy;

    public VariableProxy name_proxy() => _nameProxy;
}

public sealed class AutoAccessorSetterBody : Statement
{
    private readonly VariableProxy _nameProxy;

    internal AutoAccessorSetterBody(VariableProxy name_proxy, int pos) : base(pos, NodeType.AutoAccessorSetterBody) => _nameProxy = name_proxy;

    public VariableProxy name_proxy() => _nameProxy;
}

public sealed class ClassLiteral : Expression
{
    private readonly int _endPosition;
    private readonly ClassScope _scope;
    private readonly Expression? _extends;
    private readonly FunctionLiteral _constructor;
    private readonly List<ClassLiteralProperty>? _publicMembers;
    private readonly List<ClassLiteralProperty>? _privateMembers;
    private readonly FunctionLiteral? _staticInitializer;
    private readonly FunctionLiteral? _instanceMembersInitializerFunction;
    private readonly bool _hasStaticComputedNames;
    private readonly bool _isAnonymousExpression;
    private readonly Variable? _homeObject;
    private readonly Variable? _staticHomeObject;

    internal ClassLiteral(ClassScope scope, Expression? extends, FunctionLiteral constructor,
                          List<ClassLiteralProperty>? public_members, List<ClassLiteralProperty>? private_members,
                          FunctionLiteral? static_initializer, FunctionLiteral? instance_members_initializer_function,
                          int start_position, int end_position, bool has_static_computed_names, bool is_anonymous,
                          Variable? home_object, Variable? static_home_object)
        : base(start_position, NodeType.ClassLiteral)
    {
        _endPosition = end_position;
        _scope = scope;
        _extends = extends;
        _constructor = constructor;
        _publicMembers = public_members;
        _privateMembers = private_members;
        _staticInitializer = static_initializer;
        _instanceMembersInitializerFunction = instance_members_initializer_function;
        _hasStaticComputedNames = has_static_computed_names;
        _isAnonymousExpression = is_anonymous;
        _homeObject = home_object;
        _staticHomeObject = static_home_object;
    }

    public ClassScope scope() => _scope;
    public Expression? extends() => _extends;
    public FunctionLiteral constructor() => _constructor;
    public List<ClassLiteralProperty>? public_members() => _publicMembers;
    public List<ClassLiteralProperty>? private_members() => _privateMembers;
    public int start_position() => position();
    public int end_position() => _endPosition;
    public bool has_static_computed_names() => _hasStaticComputedNames;
    public bool is_anonymous_expression() => _isAnonymousExpression;
    public new bool IsAnonymousFunctionDefinition() => is_anonymous_expression();
    public FunctionLiteral? static_initializer() => _staticInitializer;
    public FunctionLiteral? instance_members_initializer_function() => _instanceMembersInitializerFunction;
    public Variable? home_object() => _homeObject;
    public Variable? static_home_object() => _staticHomeObject;
}

public sealed class NativeFunctionLiteral : Expression
{
    private readonly AstRawString _name;
    private readonly object? _extension;

    internal NativeFunctionLiteral(AstRawString name, object? extension, int pos) : base(pos, NodeType.NativeFunctionLiteral)
    {
        _name = name;
        _extension = extension;
    }

    public AstRawString raw_name() => _name;
    public object? extension() => _extension;
}

public sealed class SuperPropertyReference : Expression
{
    private readonly VariableProxy _homeObject;

    internal SuperPropertyReference(VariableProxy home_object, int pos) : base(pos, NodeType.SuperPropertyReference) => _homeObject = home_object;

    public VariableProxy home_object() => _homeObject;
}

public sealed class SuperCallReference : Expression
{
    private readonly VariableProxy _newTargetVar;
    private readonly VariableProxy _thisFunctionVar;

    internal SuperCallReference(VariableProxy new_target_var, VariableProxy this_function_var, int pos)
        : base(pos, NodeType.SuperCallReference)
    {
        _newTargetVar = new_target_var;
        _thisFunctionVar = this_function_var;
    }

    public VariableProxy new_target_var() => _newTargetVar;
    public VariableProxy this_function_var() => _thisFunctionVar;
}

// This AST Node is used to represent a dynamic import call --
// import(argument).
public sealed class ImportCallExpression : Expression
{
    private readonly Expression _specifier;
    private readonly ModuleImportPhase _phase;
    private readonly Expression? _importOptions;

    internal ImportCallExpression(Expression specifier, ModuleImportPhase phase, Expression? import_options, int pos)
        : base(pos, NodeType.ImportCallExpression)
    {
        _specifier = specifier;
        _phase = phase;
        _importOptions = import_options;
    }

    public Expression specifier() => _specifier;
    public ModuleImportPhase phase() => _phase;
    public Expression? import_options() => _importOptions;
}

// This class is produced when parsing the () in arrow functions without any
// arguments and is not actually a valid expression.
public sealed class EmptyParentheses : Expression
{
    internal EmptyParentheses(int pos) : base(pos, NodeType.EmptyParentheses) => mark_parenthesized();
}

// Represents the spec operation `GetTemplateObject(templateLiteral)`
// (defined at https://tc39.es/ecma262/#sec-gettemplateobject).
public sealed class GetTemplateObject : Expression
{
    private readonly List<AstRawString?> _cookedStrings;
    private readonly List<AstRawString> _rawStrings;

    internal GetTemplateObject(List<AstRawString?> cooked_strings, List<AstRawString> raw_strings, int pos)
        : base(pos, NodeType.GetTemplateObject)
    {
        _cookedStrings = cooked_strings;
        _rawStrings = raw_strings;
    }

    public List<AstRawString?> cooked_strings() => _cookedStrings;
    public List<AstRawString> raw_strings() => _rawStrings;

    // Whether GetOrBuildDescription can share the raw strings array for the
    // cooked strings (every cooked string is the same AstRawString as the raw one).
    public bool RawAndCookedMatch()
    {
        for (int i = 0; i < _rawStrings.Count; i++)
        {
            if (_rawStrings[i] != _cookedStrings[i]) return false;
        }
        return true;
    }
}

public sealed class TemplateLiteral : Expression
{
    private readonly List<AstRawString> _stringParts;
    private readonly List<Expression> _substitutions;

    internal TemplateLiteral(List<AstRawString> parts, List<Expression> substitutions, int pos)
        : base(pos, NodeType.TemplateLiteral)
    {
        _stringParts = parts;
        _substitutions = substitutions;
    }

    public List<AstRawString> string_parts() => _stringParts;
    public List<Expression> substitutions() => _substitutions;
}

// ----------------------------------------------------------------------------
// Basic visitor
// Sub-classes override Visit##NodeType; Visit(node) dispatches on node_type()
// (V8: AstVisitor<Subclass> with GENERATE_AST_VISITOR_SWITCH and
// DEFINE_AST_VISITOR_SUBCLASS_MEMBERS for the stack overflow check).
public abstract class AstVisitor
{
    private bool _stackOverflow;

    public void Visit(AstNode node)
    {
        if (CheckStackOverflow()) return;
        VisitNoStackOverflowCheck(node);
    }

    public void VisitNoStackOverflowCheck(AstNode node)
    {
        switch (node.node_type())
        {
            case NodeType.VariableDeclaration: VisitVariableDeclaration((VariableDeclaration)node); break;
            case NodeType.FunctionDeclaration: VisitFunctionDeclaration((FunctionDeclaration)node); break;
            case NodeType.DoWhileStatement: VisitDoWhileStatement((DoWhileStatement)node); break;
            case NodeType.WhileStatement: VisitWhileStatement((WhileStatement)node); break;
            case NodeType.ForStatement: VisitForStatement((ForStatement)node); break;
            case NodeType.ForInStatement: VisitForInStatement((ForInStatement)node); break;
            case NodeType.ForOfStatement: VisitForOfStatement((ForOfStatement)node); break;
            case NodeType.Block: VisitBlock((Block)node); break;
            case NodeType.SwitchStatement: VisitSwitchStatement((SwitchStatement)node); break;
            case NodeType.ExpressionStatement: VisitExpressionStatement((ExpressionStatement)node); break;
            case NodeType.EmptyStatement: VisitEmptyStatement((EmptyStatement)node); break;
            case NodeType.SloppyBlockFunctionStatement: VisitSloppyBlockFunctionStatement((SloppyBlockFunctionStatement)node); break;
            case NodeType.IfStatement: VisitIfStatement((IfStatement)node); break;
            case NodeType.ContinueStatement: VisitContinueStatement((ContinueStatement)node); break;
            case NodeType.BreakStatement: VisitBreakStatement((BreakStatement)node); break;
            case NodeType.ReturnStatement: VisitReturnStatement((ReturnStatement)node); break;
            case NodeType.WithStatement: VisitWithStatement((WithStatement)node); break;
            case NodeType.TryCatchStatement: VisitTryCatchStatement((TryCatchStatement)node); break;
            case NodeType.TryFinallyStatement: VisitTryFinallyStatement((TryFinallyStatement)node); break;
            case NodeType.DebuggerStatement: VisitDebuggerStatement((DebuggerStatement)node); break;
            case NodeType.InitializeClassMembersStatement: VisitInitializeClassMembersStatement((InitializeClassMembersStatement)node); break;
            case NodeType.InitializeClassStaticElementsStatement: VisitInitializeClassStaticElementsStatement((InitializeClassStaticElementsStatement)node); break;
            case NodeType.AutoAccessorGetterBody: VisitAutoAccessorGetterBody((AutoAccessorGetterBody)node); break;
            case NodeType.AutoAccessorSetterBody: VisitAutoAccessorSetterBody((AutoAccessorSetterBody)node); break;
            case NodeType.RegExpLiteral: VisitRegExpLiteral((RegExpLiteral)node); break;
            case NodeType.ObjectLiteral: VisitObjectLiteral((ObjectLiteral)node); break;
            case NodeType.ArrayLiteral: VisitArrayLiteral((ArrayLiteral)node); break;
            case NodeType.Assignment: VisitAssignment((Assignment)node); break;
            case NodeType.Await: VisitAwait((Await)node); break;
            case NodeType.BinaryOperation: VisitBinaryOperation((BinaryOperation)node); break;
            case NodeType.NaryOperation: VisitNaryOperation((NaryOperation)node); break;
            case NodeType.Call: VisitCall((Call)node); break;
            case NodeType.SuperCallForwardArgs: VisitSuperCallForwardArgs((SuperCallForwardArgs)node); break;
            case NodeType.CallNew: VisitCallNew((CallNew)node); break;
            case NodeType.CallRuntime: VisitCallRuntime((CallRuntime)node); break;
            case NodeType.ClassLiteral: VisitClassLiteral((ClassLiteral)node); break;
            case NodeType.CompareOperation: VisitCompareOperation((CompareOperation)node); break;
            case NodeType.CompoundAssignment: VisitCompoundAssignment((CompoundAssignment)node); break;
            case NodeType.ConditionalChain: VisitConditionalChain((ConditionalChain)node); break;
            case NodeType.Conditional: VisitConditional((Conditional)node); break;
            case NodeType.CountOperation: VisitCountOperation((CountOperation)node); break;
            case NodeType.EmptyParentheses: VisitEmptyParentheses((EmptyParentheses)node); break;
            case NodeType.FunctionLiteral: VisitFunctionLiteral((FunctionLiteral)node); break;
            case NodeType.GetTemplateObject: VisitGetTemplateObject((GetTemplateObject)node); break;
            case NodeType.ImportCallExpression: VisitImportCallExpression((ImportCallExpression)node); break;
            case NodeType.Literal: VisitLiteral((Literal)node); break;
            case NodeType.NativeFunctionLiteral: VisitNativeFunctionLiteral((NativeFunctionLiteral)node); break;
            case NodeType.OptionalChain: VisitOptionalChain((OptionalChain)node); break;
            case NodeType.Property: VisitProperty((Property)node); break;
            case NodeType.Spread: VisitSpread((Spread)node); break;
            case NodeType.SuperCallReference: VisitSuperCallReference((SuperCallReference)node); break;
            case NodeType.SuperPropertyReference: VisitSuperPropertyReference((SuperPropertyReference)node); break;
            case NodeType.TemplateLiteral: VisitTemplateLiteral((TemplateLiteral)node); break;
            case NodeType.ThisExpression: VisitThisExpression((ThisExpression)node); break;
            case NodeType.Throw: VisitThrow((Throw)node); break;
            case NodeType.UnaryOperation: VisitUnaryOperation((UnaryOperation)node); break;
            case NodeType.VariableProxy: VisitVariableProxy((VariableProxy)node); break;
            case NodeType.Yield: VisitYield((Yield)node); break;
            case NodeType.YieldStar: VisitYieldStar((YieldStar)node); break;
            case NodeType.FailureExpression: throw new InvalidOperationException("UNREACHABLE");
        }
    }

    public void VisitDeclarations(ThreadedList<Declaration> declarations)
    {
        foreach (Declaration decl in declarations) Visit(decl);
    }

    public void VisitStatements(List<Statement> statements)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            Visit(statements[i]);
        }
    }

    public void VisitExpressions(List<Expression> expressions)
    {
        for (int i = 0; i < expressions.Count; i++)
        {
            // The variable statement visiting code may pass null expressions
            // to this code.
            Expression expression = expressions[i];
            if (expression != null) Visit(expression);
        }
    }

    public void SetStackOverflow() => _stackOverflow = true;
    public void ClearStackOverflow() => _stackOverflow = false;
    public bool HasStackOverflow() => _stackOverflow;

    public bool CheckStackOverflow()
    {
        if (_stackOverflow) return true;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            _stackOverflow = true;
            return true;
        }
        return false;
    }

    public abstract void VisitVariableDeclaration(VariableDeclaration node);
    public abstract void VisitFunctionDeclaration(FunctionDeclaration node);
    public abstract void VisitDoWhileStatement(DoWhileStatement node);
    public abstract void VisitWhileStatement(WhileStatement node);
    public abstract void VisitForStatement(ForStatement node);
    public abstract void VisitForInStatement(ForInStatement node);
    public abstract void VisitForOfStatement(ForOfStatement node);
    public abstract void VisitBlock(Block node);
    public abstract void VisitSwitchStatement(SwitchStatement node);
    public abstract void VisitExpressionStatement(ExpressionStatement node);
    public abstract void VisitEmptyStatement(EmptyStatement node);
    public abstract void VisitSloppyBlockFunctionStatement(SloppyBlockFunctionStatement node);
    public abstract void VisitIfStatement(IfStatement node);
    public abstract void VisitContinueStatement(ContinueStatement node);
    public abstract void VisitBreakStatement(BreakStatement node);
    public abstract void VisitReturnStatement(ReturnStatement node);
    public abstract void VisitWithStatement(WithStatement node);
    public abstract void VisitTryCatchStatement(TryCatchStatement node);
    public abstract void VisitTryFinallyStatement(TryFinallyStatement node);
    public abstract void VisitDebuggerStatement(DebuggerStatement node);
    public abstract void VisitInitializeClassMembersStatement(InitializeClassMembersStatement node);
    public abstract void VisitInitializeClassStaticElementsStatement(InitializeClassStaticElementsStatement node);
    public abstract void VisitAutoAccessorGetterBody(AutoAccessorGetterBody node);
    public abstract void VisitAutoAccessorSetterBody(AutoAccessorSetterBody node);
    public abstract void VisitRegExpLiteral(RegExpLiteral node);
    public abstract void VisitObjectLiteral(ObjectLiteral node);
    public abstract void VisitArrayLiteral(ArrayLiteral node);
    public abstract void VisitAssignment(Assignment node);
    public abstract void VisitAwait(Await node);
    public abstract void VisitBinaryOperation(BinaryOperation node);
    public abstract void VisitNaryOperation(NaryOperation node);
    public abstract void VisitCall(Call node);
    public abstract void VisitSuperCallForwardArgs(SuperCallForwardArgs node);
    public abstract void VisitCallNew(CallNew node);
    public abstract void VisitCallRuntime(CallRuntime node);
    public abstract void VisitClassLiteral(ClassLiteral node);
    public abstract void VisitCompareOperation(CompareOperation node);
    public abstract void VisitCompoundAssignment(CompoundAssignment node);
    public abstract void VisitConditionalChain(ConditionalChain node);
    public abstract void VisitConditional(Conditional node);
    public abstract void VisitCountOperation(CountOperation node);
    public abstract void VisitEmptyParentheses(EmptyParentheses node);
    public abstract void VisitFunctionLiteral(FunctionLiteral node);
    public abstract void VisitGetTemplateObject(GetTemplateObject node);
    public abstract void VisitImportCallExpression(ImportCallExpression node);
    public abstract void VisitLiteral(Literal node);
    public abstract void VisitNativeFunctionLiteral(NativeFunctionLiteral node);
    public abstract void VisitOptionalChain(OptionalChain node);
    public abstract void VisitProperty(Property node);
    public abstract void VisitSpread(Spread node);
    public abstract void VisitSuperCallReference(SuperCallReference node);
    public abstract void VisitSuperPropertyReference(SuperPropertyReference node);
    public abstract void VisitTemplateLiteral(TemplateLiteral node);
    public abstract void VisitThisExpression(ThisExpression node);
    public abstract void VisitThrow(Throw node);
    public abstract void VisitUnaryOperation(UnaryOperation node);
    public abstract void VisitVariableProxy(VariableProxy node);
    public abstract void VisitYield(Yield node);
    public abstract void VisitYieldStar(YieldStar node);
}

// ----------------------------------------------------------------------------
// AstNode factory

public sealed partial class AstNodeFactory
{
    private readonly AstValueFactory _astValueFactory;
    private readonly EmptyStatement _emptyStatement;
    private readonly ThisExpression _thisExpression;
    private readonly FailureExpression _failureExpression;

    public AstNodeFactory(AstValueFactory ast_value_factory)
    {
        _astValueFactory = ast_value_factory;
        _emptyStatement = new EmptyStatement();
        _thisExpression = new ThisExpression(kNoSourcePosition);
        _failureExpression = new FailureExpression();
    }

    public AstNodeFactory ast_node_factory() => this;
    public AstValueFactory ast_value_factory() => _astValueFactory;

    public VariableDeclaration NewVariableDeclaration(int pos) => new(pos);

    public NestedVariableDeclaration NewNestedVariableDeclaration(Scope scope, int pos) => new(scope, pos);

    public FunctionDeclaration NewFunctionDeclaration(FunctionLiteral fun, int pos) => new(fun, pos);

    public Block NewBlock(int capacity, bool ignore_completion_value) => new(capacity, ignore_completion_value, false, false);

    public Block NewBlock(bool ignore_completion_value, bool is_breakable) => new(0, ignore_completion_value, is_breakable, false);

    public Block NewBlock(bool ignore_completion_value, IReadOnlyList<Statement> statements)
    {
        Block result = NewBlock(ignore_completion_value, false);
        result.InitializeStatements(statements);
        return result;
    }

    public Block NewParameterInitializationBlock(IReadOnlyList<Statement> statements)
    {
        var result = new Block(0, ignore_completion_value: true, is_breakable: false, is_initialization_block_for_parameters: true);
        result.InitializeStatements(statements);
        return result;
    }

    public DoWhileStatement NewDoWhileStatement(int pos) => new(pos);
    public WhileStatement NewWhileStatement(int pos) => new(pos);
    public ForStatement NewForStatement(int pos) => new(pos);

    public SwitchStatement NewSwitchStatement(Expression tag, int pos) => new(tag, pos);

    public ForEachStatement NewForEachStatement(ForEachStatement.VisitMode visit_mode, int pos) => visit_mode switch
    {
        ForEachStatement.VisitMode.ENUMERATE => new ForInStatement(pos),
        ForEachStatement.VisitMode.ITERATE => new ForOfStatement(pos, IteratorType.kNormal),
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public ForOfStatement NewForOfStatement(int pos, IteratorType type) => new(pos, type);

    public ExpressionStatement NewExpressionStatement(Expression expression, int pos) => new(expression, pos);

    public ContinueStatement NewContinueStatement(IterationStatement target, int pos) => new(target, pos);

    public BreakStatement NewBreakStatement(BreakableStatement target, int pos) => new(target, pos);

    public ReturnStatement NewReturnStatement(Expression expression, int pos, int end_position = ReturnStatement.kFunctionLiteralReturnPosition)
        => new(expression, ReturnStatement.Type.kNormal, pos, end_position);

    public ReturnStatement NewAsyncReturnStatement(Expression expression, int pos, int end_position = ReturnStatement.kFunctionLiteralReturnPosition)
        => new(expression, ReturnStatement.Type.kAsyncReturn, pos, end_position);

    public ReturnStatement NewSyntheticAsyncReturnStatement(Expression expression, int pos, int end_position = ReturnStatement.kFunctionLiteralReturnPosition)
        => new(expression, ReturnStatement.Type.kSyntheticAsyncReturn, pos, end_position);

    public WithStatement NewWithStatement(Scope scope, Expression expression, Statement statement, int pos) => new(scope, expression, statement, pos);

    public IfStatement NewIfStatement(Expression condition, Statement then_statement, Statement else_statement, int pos)
        => new(condition, then_statement, else_statement, pos);

    public TryCatchStatement NewTryCatchStatement(Block try_block, Scope? scope, Block catch_block, int pos)
        => new(try_block, scope, catch_block, CatchPrediction.CAUGHT, pos);

    public TryCatchStatement NewTryCatchStatementForReThrow(Block try_block, Scope? scope, Block catch_block, int pos)
        => new(try_block, scope, catch_block, CatchPrediction.UNCAUGHT, pos);

    public TryCatchStatement NewTryCatchStatementForAsyncAwait(Block try_block, Scope? scope, Block catch_block, int pos)
        => new(try_block, scope, catch_block, CatchPrediction.ASYNC_AWAIT, pos);

    public TryCatchStatement NewTryCatchStatementForReplAsyncAwait(Block try_block, Scope? scope, Block catch_block, int pos)
        => new(try_block, scope, catch_block, CatchPrediction.UNCAUGHT_ASYNC_AWAIT, pos);

    public TryFinallyStatement NewTryFinallyStatement(Block try_block, Block finally_block, int pos) => new(try_block, finally_block, pos);

    public DebuggerStatement NewDebuggerStatement(int pos) => new(pos);

    public EmptyStatement EmptyStatement() => _emptyStatement;

    public ThisExpression ThisExpression()
    {
        // Clear any previously set "parenthesized" flag on this_expression_ so this
        // particular token does not inherit the it. The flag is used to check
        // during arrow function head parsing whether we came from parenthesized
        // exprssion parsing, since additional arrow function verification was done
        // there. It does not matter whether a flag is unset after arrow head
        // verification, so clearing at this point is fine.
        _thisExpression.clear_parenthesized();
        return _thisExpression;
    }

    public ThisExpression NewThisExpression(int pos) => new(pos);

    public FailureExpression FailureExpression() => _failureExpression;

    public SloppyBlockFunctionStatement NewSloppyBlockFunctionStatement(int pos, Variable var, Token init)
        => new(pos, var, init, EmptyStatement());

    public CaseClause NewCaseClause(Expression? label, IReadOnlyList<Statement> statements) => new(label, statements);

    public Literal NewStringLiteral(AstRawString str, int pos) => new(str, pos);

    public Literal NewConsStringLiteral(AstConsString str, int pos) => new(str, pos);

    public Literal NewNumberLiteral(double number, int pos)
    {
        if (DoubleToSmiInteger(number, out int int_value))
        {
            return NewSmiLiteral(int_value, pos);
        }
        return new Literal(number, pos);
    }

    // DoubleToSmiInteger (src/numbers/conversions-inl.h): an integral double in
    // Smi range that is not -0.
    private static bool DoubleToSmiInteger(double value, out int smi_int_value)
    {
        smi_int_value = 0;
        if (!(value >= kSmiMinValue && value <= kSmiMaxValue)) return false;
        int i = (int)value;
        if (i != value) return false;
        if (i == 0 && double.IsNegative(value)) return false;
        smi_int_value = i;
        return true;
    }

    public Literal NewSmiLiteral(int number, int pos) => new(number, pos);

    public Literal NewBigIntLiteral(AstBigInt bigint, int pos) => new(bigint, pos);

    public Literal NewBooleanLiteral(bool b, int pos) => new(b, pos);

    public Literal NewNullLiteral(int pos) => new(Literal.Type.kNull, pos);

    public Literal NewUndefinedLiteral(int pos) => new(Literal.Type.kUndefined, pos);

    public Literal NewTheHoleLiteral() => new(Literal.Type.kTheHole, kNoSourcePosition);

    public ObjectLiteral NewObjectLiteral(IReadOnlyList<ObjectLiteralProperty> properties, uint boilerplate_properties, int pos,
                                          bool has_rest_property, Variable? home_object = null)
        => new(properties, boilerplate_properties, pos, has_rest_property, home_object);

    public ObjectLiteralProperty NewObjectLiteralProperty(Expression key, Expression value, ObjectLiteralProperty.Kind kind, bool is_computed_name)
        => new(key, value, kind, is_computed_name);

    public ObjectLiteralProperty NewObjectLiteralProperty(Expression key, Expression value, bool is_computed_name)
        => new(_astValueFactory, key, value, is_computed_name);

    public RegExpLiteral NewRegExpLiteral(AstRawString pattern, int flags, int pos) => new(pattern, flags, pos);

    public ArrayLiteral NewArrayLiteral(IReadOnlyList<Expression> values, int pos) => new(values, -1, pos);

    public ArrayLiteral NewArrayLiteral(IReadOnlyList<Expression> values, int first_spread_index, int pos) => new(values, first_spread_index, pos);

    public VariableProxy NewVariableProxy(Variable var, int start_position = kNoSourcePosition) => new(var, start_position);

    public VariableProxy NewVariableProxy(AstRawString name, VariableKind variable_kind, int start_position = kNoSourcePosition)
        => new(name, variable_kind, start_position);

    // Recreates the VariableProxy.
    public VariableProxy CopyVariableProxy(VariableProxy proxy) => new(proxy);

    public Variable CopyVariable(Variable variable) => new(variable);

    public OptionalChain NewOptionalChain(Expression expression) => new(expression);

    public Property NewProperty(Expression obj, Expression key, int pos, bool optional_chain = false) => new(obj, key, pos, optional_chain);

    public Call NewCall(Expression expression, IReadOnlyList<Expression> arguments, int pos, bool has_spread,
                        int eval_scope_info_index = 0, bool optional_chain = false)
        => new(expression, arguments, pos, has_spread, eval_scope_info_index, optional_chain);

    public SuperCallForwardArgs NewSuperCallForwardArgs(SuperCallReference expression, int pos) => new(expression, pos);

    public Call NewTaggedTemplate(Expression expression, IReadOnlyList<Expression> arguments, int pos)
        => new(expression, arguments, pos, Call.TaggedTemplateTag.kTrue);

    public CallNew NewCallNew(Expression expression, IReadOnlyList<Expression> arguments, int pos, bool has_spread)
        => new(expression, arguments, pos, has_spread);

    public CallRuntime NewCallRuntime(FunctionId id, IReadOnlyList<Expression> arguments, int pos)
        => new(Runtime.Runtime.FunctionForId(id), arguments, pos);

    public CallRuntime NewCallRuntime(RuntimeFunction function, IReadOnlyList<Expression> arguments, int pos)
        => new(function, arguments, pos);

    public UnaryOperation NewUnaryOperation(Token op, Expression expression, int pos) => new(op, expression, pos);

    public BinaryOperation NewBinaryOperation(Token op, Expression left, Expression right, int pos) => new(op, left, right, pos);

    public NaryOperation NewNaryOperation(Token op, Expression first, int initial_subsequent_size) => new(op, first, initial_subsequent_size);

    public CountOperation NewCountOperation(Token op, bool is_prefix, Expression expr, int pos) => new(op, is_prefix, expr, pos);

    public CompareOperation NewCompareOperation(Token op, Expression left, Expression right, int pos) => new(op, left, right, pos);

    public Spread NewSpread(Expression expression, int pos, int expr_pos) => new(expression, pos, expr_pos);

    public ConditionalChain NewConditionalChain(int initial_size, int pos) => new(initial_size, pos);

    public Conditional NewConditional(Expression condition, Expression then_expression, Expression else_expression, int position)
        => new(condition, then_expression, else_expression, position);

    public Assignment NewAssignment(Token op, Expression target, Expression value, int pos)
    {
        if (op != Token.Init && target.IsVariableProxy())
        {
            ((VariableProxy)target).set_is_assigned();
        }

        if (op == Token.Assign || op == Token.Init)
        {
            return new Assignment(NodeType.Assignment, op, target, value, pos);
        }
        else
        {
            return new CompoundAssignment(op, target, value, pos,
                NewBinaryOperation(Token.BinaryOpForAssignment(op), target, value, pos + 1));
        }
    }

    public Suspend NewYield(Expression? expression, int pos, Suspend.OnAbruptResume on_abrupt_resume)
    {
        expression ??= NewUndefinedLiteral(pos);
        return new Yield(expression, pos, on_abrupt_resume);
    }

    public YieldStar NewYieldStar(Expression expression, int pos) => new(expression, pos);

    public Await NewAwait(Expression? expression, int pos)
    {
        expression ??= NewUndefinedLiteral(pos);
        return new Await(expression, pos);
    }

    public Throw NewThrow(Expression exception, int pos) => new(exception, pos);

    public FunctionLiteral NewFunctionLiteral(AstRawString? name, DeclarationScope scope, IReadOnlyList<Statement> body,
                                              int expected_property_count, int parameter_count, int function_length,
                                              FunctionLiteral.ParameterFlag has_duplicate_parameters,
                                              FunctionSyntaxKind function_syntax_kind,
                                              FunctionLiteral.EagerCompileHint eager_compile_hint, int position,
                                              bool has_braces, int function_literal_id,
                                              ProducedPreparseData? produced_preparse_data = null)
        => new(name != null ? _astValueFactory.NewConsString(name) : null, _astValueFactory, scope, body,
               expected_property_count, parameter_count, function_length, function_syntax_kind,
               has_duplicate_parameters, eager_compile_hint, position, has_braces, function_literal_id,
               produced_preparse_data);

    // Creates a FunctionLiteral representing a top-level script, the
    // result of an eval (top-level or otherwise), or the result of calling
    // the Function constructor.
    public FunctionLiteral NewScriptOrEvalFunctionLiteral(DeclarationScope scope, IReadOnlyList<Statement> body,
                                                          int expected_property_count, int parameter_count)
        => new(_astValueFactory.empty_cons_string(), _astValueFactory, scope, body, expected_property_count,
               parameter_count, parameter_count, FunctionSyntaxKind.AnonymousExpression,
               FunctionLiteral.ParameterFlag.kNoDuplicateParameters, FunctionLiteral.EagerCompileHint.kShouldLazyCompile,
               0, /* has_braces */ false, kFunctionLiteralIdTopLevel);

    public AutoAccessorInfo NewAutoAccessorInfo(FunctionLiteral generated_getter, FunctionLiteral generated_setter,
                                                VariableProxy accessor_storage_name_proxy)
        => new(generated_getter, generated_setter, accessor_storage_name_proxy);

    public ClassLiteralProperty NewClassLiteralProperty(Expression key, Expression value, ClassLiteralProperty.Kind kind,
                                                        bool is_static, bool is_computed_name, bool is_private)
        => new(key, value, kind, is_static, is_computed_name, is_private);

    public ClassLiteralProperty NewClassLiteralProperty(Expression key, Expression value, AutoAccessorInfo auto_accessor_info,
                                                        bool is_static, bool is_computed_name, bool is_private)
        => new(key, value, auto_accessor_info, is_static, is_computed_name, is_private);

    public ClassLiteralStaticElement NewClassLiteralStaticElement(ClassLiteralProperty property) => new(property);

    public ClassLiteralStaticElement NewClassLiteralStaticElement(Block static_block) => new(static_block);

    public ClassLiteral NewClassLiteral(ClassScope scope, Expression? extends, FunctionLiteral constructor,
                                       List<ClassLiteralProperty>? public_members, List<ClassLiteralProperty>? private_members,
                                       FunctionLiteral? static_initializer, FunctionLiteral? instance_members_initializer_function,
                                       int start_position, int end_position, bool has_static_computed_names,
                                       bool is_anonymous, Variable? home_object, Variable? static_home_object)
        => new(scope, extends, constructor, public_members, private_members, static_initializer,
               instance_members_initializer_function, start_position, end_position, has_static_computed_names,
               is_anonymous, home_object, static_home_object);

    public NativeFunctionLiteral NewNativeFunctionLiteral(AstRawString name, object? extension, int pos) => new(name, extension, pos);

    public SuperPropertyReference NewSuperPropertyReference(VariableProxy home_object_var, int pos) => new(home_object_var, pos);

    public SuperCallReference NewSuperCallReference(VariableProxy new_target_var, VariableProxy this_function_var, int pos)
        => new(new_target_var, this_function_var, pos);

    public EmptyParentheses NewEmptyParentheses(int pos) => new(pos);

    public GetTemplateObject NewGetTemplateObject(List<AstRawString?> cooked_strings, List<AstRawString> raw_strings, int pos)
        => new(cooked_strings, raw_strings, pos);

    public TemplateLiteral NewTemplateLiteral(List<AstRawString> string_parts, List<Expression> substitutions, int pos)
        => new(string_parts, substitutions, pos);

    public ImportCallExpression NewImportCallExpression(Expression specifier, ModuleImportPhase phase, int pos)
        => new(specifier, phase, null, pos);

    public ImportCallExpression NewImportCallExpression(Expression specifier, ModuleImportPhase phase, Expression import_options, int pos)
        => new(specifier, phase, import_options, pos);

    public InitializeClassMembersStatement NewInitializeClassMembersStatement(List<ClassLiteralProperty> args, int pos) => new(args, pos);

    public InitializeClassStaticElementsStatement NewInitializeClassStaticElementsStatement(List<ClassLiteralStaticElement> args, int pos)
        => new(args, pos);

    public AutoAccessorGetterBody NewAutoAccessorGetterBody(VariableProxy name_proxy, int pos) => new(name_proxy, pos);

    public AutoAccessorSetterBody NewAutoAccessorSetterBody(VariableProxy name_proxy, int pos) => new(name_proxy, pos);
}
