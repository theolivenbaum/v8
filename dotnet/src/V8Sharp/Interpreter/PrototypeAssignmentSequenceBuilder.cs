// Port of src/interpreter/prototype-assignment-sequence-builder.h/.cc.
using V8Sharp.Ast;

namespace V8Sharp.Interpreter;

/// <summary>PrototypeAssignment: std::pair&lt;const AstRawString*, Expression*&gt;.</summary>
public readonly record struct PrototypeAssignment(AstRawString Key, Expression Value);

public sealed class ProtoAssignmentSeqBuilder(List<PrototypeAssignment> properties)
{
    readonly List<PrototypeAssignment> _properties = properties;
    object? _boilerplateDescription;

    // Populate the boilerplate description.
    public void BuildBoilerplateDescription(IBytecodeGeneratorHeap heap)
    {
        if (_boilerplateDescription is not null) return;
        int prop_sz = _properties.Count;
        var boilerplate_description = new ObjectBoilerplateDescriptionData(prop_sz, prop_sz);
        int position = 0;
        for (int i = 0; i < _properties.Count; i++)
        {
            PrototypeAssignment pair = _properties[i];
            object key = heap.RawString(pair.Key);
            object value = GetBoilerplateValue(pair.Value, heap);
            boilerplate_description.Keys[position] = key;
            boilerplate_description.Values[position] = value;
            position++;
        }
        boilerplate_description.Flags = (int)AggregateLiteral.Flags.kNoFlags;
        _boilerplateDescription = heap.NewObjectBoilerplateDescription(boilerplate_description);
    }

    // Get the boilerplate description, populating it if necessary.
    public object GetOrBuildBoilerplateDescription(IBytecodeGeneratorHeap heap)
    {
        if (_boilerplateDescription is null) BuildBoilerplateDescription(heap);
        return _boilerplateDescription!;
    }

    public object boilerplate_description()
    {
        Debug.Assert(_boilerplateDescription is not null);
        return _boilerplateDescription!;
    }

    public static object GetBoilerplateValue(Expression expression, IBytecodeGeneratorHeap heap)
    {
        if (expression.IsLiteral())
        {
            return LiteralBoilerplates.BuildValue(expression.AsLiteral()!, heap);
        }
        if (expression.IsFunctionLiteral())
        {
            object? shared_info = heap.GetSharedFunctionInfo(expression.AsFunctionLiteral()!);
            Debug.Assert(shared_info is not null);
            return shared_info!;
        }
        throw new UnreachableException();
    }

    public List<PrototypeAssignment> properties() => _properties;
}
