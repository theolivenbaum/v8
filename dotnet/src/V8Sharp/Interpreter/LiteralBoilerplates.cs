// Port of the heap-building half of src/ast/ast.cc that the bytecode
// generator's finalization calls: ObjectLiteralBoilerplateBuilder::
// BuildBoilerplateDescription, ArrayLiteralBoilerplateBuilder::
// BuildBoilerplateDescription, LiteralBoilerplateBuilder::BuildConstants and
// GetBoilerplateValue, Literal::BuildValue and GetTemplateObject::
// GetOrBuildDescription.
//
// V8 writes the boilerplates straight into heap objects. Here the values are
// collected into plain descriptions (with leaf values materialized through the
// IBytecodeGeneratorHeap) and the heap turns each description into its heap
// object; the builder caches the result in boilerplate_description_, as V8 does.
using System.Globalization;
using V8Sharp.Ast;

// AST-side description: the parser's ElementsKind (same numbering as V8Sharp.Objects.ElementsKind; the engine casts when materializing).
using ElementsKind = V8Sharp.Ast.ElementsKind;
using ElementsKinds = V8Sharp.Ast.ElementsKinds;

namespace V8Sharp.Interpreter;

/// <summary>The content of an ObjectBoilerplateDescription: key/value pairs,
/// the backing store size and the literal-type flags.</summary>
public sealed class ObjectBoilerplateDescriptionData(int boilerplatePropertyCount, int backingStoreSize)
    : IPrintableConstant
{
    public int BoilerplatePropertyCount { get; } = boilerplatePropertyCount;
    public int BackingStoreSize { get; } = backingStoreSize;

    /// <summary>A materialized string, or a number (Smi / HeapNumber) for element indices.</summary>
    public object[] Keys { get; } = new object[boilerplatePropertyCount];

    /// <summary>Materialized literal values, nested boilerplate descriptions, or the uninitialized oddball.</summary>
    public object[] Values { get; } = new object[boilerplatePropertyCount];

    /// <summary>ObjectLiteral::EncodeLiteralType() (kFastElements, kHasNullPrototype).</summary>
    public int Flags { get; set; }

    public string InstanceTypeName => "OBJECT_BOILERPLATE_DESCRIPTION_TYPE";
    public string? PrintedValue => null;
    public string Brief() =>
        // ObjectBoilerplateDescription::New: capacity is 2 * boilerplate (flags and
        // backing_store_size are header fields).
        "<ObjectBoilerplateDescription[" + (2 * BoilerplatePropertyCount).ToString(CultureInfo.InvariantCulture) + "]>";
    public override string ToString() => Brief();
}

/// <summary>The content of an ArrayBoilerplateDescription: the elements kind
/// and the constant elements (FixedArray or FixedDoubleArray).</summary>
public sealed class ArrayBoilerplateDescriptionData(ElementsKind elementsKind, int length) : IPrintableConstant
{
    public ElementsKind ElementsKind { get; } = elementsKind;
    public bool UsesDoubles => ElementsKinds.IsDoubleElementsKind(ElementsKind);

    /// <summary>The FixedArray elements (null entries of a holey array are the hole) when !UsesDoubles.</summary>
    public object?[]? Elements { get; } = ElementsKinds.IsDoubleElementsKind(elementsKind) ? null : new object?[length];

    /// <summary>The FixedDoubleArray elements when UsesDoubles.</summary>
    public double[]? DoubleElements { get; } = ElementsKinds.IsDoubleElementsKind(elementsKind) ? new double[length] : null;

    /// <summary>The holes of a FixedDoubleArray.</summary>
    public bool[]? DoubleHoles { get; } = ElementsKinds.IsDoubleElementsKind(elementsKind) ? new bool[length] : null;

    /// <summary>The elements array has the copy-on-write map (simple, shallow, Smi/object kind).</summary>
    public bool IsCopyOnWrite { get; set; }

    public int Length => UsesDoubles ? DoubleElements!.Length : Elements!.Length;

    public string InstanceTypeName => "ARRAY_BOILERPLATE_DESCRIPTION_TYPE";
    public string? PrintedValue => null;
    public string Brief() => "<ArrayBoilerplateDescription " + ElementsKind + ", <" +
                             (UsesDoubles ? "FixedDoubleArray[" : "FixedArray[") +
                             Length.ToString(CultureInfo.InvariantCulture) + "]>>";
    public override string ToString() => Brief();
}

/// <summary>The content of a TemplateObjectDescription: raw and cooked strings
/// (the same array when they all match).</summary>
public sealed class TemplateObjectDescriptionData(object[] rawStrings, object[] cookedStrings) : IPrintableConstant
{
    public object[] RawStrings { get; } = rawStrings;
    public object[] CookedStrings { get; } = cookedStrings;

    public string InstanceTypeName => "TEMPLATE_OBJECT_DESCRIPTION_TYPE";
    public string? PrintedValue => null;
    public string Brief() => "<TemplateObjectDescription>";
    public override string ToString() => Brief();
}

public static class LiteralBoilerplates
{
    /// <summary>ObjectLiteralBoilerplateBuilder::GetOrBuildBoilerplateDescription.</summary>
    public static object GetOrBuildBoilerplateDescription(ObjectLiteralBoilerplateBuilder builder,
                                                          IBytecodeGeneratorHeap heap)
    {
        if (builder.boilerplate_description_ is null) BuildBoilerplateDescription(builder, heap);
        return builder.boilerplate_description_!;
    }

    /// <summary>ArrayLiteralBoilerplateBuilder::GetOrBuildBoilerplateDescription.</summary>
    public static object GetOrBuildBoilerplateDescription(ArrayLiteralBoilerplateBuilder builder,
                                                          IBytecodeGeneratorHeap heap)
    {
        if (builder.boilerplate_description_ is null) BuildBoilerplateDescription(builder, heap);
        return builder.boilerplate_description_!;
    }

    /// <summary>ObjectLiteralBoilerplateBuilder::BuildBoilerplateDescription.</summary>
    public static void BuildBoilerplateDescription(ObjectLiteralBoilerplateBuilder builder, IBytecodeGeneratorHeap heap)
    {
        if (builder.boilerplate_description_ is not null) return;

        builder.ComputeBoilerplateDescriptionSizes(out int boilerplate_property_count, out int backing_store_size);
        var boilerplate_description = new ObjectBoilerplateDescriptionData(boilerplate_property_count, backing_store_size);

        int position = 0;
        List<ObjectLiteralProperty> properties = builder.properties();
        for (int i = 0; i < properties.Count; i++)
        {
            ObjectLiteralProperty property = properties[i];
            if (property.IsPrototype()) continue;
            if (property.is_computed_name()) break;
            if (property.emit_store())
            {
                if (!property.is_first_instance_of_key()) continue;
            }
            else
            {
                if (!property.is_first_instance_of_key()) continue;
                property = properties[property.last_instance_index()];
            }

            Debug.Assert(!property.is_computed_name());

            MaterializedLiteral? m_literal = property.value().AsMaterializedLiteral();
            if (m_literal is not null)
            {
                BuildConstants(heap, m_literal);
            }

            // Add CONSTANT and COMPUTED properties to boilerplate. Use the
            // 'uninitialized' Oddball for COMPUTED properties, the real value is filled
            // in at runtime. The enumeration order is maintained.
            Literal key_literal = property.key().AsLiteral()!;
            object key;
            if (key_literal.AsArrayIndex(out uint element_index))
            {
                key = NewNumberFromUint(heap, element_index);
            }
            else
            {
                key = heap.RawString(key_literal.AsRawPropertyName());
            }
            object value = GetBoilerplateValue(property.value(), heap);
            boilerplate_description.Keys[position] = key;
            boilerplate_description.Values[position] = value;
            position++;
        }

        Debug.Assert(position == boilerplate_property_count);

        boilerplate_description.Flags = builder.EncodeLiteralType();

        builder.boilerplate_description_ = heap.NewObjectBoilerplateDescription(boilerplate_description);
    }

    /// <summary>ArrayLiteralBoilerplateBuilder::BuildBoilerplateDescription.</summary>
    public static void BuildBoilerplateDescription(ArrayLiteralBoilerplateBuilder builder, IBytecodeGeneratorHeap heap)
    {
        if (builder.boilerplate_description_ is not null) return;

        List<Expression> values = builder.values();
        int constants_length = builder.constants_length();
        ElementsKind kind = builder.boilerplate_descriptor_kind();
        bool use_doubles = ElementsKinds.IsDoubleElementsKind(kind);

        var elements = new ArrayBoilerplateDescriptionData(kind, constants_length);

        // Fill in the literals.
        int array_index = 0;
        for (; array_index < constants_length; array_index++)
        {
            Expression element = values[array_index];
            Debug.Assert(!element.IsSpread());
            if (use_doubles)
            {
                Literal? literal = element.AsLiteral();

                if (literal is not null && literal.type() == Literal.Type.kTheHole)
                {
                    Debug.Assert(ElementsKinds.IsHoleyElementsKind(kind));
                    elements.DoubleHoles![array_index] = true;
                    continue;
                }
                else if (literal is not null && literal.IsNumber())
                {
                    elements.DoubleElements![array_index] = literal.AsNumber();
                }
                else
                {
                    elements.DoubleElements![array_index] = 0;
                }
            }
            else
            {
                MaterializedLiteral? m_literal = element.AsMaterializedLiteral();
                if (m_literal is not null)
                {
                    BuildConstants(heap, m_literal);
                }

                if (element.IsTheHoleLiteral())
                {
                    Debug.Assert(ElementsKinds.IsHoleyElementsKind(kind));
                    elements.Elements![array_index] = heap.TheHole;
                    continue;
                }

                object boilerplate_value = LiteralBoilerplateBuilder.GetBoilerplateValueKind(element) ==
                                           LiteralBoilerplateBuilder.BoilerplateValueKind.Uninitialized
                    ? heap.Smi(Smi.Zero)
                    : GetBoilerplateValue(element, heap);

                elements.Elements![array_index] = boilerplate_value;
            }
        }

        // Simple and shallow arrays can be lazily copied, we transform the
        // elements array to a copy-on-write array.
        if (builder.is_simple() && builder.depth() == LiteralBoilerplateBuilder.DepthKind.kShallow && array_index > 0 &&
            ElementsKinds.IsSmiOrObjectElementsKind(kind))
        {
            elements.IsCopyOnWrite = true;
        }

        builder.boilerplate_description_ = heap.NewArrayBoilerplateDescription(elements);
    }

    /// <summary>LiteralBoilerplateBuilder::BuildConstants.</summary>
    static void BuildConstants(IBytecodeGeneratorHeap heap, MaterializedLiteral expr)
    {
        if (expr.IsArrayLiteral())
        {
            BuildBoilerplateDescription(((ArrayLiteral)expr).builder(), heap);
            return;
        }
        if (expr.IsObjectLiteral())
        {
            BuildBoilerplateDescription(((ObjectLiteral)expr).builder(), heap);
        }
        // RegExpLiteral: nothing to build.
    }

    /// <summary>LiteralBoilerplateBuilder::GetBoilerplateValue.</summary>
    public static object GetBoilerplateValue(Expression expression, IBytecodeGeneratorHeap heap)
    {
        if (expression.IsLiteral())
        {
            return BuildValue(expression.AsLiteral()!, heap);
        }
        if (expression.IsCompileTimeValue())
        {
            if (expression.IsObjectLiteral())
            {
                ObjectLiteral object_literal = expression.AsObjectLiteral()!;
                Debug.Assert(object_literal.builder().is_simple());
                return object_literal.builder().boilerplate_description_!;
            }
            else
            {
                Debug.Assert(expression.IsArrayLiteral());
                ArrayLiteral array_literal = expression.AsArrayLiteral()!;
                Debug.Assert(array_literal.builder().is_simple());
                return array_literal.builder().boilerplate_description_!;
            }
        }
        return heap.Oddball(BoilerplateOddball.Uninitialized);
    }

    /// <summary>Literal::BuildValue.</summary>
    public static object BuildValue(Literal literal, IBytecodeGeneratorHeap heap) => literal.type() switch
    {
        Literal.Type.kSmi => heap.Smi(Smi.FromInt(literal.AsSmiLiteral())),
        Literal.Type.kHeapNumber => NewNumber(heap, literal.AsNumber()),
        Literal.Type.kString => heap.RawString(literal.AsRawString()),
        Literal.Type.kConsString => heap.ConsString(literal.AsConsString()),
        Literal.Type.kBoolean => heap.Oddball(literal.AsBooleanLiteral() ? BoilerplateOddball.True : BoilerplateOddball.False),
        Literal.Type.kNull => heap.Oddball(BoilerplateOddball.Null),
        Literal.Type.kUndefined => heap.Oddball(BoilerplateOddball.Undefined),
        Literal.Type.kTheHole => heap.TheHole,
        Literal.Type.kBigInt => heap.BigInt(literal.AsBigInt()),
        _ => throw new UnreachableException(),
    };

    // Factory::NewNumber: a Smi when the value is a Smi (DoubleToSmiInteger), else a HeapNumber.
    static object NewNumber(IBytecodeGeneratorHeap heap, double value)
    {
        if (value == Math.Truncate(value) && !(value == 0 && double.IsNegative(value)) &&
            value >= Smi.kMinValue && value <= Smi.kMaxValue)
        {
            return heap.Smi(Smi.FromInt((int)value));
        }
        return heap.HeapNumber(value);
    }

    // Factory::NewNumberFromUint.
    static object NewNumberFromUint(IBytecodeGeneratorHeap heap, uint value) =>
        value <= Smi.kMaxValue ? heap.Smi(Smi.FromInt((int)value)) : heap.HeapNumber(value);

    /// <summary>GetTemplateObject::GetOrBuildDescription.</summary>
    public static object GetOrBuildDescription(GetTemplateObject expr, IBytecodeGeneratorHeap heap)
    {
        List<AstRawString> raw = expr.raw_strings();
        List<AstRawString?> cooked = expr.cooked_strings();
        var raw_strings = new object[raw.Count];
        bool raw_and_cooked_match = true;
        for (int i = 0; i < raw.Count; ++i)
        {
            if (raw[i] != cooked[i])
            {
                // If the AstRawStrings don't match, then neither should the allocated
                // Strings, since the AstValueFactory should have deduplicated them
                // already.
                raw_and_cooked_match = false;
            }
            raw_strings[i] = heap.RawString(raw[i]);
        }
        object[] cooked_strings = raw_strings;
        if (!raw_and_cooked_match)
        {
            cooked_strings = new object[cooked.Count];
            for (int i = 0; i < cooked.Count; ++i)
            {
                cooked_strings[i] = cooked[i] is { } s
                    ? heap.RawString(s)
                    : heap.Oddball(BoilerplateOddball.Undefined);
            }
        }
        return heap.NewTemplateObjectDescription(new TemplateObjectDescriptionData(raw_strings, cooked_strings));
    }
}
