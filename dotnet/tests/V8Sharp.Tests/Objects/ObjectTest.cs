// Port of test/unittests/objects/object-unittest.cc.
//
// Tests that run JavaScript to set objects up build the same objects through
// the object API (the interpreter is a separate port); the JS each replaces
// is quoted in a comment.
//
// Not ported:
// - InstanceTypeList, InstanceTypeListOrder, StructListOrder: they check V8's
//   INSTANCE_TYPE_LIST macros, which V8Sharp does not have.
// - EmptyFunctionScopeInfo, CanOnlyAccessFixedFormalParameters,
//   UnusedParameters: they compile functions (parser and compiler ports).
// - AddDataPropertyNameCollision, AddDataPropertyNameCollisionDeprecatedMap:
//   death tests of a V8 CHECK.
namespace V8Sharp.Tests.Objects;

public class ObjectTest : TestWithContext
{
    NativeContext native_context => i_isolate.NativeContext;

    [Fact]
    public void DictionaryGrowth()
    {
        NumberDictionary dict = NumberDictionary.New(1);
        JSValue value = JSValue.Null;
        PropertyDetails details = PropertyDetails.Empty();

        // This test documents the expected growth behavior of a dictionary getting
        // elements added to it one by one.
        Assert.Equal(4, HashTableBase.kMinCapacity);
        uint i = 1;
        // 3 elements fit into the initial capacity.
        for (; i <= 3; i++)
        {
            dict = NumberDictionary.Add(dict, i, value, details, out _);
            Assert.Equal(4, dict.Capacity);
        }
        // 4th element triggers growth.
        Assert.Equal(4u, i);
        for (; i <= 5; i++)
        {
            dict = NumberDictionary.Add(dict, i, value, details, out _);
            Assert.Equal(8, dict.Capacity);
        }
        // 6th element triggers growth.
        Assert.Equal(6u, i);
        for (; i <= 11; i++)
        {
            dict = NumberDictionary.Add(dict, i, value, details, out _);
            Assert.Equal(16, dict.Capacity);
        }
        // 12th element triggers growth.
        Assert.Equal(12u, i);
        for (; i <= 21; i++)
        {
            dict = NumberDictionary.Add(dict, i, value, details, out _);
            Assert.Equal(32, dict.Capacity);
        }
        // 22nd element triggers growth.
        Assert.Equal(22u, i);
        for (; i <= 43; i++)
        {
            dict = NumberDictionary.Add(dict, i, value, details, out _);
            Assert.Equal(64, dict.Capacity);
        }
        // 44th element triggers growth.
        Assert.Equal(44u, i);
        for (; i <= 50; i++)
        {
            dict = NumberDictionary.Add(dict, i, value, details, out _);
            Assert.Equal(128, dict.Capacity);
        }

        // If we grow by larger chunks, the next (sufficiently big) power of 2 is
        // chosen as the capacity.
        dict = NumberDictionary.New(1);
        dict = NumberDictionary.EnsureCapacity(dict, 65);
        Assert.Equal(128, dict.Capacity);

        dict = NumberDictionary.New(1);
        dict = NumberDictionary.EnsureCapacity(dict, 30);
        Assert.Equal(64, dict.Capacity);
    }

    [Fact]
    public void ContextMaps()
    {
        void VerifyFunctionPrototypeMap(Context.Field storedMapContextIndex, Context.Field storedCtorContextIndex)
        {
            NativeContext context = native_context;
            Map thisMap = context.Slots[(int)storedMapContextIndex].As<Map>();
            JSFunction fun = context.Slots[(int)storedCtorContextIndex].As<JSFunction>();
            var proto = (JSObject)fun.InitialMap.Prototype!;
            Map thatMap = proto.Map;

            Assert.True(proto.HasFastProperties);
            Assert.Same(thisMap, thatMap);
        }

        VerifyFunctionPrototypeMap(Context.Field.STRING_FUNCTION_PROTOTYPE_MAP_INDEX, Context.Field.STRING_FUNCTION_INDEX);
        VerifyFunctionPrototypeMap(Context.Field.REGEXP_PROTOTYPE_MAP_INDEX, Context.Field.REGEXP_FUNCTION_INDEX);
        VerifyFunctionPrototypeMap(Context.Field.OBJECT_FUNCTION_PROTOTYPE_MAP_INDEX, Context.Field.OBJECT_FUNCTION_INDEX);
    }

    JSValue Get(JSValue obj, string name) =>
        ObjectOps.GetProperty(i_isolate, obj, factory.InternalizeString(name));

    [Fact]
    public void InitialObjects()
    {
        JSValue global = native_context.GlobalObject;
        // Initial Array prototype: Array.prototype
        Assert.Same(native_context.InitialArrayPrototype, Get(Get(global, "Array"), "prototype").Object);
        // Initial Object prototype: Object.prototype
        Assert.Same(native_context.InitialObjectPrototype, Get(Get(global, "Object"), "prototype").Object);
        // Initial ArrayIterator prototype: [][Symbol.iterator]().__proto__, via
        // the initial array iterator map.
        Assert.Same(native_context.InitialArrayIteratorPrototype, native_context.InitialArrayIteratorMap.Prototype);
        // Initial Iterator prototype: [][Symbol.iterator]().__proto__.__proto__
        Assert.Same(native_context.InitialIteratorPrototype, native_context.InitialArrayIteratorPrototype.Map.Prototype);
        // Initial Generator prototype: (function*(){}).__proto__.prototype
        var generatorFunctionPrototype = (JSObject)native_context.GeneratorFunctionMap.Prototype!;
        Assert.Same(native_context.InitialGeneratorPrototype, Get(generatorFunctionPrototype, "prototype").Object);
    }

    void CheckObject(JSValue obj, string expected)
    {
        JSString printString = ObjectOps.NoSideEffectsToString(i_isolate, obj);
        Assert.Equal(expected, printString.ToString());
    }

    [Fact]
    public void NoSideEffectsToString()
    {
        CheckObject(factory.NewStringFromAsciiChecked("fisk hest"), "fisk hest");
        CheckObject(factory.NewNumber(42.3), "42.3");
        CheckObject(JSValue.FromInt(42), "42");
        CheckObject(JSValue.True, "true");
        CheckObject(JSValue.False, "false");
        CheckObject(JSValue.False, "false");
        CheckObject(BigInt.FromNumber(i_isolate, JSValue.FromInt(42)), "42");
        CheckObject(JSValue.Undefined, "undefined");
        CheckObject(JSValue.Null, "null");

        CheckObject(ReadOnlyRoots.error_to_string, "[object Error]");
        CheckObject(ReadOnlyRoots.unscopables_symbol, "Symbol(Symbol.unscopables)");
        CheckObject(factory.NewError(native_context.ErrorFunction, ReadOnlyRoots.empty_string), "Error");
        CheckObject(factory.NewError(native_context.ErrorFunction, factory.NewStringFromAsciiChecked("fisk hest")),
            "Error: fisk hest");
        CheckObject(factory.NewJSObject(native_context.ObjectFunction), "#<Object>");
        CheckObject(factory.NewJSProxy(factory.NewJSObject(native_context.ObjectFunction),
            factory.NewJSObject(native_context.ObjectFunction), false), "#<Object>");
    }

    [Fact]
    public void NoSideEffectsToMaybeStringWithProxy()
    {
        JSObject target = factory.NewJSObject(native_context.ObjectFunction);
        JSObject.AddProperty(i_isolate, target, ReadOnlyRoots.constructor_string, JSValue.Null, PropertyAttributes.NONE);
        JSProxy proxy = factory.NewJSProxy(target, factory.NewJSObject(native_context.ObjectFunction), false);

        Assert.Null(ObjectOps.NoSideEffectsToMaybeString(i_isolate, proxy));
    }

    void ForIn(JSObject obj) =>
        KeyAccumulator.GetKeys(i_isolate, obj, KeyCollectionMode.IncludePrototypes, PropertyFilter.ENUMERABLE_STRINGS,
            GetKeysConversion.ConvertToString, isForIn: true);

    [Fact]
    public void EnumCache()
    {
        // Create a nice transition tree:
        // (a) --> (b) --> (c)   shared DescriptorArray 1
        //          |
        //          +---> (cc)   shared DescriptorArray 2
        //
        // function O(a) { this.a = 1 };
        // a = new O(); b = new O(); b.b = 2; c = new O(); c.b = 2; c.c = 3;
        // cc = new O(); cc.b = 2; cc.cc = 4;
        JSFunction o = factory.NewFunctionForTesting(factory.InternalizeString("O"));
        JSObject NewO()
        {
            JSObject obj = factory.NewJSObject(o);
            ObjectOps.SetProperty(i_isolate, obj, factory.InternalizeString("a"), JSValue.FromInt(1));
            return obj;
        }
        void Set(JSObject obj, string name, int value) =>
            ObjectOps.SetProperty(i_isolate, obj, factory.InternalizeString(name), JSValue.FromInt(value));

        JSObject a = NewO();
        JSObject b = NewO();
        Set(b, "b", 2);
        JSObject c = NewO();
        Set(c, "b", 2);
        Set(c, "c", 3);
        JSObject cc = NewO();
        Set(cc, "b", 2);
        Set(cc, "cc", 4);

        // Check the transition tree.
        Assert.Same(a.Map.InstanceDescriptors, b.Map.InstanceDescriptors);
        Assert.Same(b.Map.InstanceDescriptors, c.Map.InstanceDescriptors);
        Assert.NotSame(c.Map.InstanceDescriptors, cc.Map.InstanceDescriptors);
        Assert.NotSame(b.Map.InstanceDescriptors, cc.Map.InstanceDescriptors);

        // Check that the EnumLength is unset.
        Assert.Equal(Map.kInvalidEnumCacheSentinel, a.Map.EnumLength);
        Assert.Equal(Map.kInvalidEnumCacheSentinel, b.Map.EnumLength);
        Assert.Equal(Map.kInvalidEnumCacheSentinel, c.Map.EnumLength);
        Assert.Equal(Map.kInvalidEnumCacheSentinel, cc.Map.EnumLength);

        // Check that the EnumCache is empty.
        Assert.Same(V8Sharp.Objects.EnumCache.Empty, a.Map.InstanceDescriptors.EnumCache);
        Assert.Same(V8Sharp.Objects.EnumCache.Empty, b.Map.InstanceDescriptors.EnumCache);
        Assert.Same(V8Sharp.Objects.EnumCache.Empty, c.Map.InstanceDescriptors.EnumCache);
        Assert.Same(V8Sharp.Objects.EnumCache.Empty, cc.Map.InstanceDescriptors.EnumCache);

        // The EnumCache is shared on the DescriptorArray, creating it on {cc} has no
        // effect on the other maps.
        ForIn(cc);  // var s = 0; for (let key in cc) { s += cc[key] };
        {
            Assert.Equal(Map.kInvalidEnumCacheSentinel, a.Map.EnumLength);
            Assert.Equal(Map.kInvalidEnumCacheSentinel, b.Map.EnumLength);
            Assert.Equal(Map.kInvalidEnumCacheSentinel, c.Map.EnumLength);
            Assert.Equal(3, cc.Map.EnumLength);

            Assert.Same(V8Sharp.Objects.EnumCache.Empty, a.Map.InstanceDescriptors.EnumCache);
            Assert.Same(V8Sharp.Objects.EnumCache.Empty, b.Map.InstanceDescriptors.EnumCache);
            Assert.Same(V8Sharp.Objects.EnumCache.Empty, c.Map.InstanceDescriptors.EnumCache);

            EnumCache enumCache = cc.Map.InstanceDescriptors.EnumCache;
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, enumCache);
            Assert.Equal(3, enumCache.Keys.Length);
            Assert.Equal(3, enumCache.Indices.Length);
        }

        // Initializing the EnumCache for the topmost map {a} will not create the
        // cache for the other maps.
        ForIn(a);
        {
            Assert.Equal(1, a.Map.EnumLength);
            Assert.Equal(Map.kInvalidEnumCacheSentinel, b.Map.EnumLength);
            Assert.Equal(Map.kInvalidEnumCacheSentinel, c.Map.EnumLength);
            Assert.Equal(3, cc.Map.EnumLength);

            // The enum cache is shared on the descriptor array of maps {a}, {b} and
            // {c} only.
            EnumCache enumCache = a.Map.InstanceDescriptors.EnumCache;
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, enumCache);
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, cc.Map.InstanceDescriptors.EnumCache);
            Assert.NotSame(enumCache, cc.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, a.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, b.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, c.Map.InstanceDescriptors.EnumCache);

            Assert.Equal(1, enumCache.Keys.Length);
            Assert.Equal(1, enumCache.Indices.Length);
        }

        // Creating the EnumCache for {c} will create a new EnumCache on the shared
        // DescriptorArray.
        EnumCache previousEnumCache = a.Map.InstanceDescriptors.EnumCache;
        FixedArray previousKeys = previousEnumCache.Keys;
        FixedArray previousIndices = previousEnumCache.Indices;
        ForIn(c);
        {
            Assert.Equal(1, a.Map.EnumLength);
            Assert.Equal(Map.kInvalidEnumCacheSentinel, b.Map.EnumLength);
            Assert.Equal(3, c.Map.EnumLength);
            Assert.Equal(3, cc.Map.EnumLength);

            EnumCache enumCache = c.Map.InstanceDescriptors.EnumCache;
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, enumCache);
            // The keys and indices caches are updated.
            Assert.Same(previousEnumCache, enumCache);
            Assert.NotSame(previousKeys, enumCache.Keys);
            Assert.NotSame(previousIndices, enumCache.Indices);
            Assert.Equal(1, previousKeys.Length);
            Assert.Equal(1, previousIndices.Length);
            Assert.Equal(3, enumCache.Keys.Length);
            Assert.Equal(3, enumCache.Indices.Length);

            // The enum cache is shared on the descriptor array of maps {a}, {b} and
            // {c} only.
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, cc.Map.InstanceDescriptors.EnumCache);
            Assert.NotSame(enumCache, cc.Map.InstanceDescriptors.EnumCache);
            Assert.NotSame(previousEnumCache, cc.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, a.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, b.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, c.Map.InstanceDescriptors.EnumCache);
        }

        // {b} can reuse the existing EnumCache, hence we only need to set the correct
        // EnumLength on the map without modifying the cache itself.
        previousEnumCache = a.Map.InstanceDescriptors.EnumCache;
        previousKeys = previousEnumCache.Keys;
        previousIndices = previousEnumCache.Indices;
        ForIn(b);
        {
            Assert.Equal(1, a.Map.EnumLength);
            Assert.Equal(2, b.Map.EnumLength);
            Assert.Equal(3, c.Map.EnumLength);
            Assert.Equal(3, cc.Map.EnumLength);

            EnumCache enumCache = c.Map.InstanceDescriptors.EnumCache;
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, enumCache);
            // The keys and indices caches are not updated.
            Assert.Same(previousEnumCache, enumCache);
            Assert.Same(previousKeys, enumCache.Keys);
            Assert.Same(previousIndices, enumCache.Indices);
            Assert.Equal(3, enumCache.Keys.Length);
            Assert.Equal(3, enumCache.Indices.Length);

            // The enum cache is shared on the descriptor array of maps {a}, {b} and
            // {c} only.
            Assert.NotSame(V8Sharp.Objects.EnumCache.Empty, cc.Map.InstanceDescriptors.EnumCache);
            Assert.NotSame(enumCache, cc.Map.InstanceDescriptors.EnumCache);
            Assert.NotSame(previousEnumCache, cc.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, a.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, b.Map.InstanceDescriptors.EnumCache);
            Assert.Same(enumCache, c.Map.InstanceDescriptors.EnumCache);
        }
    }

    static bool IsMinusZero(JSValue value) => value.IsNumber && value.Number == 0 && double.IsNegative(value.Number);

    static bool IsZero(JSValue value) => value.IsNumber && value.Number == 0 && !double.IsNegative(value.Number);

    [Fact]
    public void ObjectMethodsThatTruncateMinusZero()
    {
        JSValue minusZero = factory.NewNumber(-1.0 * 0.0);
        Assert.True(IsMinusZero(minusZero));

        JSValue result = ObjectOps.ToInteger(i_isolate, minusZero);
        Assert.True(IsZero(result));

        result = ObjectOps.ToLength(i_isolate, minusZero);
        Assert.True(IsZero(result));

        // Choose an error message template, doesn't matter which.
        result = ObjectOps.ToIndex(i_isolate, minusZero, MessageTemplate.InvalidAtomicAccessIndex);
        Assert.True(IsZero(result));
    }

    static void TestFunctionKind(Func<FunctionKind, bool> expected, Func<FunctionKind, bool> actual)
    {
        for (uint i = 0; i < (uint)FunctionKind.LastFunctionKind; i++)
        {
            var kind = (FunctionKind)i;
            Assert.Equal(expected(kind), actual(kind));
        }
    }

    [Fact]
    public void IsArrowFunction() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.ArrowFunction or FunctionKind.AsyncArrowFunction => true,
        _ => false,
    }, Globals.IsArrowFunction);

    [Fact]
    public void IsAsyncGeneratorFunction() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.AsyncConciseGeneratorMethod or FunctionKind.StaticAsyncConciseGeneratorMethod
            or FunctionKind.AsyncGeneratorFunction => true,
        _ => false,
    }, Globals.IsAsyncGeneratorFunction);

    [Fact]
    public void IsGeneratorFunction() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.ConciseGeneratorMethod or FunctionKind.StaticConciseGeneratorMethod
            or FunctionKind.AsyncConciseGeneratorMethod or FunctionKind.StaticAsyncConciseGeneratorMethod
            or FunctionKind.GeneratorFunction or FunctionKind.AsyncGeneratorFunction => true,
        _ => false,
    }, Globals.IsGeneratorFunction);

    [Fact]
    public void IsAsyncFunction() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.AsyncFunction or FunctionKind.AsyncArrowFunction or FunctionKind.AsyncConciseMethod
            or FunctionKind.StaticAsyncConciseMethod or FunctionKind.AsyncConciseGeneratorMethod
            or FunctionKind.StaticAsyncConciseGeneratorMethod or FunctionKind.AsyncGeneratorFunction => true,
        _ => false,
    }, Globals.IsAsyncFunction);

    [Fact]
    public void IsConciseMethod() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.ConciseMethod or FunctionKind.StaticConciseMethod or FunctionKind.ConciseGeneratorMethod
            or FunctionKind.StaticConciseGeneratorMethod or FunctionKind.AsyncConciseMethod
            or FunctionKind.StaticAsyncConciseMethod or FunctionKind.AsyncConciseGeneratorMethod
            or FunctionKind.StaticAsyncConciseGeneratorMethod or FunctionKind.ClassMembersInitializerFunction
            or FunctionKind.ClassMembersInitializerFunctionPrecededByStatic
            or FunctionKind.ClassStaticInitializerFunction
            or FunctionKind.ClassStaticInitializerFunctionPrecededByMember => true,
        _ => false,
    }, Globals.IsConciseMethod);

    [Fact]
    public void IsAccessorFunction() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.GetterFunction or FunctionKind.StaticGetterFunction or FunctionKind.SetterFunction
            or FunctionKind.StaticSetterFunction => true,
        _ => false,
    }, Globals.IsAccessorFunction);

    [Fact]
    public void IsDefaultConstructor() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.DefaultBaseConstructor or FunctionKind.DefaultDerivedConstructor => true,
        _ => false,
    }, Globals.IsDefaultConstructor);

    [Fact]
    public void IsBaseConstructor() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.BaseConstructor or FunctionKind.DefaultBaseConstructor => true,
        _ => false,
    }, Globals.IsBaseConstructor);

    [Fact]
    public void IsDerivedConstructor() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.DefaultDerivedConstructor or FunctionKind.DerivedConstructor => true,
        _ => false,
    }, Globals.IsDerivedConstructor);

    [Fact]
    public void IsClassConstructor() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.BaseConstructor or FunctionKind.DefaultBaseConstructor or FunctionKind.DefaultDerivedConstructor
            or FunctionKind.DerivedConstructor => true,
        _ => false,
    }, Globals.IsClassConstructor);

    [Fact]
    public void IsConstructable() => TestFunctionKind(kind => kind switch
    {
        FunctionKind.GetterFunction or FunctionKind.StaticGetterFunction or FunctionKind.SetterFunction
            or FunctionKind.StaticSetterFunction or FunctionKind.ArrowFunction or FunctionKind.AsyncArrowFunction
            or FunctionKind.AsyncFunction or FunctionKind.AsyncConciseMethod or FunctionKind.StaticAsyncConciseMethod
            or FunctionKind.AsyncConciseGeneratorMethod or FunctionKind.StaticAsyncConciseGeneratorMethod
            or FunctionKind.AsyncGeneratorFunction or FunctionKind.GeneratorFunction
            or FunctionKind.ConciseGeneratorMethod or FunctionKind.StaticConciseGeneratorMethod
            or FunctionKind.ConciseMethod or FunctionKind.StaticConciseMethod
            or FunctionKind.ClassMembersInitializerFunction
            or FunctionKind.ClassMembersInitializerFunctionPrecededByStatic
            or FunctionKind.ClassStaticInitializerFunction
            or FunctionKind.ClassStaticInitializerFunctionPrecededByMember => false,
        _ => true,
    }, Globals.IsConstructable);

    [Fact]
    public void IsStrictFunctionWithoutPrototype() => TestFunctionKind(
        kind => Globals.IsArrowFunction(kind) || Globals.IsConciseMethod(kind) || Globals.IsAccessorFunction(kind),
        Globals.IsStrictFunctionWithoutPrototype);

    [Fact]
    public void ConstructorInstanceTypes()
    {
        NativeContext context = native_context;
        for (int i = 0; i < (int)Context.Field.NATIVE_CONTEXT_SLOTS; i++)
        {
            if (context.Slots[i].HeapObjectOrNull is not JSFunction function) continue;
            InstanceType instanceType = function.Map.InstanceType;

            switch ((Context.Field)i)
            {
                case Context.Field.ARRAY_FUNCTION_INDEX:
                    Assert.Equal(InstanceType.JSArrayConstructorType, instanceType);
                    break;
                case Context.Field.REGEXP_FUNCTION_INDEX:
                    Assert.Equal(InstanceType.JSRegExpConstructorType, instanceType);
                    break;
                case Context.Field.PROMISE_FUNCTION_INDEX:
                    Assert.Equal(InstanceType.JSPromiseConstructorType, instanceType);
                    break;
                default:
                    // All the other functions must have the default instance type.
                    Assert.True(InstanceTypeChecks.IsJSFunction(instanceType));
                    break;
            }
        }
    }

    [Fact]
    public void LookupIteratorWithStringLookupStartObject()
    {
        JSString v8_str(string s) => factory.NewStringFromAsciiChecked(s);

        JSString str = v8_str("some boom");
        JSString lengthStr = v8_str("length");

        // Various "abc".blah like lookups.
        Assert.False(new LookupIterator(i_isolate, str, factory.InternalizeString(v8_str("abc"))).IsFound);
        Assert.False(new LookupIterator(i_isolate, str, new PropertyKey(i_isolate, factory.InternalizeString(v8_str("-10")))).IsFound);

        void CheckSetFails(ref LookupIterator it)
        {
            // Try to set property using both throwing and non-throwing modes.
            Assert.False(ObjectOps.SetProperty(ref it, v8_str("15"), StoreOrigin.MaybeKeyed, ShouldThrow.DontThrow));
            LookupIterator copy = it;
            Assert.Throws<JavaScriptException>(() =>
                ObjectOps.SetProperty(ref copy, v8_str("15"), StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError));
        }

        {
            // Various operations with "abc".length.
            var it = new LookupIterator(i_isolate, str, new PropertyKey(i_isolate, factory.InternalizeString(lengthStr)));
            Assert.True(it.IsFound);

            Assert.Equal(9, ObjectOps.GetProperty(ref it).Number);
            CheckSetFails(ref it);
        }

        {
            // Various operations with other named properties.
            var it = new LookupIterator(i_isolate, str, new PropertyKey(i_isolate, factory.InternalizeString(v8_str("blah"))));
            Assert.False(it.IsFound);

            Assert.True(ObjectOps.GetProperty(ref it).IsUndefined);
            CheckSetFails(ref it);
        }

        {
            // Various operations with indexed properties.
            var it = new LookupIterator(i_isolate, str, 1);
            Assert.True(it.IsFound);

            Assert.True(v8_str("o").IsEqualTo(((JSString)ObjectOps.GetProperty(ref it).Object).FlatSpan()));
            CheckSetFails(ref it);
        }

        ulong[] nonExistentIndices = [153, (ulong)JSString.kMaxLength + 1];
        foreach (ulong index in nonExistentIndices)
        {
            // Various operations with indexed properties.
            var it = new LookupIterator(i_isolate, str, index);
            Assert.False(it.IsFound);

            Assert.True(ObjectOps.GetProperty(ref it).IsUndefined);
            CheckSetFails(ref it);
        }
    }

    [Fact]
    public void JSObjectCopy()
    {
        JSFunction constructor = native_context.ObjectFunction;
        JSObject obj = factory.NewJSObject(constructor);
        JSString first = factory.InternalizeString("first");
        JSString second = factory.InternalizeString("second");

        JSValue one = JSValue.FromInt(1);
        JSValue two = JSValue.FromInt(2);

        ObjectOps.SetProperty(i_isolate, obj, first, one);
        ObjectOps.SetProperty(i_isolate, obj, second, two);

        ObjectOps.SetElement(i_isolate, obj, 0, first, ShouldThrow.DontThrow);
        ObjectOps.SetElement(i_isolate, obj, 1, second, ShouldThrow.DontThrow);

        // Make the clone.
        JSObject clone = factory.CopyJSObject(obj);
        Assert.NotSame(obj, clone);

        JSValue value1 = ObjectOps.GetElement(i_isolate, obj, 0);
        JSValue value2 = ObjectOps.GetElement(i_isolate, clone, 0);
        Assert.True(value1.IsIdenticalTo(value2));
        value1 = ObjectOps.GetElement(i_isolate, obj, 1);
        value2 = ObjectOps.GetElement(i_isolate, clone, 1);
        Assert.True(value1.IsIdenticalTo(value2));

        value1 = ObjectOps.GetProperty(i_isolate, obj, first);
        value2 = ObjectOps.GetProperty(i_isolate, clone, first);
        Assert.True(value1.IsIdenticalTo(value2));
        value1 = ObjectOps.GetProperty(i_isolate, obj, second);
        value2 = ObjectOps.GetProperty(i_isolate, clone, second);
        Assert.True(value1.IsIdenticalTo(value2));

        // Flip the values on the clone.
        ObjectOps.SetProperty(i_isolate, clone, first, two);
        ObjectOps.SetProperty(i_isolate, clone, second, one);

        ObjectOps.SetElement(i_isolate, clone, 0, second, ShouldThrow.DontThrow);
        ObjectOps.SetElement(i_isolate, clone, 1, first, ShouldThrow.DontThrow);

        value1 = ObjectOps.GetElement(i_isolate, obj, 1);
        value2 = ObjectOps.GetElement(i_isolate, clone, 0);
        Assert.True(value1.IsIdenticalTo(value2));
        value1 = ObjectOps.GetElement(i_isolate, obj, 0);
        value2 = ObjectOps.GetElement(i_isolate, clone, 1);
        Assert.True(value1.IsIdenticalTo(value2));

        value1 = ObjectOps.GetProperty(i_isolate, obj, second);
        value2 = ObjectOps.GetProperty(i_isolate, clone, first);
        Assert.True(value1.IsIdenticalTo(value2));
        value1 = ObjectOps.GetProperty(i_isolate, obj, first);
        value2 = ObjectOps.GetProperty(i_isolate, clone, second);
        Assert.True(value1.IsIdenticalTo(value2));
    }
}
