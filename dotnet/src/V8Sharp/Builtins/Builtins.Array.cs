// Port of the Array constructor and the element-moving Array builtins:
//   src/builtins/builtins-array.cc      ArrayPush, ArrayPop, ArrayShift,
//     ArrayUnshift, ArrayPrototypeFill, ArrayConcat (Fast_ArrayConcat,
//     Slow_ArrayConcat, ArrayConcatVisitor, IterateElements)
//   src/builtins/builtins-array-gen.cc  ArrayConstructor(Impl), the Array
//     iterator builtins (ArrayPrototypeValues/Keys/Entries,
//     ArrayIteratorPrototypeNext), CloneFastJSArray
//   src/runtime/runtime-array.cc        Runtime_NewArray (the constructor's
//     general path), ArraySpeciesCreate
//   src/objects/elements.cc             ArrayConstructInitializeElements
//   src/builtins/array-{isarray,of,from,at,concat,shift,unshift}.tq
//
// V8's CSA fast paths for the Array constructor (ArrayNoArgumentConstructor,
// ArraySingleArgumentConstructor with allocation sites) produce the same
// arrays as Runtime_NewArray, which V8Sharp always runs.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArray()
    {
        Register(Builtin.ArrayConstructor, BuiltinsArray.ArrayConstructor);
        Register(Builtin.ArrayConstructorImpl, BuiltinsArray.ArrayConstructor);
        Register(Builtin.ArrayIsArray, BuiltinsArray.ArrayIsArray);
        Register(Builtin.ArrayOf, BuiltinsArray.ArrayOf);
        Register(Builtin.ArrayFrom, BuiltinsArray.ArrayFrom);
        Register(Builtin.ArrayPrototypePush, BuiltinsArray.ArrayPush);
        Register(Builtin.ArrayPush, BuiltinsArray.ArrayPush);
        Register(Builtin.ArrayPrototypePop, BuiltinsArray.ArrayPop);
        Register(Builtin.ArrayPop, BuiltinsArray.ArrayPop);
        Register(Builtin.ArrayPrototypeShift, BuiltinsArray.ArrayPrototypeShift);
        Register(Builtin.ArrayShift, BuiltinsArray.ArrayPrototypeShift);
        Register(Builtin.ArrayPrototypeUnshift, BuiltinsArray.ArrayPrototypeUnshift);
        Register(Builtin.ArrayUnshift, BuiltinsArray.ArrayPrototypeUnshift);
        Register(Builtin.ArrayPrototypeFill, BuiltinsArray.ArrayPrototypeFill);
        Register(Builtin.ArrayPrototypeConcat, BuiltinsArray.ArrayConcat);
        Register(Builtin.ArrayConcat, BuiltinsArray.ArrayConcat);
        Register(Builtin.ArrayPrototypeAt, BuiltinsArray.ArrayPrototypeAt);
        Register(Builtin.ArrayPrototypeValues, BuiltinsArray.ArrayPrototypeValues);
        Register(Builtin.ArrayPrototypeKeys, BuiltinsArray.ArrayPrototypeKeys);
        Register(Builtin.ArrayPrototypeEntries, BuiltinsArray.ArrayPrototypeEntries);
        Register(Builtin.ArrayIteratorPrototypeNext, BuiltinsArray.ArrayIteratorPrototypeNext);
        // The get [Symbol.species] accessors of Array, ArrayBuffer, SharedArrayBuffer
        // and %TypedArray% (builtins-internal-gen.cc ReturnReceiver). Registering it
        // again from another area is harmless.
        Register(Builtin.ReturnReceiver, static (Isolate _, in BuiltinArguments args) => args.Receiver);
        RegisterArrayMethods();
        RegisterArrayIterating();
        RegisterArrayJoin();
        RegisterArraySort();
    }

    static partial void RegisterArrayMethods();
    static partial void RegisterArrayIterating();
    static partial void RegisterArrayJoin();
    static partial void RegisterArraySort();
}

/// <summary>The Array builtins.</summary>
public static partial class BuiltinsArray
{
    // ---- Fast array predicates (cast.tq, builtins-array.cc) ------------------------------------

    /// <summary>Isolate::IsInitialArrayPrototype.</summary>
    internal static bool IsInitialArrayPrototype(Isolate isolate, JSObject obj) =>
        ReferenceEquals(obj, isolate.NativeContext.InitialArrayPrototype);

    /// <summary>IsPrototypeInitialArrayPrototype (CSA): the map's prototype is the initial Array.prototype.</summary>
    internal static bool IsPrototypeInitialArrayPrototype(Isolate isolate, Map map) =>
        ReferenceEquals(map.Prototype, isolate.NativeContext.InitialArrayPrototype);

    /// <summary>Cast&lt;FastJSArray&gt; (cast.tq CastFastJSArray).</summary>
    internal static bool IsFastJSArray(Isolate isolate, JSValue o, out JSArray array)
    {
        if (o.HeapObjectOrNull is JSArray a && ElementsKinds.IsFastElementsKind(a.Map.ElementsKind) &&
            IsPrototypeInitialArrayPrototype(isolate, a.Map) && Protectors.IsNoElementsIntact(isolate))
        {
            array = a;
            return true;
        }
        array = null!;
        return false;
    }

    /// <summary>Cast&lt;FastJSArrayForRead&gt;: fast or non-extensible/sealed/frozen elements.</summary>
    internal static bool IsFastJSArrayForRead(Isolate isolate, JSValue o, out JSArray array)
    {
        if (o.HeapObjectOrNull is JSArray a && a.Map.ElementsKind <= ElementsKind.LAST_ANY_NONEXTENSIBLE_ELEMENTS_KIND &&
            IsPrototypeInitialArrayPrototype(isolate, a.Map) && Protectors.IsNoElementsIntact(isolate))
        {
            array = a;
            return true;
        }
        array = null!;
        return false;
    }

    /// <summary>Cast&lt;FastJSArrayForCopy&gt;: a FastJSArray with an intact ArraySpecies protector.</summary>
    internal static bool IsFastJSArrayForCopy(Isolate isolate, JSValue o, out JSArray array)
    {
        array = null!;
        return Protectors.IsArraySpeciesLookupChainIntact(isolate) && IsFastJSArray(isolate, o, out array);
    }

    /// <summary>Cast&lt;FastJSArrayWithNoCustomIteration&gt;.</summary>
    internal static bool IsFastJSArrayWithNoCustomIteration(Isolate isolate, JSArray a) =>
        Protectors.IsArrayIteratorLookupChainIntact(isolate) && IsFastJSArray(isolate, a, out _);

    /// <summary>IsJSArrayFastElementMovingAllowed (builtins-array.cc).</summary>
    internal static bool IsJSArrayFastElementMovingAllowed(Isolate isolate, JSArray receiver)
    {
        NativeContext? context = receiver.GetCreationContext();
        Map map = receiver.Map;
        if (context is not null && ReferenceEquals(map.Prototype, context.InitialArrayPrototype) &&
            Protectors.IsNoElementsIntact(isolate))
        {
            return true;
        }
        return JSObject.PrototypeHasNoElements(isolate, receiver);
    }

    /// <summary>IsJSArrayWithExtensibleFastElements.</summary>
    static bool IsJSArrayWithExtensibleFastElements(JSValue receiver, out ElementsKind kind, out JSArray array)
    {
        if (receiver.HeapObjectOrNull is JSArray a)
        {
            array = a;
            kind = a.GetElementsKind();
            return ElementsKinds.IsFastElementsKind(kind);
        }
        array = null!;
        kind = default;
        return false;
    }

    /// <summary>IsJSArrayWithAddableFastElements.</summary>
    static bool IsJSArrayWithAddableFastElements(Isolate isolate, JSValue receiver, out ElementsKind kind, out JSArray array)
    {
        if (!IsJSArrayWithExtensibleFastElements(receiver, out kind, out array)) return false;
        // If there may be elements accessors in the prototype chain, the fast path
        // cannot be used if there arguments to add to the array.
        if (!IsJSArrayFastElementMovingAllowed(isolate, array)) return false;
        // Adding elements to the array prototype would break code that makes sure
        // it has no elements. Handle that elsewhere.
        if (IsInitialArrayPrototype(isolate, array)) return false;
        return true;
    }

    /// <summary>MatchArrayElementsKindToArguments.</summary>
    static void MatchArrayElementsKindToArguments(Isolate isolate, JSArray array, ReadOnlySpan<JSValue> args)
    {
        if (args.Length == 0) return;
        ElementsKind originKind = array.GetElementsKind();
        // We do not need to transition for PACKED/HOLEY_ELEMENTS.
        if (ElementsKinds.IsObjectElementsKind(originKind)) return;

        ElementsKind targetKind = originKind;
        foreach (JSValue arg in args)
        {
            if (arg.IsSmi) continue;
            if (arg.IsNumber)
            {
                targetKind = ElementsKind.PACKED_DOUBLE_ELEMENTS;
            }
            else
            {
                targetKind = ElementsKind.PACKED_ELEMENTS;
                break;
            }
        }
        if (targetKind != originKind) JSObject.TransitionElementsKind(isolate, array, targetKind);
    }

    /// <summary>CloneFastJSArray (CSA): a copy of a fast array's elements and length.</summary>
    internal static JSArray CloneFastJSArray(Isolate isolate, JSArray array)
    {
        ElementsKind kind = array.GetElementsKind();
        int length = (int)array.Length.Number;
        FixedArrayBase elements = CopyElementsPrefix(array.Elements, length, capacity: length);
        return isolate.Factory.NewJSArrayWithElements(elements, kind, length);
    }

    /// <summary>
    /// CloneFastJSArrayFillingHoles: like CloneFastJSArray, but holes become
    /// undefined and the result has a packed elements kind.
    /// </summary>
    internal static JSArray CloneFastJSArrayFillingHoles(Isolate isolate, JSArray array)
    {
        ElementsKind kind = array.GetElementsKind();
        int length = (int)array.Length.Number;
        if (!ElementsKinds.IsHoleyElementsKind(kind)) return CloneFastJSArray(isolate, array);
        if (ElementsKinds.IsDoubleElementsKind(kind))
        {
            // Holes become undefined, which a double array cannot hold.
            FixedArray result = isolate.Factory.NewFixedArray(length);
            if (array.Elements is FixedDoubleArray doubles)
            {
                for (int i = 0; i < length; i++) result[i] = doubles.IsTheHole(i) ? JSValue.Undefined : JSValue.FromNumber(doubles.Data[i]);
            }
            return isolate.Factory.NewJSArrayWithElements(result, ElementsKind.PACKED_ELEMENTS, length);
        }
        var source = (FixedArray)array.Elements;
        FixedArray copy = isolate.Factory.NewFixedArray(length);
        bool sawHole = false;
        for (int i = 0; i < length; i++)
        {
            JSValue v = source[i];
            if (v.IsTheHole)
            {
                v = JSValue.Undefined;
                sawHole = true;
            }
            copy[i] = v;
        }
        ElementsKind resultKind = ElementsKinds.GetPackedElementsKind(kind);
        if (sawHole && ElementsKinds.IsSmiElementsKind(resultKind)) resultKind = ElementsKind.PACKED_ELEMENTS;
        return isolate.Factory.NewJSArrayWithElements(copy, resultKind, length);
    }

    /// <summary>ExtractFixedArray / ExtractFixedDoubleArray of [0, count) into a store of <paramref name="capacity"/>.</summary>
    internal static FixedArrayBase CopyElementsPrefix(FixedArrayBase source, int count, int capacity) =>
        CopyElementsRange(source, 0, count, capacity);

    /// <summary>ExtractFixedArray: [start, start + count) into a new store of <paramref name="capacity"/> (holes beyond).</summary>
    internal static FixedArrayBase CopyElementsRange(FixedArrayBase source, int start, int count, int capacity)
    {
        switch (source)
        {
            case FixedDoubleArray doubles:
            {
                if (capacity == 0) return FixedArray.Empty;
                FixedDoubleArray result = FixedDoubleArray.NewWithHoles(capacity);
                Array.Copy(doubles.Data, start, result.Data, 0, count);
                return result;
            }
            case FixedArray array:
            {
                if (capacity == 0) return FixedArray.Empty;
                FixedArray result = FixedArray.NewWithHoles(capacity);
                Array.Copy(array.Data, start, result.Data, 0, count);
                return result;
            }
            default:
                throw new InvalidOperationException("CopyElementsRange: unexpected backing store");
        }
    }

    /// <summary>
    /// ArraySpeciesCreate (ES #sec-arrayspeciescreate): a new array (or
    /// species instance) of the given length.
    /// </summary>
    internal static JSReceiver ArraySpeciesCreate(Isolate isolate, JSValue originalArray, double length)
    {
        JSValue constructor = ObjectOps.ArraySpeciesConstructor(isolate, originalArray);
        if (ReferenceEquals(constructor.HeapObjectOrNull, isolate.NativeContext.ArrayFunction))
        {
            // Construct(%Array%, « length »): RangeError for invalid lengths.
            return NewArray(isolate, isolate.NativeContext.ArrayFunction, isolate.NativeContext.ArrayFunction,
                [JSValue.FromNumber(length)]);
        }
        return Execution.New(isolate, constructor, [JSValue.FromNumber(length)]).As<JSReceiver>();
    }

    // ---- Array constructor (runtime-array.cc, elements.cc) ----------------------------------------

    /// <summary>ArrayConstructor / ArrayConstructorImpl: [[Call]] and [[Construct]] of %Array%.</summary>
    public static JSValue ArrayConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSFunction function = args.Target;
        // If new_target is undefined, then this is the 'Call' case, so set new_target
        // to function.
        JSReceiver newTarget = args.NewTarget.IsUndefined ? function : args.NewTarget.As<JSReceiver>();
        // The construct feedback's AllocationSite (V8 passes it to
        // ArrayConstructorImpl in a register; see Isolate.ArrayConstructorAllocationSite).
        AllocationSite? site = isolate.ArrayConstructorAllocationSite;
        isolate.ArrayConstructorAllocationSite = null;
        return NewArray(isolate, function, newTarget, args.Arguments, site);
    }

    /// <summary>Runtime_NewArray.</summary>
    internal static JSArray NewArray(Isolate isolate, JSFunction constructor, JSReceiver newTarget, ReadOnlySpan<JSValue> argv,
        AllocationSite? site = null)
    {
        bool holey = false;
        bool canUseTypeFeedback = site is not null;
        bool canInlineArrayConstructor = true;
        // For arity 1, the constructor call  is treated as `Array(length)` if it is a
        // number, and `Array(single_element_value)` otherwise. For the length call,
        // check various bounds.
        if (argv.Length == 1)
        {
            // Keep in sync with: `ArrayConstructInitializeElements`.
            JSValue arg0 = argv[0];
            if (arg0.IsNumber)
            {
                if (!ObjectOps.ToArrayLength(arg0, out uint length))
                {
                    // The array is a dictionary in this case.
                    canUseTypeFeedback = false;
                }
                else if (JSArray.SetLengthWouldNormalizeForLength(length))
                {
                    // The array is a dictionary in this case.
                    canUseTypeFeedback = false;
                }
                else if (length != 0)
                {
                    holey = true;
                    if (length >= JSArray.kInitialMaxFastElementArray) canInlineArrayConstructor = false;
                }
            }
            else
            {
                canUseTypeFeedback = false;
            }
        }

        Map initialMap = JSFunction.GetDerivedMap(isolate, constructor, newTarget);

        ElementsKind initialKind = canUseTypeFeedback ? site!.GetElementsKind() : initialMap.ElementsKind;
        ElementsKind toKind = holey ? ElementsKinds.GetHoleyElementsKind(initialKind) : initialKind;

        if (argv.Length > 1 || (argv.Length == 1 && !argv[0].IsNumber))
        {
            toKind = GetTransitionedElementsKind(initialKind, argv);
        }

        if (toKind != initialKind)
        {
            // Update the allocation site info to reflect the advice alteration.
            site?.SetElementsKind(toKind);
        }

        // We should allocate with an initial map that reflects the allocation site
        // advice. Therefore we use AllocateJSObjectFromMap instead of passing
        // the constructor.
        initialMap = Map.AsElementsKind(isolate, initialMap, toKind);

        var array = (JSArray)isolate.Factory.NewJSObjectFromMap(initialMap);
        // If we don't care to track arrays of to_kind ElementsKind, then
        // don't emit a memento for them.
        if (site is not null && AllocationSite.ShouldTrack(toKind)) array.InitializeAllocationMemento(isolate, site);
        isolate.Factory.NewJSArrayStorage(array, 0, 0, Factory.ArrayStorageAllocationMode.DONT_INITIALIZE_ARRAY_ELEMENTS);

        ElementsKind oldKind = array.GetElementsKind();
        ArrayConstructInitializeElements(isolate, array, argv);

        if (site is not null && (oldKind != array.GetElementsKind() || !canUseTypeFeedback || !canInlineArrayConstructor))
        {
            // Protect against deopt loops by disabling speculating optimizations in
            // some cases.
            site.SetSpeculationDisabled();
        }
        return array;
    }

    /// <summary>JSObject::GetTransitionedElementsKind with ALLOW_CONVERTED_DOUBLE_ELEMENTS.</summary>
    internal static ElementsKind GetTransitionedElementsKind(ElementsKind currentKind, ReadOnlySpan<JSValue> elements)
    {
        if (currentKind == ElementsKind.HOLEY_ELEMENTS) return currentKind;
        ElementsKind targetKind = currentKind;
        bool isHoley = ElementsKinds.IsHoleyElementsKind(currentKind);
        foreach (JSValue current in elements)
        {
            if (current.IsTheHole)
            {
                isHoley = true;
                targetKind = ElementsKinds.GetHoleyElementsKind(targetKind);
            }
            else if (!current.IsSmi)
            {
                if (current.IsNumber)
                {
                    if (ElementsKinds.IsSmiElementsKind(targetKind))
                    {
                        targetKind = isHoley ? ElementsKind.HOLEY_DOUBLE_ELEMENTS : ElementsKind.PACKED_DOUBLE_ELEMENTS;
                    }
                }
                else if (isHoley)
                {
                    return ElementsKind.HOLEY_ELEMENTS;
                }
                else
                {
                    targetKind = ElementsKind.PACKED_ELEMENTS;
                }
            }
        }
        return targetKind;
    }

    /// <summary>ArrayConstructInitializeElements (elements.cc).</summary>
    static void ArrayConstructInitializeElements(Isolate isolate, JSArray array, ReadOnlySpan<JSValue> args)
    {
        if (args.Length == 0)
        {
            // Optimize the case where there are no parameters passed.
            JSArray.Initialize(isolate, array, JSArray.kPreallocatedArrayElements);
            return;
        }
        if (args.Length == 1 && args[0].IsNumber)
        {
            // Keep in sync with: `Runtime_NewArray`.
            if (!ObjectOps.ToArrayLength(args[0], out uint length))
            {
                isolate.ThrowRangeError(MessageTemplate.InvalidArrayLength);
            }

            // Optimize the case where there is one argument and the argument is a small
            // smi.
            if (length > 0 && length < JSArray.kInitialMaxFastElementArray)
            {
                ElementsKind elementsKind = array.GetElementsKind();
                JSArray.Initialize(isolate, array, (int)length, (int)length);
                if (!ElementsKinds.IsHoleyElementsKind(elementsKind))
                {
                    elementsKind = ElementsKinds.GetHoleyElementsKind(elementsKind);
                    JSObject.TransitionElementsKind(isolate, array, elementsKind);
                }
            }
            else if (length == 0)
            {
                JSArray.Initialize(isolate, array, JSArray.kPreallocatedArrayElements);
            }
            else
            {
                // Take the argument as the length.
                JSArray.Initialize(isolate, array, 0);
                JSArray.SetLength(isolate, array, length);
            }
            return;
        }

        // Set length and elements on the array.
        int numberOfElements = args.Length;
        ElementsKind targetKind = GetTransitionedElementsKind(array.GetElementsKind(), args);
        if (targetKind != array.GetElementsKind()) JSObject.TransitionElementsKind(isolate, array, targetKind);

        // Allocate an appropriately typed elements array.
        ElementsKind kind = array.GetElementsKind();
        FixedArrayBase elms;
        if (ElementsKinds.IsDoubleElementsKind(kind))
        {
            var doubles = isolate.Factory.NewFixedDoubleArray(numberOfElements);
            for (int entry = 0; entry < numberOfElements; entry++) doubles.Set(entry, args[entry].Number);
            elms = doubles;
        }
        else
        {
            FixedArray objects = isolate.Factory.NewFixedArrayWithHoles(numberOfElements);
            args.CopyTo(objects.Data);
            elms = objects;
        }
        array.Elements = elms;
        array.Length = JSValue.FromInt(numberOfElements);
    }

    // ---- Array.isArray / of / from --------------------------------------------------------------

    /// <summary>ES #sec-array.isarray.</summary>
    public static JSValue ArrayIsArray(Isolate isolate, in BuiltinArguments args)
    {
        JSValue arg = args.AtOrUndefined(1);
        return arg.HeapObjectOrNull switch
        {
            JSArray => JSValue.True,
            // TODO(verwaest): Handle proxies in-place
            JSProxy => JSValue.FromBoolean(ObjectOps.IsArray(isolate, arg)),
            _ => JSValue.False,
        };
    }

    /// <summary>ES #sec-array.of.</summary>
    public static JSValue ArrayOf(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let len be the actual number of arguments passed to this function.
        int len = args.ArgcWithoutReceiver;
        // 2. Let items be the List of arguments passed to this function.
        ReadOnlySpan<JSValue> items = args.Arguments;
        // 3. Let C be the this value.
        JSValue c = isolate.Flags.builtin_subclassing ? args.Receiver : isolate.NativeContext.ArrayFunction;

        JSReceiver a;
        // 4. If IsConstructor(C) is true, then
        if (ReferenceEquals(c.HeapObjectOrNull, isolate.NativeContext.ArrayFunction) && len <= JSArray.kMaxFastArrayLength)
        {
            // Allocate an array with PACKED elements kind for fast-path rather than
            // calling the constructor which creates an array with HOLEY kind.
            FixedArray elements = isolate.Factory.NewFixedArray(len);
            for (int i = 0; i < len; i++) elements[i] = JSValue.Zero;
            a = isolate.Factory.NewJSArrayWithElements(elements, ElementsKind.PACKED_SMI_ELEMENTS, len);
        }
        else if (ArrayBuiltinsUtils.IsConstructor(c))
        {
            // a. Let A be ? Construct(C, « len »).
            a = Execution.New(isolate, c, [JSValue.FromInt(len)]).As<JSReceiver>();
        }
        else
        {
            // a. Let A be ? ArrayCreate(len).
            a = ArrayBuiltinsUtils.ArrayCreate(isolate, len);
        }

        // 6. Let k be 0.
        // 7. Repeat, while k < len
        for (int k = 0; k < len; k++)
        {
            // a. Let kValue be items[k].
            // b. Let Pk be ! ToString(k).
            // c. Perform ? CreateDataPropertyOrThrow(A, Pk, kValue).
            ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, a, k, items[k]);
        }

        // 8. Perform ? Set(A, "length", len, true).
        ArrayBuiltinsUtils.SetPropertyLength(isolate, a, len);

        // 9. Return A.
        return a;
    }

    /// <summary>iterator::FastIterableToList for Array.from's single-argument fast path.</summary>
    static bool TryFastIterableToList(Isolate isolate, JSValue iterable, out JSArray result)
    {
        result = null!;
        // Check FastJSArrayForReadWithNoCustomIteration case.
        if (Protectors.IsArrayIteratorLookupChainIntact(isolate) && IsFastJSArray(isolate, iterable, out JSArray array))
        {
            // Fast path for fast JSArray.
            result = CloneFastJSArrayFillingHoles(isolate, array);
            return true;
        }
        // (V8 also has fast paths for primitive strings, Map/Set iterators and
        // Sets; they produce the same arrays as iterating.)
        return false;
    }

    /// <summary>ES #sec-array.from.</summary>
    public static JSValue ArrayFrom(Isolate isolate, in BuiltinArguments args)
    {
        JSValue c = isolate.Flags.builtin_subclassing ? args.Receiver : isolate.NativeContext.ArrayFunction;
        bool isArrayFunction = ReferenceEquals(c.HeapObjectOrNull, isolate.NativeContext.ArrayFunction);

        // Use fast path if:
        // * |items| is the only argument, and
        // * the receiver is the Array function.
        if (args.ArgcWithoutReceiver == 1 && isArrayFunction && TryFastIterableToList(isolate, args[1], out JSArray fastResult))
        {
            return fastResult;
        }

        JSValue items = args.AtOrUndefined(1);
        JSValue mapfn = args.AtOrUndefined(2);
        JSValue thisArg = args.AtOrUndefined(3);

        bool mapping;
        // 2. If mapfn is undefined, let mapping be false.
        if (mapfn.IsUndefined)
        {
            mapping = false;
        }
        else
        {
            // a. If IsCallable(mapfn) is false, throw a TypeError exception.
            if (!ObjectOps.IsCallable(mapfn)) ArrayBuiltinsUtils.ThrowCalledNonCallable(isolate, mapfn);
            // b. Let mapping be true.
            mapping = true;
        }

        // 4. Let usingIterator be ? GetMethod(items, @@iterator).
        JSValue usingIterator = items.IsNullOrUndefined
            ? ObjectOps.GetPropertyOrElement(isolate, items, new PropertyKey(isolate, ReadOnlyRoots.iterator_symbol))
            : ArrayBuiltinsUtils.GetIteratorMethod(isolate, items);

        // 5. If usingIterator is not undefined, then
        if (!usingIterator.IsNullOrUndefined)
        {
            if (!ObjectOps.IsCallable(usingIterator))
            {
                return isolate.ThrowTypeError(MessageTemplate.FirstArgumentIteratorSymbolNonCallable,
                    ArrayBuiltinsUtils.NewString(isolate, "%Array%.from"));
            }

            // a. If IsConstructor(C) is true, then
            //   i. Let A be ? Construct(C).
            // b. Else,
            //   i. Let A be ? ArrayCreate(0).
            JSReceiver a = ArrayBuiltinsUtils.IsConstructor(c)
                ? Execution.New(isolate, c, []).As<JSReceiver>()
                : ArrayBuiltinsUtils.ArrayCreate(isolate, 0);

            // c. Let iteratorRecord be ? GetIterator(items, sync, usingIterator).
            IteratorRecord iteratorRecord = ArrayBuiltinsUtils.GetIterator(isolate, items, usingIterator);

            // d. Let k be 0.
            double k = 0;
            // e. Repeat,
            while (true)
            {
                // iii. Let next be ? IteratorStep(iteratorRecord).
                JSReceiver? next = ArrayBuiltinsUtils.IteratorStep(isolate, iteratorRecord);
                // iv. If next is false, then
                if (next is null)
                {
                    // 1. Perform ? Set(A, "length", k, true).
                    ArrayBuiltinsUtils.SetPropertyLength(isolate, a, k);
                    // 2. Return A.
                    return a;
                }

                // v. Let nextValue be ? IteratorValue(next).
                JSValue nextValue = ArrayBuiltinsUtils.IteratorValue(isolate, next);

                JSValue mappedValue;
                // vi. If mapping is true, then
                if (mapping)
                {
                    // 1. Let mappedValue be Call(mapfn, thisArg, « nextValue, k »).
                    // 2. If mappedValue is an abrupt completion,
                    //    return ? IteratorClose(iteratorRecord, mappedValue).
                    try
                    {
                        mappedValue = Execution.Call(isolate, mapfn, thisArg, [nextValue, JSValue.FromNumber(k)]);
                    }
                    catch (JavaScriptException)
                    {
                        ArrayBuiltinsUtils.IteratorCloseOnException(isolate, iteratorRecord.Object);
                        throw;
                    }
                }
                else
                {
                    mappedValue = nextValue;
                }
                // viii. Let defineStatus be
                //       CreateDataPropertyOrThrow(A, Pk, mappedValue).
                // ix. If defineStatus is an abrupt completion,
                //     return ? IteratorClose(iteratorRecord, defineStatus).
                try
                {
                    ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, a, k, mappedValue);
                }
                catch (JavaScriptException)
                {
                    ArrayBuiltinsUtils.IteratorCloseOnException(isolate, iteratorRecord.Object);
                    throw;
                }
                // x. Set k to k + 1.
                k += 1;
            }
        }

        // 6. NOTE: items is not an Iterable so assume it is an array-like object.
        // 7. Let arrayLike be ! ToObject(items).
        JSReceiver arrayLike = ObjectOps.ToObject(isolate, items);
        // 8. Let len be ? LengthOfArrayLike(arrayLike).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, arrayLike);

        JSReceiver result;
        // 9. If IsConstructor(C) is true, then
        if (isArrayFunction && len <= JSArray.kMaxFastArrayLength)
        {
            // Allocate an array with PACKED elements kind for fast-path rather than
            // calling the constructor which creates an array with HOLEY kind.
            result = isolate.Factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, (int)len,
                Factory.ArrayStorageAllocationMode.INITIALIZE_ARRAY_ELEMENTS_WITH_HOLE);
        }
        else if (ArrayBuiltinsUtils.IsConstructor(c))
        {
            // a. Let A be ? Construct(C, « len »).
            result = Execution.New(isolate, c, [JSValue.FromNumber(len)]).As<JSReceiver>();
        }
        else
        {
            // a. Let A be ? ArrayCreate(len).
            result = ArrayBuiltinsUtils.ArrayCreate(isolate, len);
        }

        // 11. Let k be 0.
        // 12. Repeat, while k < len
        for (double k = 0; k < len; k++)
        {
            // a. Let Pk be ! ToString(k).
            // b. Let kValue be ? Get(arrayLike, Pk).
            JSValue kValue = ArrayBuiltinsUtils.GetProperty(isolate, arrayLike, k);
            // c. If mapping is true, then
            //   i. Let mappedValue be ? Call(mapfn, thisArg, « kValue, k »).
            // d. Else, let mappedValue be kValue.
            JSValue mappedValue = mapping ? Execution.Call(isolate, mapfn, thisArg, [kValue, JSValue.FromNumber(k)]) : kValue;
            // e. Perform ? CreateDataPropertyOrThrow(A, Pk, mappedValue).
            ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, result, k, mappedValue);
        }

        // 13. Perform ? Set(A, "length", len, true).
        ArrayBuiltinsUtils.SetPropertyLength(isolate, result, len);
        // 14. Return A.
        return result;
    }

    // ---- push / pop (builtins-array.cc) -------------------------------------------------------

    /// <summary>ES #sec-array.prototype.push (ArrayPush).</summary>
    public static JSValue ArrayPush(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (!IsJSArrayWithAddableFastElements(isolate, receiver, out _, out JSArray array) || JSArray.HasReadOnlyLength(array))
        {
            return GenericArrayPush(isolate, args);
        }

        // Fast Elements Path
        int toAdd = args.ArgcWithoutReceiver;
        if (toAdd == 0) return array.Length;

        // Need to ensure that the values to be pushed can be contained in the array.
        MatchArrayElementsKindToArguments(isolate, array, args.Arguments);

        ElementsAccessor accessor = array.GetElementsAccessor();
        uint newLength = accessor.Push(isolate, array, args.Arguments);
        return JSValue.FromNumber(newLength);
    }

    /// <summary>GenericArrayPush / CommonArrayPush.</summary>
    static JSValue GenericArrayPush(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver receiver = ObjectOps.ToObject(isolate, args.Receiver);
        return CommonArrayPush(isolate, receiver, args.Arguments);
    }

    /// <summary>CommonArrayPush.</summary>
    internal static JSValue CommonArrayPush(Isolate isolate, JSReceiver receiver, ReadOnlySpan<JSValue> elements)
    {
        // 2. Let len be ? ToLength(? Get(O, "length")).
        JSValue rawLengthNumber = ObjectOps.GetLengthFromArrayLike(isolate, receiver);

        // 5. If len + arg_count > 2^53-1, throw a TypeError exception.
        double length = rawLengthNumber.Number;
        int totalArgs = elements.Length;
        if (totalArgs > ArrayBuiltinsUtils.kMaxSafeInteger - length)
        {
            return isolate.ThrowTypeError(MessageTemplate.PushPastSafeLength, JSValue.FromInt(totalArgs), rawLengthNumber);
        }

        // 6. Repeat, while args is not empty.
        for (int i = 0; i < totalArgs; i++)
        {
            // a. Remove the first element from args and let E be the value of the
            //    element.
            // b. Perform ? Set(O, ! ToString(len), E, true).
            ArrayBuiltinsUtils.SetProperty(isolate, receiver, length, elements[i]);
            // c. Let len be len+1.
            ++length;
        }

        // 7. Perform ? Set(O, "length", len, true).
        JSValue finalLength = JSValue.FromNumber(length);
        ObjectOps.SetProperty(isolate, receiver, ReadOnlyRoots.length_string, finalLength, StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);

        // 8. Return len.
        return finalLength;
    }

    /// <summary>ES #sec-array.prototype.pop (ArrayPop).</summary>
    public static JSValue ArrayPop(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (!IsJSArrayWithExtensibleFastElements(receiver, out ElementsKind kind, out JSArray array))
        {
            return GenericArrayPop(isolate, args);
        }

        uint len = (uint)array.Length.Number;
        if (JSArray.HasReadOnlyLength(array)) return GenericArrayPop(isolate, args);
        if (len == 0) return JSValue.Undefined;

        if (IsJSArrayFastElementMovingAllowed(isolate, array))
        {
            return ElementsAccessor.ForKind(kind).Pop(isolate, array);
        }
        // Use Slow Lookup otherwise
        uint newLength = len - 1;
        JSValue result = JSReceiver.GetElement(isolate, array, newLength);

        // The length could have become read-only during the last GetElement() call,
        // so check again.
        if (JSArray.HasReadOnlyLength(array))
        {
            return isolate.ThrowTypeError(MessageTemplate.StrictReadOnlyProperty, ReadOnlyRoots.length_string,
                ObjectOps.TypeOf(isolate, array), array);
        }
        JSArray.SetLength(isolate, array, newLength);
        return result;
    }

    /// <summary>GenericArrayPop.</summary>
    static JSValue GenericArrayPop(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver receiver = ObjectOps.ToObject(isolate, args.Receiver);

        // 2. Let len be ? ToLength(? Get(O, "length")).
        double length = ObjectOps.GetLengthFromArrayLike(isolate, receiver).Number;

        // 3. If len is zero, then.
        if (length == 0)
        {
            // a. Perform ? Set(O, "length", 0, true).
            ObjectOps.SetProperty(isolate, receiver, ReadOnlyRoots.length_string, JSValue.Zero, StoreOrigin.MaybeKeyed,
                ShouldThrow.ThrowOnError);
            // b. Return undefined.
            return JSValue.Undefined;
        }

        // 4. Else len > 0.
        // a. Let new_len be len-1.
        double newLength = length - 1;
        // b. Let index be ! ToString(newLen).
        // c. Let element be ? Get(O, index).
        JSValue element = ArrayBuiltinsUtils.GetProperty(isolate, receiver, newLength);
        // d. Perform ? DeletePropertyOrThrow(O, index).
        ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, receiver, newLength);
        // e. Perform ? Set(O, "length", newLen, true).
        ObjectOps.SetProperty(isolate, receiver, ReadOnlyRoots.length_string, JSValue.FromNumber(newLength), StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);
        // f. Return element.
        return element;
    }

    // ---- shift / unshift (array-shift.tq, array-unshift.tq, builtins-array.cc) --------------------

    /// <summary>CanUseFastArrayModification.</summary>
    static bool CanUseFastArrayModification(Isolate isolate, JSValue receiver, out ElementsKind kind) =>
        IsJSArrayWithExtensibleFastElements(receiver, out kind, out JSArray array) &&
        IsJSArrayFastElementMovingAllowed(isolate, array) && !JSArray.HasReadOnlyLength(array);

    /// <summary>ES #sec-array.prototype.shift.</summary>
    public static JSValue ArrayPrototypeShift(Isolate isolate, in BuiltinArguments args)
    {
        if (CanUseFastArrayModification(isolate, args.Receiver, out ElementsKind kind))
        {
            var array = args.Receiver.As<JSArray>();
            return ElementsAccessor.ForKind(kind).Shift(isolate, array);
        }

        // GenericArrayShift
        // 1. Let O be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);

        // 2. Let len be ? ToLength(? Get(O, "length")).
        double length = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);

        // 3. If len is zero, then
        if (length == 0)
        {
            // a. Perform ? Set(O, "length", 0, true).
            ObjectOps.SetProperty(isolate, obj, ReadOnlyRoots.length_string, JSValue.Zero, StoreOrigin.MaybeKeyed,
                ShouldThrow.ThrowOnError);
            // b. Return undefined.
            return JSValue.Undefined;
        }

        // 4. Let first be ? Get(O, "0").
        JSValue first = ArrayBuiltinsUtils.GetProperty(isolate, obj, 0);
        // 5. Let k be 1.
        // 6. Repeat, while k < len
        for (double k = 1; k < length; k++)
        {
            // a. Let from be ! ToString(k).
            // b. Let to be ! ToString(k - 1).
            // c. Let fromPresent be ? HasProperty(O, from).
            if (ArrayBuiltinsUtils.HasProperty(isolate, obj, k))
            {
                // i. Let fromVal be ? Get(O, from).
                JSValue fromValue = ArrayBuiltinsUtils.GetProperty(isolate, obj, k);
                // ii. Perform ? Set(O, to, fromValue, true).
                ArrayBuiltinsUtils.SetProperty(isolate, obj, k - 1, fromValue);
            }
            else
            {
                // i. Perform ? DeletePropertyOrThrow(O, to).
                ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, obj, k - 1);
            }
        }

        // 7. Perform ? DeletePropertyOrThrow(O, ! ToString(len - 1)).
        ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, obj, length - 1);

        // 8. Perform ? Set(O, "length", len - 1, true).
        ObjectOps.SetProperty(isolate, obj, ReadOnlyRoots.length_string, JSValue.FromNumber(length - 1), StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);

        // 9. Return first.
        return first;
    }

    /// <summary>ES #sec-array.prototype.unshift.</summary>
    public static JSValue ArrayPrototypeUnshift(Isolate isolate, in BuiltinArguments args)
    {
        if (IsFastJSArray(isolate, args.Receiver, out JSArray array) && array.Map.IsExtensible && !JSArray.HasReadOnlyLength(array) &&
            !IsInitialArrayPrototype(isolate, array))
        {
            // ArrayUnshift (builtins-array.cc)
            MatchArrayElementsKindToArguments(isolate, array, args.Arguments);
            int toAdd = args.ArgcWithoutReceiver;
            if (toAdd == 0) return array.Length;
            ElementsAccessor accessor = array.GetElementsAccessor();
            uint newLength = accessor.Unshift(isolate, array, args.Arguments);
            return JSValue.FromNumber(newLength);
        }
        return GenericArrayUnshift(isolate, args.Receiver, args.Arguments);
    }

    /// <summary>GenericArrayUnshift.</summary>
    static JSValue GenericArrayUnshift(Isolate isolate, JSValue receiver, ReadOnlySpan<JSValue> arguments)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, receiver);

        // 2. Let len be ? ToLength(? Get(O, "length")).
        double length = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);

        // 3. Let argCount be the number of actual arguments.
        int argCount = arguments.Length;

        // 4. If argCount > 0, then.
        if (argCount > 0)
        {
            // a. If len + argCount > 2**53 - 1, throw a TypeError exception.
            if (length + argCount > ArrayBuiltinsUtils.kMaxSafeInteger)
            {
                return isolate.ThrowTypeError(MessageTemplate.InvalidArrayLength);
            }

            // b. Let k be len.
            // c. Repeat, while k > 0.
            for (double k = length; k > 0; --k)
            {
                // i. Let from be ! ToString(k - 1).
                double from = k - 1;
                // ii. Let to be ! ToString(k + argCount - 1).
                double to = k + argCount - 1;
                // iii. Let fromPresent be ? HasProperty(O, from).
                if (ArrayBuiltinsUtils.HasProperty(isolate, obj, from))
                {
                    // 1. Let fromValue be ? Get(O, from).
                    JSValue fromValue = ArrayBuiltinsUtils.GetProperty(isolate, obj, from);
                    // 2. Perform ? Set(O, to, fromValue, true).
                    ArrayBuiltinsUtils.SetProperty(isolate, obj, to, fromValue);
                }
                else
                {
                    // 1. Perform ? DeletePropertyOrThrow(O, to).
                    ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, obj, to);
                }
            }

            // d. Let j be 0.
            // e-f. Repeat, while items is not empty
            for (int j = 0; j < argCount; j++)
            {
                // ii .Perform ? Set(O, ! ToString(j), E, true).
                ArrayBuiltinsUtils.SetProperty(isolate, obj, j, arguments[j]);
            }
        }

        // 5. Perform ? Set(O, "length", len + argCount, true).
        double newLength = length + argCount;
        ObjectOps.SetProperty(isolate, obj, ReadOnlyRoots.length_string, JSValue.FromNumber(newLength), StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);

        // 6. Return length + argCount.
        return JSValue.FromNumber(newLength);
    }

    // ---- fill (builtins-array.cc) -----------------------------------------------------------------

    /// <summary>GetRelativeIndex: the index clamped into [0, length].</summary>
    static ulong GetRelativeIndex(Isolate isolate, ulong length, JSValue index, ulong initIfUndefined)
    {
        long relativeIndex = (long)initIfUndefined;
        if (!index.IsUndefined)
        {
            double providedRelativeIndex = ObjectOps.IntegerValue(isolate, index);
            if (Math.Abs(providedRelativeIndex) > length) return providedRelativeIndex < 0 ? 0 : length;
            relativeIndex = (long)providedRelativeIndex;
        }
        if (relativeIndex < 0) return (ulong)Math.Max((long)length + relativeIndex, 0);
        return Math.Min((ulong)relativeIndex, length);
    }

    /// <summary>ES #sec-array.prototype.fill.</summary>
    public static JSValue ArrayPrototypeFill(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver receiver = ObjectOps.ToObject(isolate, args.Receiver);

        // 2. Let len be ? ToLength(? Get(O, "length")).
        ulong length = (ulong)ArrayBuiltinsUtils.GetLengthProperty(isolate, receiver);

        // 3-4.
        ulong startIndex = GetRelativeIndex(isolate, length, args.AtOrUndefined(2), 0);

        // 5-6.
        ulong endIndex = GetRelativeIndex(isolate, length, args.AtOrUndefined(3), length);

        if (startIndex >= endIndex) return receiver;

        JSValue value = args.AtOrUndefined(1);

        if (TryFastArrayFill(isolate, receiver, value, startIndex, endIndex)) return receiver;

        // GenericArrayFill
        // 7. Repeat, while k < final.
        for (double k = startIndex; k < endIndex; k++)
        {
            // a. Let Pk be ! ToString(k).
            // b. Perform ? Set(O, Pk, value, true).
            ArrayBuiltinsUtils.SetProperty(isolate, receiver, k, value);
        }
        // 8. Return O.
        return receiver;
    }

    /// <summary>TryFastArrayFill.</summary>
    static bool TryFastArrayFill(Isolate isolate, JSReceiver receiver, JSValue value, ulong startIndex, ulong endIndex)
    {
        // If indices are too large, use generic path since they are stored as
        // properties, not in the element backing store.
        if (endIndex > uint.MaxValue) return false;
        if (!IsJSArrayWithAddableFastElements(isolate, receiver, out _, out JSArray array)) return false;

        uint start = (uint)startIndex;
        uint end = (uint)endIndex;
        if (end > FixedArrayBase.kMaxLength) return false;

        // Need to ensure that the fill value can be contained in the array.
        ElementsKind originKind = array.GetElementsKind();
        ElementsKind targetKind = ObjectOps.OptimalElementsKind(value);

        if (targetKind != originKind)
        {
            bool isReplacingAllElements = start == 0 && end == array.Length.Number;
            bool didTransitionMap = false;
            if (isReplacingAllElements)
            {
                // For the case where we are replacing all elements, we can migrate the
                // map backwards in the elements kind chain and ignore the current
                // contents of the elements array.
                Map? newMap = GetReplacedElementsKindsMap(isolate, array, originKind, targetKind);
                if (newMap is not null)
                {
                    FixedArrayBase elements = array.Elements;
                    if (ElementsKinds.IsDoubleElementsKind(originKind) != ElementsKinds.IsDoubleElementsKind(targetKind) ||
                        elements.IsCowArray)
                    {
                        // Reallocate the elements if doubleness doesn't match or the array is
                        // copy-on-write.
                        if (ElementsKinds.IsDoubleElementsKind(targetKind))
                        {
                            elements = isolate.Factory.NewFixedDoubleArray((int)end);
                        }
                        else
                        {
                            FixedArray zeroes = isolate.Factory.NewFixedArray((int)end);
                            zeroes.Data.AsSpan().Fill(JSValue.Zero);
                            elements = zeroes;
                        }
                    }
                    JSObject.SetMapAndElements(isolate, array, newMap, elements);
                    didTransitionMap = true;
                }
            }

            if (!didTransitionMap)
            {
                targetKind = ElementsKinds.GetMoreGeneralElementsKind(originKind, targetKind);
                JSObject.TransitionElementsKind(isolate, array, targetKind);
            }
        }

        ElementsAccessor accessor = array.GetElementsAccessor();
        accessor.Fill(isolate, array, value, start, end);

        // It's possible the JSArray's 'length' property was assigned to after the
        // length was loaded due to user code during argument coercion of the start
        // and end parameters. The spec algorithm does a Set, meaning the length would
        // grow as needed during the fill.
        //
        // ElementAccessor::Fill is able to grow the backing store as needed, but we
        // need to ensure the JSArray's length is correctly set in case the user
        // assigned a smaller value.
        if (array.Length.Number < end) accessor.SetLength(isolate, array, end);
        return true;
    }

    /// <summary>
    /// GetReplacedElementsKindsMap: a map that replaces the elements kind of an
    /// array with an initial array map, possibly backwards in the lattice.
    /// </summary>
    static Map? GetReplacedElementsKindsMap(Isolate isolate, JSArray array, ElementsKind originKind, ElementsKind targetKind)
    {
        Map map = array.Map;
        NativeContext? nativeContext = map.NativeContext;
        if (nativeContext is not null && ReferenceEquals(nativeContext.GetInitialJSArrayMap(originKind), map))
        {
            return nativeContext.GetInitialJSArrayMap(targetKind);
        }
        return null;
    }

    // ---- concat (array-concat.tq, builtins-array.cc) ----------------------------------------------

    /// <summary>ES #sec-array.prototype.concat (ArrayPrototypeConcat + ArrayConcat).</summary>
    public static JSValue ArrayConcat(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiverValue = args.Receiver;
        // Fast path if we invoke as `x.concat()`.
        if (args.ArgcWithoutReceiver == 0 && Protectors.IsIsConcatSpreadableLookupChainIntact(isolate) &&
            IsFastJSArrayForCopy(isolate, receiverValue, out JSArray a0))
        {
            return CloneFastJSArray(isolate, a0);
        }
        // Fast path if we invoke as `[].concat(x)`.
        if (args.ArgcWithoutReceiver == 1 && Protectors.IsIsConcatSpreadableLookupChainIntact(isolate) &&
            IsFastJSArrayForCopy(isolate, receiverValue, out JSArray r) && r.Length.Number == 0 &&
            IsFastJSArrayForCopy(isolate, args[1], out JSArray a1))
        {
            return CloneFastJSArray(isolate, a1);
        }

        JSReceiver receiver = ObjectOps.ToObject(isolate, receiverValue, "Array.prototype.concat");
        // All the arguments, with the receiver (converted to an object) first.
        var all = new JSValue[args.ArgcWithoutReceiver + 1];
        all[0] = receiver;
        args.Arguments.CopyTo(all.AsSpan(1));

        // Avoid a real species read to avoid extra lookups to the array constructor
        if (receiver is JSArray receiverArray && receiverArray.HasArrayPrototype(isolate) &&
            Protectors.IsArraySpeciesLookupChainIntact(isolate))
        {
            JSArray? resultArray = Fast_ArrayConcat(isolate, all);
            if (resultArray is not null) return resultArray;
        }
        // Reading @@species happens before anything else with a side effect, so
        // we can do it here to determine whether to take the fast path.
        JSValue species = ObjectOps.ArraySpeciesConstructor(isolate, receiver);
        if (ReferenceEquals(species.HeapObjectOrNull, isolate.NativeContext.ArrayFunction))
        {
            JSArray? resultArray = Fast_ArrayConcat(isolate, all);
            if (resultArray is not null) return resultArray;
        }
        return Slow_ArrayConcat(isolate, all, species);
    }

    /// <summary>IsSimpleArray: only the "length" own property and the initial Array.prototype.</summary>
    static bool IsSimpleArray(Isolate isolate, JSArray obj)
    {
        Map map = obj.Map;
        // If there is only the 'length' property we are fine.
        return ReferenceEquals(map.Prototype, isolate.NativeContext.InitialArrayPrototype) && map.NumberOfOwnDescriptors == 1;
    }

    /// <summary>HasSimpleElements.</summary>
    static bool HasSimpleElements(JSObject current) =>
        !Map.IsCustomElementsReceiverMap(current.Map) && !current.GetElementsAccessor().HasAccessors(current);

    /// <summary>HasOnlySimpleReceiverElements.</summary>
    static bool HasOnlySimpleReceiverElements(Isolate isolate, JSObject receiver)
    {
        // Check that we have no accessors on the receiver's elements.
        if (!HasSimpleElements(receiver)) return false;
        return JSObject.PrototypeHasNoElements(isolate, receiver);
    }

    /// <summary>HasOnlySimpleElements: no custom elements anywhere on the prototype chain.</summary>
    static bool HasOnlySimpleElements(JSReceiver receiver)
    {
        JSReceiver? current = receiver;
        while (current is not null)
        {
            if (current is not JSObject obj) return false;
            if (!HasSimpleElements(obj)) return false;
            current = obj.Map.Prototype;
        }
        return true;
    }

    /// <summary>
    /// Fast_ArrayConcat: all arguments are simple fast JSArrays; concatenates
    /// their backing stores (ElementsAccessor::Concat). Null to bail out.
    /// </summary>
    static JSArray? Fast_ArrayConcat(Isolate isolate, JSValue[] args)
    {
        if (!Protectors.IsIsConcatSpreadableLookupChainIntact(isolate)) return null;
        int resultLen = 0;
        bool hasDouble = false;
        bool isPacked = true;
        ElementsKind resultElementsKind = ElementsKinds.GetInitialFastElementsKind();
        bool requiresDoubleBoxing = false;
        // Iterate through all the arguments performing checks
        // and calculating total length.
        foreach (JSValue arg in args)
        {
            if (arg.HeapObjectOrNull is not JSArray array) return null;
            if (!HasOnlySimpleReceiverElements(isolate, array)) return null;
            // TODO(cbruni): support fast concatenation of DICTIONARY_ELEMENTS.
            if (!array.HasFastElements) return null;
            if (!IsSimpleArray(isolate, array)) return null;
            int length = (int)array.Length.Number;
            resultLen += length;
            // Throw an Error if we overflow the FixedArray limits
            if (resultLen > FixedArrayBase.kMaxLength) return (JSArray)isolate.ThrowRangeError(MessageTemplate.InvalidArrayLength).Object;

            // ElementsAccessor::Concat: the result kind.
            ElementsKind argKind = array.GetElementsKind();
            hasDouble = hasDouble || ElementsKinds.IsDoubleElementsKind(argKind);
            isPacked = isPacked && ElementsKinds.IsFastPackedElementsKind(argKind);
            resultElementsKind = ElementsKinds.GetMoreGeneralElementsKind(resultElementsKind, argKind);
        }
        if (ElementsKinds.IsMoreGeneralElementsKindTransition(resultElementsKind, ElementsKind.HOLEY_DOUBLE_ELEMENTS))
        {
            // If the result is not a double array, the double values of the
            // arguments have to be boxed.
            requiresDoubleBoxing = hasDouble;
        }
        if (!isPacked) resultElementsKind = ElementsKinds.GetHoleyElementsKind(resultElementsKind);
        _ = requiresDoubleBoxing;

        // ElementsAccessor::Concat: copy each argument's elements.
        if (resultLen == 0) return isolate.Factory.NewJSArray(resultElementsKind);
        FixedArrayBase storage = ElementsKinds.IsDoubleElementsKind(resultElementsKind)
            ? FixedDoubleArray.NewWithHoles(resultLen)
            : FixedArray.NewWithHoles(resultLen);
        int insertionIndex = 0;
        foreach (JSValue arg in args)
        {
            var array = (JSArray)arg.Object;
            int len = (int)array.Length.Number;
            if (len == 0) continue;
            FixedArrayBase from = array.Elements;
            switch (storage)
            {
                case FixedDoubleArray doubles:
                    if (from is FixedDoubleArray fromDoubles)
                    {
                        Array.Copy(fromDoubles.Data, 0, doubles.Data, insertionIndex, len);
                    }
                    else
                    {
                        var fromObjects = (FixedArray)from;
                        for (int i = 0; i < len; i++)
                        {
                            JSValue v = fromObjects[i];
                            if (v.IsTheHole) doubles.SetTheHole(insertionIndex + i);
                            else doubles.Set(insertionIndex + i, v.Number);
                        }
                    }
                    break;
                case FixedArray objects:
                    if (from is FixedDoubleArray fromDoubles2)
                    {
                        for (int i = 0; i < len; i++)
                        {
                            objects[insertionIndex + i] = fromDoubles2.IsTheHole(i)
                                ? JSValue.TheHole
                                : JSValue.FromNumber(fromDoubles2.Data[i]);
                        }
                    }
                    else
                    {
                        Array.Copy(((FixedArray)from).Data, 0, objects.Data, insertionIndex, len);
                    }
                    break;
            }
            insertionIndex += len;
        }
        return isolate.Factory.NewJSArrayWithElements(storage, resultElementsKind, resultLen);
    }

    /// <summary>
    /// ArrayConcatVisitor: collects visited elements into a FixedArray,
    /// a NumberDictionary, or (for species constructors) the result object.
    /// </summary>
    sealed class ArrayConcatVisitor
    {
        readonly Isolate _isolate;
        JSReceiver? _receiverStorage;
        FixedArray? _fixedStorage;
        NumberDictionary? _dictionaryStorage;
        uint _indexOffset;
        bool _fastElements;
        bool _exceedsArrayLimit;
        readonly bool _isFixedArray;
        readonly bool _hasSimpleElements;

        public ArrayConcatVisitor(Isolate isolate, HeapObject storage, bool fastElements)
        {
            _isolate = isolate;
            switch (storage)
            {
                case FixedArray fa:
                    _fixedStorage = fa;
                    break;
                case NumberDictionary nd:
                    _dictionaryStorage = nd;
                    break;
                default:
                    _receiverStorage = (JSReceiver)storage;
                    break;
            }
            _fastElements = fastElements;
            _isFixedArray = storage is FixedArray or NumberDictionary;
            _hasSimpleElements = _isFixedArray ||
                // Don't take fast path for storages that might have
                // side effects when storing to them.
                (!Map.IsCustomElementsReceiverMap(((JSReceiver)storage).Map) && storage is not JSTypedArray);
        }

        public bool Visit(uint i, JSValue elm)
        {
            uint index = _indexOffset + i;

            // Note we use >=kMaxArrayLength instead of the more appropriate
            // >kMaxArrayIndex here due to overflowing arithmetic and
            // increase_index_offset.
            if (i >= JSArray.kMaxArrayLength - _indexOffset)
            {
                _exceedsArrayLimit = true;
                // Exception hasn't been thrown at this point. Return true to
                // break out, and caller will throw. !visit would imply that
                // there is already an exception.
                return true;
            }

            if (!_isFixedArray)
            {
                JSReceiver.CreateDataProperty(_isolate, _receiverStorage!, new PropertyKey(_isolate, (double)index), elm,
                    ShouldThrow.ThrowOnError);
                return true;
            }

            if (_fastElements)
            {
                if (index < (uint)_fixedStorage!.Length)
                {
                    _fixedStorage[(int)index] = elm;
                    return true;
                }
                // Our initial estimate of length was foiled, possibly by
                // getters on the arrays increasing the length of later arrays
                // during iteration.
                // This shouldn't happen in anything but pathological cases.
                SetDictionaryMode();
                // Fall-through to dictionary mode.
            }
            Debug.Assert(!_fastElements);
            _dictionaryStorage = NumberDictionary.Set(_isolate, _dictionaryStorage!, index, elm, null);
            return true;
        }

        public uint IndexOffset => _indexOffset;

        public void IncreaseIndexOffset(uint delta)
        {
            if (JSArray.kMaxArrayLength - _indexOffset < delta) _indexOffset = JSArray.kMaxArrayLength;
            else _indexOffset += delta;
            // If the initial length estimate was off (see special case in visit()),
            // but the array blowing the limit didn't contain elements beyond the
            // provided-for index range, go to dictionary mode now.
            if (_fastElements && _indexOffset > (uint)_fixedStorage!.Length) SetDictionaryMode();
        }

        public bool ExceedsArrayLimit => _exceedsArrayLimit;

        public bool HasSimpleElements => _hasSimpleElements;

        public JSArray ToArray()
        {
            Debug.Assert(_isFixedArray);
            JSArray array = _isolate.Factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS);
            JSValue length = JSValue.FromNumber(_indexOffset);
            Map map = JSObject.GetElementsTransitionMap(_isolate, array,
                _fastElements ? ElementsKind.HOLEY_ELEMENTS : ElementsKind.DICTIONARY_ELEMENTS);
            array.Length = length;
            array.Elements = _fastElements ? _fixedStorage! : _dictionaryStorage!;
            array.Map = map;
            return array;
        }

        public JSReceiver ToJSReceiver()
        {
            Debug.Assert(!_isFixedArray);
            JSReceiver result = _receiverStorage!;
            ObjectOps.SetProperty(_isolate, result, ReadOnlyRoots.length_string, JSValue.FromNumber(_indexOffset),
                StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
            return result;
        }

        // Convert storage to dictionary mode.
        void SetDictionaryMode()
        {
            Debug.Assert(_fastElements && _fixedStorage is not null);
            FixedArray currentStorage = _fixedStorage;
            int currentLength = currentStorage.Length;
            NumberDictionary slowStorage = NumberDictionary.New(currentLength);
            for (int i = 0; i < currentLength; i++)
            {
                JSValue element = currentStorage[i];
                if (!element.IsTheHole) slowStorage = NumberDictionary.Set(_isolate, slowStorage, (uint)i, element, null);
            }
            _dictionaryStorage = slowStorage;
            _fixedStorage = null;
            _fastElements = false;
        }
    }

    /// <summary>EstimateElementCount.</summary>
    static uint EstimateElementCount(Isolate isolate, JSArray array)
    {
        uint length = (uint)array.Length.Number;
        int elementCount = 0;
        ElementsKind kind = array.GetElementsKind();
        switch (array.Elements)
        {
            case FixedDoubleArray doubles when ElementsKinds.IsDoubleElementsKind(kind):
            {
                int fastLength = (int)length;
                for (int i = 0; i < fastLength; i++)
                {
                    if (!doubles.IsTheHole(i)) elementCount++;
                }
                break;
            }
            case FixedArray elements when !ElementsKinds.IsDoubleElementsKind(kind) && kind != ElementsKind.DICTIONARY_ELEMENTS:
            {
                // Fast elements can't have lengths that are not representable by
                // a 32-bit signed integer.
                int fastLength = (int)length;
                for (int i = 0; i < fastLength; i++)
                {
                    if (!elements[i].IsTheHole) elementCount++;
                }
                break;
            }
            case NumberDictionary dictionary:
                elementCount = dictionary.NumberOfElements;
                break;
        }
        // As an estimate, we assume that the prototype doesn't contain any
        // inherited elements.
        return (uint)elementCount;
    }

    /// <summary>CollectElementIndices: the element indices below <paramref name="range"/> of the object and its prototypes.</summary>
    static void CollectElementIndices(Isolate isolate, JSObject obj, uint range, List<uint> indices)
    {
        ElementsKind kind = obj.GetElementsKind();
        switch (obj.Elements)
        {
            case FixedArray elements when ElementsKinds.IsSmiOrObjectElementsKind(kind) || ElementsKinds.IsAnyNonextensibleElementsKind(kind):
            {
                uint length = (uint)elements.Length;
                if (range < length) length = range;
                for (uint i = 0; i < length; i++)
                {
                    if (!elements[(int)i].IsTheHole) indices.Add(i);
                }
                break;
            }
            case FixedDoubleArray elements:
            {
                uint length = (uint)elements.Length;
                if (range < length) length = range;
                for (uint i = 0; i < length; i++)
                {
                    if (!elements.IsTheHole((int)i)) indices.Add(i);
                }
                break;
            }
            case NumberDictionary dict when kind == ElementsKind.DICTIONARY_ELEMENTS:
            {
                int capacity = dict.Capacity;
                for (int e = 0; e < capacity; e++)
                {
                    if (!dict.IsKey(e)) continue;
                    uint index = dict.KeyAtUInt(new InternalIndex(e));
                    if (index < range) indices.Add(index);
                }
                break;
            }
            default:
                if (obj is JSTypedArray typedArray)
                {
                    ulong length = typedArray.GetLength();
                    if (range <= length)
                    {
                        length = range;
                        // We will add all indices, so we might as well clear it first
                        // and avoid duplicates.
                        indices.Clear();
                    }
                    for (uint i = 0; i < length; i++) indices.Add(i);
                    if (length == range) return;  // All indices accounted for already.
                }
                else if (obj is JSPrimitiveWrapper wrapper && wrapper.Value.HeapObjectOrNull is JSString s)
                {
                    uint length = (uint)s.Length;
                    uint i = 0;
                    uint limit = Math.Min(length, range);
                    for (; i < limit; i++) indices.Add(i);
                    ElementsAccessor accessor = obj.GetElementsAccessor();
                    for (; i < range; i++)
                    {
                        if (accessor.HasElement(isolate, obj, i)) indices.Add(i);
                    }
                }
                else
                {
                    // Sloppy arguments.
                    ElementsAccessor accessor = obj.GetElementsAccessor();
                    for (uint i = 0; i < range; i++)
                    {
                        if (accessor.HasElement(isolate, obj, i, obj.Elements)) indices.Add(i);
                    }
                }
                break;
        }

        if (obj.Map.Prototype is JSObject prototype)
        {
            // The prototype will usually have no inherited element indices,
            // but we have to check.
            CollectElementIndices(isolate, prototype, range, indices);
        }
    }

    /// <summary>IterateElementsSlow.</summary>
    static void IterateElementsSlow(Isolate isolate, JSReceiver receiver, uint length, ArrayConcatVisitor visitor)
    {
        for (uint i = 0; i < length; ++i)
        {
            if (JSReceiver.HasElement(isolate, receiver, i))
            {
                JSValue elementValue = JSReceiver.GetElement(isolate, receiver, i);
                visitor.Visit(i, elementValue);
            }
        }
        visitor.IncreaseIndexOffset(length);
    }

    /// <summary>IterateElements: visits the "array" elements of a receiver in numerical order.</summary>
    static void IterateElements(Isolate isolate, JSReceiver receiver, ArrayConcatVisitor visitor)
    {
        uint length;
        if (receiver is not JSArray array)
        {
            JSValue val = ObjectOps.GetLengthFromArrayLike(isolate, receiver);
            if (visitor.IndexOffset + val.Number > ArrayBuiltinsUtils.kMaxSafeInteger)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.InvalidArrayLength));
            }
            // TODO(caitp): Support larger element indexes (up to 2^53-1).
            if (!ObjectOps.ToUint32(val, out length)) length = 0;
            // TODO(cbruni): handle other element kind as well
            IterateElementsSlow(isolate, receiver, length, visitor);
            return;
        }

        length = (uint)array.Length.Number;
        if (!visitor.HasSimpleElements || !HasOnlySimpleElements(receiver))
        {
            IterateElementsSlow(isolate, receiver, length, visitor);
            return;
        }

        ElementsKind kind = array.GetElementsKind();
        switch (array.Elements)
        {
            case FixedDoubleArray elements:
            {
                // Empty array is FixedArray but not FixedDoubleArray.
                if (length == 0) break;
                for (uint j = 0; j < length; j++)
                {
                    if (elements.IsTheHole((int)j))
                    {
                        if (JSReceiver.HasElement(isolate, array, j))
                        {
                            // Call GetElement on array, not its prototype, or getters won't
                            // have the correct receiver.
                            visitor.Visit(j, JSReceiver.GetElement(isolate, array, j));
                        }
                    }
                    else
                    {
                        visitor.Visit(j, JSValue.FromNumber(elements.Data[(int)j]));
                    }
                }
                break;
            }
            case FixedArray elements when ElementsKinds.IsSmiOrObjectElementsKind(kind) || ElementsKinds.IsAnyNonextensibleElementsKind(kind):
            {
                // Run through the elements FixedArray and use HasElement and GetElement
                // to check the prototype for missing elements.
                for (uint j = 0; j < length; j++)
                {
                    JSValue elementValue = elements[(int)j];
                    if (!elementValue.IsTheHole)
                    {
                        visitor.Visit(j, elementValue);
                    }
                    else if (JSReceiver.HasElement(isolate, array, j))
                    {
                        // Call GetElement on array, not its prototype, or getters won't
                        // have the correct receiver.
                        visitor.Visit(j, JSReceiver.GetElement(isolate, array, j));
                    }
                }
                break;
            }
            case NumberDictionary:
            {
                var indices = new List<uint>();
                // Collect all indices in the object and the prototypes less
                // than length. This might introduce duplicates in the indices list.
                CollectElementIndices(isolate, array, length, indices);
                indices.Sort();
                int n = indices.Count;
                for (int j = 0; j < n;)
                {
                    uint index = indices[j];
                    JSValue element = JSReceiver.GetElement(isolate, array, index);
                    visitor.Visit(index, element);
                    // Skip to next different index (i.e., omit duplicates).
                    do
                    {
                        j++;
                    } while (j < n && indices[j] == index);
                }
                break;
            }
            default:
            {
                // Sloppy arguments (FAST/SLOW_SLOPPY_ARGUMENTS_ELEMENTS).
                for (uint index = 0; index < length; index++)
                {
                    visitor.Visit(index, JSReceiver.GetElement(isolate, array, index));
                }
                break;
            }
        }
        visitor.IncreaseIndexOffset(length);
    }

    /// <summary>IsConcatSpreadable.</summary>
    static bool IsConcatSpreadable(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is not JSReceiver receiver) return false;
        if (!Protectors.IsIsConcatSpreadableLookupChainIntact(isolate) || receiver.HasProxyInPrototype(isolate))
        {
            // Slow path if @@isConcatSpreadable has been used.
            JSValue value = ObjectOps.GetPropertyOrElement(isolate, receiver,
                new PropertyKey(isolate, ReadOnlyRoots.is_concat_spreadable_symbol));
            if (!value.IsUndefined) return ObjectOps.BooleanValue(value);
        }
        return ObjectOps.IsArray(isolate, receiver);
    }

    /// <summary>Slow_ArrayConcat.</summary>
    static JSValue Slow_ArrayConcat(Isolate isolate, JSValue[] args, JSValue species)
    {
        int argumentCount = args.Length;
        bool isArraySpecies = ReferenceEquals(species.HeapObjectOrNull, isolate.NativeContext.ArrayFunction);

        // Pass 1: estimate the length and number of elements of the result.
        // The actual length can be larger if any of the arguments have getters
        // that mutate other arguments (but will otherwise be precise).
        // The number of elements is precise if there are no inherited elements.

        ElementsKind kind = ElementsKind.PACKED_SMI_ELEMENTS;

        uint estimateResultLength = 0;
        uint estimateNof = 0;
        for (int i = 0; i < argumentCount; i++)
        {
            JSValue obj = args[i];
            uint lengthEstimate;
            uint elementEstimate;
            if (obj.HeapObjectOrNull is JSArray array)
            {
                lengthEstimate = (uint)array.Length.Number;
                if (lengthEstimate != 0)
                {
                    ElementsKind arrayKind = ElementsKinds.GetPackedElementsKind(array.GetElementsKind());
                    if (ElementsKinds.IsAnyNonextensibleElementsKind(arrayKind)) arrayKind = ElementsKind.PACKED_ELEMENTS;
                    kind = ElementsKinds.GetMoreGeneralElementsKind(kind, arrayKind);
                }
                elementEstimate = EstimateElementCount(isolate, array);
            }
            else
            {
                if (obj.IsHeapObject)
                {
                    kind = ElementsKinds.GetMoreGeneralElementsKind(kind,
                        obj.IsNumber ? ElementsKind.PACKED_DOUBLE_ELEMENTS : ElementsKind.PACKED_ELEMENTS);
                }
                else if (obj.IsNumber && !obj.IsSmi)
                {
                    // A HeapNumber in V8.
                    kind = ElementsKinds.GetMoreGeneralElementsKind(kind, ElementsKind.PACKED_DOUBLE_ELEMENTS);
                }
                lengthEstimate = 1;
                elementEstimate = 1;
            }
            // Avoid overflows by capping at kMaxArrayLength.
            if (JSArray.kMaxArrayLength - estimateResultLength < lengthEstimate) estimateResultLength = JSArray.kMaxArrayLength;
            else estimateResultLength += lengthEstimate;
            if (JSArray.kMaxArrayLength - estimateNof < elementEstimate) estimateNof = JSArray.kMaxArrayLength;
            else estimateNof += elementEstimate;
        }

        // If estimated number of elements is more than half of length, a
        // fixed array (fast case) is more time and space-efficient than a
        // dictionary.
        bool fastCase = isArraySpecies && (ulong)estimateNof * 2 >= estimateResultLength &&
                        Protectors.IsIsConcatSpreadableLookupChainIntact(isolate);

        if (fastCase && kind == ElementsKind.PACKED_DOUBLE_ELEMENTS)
        {
            JSArray? doubleResult = TryConcatDoubles(isolate, args, estimateResultLength);
            if (doubleResult is not null) return doubleResult;
            // In case of failure, fall through.
        }

        HeapObject storage;
        if (fastCase)
        {
            if (estimateResultLength > FixedArrayBase.kMaxLength)
            {
                // TODO(ishell): eventually, this should be thrown by NewFixedArrayXXX.
                return isolate.ThrowRangeError(MessageTemplate.InvalidArrayLength);
            }
            // The backing storage array must have non-existing elements to preserve
            // holes across concat operations.
            storage = isolate.Factory.NewFixedArrayWithHoles((int)estimateResultLength);
        }
        else if (isArraySpecies)
        {
            storage = NumberDictionary.New((int)Math.Min(estimateNof, (uint)FixedArrayBase.kMaxLength));
        }
        else
        {
            Debug.Assert(ObjectOps.IsConstructor(species));
            storage = Execution.New(isolate, species, species, [JSValue.Zero]).As<JSReceiver>();
        }

        var visitor = new ArrayConcatVisitor(isolate, storage, fastCase);

        for (int i = 0; i < argumentCount; i++)
        {
            JSValue obj = args[i];
            if (IsConcatSpreadable(isolate, obj))
            {
                IterateElements(isolate, obj.As<JSReceiver>(), visitor);
            }
            else
            {
                visitor.Visit(0, obj);
                visitor.IncreaseIndexOffset(1);
            }
        }

        if (visitor.ExceedsArrayLimit) return isolate.ThrowRangeError(MessageTemplate.InvalidArrayLength);

        return isArraySpecies ? visitor.ToArray() : visitor.ToJSReceiver();
    }

    /// <summary>The PACKED_DOUBLE_ELEMENTS fast case of Slow_ArrayConcat; null on failure.</summary>
    static JSArray? TryConcatDoubles(Isolate isolate, JSValue[] args, uint estimateResultLength)
    {
        ElementsKind kind = ElementsKind.PACKED_DOUBLE_ELEMENTS;
        FixedDoubleArray doubleStorage = isolate.Factory.NewFixedDoubleArray((int)estimateResultLength);
        int j = 0;
        if (estimateResultLength > 0)
        {
            foreach (JSValue obj in args)
            {
                if (obj.IsNumber)
                {
                    doubleStorage.Set(j++, obj.Number);
                    continue;
                }
                var array = obj.As<JSArray>();
                bool hasArrayPrototype = array.HasArrayPrototype(isolate);
                uint length = (uint)array.Length.Number;
                switch (array.GetElementsKind())
                {
                    case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                    case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                    {
                        // Empty array is FixedArray but not FixedDoubleArray.
                        if (length == 0) break;
                        var elements = (FixedDoubleArray)array.Elements;
                        for (uint k = 0; k < length; k++)
                        {
                            if (elements.IsTheHole((int)k))
                            {
                                if (hasArrayPrototype && Protectors.IsNoElementsIntact(isolate))
                                {
                                    // If we do not have elements on the prototype chain,
                                    // we can generate a HOLEY_DOUBLE_ELEMENTS.
                                    kind = ElementsKind.HOLEY_DOUBLE_ELEMENTS;
                                    doubleStorage.SetTheHole(j);
                                }
                                else
                                {
                                    return null;
                                }
                            }
                            else
                            {
                                doubleStorage.Set(j, elements.Data[(int)k]);
                            }
                            j++;
                        }
                        break;
                    }
                    case ElementsKind.HOLEY_SMI_ELEMENTS:
                    case ElementsKind.PACKED_SMI_ELEMENTS:
                    {
                        var elements = (FixedArray)array.Elements;
                        for (uint k = 0; k < length; k++)
                        {
                            JSValue element = elements[(int)k];
                            if (element.IsTheHole) return null;
                            doubleStorage.Set(j, element.Number);
                            j++;
                        }
                        break;
                    }
                    default:
                        Debug.Assert(length == 0);
                        break;
                }
            }
        }
        return isolate.Factory.NewJSArrayWithElements(doubleStorage, kind, j);
    }

    // ---- at (array-at.tq) ------------------------------------------------------------------------

    /// <summary>https://tc39.es/proposal-item-method/#sec-array.prototype.at</summary>
    public static JSValue ArrayPrototypeAt(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? LengthOfArrayLike(O).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3. Let relativeIndex be ? ToInteger(index).
        double relativeIndex = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(1));
        // 4-5.
        double k = relativeIndex >= 0 ? relativeIndex : len + relativeIndex;
        // 6. If k < 0 or k ≥ len, then return undefined.
        if (k < 0 || k >= len) return JSValue.Undefined;
        // 7. Return ? Get(O, ! ToString(k)).
        return ArrayBuiltinsUtils.GetProperty(isolate, o, k);
    }

    // ---- Iterators (builtins-array-gen.cc) --------------------------------------------------------

    /// <summary>ES #sec-array.prototype.values.</summary>
    public static JSValue ArrayPrototypeValues(Isolate isolate, in BuiltinArguments args) =>
        isolate.Factory.NewJSArrayIterator(ObjectOps.ToObject(isolate, args.Receiver), IterationKind.Values);

    /// <summary>ES #sec-array.prototype.entries.</summary>
    public static JSValue ArrayPrototypeEntries(Isolate isolate, in BuiltinArguments args) =>
        isolate.Factory.NewJSArrayIterator(ObjectOps.ToObject(isolate, args.Receiver), IterationKind.Entries);

    /// <summary>ES #sec-array.prototype.keys.</summary>
    public static JSValue ArrayPrototypeKeys(Isolate isolate, in BuiltinArguments args) =>
        isolate.Factory.NewJSArrayIterator(ObjectOps.ToObject(isolate, args.Receiver), IterationKind.Keys);

    /// <summary>ES #sec-%arrayiteratorprototype%.next.</summary>
    public static JSValue ArrayIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Array Iterator.prototype.next";

        // If O does not have all of the internal slots of an Array Iterator Instance
        // (22.1.5.3), throw a TypeError exception
        if (args.Receiver.HeapObjectOrNull is not JSArrayIterator iterator)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, ArrayBuiltinsUtils.NewString(isolate, methodName),
                args.Receiver);
        }

        // Let a be O.[[IteratedObject]].
        var array = iterator.IteratedObject.As<JSReceiver>();
        // Let index be O.[[ArrayIteratorNextIndex]].
        double index = iterator.NextIndex.Number;
        double maxLength = ArrayBuiltinsUtils.kMaxSafeInteger;

        JSValue value;
        if (array is JSArray jsArray)
        {
            // If {array} is a JSArray, then the {index} must be in Unsigned32 range.
            maxLength = uint.MaxValue;
            if (index >= uint.MaxValue) return ArrayBuiltinsUtils.CreateIterResultObject(isolate, JSValue.Undefined, true);
            uint index32 = (uint)index;
            uint length32 = (uint)jsArray.Length.Number;
            if (index32 >= length32) return SetDone(isolate, iterator, maxLength);
            iterator.NextIndex = JSValue.FromNumber(index32 + 1.0);
            JSValue indexValue = JSValue.FromNumber(index32);
            if (iterator.Kind == IterationKind.Keys) return ArrayBuiltinsUtils.CreateIterResultObject(isolate, indexValue, false);
            value = TryLoadFastElement(isolate, jsArray, index32, out bool loaded);
            if (!loaded) value = ArrayBuiltinsUtils.GetProperty(isolate, array, index);
            return iterator.Kind == IterationKind.Values
                ? ArrayBuiltinsUtils.CreateIterResultObject(isolate, value, false)
                : ArrayBuiltinsUtils.CreateIterResultObjectForEntry(isolate, indexValue, value);
        }

        if (index >= maxLength) return ArrayBuiltinsUtils.CreateIterResultObject(isolate, JSValue.Undefined, true);

        if (array is JSTypedArray typedArray)
        {
            // If we go outside of the {length}, we don't need to update the
            // [[ArrayIteratorNextIndex]] anymore, since a JSTypedArray's
            // length cannot change anymore, so this {iterator} will never
            // produce values again anyways.
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(typedArray, TypedArrayAccessMode.kRead, out ulong length))
            {
                return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation,
                    ArrayBuiltinsUtils.NewString(isolate, methodName));
            }
            if (index >= length) return SetDone(isolate, iterator, maxLength);
            iterator.NextIndex = JSValue.FromNumber(index + 1);
            JSValue indexValue = JSValue.FromNumber(index);
            if (iterator.Kind == IterationKind.Keys) return ArrayBuiltinsUtils.CreateIterResultObject(isolate, indexValue, false);
            value = TypedArrayElementsOps.Load(isolate, typedArray, (ulong)index);
            return iterator.Kind == IterationKind.Values
                ? ArrayBuiltinsUtils.CreateIterResultObject(isolate, value, false)
                : ArrayBuiltinsUtils.CreateIterResultObjectForEntry(isolate, indexValue, value);
        }

        // if_other: check that the {index} is within the bounds of the {array}s "length".
        {
            JSValue lengthValue = ObjectOps.GetProperty(isolate, array, ReadOnlyRoots.length_string);
            double length = ObjectOps.ToLength(isolate, lengthValue).Number;
            if (index >= length) return SetDone(isolate, iterator, maxLength);
            iterator.NextIndex = JSValue.FromNumber(index + 1);
            JSValue indexValue = JSValue.FromNumber(index);
            if (iterator.Kind == IterationKind.Keys) return ArrayBuiltinsUtils.CreateIterResultObject(isolate, indexValue, false);
            value = ArrayBuiltinsUtils.GetProperty(isolate, array, index);
            return iterator.Kind == IterationKind.Values
                ? ArrayBuiltinsUtils.CreateIterResultObject(isolate, value, false)
                : ArrayBuiltinsUtils.CreateIterResultObjectForEntry(isolate, indexValue, value);
        }
    }

    /// <summary>
    /// set_done: the iterator never produces values again (V8 moves the index
    /// to the kind's maximum instead of clearing [[IteratedObject]]).
    /// </summary>
    static JSValue SetDone(Isolate isolate, JSArrayIterator iterator, double maxLength)
    {
        iterator.NextIndex = JSValue.FromNumber(maxLength);
        return ArrayBuiltinsUtils.CreateIterResultObject(isolate, JSValue.Undefined, true);
    }

    /// <summary>
    /// LoadFixedArrayBaseElementAsTagged with the hole handling of
    /// ArrayIteratorPrototypeNext: loaded is false when the generic Get must run.
    /// </summary>
    static JSValue TryLoadFastElement(Isolate isolate, JSArray array, uint index, out bool loaded)
    {
        loaded = false;
        ElementsKind kind = array.GetElementsKind();
        JSValue value;
        switch (array.Elements)
        {
            case FixedArray fa when (ElementsKinds.IsSmiOrObjectElementsKind(kind) || ElementsKinds.IsAnyNonextensibleElementsKind(kind)) &&
                                    index < (uint)fa.Length:
                value = fa[(int)index];
                break;
            case FixedDoubleArray fda when ElementsKinds.IsDoubleElementsKind(kind) && index < (uint)fda.Length:
                value = fda.Get((int)index);
                break;
            default:
                return JSValue.Undefined;
        }
        if (value.IsTheHole)
        {
            if (!Protectors.IsNoElementsIntact(isolate) || !IsPrototypeInitialArrayPrototype(isolate, array.Map))
            {
                return JSValue.Undefined;
            }
            value = JSValue.Undefined;
        }
        loaded = true;
        return value;
    }
}
