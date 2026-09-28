// Port of ClassBoilerplate (src/objects/literal-objects.{h,cc}): the static
// description of a class literal that Runtime_DefineClass instantiates.
//
// Deviation: V8 precomputes descriptor-array / dictionary templates for the
// class constructor and the prototype (ObjectDescriptor, AddToDictionaryTemplate)
// and substitutes the closures into copies of them. V8Sharp keeps the member
// list in source order with the same argument indices and DefineClass
// defines the members one by one (ClassDefinitionEvaluation's order), which
// gives the same property order and values. The resulting objects are in
// dictionary mode exactly when V8's would be (a computed member, or more
// members than fit in a descriptor array).
using V8Sharp.Ast;

namespace V8Sharp.Objects;

public sealed class ClassBoilerplate() : HeapObject(InstanceType.ClassBoilerplateType)
{
    /// <summary>ClassBoilerplate::ValueKind.</summary>
    public enum ValueKind : byte { kData, kGetter, kSetter, kAutoAccessor }

    // DefineClassArgumentsIndices.
    public const int kConstructorArgumentIndex = 1;
    public const int kPrototypeArgumentIndex = 2;
    public const int kFirstDynamicArgumentIndex = 3;

    public const int kMinimumClassPropertiesCount = 6;
    public const int kMinimumPrototypePropertiesCount = 1;

    /// <summary>A class member that DefineClass installs (fields are installed by their initializer).</summary>
    public readonly struct Member(bool isStatic, ValueKind kind, bool isComputed, JSValue key, int keyIndex, int valueIndex)
    {
        public readonly bool IsStatic = isStatic;
        public readonly ValueKind Kind = kind;
        /// <summary>The key is the dynamic argument at <see cref="KeyIndex"/> (a Name after ToName).</summary>
        public readonly bool IsComputed = isComputed;
        /// <summary>The literal key: an internalized string, or a number for an array index.</summary>
        public readonly JSValue Key = key;
        public readonly int KeyIndex = keyIndex;
        /// <summary>The argument index of the closure (the getter for an auto-accessor; the setter follows).</summary>
        public readonly int ValueIndex = valueIndex;
    }

    public int ArgumentsCount;
    public Member[] Members = [];
    public int StartPosition;
    public int EndPosition;

    /// <summary>The static side has computed members or too many members for descriptors.</summary>
    public bool StaticIsDictionary;

    /// <summary>The prototype side has computed members or too many members for descriptors.</summary>
    public bool InstanceIsDictionary;

    /// <summary>ClassBoilerplate::New.</summary>
    public static ClassBoilerplate New(Isolate isolate, ClassLiteral expr)
    {
        List<ClassLiteralProperty> publicMembers = expr.public_members() ?? [];
        var members = new List<Member>(publicMembers.Count);
        int staticComputed = 0, staticCount = 0, instanceComputed = 0, instanceCount = 0;

        int dynamicArgumentIndex = kFirstDynamicArgumentIndex;
        for (int i = 0; i < publicMembers.Count; i++)
        {
            ClassLiteralProperty property = publicMembers[i];
            ValueKind valueKind;
            int valueIndex = dynamicArgumentIndex;
            switch (property.kind())
            {
                case ClassLiteralProperty.Kind.METHOD:
                    valueKind = ValueKind.kData;
                    break;
                case ClassLiteralProperty.Kind.GETTER:
                    valueKind = ValueKind.kGetter;
                    break;
                case ClassLiteralProperty.Kind.SETTER:
                    valueKind = ValueKind.kSetter;
                    break;
                case ClassLiteralProperty.Kind.FIELD:
                    if (property.is_computed_name()) ++dynamicArgumentIndex;
                    continue;
                default:
                    valueKind = ValueKind.kAutoAccessor;
                    // Auto-accessors have two arguments (getter and setter).
                    ++dynamicArgumentIndex;
                    break;
            }

            if (property.is_static())
            {
                if (property.is_computed_name()) staticComputed++; else staticCount++;
            }
            else
            {
                if (property.is_computed_name()) instanceComputed++; else instanceCount++;
            }

            if (property.is_computed_name())
            {
                int computedNameIndex = valueIndex;
                dynamicArgumentIndex += 2;  // Computed name and value indices.
                members.Add(new Member(property.is_static(), valueKind, true, JSValue.Undefined, computedNameIndex,
                    computedNameIndex + 1));
                continue;
            }
            dynamicArgumentIndex++;

            Literal keyLiteral = property.key().AsLiteral()!;
            JSValue key = keyLiteral.AsArrayIndex(out uint index)
                ? JSValue.FromNumber(index)
                : isolate.Factory.InternalizeString(keyLiteral.AsRawPropertyName().Value);
            members.Add(new Member(property.is_static(), valueKind, false, key, -1, valueIndex));
        }

        return new ClassBoilerplate
        {
            ArgumentsCount = dynamicArgumentIndex,
            Members = members.ToArray(),
            StartPosition = expr.start_position(),
            EndPosition = expr.end_position(),
            StaticIsDictionary = staticComputed > 0 ||
                                 staticCount + kMinimumClassPropertiesCount > DescriptorArray.kMaxNumberOfDescriptors,
            InstanceIsDictionary = instanceComputed > 0 ||
                                   instanceCount + kMinimumPrototypePropertiesCount > DescriptorArray.kMaxNumberOfDescriptors,
        };
    }

    public override string ToString() => "<ClassBoilerplate>";
}
