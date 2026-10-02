// Port of the data layout of src/objects/js-objects.h (+ -inl.h): JSReceiver,
// JSObject, JSGlobalObject, JSGlobalProxy, JSPrimitiveWrapper, and
// src/objects/prototype.h (PrototypeIterator). The operations of
// src/objects/js-objects.cc are in JSReceiver.cs and JSObject.cs.
using System.Runtime.CompilerServices;
using V8Sharp.Common;

namespace V8Sharp.Objects;

/// <summary>
/// V8's JSReceiver: an object with a hidden class, properties (fast fields or
/// a dictionary) and an identity hash.
/// </summary>
public abstract partial class JSReceiver : HeapObject
{
    /// <summary>An empty field array shared by objects without fields.</summary>
    internal static readonly JSValue[] EmptyFields = [];

    /// <summary>The hidden class (V8's map word). Changed only through MigrateToMap and friends.</summary>
    public Map Map;

    /// <summary>
    /// V8's properties_or_hash. In fast mode, the PropertyArray: the
    /// out-of-object fields. Ordinary objects keep their in-object fields in
    /// object slots (JSObjects.InObject.cs); other JSObject subclasses keep them
    /// at the start of this array (architecture.md section 5). Indexed through
    /// FieldIndex.StorageIndex. In dictionary mode, the property dictionary is
    /// the last element (after the in-object area of the classes without
    /// slots), as V8 keeps the PropertyArray or the dictionary in one field: a
    /// separate dictionary field would add 8 bytes to every receiver.
    /// </summary>
    internal JSValue[] _fields = EmptyFields;

    /// <summary>The property dictionary in dictionary mode (NameDictionary, or GlobalDictionary for globals).</summary>
    internal HashTableBase? _dictionary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            JSValue[] f = _fields;
            return f.Length == 0 ? null : f[^1]._obj as HashTableBase;
        }
        set
        {
            // A dictionary-mode receiver replaces the dictionary it has (it grew
            // or shrank); anything else is (re)initialized to the dictionary
            // layout, keeping the in-object area of the classes without slots.
            if (value is null) return;
            JSValue[] f = _fields;
            if (f.Length != 0 && f[^1]._obj is HashTableBase)
            {
                f[^1] = value;
                return;
            }
            InitializeDictionaryStorage(value, 0);
        }
    }

    /// <summary>
    /// Sets the dictionary layout of <see cref="_fields"/>: <paramref name="inobject"/>
    /// in-object field values (Smi zero, as V8 clears freed in-object space) and the dictionary.
    /// </summary>
    internal void InitializeDictionaryStorage(HashTableBase dictionary, int inobject)
    {
        var f = new JSValue[inobject + 1];
        if (inobject > 0) f.AsSpan(0, inobject).Fill(JSValue.Zero);
        f[inobject] = dictionary;
        _fields = f;
    }

    /// <summary>
    /// The identity hash, or kNoHashSentinel (V8 keeps it in properties_or_hash;
    /// V8Sharp in the header word, HeapObject._hashField).
    /// </summary>
    internal int _identityHash
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)_hashField;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _hashField = (uint)value;
    }

    public const int kNoHashSentinel = 0;
    public const int kHashMask = (1 << 21) - 1;  // PropertyArray::HashField::kMax

    protected JSReceiver(Map map) : base(map.InstanceType)
    {
        Map = map;
        if (map.IsDictionaryMap)
        {
            InitializeDictionaryStorage(map.InstanceType == InstanceType.JSGlobalObjectType
                ? GlobalDictionary.New(0)
                : NameDictionary.New(NameDictionary.kInitialCapacity), 0);
        }
    }

    /// <summary>A field-for-field copy of <paramref name="source"/> (see JSObject.CloneShallow).</summary>
    protected JSReceiver(JSReceiver source) : base(source.InstanceType)
    {
        Map = source.Map;
        _fields = source._fields;
        _identityHash = source._identityHash;
        _headerFlags = source._headerFlags;
    }

    /// <summary>Whether properties are stored in fields described by the map (not a dictionary).</summary>
    public bool HasFastProperties
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !Map.IsDictionaryMap;
    }

    /// <summary>The property dictionary of a dictionary-mode (non-global) receiver.</summary>
    public NameDictionary PropertyDictionary
    {
        get
        {
            Debug.Assert(!HasFastProperties);
            return (NameDictionary)_dictionary!;
        }
    }

    /// <summary>JSReceiver::SetProperties for dictionary storage.</summary>
    public void SetProperties(NameDictionary dictionary) => _dictionary = dictionary;

    /// <summary>JSReceiver::GetIdentityHash: the hash, or undefined if none was created.</summary>
    public JSValue GetIdentityHash() =>
        _identityHash == kNoHashSentinel ? JSValue.Undefined : JSValue.FromInt(_identityHash);

    public void SetIdentityHash(int hash)
    {
        Debug.Assert(hash != kNoHashSentinel);
        _identityHash = hash;
    }

    /// <summary>JSReceiver::GetOrCreateIdentityHash.</summary>
    public int GetOrCreateIdentityHash(Isolate? isolate)
    {
        if (_identityHash != kNoHashSentinel) return _identityHash;
        return CreateIdentityHash(isolate, this);
    }

    public static int CreateIdentityHash(Isolate? isolate, JSReceiver key)
    {
        int hash = (int)(IdentityHash.Next() & kHashMask);
        if (hash == kNoHashSentinel) hash = 1;
        key.SetIdentityHash(hash);
        return hash;
    }

    /// <summary>The native context the receiver was created in (from its map).</summary>
    public NativeContext? GetCreationContext() => Map.NativeContext;

    public override string ToString() => $"<{InstanceType}>";
}

/// <summary>V8's JSObject: a JSReceiver with elements.</summary>
public partial class JSObject : JSReceiver
{
    public const uint kMaxElementCount = uint.MaxValue;
    public const uint kMaxElementIndex = kMaxElementCount - 1;
    public const uint kMaxGap = 1024;
    public const int kMaxUncheckedFastElementsLength = 5000;
    public const int kMaxUncheckedOldFastElementsLength = 500;
    public const int kInitialGlobalObjectUnusedPropertiesCount = 4;
    public const int kMaxInstanceSize = 255 * Map.kTaggedSize;
    public const int kMapCacheSize = 128;
    public const int kFieldsAdded = 3;

    /// <summary>JSObject::kHeaderSize: map, properties_or_hash, elements.</summary>
    public const int kHeaderSize = 3 * Map.kTaggedSize;
    public const int kMaxInObjectProperties = (kMaxInstanceSize - kHeaderSize) / Map.kTaggedSize;
    public const uint kMinAddedElementsCapacity = 16;

    /// <summary>The elements backing store (V8's elements field).</summary>
    public FixedArrayBase Elements;

    public JSObject(Map map) : base(map)
    {
        int inobject = map.GetInObjectProperties();
        if (inobject > 0)
        {
            // Ordinary objects with in-object properties are allocated with
            // slots (NewWithInObjectSlots); only the other subclasses get here.
            if (map.HasInObjectSlots) throw new InvalidOperationException("JSObject: map needs in-object slots");
            if (!map.IsDictionaryMap) _fields = new JSValue[inobject];
        }
        Elements = map.GetInitialElements();
    }

    /// <summary>The base constructor of the classes with in-object slots.</summary>
    private protected JSObject(Map map, bool inObjectSlots) : base(map)
    {
        Debug.Assert(inObjectSlots && map.HasInObjectSlots);
        Elements = map.GetInitialElements();
    }

    /// <summary>A field-for-field copy of <paramref name="source"/> (see CloneShallow).</summary>
    protected JSObject(JSObject source) : base(source) => Elements = source.Elements;

    /// <summary>
    /// A memberwise copy of this object (Factory::CopyJSObject then fixes up the
    /// storage). Object.MemberwiseClone is a runtime call with a GC transition
    /// (about 100 ns), so the objects literals copy (plain objects and arrays)
    /// are copied by their copy constructors; CloneShallowTest checks that
    /// they copy every field.
    /// </summary>
    internal JSObject CloneShallow()
    {
        if (this is JSArray array) return new JSArray(array);
        return CloneShallowCore();
    }

    /// <summary>The class-specific part of CloneShallow (the classes with in-object slots override it).</summary>
    internal virtual JSObject CloneShallowCore() =>
        GetType() == typeof(JSObject) ? new JSObject(this) : (JSObject)MemberwiseClone();

    /// <summary>
    /// Factory::InitializeJSObjectFromMap for an existing object: resets the
    /// fields and elements to the fresh state of <paramref name="map"/>.
    /// </summary>
    internal static void InitializeFromMap(JSObject obj, Map map)
    {
        int inobject = map.GetInObjectProperties();
        if (map.HasInObjectSlots)
        {
            if (inobject > obj.InObjectSlotCapacity) throw new InvalidOperationException("InitializeFromMap: instance too small");
            obj.ClearInObjectSlots(obj.InObjectSlotCapacity, default);
            obj._fields = EmptyFields;
        }
        else
        {
            obj._fields = !map.IsDictionaryMap && inobject > 0 ? new JSValue[inobject] : EmptyFields;
        }
        if (map.IsDictionaryMap) obj.InitializeDictionaryStorage(NameDictionary.New(NameDictionary.kInitialCapacity), 0);
        obj.Elements = map.GetInitialElements();
    }

    public static uint NewElementsCapacity(uint oldCapacity)
    {
        uint newCapacity = oldCapacity + (oldCapacity >> 1) + kMinAddedElementsCapacity;
        const uint kMaxFixedArrayCapacity = FixedArrayBase.kMaxLength;
        if (newCapacity > kMaxFixedArrayCapacity && oldCapacity + kMinAddedElementsCapacity <= kMaxFixedArrayCapacity)
        {
            return kMaxFixedArrayCapacity;
        }
        return newCapacity;
    }

    public static int NewElementsCapacity(int oldCapacity) => (int)NewElementsCapacity((uint)oldCapacity);

    // ---- Elements predicates ------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ElementsKind GetElementsKind() => Map.ElementsKind;

    public bool HasSmiElements => ElementsKinds.IsSmiElementsKind(GetElementsKind());
    public bool HasObjectElements => ElementsKinds.IsObjectElementsKind(GetElementsKind());
    public bool HasSmiOrObjectElements => ElementsKinds.IsSmiOrObjectElementsKind(GetElementsKind());
    public bool HasFastElements => ElementsKinds.IsFastElementsKind(GetElementsKind());
    public bool HasFastPackedElements => ElementsKinds.IsFastPackedElementsKind(GetElementsKind());
    public bool HasDoubleElements => ElementsKinds.IsDoubleElementsKind(GetElementsKind());
    public bool HasHoleyElements => ElementsKinds.IsHoleyElementsKindForRead(GetElementsKind());
    public bool HasSloppyArgumentsElements => ElementsKinds.IsSloppyArgumentsElementsKind(GetElementsKind());
    public bool HasStringWrapperElements => ElementsKinds.IsStringWrapperElementsKind(GetElementsKind());
    public bool HasDictionaryElements => ElementsKinds.IsDictionaryElementsKind(GetElementsKind());
    public bool HasPackedElements => GetElementsKind() == ElementsKind.PACKED_ELEMENTS;
    public bool HasAnyNonextensibleElements => ElementsKinds.IsAnyNonextensibleElementsKind(GetElementsKind());
    public bool HasSealedElements => ElementsKinds.IsSealedElementsKind(GetElementsKind());
    public bool HasSharedArrayElements => ElementsKinds.IsSharedArrayElementsKind(GetElementsKind());
    public bool HasNonextensibleElements => ElementsKinds.IsNonextensibleElementsKind(GetElementsKind());
    public bool HasTypedArrayOrRabGsabTypedArrayElements => ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(GetElementsKind());
    public bool HasFastArgumentsElements => GetElementsKind() == ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS;
    public bool HasSlowArgumentsElements => GetElementsKind() == ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS;
    public bool HasFastStringWrapperElements => GetElementsKind() == ElementsKind.FAST_STRING_WRAPPER_ELEMENTS;
    public bool HasSlowStringWrapperElements => GetElementsKind() == ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS;

    public NumberDictionary ElementDictionary
    {
        get
        {
            Debug.Assert(HasDictionaryElements || HasSlowStringWrapperElements);
            return (NumberDictionary)Elements;
        }
    }

    /// <summary>The elements accessor for this object's elements kind.</summary>
    public ElementsAccessor GetElementsAccessor() => ElementsAccessor.ForKind(GetElementsKind());

    // ---- Fast properties ------------------------------------------------------------

    /// <summary>JSObject::RawFastPropertyAt.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue RawFastPropertyAt(FieldIndex index) => FieldAt(index.StorageIndex);

    /// <summary>JSObject::FastPropertyAtPut.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FastPropertyAtPut(FieldIndex index, JSValue value) => FieldAt(index.StorageIndex) = value;

    /// <summary>
    /// JSObject::FastPropertyAt: numbers are unboxed values in V8Sharp, so no
    /// HeapNumber copy is needed for double fields (V8's WrapForRead).
    /// </summary>
    public static JSValue FastPropertyAt(Isolate isolate, JSObject obj, Representation representation, FieldIndex index)
    {
        JSValue raw = obj.RawFastPropertyAt(index);
        return raw;
    }

    /// <summary>JSObject::WriteToField.</summary>
    public void WriteToField(InternalIndex descriptor, PropertyDetails details, JSValue value)
    {
        Debug.Assert(details.Location == PropertyLocation.Field);
        Debug.Assert(details.Kind == PropertyKind.Data);
        FieldIndex index = FieldIndex.ForDetails(Map, details);
        if (details.Representation.IsDouble)
        {
            // Manipulating the signaling NaN used for the hole and uninitialized
            // double field sentinel in C++, e.g. with base::bit_cast or
            // value()/set_value(), will change its value on ia32 (the x87 stack is
            // used to return values and stores to the stack silently clear the
            // signalling bit).
            if (ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized))
            {
                FastPropertyAtPut(index, JSValue.FromNumber(FixedDoubleArray.HoleNaN));
                return;
            }
            Debug.Assert(value.IsNumber);
            double d = value.Number;
            if (double.IsNaN(d)) value = JSValue.NaN;
        }
        FastPropertyAtPut(index, value);
    }

    /// <summary>JSObject::DictionaryPropertyAt.</summary>
    public static JSValue DictionaryPropertyAt(Isolate isolate, JSObject obj, InternalIndex dictIndex)
    {
        if (obj is JSGlobalObject global) return global.GlobalDictionary.ValueAt(dictIndex);
        return obj.PropertyDictionary.ValueAt(dictIndex);
    }

    /// <summary>
    /// Grows the PropertyArray to at least <paramref name="length"/> entries
    /// (V8 grows it by JSObject::kFieldsAdded when a transition runs out of space).
    /// </summary>
    internal void EnsurePropertyArrayLength(int length)
    {
        if (_fields.Length >= length) return;
        int newLength = Math.Max(length, _fields.Length + kFieldsAdded);
        var newFields = new JSValue[newLength];
        _fields.AsSpan().CopyTo(newFields);
        _fields = newFields;
    }

    /// <summary>
    /// The PropertyArray length <paramref name="map"/> needs for
    /// <paramref name="numberOfFields"/> fields (in-object ones included).
    /// </summary>
    internal static int PropertyArrayLengthFor(Map map, int numberOfFields) =>
        map.HasInObjectSlots ? Math.Max(0, numberOfFields - map.GetInObjectProperties()) : numberOfFields;

    /// <summary>The length of the PropertyArray V8 would have (the out-of-object part).</summary>
    public int OutOfObjectPropertyArrayLength =>
        !HasFastProperties ? 0 : Map.HasInObjectSlots ? _fields.Length : Math.Max(0, _fields.Length - Map.GetInObjectProperties());

    /// <summary>
    /// Installs fast-mode fields laid out by property index (in-object fields
    /// first, V8's field order) as the storage of <paramref name="map"/>.
    /// </summary>
    internal void SetFieldsByPropertyIndex(Map map, JSValue[] fields)
    {
        if (!map.HasInObjectSlots)
        {
            _fields = fields;
            return;
        }
        int inobject = map.GetInObjectProperties();
        // A map keeps the instance size of the objects it describes (V8 never
        // migrates an object to a map with more in-object properties); the
        // slots beyond the class's capacity do not exist, so check it here
        // rather than trust it.
        if (inobject > InObjectSlotCapacity) throw new InvalidOperationException("SetFieldsByPropertyIndex: instance too small");
        int n = Math.Min(inobject, fields.Length);
        for (int i = 0; i < n; i++) InObjectSlot(i) = fields[i];
        for (int i = n; i < inobject; i++) InObjectSlot(i) = default;
        _fields = fields.Length > inobject ? fields.AsSpan(inobject).ToArray() : EmptyFields;
    }
}

/// <summary>V8's JSGlobalObject: the global object; its properties are PropertyCells in a GlobalDictionary.</summary>
public sealed class JSGlobalObject(Map map) : JSObject(map)
{
    public JSGlobalProxy? GlobalProxy;

    public GlobalDictionary GlobalDictionary
    {
        get => (GlobalDictionary)_dictionary!;
        set => _dictionary = value;
    }

    public NativeContext NativeContext => GetCreationContext()!;

    public bool IsDetached => GlobalProxy is null || GlobalProxy.IsDetachedFrom(this);

    /// <summary>JSGlobalObject::InvalidatePropertyCell.</summary>
    public static void InvalidatePropertyCell(Isolate isolate, JSGlobalObject global, Name name)
    {
        // Regardless of whether the property is there or not invalidate
        // Load/StoreGlobalICs that load/store through global object's prototype.
        JSObject.InvalidatePrototypeValidityCell(global);
        GlobalDictionary dictionary = global.GlobalDictionary;
        InternalIndex entry = dictionary.FindEntry(name);
        if (entry.IsNotFound) return;
        PropertyCell cell = dictionary.CellAt(entry);
        JSValue value = cell.Value;
        PropertyDetails details = cell.PropertyDetails;
        details = details.SetCellType(PropertyCellType.Mutable);
        PropertyCell.PrepareForAndSetValue(isolate, dictionary, entry, value, details);
    }
}

/// <summary>V8's JSGlobalProxy: the object scripts see as the global object; forwards to the JSGlobalObject.</summary>
public sealed class JSGlobalProxy(Map map) : JSObject(map)
{
    public bool IsDetachedFrom(JSGlobalObject global) => !ReferenceEquals(Map.Prototype, global);
    public bool IsDetached => Map.Prototype is null;
}

/// <summary>V8's JSPrimitiveWrapper: the object wrapper of a primitive (new Number(1), Object("s")...).</summary>
public sealed class JSPrimitiveWrapper(Map map) : JSObject(map)
{
    public JSValue Value;
}

/// <summary>
/// V8's PrototypeIterator (src/objects/prototype.h): walks the prototype
/// chain, optionally through proxies.
/// </summary>
public struct PrototypeIterator
{
    public enum WhereToEnd
    {
        END_AT_NULL,
        END_AT_NON_HIDDEN,
    }

    const int JSProxyMaxSeen = 100 * 1024;

    readonly Isolate _isolate;
    JSReceiver? _object;
    readonly WhereToEnd _whereToEnd;
    bool _isAtEnd;
    int _seenProxies;

    public PrototypeIterator(Isolate isolate, JSReceiver receiver, WhereToStart whereToStart = WhereToStart.StartAtPrototype,
        WhereToEnd whereToEnd = WhereToEnd.END_AT_NULL)
    {
        _isolate = isolate;
        _object = receiver;
        _whereToEnd = whereToEnd;
        _isAtEnd = false;
        _seenProxies = 0;
        if (whereToStart == WhereToStart.StartAtPrototype) Advance();
    }

    public PrototypeIterator(Isolate isolate, Map receiverMap, WhereToEnd whereToEnd = WhereToEnd.END_AT_NULL)
    {
        _isolate = isolate;
        _object = receiverMap.Prototype;
        _whereToEnd = whereToEnd;
        _isAtEnd = _object is null;
        _seenProxies = 0;
        if (!_isAtEnd && _whereToEnd == WhereToEnd.END_AT_NON_HIDDEN)
        {
            _isAtEnd = !Map.IsJSGlobalProxyMap(receiverMap);
        }
    }

    public readonly bool IsAtEnd => _isAtEnd;

    /// <summary>The current object (a JSReceiver; null means the end, JavaScript null).</summary>
    public readonly JSReceiver? GetCurrent() => _object;

    public readonly T GetCurrent<T>() where T : JSReceiver => (T)_object!;

    /// <summary>PrototypeIterator::HasAccess: Isolate::MayAccess for access-checked objects.</summary>
    public readonly bool HasAccess()
    {
        if (_object is not JSObject obj || !obj.Map.IsAccessCheckNeeded) return true;
        Context? context = _isolate.Context;
        return context is null || _isolate.MayAccess(context.NativeContext, obj);
    }

    public void Advance()
    {
        if (_object is null)
        {
            // Advancing from null to null is a no-op.
            _isAtEnd = true;
            return;
        }
        AdvanceIgnoringProxies();
    }

    public void AdvanceIgnoringProxies()
    {
        JSReceiver current = _object!;
        Map map = current.Map;
        JSReceiver? prototype = map.Prototype;
        _isAtEnd = prototype is null ||
                   (_whereToEnd == WhereToEnd.END_AT_NON_HIDDEN && !Map.IsJSGlobalProxyMap(map));
        _object = prototype;
    }

    /// <summary>Returns false iff a call to JSProxy::GetPrototype throws (here: throws).</summary>
    public bool AdvanceFollowingProxies()
    {
        if (!HasAccess())
        {
            // Abort the lookup if we do not have access to the current object.
            _object = null;
            _isAtEnd = true;
            return true;
        }
        return AdvanceFollowingProxiesIgnoringAccessChecks();
    }

    public bool AdvanceFollowingProxiesIgnoringAccessChecks()
    {
        if (_object is JSProxy proxy)
        {
            // Due to possible __proto__ recursion limit the number of Proxies
            // we visit to an arbitrarily chosen large number.
            _seenProxies++;
            if (_seenProxies > JSProxyMaxSeen)
            {
                _isolate.StackOverflow();
                return false;
            }
            JSReceiver? proto = JSProxy.GetPrototype(_isolate, proxy);
            _object = proto;
            _isAtEnd = _whereToEnd == WhereToEnd.END_AT_NON_HIDDEN || proto is null;
            return true;
        }
        AdvanceIgnoringProxies();
        return true;
    }

}

/// <summary>Deterministic identity-hash generator (V8's Isolate::GenerateIdentityHash).</summary>
public static class IdentityHash
{
    static ulong s_state = 0x9E3779B97F4A7C15UL;

    /// <summary>A non-zero pseudo-random 30-bit hash.</summary>
    public static uint Next()
    {
        ulong z = Interlocked.Add(ref s_state, 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        uint h = (uint)z & Name.HashBitsMax;
        return h == 0 ? 1u : h;
    }
}
