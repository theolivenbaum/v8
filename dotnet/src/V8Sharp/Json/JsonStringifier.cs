// Port of src/json/json-stringifier.cc: JsonStringifier (replacer and gap
// handling, toJSON and replacer calls, the fast paths over fast-mode objects
// and fast-elements arrays, the slow paths over KeyAccumulator keys and array
// likes, proxies, the circular-structure error message, the JSON string
// escaping) and JsonStringify.
//
// Deviations (deviations.md, JSON): V8 first tries FastJsonStringifier, a
// no-side-effect serializer that restarts in JsonStringifier when it meets
// anything it cannot handle; both produce the same text, and only
// JsonStringifier is ported, writing into one pooled UTF-16 buffer and
// escaping with SearchValues. V8's lazy cycle-detection stack (need_stack_,
// restarting the serialization when nesting exceeds 10) is replaced by always
// keeping the stack, which reports the same errors.
using System.Buffers;
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Json;

public sealed class JsonStringifier
{
    enum Result { UNCHANGED, SUCCESS, EXCEPTION }

    const int kInitialPartLength = 2048;
    const int kCircularErrorMessagePrefixCount = 2;
    const int kCircularErrorMessagePostfixCount = 1;

    readonly Isolate _isolate;
    List<JSString>? _propertyList;
    JSReceiver? _replacerFunction;
    string? _gap;
    int _indent;

    char[] _buffer;
    int _currentIndex;
    bool _overflowed;

    readonly List<(JSValue Key, JSReceiver Object)> _stack = [];

    /// <summary>
    /// The ASCII characters that need escaping in a JSON string (controls, '"'
    /// and '\'); surrogates are found with a range search. V8's
    /// DoNotEscape / JsonDoNotEscapeFlagTable.
    /// </summary>
    static readonly SearchValues<char> s_asciiNeedsEscape = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000a\u000b\u000c\u000d\u000e\u000f" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f");

    JsonStringifier(Isolate isolate)
    {
        _isolate = isolate;
        _buffer = ArrayPool<char>.Shared.Rent(kInitialPartLength);
    }

    /// <summary>JsonStringify (json-stringifier.h): JSON.stringify(object, replacer, gap).</summary>
    public static JSValue JsonStringify(Isolate isolate, JSValue obj, JSValue replacer, JSValue gap)
    {
        var stringifier = new JsonStringifier(isolate);
        int registerStackTop = isolate.RegisterStackTop;
        try
        {
            return stringifier.Stringify(obj, replacer, gap);
        }
        finally
        {
            stringifier.Release();
            isolate.RegisterStackTop = registerStackTop;
        }
    }

    void Release()
    {
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = [];
    }

    JSValue Stringify(JSValue obj, JSValue replacer, JSValue gap)
    {
        InitializeReplacer(replacer);
        if (!gap.IsUndefined) InitializeGap(gap);
        Result result = SerializeObject(obj);
        if (result == Result.UNCHANGED) return JSValue.Undefined;
        Debug.Assert(result == Result.SUCCESS);
        if (_overflowed || _currentIndex > JSString.kMaxLength)
        {
            _isolate.Throw(_isolate.Factory.NewInvalidStringLengthError());
        }
        return _isolate.Factory.NewStringFromUtf16(_buffer.AsSpan(0, _currentIndex));
    }

    void InitializeReplacer(JSValue replacer)
    {
        Debug.Assert(_propertyList is null && _replacerFunction is null);
        if (ObjectOps.IsArray(_isolate, replacer))
        {
            var set = new HashSet<JSString>(ReferenceEqualityComparer.Instance);
            var list = new List<JSString>();
            var replacerObj = (JSReceiver)replacer.Object;
            JSValue lengthObj = ObjectOps.GetLengthFromArrayLike(_isolate, replacerObj);
            if (!ObjectOps.ToUint32(lengthObj, out uint length)) length = uint.MaxValue;
            for (uint i = 0; i < length; i++)
            {
                JSValue element = ObjectOps.GetElement(_isolate, replacer, i);
                JSString? key = null;
                if (element.IsNumber || element.IsString)
                {
                    key = ObjectOps.ToString(_isolate, element);
                }
                else if (element.HeapObjectOrNull is JSPrimitiveWrapper wrapper)
                {
                    JSValue value = wrapper.Value;
                    if (value.IsNumber || value.IsString) key = ObjectOps.ToString(_isolate, element);
                }
                if (key is null) continue;
                // Object keys are internalized, so do it here.
                key = _isolate.Factory.InternalizeString(key);
                if (set.Add(key)) list.Add(key);
            }
            _propertyList = list;
        }
        else if (ObjectOps.IsCallable(replacer))
        {
            _replacerFunction = (JSReceiver)replacer.Object;
        }
    }

    void InitializeGap(JSValue gap)
    {
        Debug.Assert(_gap is null);
        if (gap.HeapObjectOrNull is JSPrimitiveWrapper wrapper)
        {
            JSValue value = wrapper.Value;
            if (value.IsString)
            {
                gap = ObjectOps.ToString(_isolate, gap);
            }
            else if (value.IsNumber)
            {
                gap = ObjectOps.ToNumber(_isolate, gap);
            }
        }

        if (gap.HeapObjectOrNull is JSString gapString)
        {
            if (gapString.Length > 0)
            {
                int gapLength = Math.Min(gapString.Length, 10);
                _gap = new string(gapString.FlatSpan()[..gapLength]);
            }
        }
        else if (gap.IsNumber)
        {
            double value = Math.Min(gap.Number, 10.0);
            if (value > 0)
            {
                uint gapLength = Conversions.DoubleToUint32(value);
                _gap = new string(' ', (int)gapLength);
            }
        }
    }

    JSValue ApplyToJsonFunction(JSValue obj, JSValue key)
    {
        // Retrieve toJSON function. The LookupIterator automatically handles
        // the ToObject() equivalent ("GetRoot") if {object} is a BigInt.
        JSValue fun = ObjectOps.GetProperty(_isolate, obj, ReadOnlyRoots.toJSON_string);
        if (!ObjectOps.IsCallable(fun)) return obj;

        // Call toJSON function.
        if (key.IsNumber) key = _isolate.Factory.SmiToString((int)key.Number);
        return Execution.Call(_isolate, fun, obj, [key]);
    }

    JSValue ApplyReplacerFunction(JSValue value, JSValue key, JSValue initialHolder)
    {
        if (key.IsNumber) key = _isolate.Factory.SmiToString((int)key.Number);
        JSReceiver holder = CurrentHolder(initialHolder);
        return Execution.Call(_isolate, _replacerFunction!, holder, [key, value]);
    }

    JSReceiver CurrentHolder(JSValue initialHolder)
    {
        if (_stack.Count == 0)
        {
            JSObject holder = _isolate.Factory.NewJSObject(_isolate.NativeContext.ObjectFunction);
            JSObject.AddProperty(_isolate, holder, ReadOnlyRoots.empty_string, initialHolder, PropertyAttributes.NONE);
            return holder;
        }
        return _stack[^1].Object;
    }

    /// <summary>
    /// The machine stack one level of V8's recursive Serialize_ takes, in stack
    /// slots. Deviation: the recursion runs on the .NET stack, sized apart from
    /// --stack-size; each level also reserves these slots on the register stack
    /// (V8Sharp's stand-in for V8's machine stack), so deep structures overflow
    /// at about V8's depth.
    /// </summary>
    const int kSerializeFrameSlots = 20;

    Result StackPush(JSReceiver obj, JSValue key)
    {
        _isolate.StackGuard.StackCheck(_isolate);
        _isolate.AllocateRegisters(kSerializeFrameSlots);
        for (int i = 0; i < _stack.Count; ++i)
        {
            if (ReferenceEquals(_stack[i].Object, obj))
            {
                JSString circleDescription = ConstructCircularStructureErrorMessage(key, i);
                _isolate.Throw(_isolate.Factory.NewTypeError(MessageTemplate.CircularStructure, circleDescription));
                return Result.EXCEPTION;
            }
        }
        _stack.Add((key, obj));
        return Result.SUCCESS;
    }

    void StackPop()
    {
        _stack.RemoveAt(_stack.Count - 1);
        _isolate.RegisterStackTop -= kSerializeFrameSlots;
    }

    // ---------------------------------------------------------------------
    // CircularStructureMessageBuilder.

    JSString ConstructCircularStructureErrorMessage(JSValue lastKey, int startIndex)
    {
        Debug.Assert(startIndex < _stack.Count);
        var builder = new IncrementalStringBuilder(_isolate);

        // We track the index to be printed next for better readability.
        int index = startIndex;
        int stackSize = _stack.Count;

        // AppendStartLine
        builder.AppendCString("\n    --> ");
        builder.AppendCStringLiteral("starting at object with constructor ");
        AppendConstructorName(builder, _stack[index++].Object);

        // Append a maximum of kCircularErrorMessagePrefixCount normal lines.
        int prefixEnd = Math.Min(stackSize, index + kCircularErrorMessagePrefixCount);
        for (; index < prefixEnd; ++index)
        {
            AppendNormalLine(builder, _stack[index].Key, _stack[index].Object);
        }

        // If the circle consists of too many objects, we skip them and just
        // print an ellipsis.
        if (stackSize > index + kCircularErrorMessagePostfixCount)
        {
            builder.AppendCString("\n    |     ");
            builder.AppendCStringLiteral("...");
        }

        // Since we calculate the postfix lines from the back of the stack,
        // we have to ensure that lines are not printed twice.
        index = Math.Max(index, stackSize - kCircularErrorMessagePostfixCount);
        for (; index < stackSize; ++index)
        {
            AppendNormalLine(builder, _stack[index].Key, _stack[index].Object);
        }

        // AppendClosingLine
        builder.AppendCString("\n    --- ");
        AppendKey(builder, lastKey);
        builder.AppendCStringLiteral(" closes the circle");

        return builder.Finish();
    }

    void AppendNormalLine(IncrementalStringBuilder builder, JSValue key, JSReceiver obj)
    {
        builder.AppendCString("\n    |     ");
        AppendKey(builder, key);
        builder.AppendCStringLiteral(" -> object with constructor ");
        AppendConstructorName(builder, obj);
    }

    void AppendConstructorName(IncrementalStringBuilder builder, JSReceiver obj)
    {
        builder.AppendCharacter('\'');
        JSString constructorName = JSReceiver.GetConstructorName(_isolate, obj);
        builder.AppendString(constructorName);
        builder.AppendCharacter('\'');
    }

    // A key can either be a string, the empty string or a Smi.
    static void AppendKey(IncrementalStringBuilder builder, JSValue key)
    {
        if (key.IsNumber)
        {
            builder.AppendCStringLiteral("index ");
            builder.AppendInt((int)key.Number);
            return;
        }

        var keyAsString = (JSString)key.Object;
        if (keyAsString.Length == 0)
        {
            builder.AppendCStringLiteral("<anonymous>");
        }
        else
        {
            builder.AppendCStringLiteral("property '");
            builder.AppendString(keyAsString);
            builder.AppendCharacter('\'');
        }
    }

    // ---------------------------------------------------------------------
    // Serialization.

    /// <summary>Entry point to serialize the object.</summary>
    Result SerializeObject(JSValue obj) => Serialize(obj, false, ReadOnlyRoots.empty_string, deferredStringKey: false);

    /// <summary>Serialize an array element. The index may serve as argument for the toJSON function.</summary>
    Result SerializeElement(JSValue obj, int i) => Serialize(obj, false, JSValue.FromInt(i), deferredStringKey: false);

    /// <summary>
    /// Serialize an object property. The key may or may not be serialized
    /// depending on the property. The key may also serve as argument for the
    /// toJSON function.
    /// </summary>
    Result SerializeProperty(JSValue obj, bool deferredComma, JSString deferredKey) =>
        Serialize(obj, deferredComma, deferredKey, deferredStringKey: true);

    static bool MayHaveInterestingProperties(Isolate isolate, JSReceiver obj)
    {
        for (var iter = new PrototypeIterator(isolate, obj, WhereToStart.StartAtReceiver); !iter.IsAtEnd; iter.Advance())
        {
            JSReceiver current = iter.GetCurrent()!;
            if (current is JSProxy || current.Map.MayHaveInterestingProperties) return true;
        }
        return false;
    }

    /// <summary>JsonStringifier::Serialize_.</summary>
    Result Serialize(JSValue obj, bool comma, JSValue key, bool deferredStringKey)
    {
        StackGuard stackGuard = _isolate.StackGuard;
        if (stackGuard.HasPendingInterrupts) stackGuard.HandleInterrupts();

        JSValue initialValue = obj;
        if ((obj.HeapObjectOrNull is JSReceiver receiver && MayHaveInterestingProperties(_isolate, receiver)) || obj.IsBigInt)
        {
            obj = ApplyToJsonFunction(obj, key);
        }
        if (_replacerFunction is not null)
        {
            obj = ApplyReplacerFunction(obj, key, initialValue);
        }

        if (obj.IsNumber)
        {
            if (deferredStringKey) SerializeDeferredKey(comma, key);
            if (obj.IsSmi) return SerializeSmi((int)obj.Number);
            return SerializeDouble(obj.Number);
        }

        HeapObject heapObject = obj.HeapObjectOrNull!;
        if (heapObject is null) return Result.UNCHANGED;  // undefined
        switch (heapObject)
        {
            case BigInt:
                _isolate.ThrowTypeError(MessageTemplate.BigIntSerializeJSON);
                return Result.EXCEPTION;
            case Oddball oddball:
                if (ReferenceEquals(oddball, Oddball.False))
                {
                    if (deferredStringKey) SerializeDeferredKey(comma, key);
                    AppendCStringLiteral("false");
                    return Result.SUCCESS;
                }
                if (ReferenceEquals(oddball, Oddball.True))
                {
                    if (deferredStringKey) SerializeDeferredKey(comma, key);
                    AppendCStringLiteral("true");
                    return Result.SUCCESS;
                }
                if (ReferenceEquals(oddball, Oddball.Null))
                {
                    if (deferredStringKey) SerializeDeferredKey(comma, key);
                    AppendCStringLiteral("null");
                    return Result.SUCCESS;
                }
                return Result.UNCHANGED;
            case JSString s:
                if (deferredStringKey) SerializeDeferredKey(comma, key);
                SerializeString(s, rawJson: false);
                return Result.SUCCESS;
            case Symbol:
                return Result.UNCHANGED;
            case JSArray array:
                if (deferredStringKey) SerializeDeferredKey(comma, key);
                return SerializeJSArray(array, key);
            case JSPrimitiveWrapper wrapper:
                if (deferredStringKey) SerializeDeferredKey(comma, key);
                return SerializeJSPrimitiveWrapper(wrapper, key);
            case JSRawJson rawJsonObj:
            {
                if (deferredStringKey) SerializeDeferredKey(comma, key);
                JSString rawJson;
                if (ReferenceEquals(rawJsonObj.Map, _isolate.NativeContext.JSRawJsonMap))
                {
                    // Fast path: the object returned by JSON.rawJSON has its initial map
                    // intact.
                    rawJson = (JSString)rawJsonObj.RawFields[JSRawJson.kRawJsonInitialIndex].Object;
                }
                else
                {
                    // Slow path: perform a property get for "rawJSON". Because raw JSON
                    // objects are created frozen, it is still guaranteed that there will
                    // be a property named "rawJSON" that is a String.
                    rawJson = (JSString)ObjectOps.GetProperty(_isolate, rawJsonObj, ReadOnlyRoots.raw_json_string).Object;
                }
                AppendString(rawJson);
                return Result.SUCCESS;
            }
            case JSReceiver r:
                if (ObjectOps.IsCallable(obj)) return Result.UNCHANGED;
                if (deferredStringKey) SerializeDeferredKey(comma, key);
                if (r is JSProxy proxy) return SerializeJSProxy(proxy, key);
                return SerializeJSObject((JSObject)r, key);
            default:
                throw new InvalidOperationException("JSON.stringify: unexpected internal object " + heapObject);
        }
    }

    Result SerializeJSPrimitiveWrapper(JSPrimitiveWrapper obj, JSValue key)
    {
        JSValue raw = obj.Value;
        if (raw.IsString)
        {
            JSString value = ObjectOps.ToString(_isolate, obj);
            SerializeString(value, rawJson: false);
        }
        else if (raw.IsNumber)
        {
            JSValue value = ObjectOps.ToNumber(_isolate, obj);
            if (value.IsSmi) return SerializeSmi((int)value.Number);
            SerializeDouble(value.Number);
        }
        else if (raw.IsBigInt)
        {
            _isolate.ThrowTypeError(MessageTemplate.BigIntSerializeJSON);
            return Result.EXCEPTION;
        }
        else if (raw.IsBoolean)
        {
            AppendCStringLiteral(raw.IsTrue ? "true" : "false");
        }
        else
        {
            // ES6 24.3.2.1 step 10.c, serialize as an ordinary JSObject.
            return SerializeJSObject(obj, key);
        }
        return Result.SUCCESS;
    }

    Result SerializeSmi(int value)
    {
        Span<char> buffer = stackalloc char[11];
        AppendChars(Conversions.IntToStringView(value, buffer));
        return Result.SUCCESS;
    }

    Result SerializeDouble(double number)
    {
        if (double.IsInfinity(number) || double.IsNaN(number))
        {
            AppendCStringLiteral("null");
            return Result.SUCCESS;
        }
        Span<char> buffer = stackalloc char[100];
        AppendChars(Conversions.DoubleToStringView(number, buffer));
        return Result.SUCCESS;
    }

    static bool CanFastSerializeJSArray(Isolate isolate, JSArray obj)
    {
        // If the no elements protector is intact, Array.prototype and
        // Object.prototype are guaranteed to not have elements in any native context.
        if (!Protectors.IsNoElementsIntact(isolate)) return false;
        HeapObject? proto = obj.Map.Prototype;
        return proto is JSObject p && ReferenceEquals(p, isolate.NativeContext.InitialArrayPrototype);
    }

    static bool CanFastSerializeJSObject(JSObject obj)
    {
        if (Map.IsCustomElementsReceiverMap(obj.Map)) return false;
        if (!obj.HasFastProperties) return false;
        FixedArrayBase elements = obj.Elements;
        // empty_fixed_array or empty_slow_element_dictionary: a typed array's
        // empty byte array does not mean it has no elements.
        return ReferenceEquals(elements, FixedArray.Empty) ||
               ReferenceEquals(elements, ReadOnlyRoots.empty_slow_element_dictionary);
    }

    Result SerializeJSArray(JSArray obj, JSValue key)
    {
        if (!ObjectOps.ToArrayLength(obj.Length, out uint length)) throw new InvalidOperationException("array length");
        if (length == 0)
        {
            AppendCStringLiteral("[]");
            return Result.SUCCESS;
        }

        Result stackPush = StackPush(obj, key);
        if (stackPush != Result.SUCCESS) return stackPush;

        AppendCharacter('[');
        Indent();
        uint slowPathIndex = 0;
        Result result = Result.UNCHANGED;
        if (_replacerFunction is null)
        {
            switch (obj.GetElementsKind())
            {
                case ElementsKind.PACKED_SMI_ELEMENTS:
                case ElementsKind.HOLEY_SMI_ELEMENTS:
                case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                    result = SerializeFixedArrayWithInterruptCheck(obj, obj.GetElementsKind(), length, ref slowPathIndex);
                    break;
                case ElementsKind.PACKED_ELEMENTS:
                case ElementsKind.HOLEY_ELEMENTS:
                    result = SerializeFixedArrayWithPossibleTransitions(obj, obj.GetElementsKind(), length, ref slowPathIndex);
                    break;
            }
        }
        if (result == Result.UNCHANGED)
        {
            // Slow path for non-fast elements and fall-back in edge cases.
            result = SerializeArrayLikeSlow(obj, slowPathIndex, length);
        }
        if (result != Result.SUCCESS) return result;
        Unindent();
        NewLine();
        AppendCharacter(']');
        StackPop();
        return Result.SUCCESS;
    }

    Result SerializeFixedArrayWithInterruptCheck(JSArray array, ElementsKind kind, uint length, ref uint slowPathIndex)
    {
        const uint kInterruptLength = 4000;
        uint limit = Math.Min(length, kInterruptLength);
        bool isHoley = ElementsKinds.IsHoleyElementsKind(kind);
        bool bailoutOnHole = !isHoley || !CanFastSerializeJSArray(_isolate, array);
        bool isDouble = ElementsKinds.IsDoubleElementsKind(kind);

        uint i = 0;
        while (true)
        {
            for (; i < limit; i++)
            {
                FixedArrayBase elements = array.Elements;
                bool hole = isDouble ? ((FixedDoubleArray)elements).IsTheHole((int)i) : ((FixedArray)elements).IsTheHole((int)i);
                if (isHoley && hole)
                {
                    if (bailoutOnHole)
                    {
                        slowPathIndex = i;
                        return Result.UNCHANGED;
                    }
                    Separator(i == 0);
                    AppendCStringLiteral("null");
                    continue;
                }
                Separator(i == 0);
                if (isDouble)
                {
                    SerializeDouble(((FixedDoubleArray)elements).GetScalar((int)i));
                }
                else
                {
                    SerializeSmi((int)((FixedArray)elements).Get((int)i).Number);
                }
            }
            if (i >= length) return Result.SUCCESS;
            limit = Math.Min(length, limit + kInterruptLength);
            StackGuard stackGuard = _isolate.StackGuard;
            if (stackGuard.HasPendingInterrupts) stackGuard.HandleInterrupts();
        }
    }

    Result SerializeFixedArrayWithPossibleTransitions(JSArray array, ElementsKind kind, uint length, ref uint slowPathIndex)
    {
        JSValue oldLength = array.Length;
        bool isHoley = ElementsKinds.IsHoleyElementsKind(kind);
        bool shouldCheckTreatHoleAsUndefined = true;
        for (uint i = 0; i < length; i++)
        {
            if (!array.Length.IsIdenticalTo(oldLength) || kind != array.GetElementsKind())
            {
                // Array was modified during SerializeElement.
                slowPathIndex = i;
                return Result.UNCHANGED;
            }
            JSValue currentElement = ((FixedArray)array.Elements).Get((int)i);
            if (isHoley && currentElement.IsTheHole)
            {
                if (shouldCheckTreatHoleAsUndefined)
                {
                    if (!CanFastSerializeJSArray(_isolate, array))
                    {
                        slowPathIndex = i;
                        return Result.UNCHANGED;
                    }
                    shouldCheckTreatHoleAsUndefined = false;
                }
                Separator(i == 0);
                AppendCStringLiteral("null");
            }
            else
            {
                Separator(i == 0);
                Result result = SerializeElement(currentElement, (int)i);
                if (result == Result.UNCHANGED)
                {
                    AppendCStringLiteral("null");
                }
                else if (result != Result.SUCCESS)
                {
                    return result;
                }
                if (isHoley) shouldCheckTreatHoleAsUndefined = true;
            }
        }
        return Result.SUCCESS;
    }

    Result SerializeArrayLikeSlow(JSReceiver obj, uint start, uint length)
    {
        // We need to write out at least two characters per array element.
        const uint kMaxSerializableArrayLength = JSString.kMaxLength / 2;
        if (length > kMaxSerializableArrayLength)
        {
            _isolate.Throw(_isolate.Factory.NewInvalidStringLengthError());
            return Result.EXCEPTION;
        }
        for (uint i = start; i < length; i++)
        {
            Separator(i == 0);
            JSValue element = ObjectOps.GetElement(_isolate, obj, i);
            Result result = SerializeElement(element, (int)i);
            if (result == Result.SUCCESS) continue;
            if (result == Result.UNCHANGED)
            {
                // Detect overflow sooner for large sparse arrays.
                if (_overflowed)
                {
                    _isolate.Throw(_isolate.Factory.NewInvalidStringLengthError());
                    return Result.EXCEPTION;
                }
                AppendCStringLiteral("null");
            }
            else
            {
                return result;
            }
        }
        return Result.SUCCESS;
    }

    Result SerializeJSObject(JSObject obj, JSValue key)
    {
        if (_propertyList is not null || !CanFastSerializeJSObject(obj))
        {
            Result push = StackPush(obj, key);
            if (push != Result.SUCCESS) return push;
            Result slow = SerializeJSReceiverSlow(obj);
            if (slow != Result.SUCCESS) return slow;
            StackPop();
            return Result.SUCCESS;
        }

        Map map = obj.Map;
        if (map.NumberOfOwnDescriptors == 0)
        {
            AppendCStringLiteral("{}");
            return Result.SUCCESS;
        }

        Result stackPush = StackPush(obj, key);
        if (stackPush != Result.SUCCESS) return stackPush;
        AppendCharacter('{');
        Indent();
        bool comma = false;
        int count = map.NumberOfOwnDescriptors;
        for (int i = 0; i < count; i++)
        {
            var index = new InternalIndex(i);
            DescriptorArray descriptors = map.InstanceDescriptors;
            Name name = descriptors.GetKey(index);
            // TODO(rossberg): Should this throw?
            if (name is not JSString keyName) continue;
            PropertyDetails details = descriptors.GetDetails(index);
            if (!details.IsEnumerable) continue;
            JSValue property;
            if (details.Location == PropertyLocation.Field && ReferenceEquals(map, obj.Map))
            {
                Debug.Assert(details.Kind == PropertyKind.Data);
                FieldIndex fieldIndex = FieldIndex.ForDetails(map, details);
                property = JSObject.FastPropertyAt(_isolate, obj, details.Representation, fieldIndex);
            }
            else
            {
                property = ObjectOps.GetPropertyOrElement(_isolate, obj, keyName);
            }

            Result result = SerializeProperty(property, comma, keyName);
            if (!comma && result == Result.SUCCESS) comma = true;
            if (result == Result.EXCEPTION) return result;
        }
        Unindent();
        if (comma) NewLine();
        AppendCharacter('}');
        StackPop();
        return Result.SUCCESS;
    }

    Result SerializeJSReceiverSlow(JSReceiver obj)
    {
        AppendCharacter('{');
        Indent();
        bool comma = false;
        if (_propertyList is not null)
        {
            foreach (JSString key in _propertyList)
            {
                JSValue property = ObjectOps.GetPropertyOrElement(_isolate, obj, key);
                Result result = SerializeProperty(property, comma, key);
                if (!comma && result == Result.SUCCESS) comma = true;
                if (result == Result.EXCEPTION) return result;
            }
        }
        else
        {
            FixedArray contents = KeyAccumulator.GetKeys(_isolate, obj, KeyCollectionMode.OwnOnly,
                PropertyFilter.ENUMERABLE_STRINGS, GetKeysConversion.ConvertToString);
            for (int i = 0; i < contents.Length; i++)
            {
                var key = (JSString)contents.Get(i).Object;
                JSValue property = ObjectOps.GetPropertyOrElement(_isolate, obj, _isolate.Factory.InternalizeString(key));
                Result result = SerializeProperty(property, comma, key);
                if (!comma && result == Result.SUCCESS) comma = true;
                if (result == Result.EXCEPTION) return result;
            }
        }
        Unindent();
        if (comma) NewLine();
        AppendCharacter('}');
        return Result.SUCCESS;
    }

    Result SerializeJSProxy(JSProxy obj, JSValue key)
    {
        Result stackPush = StackPush(obj, key);
        if (stackPush != Result.SUCCESS) return stackPush;
        if (ObjectOps.IsArray(_isolate, obj))
        {
            JSValue lengthObject = ObjectOps.GetLengthFromArrayLike(_isolate, obj);
            if (!ObjectOps.ToUint32(lengthObject, out uint length))
            {
                // Technically, we need to be able to handle lengths outside the
                // uint32_t range. However, we would run into string size overflow
                // if we tried to stringify such an array.
                _isolate.Throw(_isolate.Factory.NewInvalidStringLengthError());
                return Result.EXCEPTION;
            }
            AppendCharacter('[');
            Indent();
            Result result = SerializeArrayLikeSlow(obj, 0, length);
            if (result != Result.SUCCESS) return result;
            Unindent();
            if (length > 0) NewLine();
            AppendCharacter(']');
        }
        else
        {
            Result result = SerializeJSReceiverSlow(obj);
            if (result != Result.SUCCESS) return result;
        }
        StackPop();
        return Result.SUCCESS;
    }

    // ---------------------------------------------------------------------
    // Strings.

    /// <summary>
    /// JsonEscapeTable: the escape of a character below 0x60 that needs one
    /// (controls, '"' and '\').
    /// </summary>
    static readonly string[] s_jsonEscapeTable = BuildEscapeTable();

    static string[] BuildEscapeTable()
    {
        var table = new string[0x60];
        for (int c = 0; c < 0x20; c++) table[c] = "\\u00" + ((c >> 4) & 0xF).ToString("x") + (c & 0xF).ToString("x");
        table['\b'] = "\\b";
        table['\t'] = "\\t";
        table['\n'] = "\\n";
        table['\f'] = "\\f";
        table['\r'] = "\\r";
        table['"'] = "\\\"";
        table['\\'] = "\\\\";
        return table;
    }

    /// <summary>
    /// SerializeString: the string, quoted and escaped (raw_json: copied as
    /// is). Returns whether any escape sequences were used.
    /// </summary>
    bool SerializeString(JSString @object, bool rawJson)
    {
        ReadOnlySpan<char> src = @object.FlatSpan();
        if (rawJson)
        {
            AppendChars(src);
            return false;
        }
        AppendCharacter('"');
        bool requiredEscaping = false;
        while (true)
        {
            // Two vectorized scans (ASCII escapes, then surrogates before the
            // first ASCII escape) are faster than one over a set that mixes both.
            int found = src.IndexOfAny(s_asciiNeedsEscape);
            int surrogate = (found < 0 ? src : src[..found]).IndexOfAnyInRange('\uD800', '\uDFFF');
            if (surrogate >= 0) found = surrogate;
            if (found < 0)
            {
                AppendChars(src);
                break;
            }
            AppendChars(src[..found]);
            char c = src[found];
            requiredEscaping = true;
            if (c >= 0xD800 && c <= 0xDFFF)
            {
                // The current character is a surrogate.
                if (c <= 0xDBFF && found + 1 < src.Length && src[found + 1] >= 0xDC00 && src[found + 1] <= 0xDFFF)
                {
                    // The next character is a trailing surrogate, meaning this is a
                    // surrogate pair.
                    AppendCharacter(c);
                    AppendCharacter(src[found + 1]);
                    src = src[(found + 2)..];
                    continue;
                }
                // A lone leading or trailing surrogate.
                AppendCStringLiteral("\\u");
                // A surrogate is always four lowercase hex digits (V8 uses
                // DoubleToRadixCString(c, 16) here, which gives the same text).
                const string HexDigits = "0123456789abcdef";
                AppendCharacter(HexDigits[c >> 12]);
                AppendCharacter(HexDigits[(c >> 8) & 0xF]);
                AppendCharacter(HexDigits[(c >> 4) & 0xF]);
                AppendCharacter(HexDigits[c & 0xF]);
            }
            else
            {
                Debug.Assert(c < 0x60);
                AppendCStringLiteral(s_jsonEscapeTable[c]);
            }
            src = src[(found + 1)..];
        }
        AppendCharacter('"');
        return requiredEscaping;
    }

    /// <summary>AppendString for a raw JSON text (JSON.rawJSON).</summary>
    void AppendString(JSString s) => SerializeString(s, rawJson: true);

    void NewLine()
    {
        if (_gap is null) return;
        NewLineOutline();
    }

    void NewLineOutline()
    {
        AppendCharacter('\n');
        for (int i = 0; i < _indent; i++) AppendCStringLiteral(_gap!);
    }

    void Separator(bool first)
    {
        if (!first) AppendCharacter(',');
        NewLine();
    }

    void Indent() => _indent++;

    void Unindent() => _indent--;

    void SerializeDeferredKey(bool deferredComma, JSValue deferredKey)
    {
        Separator(!deferredComma);
        SerializeString((JSString)deferredKey.Object, rawJson: false);
        AppendCharacter(':');
        if (_gap is not null) AppendCharacter(' ');
    }

    // ---------------------------------------------------------------------
    // The output buffer (V8: a one-byte or two-byte part that grows by
    // kPartLengthGrowthFactor and switches encoding when needed).

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AppendCharacter(char c)
    {
        if (_currentIndex == _buffer.Length) Extend(1);
        _buffer[_currentIndex++] = c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AppendCStringLiteral(string literal) => AppendChars(literal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AppendChars(ReadOnlySpan<char> chars)
    {
        if (_buffer.Length - _currentIndex < chars.Length) Extend(chars.Length);
        chars.CopyTo(_buffer.AsSpan(_currentIndex));
        _currentIndex += chars.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Extend(int needed)
    {
        long required = (long)_currentIndex + needed;
        if (required > JSString.kMaxLength)
        {
            // Set the flag and carry on. Delay throwing the exception till the end.
            _currentIndex = 0;
            _overflowed = true;
            if (needed <= _buffer.Length) return;
            required = needed;
        }
        long newLength = Math.Max(_buffer.Length * 2L, required);
        newLength = Math.Min(newLength, Math.Max(required, JSString.kMaxLength + 1L));
        char[] newBuffer = ArrayPool<char>.Shared.Rent((int)newLength);
        _buffer.AsSpan(0, _currentIndex).CopyTo(newBuffer);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
