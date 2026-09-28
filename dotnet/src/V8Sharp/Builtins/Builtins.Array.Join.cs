// Port of src/builtins/array-join.tq: Array.prototype.join / toString /
// toLocaleString and %TypedArray%.prototype.join / toLocaleString, with the
// join stack that detects cyclic joins (ARRAY_JOIN_STACK_INDEX) and the
// separator-run-length Buffer.
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArrayJoin()
    {
        Register(Builtin.ArrayPrototypeJoin, BuiltinsArray.ArrayPrototypeJoin);
        Register(Builtin.ArrayPrototypeToString, BuiltinsArray.ArrayPrototypeToString);
        Register(Builtin.ArrayPrototypeToLocaleString, BuiltinsArray.ArrayPrototypeToLocaleString);
    }
}

public static partial class BuiltinsArray
{
    /// <summary>kMaxBufferChunkSize (kMaxNewSpaceFixedArrayElements).</summary>
    const int kMaxBufferChunkSize = (128 * 1024 - 16) / 8;

    enum JoinLoad { Generic, FastSmiOrObject, FastDouble, Typed }

    /// <summary>
    /// array-join.tq Buffer: entries are strings, positive separator counts or
    /// negative repeat counts of the previous string. V8 keeps a linked list of
    /// FixedArray chunks; one growable array is equivalent.
    /// </summary>
    struct JoinBuffer
    {
        object?[] _entries;
        int _count;
        long _totalStringLength;
        JSString? _lastString;

        public JoinBuffer(ulong len)
        {
            int capacity = len >= kMaxBufferChunkSize ? kMaxBufferChunkSize : (int)len + 1;
            _entries = new object?[capacity];
            _count = 0;
            _totalStringLength = 0;
            _lastString = null;
        }

        public readonly long TotalStringLength => _totalStringLength;

        static long AddStringLength(Isolate isolate, long a, long b)
        {
            long length = a + b;
            if (length > JSString.kMaxLength) isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
            return length;
        }

        public void Add(Isolate isolate, JSString str, long nofSeparators, int separatorLength)
        {
            // Add separators if necessary (at the beginning or more than one).
            bool writeSeparators = _count == 0 || nofSeparators > 1;
            AddSeparators(isolate, nofSeparators, separatorLength, writeSeparators);

            _totalStringLength = AddStringLength(isolate, _totalStringLength, str.Length);
            // V8 compares pointers only (internalized strings).
            if (ReferenceEquals(str, _lastString))
            {
                RepeatLast();
            }
            else
            {
                Append(str);
                _lastString = str;
            }
        }

        public void AddSeparators(Isolate isolate, long nofSeparators, int separatorLength, bool write)
        {
            if (nofSeparators == 0 || separatorLength == 0) return;
            long sepsLen = separatorLength * nofSeparators;
            _totalStringLength = AddStringLength(isolate, _totalStringLength, sepsLen);
            if (write)
            {
                Append((int)nofSeparators);
                _lastString = null;
            }
        }

        void RepeatLast()
        {
            if (_entries[_count - 1] is int count && count < 0) _entries[_count - 1] = count - 1;
            else Append(-1);
        }

        void Append(object element)
        {
            if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length + (_entries.Length >> 1) + 16);
            _entries[_count++] = element;
        }

        /// <summary>BufferJoin (and JSArray::ArrayJoinConcatToSequentialString).</summary>
        public readonly JSString Join(Isolate isolate, JSString sep)
        {
            if (_totalStringLength == 0) return ReadOnlyRoots.empty_string;
            // Fast path when there's only one buffer element.
            if (_count == 1)
            {
                if (_entries[0] is JSString s) return s;
            }
            ReadOnlySpan<char> sepChars = sep.FlatSpan();
            char[] result = new char[_totalStringLength];
            int pos = 0;
            ReadOnlySpan<char> last = default;
            // WriteChunkListToFlat: consecutive strings are separated by one
            // implicit separator; a positive count replaces it.
            int numSeparators = 0;
            for (int i = 0; i < _count; i++)
            {
                object? entry = _entries[i];
                int repeatLast = 0;
                if (entry is int n)
                {
                    if (n > 0) numSeparators = n;
                    else repeatLast = -n;
                }
                for (int k = 0; k < numSeparators; k++)
                {
                    sepChars.CopyTo(result.AsSpan(pos));
                    pos += sepChars.Length;
                }
                numSeparators = 0;
                if (repeatLast > 0)
                {
                    // Repeat the last written string (with separators between).
                    for (int k = 0; k < repeatLast; k++)
                    {
                        if (k > 0)
                        {
                            sepChars.CopyTo(result.AsSpan(pos));
                            pos += sepChars.Length;
                        }
                        last.CopyTo(result.AsSpan(pos));
                        pos += last.Length;
                    }
                    numSeparators = 1;
                }
                if (entry is JSString str)
                {
                    last = str.FlatSpan();
                    last.CopyTo(result.AsSpan(pos));
                    pos += last.Length;
                    // Next string element, needs at least one separator preceding it.
                    numSeparators = 1;
                }
            }
            return isolate.Factory.NewStringFromUtf16(new string(result));
        }
    }

    static void ThrowInvalidStringLength(Isolate isolate) => isolate.Throw(isolate.Factory.NewInvalidStringLengthError());

    /// <summary>
    /// ConvertToLocaleString. V8Sharp is the !V8_INTL_SUPPORT build, so, as the
    /// ECMA-262 (not ECMA-402) algorithm says, locales and options are not
    /// passed. (The oracle is built with ICU and passes them.)
    /// </summary>
    static JSString ConvertToLocaleString(Isolate isolate, JSValue element, JSValue locales, JSValue options)
    {
        if (element.IsNullOrUndefined) return ReadOnlyRoots.empty_string;
        JSValue prop = ObjectOps.GetProperty(isolate, element, ArrayBuiltinsUtils.NewString(isolate, "toLocaleString"));
        if (!ObjectOps.IsCallable(prop)) ArrayBuiltinsUtils.ThrowCalledNonCallable(isolate, prop);
        _ = locales;
        _ = options;
        JSValue result = Execution.Call(isolate, prop, element, []);
        return ObjectOps.ToString(isolate, result);
    }

    static JSString ElementToJoinString(Isolate isolate, JSValue element)
    {
        if (element.IsString) return element.As<JSString>();
        if (element.IsNumber) return isolate.Factory.NumberToString(element);
        if (element.IsNullOrUndefined) return ReadOnlyRoots.empty_string;
        return ObjectOps.ToString(isolate, element);
    }

    /// <summary>ArrayJoinImpl&lt;T&gt;.</summary>
    static JSString ArrayJoinImpl(Isolate isolate, JSReceiver receiver, JSString sep, double lengthNumber,
        bool useToLocaleString, JSValue locales, JSValue options, JoinLoad initialLoad)
    {
        Map initialMap = receiver.Map;
        ulong len = (ulong)lengthNumber;
        int separatorLength = sep.Length;
        long nofSeparators = 0;
        JoinLoad load = initialLoad;
        JoinBuffer buffer = new(len);

        ulong k = 0;
        while (k < len)
        {
            if (load != JoinLoad.Generic && CannotUseSameArrayAccessor(isolate, receiver, initialMap, lengthNumber))
            {
                load = JoinLoad.Generic;
            }

            if (k > 0) nofSeparators++;

            JSValue element = LoadJoinElement(isolate, load, receiver, k++);

            JSString next;
            if (useToLocaleString)
            {
                next = ConvertToLocaleString(isolate, element, locales, options);
                if (next.Length == 0) continue;
            }
            else
            {
                next = ElementToJoinString(isolate, element);
                if (next.Length == 0) continue;
            }

            buffer.Add(isolate, next, nofSeparators, separatorLength);
            nofSeparators = 0;
        }

        buffer.AddSeparators(isolate, nofSeparators, separatorLength, true);
        return buffer.Join(isolate, sep);
    }

    static JSValue LoadJoinElement(Isolate isolate, JoinLoad load, JSReceiver receiver, ulong k)
    {
        switch (load)
        {
            case JoinLoad.FastSmiOrObject:
            {
                var fixedArray = (FixedArray)((JSArray)receiver).Elements;
                JSValue element = fixedArray.Data[(int)k];
                return element.IsTheHole ? ReadOnlyRoots.empty_string : element;
            }
            case JoinLoad.FastDouble:
            {
                var fixedDoubleArray = (FixedDoubleArray)((JSArray)receiver).Elements;
                if (fixedDoubleArray.IsTheHole((int)k)) return ReadOnlyRoots.empty_string;
                return JSValue.FromNumber(fixedDoubleArray.GetScalar((int)k));
            }
            case JoinLoad.Typed:
                return TypedArrayElementsOps.Load(isolate, (JSTypedArray)receiver, k);
            default:
                return ArrayBuiltinsUtils.GetProperty(isolate, receiver, (double)k);
        }
    }

    /// <summary>CannotUseSameArrayAccessor&lt;JSArray | JSTypedArray&gt;.</summary>
    static bool CannotUseSameArrayAccessor(Isolate isolate, JSReceiver receiver, Map originalMap, double originalLen)
    {
        if (receiver is JSTypedArray typedArray)
        {
            if (typedArray.Buffer.WasDetached) return true;
            if (typedArray.IsVariableLength) return true;
            return false;
        }
        var array = (JSArray)receiver;
        if (!ReferenceEquals(originalMap, array.Map)) return true;
        if (array.Length.Number != originalLen) return true;
        if (!Protectors.IsNoElementsIntact(isolate)) return true;
        return false;
    }

    /// <summary>ArrayJoin&lt;JSArray&gt;.</summary>
    static JSString ArrayJoinJSArray(Isolate isolate, bool useToLocaleString, JSReceiver receiver, JSString sep,
        double lenNumber, JSValue locales, JSValue options)
    {
        Map map = receiver.Map;
        ElementsKind kind = map.ElementsKind;
        JoinLoad load = JoinLoad.Generic;
        if (receiver is JSArray array && array.Length.Number == lenNumber &&
            IsPrototypeInitialArrayPrototype(isolate, map) && Protectors.IsNoElementsIntact(isolate))
        {
            if (kind <= ElementsKind.HOLEY_ELEMENTS)
            {
                load = JoinLoad.FastSmiOrObject;
            }
            else if (kind <= ElementsKind.HOLEY_DOUBLE_ELEMENTS)
            {
                load = JoinLoad.FastDouble;
            }
            else if (kind <= ElementsKind.LAST_ANY_NONEXTENSIBLE_ELEMENTS_KIND)
            {
                load = JoinLoad.FastSmiOrObject;
            }
            else if (kind == ElementsKind.DICTIONARY_ELEMENTS)
            {
                var dict = (NumberDictionary)array.Elements;
                if (dict.NumberOfElements == 0)
                {
                    if (sep.Length == 0) return ReadOnlyRoots.empty_string;
                    double nofSeparators = lenNumber - 1;
                    if (nofSeparators * sep.Length > JSString.kMaxLength) ThrowInvalidStringLength(isolate);
                    return StringRepeat(isolate, sep, (int)nofSeparators);
                }
                // LoadJoinElement<DictionaryElements> reads the dictionary and falls
                // back to GetProperty; the generic accessor is equivalent here.
            }
        }
        return ArrayJoinImpl(isolate, receiver, sep, lenNumber, useToLocaleString, locales, options, load);
    }

    static JSString StringRepeat(Isolate isolate, JSString s, int count)
    {
        if (count <= 0 || s.Length == 0) return ReadOnlyRoots.empty_string;
        long total = (long)s.Length * count;
        if (total > JSString.kMaxLength) ThrowInvalidStringLength(isolate);
        ReadOnlySpan<char> chars = s.FlatSpan();
        char[] result = new char[total];
        for (int i = 0; i < count; i++) chars.CopyTo(result.AsSpan(i * chars.Length));
        return isolate.Factory.NewStringFromUtf16(new string(result));
    }

    // ---- The join stack -------------------------------------------------------------------------

    /// <summary>JoinStackPushInline / JoinStackPush.</summary>
    static bool JoinStackPush(Isolate isolate, JSReceiver receiver)
    {
        NativeContext context = isolate.NativeContext;
        if (context.ArrayJoinStack.HeapObjectOrNull is not FixedArray stack)
        {
            stack = FixedArray.NewWithHoles((int)JSArray.kMinJoinStackSize);
            stack.Data[0] = receiver;
            context.ArrayJoinStack = stack;
            return true;
        }
        JSValue[] data = stack.Data;
        int capacity = data.Length;
        for (int i = 0; i < capacity; i++)
        {
            JSValue previouslyVisited = data[i];
            // Add `receiver` to the first open slot.
            if (previouslyVisited.IsTheHole)
            {
                data[i] = receiver;
                return true;
            }
            // Detect cycles.
            if (ReferenceEquals(previouslyVisited.HeapObjectOrNull, receiver)) return false;
        }
        // If no open slots were found, grow the stack and add receiver to the end.
        int newLength = capacity + (capacity >> 1) + 16;
        FixedArray newStack = FixedArray.NewWithHoles(newLength);
        data.AsSpan().CopyTo(newStack.Data);
        newStack.Data[capacity] = receiver;
        context.ArrayJoinStack = newStack;
        return true;
    }

    /// <summary>JoinStackPopInline / JoinStackPop.</summary>
    static void JoinStackPop(Isolate isolate, JSReceiver receiver)
    {
        NativeContext context = isolate.NativeContext;
        var stack = context.ArrayJoinStack.As<FixedArray>();
        JSValue[] data = stack.Data;
        int len = data.Length;
        for (int i = 0; i < len; i++)
        {
            if (ReferenceEquals(data[i].HeapObjectOrNull, receiver))
            {
                // Shrink the Join Stack if the stack will be empty and is larger than
                // the minimum size.
                if (i == 0 && len > JSArray.kMinJoinStackSize)
                {
                    context.ArrayJoinStack = FixedArray.NewWithHoles((int)JSArray.kMinJoinStackSize);
                }
                else
                {
                    data[i] = JSValue.TheHole;
                }
                return;
            }
        }
    }

    /// <summary>CycleProtectedArrayJoin&lt;T&gt;.</summary>
    static JSValue CycleProtectedArrayJoin(Isolate isolate, bool typed, bool useToLocaleString, JSReceiver o,
        double len, JSString sep, JSValue locales, JSValue options)
    {
        // Check early if the separators might overflow.
        int separatorLength = sep.Length;
        if (separatorLength >= 1)
        {
            if (len <= int.MaxValue)
            {
                if (len > 1)
                {
                    if ((len - 1) * separatorLength > JSString.kMaxLength) ThrowInvalidStringLength(isolate);
                }
            }
            else
            {
                // A HeapNumber length is > kSmiMaxValue > kStringMaxLength.
                ThrowInvalidStringLength(isolate);
            }
        }
        // If the receiver is not empty and not already being joined, continue with
        // the normal join algorithm.
        if (len > 0 && JoinStackPush(isolate, o))
        {
            try
            {
                return typed
                    ? ArrayJoinImpl(isolate, o, sep, len, useToLocaleString, locales, options, JoinLoad.Typed)
                    : ArrayJoinJSArray(isolate, useToLocaleString, o, sep, len, locales, options);
            }
            finally
            {
                JoinStackPop(isolate, o);
            }
        }
        return ReadOnlyRoots.empty_string;
    }

    static JSString CommaString(Isolate isolate) => isolate.Factory.LookupSingleCharacterStringFromCode(',');

    /// <summary>ES #sec-array.prototype.join.</summary>
    public static JSValue ArrayPrototypeJoin(Isolate isolate, in BuiltinArguments args)
    {
        JSValue sepObj = args.AtOrUndefined(1);
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? ToLength(? Get(O, "length")).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // Only handle valid array lengths. Although the spec allows larger
        // values, this matches historical V8 behavior.
        if (len > JSArray.kMaxArrayLength) return isolate.ThrowTypeError(MessageTemplate.InvalidArrayLength);
        // 3-4.
        JSString separator = sepObj.IsUndefined ? CommaString(isolate) : ObjectOps.ToString(isolate, sepObj);
        if (len == 0) return ReadOnlyRoots.empty_string;
        return ArrayPrototypeJoinImpl(isolate, o, len, separator);
    }

    /// <summary>ArrayPrototypeJoinImpl.</summary>
    internal static JSValue ArrayPrototypeJoinImpl(Isolate isolate, JSReceiver o, double len, JSString separator)
    {
        if (len <= JSString.kMaxLength && IsFastJSArrayForRead(isolate, o, out JSArray fastO))
        {
            ElementsKind kind = fastO.Map.ElementsKind;
            if (ElementsKinds.IsFastElementsKind(kind) &&
                TryFastArrayJoin(isolate, kind, fastO, separator, (int)len, out JSString? result))
            {
                return result!;
            }
        }
        isolate.StackGuard.StackCheck(isolate);
        return CycleProtectedArrayJoin(isolate, false, false, o, len, separator, JSValue.Undefined, JSValue.Undefined);
    }

    /// <summary>FastArrayJoin; returns false on Bailout.</summary>
    static bool TryFastArrayJoin(Isolate isolate, ElementsKind kind, JSArray array, JSString sep, int length,
        out JSString? result)
    {
        result = null;
        ulong len = (ulong)length;
        int separatorLength = sep.Length;
        if (separatorLength > 1 && len > 1)
        {
            if ((long)(length - 1) * separatorLength > JSString.kMaxLength) ThrowInvalidStringLength(isolate);
        }

        FixedArrayBase elements = array.Elements;
        long nofSeparators = 0;
        JoinBuffer buffer = new(len);

        long nofAdditionalEmptyEntries = 0;
        {
            ulong currentLen = (ulong)array.Length.Number;
            if (currentLen < len)
            {
                // If the array was trimmed by a side effectful ToString(separatorObj)
                // operation then we must iterate up to the new length and add respective
                // number of empty enties at the end.
                nofAdditionalEmptyEntries = (long)(len - currentLen);
                len = currentLen;
            }
        }

        bool isDouble = ElementsKinds.IsDoubleElementsKind(kind);
        bool isSmi = ElementsKinds.IsSmiElementsKind(kind);
        ulong k = 0;
        while (k < len)
        {
            if (k > 0) nofSeparators++;

            JSString next;
            if (isDouble)
            {
                var fixedDoubleArray = (FixedDoubleArray)elements;
                int i = (int)k++;
                if (fixedDoubleArray.IsTheHole(i)) continue;
                next = isolate.Factory.NumberToString(fixedDoubleArray.GetScalar(i));
            }
            else
            {
                JSValue element = ((FixedArray)elements).Data[(int)k++];
                if (element.IsTheHole) continue;
                if (isSmi)
                {
                    next = isolate.Factory.NumberToString(element);
                }
                else if (element.IsString)
                {
                    next = element.As<JSString>();
                    if (next.Length == 0) continue;
                }
                else if (element.IsNumber)
                {
                    next = isolate.Factory.NumberToString(element);
                }
                else if (element.IsNullOrUndefined)
                {
                    continue;
                }
                else if (element.IsBoolean)
                {
                    next = ObjectOps.ToString(isolate, element);
                }
                else
                {
                    return false;
                }
            }

            buffer.Add(isolate, next, nofSeparators, separatorLength);
            nofSeparators = 0;
        }
        // Append additional number of empty entries if necessary.
        if (nofAdditionalEmptyEntries > 0)
        {
            if (k > 0) ++nofSeparators;
            nofSeparators += nofAdditionalEmptyEntries - 1;
        }

        buffer.AddSeparators(isolate, nofSeparators, separatorLength, true);
        result = buffer.Join(isolate, sep);
        return true;
    }

    /// <summary>ES #sec-array.prototype.tolocalestring.</summary>
    public static JSValue ArrayPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args)
    {
        JSValue locales = args.AtOrUndefined(1);
        JSValue options = args.AtOrUndefined(2);
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        if (len > JSArray.kMaxArrayLength) return isolate.ThrowTypeError(MessageTemplate.InvalidArrayLength);
        return CycleProtectedArrayJoin(isolate, false, true, o, len, CommaString(isolate), locales, options);
    }

    /// <summary>ES #sec-array.prototype.tostring.</summary>
    public static JSValue ArrayPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let array be ? ToObject(this value).
        JSReceiver array = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let func be ? Get(array, "join").
        JSValue prop = ObjectOps.GetProperty(isolate, array, ArrayBuiltinsUtils.NewString(isolate, "join"));
        // 3. If IsCallable(func) is false, let func be the intrinsic function
        //    %ObjProto_toString%.
        if (!ObjectOps.IsCallable(prop)) prop = isolate.NativeContext.ObjectToString;
        // 4. Return ? Call(func, array).
        return Execution.Call(isolate, prop, array, []);
    }

    /// <summary>ES #sec-%typedarray%.prototype.join.</summary>
    public static JSValue TypedArrayPrototypeJoin(Isolate isolate, in BuiltinArguments args)
    {
        JSValue sepObj = args.AtOrUndefined(1);
        JSTypedArray typedArray = BuiltinsTypedArray.ValidateTypedArrayAndGetLength(isolate, args.Receiver,
            "%TypedArray%.prototype.join", TypedArrayAccessMode.kRead, out ulong length);
        JSString separator = sepObj.IsUndefined ? CommaString(isolate) : ObjectOps.ToString(isolate, sepObj);
        return CycleProtectedArrayJoin(isolate, true, false, typedArray, length, separator, JSValue.Undefined, JSValue.Undefined);
    }

    /// <summary>ES #sec-%typedarray%.prototype.tolocalestring.</summary>
    public static JSValue TypedArrayPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args)
    {
        JSValue locales = args.AtOrUndefined(1);
        JSValue options = args.AtOrUndefined(2);
        JSTypedArray typedArray = BuiltinsTypedArray.ValidateTypedArrayAndGetLength(isolate, args.Receiver,
            "%TypedArray%.prototype.toLocaleString", TypedArrayAccessMode.kRead, out ulong length);
        return CycleProtectedArrayJoin(isolate, true, true, typedArray, length, CommaString(isolate), locales, options);
    }
}
