// Port of the object-allocating parts of src/heap/factory.{h,cc} and
// factory-base.{h,cc}. There is no heap to allocate in (architecture.md
// section 2): each New* constructs the C# object and initializes it the way
// V8's factory initializes the fresh heap object.
using System.Globalization;
using V8Sharp.Base.Numbers;
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's Factory: creates heap objects in their initial state.</summary>
public sealed partial class Factory(Isolate isolate)
{
    readonly Isolate _isolate = isolate;

    public Isolate Isolate => _isolate;

    /// <summary>ReadOnlyRoots::empty_script (the script of messages without a location).</summary>
    public static readonly Script EmptyScript = new() { Id = 0, Source = ReadOnlyRoots.empty_string };

    // --- numbers -----------------------------------------------------------------

    public JSValue NewNumber(double value) => JSValue.FromNumber(value);
    public JSValue NewNumberFromInt(int value) => JSValue.FromInt(value);
    public JSValue NewNumberFromUint(uint value) => JSValue.FromNumber(value);
    public JSValue NewNumberFromSize(ulong value) => JSValue.FromNumber(value);

    /// <summary>Factory::NumberToString (without V8's number string cache).</summary>
    public JSString NumberToString(JSValue number)
    {
        double d = number.Number;
        if (number.IsSmi) return SmiToString((int)d);
        return NewStringFromAsciiChecked(Conversions.DoubleToCString(d));
    }

    public JSString NumberToString(double d) => NumberToString(JSValue.FromNumber(d));

    /// <summary>Factory::SmiToString: sets the array-index hash like V8 does.</summary>
    public JSString SmiToString(int value)
    {
        if ((uint)value < 10) return ReadOnlyRoots.SingleCharacterStringTable['0' + value];
        var result = new SeqString(value.ToString(CultureInfo.InvariantCulture));
        if (value >= 0)
        {
            result.RawHashField = StringHasher.MakeArrayIndexHash((uint)value, (uint)result.Length);
        }
        return result;
    }

    /// <summary>Factory::SizeToString.</summary>
    public JSString SizeToString(ulong value)
    {
        if (value <= (ulong)JSValue.SmiMaxValue) return SmiToString((int)value);
        var result = new SeqString(value.ToString(CultureInfo.InvariantCulture));
        if (value <= JSArray.kMaxArrayIndex)
        {
            result.RawHashField = StringHasher.MakeArrayIndexHash((uint)value, (uint)result.Length);
        }
        return result;
    }

    // --- strings -------------------------------------------------------------------

    public JSString EmptyString => ReadOnlyRoots.empty_string;

    /// <summary>Factory::NewStringFromUtf16 (and NewStringFromTwoByte).</summary>
    public JSString NewStringFromUtf16(string value)
    {
        if (value.Length == 0) return ReadOnlyRoots.empty_string;
        if (value.Length == 1) return LookupSingleCharacterStringFromCode(value[0]);
        if (value.Length > JSString.kMaxLength) _isolate.Throw(NewInvalidStringLengthError());
        return new SeqString(value);
    }

    public JSString NewStringFromUtf16(ReadOnlySpan<char> value) =>
        value.Length <= 1 ? NewStringFromUtf16(value.ToString()) : NewStringFromUtf16(new string(value));

    /// <summary>Factory::NewStringFromAsciiChecked.</summary>
    public JSString NewStringFromAsciiChecked(string value) => NewStringFromUtf16(value);

    /// <summary>Factory::LookupSingleCharacterStringFromCode.</summary>
    public JSString LookupSingleCharacterStringFromCode(int code)
    {
        if ((uint)code <= 0xFF) return ReadOnlyRoots.SingleCharacterStringTable[code];
        return InternalizeString(((char)code).ToString());
    }

    /// <summary>Factory::InternalizeString.</summary>
    public JSString InternalizeString(JSString s) => _isolate.StringTable.LookupString(_isolate, s);

    public JSString InternalizeString(string s) => _isolate.StringTable.LookupString(s);

    public JSString InternalizeString(ReadOnlySpan<char> s) => _isolate.StringTable.LookupString(s);

    /// <summary>Factory::InternalizeName: internalizes strings, returns symbols unchanged.</summary>
    public Name InternalizeName(Name name) => name is JSString s ? InternalizeString(s) : name;

    /// <summary>
    /// Factory::NewConsString: short results are flattened; results longer
    /// than String::kMaxLength throw "Invalid string length".
    /// </summary>
    public JSString NewConsString(JSString left, JSString right)
    {
        int leftLength = left.Length;
        if (leftLength == 0) return right;
        int rightLength = right.Length;
        if (rightLength == 0) return left;

        int length = leftLength + rightLength;

        if (length == 2)
        {
            char c1 = left.Get(0);
            char c2 = right.Get(0);
            return MakeOrFindTwoCharacterString(c1, c2);
        }

        // Make sure that an out of memory exception is thrown if the length
        // of the new cons string is too large.
        if (length > JSString.kMaxLength || length < 0)
        {
            return (JSString)_isolate.Throw(NewInvalidStringLengthError()).Object;
        }

        // If the resulting string is small make a flat string.
        if (length < ConsString.kMinLength)
        {
            return new SeqString(string.Concat(left.FlatSpan(), right.FlatSpan()));
        }

        return new ConsString(left, right);
    }

    JSString MakeOrFindTwoCharacterString(char c1, char c2)
    {
        // V8 looks the pair up in the string table first (without inserting).
        Span<char> pair = [c1, c2];
        return _isolate.StringTable.TryLookupExisting(pair) ?? new SeqString(new string(pair));
    }

    /// <summary>Factory::NewSubString.</summary>
    public JSString NewSubString(JSString str, int begin, int end)
    {
        if (begin == 0 && end == str.Length) return str;
        return NewProperSubString(str, begin, end);
    }

    /// <summary>Factory::NewProperSubString: single chars from the table, short ones copied, long ones sliced.</summary>
    public JSString NewProperSubString(JSString str, int begin, int end)
    {
        str = JSString.Flatten(_isolate, str);

        int length = end - begin;
        if (length <= 0) return ReadOnlyRoots.empty_string;
        if (length == 1) return LookupSingleCharacterStringFromCode(str.Get(begin));
        if (length == 2)
        {
            // Optimization for 2-byte strings often used as keys in a decompression
            // dictionary.  Check whether we already have the string in the string
            // table to prevent creation of many unnecessary strings.
            return MakeOrFindTwoCharacterString(str.Get(begin), str.Get(begin + 1));
        }

        if (length < SlicedString.kMinLength)
        {
            return new SeqString(new string(str.FlatSpan().Slice(begin, length)));
        }

        // Slices of slices point at the parent's parent.
        JSString parent = str;
        int offset = begin;
        if (str is SlicedString sliced)
        {
            parent = sliced.Parent;
            offset += sliced.Offset;
        }
        return new SlicedString(parent, offset, length);
    }

    // --- symbols -------------------------------------------------------------------

    /// <summary>Factory::NewSymbol.</summary>
    public Symbol NewSymbol() => new(JSValue.Undefined);

    public Symbol NewSymbol(JSValue description) => new(description);

    /// <summary>Factory::NewPrivateSymbol.</summary>
    public Symbol NewPrivateSymbol() => new(JSValue.Undefined) { PrivateSymbolKind = PrivateSymbolKind.Internal };

    /// <summary>Factory::NewPrivateNameSymbol (#name).</summary>
    public Symbol NewPrivateNameSymbol(JSString name) =>
        new(name) { PrivateSymbolKind = PrivateSymbolKind.FieldName };

    // --- errors --------------------------------------------------------------------

    /// <summary>Factory::NewError(constructor, template, args).</summary>
    public JSObject NewError(JSFunction constructor, MessageTemplate template, ReadOnlySpan<JSValue> args) =>
        ErrorUtils.MakeGenericError(_isolate, constructor, template, args, FrameSkipMode.SKIP_NONE);

    /// <summary>Factory::NewError(constructor, message).</summary>
    public JSObject NewError(JSFunction constructor, JSString message) =>
        ErrorUtils.Construct(_isolate, constructor, constructor, message, JSValue.Undefined,
            FrameSkipMode.SKIP_NONE, JSValue.Undefined, ErrorUtils.StackTraceCollection.Enabled);

    public JSObject NewError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.ErrorFunction, template, args);
    public JSObject NewEvalError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.EvalErrorFunction, template, args);
    public JSObject NewRangeError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.RangeErrorFunction, template, args);
    public JSObject NewReferenceError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.ReferenceErrorFunction, template, args);
    public JSObject NewSyntaxError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.SyntaxErrorFunction, template, args);
    public JSObject NewSuppressedError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.SuppressedErrorFunction, template, args);
    public JSObject NewTypeError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        NewError(_isolate.NativeContext.TypeErrorFunction, template, args);

    /// <summary>Factory::NewInvalidStringLengthError.</summary>
    public JSObject NewInvalidStringLengthError()
    {
        // Invalidate the "string length" protector.
        if (Protectors.IsStringLengthOverflowLookupChainIntact(_isolate))
        {
            Protectors.InvalidateStringLengthOverflowLookupChain(_isolate);
        }
        return NewRangeError(MessageTemplate.InvalidStringLength);
    }

    // --- fixed arrays and dictionaries ---------------------------------------------

    public FixedArray NewFixedArray(int length) => length == 0 ? FixedArray.Empty : new FixedArray(length);
    public FixedArray NewFixedArrayWithHoles(int length) => length == 0 ? FixedArray.Empty : FixedArray.NewWithHoles(length);
    public FixedDoubleArray NewFixedDoubleArray(int length) => new(length);
    public FixedDoubleArray NewFixedDoubleArrayWithHoles(int length) => FixedDoubleArray.NewWithHoles(length);

    public FixedArray NewFixedArrayFrom(ReadOnlySpan<JSValue> values) =>
        values.Length == 0 ? FixedArray.Empty : new FixedArray(values.ToArray());

    // --- maps ------------------------------------------------------------------------

    /// <summary>Factory::NewMap / NewContextlessMap.</summary>
    public Map NewMap(InstanceType type, int instanceSize, ElementsKind elementsKind = ElementsKind.TERMINAL_FAST_ELEMENTS_KIND,
        int inobjectProperties = 0) =>
        new(type, instanceSize, elementsKind, inobjectProperties);

    /// <summary>Factory::NewContextfulMap: a map owned by a native context.</summary>
    public Map NewContextfulMap(NativeContext nativeContext, InstanceType type, int instanceSize,
        ElementsKind elementsKind = ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, int inobjectProperties = 0)
    {
        Map map = NewMap(type, instanceSize, elementsKind, inobjectProperties);
        map.NativeContext = nativeContext;
        return map;
    }

    /// <summary>Factory::NewContextfulMapForCurrentContext.</summary>
    public Map NewContextfulMapForCurrentContext(InstanceType type, int instanceSize,
        ElementsKind elementsKind = ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, int inobjectProperties = 0) =>
        NewContextfulMap(_isolate.NativeContext, type, instanceSize, elementsKind, inobjectProperties);

    // --- JS objects ------------------------------------------------------------------

    /// <summary>Factory::NewJSObject: an instance of <paramref name="constructor"/>'s initial map.</summary>
    public JSObject NewJSObject(JSFunction constructor)
    {
        JSFunction.EnsureHasInitialMap(_isolate, constructor);
        return NewJSObjectFromMap(constructor.InitialMap);
    }

    /// <summary>Factory::NewJSObjectFromMap.</summary>
    public JSObject NewJSObjectFromMap(Map map, AllocationSite? allocationSite = null)
    {
        JSObject obj = JSObject.AllocateForMap(map);
        if (map.IsInobjectSlackTrackingInProgress())
        {
            map.FindRootMap().InobjectSlackTrackingStep(_isolate);
        }
        return obj;
    }

    /// <summary>Factory::NewSlowJSObjectFromMap: a dictionary-mode object.</summary>
    public JSObject NewSlowJSObjectFromMap(Map map, int capacity = NameDictionary.kInitialCapacity)
    {
        JSObject obj = NewJSObjectFromMap(map);
        obj.SetProperties(NameDictionary.New(capacity));
        return obj;
    }

    /// <summary>Factory::NewJSObjectWithNullProto.</summary>
    public JSObject NewJSObjectWithNullProto()
    {
        Map map = _isolate.NativeContext.ObjectFunction.InitialMap;
        Map mapWithNullProto = Map.TransitionRootMapToPrototypeForNewObject(_isolate, map, null);
        return NewJSObjectFromMap(mapWithNullProto);
    }

    /// <summary>Factory::NewSlowJSObjectWithNullProto.</summary>
    public JSObject NewSlowJSObjectWithNullProto() =>
        NewSlowJSObjectFromMap(_isolate.NativeContext.SlowObjectWithNullPrototypeMap);

    /// <summary>
    /// Factory::ObjectLiteralMapFromCache. V8 keeps the cache in a
    /// WeakFixedArray; V8Sharp's map_cache is a FixedArray of strong refs
    /// (maps are not collected while their native context lives).
    /// </summary>
    public Map ObjectLiteralMapFromCache(NativeContext context, int numberOfProperties)
    {
        // Use initial slow object proto map for too many properties.
        if (numberOfProperties >= JSObject.kMapCacheSize)
        {
            return context.SlowObjectWithObjectPrototypeMap;
        }
        if (numberOfProperties < 0) throw new ArgumentOutOfRangeException(nameof(numberOfProperties));

        var cache = (FixedArray)context.MapCache.Object;

        // Check to see whether there is a matching element in the cache.
        if (cache[numberOfProperties].HeapObjectOrNull is Map cached)
        {
            Debug.Assert(!cached.IsDictionaryMap);
            return cached;
        }

        // Create a new map and add it to the cache.
        Map map = Map.Create(_isolate, numberOfProperties);
        Debug.Assert(!map.IsDictionaryMap);
        cache[numberOfProperties] = map;
        return map;
    }

    /// <summary>
    /// Factory::CopyJSObject (CopyJSObjectWithAllocationSite without a site):
    /// a shallow clone with its own elements and property storage. V8 copies
    /// the object's bytes; V8Sharp clones the CLR object.
    /// </summary>
    public JSObject CopyJSObject(JSObject source, AllocationSite? site = null)
    {
        JSObject clone = source.CloneShallow();
        if (clone is JSArray cloneArray)
        {
            // CopyJSObjectWithAllocationSite: the memento goes behind the copy
            // only when a site is passed (never copied from the source).
            cloneArray.AllocationMementoSite = site;
        }

        FixedArrayBase elements = source.Elements;
        // Update elements if necessary.
        if (elements.Length > 0)
        {
            if (elements.IsCowArray)
            {
                clone.Elements = elements;
            }
            else if (elements is FixedDoubleArray doubles)
            {
                var copy = new FixedDoubleArray(doubles.Length);
                doubles.Data.AsSpan().CopyTo(copy.Data);
                clone.Elements = copy;
            }
            else if (elements is FixedArray fixedArray)
            {
                clone.Elements = fixedArray.CopyAndResize(fixedArray.Length, false);
            }
            else if (elements is NumberDictionary numberDictionary)
            {
                clone.Elements = numberDictionary.ShallowCopy();
            }
        }

        // Update properties if necessary.
        if (source.HasFastProperties)
        {
            // Array.Clone is a runtime call, like MemberwiseClone.
            clone._fields = source._fields.Length == 0 ? source._fields : source._fields.AsSpan().ToArray();
        }
        else
        {
            clone.SetProperties(source.PropertyDictionary.ShallowCopy());
        }
        return clone;
    }

    /// <summary>Factory::NewOrderedHashSet.</summary>
    public OrderedHashSet NewOrderedHashSet() => OrderedHashSet.Allocate(OrderedHashTable.kInitialCapacity, _isolate);

    /// <summary>Factory::NewOrderedHashMap.</summary>
    public OrderedHashMap NewOrderedHashMap() => OrderedHashMap.Allocate(OrderedHashTable.kInitialCapacity, _isolate);

    /// <summary>Factory::NewNativeContext.</summary>
    public NativeContext NewNativeContext()
    {
        var context = new NativeContext
        {
            ErrorsThrown = JSValue.Zero,
            ScriptContextTable = new ScriptContextTable(),
        };
        return context;
    }

    /// <summary>
    /// Factory::NewJSGlobalObject: the global object in dictionary mode, with the
    /// accessors of its constructor's initial map moved into property cells.
    /// </summary>
    public JSGlobalObject NewJSGlobalObject(JSFunction constructor)
    {
        Map map = constructor.InitialMap;
        Debug.Assert(map.IsDictionaryMap);

        // Initial size of the backing store to avoid resize of the storage during
        // bootstrapping. The size differs between the JS global object ad the
        // builtins object.
        const int initialSize = 64;

        // Allocate a dictionary object for backing storage.
        int atLeastSpaceFor = map.NumberOfOwnDescriptors * 2 + initialSize;
        GlobalDictionary dictionary = GlobalDictionary.New(atLeastSpaceFor);

        // The global object might be created from an object template with accessors.
        // Fill these accessors into the dictionary.
        DescriptorArray descs = map.InstanceDescriptors;
        int count = map.NumberOfOwnDescriptors;
        for (int i = 0; i < count; i++)
        {
            var index = new InternalIndex(i);
            PropertyDetails details = descs.GetDetails(index);
            // Only accessors are expected.
            Debug.Assert(details.Kind == PropertyKind.Accessor);
            var d = new PropertyDetails(PropertyKind.Accessor, details.Attributes, PropertyCellType.Mutable);
            Name name = descs.GetKey(index);
            JSValue value = descs.GetStrongValue(index);
            PropertyCell cell = NewPropertyCell(name, d, value);
            dictionary = GlobalDictionary.Add(_isolate, dictionary, name, cell, d, out _);
        }

        // Create a new map for the global object.
        Map newMap = Map.CopyDropDescriptors(_isolate, map);
        newMap.MayHaveInterestingProperties = true;
        newMap.IsDictionaryMap = true;

        // Allocate the global object and initialize it with the backing store.
        var global = new JSGlobalObject(newMap) { GlobalDictionary = dictionary };
        Debug.Assert(!global.HasFastProperties);
        return global;
    }

    /// <summary>Factory::NewUninitializedJSGlobalProxy.</summary>
    public JSGlobalProxy NewUninitializedJSGlobalProxy(int size)
    {
        // Create an empty shell of a JSGlobalProxy that needs to be reinitialized
        // via ReinitializeJSGlobalProxy later.
        Map map = NewMap(InstanceType.JSGlobalProxyType, size);
        // Maintain invariant expected from any JSGlobalProxy.
        map.IsAccessCheckNeeded = true;
        map.MayHaveInterestingProperties = true;
        return new JSGlobalProxy(map);
    }

    /// <summary>Factory::ReinitializeJSGlobalProxy.</summary>
    public void ReinitializeJSGlobalProxy(JSGlobalProxy obj, JSFunction constructor)
    {
        Map map = constructor.InitialMap;
        Map oldMap = obj.Map;

        if (oldMap.IsPrototypeMap)
        {
            map = Map.Copy(_isolate, map, "CopyAsPrototypeForJSGlobalProxy");
            map.IsPrototypeMap = true;
        }
        JSObject.NotifyMapChange(oldMap, map, _isolate);

        // Reset the map for the object and reinitialize it from the constructor
        // map; the identity hash is retained across reinitialization.
        obj.Map = map;
        JSObject.InitializeFromMap(obj, map);
    }

    /// <summary>Factory::NewFunctionPrototype: the default .prototype of a function.</summary>
    public JSObject NewFunctionPrototype(JSFunction function)
    {
        // Make sure to use globals from the function's context, since the function
        // can be from a different context.
        NativeContext nativeContext = function.NativeContext;
        Map newMap;
        if (Globals.IsAsyncGeneratorFunction(function.Shared.Kind))
        {
            newMap = nativeContext.AsyncGeneratorObjectPrototypeMap;
        }
        else if (Globals.IsResumableFunction(function.Shared.Kind))
        {
            // Generator and async function prototypes can share maps since they
            // don't have "constructor" properties.
            newMap = nativeContext.GeneratorObjectPrototypeMap;
        }
        else
        {
            // Each function prototype gets a fresh map to avoid unwanted sharing of
            // maps between prototypes of different constructors.
            JSFunction objectFunction = nativeContext.ObjectFunction;
            newMap = objectFunction.InitialMap;
        }

        JSObject prototype = NewJSObjectFromMap(newMap);
        if (!Globals.IsResumableFunction(function.Shared.Kind))
        {
            JSObject.AddProperty(_isolate, prototype, ReadOnlyRoots.constructor_string, function, PropertyAttributes.DONT_ENUM);
        }
        return prototype;
    }

    /// <summary>Factory::NewArgumentsObject.</summary>
    public JSObject NewArgumentsObject(JSFunction callee, int length)
    {
        bool strictModeCallee = callee.Shared.LanguageMode != LanguageMode.Sloppy || !callee.Shared.HasSimpleParameters;
        NativeContext nc = _isolate.NativeContext;
        JSObject result = NewJSObjectFromMap(strictModeCallee ? nc.StrictArgumentsMap : nc.SloppyArgumentsMap);
        ObjectOps.SetProperty(_isolate, result, ReadOnlyRoots.length_string, JSValue.FromInt(length),
            StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        return result;
    }

    /// <summary>Factory::NewJSPrimitiveWrapper via the wrapper constructor of the value's type.</summary>
    public JSPrimitiveWrapper NewJSPrimitiveWrapper(JSFunction constructor, JSValue value)
    {
        var wrapper = (JSPrimitiveWrapper)NewJSObject(constructor);
        wrapper.Value = value;
        return wrapper;
    }

    // --- arrays ---------------------------------------------------------------------

    public enum ArrayStorageAllocationMode
    {
        DONT_INITIALIZE_ARRAY_ELEMENTS,
        INITIALIZE_ARRAY_ELEMENTS_WITH_HOLE,
    }

    /// <summary>Factory::NewJSArray(elements_kind, length, capacity).</summary>
    public JSArray NewJSArray(ElementsKind elementsKind, int length = 0, int capacity = 0,
        ArrayStorageAllocationMode mode = ArrayStorageAllocationMode.DONT_INITIALIZE_ARRAY_ELEMENTS)
    {
        if (capacity == 0) return NewJSArrayWithElements(FixedArray.Empty, elementsKind, length);
        FixedArrayBase elms = NewJSArrayStorage(elementsKind, capacity, mode);
        return NewJSArrayWithUnverifiedElements(elms, elementsKind, length);
    }

    /// <summary>Factory::NewJSArrayWithElements.</summary>
    public JSArray NewJSArrayWithElements(FixedArrayBase elements, ElementsKind elementsKind, int length) =>
        NewJSArrayWithUnverifiedElements(elements, elementsKind, length);

    /// <summary>NewJSArrayWithElements(elements) with PACKED_ELEMENTS and the full length.</summary>
    public JSArray NewJSArrayWithElements(FixedArray elements) =>
        NewJSArrayWithElements(elements, ElementsKind.PACKED_ELEMENTS, elements.Length);

    public JSArray NewJSArrayWithUnverifiedElements(FixedArrayBase elements, ElementsKind elementsKind, int length)
    {
        NativeContext nativeContext = _isolate.NativeContext;
        Map? map = nativeContext.GetInitialJSArrayMap(elementsKind) ?? nativeContext.ArrayFunction.InitialMap;
        return NewJSArrayWithUnverifiedElements(map, elements, length);
    }

    public JSArray NewJSArrayWithUnverifiedElements(Map map, FixedArrayBase elements, int length)
    {
        var array = (JSArray)NewJSObjectFromMap(map);
        array.Elements = elements;
        array.Length = JSValue.FromInt(length);
        return array;
    }

    /// <summary>Factory::NewJSArrayStorage.</summary>
    public FixedArrayBase NewJSArrayStorage(ElementsKind elementsKind, int capacity, ArrayStorageAllocationMode mode)
    {
        if (ElementsKinds.IsDoubleElementsKind(elementsKind))
        {
            return mode == ArrayStorageAllocationMode.DONT_INITIALIZE_ARRAY_ELEMENTS
                ? NewFixedDoubleArray(capacity)
                : NewFixedDoubleArrayWithHoles(capacity);
        }
        return mode == ArrayStorageAllocationMode.DONT_INITIALIZE_ARRAY_ELEMENTS
            ? NewFixedArray(capacity)
            : NewFixedArrayWithHoles(capacity);
    }

    /// <summary>Factory::NewJSArrayStorage(array, length, capacity).</summary>
    public void NewJSArrayStorage(JSArray array, int length, int capacity, ArrayStorageAllocationMode mode)
    {
        if (capacity == 0)
        {
            array.Length = JSValue.Zero;
            array.Elements = FixedArray.Empty;
            return;
        }
        array.Elements = NewJSArrayStorage(array.GetElementsKind(), capacity, mode);
        array.Length = JSValue.FromInt(length);
    }

    // --- functions ------------------------------------------------------------------

    /// <summary>Factory::NewSharedFunctionInfo(name, function_data, builtin, len, adapt, kind).</summary>
    public SharedFunctionInfo NewSharedFunctionInfo(JSString? name, object? functionData, Builtin builtin, int len,
        bool adapt, FunctionKind kind = FunctionKind.NormalFunction)
    {
        var shared = new SharedFunctionInfo
        {
            BuiltinId = Builtin.Illegal,
            FunctionLiteralId = Globals.kInvalidInfoId,
            UniqueId = _isolate.GetNextUniqueSharedFunctionInfoId(),
        };
        if (name is not null) shared.SetName(name);
        if (functionData is not null)
        {
            shared.FunctionData = functionData;
            shared.BuiltinId = Builtin.NoBuiltinId;
        }
        else if (builtin != Builtin.NoBuiltinId)
        {
            shared.BuiltinId = builtin;
        }
        shared.Kind = kind;
        if (adapt) shared.SetInternalFormalParameterCount(len + 1);
        else shared.DontAdaptArguments();
        shared.Length = (ushort)len;
        shared.FunctionMapIndex = Context.FunctionMapIndex(shared.LanguageMode, kind, shared.HasSharedName);
        return shared;
    }

    /// <summary>Factory::NewSharedFunctionInfoForBuiltin.</summary>
    public SharedFunctionInfo NewSharedFunctionInfoForBuiltin(JSString? name, Builtin builtin, int len, bool adapt,
        FunctionKind kind = FunctionKind.NormalFunction) =>
        NewSharedFunctionInfo(name, null, builtin, len, adapt, kind);

    /// <summary>
    /// Factory::JSFunctionBuilder::Build: a closure of <paramref name="shared"/>
    /// in <paramref name="context"/>, with the map the SFI's function map index
    /// selects unless <paramref name="map"/> is given.
    /// </summary>
    public JSFunction NewFunction(SharedFunctionInfo shared, Context context, Map? map = null, FeedbackCell? feedbackCell = null)
    {
        map ??= context.NativeContext.Slots[shared.FunctionMapIndex].As<Map>();
        var function = new JSFunction(map, shared, context)
        {
            RawFeedbackCell = feedbackCell ?? FeedbackCell.ManyClosuresCell,
        };
        return function;
    }

    /// <summary>
    /// Factory::NewFunctionForTesting: a sloppy function with a fresh
    /// SharedFunctionInfo (builtin Illegal) in the current native context.
    /// </summary>
    public JSFunction NewFunctionForTesting(JSString name)
    {
        SharedFunctionInfo info = NewSharedFunctionInfoForBuiltin(name, Builtin.Illegal, 0, false);
        info.LanguageMode = LanguageMode.Sloppy;
        info.FunctionMapIndex = Context.FunctionMapIndex(LanguageMode.Sloppy, FunctionKind.NormalFunction, true);
        return NewFunction(info, _isolate.NativeContext);
    }

    /// <summary>Factory::NewJSBoundFunction.</summary>
    public JSBoundFunction NewJSBoundFunction(JSReceiver targetFunction, JSValue boundThis, ReadOnlySpan<JSValue> boundArgs,
        JSReceiver? prototype)
    {
        if (boundArgs.Length >= Interpreter.InterpreterConstants.kMaxArguments)
        {
            _isolate.ThrowRangeError(MessageTemplate.TooManyArguments);
        }

        NativeContext creationContext = targetFunction.GetCreationContext() ?? _isolate.NativeContext;
        using var _ = _isolate.EnterContext(creationContext);

        // Create the [[BoundArguments]] for the result.
        FixedArray boundArguments = NewFixedArrayFrom(boundArgs);

        // Setup the map for the JSBoundFunction instance.
        Map map = targetFunction.Map.IsConstructor
            ? creationContext.BoundFunctionWithConstructorMap
            : creationContext.BoundFunctionWithoutConstructorMap;
        if (!ReferenceEquals(map.Prototype, prototype))
        {
            map = Map.TransitionRootMapToPrototypeForNewObject(_isolate, map, prototype);
        }
        return new JSBoundFunction(map, targetFunction, boundThis, boundArguments);
    }

    /// <summary>Factory::NewJSProxy (ES6 section 9.5.15 ProxyCreate).</summary>
    public JSProxy NewJSProxy(JSReceiver target, JSReceiver handler, bool revocable = false)
    {
        NativeContext nc = _isolate.NativeContext;
        Map map = target.Map.IsCallable
            ? target.Map.IsConstructor ? nc.ProxyConstructorMap : nc.ProxyCallableMap
            : nc.ProxyMap;
        return new JSProxy(map, target, handler) { IsRevocable = revocable };
    }

    // --- contexts ---------------------------------------------------------------------

    /// <summary>Factory::NewScriptContext.</summary>
    public Context NewScriptContext(NativeContext outer, ScopeInfo scopeInfo)
    {
        // A script scope always has its extension slot (Scope::HasContextExtensionSlot),
        // so the context has at least the extended header even without locals.
        int length = Math.Max(scopeInfo.ContextLength(), scopeInfo.ContextHeaderLength());
        var context = new Context(ContextKind.ScriptContext, length, outer);
        context.ScopeInfo = scopeInfo;
        context.Previous = outer;
        return context;
    }

    /// <summary>Factory::NewModuleContext.</summary>
    public Context NewModuleContext(HeapObject module, NativeContext outer, ScopeInfo scopeInfo)
    {
        var context = new Context(ContextKind.ModuleContext, scopeInfo.ContextLength(), outer);
        context.ScopeInfo = scopeInfo;
        context.Previous = outer;
        context.Extension = module;
        return context;
    }

    /// <summary>Factory::NewFunctionContext (function or eval scope).</summary>
    public Context NewFunctionContext(Context outer, ScopeInfo scopeInfo)
    {
        ContextKind kind = scopeInfo.ScopeType switch
        {
            ScopeType.EVAL_SCOPE => ContextKind.EvalContext,
            ScopeType.FUNCTION_SCOPE => ContextKind.FunctionContext,
            _ => throw new InvalidOperationException("unreachable"),
        };
        var context = new Context(kind, scopeInfo.ContextLength(), outer.NativeContext);
        context.ScopeInfo = scopeInfo;
        context.Previous = outer;
        return context;
    }

    /// <summary>Factory::NewCatchContext.</summary>
    public Context NewCatchContext(Context previous, ScopeInfo scopeInfo, JSValue thrownObject)
    {
        var context = new Context(ContextKind.CatchContext, (int)Context.Field.MIN_CONTEXT_SLOTS + 1, previous.NativeContext);
        context.ScopeInfo = scopeInfo;
        context.Previous = previous;
        context[(int)Context.Field.THROWN_OBJECT_INDEX] = thrownObject;
        return context;
    }

    /// <summary>Factory::NewDebugEvaluateContext.</summary>
    public Context NewDebugEvaluateContext(Context previous, ScopeInfo scopeInfo, JSReceiver? extension, Context? wrapped)
    {
        var context = new Context(ContextKind.DebugEvaluateContext, (int)Context.Field.MIN_CONTEXT_EXTENDED_SLOTS + 1,
            previous.NativeContext);
        context.ScopeInfo = scopeInfo;
        context.Previous = previous;
        context.Extension = JSValue.FromObject(extension);
        if (wrapped is not null) context[(int)Context.Field.WRAPPED_CONTEXT_INDEX] = wrapped;
        return context;
    }

    /// <summary>Factory::NewWithContext.</summary>
    public Context NewWithContext(Context previous, ScopeInfo scopeInfo, JSReceiver extension)
    {
        var context = new Context(ContextKind.WithContext, (int)Context.Field.MIN_CONTEXT_EXTENDED_SLOTS, previous.NativeContext);
        context.ScopeInfo = scopeInfo;
        context.Previous = previous;
        context.Extension = extension;
        return context;
    }

    /// <summary>Factory::NewBlockContext (block or class scope).</summary>
    public Context NewBlockContext(Context previous, ScopeInfo scopeInfo)
    {
        var context = new Context(ContextKind.BlockContext, scopeInfo.ContextLength(), previous.NativeContext);
        context.ScopeInfo = scopeInfo;
        context.Previous = previous;
        return context;
    }

    /// <summary>Factory::NewAwaitContext (the context of await closures).</summary>
    public Context NewAwaitContext(Context previous, ScopeInfo scopeInfo, int length)
    {
        var context = new Context(ContextKind.AwaitContext, length, previous.NativeContext);
        context.ScopeInfo = scopeInfo;
        context.Previous = previous;
        return context;
    }

    /// <summary>Factory::NewBuiltinContext: a function context for builtin closures.</summary>
    public Context NewBuiltinContext(NativeContext nativeContext, int variadicPartLength)
    {
        var context = new Context(ContextKind.FunctionContext, variadicPartLength, nativeContext);
        context.ScopeInfo = ScopeInfo.EmptyScopeInfo;
        context.Previous = nativeContext;
        return context;
    }

    // --- misc ------------------------------------------------------------------------

    /// <summary>Factory::NewPropertyCell.</summary>
    public PropertyCell NewPropertyCell(Name name, PropertyDetails details, JSValue value) => new(name, details, value);

    /// <summary>Factory::NewScript.</summary>
    public Script NewScript(JSValue source, Script.Type type = Script.Type.Normal)
    {
        return new Script
        {
            Source = source,
            Id = _isolate.GetNextScriptId(),
            ScriptType = type,
        };
    }

    /// <summary>Factory::NewAccessorPair.</summary>
    public AccessorPair NewAccessorPair() => new();

    /// <summary>Factory::NewJSMessageObject.</summary>
    public JSMessageObject NewJSMessageObject(MessageTemplate message, JSValue argument, int startPosition, int endPosition,
        SharedFunctionInfo? shared, int bytecodeOffset, Script script, FixedArray? stackTrace) =>
        new()
        {
            Type = message,
            Argument = argument,
            StartPosition = startPosition,
            EndPosition = endPosition,
            SharedInfo = shared,
            BytecodeOffset = bytecodeOffset,
            Script = script,
            StackTrace = stackTrace,
        };
}
