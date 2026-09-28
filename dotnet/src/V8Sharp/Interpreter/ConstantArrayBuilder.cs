// Port of src/interpreter/constant-array-builder.h/.cc.
//
// AST-keyed entries (AstRawString, AstConsString, AstBigInt, Scope) are keyed
// by object identity, as V8 keys them by pointer. The builder does not know
// the AST types (V8Sharp.Parsing cannot reference the engine); it stores the
// key objects and asks an IConstantPoolMaterializer to turn them into heap
// values in ToFixedArray(), which is where V8 calls
// AstRawString::string(), AstConsString::AllocateFlat(), BigIntLiteral() and
// Scope::scope_info().
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace V8Sharp.Interpreter;

/// <summary>A Smi (small integer) constant, V8's Tagged&lt;Smi&gt; as stored in a constant pool.</summary>
// TODO(merge): the object model represents numbers as JSValue; the interpreter
// may prefer JSValue constant pools once strings and ScopeInfos are heap objects.
public readonly record struct Smi(int Value)
{
    public static Smi FromInt(int value) => new(value);
    public static Smi Zero => default;
    public static bool IsValid(long value) => value >= kMinValue && value <= kMaxValue;

    // 31-bit Smis (pointer compression is V8's default configuration).
    public const int kMinValue = -(1 << 30);
    public const int kMaxValue = (1 << 30) - 1;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Constant array entries that represent singletons (SINGLETON_CONSTANT_ENTRY_TYPES).</summary>
public enum SingletonConstant : byte
{
    AsyncIteratorSymbol,
    ClassFieldsSymbol,
    EmptyObjectBoilerplateDescription,
    EmptyArrayBoilerplateDescription,
    EmptyFixedArray,
    IteratorSymbol,
    InterpreterTrampolineSymbol,
    NaN,
}

/// <summary>
/// Turns constant-pool entries into the objects stored in the BytecodeArray's
/// constant pool (V8's ConstantArrayBuilder::Entry::ToHandle). The bytecode
/// generator supplies one backed by the isolate's factory.
/// </summary>
// TODO(merge): the BytecodeGenerator / Factory port implements this with real
// heap objects (internalized strings, ScopeInfo, BigInt, symbols, boilerplates).
public interface IConstantPoolMaterializer
{
    object RawString(object rawString);
    object ConsString(object consString);
    object BigInt(object bigint);
    object ScopeInfo(object scope);
    object Singleton(SingletonConstant singleton);
    object HeapNumber(double value);
    object Smi(Smi value);
    /// <summary>The value left in slots skipped by reservations (V8's the_hole).</summary>
    object TheHole { get; }
}

/// <summary>
/// A helper class for constructing constant arrays for the interpreter. Each
/// instance of this class is intended to be used to generate exactly one
/// constant pool array via the ToFixedArray method.
/// </summary>
public sealed class ConstantArrayBuilder
{
    /// <summary>Capacity of the 8-bit operand slice.</summary>
    public const int k8BitCapacity = 1 << 8;

    /// <summary>Capacity of the 16-bit operand slice.</summary>
    public const int k16BitCapacity = (1 << 16) - k8BitCapacity;

    /// <summary>Capacity of the 32-bit operand slice (kMaxUInt32 - k16BitCapacity - k8BitCapacity + 1).</summary>
    public const long k32BitCapacity = uint.MaxValue - (long)k16BitCapacity - k8BitCapacity + 1;

    enum Tag : byte
    {
        Deferred,
        Handle,
        Smi,
        RawString,
        ConsString,
        HeapNumber,
        BigInt,
        Scope,
        UninitializedJumpTableSmi,
        JumpTableSmi,
        Singleton,
    }

    struct Entry
    {
        public Tag Tag;
        public SingletonConstant Singleton;
        public int SmiValue;
        public double HeapNumberValue;
        // The AST key (raw string, cons string, bigint, scope) or the deferred handle.
        public object? Object;

        public static Entry ForSmi(Smi smi) => new() { Tag = Tag.Smi, SmiValue = smi.Value };
        public static Entry ForHeapNumber(double value) => new() { Tag = Tag.HeapNumber, HeapNumberValue = value };
        public static Entry ForObject(Tag tag, object key) => new() { Tag = tag, Object = key };
        public static Entry ForSingleton(SingletonConstant s) => new() { Tag = Tag.Singleton, Singleton = s };
        public static Entry Deferred() => new() { Tag = Tag.Deferred };
        public static Entry UninitializedJumpTableSmi() => new() { Tag = Tag.UninitializedJumpTableSmi };

        public readonly bool IsDeferred => Tag == Tag.Deferred;
        public readonly bool IsJumpTableEntry => Tag is Tag.UninitializedJumpTableSmi or Tag.JumpTableSmi;

        public void SetDeferred(object handle)
        {
            Debug.Assert(Tag == Tag.Deferred);
            Tag = Tag.Handle;
            Object = handle;
        }

        public void SetJumpTableSmi(Smi smi)
        {
            Debug.Assert(Tag == Tag.UninitializedJumpTableSmi);
            Tag = Tag.JumpTableSmi;
            SmiValue = smi.Value;
        }

        public readonly object ToHandle(IConstantPoolMaterializer m) => Tag switch
        {
            // We shouldn't have any deferred entries by now.
            Tag.Deferred => throw new UnreachableException(),
            Tag.Handle => Object!,
            Tag.Smi or Tag.JumpTableSmi => m.Smi(new Smi(SmiValue)),
            Tag.UninitializedJumpTableSmi => throw new UnreachableException(),
            Tag.RawString => m.RawString(Object!),
            Tag.ConsString => m.ConsString(Object!),
            Tag.HeapNumber => m.HeapNumber(HeapNumberValue),
            Tag.BigInt => m.BigInt(Object!),
            Tag.Scope => m.ScopeInfo(Object!),
            Tag.Singleton => m.Singleton(Singleton),
            _ => throw new UnreachableException(),
        };
    }

    sealed class ConstantArraySlice(int startIndex, long capacity, OperandSize operandSize)
    {
        int _reserved;
        readonly List<Entry> _constants = [];

        public void Reserve()
        {
            Debug.Assert(Available > 0);
            _reserved++;
            Debug.Assert(_reserved <= Capacity - _constants.Count);
        }

        public void Unreserve()
        {
            Debug.Assert(_reserved > 0);
            _reserved--;
        }

        public int Allocate(Entry entry, int count = 1)
        {
            Debug.Assert(Available >= count);
            int index = _constants.Count;
            Debug.Assert(index < Capacity);
            for (int i = 0; i < count; ++i) _constants.Add(entry);
            return index + StartIndex;
        }

        public ref Entry At(int index)
        {
            Debug.Assert(index >= StartIndex && index < StartIndex + Size);
            return ref CollectionsMarshal.AsSpan(_constants)[index - StartIndex];
        }

        public long Available => Capacity - Reserved - Size;
        public int Reserved => _reserved;
        public long Capacity => capacity;
        public int Size => _constants.Count;
        public int StartIndex => startIndex;
        public long MaxIndex => StartIndex + Capacity - 1;
        public OperandSize OperandSize => operandSize;
    }

    readonly ConstantArraySlice[] _idxSlice =
    [
        new(0, k8BitCapacity, OperandSize.Byte),
        new(k8BitCapacity, k16BitCapacity, OperandSize.Short),
        new(k8BitCapacity + k16BitCapacity, k32BitCapacity, OperandSize.Quad),
    ];

    // V8 keys AST entries by pointer (constants_map_); identity semantics here.
    readonly Dictionary<object, int> _constantsMap = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, int> _smiMap = [];
    // std::map<double> compares with operator<, so 0.0 and -0.0 share an entry;
    // .NET's double equality and hashing agree (0.0.Equals(-0.0) is true).
    readonly Dictionary<double, int> _heapNumberMap = [];
    readonly int[] _singletonIndices = [-1, -1, -1, -1, -1, -1, -1, -1];

    /// <summary>Returns the number of elements in the array.</summary>
    public int Size()
    {
        int i = _idxSlice.Length;
        while (i > 0)
        {
            ConstantArraySlice slice = _idxSlice[--i];
            if (slice.Size > 0) return slice.StartIndex + slice.Size;
        }
        return _idxSlice[0].Size;
    }

    ConstantArraySlice IndexToSlice(int index)
    {
        foreach (ConstantArraySlice slice in _idxSlice)
        {
            if (index <= slice.MaxIndex) return slice;
        }
        throw new UnreachableException();
    }

    /// <summary>
    /// Returns the object that is in the constant pool array at index |index|,
    /// or null if there is none (or it is still deferred). Only expected to be
    /// used in tests.
    /// </summary>
    public object? At(int index, IConstantPoolMaterializer materializer)
    {
        ConstantArraySlice slice = IndexToSlice(index);
        if (index < slice.StartIndex + slice.Size)
        {
            ref Entry entry = ref slice.At(index);
            if (!entry.IsDeferred) return entry.ToHandle(materializer);
        }
        return null;
    }

    /// <summary>Generate a fixed array of constants based on inserted objects.</summary>
    public object[] ToFixedArray(IConstantPoolMaterializer materializer)
    {
        int array_len = Size();
        var fixed_array = new object[array_len];
        Array.Fill(fixed_array, materializer.TheHole);
        int array_index = 0;
        foreach (ConstantArraySlice slice in _idxSlice)
        {
            Debug.Assert(slice.Reserved == 0);
            Debug.Assert(array_index == 0 || int.IsPow2(array_index));
#if DEBUG
            // Different slices might contain the same element due to reservations, but
            // all elements within a slice should be unique.
            CheckAllElementsAreUnique(slice);
#endif
            // Copy objects from slice into array.
            for (int i = 0; i < slice.Size; ++i)
            {
                fixed_array[array_index++] = slice.At(slice.StartIndex + i).ToHandle(materializer);
            }
            // Leave holes where reservations led to unused slots.
            long padding = slice.Capacity - slice.Size;
            if (array_len - array_index <= padding) break;
            array_index += (int)padding;
        }
        Debug.Assert(array_index >= array_len);
        return fixed_array;
    }

#if DEBUG
    static void CheckAllElementsAreUnique(ConstantArraySlice slice)
    {
        var smis = new HashSet<int>();
        var heap_numbers = new HashSet<double>();
        var objects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < slice.Size; i++)
        {
            ref Entry entry = ref slice.At(slice.StartIndex + i);
            bool duplicate = entry.Tag switch
            {
                Tag.Smi => !smis.Add(entry.SmiValue),
                Tag.HeapNumber => !heap_numbers.Add(entry.HeapNumberValue),
                Tag.RawString or Tag.ConsString or Tag.BigInt or Tag.Scope or Tag.Handle =>
                    !objects.Add(entry.Object!),
                Tag.Deferred => throw new UnreachableException(), // Should be kHandle at this point.
                // Ignore jump tables because they have to be contiguous, so they can
                // contain duplicates. Singletons are non-duplicated by definition.
                _ => false,
            };
            if (duplicate) throw new InvalidOperationException("Duplicate constant found at index " + (slice.StartIndex + i));
        }
    }
#endif

    /// <summary>Insert an object into the constants array if it is not already present.
    /// Returns the array index associated with the object.</summary>
    public int Insert(Smi smi)
    {
        if (_smiMap.TryGetValue(smi.Value, out int index)) return index;
        return AllocateReservedEntry(smi);
    }

    public int Insert(double number)
    {
        if (double.IsNaN(number)) return InsertNaN();
        if (_heapNumberMap.TryGetValue(number, out int existing)) return existing;
        int index = AllocateIndex(Entry.ForHeapNumber(number));
        _heapNumberMap[number] = index;
        return index;
    }

    int InsertKeyed(Tag tag, object key)
    {
        ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_constantsMap, key, out bool exists);
        if (!exists) slot = AllocateIndex(Entry.ForObject(tag, key));
        return slot;
    }

    /// <summary>Insert(const AstRawString*): |rawString| is the AST string object (keyed by identity).</summary>
    public int InsertRawString(object rawString) => InsertKeyed(Tag.RawString, rawString);

    /// <summary>Insert(const AstConsString*).</summary>
    public int InsertConsString(object consString) => InsertKeyed(Tag.ConsString, consString);

    /// <summary>Insert(AstBigInt): V8 keys by the literal's character pointer, so
    /// |bigint| should be an object unique per literal.</summary>
    public int InsertBigInt(object bigint) => InsertKeyed(Tag.BigInt, bigint);

    /// <summary>Insert(const Scope*).</summary>
    public int InsertScope(object scope) => InsertKeyed(Tag.Scope, scope);

    int InsertSingleton(SingletonConstant singleton)
    {
        ref int index = ref _singletonIndices[(int)singleton];
        if (index < 0) index = AllocateIndex(Entry.ForSingleton(singleton));
        return index;
    }

    public int InsertAsyncIteratorSymbol() => InsertSingleton(SingletonConstant.AsyncIteratorSymbol);
    public int InsertClassFieldsSymbol() => InsertSingleton(SingletonConstant.ClassFieldsSymbol);
    public int InsertEmptyObjectBoilerplateDescription() => InsertSingleton(SingletonConstant.EmptyObjectBoilerplateDescription);
    public int InsertEmptyArrayBoilerplateDescription() => InsertSingleton(SingletonConstant.EmptyArrayBoilerplateDescription);
    public int InsertEmptyFixedArray() => InsertSingleton(SingletonConstant.EmptyFixedArray);
    public int InsertIteratorSymbol() => InsertSingleton(SingletonConstant.IteratorSymbol);
    public int InsertInterpreterTrampolineSymbol() => InsertSingleton(SingletonConstant.InterpreterTrampolineSymbol);
    public int InsertNaN() => InsertSingleton(SingletonConstant.NaN);

    /// <summary>Inserts an empty entry and returns the array index associated with the
    /// reservation. The entry's value can be inserted by calling SetDeferredAt().</summary>
    public int InsertDeferred() => AllocateIndex(Entry.Deferred());

    /// <summary>Inserts |size| consecutive empty entries and returns the array index
    /// associated with the first reservation. Each entry's Smi value can be
    /// inserted by calling SetJumpTableSmi().</summary>
    public int InsertJumpTable(int size) => AllocateIndexArray(Entry.UninitializedJumpTableSmi(), size);

    /// <summary>Sets the deferred value at |index| to |obj|.</summary>
    public void SetDeferredAt(int index, object obj)
    {
        ConstantArraySlice slice = IndexToSlice(index);
        slice.At(index).SetDeferred(obj);
    }

    /// <summary>Sets the jump table entry at |index| to |smi|. |index| is the
    /// constant pool index, not the switch case value.</summary>
    public void SetJumpTableSmi(int index, Smi smi)
    {
        ConstantArraySlice slice = IndexToSlice(index);
        // Allow others to reuse these Smis, but insert using emplace to avoid
        // overwriting existing values in the Smi map (which may have a smaller
        // operand size).
        _smiMap.TryAdd(smi.Value, index);
        slice.At(index).SetJumpTableSmi(smi);
    }

    /// <summary>Creates a reserved entry in the constant pool and returns the size
    /// of the operand that'll be required to hold the entry when committed.</summary>
    public OperandSize CreateReservedEntry(OperandSize minimumOperandSize = OperandSize.None)
    {
        foreach (ConstantArraySlice slice in _idxSlice)
        {
            if (slice.Available > 0 && slice.OperandSize >= minimumOperandSize)
            {
                slice.Reserve();
                return slice.OperandSize;
            }
        }
        throw new UnreachableException();
    }

    int AllocateReservedEntry(Smi value)
    {
        int index = AllocateIndex(Entry.ForSmi(value));
        _smiMap[value.Value] = index;
        return index;
    }

    /// <summary>Commit reserved entry and returns the constant pool index for the SMI value.</summary>
    public int CommitReservedEntry(OperandSize operandSize, Smi value)
    {
        DiscardReservedEntry(operandSize);
        int index;
        if (!_smiMap.TryGetValue(value.Value, out index))
        {
            index = AllocateReservedEntry(value);
        }
        else
        {
            ConstantArraySlice slice = OperandSizeToSlice(operandSize);
            if (index > slice.MaxIndex)
            {
                // The object is already in the constant array, but may have an
                // index too big for the reserved operand_size. So, duplicate
                // entry with the smaller operand size.
                index = AllocateReservedEntry(value);
            }
            Debug.Assert(index <= slice.MaxIndex);
        }
        return index;
    }

    /// <summary>Discards constant pool reservation.</summary>
    public void DiscardReservedEntry(OperandSize operandSize) => OperandSizeToSlice(operandSize).Unreserve();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int AllocateIndex(Entry constantEntry) => AllocateIndexArray(constantEntry, 1);

    int AllocateIndexArray(Entry constantEntry, int count)
    {
        foreach (ConstantArraySlice slice in _idxSlice)
        {
            if (slice.Available >= count) return slice.Allocate(constantEntry, count);
        }
        throw new UnreachableException();
    }

    ConstantArraySlice OperandSizeToSlice(OperandSize operandSize)
    {
        ConstantArraySlice slice = operandSize switch
        {
            OperandSize.Byte => _idxSlice[0],
            OperandSize.Short => _idxSlice[1],
            OperandSize.Quad => _idxSlice[2],
            _ => throw new UnreachableException(),
        };
        Debug.Assert(slice.OperandSize == operandSize);
        return slice;
    }
}
