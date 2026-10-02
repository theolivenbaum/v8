// V8Sharp-specific: JSObject.CloneShallow copies plain objects and arrays with
// copy constructors instead of MemberwiseClone; these tests fail when a field
// is added to those classes without being copied.
using System.Reflection;

namespace V8Sharp.Tests.Objects;

public class CloneShallowTest : TestWithContext
{
    static readonly string[] s_copiedFields =
    [
        "HeapObject.InstanceType", "HeapObject._hashField", "HeapObject._headerFlags", "JSReceiver.Map", "JSReceiver._fields", "JSReceiver._dictionary",
        "JSObject.Elements", "JSArray._length", "JSArray.AllocationMementoSite",
    ];

    static IEnumerable<string> InstanceFields(Type type)
    {
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.DeclaredOnly))
            {
                yield return t.Name + "." + f.Name;
            }
        }
    }

    [Fact]
    public void CopyConstructorsCoverAllFields()
    {
        foreach (string field in InstanceFields(typeof(JSArray))) Assert.Contains(field, s_copiedFields);
        foreach (string field in InstanceFields(typeof(JSObject))) Assert.Contains(field, s_copiedFields);
    }

    [Fact]
    public void InObjectLayoutIsContiguous() => Assert.True(InObjectLayout.IsContiguous);

    [Fact]
    public void CloneShallowCopiesInObjectSlots()
    {
        foreach (int count in new[] { 1, 2, 3, 4, 5, 9, 13, 20, 40, 100, 200 })
        {
            Map map = Map.Create(i_isolate, count);
            JSObject obj = i_isolate.Factory.NewJSObjectFromMap(map);
            Assert.True(obj.InObjectSlotCapacity >= count);
            for (int i = 0; i < count; i++) obj.InObjectPropertyRef(i) = JSValue.FromInt(i);
            JSObject clone = obj.CloneShallow();
            Assert.Equal(obj.GetType(), clone.GetType());
            for (int i = 0; i < count; i++) Assert.Equal(i, clone.InObjectPropertyRef(i).Number);
        }
    }

    [Fact]
    public void CloneShallowCopiesAllFields()
    {
        JSObject obj = i_isolate.Factory.NewJSObject(i_isolate.NativeContext.ObjectFunction);
        JSObject objClone = obj.CloneShallow();
        Assert.Equal(obj.GetType(), objClone.GetType());
        Assert.NotSame(obj, objClone);
        foreach (FieldInfo f in AllFields(obj.GetType()))
        {
            if (f.FieldType.Name.StartsWith("InObjectSlots", StringComparison.Ordinal)) continue;  // CloneShallowCopiesInObjectSlots
            Assert.Equal(f.GetValue(obj), f.GetValue(objClone));
        }

        JSArray array = i_isolate.Factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 4);
        array.AllocationMementoSite = new AllocationSite();
        var arrayClone = Assert.IsType<JSArray>(array.CloneShallow());
        foreach (FieldInfo f in AllFields(typeof(JSArray))) Assert.Equal(f.GetValue(array), f.GetValue(arrayClone));
    }

    static IEnumerable<FieldInfo> AllFields(Type type)
    {
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.DeclaredOnly))
            {
                yield return f;
            }
        }
    }
}
