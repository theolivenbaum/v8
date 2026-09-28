// Smoke tests of Bootstrapper::CreateEnvironment (src/init/bootstrapper.cc):
// the intrinsics exist with V8's shapes.
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.Tests.Init;

public class BootstrapperTests
{
    [Fact]
    public void CreateEnvironment()
    {
        Isolate isolate = Isolate.New();
        using var _ = isolate.Enter();
        NativeContext nc = isolate.NativeContext;

        Assert.NotNull(nc.ObjectFunction);
        Assert.NotNull(nc.ArrayFunction);
        Assert.NotNull(nc.FunctionFunction);
        Assert.Same(nc.InitialObjectPrototype, nc.ObjectFunction.InitialMap.Prototype);
        Assert.Same(nc.InitialArrayPrototype, nc.ArrayFunction.InitialMap.Prototype);
        Assert.True(nc.ObjectFunction.InitialMap.GetInObjectProperties() == JSObject.kInitialGlobalObjectUnusedPropertiesCount);
    }

    [Fact]
    public void GlobalObjectHasIntrinsics()
    {
        Isolate isolate = Isolate.New();
        using var _ = isolate.Enter();
        NativeContext nc = isolate.NativeContext;
        JSGlobalObject global = nc.GlobalObject;

        foreach (string name in (string[])["Object", "Function", "Array", "Number", "Boolean", "String", "Symbol", "Promise",
                     "Error", "TypeError", "RangeError", "Map", "Set", "WeakMap", "WeakSet", "Proxy", "Reflect", "JSON",
                     "Math", "BigInt", "Date", "RegExp", "parseInt", "parseFloat", "globalThis", "eval"])
        {
            JSValue value = ObjectOps.GetProperty(isolate, global, isolate.Factory.InternalizeString(name));
            Assert.False(value.IsUndefined, name);
        }
    }

    [Fact]
    public void FunctionMapsHaveLengthAndName()
    {
        Isolate isolate = Isolate.New();
        using var _ = isolate.Enter();
        NativeContext nc = isolate.NativeContext;
        Map map = nc.StrictFunctionMap;
        Assert.Equal(3, map.NumberOfOwnDescriptors);
        Assert.Same(ReadOnlyRoots.length_string, map.InstanceDescriptors.GetKey(new InternalIndex(0)));
        Assert.Same(ReadOnlyRoots.name_string, map.InstanceDescriptors.GetKey(new InternalIndex(1)));
        Assert.Same(ReadOnlyRoots.prototype_string, map.InstanceDescriptors.GetKey(new InternalIndex(2)));
    }

    [Fact]
    public void ArrayMapsForAllFastKinds()
    {
        Isolate isolate = Isolate.New();
        using var _ = isolate.Enter();
        NativeContext nc = isolate.NativeContext;
        for (int i = 0; i < ElementsKinds.kFastElementsKindCount; i++)
        {
            ElementsKind kind = ElementsKinds.GetFastElementsKindFromSequenceIndex(i);
            Map? map = nc.GetInitialJSArrayMap(kind);
            Assert.NotNull(map);
            Assert.Equal(kind, map!.ElementsKind);
        }
    }
}
