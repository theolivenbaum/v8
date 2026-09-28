// Port of test/unittests/objects/feedback-vector-unittest.cc.
//
// V8Sharp keeps feedback as strong references (the GC is .NET's), so
// GetHeapObjectIfWeak is GetFeedback's heap object and InvokeMajorGC is a
// .NET collection. Not ported: VectorCallSpeculationModeAndFeedbackContent
// (needs TurboFan) and MaxLengthAndSizeFor (object sizes).
using V8Sharp.Codegen;
using V8Sharp.IC;
using V8Sharp.Interpreter;
using V8Sharp.Objects;

namespace V8Sharp.Tests.IC;

public class FeedbackVectorUnitTest : TestWithContext
{
    public FeedbackVectorUnitTest()
    {
        i_isolate.Flags.allow_natives_syntax = true;
    }

    JSFunction GetFunction(string name) =>
        ObjectOps.GetProperty(i_isolate, i_isolate.NativeContext.GlobalProxyObject, factory.InternalizeString(name)).As<JSFunction>();

    void TryRunJS(string source) => Compiler.CompileAndRun(i_isolate, source);

    static void InvokeMajorGC() => GC.Collect();

    FeedbackVector VectorOf(JSFunction f) => JSFunctionFeedback.GetFeedbackVector(f)!;

    /// <summary>FeedbackVectorHelper: the slots of a vector in order.</summary>
    sealed class FeedbackVectorHelper
    {
        readonly List<FeedbackSlot> _slots = [];

        public FeedbackVectorHelper(FeedbackVector vector)
        {
            FeedbackMetadata metadata = vector.Metadata;
            int i = 0;
            while (i < vector.Length)
            {
                _slots.Add(new FeedbackSlot(i));
                i += FeedbackMetadata.GetSlotSize(metadata.GetKind(i));
            }
        }

        public int slot_count() => _slots.Count;
        public FeedbackSlot slot(int index) => _slots[index];
    }

    static void CHECK_SLOT_KIND(FeedbackVector vector, FeedbackVectorHelper helper, int index, FeedbackSlotKind expected) =>
        Assert.Equal(expected, vector.GetKind(helper.slot(index)));

    [Fact]
    public void VectorStructure()
    {
        {
            var oneSlot = new FeedbackVectorSpec();
            oneSlot.AddForInSlot();
            var helper = new FeedbackVectorHelper(FeedbackVector.NewForTesting(i_isolate, oneSlot));
            Assert.Equal(1, helper.slot_count());
        }
        {
            var oneIcSlot = new FeedbackVectorSpec();
            oneIcSlot.AddCallICSlot();
            var helper = new FeedbackVectorHelper(FeedbackVector.NewForTesting(i_isolate, oneIcSlot));
            Assert.Equal(1, helper.slot_count());
        }
        {
            var spec = new FeedbackVectorSpec();
            for (int i = 0; i < 3; i++) spec.AddForInSlot();
            for (int i = 0; i < 5; i++) spec.AddCallICSlot();
            FeedbackVector vector = FeedbackVector.NewForTesting(i_isolate, spec);
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(8, helper.slot_count());

            int index = FeedbackVector.GetIndex(helper.slot(0));
            Assert.Equal(helper.slot(0), FeedbackVector.ToSlot(index));
            index = FeedbackVector.GetIndex(helper.slot(3));
            Assert.Equal(helper.slot(3), FeedbackVector.ToSlot(index));
            index = FeedbackVector.GetIndex(helper.slot(7));
            Assert.Equal(3 + 4 * FeedbackMetadata.GetSlotSize(FeedbackSlotKind.kCall), index);
            Assert.Equal(helper.slot(7), FeedbackVector.ToSlot(index));
            Assert.Equal(3 + 5 * FeedbackMetadata.GetSlotSize(FeedbackSlotKind.kCall), vector.Length);
        }
        {
            var spec = new FeedbackVectorSpec();
            spec.AddForInSlot();
            spec.AddCreateClosureParameterCount(0);
            spec.AddForInSlot();
            FeedbackVector vector = FeedbackVector.NewForTesting(i_isolate, spec);
            FeedbackCell cell = vector.GetClosureFeedbackCell(0);
            Assert.True(cell.Value is null || cell.Value is Oddball { Kind: Oddball.OddballKind.Undefined });
        }
    }

    // IC slots need an encoding to recognize what is in there.
    [Fact]
    public void VectorICMetadata()
    {
        var spec = new FeedbackVectorSpec();
        // Set metadata.
        for (int i = 0; i < 40; i++)
        {
            switch (i % 4)
            {
                case 0: spec.AddForInSlot(); break;
                case 1: spec.AddCallICSlot(); break;
                case 2: spec.AddLoadICSlot(); break;
                case 3: spec.AddKeyedLoadICSlot(); break;
            }
        }
        FeedbackVector vector = FeedbackVector.NewForTesting(i_isolate, spec);
        var helper = new FeedbackVectorHelper(vector);
        Assert.Equal(40, helper.slot_count());

        // Meanwhile set some feedback values and type feedback values to
        // verify the data structure remains intact.
        vector.Slots[0] = vector;

        // Verify the metadata is correctly set up from the spec.
        for (int i = 0; i < 40; i++)
        {
            FeedbackSlotKind kind = vector.GetKind(helper.slot(i));
            FeedbackSlotKind expected = (i % 4) switch
            {
                0 => FeedbackSlotKind.kForIn,
                1 => FeedbackSlotKind.kCall,
                2 => FeedbackSlotKind.kLoadProperty,
                _ => FeedbackSlotKind.kLoadKeyed,
            };
            Assert.Equal(expected, kind);
        }
    }

    [Fact]
    public void VectorCallICStates()
    {
        // Make sure function f has a call that uses a type feedback slot.
        TryRunJS("function foo() { return 17; };%EnsureFeedbackVectorForFunction(f);function f(a) { a(); } f(foo);");
        JSFunction f = GetFunction("f");
        // There should be one IC.
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());

        TryRunJS("f(function() { return 16; })");
        Assert.Equal(InlineCacheState.GENERIC, nexus.IcState());

        // After a collection, state should remain GENERIC.
        InvokeMajorGC();
        Assert.Equal(InlineCacheState.GENERIC, nexus.IcState());
    }

    // Test the Call IC states transfer with Function.prototype.apply
    [Fact]
    public void VectorCallICStateApply()
    {
        // Make sure function f has a call that uses a type feedback slot.
        TryRunJS("var F;%EnsureFeedbackVectorForFunction(foo);function foo() { return F.apply(null, arguments); }F = Math.min;foo();");
        JSFunction foo = GetFunction("foo");
        JSFunction F = GetFunction("F");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(foo), new FeedbackSlot(4));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.Equal(CallFeedbackContent.kReceiver, nexus.GetCallFeedbackContent());
        Assert.Same(F, nexus.GetFeedback().HeapObjectOrNull);

        TryRunJS("F = Math.max;foo();");
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.Equal(CallFeedbackContent.kTarget, nexus.GetCallFeedbackContent());
        Assert.Same(i_isolate.NativeContext.FunctionPrototypeApply, nexus.GetFeedback().HeapObjectOrNull);

        TryRunJS("F.apply = (function () { return; });foo();");
        Assert.Equal(InlineCacheState.GENERIC, nexus.IcState());
    }

    [Fact]
    public void VectorCallFeedback()
    {
        TryRunJS("function foo() { return 17; }%EnsureFeedbackVectorForFunction(f);function f(a) { a(); } f(foo);");
        JSFunction f = GetFunction("f");
        JSFunction foo = GetFunction("foo");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.Same(foo, nexus.GetFeedback().HeapObjectOrNull);

        InvokeMajorGC();
        // It should stay monomorphic even after a GC.
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
    }

    [Fact]
    public void VectorPolymorphicCallFeedback()
    {
        i_isolate.Flags.lazy_feedback_allocation = false;
        // Make sure the call feedback of a() in f() becomes polymorphic.
        TryRunJS("function foo_maker() { return () => { return 17; } }a_foo = foo_maker();function f(a) { a(); } f(foo_maker());" +
                 "f(foo_maker());");
        JSFunction f = GetFunction("f");
        JSFunction aFoo = GetFunction("a_foo");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.POLYMORPHIC, nexus.IcState());
        HeapObject? heapObject = nexus.GetFeedback().HeapObjectOrNull;
        Assert.IsType<FeedbackCell>(heapObject);
        // Ensure this is the feedback cell for the closure returned by
        // foo_maker.
        Assert.Same(aFoo.RawFeedbackCell, heapObject);
    }

    [Fact]
    public void VectorCallFeedbackForArray()
    {
        TryRunJS("function f(a) { a(); };%EnsureFeedbackVectorForFunction(f);f(Array);");
        JSFunction f = GetFunction("f");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.Same(i_isolate.NativeContext.ArrayFunction, nexus.GetFeedback().HeapObjectOrNull);

        InvokeMajorGC();
        // It should stay monomorphic even after a GC.
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
    }

    [Fact]
    public void VectorCallCounts()
    {
        TryRunJS("function foo() { return 17; }%EnsureFeedbackVectorForFunction(f);function f(a) { a(); } f(foo);");
        JSFunction f = GetFunction("f");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());

        TryRunJS("f(foo); f(foo);");
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.Equal(3, nexus.GetCallCount());

        // Send the IC megamorphic, but we should still have incrementing counts.
        TryRunJS("f(function() { return 12; });");
        Assert.Equal(InlineCacheState.GENERIC, nexus.IcState());
        Assert.Equal(4, nexus.GetCallCount());
    }

    [Fact]
    public void VectorConstructCounts()
    {
        TryRunJS("function Foo() {}%EnsureFeedbackVectorForFunction(f);function f(a) { new a(); } f(Foo);");
        JSFunction f = GetFunction("f");
        FeedbackVector vector = VectorOf(f);
        var nexus = new FeedbackNexus(i_isolate, vector, new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.IsType<JSFunction>(vector.Slots[0].HeapObjectOrNull);

        TryRunJS("f(Foo); f(Foo);");
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        Assert.Equal(3, nexus.GetCallCount());

        // Send the IC megamorphic, but we should still have incrementing counts.
        TryRunJS("f(function() {});");
        Assert.Equal(InlineCacheState.GENERIC, nexus.IcState());
        Assert.Equal(4, nexus.GetCallCount());
    }

    [Fact]
    public void VectorSpeculationMode()
    {
        TryRunJS("function Foo() {}%EnsureFeedbackVectorForFunction(f);function f(a) { new a(); } f(Foo);");
        JSFunction f = GetFunction("f");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(SpeculationMode.kAllowSpeculation, nexus.GetSpeculationMode());

        TryRunJS("f(Foo); f(Foo);");
        Assert.Equal(3, nexus.GetCallCount());
        Assert.Equal(SpeculationMode.kAllowSpeculation, nexus.GetSpeculationMode());

        nexus.SetSpeculationMode(SpeculationMode.kDisallowSpeculation);
        Assert.Equal(SpeculationMode.kDisallowSpeculation, nexus.GetSpeculationMode());
        Assert.Equal(3, nexus.GetCallCount());

        nexus.SetSpeculationMode(SpeculationMode.kAllowSpeculation);
        Assert.Equal(SpeculationMode.kAllowSpeculation, nexus.GetSpeculationMode());
        Assert.Equal(3, nexus.GetCallCount());
    }

    JSObject GlobalObject(string name) =>
        ObjectOps.GetProperty(i_isolate, i_isolate.NativeContext.GlobalProxyObject, factory.InternalizeString(name)).As<JSObject>();

    [Fact]
    public void VectorLoadICStates()
    {
        TryRunJS("var o = { foo: 3 };%EnsureFeedbackVectorForFunction(f);function f(a) { return a.foo; } f(o);");
        JSFunction f = GetFunction("f");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        // Verify that the monomorphic map is the one we expect.
        JSObject o = GlobalObject("o");
        Assert.Same(o.Map, nexus.GetFirstMap());

        // Now go polymorphic.
        TryRunJS("f({ blarg: 3, foo: 2 })");
        Assert.Equal(InlineCacheState.POLYMORPHIC, nexus.IcState());

        TryRunJS("delete o.foo;f(o)");
        Assert.Equal(InlineCacheState.POLYMORPHIC, nexus.IcState());

        TryRunJS("f({ blarg: 3, torino: 10, foo: 2 })");
        Assert.Equal(InlineCacheState.POLYMORPHIC, nexus.IcState());
        var maps = new List<Map>();
        nexus.ExtractMaps(maps);
        Assert.Equal(4, maps.Count);

        // Finally driven megamorphic.
        TryRunJS("f({ blarg: 3, gran: 3, torino: 10, foo: 2 })");
        Assert.Equal(InlineCacheState.MEGAMORPHIC, nexus.IcState());
        Assert.Null(nexus.GetFirstMap());

        // After a collection, state should not be reset to PREMONOMORPHIC.
        InvokeMajorGC();
        Assert.Equal(InlineCacheState.MEGAMORPHIC, nexus.IcState());
    }

    [Fact]
    public void VectorLoadGlobalICSlotSharing()
    {
        // Function f has 5 LoadGlobalICs: 3 for {o} references outside of "typeof"
        // operator and 2 for {o} references inside "typeof" operator.
        TryRunJS("o = 10;function f() {  var x = o || 10;  var y = typeof o;  return o , typeof o, x , y, o;}" +
                 "%EnsureFeedbackVectorForFunction(f);f();");
        JSFunction f = GetFunction("f");
        // There should be two IC slots for {o} references outside and inside
        // typeof operator respectively.
        FeedbackVector vector = VectorOf(f);
        var helper = new FeedbackVectorHelper(vector);
        Assert.Equal(4, helper.slot_count());
        CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
        CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kLoadGlobalInsideTypeof);
        CHECK_SLOT_KIND(vector, helper, 2, FeedbackSlotKind.kTypeOf);
        CHECK_SLOT_KIND(vector, helper, 3, FeedbackSlotKind.kTypeOf);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(InlineCacheState.MONOMORPHIC, new FeedbackNexus(i_isolate, vector, helper.slot(i)).IcState());
        }
    }

    [Fact]
    public void VectorLoadICOnSmi()
    {
        TryRunJS("var o = { foo: 3 };%EnsureFeedbackVectorForFunction(f);function f(a) { return a.foo; } f(34);");
        JSFunction f = GetFunction("f");
        var nexus = new FeedbackNexus(i_isolate, VectorOf(f), new FeedbackSlot(0));
        Assert.Equal(InlineCacheState.MONOMORPHIC, nexus.IcState());
        // Verify that the monomorphic map is the one we expect.
        Map numberMap = ICMaps.GetPrimitiveMaps(i_isolate, i_isolate.NativeContext).NumberMap;
        Assert.Same(numberMap, nexus.GetFirstMap());

        // Now go polymorphic on o.
        TryRunJS("f(o)");
        Assert.Equal(InlineCacheState.POLYMORPHIC, nexus.IcState());

        var maps = new List<Map>();
        nexus.ExtractMaps(maps);
        Assert.Equal(2, maps.Count);

        // One of the maps should be the o map.
        JSObject o = GlobalObject("o");
        bool numberMapFound = false;
        bool oMapFound = false;
        foreach (Map current in maps)
        {
            if (ReferenceEquals(current, numberMap)) numberMapFound = true;
            else if (ReferenceEquals(current, o.Map)) oMapFound = true;
        }
        Assert.True(numberMapFound && oMapFound);

        // The degree of polymorphism doesn't change.
        TryRunJS("f(100)");
        Assert.Equal(InlineCacheState.POLYMORPHIC, nexus.IcState());
        var maps2 = new List<Map>();
        nexus.ExtractMaps(maps2);
        Assert.Equal(2, maps2.Count);
    }

    [Fact]
    public void ReferenceContextAllocatesNoSlots()
    {
        {
            TryRunJS("function testvar(x) {  y = x;  y = a;  return y;}%EnsureFeedbackVectorForFunction(testvar);a = 3;testvar({});");
            FeedbackVector vector = VectorOf(GetFunction("testvar"));
            // There should be two LOAD_ICs, one for a and one for y at the end.
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(3, helper.slot_count());
            CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kStoreGlobalSloppy);
            CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
            CHECK_SLOT_KIND(vector, helper, 2, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
        }
        {
            TryRunJS("function testprop(x) {  'use strict';  x.blue = a;}%EnsureFeedbackVectorForFunction(testprop);testprop({ blue: 3 });");
            FeedbackVector vector = VectorOf(GetFunction("testprop"));
            // There should be one LOAD_IC, for the load of a.
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(2, helper.slot_count());
            CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
            CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kSetNamedStrict);
        }
        {
            TryRunJS("function testpropfunc(x) {  x().blue = a;  return x().blue;}%EnsureFeedbackVectorForFunction(testpropfunc);" +
                     "function makeresult() { return { blue: 3 }; }testpropfunc(makeresult);");
            FeedbackVector vector = VectorOf(GetFunction("testpropfunc"));
            // There should be 1 LOAD_GLOBAL_IC to load x (in both cases), 2 CALL_ICs
            // to call x and a LOAD_IC to load blue.
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(5, helper.slot_count());
            CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kCall);
            CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
            CHECK_SLOT_KIND(vector, helper, 2, FeedbackSlotKind.kSetNamedSloppy);
            CHECK_SLOT_KIND(vector, helper, 3, FeedbackSlotKind.kCall);
            CHECK_SLOT_KIND(vector, helper, 4, FeedbackSlotKind.kLoadProperty);
        }
        {
            TryRunJS("function testkeyedprop(x) {  x[0] = a;  return x[0];}%EnsureFeedbackVectorForFunction(testkeyedprop);" +
                     "testkeyedprop([0, 1, 2]);");
            FeedbackVector vector = VectorOf(GetFunction("testkeyedprop"));
            // There should be 1 LOAD_GLOBAL_ICs for the load of a, and one
            // KEYED_LOAD_IC for the load of x[0] in the return statement.
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(3, helper.slot_count());
            CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
            CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kSetKeyedSloppy);
            CHECK_SLOT_KIND(vector, helper, 2, FeedbackSlotKind.kLoadKeyed);
        }
        {
            TryRunJS("function testkeyedprop(x) {  'use strict';  x[0] = a;  return x[0];}" +
                     "%EnsureFeedbackVectorForFunction(testkeyedprop);testkeyedprop([0, 1, 2]);");
            FeedbackVector vector = VectorOf(GetFunction("testkeyedprop"));
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(3, helper.slot_count());
            CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
            CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kSetKeyedStrict);
            CHECK_SLOT_KIND(vector, helper, 2, FeedbackSlotKind.kLoadKeyed);
        }
        {
            TryRunJS("function testcompound(x) {  'use strict';  x.old = x.young = x.in_between = a;  return x.old + x.young;}" +
                     "%EnsureFeedbackVectorForFunction(testcompound);testcompound({ old: 3, young: 3, in_between: 3 });");
            FeedbackVector vector = VectorOf(GetFunction("testcompound"));
            // There should be 1 LOAD_GLOBAL_IC for load of a and 2 LOAD_ICs, for load
            // of x.old and x.young. The `+` in `x.old + x.young` carries embedded
            // feedback in the BytecodeArray, so no FeedbackVector slot is allocated.
            var helper = new FeedbackVectorHelper(vector);
            Assert.Equal(6, helper.slot_count());
            CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kLoadGlobalNotInsideTypeof);
            CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kSetNamedStrict);
            CHECK_SLOT_KIND(vector, helper, 2, FeedbackSlotKind.kSetNamedStrict);
            CHECK_SLOT_KIND(vector, helper, 3, FeedbackSlotKind.kSetNamedStrict);
            CHECK_SLOT_KIND(vector, helper, 4, FeedbackSlotKind.kLoadProperty);
            CHECK_SLOT_KIND(vector, helper, 5, FeedbackSlotKind.kLoadProperty);
        }
    }

    [Fact]
    public void VectorStoreICBasic()
    {
        TryRunJS("function f(a) {  a.foo = 5;};%EnsureFeedbackVectorForFunction(f);var a = { foo: 3 };f(a);f(a);f(a);");
        FeedbackVector vector = VectorOf(GetFunction("f"));
        // There should be one IC slot.
        var helper = new FeedbackVectorHelper(vector);
        Assert.Equal(1, helper.slot_count());
        Assert.Equal(InlineCacheState.MONOMORPHIC, new FeedbackNexus(i_isolate, vector, new FeedbackSlot(0)).IcState());
    }

    [Fact]
    public void DefineNamedOwnIC()
    {
        TryRunJS("function f(v) {  return {a: 0, b: v, c: 0};}%EnsureFeedbackVectorForFunction(f);f(1);f(2);f(3);");
        FeedbackVector vector = VectorOf(GetFunction("f"));
        // There should be one IC slot.
        var helper = new FeedbackVectorHelper(vector);
        Assert.Equal(2, helper.slot_count());
        CHECK_SLOT_KIND(vector, helper, 0, FeedbackSlotKind.kLiteral);
        CHECK_SLOT_KIND(vector, helper, 1, FeedbackSlotKind.kDefineNamedOwn);
        Assert.Equal(InlineCacheState.MONOMORPHIC, new FeedbackNexus(i_isolate, vector, helper.slot(1)).IcState());
    }
}
