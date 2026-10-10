// Port of test/unittests/objects/elements-kind-unittest.cc.
//
// Not ported: SystemPointerElementsKind (V8Sharp has no
// SYSTEM_POINTER_ELEMENTS; there are no raw pointers).
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.Tests.Objects;

public class ElementsKindTest : TestWithContext
{
    static bool ElementsKindIsHoleyElementsKindForRead(ElementsKind kind) => kind switch
    {
        ElementsKind.HOLEY_SMI_ELEMENTS or ElementsKind.HOLEY_ELEMENTS or ElementsKind.HOLEY_DOUBLE_ELEMENTS
            or ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS or ElementsKind.HOLEY_SEALED_ELEMENTS
            or ElementsKind.HOLEY_FROZEN_ELEMENTS => true,
        _ => false,
    };

    static bool ElementsKindIsHoleyElementsKind(ElementsKind kind) => kind switch
    {
        ElementsKind.HOLEY_SMI_ELEMENTS or ElementsKind.HOLEY_ELEMENTS or ElementsKind.HOLEY_DOUBLE_ELEMENTS => true,
        _ => false,
    };

    static bool ElementsKindIsFastPackedElementsKind(ElementsKind kind) => kind switch
    {
        ElementsKind.PACKED_SMI_ELEMENTS or ElementsKind.PACKED_ELEMENTS or ElementsKind.PACKED_DOUBLE_ELEMENTS => true,
        _ => false,
    };

    [Fact]
    public void JSObjectAddingProperties()
    {
        JSFunction function = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        JSValue value = JSValue.FromInt(42);

        JSObject obj = factory.NewJSObject(function);
        Map previousMap = obj.Map;
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.Same(ReadOnlyRoots.empty_fixed_array, obj.Elements);

        // For the default constructor function no in-object properties are reserved
        // hence adding a single property will initialize the property-array.
        JSString name = MakeName("property", 0);
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, obj, name, value, PropertyAttributes.NONE);
        Assert.NotSame(previousMap, obj.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, obj.Map.ElementsKind);
        Assert.True(PropertyArrayLength(obj) >= 1);
        Assert.Same(ReadOnlyRoots.empty_fixed_array, obj.Elements);
    }

    [Fact]
    public void JSObjectInObjectAddingProperties()
    {
        JSFunction function = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        const int nofInobjectProperties = 10;
        // Force in object properties by changing the expected_nof_properties
        // (we always reserve 8 inobject properties slack on top).
        function.Shared.ExpectedNofProperties = nofInobjectProperties - 8;
        JSValue value = JSValue.FromInt(42);

        JSObject obj = factory.NewJSObject(function);
        Map previousMap = obj.Map;
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.Same(ReadOnlyRoots.empty_fixed_array, obj.Elements);

        // We have reserved space for in-object properties, hence adding up to
        // |nof_inobject_properties| will not create a property store.
        for (int i = 0; i < nofInobjectProperties; i++)
        {
            JSString propertyName = MakeName("property", i);
            JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, obj, propertyName, value, PropertyAttributes.NONE);
        }
        Assert.NotSame(previousMap, obj.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, obj.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.Same(ReadOnlyRoots.empty_fixed_array, obj.Elements);

        // Adding one more property will not fit in the in-object properties, thus
        // creating a property store.
        int index = nofInobjectProperties + 1;
        JSString name = MakeName("property", index);
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, obj, name, value, PropertyAttributes.NONE);
        Assert.NotSame(previousMap, obj.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, obj.Map.ElementsKind);
        // There must be at least 1 element in the properties store.
        Assert.True(PropertyArrayLength(obj) >= 1);
        Assert.Same(ReadOnlyRoots.empty_fixed_array, obj.Elements);
    }

    [Fact]
    public void JSObjectAddingElements()
    {
        JSFunction function = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        JSValue value = JSValue.FromInt(42);

        JSObject obj = factory.NewJSObject(function);
        Map previousMap = obj.Map;
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.Same(ReadOnlyRoots.empty_fixed_array, obj.Elements);

        // Adding an indexed element initializes the elements array.
        JSString name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, obj, name, value, PropertyAttributes.NONE);
        // No change in elements_kind => no map transition.
        Assert.Same(previousMap, obj.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, obj.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.True(obj.Elements.Length >= 1);

        // Adding more consecutive elements without a change in the backing store.
        const uint nonDictBackingStoreLimit = 100;
        for (uint i = 1; i < nonDictBackingStoreLimit; i++)
        {
            name = MakeName("", i);
            JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, obj, name, value, PropertyAttributes.NONE);
        }
        // No change in elements_kind => no map transition.
        Assert.Same(previousMap, obj.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, obj.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.True(obj.Elements.Length >= nonDictBackingStoreLimit);

        // Adding an element at an very large index causes a change to
        // DICTIONARY_ELEMENTS.
        name = MakeString("100000000");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, obj, name, value, PropertyAttributes.NONE);
        // Change in elements_kind => map transition.
        Assert.NotSame(previousMap, obj.Map);
        Assert.Equal(ElementsKind.DICTIONARY_ELEMENTS, obj.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(obj));
        Assert.True(((NumberDictionary)obj.Elements).NumberOfElements >= nonDictBackingStoreLimit);
    }

    [Fact]
    public void JSArrayAddingProperties()
    {
        JSValue value = JSValue.FromInt(42);

        JSArray array = factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 0);
        Map previousMap = array.Map;
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(array));
        Assert.Same(ReadOnlyRoots.empty_fixed_array, array.Elements);
        Assert.Equal(0, array.Length.Number);

        // For the default constructor function no in-object properties are reserved
        // hence adding a single property will initialize the property-array.
        JSString name = MakeName("property", 0);
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, value, PropertyAttributes.NONE);
        // No change in elements_kind but added property => new map.
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, array.Map.ElementsKind);
        Assert.True(PropertyArrayLength(array) >= 1);
        Assert.Same(ReadOnlyRoots.empty_fixed_array, array.Elements);
        Assert.Equal(0, array.Length.Number);
    }

    [Fact]
    public void JSArrayAddingElements()
    {
        JSValue value = JSValue.FromInt(42);

        JSArray array = factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 0);
        Map previousMap = array.Map;
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(array));
        Assert.Same(ReadOnlyRoots.empty_fixed_array, array.Elements);
        Assert.Equal(0, array.Length.Number);

        // Adding an indexed element initializes the elements array.
        JSString name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, value, PropertyAttributes.NONE);
        // No change in elements_kind => no map transition.
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(array));
        Assert.True(array.Elements.Length >= 1);
        Assert.Equal(1, array.Length.Number);

        // Adding more consecutive elements without a change in the backing store.
        const uint nonDictBackingStoreLimit = 100;
        for (uint i = 1; i < nonDictBackingStoreLimit; i++)
        {
            name = MakeName("", i);
            JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, value, PropertyAttributes.NONE);
        }
        // No change in elements_kind => no map transition.
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(array));
        Assert.True(array.Elements.Length >= nonDictBackingStoreLimit);
        Assert.Equal(nonDictBackingStoreLimit, array.Length.Number);

        // Adding an element at an very large index causes a change to
        // DICTIONARY_ELEMENTS.
        const int index = 100000000;
        name = MakeName("", index);
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, value, PropertyAttributes.NONE);
        // Change in elements_kind => map transition.
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.DICTIONARY_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(0, PropertyArrayLength(array));
        Assert.True(((NumberDictionary)array.Elements).NumberOfElements >= nonDictBackingStoreLimit);
        Assert.Equal(index + 1, array.Length.Number);
    }

    [Fact]
    public void JSArrayAddingElementsGeneralizingiFastSmiElements()
    {
        JSValue valueSmi = JSValue.FromInt(42);
        JSValue valueString = MakeString("value");
        JSValue valueDouble = factory.NewNumber(3.1415);

        JSArray array = factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 0);
        Map previousMap = array.Map;
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, array.Length.Number);

        // `array[0] = smi_value` doesn't change the elements_kind.
        JSString name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        // no change in elements_kind => no map transition
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(1, array.Length.Number);

        // `delete array[0]` does not alter length, but changes the elements_kind.
        name = MakeString("0");
        Assert.True(JSReceiver.DeletePropertyOrElement(i_isolate, array, name));
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_SMI_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(1, array.Length.Number);
        previousMap = array.Map;

        // Add a couple of elements again.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        name = MakeString("1");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_SMI_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);

        // Adding a string to the array changes from FAST_HOLEY_SMI to FAST_HOLEY.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueString, PropertyAttributes.NONE);
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);
        previousMap = array.Map;

        // We don't transition back to FAST_SMI even if we remove the string.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);

        // Adding a double doesn't change the map either.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueDouble, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);
    }

    [Fact]
    public void JSArrayAddingElementsGeneralizingFastElements()
    {
        JSValue valueSmi = JSValue.FromInt(42);
        JSValue valueString = MakeString("value");

        JSArray array = factory.NewJSArray(ElementsKind.PACKED_ELEMENTS, 0, 0);
        Map previousMap = array.Map;
        Assert.Equal(ElementsKind.PACKED_ELEMENTS, previousMap.ElementsKind);
        Assert.Equal(0, array.Length.Number);

        // `array[0] = smi_value` doesn't change the elements_kind.
        JSString name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        // no change in elements_kind => no map transition
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(1, array.Length.Number);

        // `delete array[0]` does not alter length, but changes the elements_kind.
        name = MakeString("0");
        Assert.True(JSReceiver.DeletePropertyOrElement(i_isolate, array, name));
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(1, array.Length.Number);
        previousMap = array.Map;

        // Add a couple of elements, elements_kind stays HOLEY.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueString, PropertyAttributes.NONE);
        name = MakeString("1");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);
    }

    [Fact]
    public void JSArrayAddingElementsGeneralizingiFastDoubleElements()
    {
        JSValue valueSmi = JSValue.FromInt(42);
        JSValue valueString = MakeString("value");
        JSValue valueDouble = factory.NewNumber(3.1415);

        JSArray array = factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 0);
        Map previousMap = array.Map;

        // `array[0] = value_double` changes |elements_kind| to
        // PACKED_DOUBLE_ELEMENTS.
        JSString name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueDouble, PropertyAttributes.NONE);
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_DOUBLE_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(1, array.Length.Number);
        previousMap = array.Map;

        // `array[1] = value_smi` doesn't alter the |elements_kind|.
        name = MakeString("1");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueSmi, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.PACKED_DOUBLE_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);

        // `delete array[0]` does not alter length, but changes the elements_kind.
        name = MakeString("0");
        Assert.True(JSReceiver.DeletePropertyOrElement(i_isolate, array, name));
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_DOUBLE_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);
        previousMap = array.Map;

        // Filling the hole `array[0] = value_smi` again doesn't transition back.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueDouble, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_DOUBLE_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);

        // Adding a string to the array changes to elements_kind PACKED_ELEMENTS.
        name = MakeString("1");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueString, PropertyAttributes.NONE);
        Assert.NotSame(previousMap, array.Map);
        Assert.Equal(ElementsKind.HOLEY_ELEMENTS, array.Map.ElementsKind);
        Assert.Equal(2, array.Length.Number);
        previousMap = array.Map;

        // Adding a double doesn't change the map.
        name = MakeString("0");
        JSObject.DefinePropertyOrElementIgnoreAttributes(i_isolate, array, name, valueDouble, PropertyAttributes.NONE);
        Assert.Same(previousMap, array.Map);
    }

    [Fact]
    public void IsHoleyElementsKindForRead()
    {
        for (int i = 0; i <= (int)ElementsKind.LAST_ELEMENTS_KIND; i++)
        {
            var kind = (ElementsKind)i;
            Assert.Equal(ElementsKindIsHoleyElementsKindForRead(kind), ElementsKinds.IsHoleyElementsKindForRead(kind));
        }
    }

    [Fact]
    public void IsHoleyElementsKind()
    {
        for (int i = 0; i <= (int)ElementsKind.LAST_ELEMENTS_KIND; i++)
        {
            var kind = (ElementsKind)i;
            Assert.Equal(ElementsKindIsHoleyElementsKind(kind), ElementsKinds.IsHoleyElementsKind(kind));
        }
    }

    [Fact]
    public void IsFastPackedElementsKind()
    {
        for (int i = 0; i <= (int)ElementsKind.LAST_ELEMENTS_KIND; i++)
        {
            var kind = (ElementsKind)i;
            Assert.Equal(ElementsKindIsFastPackedElementsKind(kind), ElementsKinds.IsFastPackedElementsKind(kind));
        }
    }

    [Fact]
    public void JSArraySetLengthDictionaryElements()
    {
        JSString name = factory.InternalizeString("Array");
        JSArray array = factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 0);

        // Set array length to 0.
        JSArray.SetLength(i_isolate, array, 0);
        Assert.True(array.Length.IsIdenticalTo(JSValue.Zero));
        Assert.True(array.HasSmiOrObjectElements);

        // array[0] = name.
        ObjectOps.SetElement(i_isolate, array, 0, name, ShouldThrow.DontThrow);
        Assert.Equal(1, array.Length.Number);
        JSValue element = ObjectOps.GetElement(i_isolate, array, 0);
        Assert.Same(name, element.Object);

        // Set array length with large value exceeding Smi range.
        const uint largeLength = (uint)JSValue.SmiMaxValue + 1;
        JSArray.SetLength(i_isolate, array, largeLength);

        Assert.True(ObjectOps.ToArrayIndex(array.Length, out uint intLength));
        Assert.Equal(largeLength, intLength);
        Assert.True(array.HasDictionaryElements);

        // array[length] = name.
        ObjectOps.SetElement(i_isolate, array, intLength, name, ShouldThrow.DontThrow);
        Assert.True(ObjectOps.ToArrayIndex(array.Length, out uint newIntLength));
        Assert.Equal(intLength + 1, newIntLength);
        element = ObjectOps.GetElement(i_isolate, array, intLength);
        Assert.Same(name, element.Object);
        element = ObjectOps.GetElement(i_isolate, array, 0);
        Assert.Same(name, element.Object);
    }
}
