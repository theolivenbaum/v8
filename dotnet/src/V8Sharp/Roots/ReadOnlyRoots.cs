// Port of the non-string read-only roots of src/roots/roots.h that the object
// model uses (the strings and symbols are generated from heap-symbols.h into
// ReadOnlyRoots.Generated.cs).
using V8Sharp.Objects;

namespace V8Sharp.Roots;

public static partial class ReadOnlyRoots
{
    /// <summary><c>empty_fixed_array</c></summary>
    public static FixedArray empty_fixed_array => FixedArray.Empty;

    /// <summary><c>empty_byte_array</c></summary>
    public static ByteArray empty_byte_array => ByteArray.Empty;

    /// <summary><c>empty_descriptor_array</c></summary>
    public static DescriptorArray empty_descriptor_array => DescriptorArray.Empty;

    /// <summary>
    /// <c>empty_slow_element_dictionary</c>: capacity 1 and requires-slow-elements
    /// set, so every addition allocates a new dictionary (V8 keeps it in
    /// read-only space for the same reason).
    /// </summary>
    public static readonly NumberDictionary empty_slow_element_dictionary = CreateEmptySlowElementDictionary();

    static NumberDictionary CreateEmptySlowElementDictionary()
    {
        NumberDictionary dictionary = NumberDictionary.New(1, useCustomMinimumCapacity: true);
        dictionary.SetRequiresSlowElements();
        return dictionary;
    }

    /// <summary><c>empty_ordered_hash_map</c>: the zero-capacity OrderedHashMap.</summary>
    public static readonly OrderedHashMap empty_ordered_hash_map = OrderedHashMap.AllocateEmpty();

    /// <summary><c>empty_ordered_hash_set</c>: the zero-capacity OrderedHashSet.</summary>
    public static readonly OrderedHashSet empty_ordered_hash_set = OrderedHashSet.AllocateEmpty();

    /// <summary>
    /// <c>preallocated_number_string_table</c>: the internalized strings of
    /// 0..kPreallocatedNumberStringTableSize-1, the single-digit ones shared
    /// with the single character string table (setup-heap-internal.cc).
    /// </summary>
    public static SeqString[] preallocated_number_string_table => PreallocatedNumberStrings.Table;

    // A holder class, so the table is built after the generated roots it
    // shares (static initializers of a partial class run in an unspecified
    // file order).
    static class PreallocatedNumberStrings
    {
        public static readonly SeqString[] Table = Create();

        static SeqString[] Create()
        {
            var table = new SeqString[Factory.kPreallocatedNumberStringTableSize];
            for (int i = 0; i < 10; i++) table[i] = SingleCharacterStringTable['0' + i];
            for (int i = 10; i < table.Length; i++) table[i] = RootsBuilder.String(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return table;
        }
    }
}
