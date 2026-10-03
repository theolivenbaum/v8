// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/prettyprinter.h and src/ast/prettyprinter.cc.
//
// CallPrinter renders the callee of a failed call (for "x is not a function"
// and friends). V8 builds a heap String through an IncrementalStringBuilder
// and prints literals by first materializing them on the heap
// (Literal::BuildValue); here it builds a managed string and prints each
// Literal type the way its heap value prints (see PrintLiteral).
//
// AstPrinter is V8's DEBUG-only AST dump (--print-ast). It is always compiled
// here. It writes into a StringBuilder rather than a growing char buffer, and
// prints object identities in place of the %p addresses V8 prints.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using V8Sharp.Common;
using V8Sharp.Parsing;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Ast;

public sealed class CallPrinter : AstVisitor
{
    public enum SpreadErrorInArgsHint { kErrorInArgs, kNoErrorInArgs }

    public enum ErrorHint
    {
        kNone,
        kNormalIterator,
        kAsyncIterator,
        kCallAndNormalIterator,
        kCallAndAsyncIterator,
    }

    private int _numPrints;
    private readonly StringBuilder _builder = new();
    private int _position;  // position of ast node to print
    private bool _found;
    private bool _done;
    private readonly bool _isUserJs;
    private bool _isIteratorError;
    private bool _isAsyncIteratorError;
    private bool _isCallError;
    private readonly SpreadErrorInArgsHint _errorInSpreadArgs;
    private ObjectLiteralProperty? _destructuringProp;
    private Assignment? _destructuringAssignment;
    private Expression? _spreadArg;
    private FunctionKind _functionKind;

    public CallPrinter(bool is_user_js,
                       SpreadErrorInArgsHint error_in_spread_args = SpreadErrorInArgsHint.kNoErrorInArgs)
    {
        _position = 0;
        _numPrints = 0;
        _found = false;
        _done = false;
        _isCallError = false;
        _isIteratorError = false;
        _isAsyncIteratorError = false;
        _destructuringProp = null;
        _destructuringAssignment = null;
        _isUserJs = is_user_js;
        _errorInSpreadArgs = error_in_spread_args;
        _spreadArg = null;
        _functionKind = FunctionKind.NormalFunction;
    }

    public ErrorHint GetErrorHint()
    {
        if (_isCallError)
        {
            if (_isIteratorError) return ErrorHint.kCallAndNormalIterator;
            if (_isAsyncIteratorError) return ErrorHint.kCallAndAsyncIterator;
        }
        else
        {
            if (_isIteratorError) return ErrorHint.kNormalIterator;
            if (_isAsyncIteratorError) return ErrorHint.kAsyncIterator;
        }
        return ErrorHint.kNone;
    }

    public Expression? spread_arg() => _spreadArg;
    public ObjectLiteralProperty? destructuring_prop() => _destructuringProp;
    public Assignment? destructuring_assignment() => _destructuringAssignment;

    // Prints the node with position |position| into a string.
    public string Print(FunctionLiteral program, int position)
    {
        _numPrints = 0;
        _position = position;
        Find(program);
        return _builder.ToString();
    }

    private void Find(AstNode? node, bool print = false)
    {
        if (_found)
        {
            if (print)
            {
                int prev_num_prints = _numPrints;
                Visit(node!);
                if (prev_num_prints != _numPrints) return;
            }
            Print("(intermediate value)");
        }
        else
        {
            Visit(node!);
        }
    }

    private bool ShouldPrint() => _found && !_done;

    private void Print(char c)
    {
        if (!ShouldPrint()) return;
        _numPrints++;
        _builder.Append(c);
    }

    private void Print(string str)
    {
        if (!ShouldPrint()) return;
        _numPrints++;
        _builder.Append(str);
    }

    public override void VisitBlock(Block node) => FindStatements(node.statements());

    public override void VisitVariableDeclaration(VariableDeclaration node) { }

    public override void VisitFunctionDeclaration(FunctionDeclaration node) { }

    public override void VisitExpressionStatement(ExpressionStatement node) => Find(node.expression());

    public override void VisitEmptyStatement(EmptyStatement node) { }

    public override void VisitSloppyBlockFunctionStatement(SloppyBlockFunctionStatement node) =>
        Find(node.statement());

    public override void VisitIfStatement(IfStatement node)
    {
        Find(node.condition());
        Find(node.then_statement());
        if (node.HasElseStatement())
        {
            Find(node.else_statement());
        }
    }

    public override void VisitContinueStatement(ContinueStatement node) { }

    public override void VisitBreakStatement(BreakStatement node) { }

    public override void VisitReturnStatement(ReturnStatement node) => Find(node.expression());

    public override void VisitWithStatement(WithStatement node)
    {
        Find(node.expression());
        Find(node.statement());
    }

    public override void VisitSwitchStatement(SwitchStatement node)
    {
        Find(node.tag());
        foreach (CaseClause clause in node.cases())
        {
            if (!clause.is_default()) Find(clause.label());
            FindStatements(clause.statements());
        }
    }

    public override void VisitDoWhileStatement(DoWhileStatement node)
    {
        Find(node.body());
        Find(node.cond());
    }

    public override void VisitWhileStatement(WhileStatement node)
    {
        Find(node.cond());
        Find(node.body());
    }

    public override void VisitForStatement(ForStatement node)
    {
        if (node.init() != null)
        {
            Find(node.init());
        }
        if (node.cond() != null) Find(node.cond());
        if (node.next() != null) Find(node.next());
        Find(node.body());
    }

    public override void VisitForInStatement(ForInStatement node)
    {
        Find(node.each());
        Find(node.subject());
        Find(node.body());
    }

    public override void VisitForOfStatement(ForOfStatement node)
    {
        Find(node.each());

        // Check the subject's position in case there was a GetIterator error.
        bool was_found = false;
        if (node.subject().position() == _position)
        {
            _isAsyncIteratorError = node.type() == IteratorType.kAsync;
            _isIteratorError = !_isAsyncIteratorError;
            was_found = !_found;
            if (was_found)
            {
                _found = true;
            }
        }
        Find(node.subject(), true);
        if (was_found)
        {
            _done = true;
            _found = false;
        }

        Find(node.body());
    }

    public override void VisitTryCatchStatement(TryCatchStatement node)
    {
        Find(node.try_block());
        Find(node.catch_block());
    }

    public override void VisitTryFinallyStatement(TryFinallyStatement node)
    {
        Find(node.try_block());
        Find(node.finally_block());
    }

    public override void VisitDebuggerStatement(DebuggerStatement node) { }

    public override void VisitFunctionLiteral(FunctionLiteral node)
    {
        FunctionKind last_function_kind = _functionKind;
        _functionKind = node.kind();
        FindStatements(node.body());
        _functionKind = last_function_kind;
    }

    public override void VisitClassLiteral(ClassLiteral node)
    {
        if (node.extends() != null) Find(node.extends());
        List<ClassLiteralProperty> public_members = node.public_members()!;
        for (int i = 0; i < public_members.Count; i++)
        {
            Find(public_members[i].value());
        }
        List<ClassLiteralProperty> private_members = node.private_members()!;
        for (int i = 0; i < private_members.Count; i++)
        {
            Find(private_members[i].value());
        }
    }

    public override void VisitInitializeClassMembersStatement(InitializeClassMembersStatement node)
    {
        List<ClassLiteralProperty> fields = node.fields();
        for (int i = 0; i < fields.Count; i++)
        {
            Find(fields[i].value());
        }
    }

    public override void VisitInitializeClassStaticElementsStatement(InitializeClassStaticElementsStatement node)
    {
        List<ClassLiteralStaticElement> elements = node.elements();
        for (int i = 0; i < elements.Count; i++)
        {
            ClassLiteralStaticElement element = elements[i];
            if (element.kind() == ClassLiteralStaticElement.Kind.PROPERTY)
            {
                Find(element.property().value());
            }
            else
            {
                Find(element.static_block());
            }
        }
    }

    public override void VisitAutoAccessorGetterBody(AutoAccessorGetterBody node) { }

    public override void VisitAutoAccessorSetterBody(AutoAccessorSetterBody node) { }

    public override void VisitNativeFunctionLiteral(NativeFunctionLiteral node) { }

    public override void VisitConditionalChain(ConditionalChain node)
    {
        for (int i = 0; i < node.conditional_chain_length(); ++i)
        {
            Find(node.condition_at(i));
            Find(node.then_expression_at(i));
        }
        Find(node.else_expression());
    }

    public override void VisitConditional(Conditional node)
    {
        Find(node.condition());
        Find(node.then_expression());
        Find(node.else_expression());
    }

    public override void VisitLiteral(Literal node)
    {
        if (!ShouldPrint()) return;
        PrintLiteral(node, true);
    }

    public override void VisitRegExpLiteral(RegExpLiteral node)
    {
        Print("/");
        PrintLiteral(node.raw_pattern(), false);
        Print("/");
        AppendRegExpFlags(node.flags(), this, null);
    }

    // REGEXP_FLAG_LIST order: alphabetic by the flag character.
    internal static void AppendRegExpFlags(int flags, CallPrinter? printer, StringBuilder? sb)
    {
        ReadOnlySpan<RegExpFlags> bits =
        [
            RegExpFlags.HasIndices, RegExpFlags.Global, RegExpFlags.IgnoreCase, RegExpFlags.Linear,
            RegExpFlags.Multiline, RegExpFlags.DotAll, RegExpFlags.Unicode, RegExpFlags.UnicodeSets,
            RegExpFlags.Sticky,
        ];
        ReadOnlySpan<char> chars = ['d', 'g', 'i', 'l', 'm', 's', 'u', 'v', 'y'];
        for (int i = 0; i < bits.Length; i++)
        {
            if ((flags & (int)bits[i]) == 0) continue;
            if (printer != null) printer.Print(chars[i]);
            else sb!.Append(chars[i]);
        }
    }

    public override void VisitObjectLiteral(ObjectLiteral node)
    {
        Print("{");
        List<ObjectLiteralProperty> properties = node.properties();
        for (int i = 0; i < properties.Count; i++)
        {
            Find(properties[i].value());
        }
        Print("}");
    }

    public override void VisitArrayLiteral(ArrayLiteral node)
    {
        Print("[");
        List<Expression> values = node.values();
        for (int i = 0; i < values.Count; i++)
        {
            if (i != 0) Print(",");
            Expression subexpr = values[i];
            Spread? spread = subexpr.AsSpread();
            if (spread != null && !_found && _position == spread.expression().position())
            {
                _found = true;
                _isIteratorError = true;
                Find(spread.expression(), true);
                _done = true;
                return;
            }
            Find(subexpr, true);
        }
        Print("]");
    }

    public override void VisitVariableProxy(VariableProxy node)
    {
        if (_isUserJs)
        {
            PrintLiteral(node.raw_name(), false);
        }
        else
        {
            // Variable names of non-user code are meaningless due to minification.
            Print("(var)");
        }
    }

    public override void VisitAssignment(Assignment node)
    {
        bool was_found = false;
        if (node.target().IsObjectLiteral())
        {
            ObjectLiteral target = node.target().AsObjectLiteral()!;
            if (target.position() == _position)
            {
                was_found = !_found;
                _found = true;
                _destructuringAssignment = node;
            }
            else
            {
                foreach (ObjectLiteralProperty prop in target.properties())
                {
                    if (prop.value().position() == _position)
                    {
                        was_found = !_found;
                        _found = true;
                        _destructuringProp = prop;
                        _destructuringAssignment = node;
                        break;
                    }
                }
            }
        }
        if (!was_found)
        {
            if (_found)
            {
                Find(node.target(), true);
                return;
            }
            Find(node.target());
            if (node.target().IsArrayLiteral())
            {
                // Special case the visit for destructuring array assignment.
                if (node.value().position() == _position)
                {
                    _isIteratorError = true;
                    was_found = !_found;
                    _found = true;
                }
                Find(node.value(), true);
            }
            else
            {
                Find(node.value());
            }
        }
        else
        {
            Find(node.value(), true);
        }

        if (was_found)
        {
            _done = true;
            _found = false;
        }
    }

    public override void VisitCompoundAssignment(CompoundAssignment node) => VisitAssignment(node);

    public override void VisitYield(Yield node) => Find(node.expression());

    public override void VisitYieldStar(YieldStar node)
    {
        if (!_found && _position == node.expression().position())
        {
            _found = true;
            if (IsAsyncFunction(_functionKind))
            {
                _isAsyncIteratorError = true;
            }
            else
            {
                _isIteratorError = true;
            }
            Print("yield* ");
        }
        Find(node.expression());
    }

    public override void VisitAwait(Await node) => Find(node.expression());

    public override void VisitThrow(Throw node) => Find(node.exception());

    public override void VisitOptionalChain(OptionalChain node) => Find(node.expression());

    public override void VisitProperty(Property node)
    {
        Expression key = node.key();
        Literal? literal = key.AsLiteral();
        if (literal != null && IsInternalizedString(literal))
        {
            Find(node.obj(), true);
            if (node.is_optional_chain_link())
            {
                Print("?");
            }
            Print(".");
            if (!ShouldPrint()) return;
            PrintLiteral(literal, false);
        }
        else
        {
            Find(node.obj(), true);
            if (node.is_optional_chain_link())
            {
                Print("?.");
            }
            Print("[");
            Find(key, true);
            Print("]");
        }
    }

    public override void VisitCall(Call node)
    {
        bool was_found = false;
        if (node.position() == _position)
        {
            if (_errorInSpreadArgs == SpreadErrorInArgsHint.kErrorInArgs && node.arguments().Count != 0)
            {
                Spread? spread = node.arguments()[^1].AsSpread();
                if (spread != null)
                {
                    _found = true;
                    _spreadArg = spread.expression();
                    Find(_spreadArg, true);

                    _done = true;
                    _found = false;
                    return;
                }
            }

            _isCallError = true;
            was_found = !_found;
        }

        if (was_found)
        {
            // Bail out if the error is caused by a direct call to a variable in
            // non-user JS code. The variable name is meaningless due to minification.
            if (!_isUserJs && node.expression().IsVariableProxy())
            {
                _done = true;
                return;
            }
            _found = true;
        }
        Find(node.expression(), true);
        if (!was_found && !_isIteratorError) Print("(...)");
        FindArguments(node.arguments());
        if (was_found)
        {
            _done = true;
            _found = false;
        }
    }

    public override void VisitCallNew(CallNew node)
    {
        bool was_found = false;
        if (node.position() == _position)
        {
            if (_errorInSpreadArgs == SpreadErrorInArgsHint.kErrorInArgs && node.arguments().Count != 0)
            {
                Spread? spread = node.arguments()[^1].AsSpread();
                if (spread != null)
                {
                    _found = true;
                    _spreadArg = spread.expression();
                    Find(_spreadArg, true);

                    _done = true;
                    _found = false;
                    return;
                }
            }

            _isCallError = true;
            was_found = !_found;
        }
        if (was_found)
        {
            // Bail out if the error is caused by a direct call to a variable in
            // non-user JS code. The variable name is meaningless due to minification.
            if (!_isUserJs && node.expression().IsVariableProxy())
            {
                _done = true;
                return;
            }
            _found = true;
        }
        Find(node.expression(), was_found || _isIteratorError);
        FindArguments(node.arguments());
        if (was_found)
        {
            _done = true;
            _found = false;
        }
    }

    public override void VisitCallRuntime(CallRuntime node) => FindArguments(node.arguments());

    public override void VisitSuperCallForwardArgs(SuperCallForwardArgs node)
    {
        Find(node.expression(), true);
        Print("(...forwarded args...)");
    }

    public override void VisitUnaryOperation(UnaryOperation node)
    {
        Token op = node.op();
        bool needsSpace = op == Token.Delete || op == Token.TypeOf || op == Token.Void;
        Print("(");
        Print(Token.StringOf(op)!);
        if (needsSpace) Print(" ");
        Find(node.expression(), true);
        Print(")");
    }

    public override void VisitCountOperation(CountOperation node)
    {
        Print("(");
        if (node.is_prefix()) Print(Token.StringOf(node.op())!);
        Find(node.expression(), true);
        if (node.is_postfix()) Print(Token.StringOf(node.op())!);
        Print(")");
    }

    public override void VisitBinaryOperation(BinaryOperation node)
    {
        Print("(");
        Find(node.left(), true);
        Print(" ");
        Print(Token.StringOf(node.op())!);
        Print(" ");
        Find(node.right(), true);
        Print(")");
    }

    public override void VisitNaryOperation(NaryOperation node)
    {
        Print("(");
        Find(node.first(), true);
        for (int i = 0; i < node.subsequent_length(); ++i)
        {
            Print(" ");
            Print(Token.StringOf(node.op())!);
            Print(" ");
            Find(node.subsequent(i), true);
        }
        Print(")");
    }

    public override void VisitCompareOperation(CompareOperation node)
    {
        Print("(");
        Find(node.left(), true);
        Print(" ");
        Print(Token.StringOf(node.op())!);
        Print(" ");
        Find(node.right(), true);
        Print(")");
    }

    public override void VisitSpread(Spread node)
    {
        Print("(...");
        Find(node.expression(), true);
        Print(")");
    }

    public override void VisitEmptyParentheses(EmptyParentheses node) =>
        throw new InvalidOperationException("UNREACHABLE");

    public override void VisitGetTemplateObject(GetTemplateObject node) { }

    public override void VisitTemplateLiteral(TemplateLiteral node)
    {
        foreach (Expression substitution in node.substitutions())
        {
            Find(substitution, true);
        }
    }

    public override void VisitImportCallExpression(ImportCallExpression node)
    {
        Print("import");
        switch (node.phase())
        {
            case ModuleImportPhase.kSource:
                Print(".source");
                break;
            case ModuleImportPhase.kDefer:
                Print(".defer");
                break;
            case ModuleImportPhase.kEvaluation:
                break;
        }
        Print("(");
        Find(node.specifier(), true);
        if (node.import_options() != null)
        {
            Print(", ");
            Find(node.import_options(), true);
        }
        Print(")");
    }

    public override void VisitThisExpression(ThisExpression node) => Print("this");

    public override void VisitSuperPropertyReference(SuperPropertyReference node) { }

    public override void VisitSuperCallReference(SuperCallReference node) => Print("super");

    private void FindStatements(List<Statement>? statements)
    {
        if (statements == null) return;
        for (int i = 0; i < statements.Count; i++)
        {
            Find(statements[i]);
        }
    }

    private void FindArguments(List<Expression> arguments)
    {
        if (_found) return;
        for (int i = 0; i < arguments.Count; i++)
        {
            Find(arguments[i]);
        }
    }

    // IsInternalizedString(*literal->BuildValue(isolate)): string literals
    // build internalized strings; a cons string of two or more segments is
    // flattened into a fresh, non-internalized string.
    private static bool IsInternalizedString(Literal literal) => literal.type() switch
    {
        Literal.Type.kString => true,
        Literal.Type.kConsString => literal.AsConsString().ToRawStrings().Count <= 1,
        _ => false,
    };

    // CallPrinter::PrintLiteral(DirectHandle<Object>, bool) applied to
    // literal->BuildValue(isolate).
    private void PrintLiteral(Literal literal, bool quote)
    {
        if (!ShouldPrint()) return;
        switch (literal.type())
        {
            case Literal.Type.kTheHole:
                // Holes can occur in array literals, and should show up as empty entries.
                Print("");
                break;
            case Literal.Type.kString:
                PrintLiteral(literal.AsRawString(), quote);
                break;
            case Literal.Type.kConsString:
                if (quote) Print("\"");
                Print(literal.AsConsString().ToFlatString());
                if (quote) Print("\"");
                break;
            case Literal.Type.kNull:
                Print("null");
                break;
            case Literal.Type.kBoolean:
                Print(literal.ToBooleanIsTrue() ? "true" : "false");
                break;
            case Literal.Type.kUndefined:
                Print("undefined");
                break;
            case Literal.Type.kSmi:
                Print(literal.AsSmiLiteral().ToString(CultureInfo.InvariantCulture));
                break;
            case Literal.Type.kHeapNumber:
                Print(NumberConversions.DoubleToCString(literal.AsNumber()));
                break;
            case Literal.Type.kBigInt:
                // A BigInt is neither a String, an oddball, a Number nor a
                // Symbol, so V8 prints nothing for it.
                break;
        }
    }

    private void PrintLiteral(AstRawString value, bool quote)
    {
        if (!ShouldPrint()) return;
        if (quote) Print("\"");
        Print(value.Value);
        if (quote) Print("\"");
    }
}

public sealed class AstPrinter : AstVisitor
{
    private readonly StringBuilder _output = new();  // output string buffer
    private int _indent;

    // The following routines print a node into a string.
    public string Print(AstNode node)
    {
        Init();
        Visit(node);
        return _output.ToString();
    }

    public string PrintProgram(FunctionLiteral program)
    {
        Init();
        using (new IndentedScope(this, "FUNC", program.position()))
        {
            PrintIndented("KIND");
            Print(" ").Append((uint)program.kind()).Append('\n');
            PrintIndented("LITERAL ID");
            Print(" ").Append(program.function_literal_id()).Append('\n');
            PrintIndented("SUSPEND COUNT");
            Print(" ").Append(program.suspend_count()).Append('\n');
            PrintLiteralIndented("NAME", program.raw_name(), true);
            if (program.raw_inferred_name() != null)
            {
                PrintLiteralIndented("INFERRED NAME", program.raw_inferred_name(), true);
            }
            if (program.requires_instance_members_initializer())
            {
                Print(" REQUIRES INSTANCE FIELDS INITIALIZER\n");
            }
            if (program.class_scope_has_private_brand())
            {
                Print(" CLASS SCOPE HAS PRIVATE BRAND\n");
            }
            if (program.has_static_private_methods_or_accessors())
            {
                Print(" HAS STATIC PRIVATE METHODS\n");
            }
            PrintParameters(program.scope());
            PrintDeclarations(program.scope().declarations());
            PrintStatements(program.body());
        }
        return _output.ToString();
    }

    // AstPrinter::PrintOut prints to stdout; this returns the text.
    public static string PrintOut(AstNode node)
    {
        var printer = new AstPrinter();
        printer.Init();
        printer.Visit(node);
        return printer._output.ToString();
    }

    private void Init() => _output.Clear();

    private StringBuilder Print(string text) => _output.Append(text);

    private readonly ref struct IndentedScope
    {
        private readonly AstPrinter _astPrinter;

        public IndentedScope(AstPrinter printer, string txt)
        {
            _astPrinter = printer;
            _astPrinter.PrintIndented(txt);
            _astPrinter.Print("\n");
            _astPrinter._indent++;
        }

        public IndentedScope(AstPrinter printer, string txt, int pos)
        {
            _astPrinter = printer;
            _astPrinter.PrintIndented(txt);
            _astPrinter.Print(" at ").Append(pos).Append('\n');
            _astPrinter._indent++;
        }

        public void Dispose() => _astPrinter._indent--;
    }

    private void PrintLiteral(Literal literal, bool quote)
    {
        switch (literal.type())
        {
            case Literal.Type.kString:
                PrintLiteral(literal.AsRawString(), quote);
                break;
            case Literal.Type.kConsString:
                PrintLiteral(literal.AsConsString(), quote);
                break;
            case Literal.Type.kSmi:
                _output.Append(literal.AsSmiLiteral());
                break;
            case Literal.Type.kHeapNumber:
                _output.Append(FormatG(literal.AsNumber()));
                break;
            case Literal.Type.kBigInt:
                _output.Append(literal.AsBigInt().c_str()).Append('n');
                break;
            case Literal.Type.kNull:
                Print("null");
                break;
            case Literal.Type.kUndefined:
                Print("undefined");
                break;
            case Literal.Type.kTheHole:
                Print("the hole");
                break;
            case Literal.Type.kBoolean:
                Print(literal.ToBooleanIsTrue() ? "true" : "false");
                break;
        }
    }

    // V8 prints a one-byte string byte by byte with %c and a two-byte one
    // with %lc over the low byte of each code unit; here the code units are
    // appended as they are.
    private void PrintLiteral(AstRawString? value, bool quote)
    {
        if (quote) Print("\"");
        if (value != null) _output.Append(value.Value);
        if (quote) Print("\"");
    }

    private void PrintLiteral(AstConsString? value, bool quote)
    {
        if (quote) Print("\"");
        if (value != null)
        {
            foreach (AstRawString s in value.ToRawStrings())
            {
                PrintLiteral(s, false);
            }
        }
        if (quote) Print("\"");
    }

    private void PrintIndented(string txt)
    {
        for (int i = 0; i < _indent; i++)
        {
            Print(". ");
        }
        Print(txt);
    }

    private void PrintLiteralIndented(string info, Literal literal, bool quote)
    {
        PrintIndented(info);
        Print(" ");
        PrintLiteral(literal, quote);
        Print("\n");
    }

    private void PrintLiteralIndented(string info, AstRawString? value, bool quote)
    {
        PrintIndented(info);
        Print(" ");
        PrintLiteral(value, quote);
        Print("\n");
    }

    private void PrintLiteralIndented(string info, AstConsString? value, bool quote)
    {
        PrintIndented(info);
        Print(" ");
        PrintLiteral(value, quote);
        Print("\n");
    }

    private void PrintLiteralWithModeIndented(string info, Variable? var, AstRawString value)
    {
        if (var == null)
        {
            PrintLiteralIndented(info, value, true);
        }
        else
        {
            string buf = info + " (0x" +
                         RuntimeHelpers.GetHashCode(var).ToString("x", CultureInfo.InvariantCulture) +
                         ") (mode = " + VariableMode2String(var.mode()) + ", assigned = " +
                         (var.maybe_assigned() == MaybeAssignedFlag.kMaybeAssigned ? "true" : "false") + ")";
            PrintLiteralIndented(buf, value, true);
        }
    }

    private void PrintIndentedVisit(string s, AstNode? node)
    {
        if (node != null)
        {
            using var indent = new IndentedScope(this, s, node.position());
            Visit(node);
        }
    }

    private void PrintDeclarations(ThreadedList<Declaration> declarations)
    {
        if (!declarations.is_empty())
        {
            using var indent = new IndentedScope(this, "DECLS");
            foreach (Declaration decl in declarations) Visit(decl);
        }
    }

    private void PrintParameters(DeclarationScope scope)
    {
        if (scope.num_parameters() > 0)
        {
            using var indent = new IndentedScope(this, "PARAMS");
            for (int i = 0; i < scope.num_parameters(); i++)
            {
                PrintLiteralWithModeIndented("VAR", scope.parameter(i), scope.parameter(i).raw_name());
            }
        }
    }

    private void PrintStatements(List<Statement> statements)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            Visit(statements[i]);
        }
    }

    private void PrintArguments(List<Expression> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            Visit(arguments[i]);
        }
    }

    public override void VisitBlock(Block node)
    {
        string block_txt = node.ignore_completion_value() ? "BLOCK NOCOMPLETIONS" : "BLOCK";
        using var indent = new IndentedScope(this, block_txt, node.position());
        PrintStatements(node.statements());
    }

    public override void VisitVariableDeclaration(VariableDeclaration node) =>
        PrintLiteralWithModeIndented("VARIABLE", node.var(), node.var()!.raw_name());

    public override void VisitFunctionDeclaration(FunctionDeclaration node)
    {
        PrintIndented("FUNCTION ");
        PrintLiteral(node.var()!.raw_name(), true);
        Print(" = function ");
        PrintLiteral(node.fun().raw_name(), false);
        Print("\n");
    }

    public override void VisitExpressionStatement(ExpressionStatement node)
    {
        using var indent = new IndentedScope(this, "EXPRESSION STATEMENT", node.position());
        Visit(node.expression());
    }

    public override void VisitEmptyStatement(EmptyStatement node)
    {
        using var indent = new IndentedScope(this, "EMPTY", node.position());
    }

    public override void VisitSloppyBlockFunctionStatement(SloppyBlockFunctionStatement node) =>
        Visit(node.statement());

    public override void VisitIfStatement(IfStatement node)
    {
        using var indent = new IndentedScope(this, "IF", node.position());
        PrintIndentedVisit("CONDITION", node.condition());
        PrintIndentedVisit("THEN", node.then_statement());
        if (node.HasElseStatement())
        {
            PrintIndentedVisit("ELSE", node.else_statement());
        }
    }

    public override void VisitContinueStatement(ContinueStatement node)
    {
        using var indent = new IndentedScope(this, "CONTINUE", node.position());
    }

    public override void VisitBreakStatement(BreakStatement node)
    {
        using var indent = new IndentedScope(this, "BREAK", node.position());
    }

    public override void VisitReturnStatement(ReturnStatement node)
    {
        using var indent = new IndentedScope(this, "RETURN", node.position());
        Visit(node.expression());
    }

    public override void VisitWithStatement(WithStatement node)
    {
        using var indent = new IndentedScope(this, "WITH", node.position());
        PrintIndentedVisit("OBJECT", node.expression());
        PrintIndentedVisit("BODY", node.statement());
    }

    public override void VisitSwitchStatement(SwitchStatement node)
    {
        using var switch_indent = new IndentedScope(this, "SWITCH", node.position());
        PrintIndentedVisit("TAG", node.tag());
        foreach (CaseClause clause in node.cases())
        {
            if (clause.is_default())
            {
                using var indent = new IndentedScope(this, "DEFAULT");
                PrintStatements(clause.statements());
            }
            else
            {
                using var indent = new IndentedScope(this, "CASE");
                Visit(clause.label());
                PrintStatements(clause.statements());
            }
        }
    }

    public override void VisitDoWhileStatement(DoWhileStatement node)
    {
        using var indent = new IndentedScope(this, "DO", node.position());
        PrintIndentedVisit("BODY", node.body());
        PrintIndentedVisit("COND", node.cond());
    }

    public override void VisitWhileStatement(WhileStatement node)
    {
        using var indent = new IndentedScope(this, "WHILE", node.position());
        PrintIndentedVisit("COND", node.cond());
        PrintIndentedVisit("BODY", node.body());
    }

    public override void VisitForStatement(ForStatement node)
    {
        using var indent = new IndentedScope(this, "FOR", node.position());
        if (node.init() != null) PrintIndentedVisit("INIT", node.init());
        if (node.cond() != null) PrintIndentedVisit("COND", node.cond());
        PrintIndentedVisit("BODY", node.body());
        if (node.next() != null) PrintIndentedVisit("NEXT", node.next());
    }

    public override void VisitForInStatement(ForInStatement node)
    {
        using var indent = new IndentedScope(this, "FOR IN", node.position());
        PrintIndentedVisit("FOR", node.each());
        PrintIndentedVisit("IN", node.subject());
        PrintIndentedVisit("BODY", node.body());
    }

    public override void VisitForOfStatement(ForOfStatement node)
    {
        using var indent = new IndentedScope(this, "FOR OF", node.position());
        string for_type = node.type() == IteratorType.kAsync ? "FOR AWAIT" : "FOR";
        PrintIndentedVisit(for_type, node.each());
        PrintIndentedVisit("OF", node.subject());
        PrintIndentedVisit("BODY", node.body());
    }

    public override void VisitTryCatchStatement(TryCatchStatement node)
    {
        using var indent = new IndentedScope(this, "TRY CATCH", node.position());
        PrintIndentedVisit("TRY", node.try_block());
        PrintIndented("CATCH PREDICTION");
        string prediction = node.GetCatchPrediction(CatchPrediction.UNCAUGHT) switch
        {
            CatchPrediction.UNCAUGHT => "UNCAUGHT",
            CatchPrediction.CAUGHT => "CAUGHT",
            CatchPrediction.ASYNC_AWAIT => "ASYNC_AWAIT",
            CatchPrediction.UNCAUGHT_ASYNC_AWAIT => "UNCAUGHT_ASYNC_AWAIT",
            // Catch prediction resulting in promise rejections aren't
            // parsed by the parser.
            _ => throw new InvalidOperationException("UNREACHABLE"),
        };
        Print(" ").Append(prediction).Append('\n');
        if (node.scope() != null)
        {
            PrintLiteralWithModeIndented("CATCHVAR", node.scope()!.catch_variable(),
                                         node.scope()!.catch_variable().raw_name());
        }
        PrintIndentedVisit("CATCH", node.catch_block());
    }

    public override void VisitTryFinallyStatement(TryFinallyStatement node)
    {
        using var indent = new IndentedScope(this, "TRY FINALLY", node.position());
        PrintIndentedVisit("TRY", node.try_block());
        PrintIndentedVisit("FINALLY", node.finally_block());
    }

    public override void VisitDebuggerStatement(DebuggerStatement node)
    {
        using var indent = new IndentedScope(this, "DEBUGGER", node.position());
    }

    public override void VisitFunctionLiteral(FunctionLiteral node)
    {
        using var indent = new IndentedScope(this, "FUNC LITERAL", node.position());
        PrintIndented("LITERAL ID");
        Print(" ").Append(node.function_literal_id()).Append('\n');
        PrintLiteralIndented("NAME", node.raw_name(), false);
        PrintLiteralIndented("INFERRED NAME", node.raw_inferred_name(), false);
        // We don't want to see the function literal in this case: it
        // will be printed via PrintProgram when the code for it is
        // generated.
    }

    public override void VisitClassLiteral(ClassLiteral node)
    {
        using var indent = new IndentedScope(this, "CLASS LITERAL", node.position());
        PrintLiteralIndented("NAME", node.constructor().raw_name(), false);
        if (node.extends() != null)
        {
            PrintIndentedVisit("EXTENDS", node.extends());
        }
        Scope outer = node.constructor().scope().outer_scope()!;
        if (outer.is_class_scope())
        {
            Variable? brand = outer.AsClassScope().brand();
            if (brand != null)
            {
                PrintLiteralWithModeIndented("BRAND", brand, brand.raw_name());
            }
        }
        if (node.static_initializer() != null)
        {
            PrintIndentedVisit("STATIC INITIALIZER", node.static_initializer());
        }
        if (node.instance_members_initializer_function() != null)
        {
            PrintIndentedVisit("INSTANCE MEMBERS INITIALIZER", node.instance_members_initializer_function());
        }
        PrintClassProperties(node.private_members()!);
        PrintClassProperties(node.public_members()!);
    }

    public override void VisitInitializeClassMembersStatement(InitializeClassMembersStatement node)
    {
        using var indent = new IndentedScope(this, "INITIALIZE CLASS MEMBERS", node.position());
        PrintClassProperties(node.fields());
    }

    public override void VisitInitializeClassStaticElementsStatement(InitializeClassStaticElementsStatement node)
    {
        using var indent = new IndentedScope(this, "INITIALIZE CLASS STATIC ELEMENTS", node.position());
        PrintClassStaticElements(node.elements());
    }

    public override void VisitAutoAccessorGetterBody(AutoAccessorGetterBody node)
    {
        using var indent = new IndentedScope(this, "AUTO ACCESSOR GETTER BODY", node.position());
        PrintIndentedVisit("AUTO ACCESSOR STORAGE PRIVATE NAME", node.name_proxy());
    }

    public override void VisitAutoAccessorSetterBody(AutoAccessorSetterBody node)
    {
        using var indent = new IndentedScope(this, "AUTO ACCESSOR SETTER BODY", node.position());
        PrintIndentedVisit("AUTO ACCESSOR STORAGE PRIVATE NAME", node.name_proxy());
    }

    private void PrintClassProperty(ClassLiteralProperty property)
    {
        string prop_kind = property.kind() switch
        {
            ClassLiteralProperty.Kind.METHOD => "METHOD",
            ClassLiteralProperty.Kind.GETTER => "GETTER",
            ClassLiteralProperty.Kind.SETTER => "SETTER",
            ClassLiteralProperty.Kind.FIELD => "FIELD",
            _ => "AUTO ACCESSOR",
        };
        string buf = "PROPERTY" + (property.is_static() ? " - STATIC" : "") +
                     (property.is_private() ? " - PRIVATE" : " - PUBLIC") + " - " + prop_kind;
        using var prop = new IndentedScope(this, buf);
        PrintIndentedVisit("KEY", property.key());
        PrintIndentedVisit("VALUE", property.value());
    }

    private void PrintClassProperties(List<ClassLiteralProperty> properties)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            PrintClassProperty(properties[i]);
        }
    }

    private void PrintClassStaticElements(List<ClassLiteralStaticElement> static_elements)
    {
        for (int i = 0; i < static_elements.Count; i++)
        {
            ClassLiteralStaticElement element = static_elements[i];
            switch (element.kind())
            {
                case ClassLiteralStaticElement.Kind.PROPERTY:
                    PrintClassProperty(element.property());
                    break;
                case ClassLiteralStaticElement.Kind.STATIC_BLOCK:
                    PrintIndentedVisit("STATIC BLOCK", element.static_block());
                    break;
            }
        }
    }

    public override void VisitNativeFunctionLiteral(NativeFunctionLiteral node)
    {
        using var indent = new IndentedScope(this, "NATIVE FUNC LITERAL", node.position());
        PrintLiteralIndented("NAME", node.raw_name(), false);
    }

    public override void VisitConditionalChain(ConditionalChain node)
    {
        using var indent = new IndentedScope(this, "CONDITIONAL_CHAIN", node.position());
        PrintIndentedVisit("CONDITION", node.condition_at(0));
        PrintIndentedVisit("THEN", node.then_expression_at(0));
        for (int i = 1; i < node.conditional_chain_length(); ++i)
        {
            using var inner_indent = new IndentedScope(this, "ELSE IF", node.condition_position_at(i));
            PrintIndentedVisit("CONDITION", node.condition_at(i));
            PrintIndentedVisit("THEN", node.then_expression_at(i));
        }
        PrintIndentedVisit("ELSE", node.else_expression());
    }

    public override void VisitConditional(Conditional node)
    {
        using var indent = new IndentedScope(this, "CONDITIONAL", node.position());
        PrintIndentedVisit("CONDITION", node.condition());
        PrintIndentedVisit("THEN", node.then_expression());
        PrintIndentedVisit("ELSE", node.else_expression());
    }

    public override void VisitLiteral(Literal node) => PrintLiteralIndented("LITERAL", node, true);

    public override void VisitRegExpLiteral(RegExpLiteral node)
    {
        using var indent = new IndentedScope(this, "REGEXP LITERAL", node.position());
        PrintLiteralIndented("PATTERN", node.raw_pattern(), false);
        PrintIndented("FLAGS ");
        CallPrinter.AppendRegExpFlags(node.flags(), null, _output);
        Print("\n");
    }

    public override void VisitObjectLiteral(ObjectLiteral node)
    {
        using var indent = new IndentedScope(this, "OBJ LITERAL", node.position());
        PrintObjectProperties(node.properties());
    }

    private void PrintObjectProperties(List<ObjectLiteralProperty> properties)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            ObjectLiteralProperty property = properties[i];
            string prop_kind = property.kind() switch
            {
                ObjectLiteralProperty.Kind.CONSTANT => "CONSTANT",
                ObjectLiteralProperty.Kind.COMPUTED => "COMPUTED",
                ObjectLiteralProperty.Kind.MATERIALIZED_LITERAL => "MATERIALIZED_LITERAL",
                ObjectLiteralProperty.Kind.GETTER => "GETTER",
                ObjectLiteralProperty.Kind.SETTER => "SETTER",
                ObjectLiteralProperty.Kind.PROTOTYPE => "PROTOTYPE",
                _ => "SPREAD",
            };
            string buf = "PROPERTY - " + prop_kind;
            if (!property.emit_store())
            {
                if (property.is_first_instance_of_key())
                {
                    buf += " (no emit store, first instance, last at " +
                           property.last_instance_index().ToString(CultureInfo.InvariantCulture) + ")";
                }
                else
                {
                    buf += " (no emit store)";
                }
            }

            using var prop = new IndentedScope(this, buf);
            PrintIndentedVisit("KEY", properties[i].key());
            PrintIndentedVisit("VALUE", properties[i].value());
        }
    }

    public override void VisitArrayLiteral(ArrayLiteral node)
    {
        using var array_indent = new IndentedScope(this, "ARRAY LITERAL", node.position());
        if (node.values().Count > 0)
        {
            using var indent = new IndentedScope(this, "VALUES", node.position());
            for (int i = 0; i < node.values().Count; i++)
            {
                Visit(node.values()[i]);
            }
        }
    }

    public override void VisitVariableProxy(VariableProxy node)
    {
        string buf = "VAR PROXY";

        if (!node.is_resolved())
        {
            buf += " unresolved";
            PrintLiteralWithModeIndented(buf, null, node.raw_name());
        }
        else
        {
            Variable var = node.var();
            buf += var.location() switch
            {
                VariableLocation.UNALLOCATED => " unallocated",
                VariableLocation.PARAMETER => " parameter[" + var.index().ToString(CultureInfo.InvariantCulture) + "]",
                VariableLocation.LOCAL => " local[" + var.index().ToString(CultureInfo.InvariantCulture) + "]",
                VariableLocation.CONTEXT => " context[" + var.index().ToString(CultureInfo.InvariantCulture) + "]",
                VariableLocation.LOOKUP => " lookup",
                VariableLocation.MODULE => " module",
                _ => " repl global[" + var.index().ToString(CultureInfo.InvariantCulture) + "]",
            };
            PrintLiteralWithModeIndented(buf, var, node.raw_name());
        }
    }

    public override void VisitAssignment(Assignment node)
    {
        using var indent = new IndentedScope(this, Token.Name(node.op()), node.position());
        Visit(node.target());
        Visit(node.value());
    }

    public override void VisitCompoundAssignment(CompoundAssignment node) => VisitAssignment(node);

    public override void VisitYield(Yield node)
    {
        using var indent = new IndentedScope(this, "YIELD", node.position());
        Visit(node.expression());
    }

    public override void VisitYieldStar(YieldStar node)
    {
        using var indent = new IndentedScope(this, "YIELD_STAR", node.position());
        Visit(node.expression());
    }

    public override void VisitAwait(Await node)
    {
        using var indent = new IndentedScope(this, "AWAIT", node.position());
        Visit(node.expression());
    }

    public override void VisitThrow(Throw node)
    {
        using var indent = new IndentedScope(this, "THROW", node.position());
        Visit(node.exception());
    }

    public override void VisitOptionalChain(OptionalChain node)
    {
        using var indent = new IndentedScope(this, "OPTIONAL_CHAIN", node.position());
        Visit(node.expression());
    }

    public override void VisitProperty(Property node)
    {
        using var indent = new IndentedScope(this, "PROPERTY", node.position());

        Visit(node.obj());
        AssignType type = Property.GetAssignType(node);
        switch (type)
        {
            case AssignType.NAMED_PROPERTY:
            case AssignType.NAMED_SUPER_PROPERTY:
                PrintLiteralIndented("NAME", node.key().AsLiteral()!, false);
                break;
            case AssignType.PRIVATE_METHOD:
                PrintIndentedVisit("PRIVATE_METHOD", node.key());
                break;
            case AssignType.PRIVATE_GETTER_ONLY:
                PrintIndentedVisit("PRIVATE_GETTER_ONLY", node.key());
                break;
            case AssignType.PRIVATE_SETTER_ONLY:
                PrintIndentedVisit("PRIVATE_SETTER_ONLY", node.key());
                break;
            case AssignType.PRIVATE_GETTER_AND_SETTER:
                PrintIndentedVisit("PRIVATE_GETTER_AND_SETTER", node.key());
                break;
            case AssignType.KEYED_PROPERTY:
            case AssignType.KEYED_SUPER_PROPERTY:
                PrintIndentedVisit("KEY", node.key());
                break;
            case AssignType.PRIVATE_DEBUG_DYNAMIC:
                PrintIndentedVisit("PRIVATE_DEBUG_DYNAMIC", node.key());
                break;
            case AssignType.NON_PROPERTY:
                throw new InvalidOperationException("UNREACHABLE");
        }
    }

    public override void VisitCall(Call node)
    {
        using var indent = new IndentedScope(this, "CALL");
        Visit(node.expression());
        PrintArguments(node.arguments());
    }

    public override void VisitCallNew(CallNew node)
    {
        using var indent = new IndentedScope(this, "CALL NEW", node.position());
        Visit(node.expression());
        PrintArguments(node.arguments());
    }

    public override void VisitCallRuntime(CallRuntime node)
    {
        using var indent = new IndentedScope(this, "CALL RUNTIME " + node.function().name, node.position());
        PrintArguments(node.arguments());
    }

    public override void VisitUnaryOperation(UnaryOperation node)
    {
        using var indent = new IndentedScope(this, Token.Name(node.op()), node.position());
        Visit(node.expression());
    }

    public override void VisitCountOperation(CountOperation node)
    {
        string buf = (node.is_prefix() ? "PRE" : "POST") + " " + Token.Name(node.op());
        using var indent = new IndentedScope(this, buf, node.position());
        Visit(node.expression());
    }

    public override void VisitBinaryOperation(BinaryOperation node)
    {
        using var indent = new IndentedScope(this, Token.Name(node.op()), node.position());
        Visit(node.left());
        Visit(node.right());
    }

    public override void VisitNaryOperation(NaryOperation node)
    {
        using var indent = new IndentedScope(this, Token.Name(node.op()), node.position());
        Visit(node.first());
        for (int i = 0; i < node.subsequent_length(); ++i)
        {
            Visit(node.subsequent(i));
        }
    }

    public override void VisitCompareOperation(CompareOperation node)
    {
        using var indent = new IndentedScope(this, Token.Name(node.op()), node.position());
        Visit(node.left());
        Visit(node.right());
    }

    public override void VisitSpread(Spread node)
    {
        using var indent = new IndentedScope(this, "SPREAD", node.position());
        Visit(node.expression());
    }

    public override void VisitEmptyParentheses(EmptyParentheses node)
    {
        using var indent = new IndentedScope(this, "()", node.position());
    }

    public override void VisitGetTemplateObject(GetTemplateObject node)
    {
        using var indent = new IndentedScope(this, "GET-TEMPLATE-OBJECT", node.position());
    }

    public override void VisitTemplateLiteral(TemplateLiteral node)
    {
        using var indent = new IndentedScope(this, "TEMPLATE-LITERAL", node.position());
        AstRawString s = node.string_parts()[0];
        if (!s.IsEmpty()) PrintLiteralIndented("SPAN", s, true);
        for (int i = 0; i < node.substitutions().Count;)
        {
            PrintIndentedVisit("EXPR", node.substitutions()[i++]);
            if (i < node.string_parts().Count)
            {
                s = node.string_parts()[i];
                if (!s.IsEmpty()) PrintLiteralIndented("SPAN", s, true);
            }
        }
    }

    public override void VisitImportCallExpression(ImportCallExpression node)
    {
        using var indent = new IndentedScope(this, "IMPORT-CALL", node.position());
        PrintIndented("PHASE");
        Print(" ").Append((uint)node.phase()).Append('\n');
        Visit(node.specifier());
        if (node.import_options() != null)
        {
            Visit(node.import_options()!);
        }
    }

    public override void VisitThisExpression(ThisExpression node)
    {
        using var indent = new IndentedScope(this, "THIS-EXPRESSION", node.position());
    }

    public override void VisitSuperPropertyReference(SuperPropertyReference node)
    {
        using var indent = new IndentedScope(this, "SUPER-PROPERTY-REFERENCE", node.position());
    }

    public override void VisitSuperCallReference(SuperCallReference node)
    {
        using var indent = new IndentedScope(this, "SUPER-CALL-REFERENCE", node.position());
    }

    public override void VisitSuperCallForwardArgs(SuperCallForwardArgs node)
    {
        using var indent = new IndentedScope(this, "SUPER FORWARD-VARARGS", node.position());
        Visit(node.expression());
    }

    // printf("%g"): six significant digits, exponent form when the exponent
    // is below -4 or at least the precision, trailing zeros removed.
    public static string FormatG(double value)
    {
        if (double.IsNaN(value)) return double.IsNegative(value) ? "-nan" : "nan";
        if (double.IsInfinity(value)) return value < 0 ? "-inf" : "inf";
        if (value == 0) return double.IsNegative(value) ? "-0" : "0";
        const int precision = 6;
        string e = value.ToString("E" + (precision - 1).ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.InvariantCulture);
        int e_index = e.IndexOf('E');
        int exponent = int.Parse(e.AsSpan(e_index + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponent < -4 || exponent >= precision)
        {
            string mantissa = TrimZeros(e[..e_index]);
            return mantissa + "e" + (exponent < 0 ? "-" : "+") +
                   Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
        string fixed_text = value.ToString("F" + (precision - 1 - exponent).ToString(CultureInfo.InvariantCulture),
                                           CultureInfo.InvariantCulture);
        return TrimZeros(fixed_text);
    }

    private static string TrimZeros(string s)
    {
        if (s.IndexOf('.') < 0) return s;
        s = s.TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }
}
