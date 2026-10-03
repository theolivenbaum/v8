// Port of the boilerplate descriptions of src/objects/literal-objects.h and
// src/objects/template-objects.h: ObjectBoilerplateDescription,
// ArrayBoilerplateDescription, RegExpBoilerplateDescription and
// TemplateObjectDescription. The bytecode generator's constant pool holds
// them; the literal runtime functions (RuntimeLiterals.cs) build objects
// from them. ClassBoilerplate is in ClassBoilerplate.cs.
namespace V8Sharp.Objects;

/// <summary>V8's ObjectBoilerplateDescription: the constant properties of an object literal.</summary>
public sealed class ObjectBoilerplateDescription(int boilerplatePropertiesCount, int backingStoreSize, int flags)
    : HeapObject(InstanceType.ObjectBoilerplateDescriptionType)
{
    readonly JSValue[] _names = boilerplatePropertiesCount == 0 ? [] : new JSValue[boilerplatePropertiesCount];
    readonly JSValue[] _values = boilerplatePropertiesCount == 0 ? [] : new JSValue[boilerplatePropertiesCount];

    /// <summary>ObjectLiteral::Flags (kFastElements, kHasNullPrototype ...).</summary>
    public readonly int Flags = flags;

    /// <summary>The number of properties the literal will have (backing_store_size).</summary>
    public readonly int BackingStoreSize = backingStoreSize;

    public int BoilerplatePropertiesCount => _names.Length;

    /// <summary>The key: an internalized string or a number (array index).</summary>
    public JSValue Name(int index) => _names[index];

    /// <summary>The value: a constant, a nested boilerplate description, or the uninitialized oddball.</summary>
    public JSValue Value(int index) => _values[index];

    public void SetKeyValue(int index, JSValue key, JSValue value)
    {
        _names[index] = key;
        _values[index] = value;
    }

    public override string ToString() => $"<ObjectBoilerplateDescription[{2 * _names.Length}]>";
}

/// <summary>V8's ArrayBoilerplateDescription: the elements kind and constant elements of an array literal.</summary>
public sealed class ArrayBoilerplateDescription(ElementsKind elementsKind, FixedArrayBase constantElements)
    : HeapObject(InstanceType.ArrayBoilerplateDescriptionType)
{
    public readonly ElementsKind ElementsKind = elementsKind;

    /// <summary>A FixedArray (copy-on-write when shallow and simple) or a FixedDoubleArray.</summary>
    public readonly FixedArrayBase ConstantElements = constantElements;

    public bool IsEmpty() => ConstantElements.Length == 0;

    public override string ToString() => $"<ArrayBoilerplateDescription {ElementsKind}>";
}

/// <summary>V8's RegExpBoilerplateDescription: the compiled data and flags of a regexp literal site.</summary>
public sealed class RegExpBoilerplateDescription(HeapObject data, JSString source, int flags)
    : HeapObject(InstanceType.RegExpBoilerplateDescriptionType)
{
    public readonly HeapObject Data = data;
    public readonly JSString Source = source;
    public readonly int Flags = flags;
}

/// <summary>V8's TemplateObjectDescription: the raw and cooked strings of a tagged template.</summary>
public sealed class TemplateObjectDescription(FixedArray rawStrings, FixedArray cookedStrings)
    : HeapObject(InstanceType.TemplateObjectDescriptionType)
{
    public readonly FixedArray RawStrings = rawStrings;
    public readonly FixedArray CookedStrings = cookedStrings;
}
