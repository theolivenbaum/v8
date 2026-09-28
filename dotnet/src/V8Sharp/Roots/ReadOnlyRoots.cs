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
}
