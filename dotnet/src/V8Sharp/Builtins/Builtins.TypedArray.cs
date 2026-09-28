// Port of the typed array construction and accessor builtins:
//   src/builtins/typed-array-createtypedarray.tq  CreateTypedArray and its
//     ConstructBy* macros, TypedArrayCreateByLength, TypedArraySpeciesCreate*,
//     TypedArrayCreateSameType
//   src/builtins/builtins-typed-array-gen.cc      TypedArrayBaseConstructor,
//     TypedArrayConstructor, the byteLength/byteOffset/length/@@toStringTag
//     getters, ValidateTypedArrayAndGetLength, PartiallyValidateTypedArrayMaybeOOB
//   src/builtins/builtins-typed-array.cc          TypedArrayPrototypeBuffer
//   src/builtins/typed-array-{entries,keys,values}.tq
//   src/builtins/typed-array.tq                    EnsureValid* helpers
//
// V8 allocates small typed arrays on the heap with an empty ArrayBuffer and
// materializes the buffer lazily (JSTypedArray::GetBuffer); V8Sharp always
// allocates the backing store up front. This is not observable.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterTypedArray()
    {
        Register(Builtin.TypedArrayBaseConstructor, BuiltinsTypedArray.TypedArrayBaseConstructor);
        Register(Builtin.TypedArrayConstructor, BuiltinsTypedArray.TypedArrayConstructor);
        Register(Builtin.TypedArrayPrototypeBuffer, BuiltinsTypedArray.TypedArrayPrototypeBuffer);
        Register(Builtin.TypedArrayPrototypeByteLength, BuiltinsTypedArray.TypedArrayPrototypeByteLength);
        Register(Builtin.TypedArrayPrototypeByteOffset, BuiltinsTypedArray.TypedArrayPrototypeByteOffset);
        Register(Builtin.TypedArrayPrototypeLength, BuiltinsTypedArray.TypedArrayPrototypeLength);
        Register(Builtin.TypedArrayPrototypeToStringTag, BuiltinsTypedArray.TypedArrayPrototypeToStringTag);
        Register(Builtin.TypedArrayPrototypeEntries, BuiltinsTypedArray.TypedArrayPrototypeEntries);
        Register(Builtin.TypedArrayPrototypeKeys, BuiltinsTypedArray.TypedArrayPrototypeKeys);
        Register(Builtin.TypedArrayPrototypeValues, BuiltinsTypedArray.TypedArrayPrototypeValues);
        RegisterTypedArrayMethods();
        RegisterTypedArrayBase64();
    }

    static partial void RegisterTypedArrayMethods();
    static partial void RegisterTypedArrayBase64();
}

/// <summary>The %TypedArray% constructors, getters and creation helpers.</summary>
public static partial class BuiltinsTypedArray
{
    // ---- Validation (typed-array.tq, builtins-typed-array-gen.cc) ----------------------------

    /// <summary>ThrowIfNotJSTypedArray: TypeError "Method X called on incompatible receiver Y".</summary>
    static JSTypedArray ThrowIfNotJSTypedArray(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSTypedArray array) return array;
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, ArrayBuiltinsUtils.NewString(isolate, methodName),
            receiver);
        return null!;
    }

    /// <summary>ThrowTypeError(kTypedArrayValidate[Write]ErrorOperation, methodName).</summary>
    internal static JSValue ThrowValidateError(Isolate isolate, string methodName, TypedArrayAccessMode mode) =>
        isolate.ThrowTypeError(JSTypedArray.ValidateErrorMessage(isolate, mode), ArrayBuiltinsUtils.NewString(isolate, methodName));

    /// <summary>
    /// TypedArrayBuiltinsAssembler::ValidateTypedArrayAndGetLength: the
    /// receiver as a typed array that is attached and in bounds (and writable
    /// for kWrite), with its current length.
    /// </summary>
    internal static JSTypedArray ValidateTypedArrayAndGetLength(Isolate isolate, JSValue obj, string methodName,
        TypedArrayAccessMode accessMode, out ulong length)
    {
        JSTypedArray array = ThrowIfNotJSTypedArray(isolate, obj, methodName);
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, accessMode, out length))
        {
            ThrowValidateError(isolate, methodName, accessMode);
        }
        return array;
    }

    /// <summary>
    /// PartiallyValidateTypedArrayMaybeOOB: throws for non-typed-arrays and
    /// detached (or, for kWrite, immutable) buffers; does not check bounds.
    /// </summary>
    static JSTypedArray PartiallyValidateTypedArrayMaybeOOB(Isolate isolate, JSValue obj, string methodName,
        TypedArrayAccessMode accessMode)
    {
        JSTypedArray array = ThrowIfNotJSTypedArray(isolate, obj, methodName);
        JSArrayBuffer buffer = array.Buffer;
        if (buffer.WasDetached || (accessMode == TypedArrayAccessMode.kWrite && buffer.IsImmutable))
        {
            ThrowValidateError(isolate, methodName, accessMode);
        }
        // Does not check for OOB since the clients of this function do that already.
        return array;
    }

    // ---- Construction (typed-array-createtypedarray.tq) --------------------------------------

    /// <summary>TypedArrayBaseConstructor: %TypedArray% itself throws.</summary>
    public static JSValue TypedArrayBaseConstructor(Isolate isolate, in BuiltinArguments args) =>
        isolate.ThrowTypeError(MessageTemplate.ConstructAbstractClass, ArrayBuiltinsUtils.NewString(isolate, "TypedArray"));

    /// <summary>ES #sec-typedarray-constructors.</summary>
    public static JSValue TypedArrayConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSFunction target = args.Target;
        // If NewTarget is undefined, throw a TypeError exception.
        // All the TypedArray constructors have this as the first step:
        // https://tc39.es/ecma262/#sec-typedarray-constructors
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, JSFunction.GetName(isolate, target));
        }
        return CreateTypedArray(isolate, target, args.NewTarget.As<JSReceiver>(), args.AtOrUndefined(1), args.AtOrUndefined(2),
            args.AtOrUndefined(3));
    }

    /// <summary>TypedArrayElementsInfo::CalculateByteLength: false (IfInvalid) when too large.</summary>
    static bool CalculateByteLength(ElementsKind kind, ulong length, out ulong byteLength)
    {
        int sizeLog2 = ElementsKinds.ElementsKindToShiftSize(kind);
        ulong maxArrayLength = JSArrayBuffer.kMaxByteLength >> sizeLog2;
        if (length > maxArrayLength)
        {
            byteLength = 0;
            return false;
        }
        byteLength = length << sizeLog2;
        return true;
    }

    /// <summary>ToIndex (base.tq): false (IfRangeError) for negative or unsafe integers.</summary>
    internal static bool ToIndex(Isolate isolate, JSValue value, out ulong index)
    {
        index = 0;
        if (value.IsUndefined) return true;
        double indexNumber = ObjectOps.IntegerValue(isolate, value);
        if (indexNumber < 0 || indexNumber > ArrayBuiltinsUtils.kMaxSafeInteger) return false;
        index = (ulong)indexNumber;
        return true;
    }

    /// <summary>
    /// AllocateTypedArray (off-heap): a typed array of <paramref name="map"/>
    /// viewing <paramref name="buffer"/>.
    /// </summary>
    static JSTypedArray AllocateTypedArray(Isolate isolate, Map map, JSArrayBuffer buffer, ulong byteOffset, ulong byteLength,
        bool isLengthTracking)
    {
        var typedArray = (JSTypedArray)isolate.Factory.NewJSObjectFromMap(map);
        typedArray.Elements = ByteArray.Empty;
        typedArray.Buffer = buffer;
        typedArray.ByteOffset = byteOffset;
        if (isLengthTracking)
        {
            Debug.Assert(buffer.IsResizableByJs);
            // Set the byte_length and length fields of length-tracking TAs to zero, so
            // that we won't accidentally use them and access invalid data.
            typedArray.RawByteLength = 0;
            typedArray.RawLength = 0;
        }
        else
        {
            typedArray.RawByteLength = byteLength;
            typedArray.RawLength = byteLength / (ulong)ElementsKinds.ElementsKindToByteSize(map.ElementsKind);
        }
        typedArray.IsLengthTracking = isLengthTracking;
        typedArray.IsBackedByRab = buffer.IsResizableByJs && !buffer.IsShared;
        return typedArray;
    }

    /// <summary>
    /// TypedArrayInitialize: a typed array of <paramref name="length"/>
    /// elements over a fresh ArrayBuffer. False (IfRangeError) when the length
    /// is too large.
    /// </summary>
    static bool TypedArrayInitialize(Isolate isolate, Map map, ulong length, out JSTypedArray typedArray)
    {
        typedArray = null!;
        if (!CalculateByteLength(map.ElementsKind, length, out ulong byteLength)) return false;
        // V8 constructs the buffer with %ArrayBuffer% (or arrayBufferConstructor_DoNotInitialize);
        // that is not observable, so the buffer is created directly.
        JSArrayBuffer? buffer = isolate.Factory.NewJSArrayBufferAndBackingStore(byteLength, initialized: true);
        if (buffer is null)
        {
            isolate.ThrowRangeError(MessageTemplate.ArrayBufferAllocationFailed);
            return false;
        }
        typedArray = AllocateTypedArray(isolate, map, buffer, 0, byteLength, false);
        return true;
    }

    /// <summary>ConstructByLength.</summary>
    static JSTypedArray ConstructByLength(Isolate isolate, JSFunction target, JSReceiver newTarget, JSValue lengthObj)
    {
        // i. Assert: firstArgument is not an Object.
        // ii. Let elementLength be ? ToIndex(firstArgument).
        if (ToIndex(isolate, lengthObj, out ulong length))
        {
            Map map = JSFunction.GetDerivedMap(isolate, target, newTarget);
            // iii. Return ? AllocateTypedArray(constructorName, NewTarget, proto,
            // elementLength).
            if (TypedArrayInitialize(isolate, map, length, out JSTypedArray typedArray)) return typedArray;
        }
        isolate.ThrowRangeError(MessageTemplate.InvalidTypedArrayLength, lengthObj);
        return null!;
    }

    /// <summary>ConstructByArrayLike (ES #sec-typedarray-object).</summary>
    static JSTypedArray ConstructByArrayLike(Isolate isolate, Map map, JSReceiver arrayLike, ulong length)
    {
        if (!TypedArrayInitialize(isolate, map, length, out JSTypedArray typedArray))
        {
            isolate.ThrowRangeError(MessageTemplate.InvalidTypedArrayLength, JSValue.FromNumber(length));
        }

        if (arrayLike is JSTypedArray src)
        {
            if (src.IsDetachedOrOutOfBounds)
            {
                isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation,
                    ArrayBuiltinsUtils.NewString(isolate, "Construct"));
            }
            ulong byteLength = src.GetByteLength();
            if (src.Kind != map.ElementsKind)
            {
                // IfElementsKindMismatch
                if (ElementsKinds.IsBigIntTypedArrayElementsKind(src.Kind) != ElementsKinds.IsBigIntTypedArrayElementsKind(map.ElementsKind))
                {
                    isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
                }
                // IfSlow
                if (length > 0) TypedArrayElementsOps.CopyElementsHandle(isolate, arrayLike, typedArray, length, 0);
            }
            else if (length > 0)
            {
                // CallCMemcpy / CallCRelaxedMemcpy
                src.Buffer.BackingStoreBuffer.AsSpan((int)src.ByteOffset, (int)byteLength)
                    .CopyTo(typedArray.Buffer.BackingStoreBuffer);
            }
        }
        else if (length > 0)
        {
            // IfSlow: Runtime_TypedArrayCopyElements.
            TypedArrayElementsOps.CopyElementsHandle(isolate, arrayLike, typedArray, length, 0);
        }
        return typedArray;
    }

    /// <summary>ConstructByIterable: the source values as an array-like (JSArray).</summary>
    static JSArray ConstructByIterable(Isolate isolate, JSReceiver iterable, JSValue iteratorFn)
    {
        if (iterable is JSArray array)
        {
            ElementsKind elementsKind = array.GetElementsKind();
            // Can only use the fast path for numeric fast elements arrays, since
            // objects in the array can have side-effects in their ToNumber conversion.
            if ((ElementsKinds.IsSmiElementsKind(elementsKind) || ElementsKinds.IsDoubleElementsKind(elementsKind)) &&
                // Check that the ArrayIterator prototype's "next" method hasn't been
                // overridden.
                Protectors.IsArrayIteratorLookupChainIntact(isolate) &&
                // Check that the iterator function is exactly
                // Builtin::kArrayPrototypeValues.
                IsBuiltinFunction(iteratorFn, Builtin.ArrayPrototypeValues))
            {
                return array;
            }
        }
        // UseUserProvidedIterator
        return IterableToListConvertHoles(isolate, iterable, iteratorFn);
    }

    /// <summary>True when <paramref name="fn"/> is a JSFunction running <paramref name="builtin"/>.</summary>
    internal static bool IsBuiltinFunction(JSValue fn, Builtin builtin) =>
        fn.HeapObjectOrNull is JSFunction function && function.Shared.HasBuiltinId && function.Shared.BuiltinId == builtin;

    /// <summary>
    /// IterableToListConvertHoles: fast JSArrays without custom iteration are
    /// cloned with holes as undefined; everything else is iterated.
    /// </summary>
    internal static JSArray IterableToListConvertHoles(Isolate isolate, JSValue iterable, JSValue iteratorFn)
    {
        if (iterable.HeapObjectOrNull is JSArray array && BuiltinsArray.IsFastJSArrayWithNoCustomIteration(isolate, array) &&
            IsBuiltinFunction(iteratorFn, Builtin.ArrayPrototypeValues))
        {
            // CloneFastJSArrayFillingHoles
            return BuiltinsArray.CloneFastJSArrayFillingHoles(isolate, array);
        }
        return ArrayBuiltinsUtils.IterableToList(isolate, iterable, iteratorFn);
    }

    /// <summary>ConstructByTypedArray: the source's length (0 if detached or out of bounds).</summary>
    static ulong ConstructByTypedArray(JSTypedArray srcTypedArray)
    {
        // TODO(petermarshall): Throw on detached typedArray.
        return TypedArrayElementsOps.TryGetLengthAndValidate(srcTypedArray, TypedArrayAccessMode.kRead, out ulong length)
            ? length
            : 0;
    }

    /// <summary>JSFunction::GetDerivedRabGsabTypedArrayMap.</summary>
    static Map GetDerivedRabGsabTypedArrayMap(Isolate isolate, JSFunction constructor, JSReceiver newTarget)
    {
        Map map = JSFunction.GetDerivedMap(isolate, constructor, newTarget);
        NativeContext context = isolate.NativeContext;
        if (ReferenceEquals(newTarget, context.Slots[(int)JSTypedArray.ConstructorIndexForKind(map.ElementsKind)].HeapObjectOrNull))
        {
            return JSTypedArray.TypedArrayElementsKindToRabGsabCtorMap(context, map.ElementsKind);
        }
        // This only happens when subclassing TypedArrays. Create a new map with the
        // corresponding RAB / GSAB ElementsKind. Note: the map is not cached and
        // reused -> every array gets a unique map, making ICs slow.
        Map rabGsabMap = Map.Copy(isolate, map, "RAB / GSAB");
        rabGsabMap.SetElementsKind(ElementsKinds.GetCorrespondingRabGsabElementsKind(map.ElementsKind));
        return rabGsabMap;
    }

    /// <summary>ConstructByArrayBuffer (ES #sec-initializetypedarrayfromarraybuffer).</summary>
    static JSTypedArray ConstructByArrayBuffer(Isolate isolate, JSFunction target, JSReceiver newTarget, JSArrayBuffer buffer,
        JSValue byteOffset, JSValue length)
    {
        bool isLengthTracking = buffer.IsResizableByJs && length.IsUndefined;
        // Pick the RAB / GSAB map (containing the corresponding RAB / GSAB
        // ElementsKind). GSAB-backed non-length-tracking TypedArrays behave just like
        // normal TypedArrays, so exclude them.
        bool rabGsab = buffer.IsResizableByJs && (!buffer.IsShared || isLengthTracking);
        Map map = rabGsab
            ? GetDerivedRabGsabTypedArrayMap(isolate, target, newTarget)
            : JSFunction.GetDerivedMap(isolate, target, newTarget);

        // 1. Let elementSize be TypedArrayElementSize(O).
        ElementsKind kind = map.ElementsKind;
        ulong elementSize = (ulong)ElementsKinds.ElementsKindToByteSize(kind);

        // 2. Let offset be ? ToIndex(byteOffset).
        if (!ToIndex(isolate, byteOffset, out ulong offset)) return ThrowInvalidOffset(isolate, byteOffset);

        // 3. If offset modulo elementSize ≠ 0, throw a RangeError exception.
        if (offset % elementSize != 0) return ThrowInvalidAlignment(isolate, map, "start offset");

        // 5. If length is not undefined, then
        // a. Let newLength be ? ToIndex(length).
        if (!ToIndex(isolate, length, out ulong newLength)) return ThrowInvalidLength(isolate, length);
        ulong newByteLength;

        // 6. If IsDetachedBuffer(buffer) is true, throw a TypeError exception.
        if (buffer.WasDetached)
        {
            isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation, ArrayBuiltinsUtils.NewString(isolate, "Construct"));
        }

        // 7. Let bufferByteLength be ArrayBufferByteLength(buffer, SeqCst).
        ulong bufferByteLength = buffer.GetByteLength();

        // 8. If length is undefined and bufferIsResizable is true, then
        //   a. If offset > bufferByteLength, throw a RangeError exception.
        //   b. Set O.[[ByteLength]] to auto.
        //   c. Set O.[[ArrayLength]] to auto.
        if (isLengthTracking)
        {
            if (bufferByteLength < offset) return ThrowInvalidOffset(isolate, byteOffset);
            newByteLength = 0;
        }
        else if (length.IsUndefined)
        {
            // 9. Else
            //   a. If length is undefined, then
            //   i. If bufferByteLength modulo elementSize ≠ 0, throw a RangeError
            //   exception.
            if (bufferByteLength % elementSize != 0) return ThrowInvalidAlignment(isolate, map, "byte length");

            //   ii. Let newByteLength be bufferByteLength - offset.
            //   iii. If newByteLength < 0, throw a RangeError exception.
            if (bufferByteLength < offset) return ThrowInvalidOffset(isolate, byteOffset);
            newByteLength = bufferByteLength - offset;
        }
        else
        {
            // b. Else,
            //   i. Let newByteLength be newLength × elementSize.
            if (!CalculateByteLength(kind, newLength, out newByteLength)) return ThrowInvalidLength(isolate, length);

            //   ii. If offset + newByteLength > bufferByteLength, throw a
            //   RangeError exception.
            if (bufferByteLength < newByteLength || offset > bufferByteLength - newByteLength)
            {
                return ThrowInvalidLength(isolate, length);
            }
        }

        return AllocateTypedArray(isolate, map, buffer, offset, newByteLength, isLengthTracking);
    }

    static JSTypedArray ThrowInvalidOffset(Isolate isolate, JSValue byteOffset)
    {
        isolate.ThrowRangeError(MessageTemplate.InvalidOffset, byteOffset);
        return null!;
    }

    static JSTypedArray ThrowInvalidLength(Isolate isolate, JSValue length)
    {
        isolate.ThrowRangeError(MessageTemplate.InvalidTypedArrayLength, length);
        return null!;
    }

    /// <summary>Runtime_ThrowInvalidTypedArrayAlignment.</summary>
    static JSTypedArray ThrowInvalidAlignment(Isolate isolate, Map map, string problemString)
    {
        ElementsKind kind = map.ElementsKind;
        isolate.ThrowRangeError(MessageTemplate.InvalidTypedArrayAlignment, ArrayBuiltinsUtils.NewString(isolate, problemString),
            JSTypedArray.TypedArrayClassName(kind), JSValue.FromInt(ElementsKinds.ElementsKindToByteSize(kind)));
        return null!;
    }

    /// <summary>CreateTypedArray (ES #sec-typedarray-constructors).</summary>
    public static JSTypedArray CreateTypedArray(Isolate isolate, JSFunction target, JSReceiver newTarget, JSValue arg1,
        JSValue arg2, JSValue arg3)
    {
        Debug.Assert(target.Map.IsConstructor);
        JSReceiver arrayLike;
        ulong length;
        // 4. Let O be ? AllocateTypedArray(constructorName, NewTarget,
        // "%TypedArrayPrototype%").
        switch (arg1.HeapObjectOrNull)
        {
            case JSArrayBuffer buffer:
                return ConstructByArrayBuffer(isolate, target, newTarget, buffer, arg2, arg3);
            case JSTypedArray typedArray:
                arrayLike = typedArray;
                length = ConstructByTypedArray(typedArray);
                break;
            case JSReceiver obj:
            {
                // ConstructByJSReceiver
                JSValue iteratorMethod = ArrayBuiltinsUtils.GetIteratorMethod(isolate, obj);
                if (!iteratorMethod.IsNullOrUndefined)
                {
                    if (!ObjectOps.IsCallable(iteratorMethod))
                    {
                        // IfIteratorNotCallable
                        isolate.ThrowTypeError(MessageTemplate.FirstArgumentIteratorSymbolNonCallable,
                            ArrayBuiltinsUtils.NewString(isolate, "TypedArray's constructor"));
                    }
                    JSArray array = ConstructByIterable(isolate, obj, iteratorMethod);
                    // Max JSArray length is a valid JSTypedArray length so we just use it.
                    arrayLike = array;
                    length = (ulong)array.Length.Number;
                }
                else
                {
                    // IfIteratorUndefined
                    JSValue lengthObj = ObjectOps.GetProperty(isolate, obj, ReadOnlyRoots.length_string);
                    double lengthNumber = ObjectOps.ToLength(isolate, lengthObj).Number;
                    // Throw RangeError here if the length does not fit in uintptr because
                    // such a length will not pass bounds checks in ConstructByArrayLike()
                    // anyway.
                    arrayLike = obj;
                    length = (ulong)lengthNumber;
                }
                break;
            }
            default:
                // The first argument was a number or fell through and is treated as
                // a number. https://tc39.es/ecma262/#sec-typedarray-length
                return ConstructByLength(isolate, target, newTarget, arg1);
        }

        // IfConstructByArrayLike
        Map map = JSFunction.GetDerivedMap(isolate, target, newTarget);
        return ConstructByArrayLike(isolate, map, arrayLike, length);
    }

    /// <summary>TypedArrayCreateByLength (ES #typedarray-create with a single Number).</summary>
    internal static JSTypedArray TypedArrayCreateByLength(Isolate isolate, JSValue constructor, double length, string methodName,
        TypedArrayAccessMode accessMode)
    {
        // 1. Let newTypedArray be ? Construct(constructor, argumentList).
        JSValue newTypedArrayObj = Execution.New(isolate, constructor, [JSValue.FromNumber(length)]);

        // 2. Perform ? ValidateTypedArray(newTypedArray).
        JSTypedArray newTypedArray = ValidateTypedArrayAndGetLength(isolate, newTypedArrayObj, methodName, accessMode,
            out ulong newTypedArrayLength);

        if (newTypedArray.Buffer.WasDetached)
        {
            isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation, ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        // 3. If argumentList is a List of a single Number, then
        //   a. If newTypedArray.[[ArrayLength]] < argumentList[0], throw a
        //      TypeError exception.
        if (newTypedArrayLength < length) isolate.ThrowTypeError(MessageTemplate.TypedArrayTooShort);

        // 4. Return newTypedArray.
        return newTypedArray;
    }

    /// <summary>TypedArraySpeciesCreateMaybeOOB: only partially validates the created typed array.</summary>
    static JSTypedArray TypedArraySpeciesCreateMaybeOOB(Isolate isolate, string methodName, int numArgs, JSTypedArray exemplar,
        JSValue arg0, JSValue arg1, JSValue arg2, TypedArrayAccessMode accessMode)
    {
        NativeContext nativeContext = isolate.NativeContext;
        JSFunction defaultConstructor = JSTypedArray.ConstructorForKind(nativeContext, exemplar.Kind);

        if (!IsPrototypeTypedArrayPrototype(isolate, exemplar.Map) ||
            !Protectors.IsTypedArraySpeciesLookupChainIntact(isolate))
        {
            // IfSlow
            JSValue constructor = ObjectOps.SpeciesConstructor(isolate, exemplar, defaultConstructor);
            JSValue newObj;
            if (numArgs == 1)
            {
                newObj = Execution.New(isolate, constructor, [arg0]);
            }
            else
            {
                Debug.Assert(numArgs == 3);
                newObj = arg2.IsUndefined
                    ? Execution.New(isolate, constructor, [arg0, arg1])
                    : Execution.New(isolate, constructor, [arg0, arg1, arg2]);
            }
            return PartiallyValidateTypedArrayMaybeOOB(isolate, newObj, methodName, accessMode);
        }

        JSTypedArray typedArray = CreateTypedArray(isolate, defaultConstructor, defaultConstructor, arg0, arg1, arg2);
        // It is assumed that the CreateTypedArray builtin does not produce a
        // typed array that fails ValidateTypedArray
        Debug.Assert(!typedArray.Buffer.WasDetached);
        return typedArray;
    }

    /// <summary>
    /// CodeStubAssembler::IsPrototypeTypedArrayPrototype: the prototype of the
    /// map's prototype is %TypedArray%.prototype.
    /// </summary>
    static bool IsPrototypeTypedArrayPrototype(Isolate isolate, Map map)
    {
        if (map.Prototype is not JSObject prototype) return false;
        return ReferenceEquals(prototype.Map.Prototype, isolate.NativeContext.TypedArrayPrototype);
    }

    /// <summary>TypedArraySpeciesCreateByLength.</summary>
    internal static JSTypedArray TypedArraySpeciesCreateByLength(Isolate isolate, string methodName, JSTypedArray exemplar,
        ulong length, TypedArrayAccessMode accessMode)
    {
        JSTypedArray typedArray = TypedArraySpeciesCreateMaybeOOB(isolate, methodName, 1, exemplar, JSValue.FromNumber(length),
            JSValue.Undefined, JSValue.Undefined, accessMode);
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(typedArray, TypedArrayAccessMode.kRead, out ulong createdArrayLength) ||
            createdArrayLength < length)
        {
            isolate.ThrowTypeError(MessageTemplate.TypedArrayTooShort);
        }
        return typedArray;
    }

    /// <summary>TypedArraySpeciesCreateByBuffer.</summary>
    static JSTypedArray TypedArraySpeciesCreateByBuffer(Isolate isolate, string methodName, JSTypedArray exemplar,
        JSArrayBuffer buffer, ulong beginByteOffset, JSValue newLength, TypedArrayAccessMode accessMode) =>
        TypedArraySpeciesCreateMaybeOOB(isolate, methodName, 3, exemplar, buffer, JSValue.FromNumber(beginByteOffset), newLength,
            accessMode);

    /// <summary>TypedArrayCreateSameType: a new, non-resizable array of the exemplar's type.</summary>
    internal static JSTypedArray TypedArrayCreateSameType(Isolate isolate, JSTypedArray exemplar, ulong newLength)
    {
        JSFunction constructor = JSTypedArray.ConstructorForKind(isolate.NativeContext, exemplar.Kind);
        JSTypedArray typedArray = CreateTypedArray(isolate, constructor, constructor, JSValue.FromNumber(newLength),
            JSValue.Undefined, JSValue.Undefined);
        Debug.Assert(!typedArray.Buffer.WasDetached);
        return typedArray;
    }

    // ---- Getters (builtins-typed-array-gen.cc, builtins-typed-array.cc) ------------------------

    /// <summary>ES6 section 22.2.3.1 get %TypedArray%.prototype.buffer.</summary>
    public static JSValue TypedArrayPrototypeBuffer(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSTypedArray typedArray)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                ArrayBuiltinsUtils.NewString(isolate, "get %TypedArray%.prototype.buffer"), args.Receiver);
        }
        // JSTypedArray::GetBuffer: V8Sharp typed arrays are never on-heap.
        return typedArray.Buffer;
    }

    /// <summary>ES #sec-get-%typedarray%.prototype.bytelength.</summary>
    public static JSValue TypedArrayPrototypeByteLength(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray receiverArray = ThrowIfNotJSTypedArray(isolate, args.Receiver, "get TypedArray.prototype.byteLength");
        if (receiverArray.IsVariableLength)
        {
            // LoadVariableLengthJSTypedArrayByteLength: 0 when detached or out of bounds.
            if (receiverArray.WasDetached) return JSValue.Zero;
            ulong byteLength = receiverArray.GetVariableByteLengthOrOutOfBounds(out bool outOfBounds);
            return outOfBounds ? JSValue.Zero : JSValue.FromNumber(byteLength);
        }
        if (receiverArray.Buffer.WasDetached) return JSValue.Zero;
        return JSValue.FromNumber(receiverArray.RawByteLength);
    }

    /// <summary>ES #sec-get-%typedarray%.prototype.byteoffset.</summary>
    public static JSValue TypedArrayPrototypeByteOffset(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray receiverArray = ThrowIfNotJSTypedArray(isolate, args.Receiver, "get TypedArray.prototype.byteOffset");
        // Default to zero if the {receiver}s buffer was detached / out of bounds.
        if (receiverArray.IsViewDetachedOrOutOfBounds) return JSValue.Zero;
        return JSValue.FromNumber(receiverArray.ByteOffset);
    }

    /// <summary>ES #sec-get-%typedarray%.prototype.length.</summary>
    public static JSValue TypedArrayPrototypeLength(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray receiverArray = ThrowIfNotJSTypedArray(isolate, args.Receiver, "get TypedArray.prototype.length");
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(receiverArray, TypedArrayAccessMode.kRead, out ulong length))
        {
            return JSValue.Zero;
        }
        return JSValue.FromNumber(length);
    }

    /// <summary>ES #sec-get-%typedarray%.prototype-@@tostringtag.</summary>
    public static JSValue TypedArrayPrototypeToStringTag(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is JSObject obj && ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(obj.Map.ElementsKind))
        {
            return JSTypedArray.TypedArrayClassName(obj.Map.ElementsKind);
        }
        return JSValue.Undefined;
    }

    // ---- Iterators (typed-array-{entries,keys,values}.tq) ----------------------------------------

    static JSValue CreateIterator(Isolate isolate, JSValue receiver, IterationKind kind, string name,
        MessageTemplate detachedMessage)
    {
        if (receiver.HeapObjectOrNull is not JSTypedArray array)
        {
            return isolate.ThrowTypeError(MessageTemplate.NotTypedArray, ArrayBuiltinsUtils.NewString(isolate, name));
        }
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kRead, out _))
        {
            return isolate.ThrowTypeError(detachedMessage, ArrayBuiltinsUtils.NewString(isolate, name));
        }
        return isolate.Factory.NewJSArrayIterator(array, kind);
    }

    /// <summary>ES #sec-%typedarray%.entries.</summary>
    public static JSValue TypedArrayPrototypeEntries(Isolate isolate, in BuiltinArguments args) =>
        CreateIterator(isolate, args.Receiver, IterationKind.Entries, "%TypedArray%.prototype.entries",
            MessageTemplate.TypedArrayValidateErrorOperation);

    /// <summary>ES #sec-%typedarray%.keys.</summary>
    public static JSValue TypedArrayPrototypeKeys(Isolate isolate, in BuiltinArguments args) =>
        CreateIterator(isolate, args.Receiver, IterationKind.Keys, "%TypedArray%.prototype.keys",
            MessageTemplate.TypedArrayDetachedErrorOperation);

    /// <summary>ES #sec-%typedarray%.values.</summary>
    public static JSValue TypedArrayPrototypeValues(Isolate isolate, in BuiltinArguments args) =>
        CreateIterator(isolate, args.Receiver, IterationKind.Values, "%TypedArray%.prototype.values",
            MessageTemplate.TypedArrayValidateErrorOperation);
}
