// The heap-facing side of bytecode generation: what BytecodeGenerator::
// FinalizeBytecode and AllocateDeferredConstants ask of the isolate
// (Compiler::GetSharedFunctionInfo, the boilerplate and template-object
// factories, ClassBoilerplate::New, FixedArrays for the global declarations,
// CoverageInfo, the eval ScopeInfo list of the Script) plus the constant-pool
// materialization of IConstantPoolMaterializer.
//
// The generator produces plain data descriptions (LiteralBoilerplates.cs); the
// engine implements IBytecodeGeneratorHeap to turn them into heap objects.
// DefaultBytecodeGeneratorHeap keeps the descriptions themselves, which is what
// the tests and tools use until the object model lands.
// TODO(merge): the object-model port implements IBytecodeGeneratorHeap over
// its Factory (internalized strings, ScopeInfo, SharedFunctionInfo,
// ObjectBoilerplateDescription, ArrayBoilerplateDescription, ClassBoilerplate,
// TemplateObjectDescription, CoverageInfo).
using System.Globalization;
using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Interpreter;

public interface IBytecodeGeneratorHeap : IConstantPoolMaterializer
{
    /// <summary>Compiler::GetSharedFunctionInfo(literal, script, isolate): the
    /// existing SharedFunctionInfo for the literal's function_literal_id, or a
    /// new one. Null on failure (stack overflow).</summary>
    object? GetSharedFunctionInfo(FunctionLiteral literal);

    /// <summary>FunctionTemplateInfo::GetOrCreateSharedFunctionInfo for a v8::Extension native function.</summary>
    object GetNativeFunctionSharedFunctionInfo(NativeFunctionLiteral literal);

    /// <summary>Factory::NewObjectBoilerplateDescription, filled from |description|.</summary>
    object NewObjectBoilerplateDescription(ObjectBoilerplateDescriptionData description);

    /// <summary>Factory::NewArrayBoilerplateDescription, filled from |description|.</summary>
    object NewArrayBoilerplateDescription(ArrayBoilerplateDescriptionData description);

    /// <summary>ClassBoilerplate::New(isolate, class_literal).</summary>
    object NewClassBoilerplate(ClassLiteral literal);

    /// <summary>Factory::NewTemplateObjectDescription.</summary>
    object NewTemplateObjectDescription(TemplateObjectDescriptionData description);

    /// <summary>Factory::NewFixedArray with these (already materialized) elements.</summary>
    object NewFixedArray(object[] elements);

    /// <summary>Factory::NewCoverageInfo(slots).</summary>
    object NewCoverageInfo(IReadOnlyList<SourceRange> slots);

    /// <summary>script-&gt;infos()-&gt;set(eval_scope_info_index, MakeWeak(scope-&gt;scope_info())).</summary>
    void RecordEvalScopeInfo(int evalScopeInfoIndex, Scope scope);

    /// <summary>The oddball for a boolean / null / undefined literal value (Literal::BuildValue).</summary>
    object Oddball(BoilerplateOddball oddball);
}

/// <summary>The oddballs a boilerplate can contain.</summary>
public enum BoilerplateOddball : byte { True, False, Null, Undefined, TheHole, Uninitialized }

/// <summary>A SharedFunctionInfo stand-in: the FunctionLiteral's data, and the
/// bytecode once the function is compiled.</summary>
// TODO(merge): the object model's SharedFunctionInfo replaces this.
public sealed class SharedFunctionInfoDescription(FunctionLiteral literal) : IPrintableConstant
{
    public FunctionLiteral Literal { get; } = literal;
    public int FunctionLiteralId { get; } = literal.function_literal_id();
    // SharedFunctionInfo::Name(): the literal's own name, not the inferred one.
    public string Name { get; } = literal.raw_name() is { } name && !name.IsEmpty() ? literal.GetDebugName() : "";
    public FunctionKind Kind { get; } = literal.kind();
    public LanguageMode LanguageMode { get; } = literal.language_mode();
    public int ParameterCount { get; } = literal.parameter_count();
    public int FunctionLength { get; } = literal.function_length();
    public int ExpectedPropertyCount { get; } = literal.expected_property_count();
    public int StartPosition { get; } = literal.start_position();
    public int EndPosition { get; } = literal.end_position();

    /// <summary>Set by the compilation pipeline when the function is compiled.</summary>
    public BytecodeArray? BytecodeArray { get; set; }

    /// <summary>The feedback slots of the compiled function (FeedbackMetadata's source).</summary>
    public FeedbackVectorSpec? FeedbackVectorSpec { get; set; }

    /// <summary>The CoverageInfo, when block coverage is enabled.</summary>
    public object? CoverageInfo { get; set; }

    public bool IsCompiled => BytecodeArray is not null;

    public string InstanceTypeName => "SHARED_FUNCTION_INFO_TYPE";
    public string? PrintedValue => null;
    public string Brief() => Name.Length == 0 ? "<SharedFunctionInfo>" : "<SharedFunctionInfo " + Name + ">";
    public override string ToString() => Brief();
}

/// <summary>A FixedArray of materialized constants (the global declarations array).</summary>
public sealed class FixedArrayDescription(object[] elements) : IPrintableConstant
{
    public object[] Elements { get; } = elements;
    public string InstanceTypeName => "FIXED_ARRAY_TYPE";
    public string? PrintedValue => null;
    public string Brief() => "<FixedArray[" + Elements.Length.ToString(CultureInfo.InvariantCulture) + "]>";
    public override string ToString() => Brief();
}

/// <summary>ClassBoilerplate::New's input: the class literal.</summary>
// TODO(merge): port ClassBoilerplate::New (src/objects/literal-objects.cc) with
// the object model; the generator only needs the constant-pool entry.
public sealed class ClassBoilerplateDescription(ClassLiteral literal) : IPrintableConstant
{
    public ClassLiteral Literal { get; } = literal;
    public string InstanceTypeName => "CLASS_BOILERPLATE_TYPE";
    public string? PrintedValue => null;
    public string Brief() => "<ClassBoilerplate>";
    public override string ToString() => Brief();
}

/// <summary>A CoverageInfo: the source ranges of the block coverage slots.</summary>
public sealed class CoverageInfoDescription(IReadOnlyList<SourceRange> slots) : IPrintableConstant
{
    public IReadOnlyList<SourceRange> Slots { get; } = slots;
    public string InstanceTypeName => "COVERAGE_INFO_TYPE";
    public string? PrintedValue => null;
    public string Brief() => "<CoverageInfo[" + Slots.Count.ToString(CultureInfo.InvariantCulture) + "]>";
}

/// <summary>
/// An IBytecodeGeneratorHeap without a heap: strings stay C# strings, other
/// constants are printable descriptions, shared function infos are
/// SharedFunctionInfoDescriptions cached by function literal id (as V8 finds
/// them in the Script's infos list).
/// </summary>
public class DefaultBytecodeGeneratorHeap : IBytecodeGeneratorHeap
{
    readonly Dictionary<int, SharedFunctionInfoDescription> _sharedFunctionInfos = [];
    readonly Dictionary<int, Scope> _evalScopeInfos = [];
    readonly IConstantPoolMaterializer _materializer;

    static readonly PrintableConstant s_true = new("ODDBALL_TYPE", null, "<true>");
    static readonly PrintableConstant s_false = new("ODDBALL_TYPE", null, "<false>");
    static readonly PrintableConstant s_null = new("ODDBALL_TYPE", null, "<null>");
    static readonly PrintableConstant s_undefined = new("ODDBALL_TYPE", null, "<undefined>");
    static readonly PrintableConstant s_uninitialized = new("HOLE_TYPE", null, "<uninitialized_value>");

    public DefaultBytecodeGeneratorHeap(IConstantPoolMaterializer? materializer = null)
    {
        _materializer = materializer ?? DefaultConstantPoolMaterializer.Instance;
    }

    public IReadOnlyDictionary<int, SharedFunctionInfoDescription> SharedFunctionInfos => _sharedFunctionInfos;
    public IReadOnlyDictionary<int, Scope> EvalScopeInfos => _evalScopeInfos;

    public virtual object? GetSharedFunctionInfo(FunctionLiteral literal) => GetOrCreateSharedFunctionInfo(literal);

    public SharedFunctionInfoDescription GetOrCreateSharedFunctionInfo(FunctionLiteral literal)
    {
        int id = literal.function_literal_id();
        if (!_sharedFunctionInfos.TryGetValue(id, out SharedFunctionInfoDescription? sfi))
        {
            sfi = new SharedFunctionInfoDescription(literal);
            _sharedFunctionInfos.Add(id, sfi);
        }
        return sfi;
    }

    public virtual object GetNativeFunctionSharedFunctionInfo(NativeFunctionLiteral literal) =>
        new PrintableConstant("SHARED_FUNCTION_INFO_TYPE", null,
                              "<SharedFunctionInfo " + literal.raw_name().Value + ">");

    public virtual object NewObjectBoilerplateDescription(ObjectBoilerplateDescriptionData description) => description;
    public virtual object NewArrayBoilerplateDescription(ArrayBoilerplateDescriptionData description) => description;
    public virtual object NewClassBoilerplate(ClassLiteral literal) => new ClassBoilerplateDescription(literal);
    public virtual object NewTemplateObjectDescription(TemplateObjectDescriptionData description) => description;
    public virtual object NewFixedArray(object[] elements) => new FixedArrayDescription(elements);
    public virtual object NewCoverageInfo(IReadOnlyList<SourceRange> slots) => new CoverageInfoDescription(slots);

    public virtual void RecordEvalScopeInfo(int evalScopeInfoIndex, Scope scope) =>
        _evalScopeInfos[evalScopeInfoIndex] = scope;

    public virtual object Oddball(BoilerplateOddball oddball) => oddball switch
    {
        BoilerplateOddball.True => s_true,
        BoilerplateOddball.False => s_false,
        BoilerplateOddball.Null => s_null,
        BoilerplateOddball.Undefined => s_undefined,
        BoilerplateOddball.TheHole => TheHole,
        BoilerplateOddball.Uninitialized => s_uninitialized,
        _ => throw new UnreachableException(),
    };

    // IConstantPoolMaterializer.
    public virtual object RawString(object rawString) =>
        _materializer.RawString(rawString is AstRawString s ? s.Value : rawString);
    public virtual object ConsString(object consString) =>
        _materializer.ConsString(consString is AstConsString s ? s.ToFlatString() : consString);
    public virtual object BigInt(object bigint) =>
        _materializer.BigInt(bigint is AstBigInt b ? b.c_str() : bigint);
    public virtual object ScopeInfo(object scope) => _materializer.ScopeInfo(scope);
    public virtual object Singleton(SingletonConstant singleton) => _materializer.Singleton(singleton);
    public virtual object HeapNumber(double value) => _materializer.HeapNumber(value);
    public virtual object Smi(Smi value) => _materializer.Smi(value);
    public virtual object TheHole => _materializer.TheHole;
}
