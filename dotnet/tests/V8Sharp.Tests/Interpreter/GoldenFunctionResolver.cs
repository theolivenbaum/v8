// Finds the function a golden snippet leaves in a global variable (or as the
// script's completion value) without running the script: a small static
// evaluator over the forms the golden files use (function declarations,
// function and class literals, assignments, `new C().m`, `C.m`, calls that
// return functions, `arguments.callee`, direct eval with a string literal).
using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public sealed class GoldenFunctionResolver
{
    readonly GoldenBytecodeCompiler.CompiledScript _compiled;
    readonly BytecodeExpectationsHeaderOptions _options;
    readonly List<AstNode> _nodes;
    readonly Dictionary<FunctionLiteral, BytecodeArray> _functions;

    sealed class Collector(AstNode root) : AstTraversalVisitor(root)
    {
        public readonly List<AstNode> Nodes = [];

        protected override bool VisitNode(AstNode node)
        {
            Nodes.Add(node);
            return true;
        }
    }

    public GoldenFunctionResolver(GoldenBytecodeCompiler.CompiledScript compiled, BytecodeExpectationsHeaderOptions options)
    {
        _compiled = compiled;
        _options = options;
        _functions = new Dictionary<FunctionLiteral, BytecodeArray>(compiled.Functions, ReferenceEqualityComparer.Instance);
        _nodes = Collect(compiled.ParseInfo.literal()!);
    }

    static List<AstNode> Collect(AstNode root)
    {
        var collector = new Collector(root);
        collector.Run();
        return collector.Nodes;
    }

    public BytecodeArray BytecodeOf(FunctionLiteral literal) =>
        _functions.TryGetValue(literal, out BytecodeArray? bytecode)
            ? bytecode
            : throw new InvalidOperationException("function " + literal.GetDebugName() + " was not compiled");

    static bool IsGlobalBinding(Variable var) =>
        var.location() is VariableLocation.UNALLOCATED or VariableLocation.LOOKUP or VariableLocation.REPL_GLOBAL;

    /// <summary>The function the global |name| refers to after the script ran.</summary>
    public FunctionLiteral? ResolveGlobal(string name)
    {
        FunctionLiteral top = _compiled.ParseInfo.literal()!;
        AstNode? result = null;
        foreach (Declaration decl in top.scope().declarations())
        {
            if (decl is FunctionDeclaration fd && decl.var()!.raw_name().Value == name) result = fd.fun();
        }
        foreach (AstNode node in _nodes)
        {
            if (node is Assignment a && a.target() is VariableProxy p && p.is_resolved() &&
                p.raw_name().Value == name && IsGlobalBinding(p.var()))
            {
                AstNode? value = Evaluate(a.value(), null);
                if (value is not null) result = value;
            }
        }
        return AsFunction(result);
    }

    /// <summary>The function the script's completion value is (print callee).</summary>
    public FunctionLiteral? ResolveCompletionValue()
    {
        FunctionLiteral top = _compiled.ParseInfo.literal()!;
        // The rewriter turns the completion value into assignments to .result.
        AstNode? result = null;
        foreach (AstNode node in _nodes)
        {
            if (node is Assignment a && a.target() is VariableProxy p && p.raw_name().Value == ".result")
            {
                AstNode? value = Evaluate(a.value(), top);
                if (value is not null) result = value;
            }
        }
        return AsFunction(result);
    }

    static FunctionLiteral? AsFunction(AstNode? node) => node switch
    {
        FunctionLiteral f => f,
        ClassLiteral c => c.constructor(),
        _ => null,
    };

    // Evaluates |expr| to a function or class literal, if it is one of the
    // supported forms. |current| is the function whose body contains |expr|.
    AstNode? Evaluate(Expression expr, FunctionLiteral? current)
    {
        switch (expr)
        {
            case FunctionLiteral f:
                return f;
            case ClassLiteral c:
                return c;
            case Assignment a:
                return Evaluate(a.value(), current);
            case VariableProxy p when p.is_resolved():
                return ResolveVariable(p.var());
            case Call call:
            {
                AstNode? callee = Evaluate(call.expression(), current);
                return callee is FunctionLiteral f ? EvaluateReturn(f) : null;
            }
            case Property prop:
                return EvaluateProperty(prop, current);
            default:
                return null;
        }
    }

    AstNode? ResolveVariable(Variable var)
    {
        AstNode? result = null;
        foreach (AstNode node in _nodes)
        {
            if (node is FunctionDeclaration fd && fd.var() == var) result = fd.fun();
            if (node is Assignment a && a.target() is VariableProxy p && p.is_resolved() && p.var() == var)
            {
                AstNode? value = Evaluate(a.value(), null);
                if (value is not null) result = value;
            }
        }
        return result;
    }

    AstNode? EvaluateProperty(Property prop, FunctionLiteral? current)
    {
        if (!prop.key().IsPropertyName()) return null;
        string name = prop.key().AsLiteral()!.AsRawPropertyName().Value;

        // arguments.callee
        if (name == "callee" && prop.obj() is VariableProxy args && args.raw_name().Value == "arguments")
        {
            return current;
        }

        if (prop.obj() is CallNew call_new)
        {
            AstNode? constructor = Evaluate(call_new.expression(), current);
            if (name == "constructor") return constructor;
            return InstanceMember(constructor, name);
        }

        AstNode? receiver = Evaluate(prop.obj(), current);
        if (receiver is ClassLiteral cls) return StaticMember(cls, name);
        return null;
    }

    AstNode? InstanceMember(AstNode? constructor, string name)
    {
        switch (constructor)
        {
            case ClassLiteral cls:
            {
                foreach (ClassLiteralProperty property in cls.public_members() ?? [])
                {
                    if (!property.is_static() && !property.is_computed_name() && property.key().IsPropertyName() &&
                        property.key().AsLiteral()!.AsRawPropertyName().Value == name &&
                        property.kind() == ClassLiteralProperty.Kind.METHOD)
                    {
                        return property.value();
                    }
                }
                return cls.extends() is { } base_class ? InstanceMember(Evaluate(base_class, null), name) : null;
            }
            case FunctionLiteral f:
            {
                // this.name = <value> in the constructor function's body.
                AstNode? result = null;
                foreach (AstNode node in BodyNodes(f))
                {
                    if (node is Assignment a && a.target() is Property target && target.obj().IsThisExpression() &&
                        target.key().IsPropertyName() && target.key().AsLiteral()!.AsRawPropertyName().Value == name)
                    {
                        result = Evaluate(a.value(), f);
                    }
                }
                return result;
            }
            default:
                return null;
        }
    }

    static AstNode? StaticMember(ClassLiteral cls, string name)
    {
        foreach (ClassLiteralProperty property in cls.public_members() ?? [])
        {
            if (property.is_static() && !property.is_computed_name() && property.key().IsPropertyName() &&
                property.key().AsLiteral()!.AsRawPropertyName().Value == name)
            {
                return property.value();
            }
        }
        return null;
    }

    // The nodes of |f|'s body, not descending into nested functions.
    static IEnumerable<AstNode> BodyNodes(FunctionLiteral f)
    {
        foreach (Statement stmt in f.body())
        {
            foreach (AstNode node in Collect(stmt))
            {
                yield return node;
            }
        }
    }

    // The value of the last return statement of |f| (a call of f).
    AstNode? EvaluateReturn(FunctionLiteral f)
    {
        AstNode? result = null;
        foreach (AstNode node in BodyNodes(f))
        {
            if (node is ReturnStatement ret && EnclosingFunctionIs(ret, f))
            {
                AstNode? value = Evaluate(ret.expression(), f);
                if (value is not null) result = value;
            }
        }
        return result;
    }

    // Whether |node| belongs to |f| rather than a function nested in it.
    bool EnclosingFunctionIs(AstNode node, FunctionLiteral f)
    {
        int position = node.position();
        foreach (FunctionLiteral inner in _functions.Keys)
        {
            if (inner != f && inner.start_position() > f.start_position() && inner.end_position() <= f.end_position() &&
                position >= inner.start_position() && position < inner.end_position())
            {
                return false;
            }
        }
        return true;
    }
}
