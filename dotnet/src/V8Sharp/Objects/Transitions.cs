// Port of src/objects/transitions{.h,-inl.h,.cc}: map transitions.
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's SideStepTransition kinds.</summary>
public static class SideStepTransition
{
    public enum Kind
    {
        CloneObject,
        ObjectAssign,
        ObjectAssignValidityCell,
    }

    public const int kSize = (int)Kind.ObjectAssignValidityCell + 1;

    /// <summary>Marks an empty side-step slot (V8: Smi 0).</summary>
    public static readonly HeapObject Empty = new SideStepSentinel();

    /// <summary>Marks an unreachable side-step slot (V8: Smi 1).</summary>
    public static readonly HeapObject Unreachable = new SideStepSentinel();

    sealed class SideStepSentinel() : HeapObject(InstanceType.CellType);
}

/// <summary>
/// V8's TransitionArray: the property transitions of a map, sorted by (name
/// hash, kind, attributes) once there are more than
/// <see cref="kMaxElementsForLinearSearch"/>, plus prototype and side-step
/// transitions.
/// </summary>
/// <remarks>Deviation: V8 holds transition targets weakly; V8Sharp holds them strongly.</remarks>
public sealed class TransitionArray : HeapObject
{
    public const int kMaxElementsForLinearSearch = 32;
    public const int kNotFound = -1;
    public const int kMaxCachedPrototypeTransitions = 256;

    Name[] _keys;
    Map?[] _targets;
    int _numberOfTransitions;

    /// <summary>Prototype transitions (V8's prototype transitions WeakFixedArray), or null.</summary>
    internal List<Map>? PrototypeTransitions;

    /// <summary>Side-step transitions (V8's WeakFixedArray of kSize), or null.</summary>
    internal HeapObject[]? SideStepTransitions;

    public TransitionArray(int numberOfTransitions, int slack = 0) : base(InstanceType.TransitionArrayType)
    {
        _keys = new Name[numberOfTransitions + slack];
        _targets = new Map?[numberOfTransitions + slack];
        _numberOfTransitions = numberOfTransitions;
    }

    public int NumberOfTransitions => _numberOfTransitions;
    internal int Capacity => _keys.Length;
    internal void SetNumberOfTransitions(int n) => _numberOfTransitions = n;

    public bool HasPrototypeTransitions => PrototypeTransitions is not null;
    public bool HasSideStepTransitions => SideStepTransitions is not null;

    public Name GetKey(int transitionNumber) => _keys[transitionNumber];
    public void SetKey(int transitionNumber, Name key) => _keys[transitionNumber] = key;
    public Map GetTarget(int transitionNumber) => _targets[transitionNumber]!;
    internal Map? GetRawTarget(int transitionNumber) => _targets[transitionNumber];
    internal void SetRawTarget(int transitionNumber, Map? target) => _targets[transitionNumber] = target;

    internal void Set(int transitionNumber, Name key, Map? target)
    {
        _keys[transitionNumber] = key;
        _targets[transitionNumber] = target;
    }

    static int CompareKeys(Name key1, uint hash1, PropertyKind kind1, PropertyAttributes attributes1,
        Name key2, uint hash2, PropertyKind kind2, PropertyAttributes attributes2)
    {
        int cmp = CompareNames(key1, hash1, key2, hash2);
        if (cmp != 0) return cmp;
        return CompareDetails(kind1, attributes1, kind2, attributes2);
    }

    static int CompareNames(Name key1, uint hash1, Name key2, uint hash2)
    {
        if (!ReferenceEquals(key1, key2)) return hash1 <= hash2 ? -1 : 1;
        return 0;
    }

    static int CompareDetails(PropertyKind kind1, PropertyAttributes attributes1, PropertyKind kind2, PropertyAttributes attributes2)
    {
        if (kind1 != kind2) return (int)kind1 < (int)kind2 ? -1 : 1;
        if (attributes1 != attributes2) return (int)attributes1 < (int)attributes2 ? -1 : 1;
        return 0;
    }

    internal int SearchName(Name name, out int insertionIndex)
    {
        if (_numberOfTransitions == 0)
        {
            insertionIndex = 0;
            return kNotFound;
        }
        if (_numberOfTransitions <= kMaxElementsForLinearSearch) return LinearSearchName(name, out insertionIndex);
        return BinarySearchName(name, out insertionIndex);
    }

    internal int SearchName(Name name) => SearchName(name, out _);

    int LinearSearchName(Name name, out int insertionIndex)
    {
        int len = _numberOfTransitions;
        for (int i = 0; i < len; i++)
        {
            if (ReferenceEquals(_keys[i], name))
            {
                insertionIndex = i;
                return i;
            }
        }
        insertionIndex = len;
        return kNotFound;
    }

    int BinarySearchName(Name name, out int insertionIndex)
    {
        int end = _numberOfTransitions;
        uint hash = name.EnsureHash();
        int low = 0, high = end;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (_keys[mid].EnsureHash() < hash) low = mid + 1;
            else high = mid;
        }
        for (int i = low; i < end; ++i)
        {
            Name entry = _keys[i];
            if (ReferenceEquals(entry, name))
            {
                insertionIndex = i;
                return i;
            }
            uint entryHash = entry.EnsureHash();
            if (entryHash != hash)
            {
                insertionIndex = i + (entryHash > hash ? 0 : 1);
                return kNotFound;
            }
        }
        insertionIndex = end;
        return kNotFound;
    }

    internal int SearchSpecial(Symbol symbol, out int insertionIndex) => SearchName(symbol, out insertionIndex);

    internal int SearchSpecial(Symbol symbol) => SearchName(symbol, out _);

    int SearchDetails(int transition, PropertyKind kind, PropertyAttributes attributes, out int insertionIndex)
    {
        int nof = _numberOfTransitions;
        Name key = _keys[transition];
        for (; transition < nof && ReferenceEquals(_keys[transition], key); transition++)
        {
            Map target = GetTarget(transition);
            PropertyDetails targetDetails = TransitionsAccessor.GetTargetDetails(key, target);
            int cmp = CompareDetails(kind, attributes, targetDetails.Kind, targetDetails.Attributes);
            if (cmp == 0)
            {
                insertionIndex = transition;
                return transition;
            }
            if (cmp < 0) break;
        }
        insertionIndex = transition;
        return kNotFound;
    }

    internal int Search(PropertyKind kind, Name name, PropertyAttributes attributes, out int insertionIndex)
    {
        int transition = SearchName(name, out insertionIndex);
        if (transition == kNotFound) return kNotFound;
        return SearchDetails(transition, kind, attributes, out insertionIndex);
    }

    internal Map? SearchAndGetTarget(PropertyKind kind, Name name, PropertyAttributes attributes)
    {
        int transition = SearchName(name);
        if (transition == kNotFound) return null;
        int nof = _numberOfTransitions;
        Name key = _keys[transition];
        for (; transition < nof && ReferenceEquals(_keys[transition], key); transition++)
        {
            Map target = GetTarget(transition);
            PropertyDetails targetDetails = TransitionsAccessor.GetTargetDetails(key, target);
            int cmp = CompareDetails(kind, attributes, targetDetails.Kind, targetDetails.Attributes);
            if (cmp == 0) return target;
            if (cmp < 0) break;
        }
        return null;
    }

    public Map? SearchAndGetTargetForTesting(PropertyKind kind, Name name, PropertyAttributes attributes) =>
        SearchAndGetTarget(kind, name, attributes);

    public int SearchNameForTesting(Name name) => SearchName(name);

    internal void ForEachTransitionTo(Name name, Action<Map> callback)
    {
        int transition = SearchName(name);
        if (transition == kNotFound) return;
        int nof = _numberOfTransitions;
        Name key = _keys[transition];
        for (; transition < nof && ReferenceEquals(_keys[transition], key); transition++)
        {
            callback(GetTarget(transition));
        }
    }

    /// <summary>Insertion sort by (hash, kind, attributes) (TransitionArray::Sort).</summary>
    public void Sort(bool force = false)
    {
        int length = _numberOfTransitions;
        if (!force && length <= kMaxElementsForLinearSearch) return;
        for (int i = 1; i < length; i++)
        {
            Name key = _keys[i];
            Map? target = _targets[i];
            PropertyKind kind = PropertyKind.Data;
            PropertyAttributes attributes = PropertyAttributes.NONE;
            if (!TransitionsAccessor.IsSpecialTransition(key))
            {
                PropertyDetails details = TransitionsAccessor.GetTargetDetails(key, target!);
                kind = details.Kind;
                attributes = details.Attributes;
            }
            int j;
            for (j = i - 1; j >= 0; j--)
            {
                Name tempKey = _keys[j];
                Map? tempTarget = _targets[j];
                PropertyKind tempKind = PropertyKind.Data;
                PropertyAttributes tempAttributes = PropertyAttributes.NONE;
                if (!TransitionsAccessor.IsSpecialTransition(tempKey))
                {
                    PropertyDetails details = TransitionsAccessor.GetTargetDetails(tempKey, tempTarget!);
                    tempKind = details.Kind;
                    tempAttributes = details.Attributes;
                }
                int cmp = CompareKeys(tempKey, tempKey.EnsureHash(), tempKind, tempAttributes, key, key.EnsureHash(), kind, attributes);
                if (cmp > 0)
                {
                    _keys[j + 1] = tempKey;
                    _targets[j + 1] = tempTarget;
                }
                else
                {
                    break;
                }
            }
            _keys[j + 1] = key;
            _targets[j + 1] = target;
        }
    }

    /// <summary>Debug check (TransitionArray::IsSortedNoDuplicates).</summary>
    public bool IsSortedNoDuplicates()
    {
        if (_numberOfTransitions <= kMaxElementsForLinearSearch) return true;
        Name? prevKey = null;
        uint prevHash = 0;
        PropertyKind prevKind = PropertyKind.Data;
        PropertyAttributes prevAttributes = PropertyAttributes.NONE;
        for (int i = 0; i < _numberOfTransitions; i++)
        {
            Name key = _keys[i];
            uint hash = key.EnsureHash();
            PropertyKind kind = PropertyKind.Data;
            PropertyAttributes attributes = PropertyAttributes.NONE;
            if (!TransitionsAccessor.IsSpecialTransition(key))
            {
                PropertyDetails details = TransitionsAccessor.GetTargetDetails(key, GetTarget(i));
                kind = details.Kind;
                attributes = details.Attributes;
            }
            if (prevKey is not null && CompareKeys(prevKey, prevHash, prevKind, prevAttributes, key, hash, kind, attributes) >= 0)
            {
                return false;
            }
            prevKey = key;
            prevHash = hash;
            prevKind = kind;
            prevAttributes = attributes;
        }
        return true;
    }
}

/// <summary>
/// The raw_transitions encoding of a deprecated map pointing at its migration
/// target (V8 stores the target map strongly; a simple transition is stored
/// weakly, which is how V8 tells the two apart).
/// </summary>
internal sealed class MigrationTarget(Map target) : HeapObject(InstanceType.CellType)
{
    public readonly Map Target = target;
}

/// <summary>V8's TransitionsAccessor: reads and updates a map's raw transitions.</summary>
public readonly struct TransitionsAccessor
{
    public const int kMaxNumberOfTransitions = 1024 + 512;

    public enum Encoding
    {
        PrototypeInfo,
        Uninitialized,
        MigrationTarget,
        WeakRef,
        FullTransitionArray,
        PrototypeSharedClosureInfo,
    }

    readonly Map _map;
    readonly object? _rawTransitions;
    readonly Encoding _encoding;

    public TransitionsAccessor(Isolate? isolate, Map map)
    {
        _map = map;
        _rawTransitions = map.RawTransitions;
        _encoding = GetEncoding(_rawTransitions);
    }

    public Encoding encoding => _encoding;

    static Encoding GetEncoding(object? raw) => raw switch
    {
        null => Encoding.Uninitialized,
        Map => Encoding.WeakRef,
        TransitionArray => Encoding.FullTransitionArray,
        PrototypeInfo => Encoding.PrototypeInfo,
        MigrationTarget => Encoding.MigrationTarget,
        _ => throw new InvalidOperationException("unexpected raw transitions"),
    };

    TransitionArray Transitions => (TransitionArray)_rawTransitions!;

    /// <summary>TestTransitionsAccessor::transitions (the full transition array).</summary>
    internal TransitionArray TransitionArrayForTesting => Transitions;

    static Map? GetSimpleTransition(Map map) => map.RawTransitions as Map;

    static Name GetSimpleTransitionKey(Map transition) =>
        transition.InstanceDescriptors.GetKey(transition.LastAdded());

    public static PropertyDetails GetTargetDetails(Name name, Map target)
    {
        Debug.Assert(!IsSpecialTransition(name));
        InternalIndex descriptor = target.LastAdded();
        DescriptorArray descriptors = target.InstanceDescriptors;
        return descriptors.GetDetails(descriptor);
    }

    public static bool IsSpecialTransition(Name name)
    {
        if (name is not Symbol) return false;
        return ReferenceEquals(name, ReadOnlyRoots.nonextensible_symbol) ||
               ReferenceEquals(name, ReadOnlyRoots.sealed_symbol) ||
               ReferenceEquals(name, ReadOnlyRoots.frozen_symbol) ||
               ReferenceEquals(name, ReadOnlyRoots.elements_transition_symbol) ||
               ReferenceEquals(name, ReadOnlyRoots.strict_function_transition_symbol) ||
               ReferenceEquals(name, ReadOnlyRoots.detached_symbol);
    }

    public static void Insert(Isolate isolate, Map map, Name name, Map target, TransitionKindFlag flag) =>
        InsertHelper(isolate, map, name, target, flag);

    static void ReplaceTransitions(Map map, HeapObject? newTransitions) => map.RawTransitions = newTransitions;

    static void InsertHelper(Isolate isolate, Map map, Name name, Map target, TransitionKindFlag flag)
    {
        Debug.Assert(flag != TransitionKindFlag.PROTOTYPE_TRANSITION);
        Encoding encoding = GetEncoding(map.RawTransitions);
        Debug.Assert(encoding != Encoding.PrototypeInfo);
        target.SetBackPointer(map);

        // If the map doesn't have any transitions at all yet, install the new
        // target as a simple transition.
        if (encoding is Encoding.Uninitialized or Encoding.MigrationTarget)
        {
            if (flag == TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION)
            {
                ReplaceTransitions(map, target);
                return;
            }
            var result = new TransitionArray(1, 0);
            result.Set(0, name, target);
            ReplaceTransitions(map, result);
            return;
        }

        if (encoding == Encoding.WeakRef)
        {
            Map simpleTransition = GetSimpleTransition(map)!;
            if (flag == TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION)
            {
                Name key = GetSimpleTransitionKey(simpleTransition);
                PropertyDetails oldDetails = simpleTransition.GetLastDescriptorDetails();
                PropertyDetails newDetails = GetTargetDetails(name, target);
                if (key.Equals(name) && oldDetails.Kind == newDetails.Kind && oldDetails.Attributes == newDetails.Attributes)
                {
                    ReplaceTransitions(map, target);
                    return;
                }
            }

            // Otherwise allocate a full TransitionArray with slack for a new entry.
            var result = new TransitionArray(1, 1);
            result.Set(0, GetSimpleTransitionKey(simpleTransition), simpleTransition);
            int insertionIndex;
            int index;
            if (flag == TransitionKindFlag.SPECIAL_TRANSITION)
            {
                index = result.SearchSpecial((Symbol)name, out insertionIndex);
            }
            else
            {
                PropertyDetails details = GetTargetDetails(name, target);
                index = result.Search(details.Kind, name, details.Attributes, out insertionIndex);
            }
            Debug.Assert(index == TransitionArray.kNotFound);
            result.SetNumberOfTransitions(2);
            if (insertionIndex == 0)
            {
                // If the new transition will be inserted in front of the simple
                // transition, move the simple transition to index 1.
                result.Set(1, GetSimpleTransitionKey(simpleTransition), simpleTransition);
            }
            result.SetKey(insertionIndex, name);
            result.SetRawTarget(insertionIndex, target);
            ReplaceTransitions(map, result);
            return;
        }

        // At this point, we know that the map has a full TransitionArray.
        Debug.Assert(encoding == Encoding.FullTransitionArray);
        int numberOfTransitions;
        int newNof;
        int insertion;
        bool isSpecialTransition = flag == TransitionKindFlag.SPECIAL_TRANSITION;
        PropertyDetails d = isSpecialTransition ? PropertyDetails.Empty() : GetTargetDetails(name, target);
        TransitionArray array = (TransitionArray)map.RawTransitions!;
        numberOfTransitions = array.NumberOfTransitions;
        int idx = isSpecialTransition
            ? array.SearchSpecial((Symbol)name, out insertion)
            : array.Search(d.Kind, name, d.Attributes, out insertion);
        // If an existing entry was found, overwrite it and return.
        if (idx != TransitionArray.kNotFound)
        {
            array.SetRawTarget(idx, target);
            return;
        }
        newNof = numberOfTransitions + 1;
        if (newNof > kMaxNumberOfTransitions) throw new InvalidOperationException("too many transitions");

        // If there is enough capacity, insert new entry into the existing array.
        if (newNof <= array.Capacity)
        {
            array.SetNumberOfTransitions(newNof);
            for (int i = numberOfTransitions; i > insertion; --i)
            {
                array.SetKey(i, array.GetKey(i - 1));
                array.SetRawTarget(i, array.GetRawTarget(i - 1));
            }
            array.SetKey(insertion, name);
            array.SetRawTarget(insertion, target);
            if (newNof == TransitionArray.kMaxElementsForLinearSearch + 1) array.Sort();
            return;
        }

        // We're gonna need a bigger TransitionArray.
        int slack = Map.SlackForArraySize(numberOfTransitions, kMaxNumberOfTransitions);
        var grown = new TransitionArray(newNof, slack);
        if (array.HasPrototypeTransitions) grown.PrototypeTransitions = array.PrototypeTransitions;
        if (array.HasSideStepTransitions) grown.SideStepTransitions = array.SideStepTransitions;
        for (int i = 0; i < insertion; ++i) grown.Set(i, array.GetKey(i), array.GetRawTarget(i));
        grown.Set(insertion, name, target);
        for (int i = insertion; i < numberOfTransitions; ++i) grown.Set(i + 1, array.GetKey(i), array.GetRawTarget(i));
        if (newNof == TransitionArray.kMaxElementsForLinearSearch + 1) grown.Sort();
        ReplaceTransitions(map, grown);
    }

    public Map? SearchTransition(Name name, PropertyKind kind, PropertyAttributes attributes)
    {
        Debug.Assert(name.IsUniqueName);
        switch (_encoding)
        {
            case Encoding.PrototypeInfo:
            case Encoding.PrototypeSharedClosureInfo:
            case Encoding.Uninitialized:
            case Encoding.MigrationTarget:
                return null;
            case Encoding.WeakRef:
            {
                Map map = (Map)_rawTransitions!;
                if (!IsMatchingMap(map, name, kind, attributes)) return null;
                return map;
            }
            case Encoding.FullTransitionArray:
                return Transitions.SearchAndGetTarget(kind, name, attributes);
        }
        throw new InvalidOperationException("unreachable");
    }

    public static Map? SearchTransition(Isolate isolate, Map map, Name name, PropertyKind kind, PropertyAttributes attributes) =>
        new TransitionsAccessor(isolate, map).SearchTransition(name, kind, attributes);

    public Map? SearchSpecial(Symbol name)
    {
        if (_encoding != Encoding.FullTransitionArray) return null;
        int transition = Transitions.SearchSpecial(name);
        if (transition == TransitionArray.kNotFound) return null;
        return Transitions.GetTarget(transition);
    }

    public static Map? SearchSpecial(Isolate isolate, Map map, Symbol name) =>
        new TransitionsAccessor(isolate, map).SearchSpecial(name);

    public Map? FindTransitionToField(JSString name)
    {
        Debug.Assert(name.IsInternalized);
        return SearchTransition(name, PropertyKind.Data, PropertyAttributes.NONE);
    }

    public void ForEachTransitionTo(Name name, Action<Map> callback)
    {
        switch (_encoding)
        {
            case Encoding.PrototypeInfo:
            case Encoding.PrototypeSharedClosureInfo:
            case Encoding.Uninitialized:
            case Encoding.MigrationTarget:
                return;
            case Encoding.WeakRef:
            {
                Map target = (Map)_rawTransitions!;
                InternalIndex descriptor = target.LastAdded();
                if (ReferenceEquals(target.InstanceDescriptors.GetKey(descriptor), name)) callback(target);
                return;
            }
            case Encoding.FullTransitionArray:
                Transitions.ForEachTransitionTo(name, callback);
                return;
        }
    }

    public static bool CanHaveMoreTransitions(Isolate isolate, Map map)
    {
        if (map.IsDictionaryMap) return false;
        if (map.RawTransitions is TransitionArray array)
        {
            return array.NumberOfTransitions < kMaxNumberOfTransitions;
        }
        return true;
    }

    public static bool IsMatchingMap(Map target, Name name, PropertyKind kind, PropertyAttributes attributes)
    {
        InternalIndex descriptor = target.LastAdded();
        DescriptorArray descriptors = target.InstanceDescriptors;
        if (!ReferenceEquals(descriptors.GetKey(descriptor), name)) return false;
        return descriptors.GetDetails(descriptor).HasKindAndAttributes(kind, attributes);
    }

    public int NumberOfTransitions() => _encoding switch
    {
        Encoding.WeakRef => 1,
        Encoding.FullTransitionArray => Transitions.NumberOfTransitions,
        _ => 0,
    };

    public Name GetKey(int transitionNumber) => _encoding switch
    {
        Encoding.WeakRef => GetSimpleTransitionKey((Map)_rawTransitions!),
        Encoding.FullTransitionArray => Transitions.GetKey(transitionNumber),
        _ => throw new InvalidOperationException("unreachable"),
    };

    public Map GetTarget(int transitionNumber) => _encoding switch
    {
        Encoding.WeakRef => (Map)_rawTransitions!,
        Encoding.FullTransitionArray => Transitions.GetTarget(transitionNumber),
        _ => throw new InvalidOperationException("unreachable"),
    };

    public bool HasPrototypeTransitions() =>
        _encoding == Encoding.FullTransitionArray && Transitions.HasPrototypeTransitions;

    public static void SetMigrationTarget(Isolate isolate, Map map, Map migrationTarget)
    {
        // We only cache the migration target for maps with empty transitions for GC's sake.
        if (GetEncoding(map.RawTransitions) != Encoding.Uninitialized) return;
        Debug.Assert(map.IsDeprecated);
        map.RawTransitions = new MigrationTarget(migrationTarget);
    }

    public Map? GetMigrationTarget() =>
        _encoding == Encoding.MigrationTarget ? ((MigrationTarget)_rawTransitions!).Target : null;

    static void EnsureHasFullTransitionArray(Map map)
    {
        Encoding encoding = GetEncoding(map.RawTransitions);
        if (encoding == Encoding.FullTransitionArray) return;
        int nof = encoding is Encoding.Uninitialized or Encoding.MigrationTarget ? 0 : 1;
        var result = new TransitionArray(nof);
        if (nof == 1)
        {
            Map target = GetSimpleTransition(map)!;
            result.Set(0, GetSimpleTransitionKey(target), target);
        }
        ReplaceTransitions(map, result);
    }

    /// <summary>TransitionsAccessor::PutPrototypeTransition.</summary>
    public static bool PutPrototypeTransition(Isolate isolate, Map map, JSReceiver? prototype, Map targetMap)
    {
        Debug.Assert(map.GetBackPointer() is null);
        // Don't cache prototype transition if this map is either shared, or a map
        // of a prototype.
        if (map.IsPrototypeMap) return false;
        if (map.IsDictionaryMap || !isolate.Flags.cache_prototype_transitions) return false;

        EnsureHasFullTransitionArray(map);
        TransitionArray array = (TransitionArray)map.RawTransitions!;
        List<Map> cache = array.PrototypeTransitions ??= [];
        if (cache.Count >= TransitionArray.kMaxCachedPrototypeTransitions) return false;
        targetMap.SetBackPointer(map);
        cache.Add(targetMap);
        return true;
    }

    /// <summary>TransitionsAccessor::GetPrototypeTransition.</summary>
    public static Map? GetPrototypeTransition(Isolate isolate, Map map, JSReceiver? prototype)
    {
        if (map.RawTransitions is not TransitionArray array || array.PrototypeTransitions is null) return null;
        foreach (Map target in array.PrototypeTransitions)
        {
            if (ReferenceEquals(target.Prototype, prototype)) return target;
        }
        return null;
    }

    public bool HasSideStepTransitions() =>
        _encoding == Encoding.FullTransitionArray && Transitions.HasSideStepTransitions;

    public static void EnsureHasSideStepTransitions(Isolate isolate, Map map)
    {
        EnsureHasFullTransitionArray(map);
        TransitionArray transitions = (TransitionArray)map.RawTransitions!;
        if (transitions.HasSideStepTransitions) return;
        var result = new HeapObject[SideStepTransition.kSize];
        result.AsSpan().Fill(SideStepTransition.Empty);
        transitions.SideStepTransitions = result;
    }

    public HeapObject GetSideStepTransition(SideStepTransition.Kind kind)
    {
        Debug.Assert(HasSideStepTransitions());
        return Transitions.SideStepTransitions![(int)kind];
    }

    public void SetSideStepTransition(SideStepTransition.Kind kind, HeapObject target)
    {
        Debug.Assert(HasSideStepTransitions());
        Transitions.SideStepTransitions![(int)kind] = target;
    }

    public bool HasIntegrityLevelTransitionTo(Map to, out Symbol? outSymbol, out PropertyAttributes outIntegrityLevel)
    {
        if (ReferenceEquals(SearchSpecial(ReadOnlyRoots.frozen_symbol), to))
        {
            outIntegrityLevel = PropertyAttributes.FROZEN;
            outSymbol = ReadOnlyRoots.frozen_symbol;
        }
        else if (ReferenceEquals(SearchSpecial(ReadOnlyRoots.sealed_symbol), to))
        {
            outIntegrityLevel = PropertyAttributes.SEALED;
            outSymbol = ReadOnlyRoots.sealed_symbol;
        }
        else if (ReferenceEquals(SearchSpecial(ReadOnlyRoots.nonextensible_symbol), to))
        {
            outIntegrityLevel = PropertyAttributes.NONE;
            outSymbol = ReadOnlyRoots.nonextensible_symbol;
        }
        else
        {
            outSymbol = null;
            outIntegrityLevel = PropertyAttributes.NONE;
            return false;
        }
        return true;
    }

    /// <summary>Calls <paramref name="callback"/> on every map of the transition tree.</summary>
    public void TraverseTransitionTree(Action<Map> callback)
    {
        var stack = new Stack<Map>();
        stack.Push(_map);
        while (stack.Count > 0)
        {
            Map current = stack.Pop();
            callback(current);
            switch (current.RawTransitions)
            {
                case Map simple:
                    stack.Push(simple);
                    break;
                case TransitionArray transitions:
                    if (transitions.PrototypeTransitions is { } protoTransitions)
                    {
                        foreach (Map m in protoTransitions) stack.Push(m);
                    }
                    for (int i = 0; i < transitions.NumberOfTransitions; ++i) stack.Push(transitions.GetTarget(i));
                    break;
            }
        }
    }

    /// <summary>Calls <paramref name="callback"/> on every direct transition target (ForEachTransition).</summary>
    public void ForEachTransition(Action<Map> callback)
    {
        switch (_encoding)
        {
            case Encoding.WeakRef:
                callback((Map)_rawTransitions!);
                return;
            case Encoding.FullTransitionArray:
            {
                TransitionArray transitions = Transitions;
                if (transitions.PrototypeTransitions is { } protoTransitions)
                {
                    foreach (Map m in protoTransitions.ToArray()) callback(m);
                }
                for (int i = 0; i < transitions.NumberOfTransitions; ++i) callback(transitions.GetTarget(i));
                return;
            }
        }
    }
}
