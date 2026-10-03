// The ICU property lookups of src/regexp/regexp-parser.cc (the
// V8_INTL_SUPPORT branch): u_getPropertyEnum / u_getPropertyValueEnum with
// V8's exact-alias checks, UnicodeSet::applyIntPropertyValue and the
// IsSupportedBinaryProperty allowlist, answered from the generated tables.

namespace V8Sharp.RegExp.Unicode;

internal static class UnicodeProperties
{
    public enum PropertyKind { GeneralCategoryMask, Script, ScriptExtensions, Binary, BinaryOfStrings }

    /// <summary>A resolved property: which table and which entry.</summary>
    public readonly record struct Property(PropertyKind Kind, int Index);

    static bool NameIs(string[] names, string name)
    {
        foreach (string n in names)
        {
            if (string.Equals(n, name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// u_getPropertyEnum + IsExactPropertyAlias for an enumerated property
    /// name as used in \p{name=value}: General_Category, Script or
    /// Script_Extensions. Returns null if the name is not an exact alias of
    /// one of them.
    /// </summary>
    public static PropertyKind? LookupEnumeratedProperty(string name) => name switch
    {
        "General_Category" or "gc" => PropertyKind.GeneralCategoryMask,
        "Script" or "sc" => PropertyKind.Script,
        "Script_Extensions" or "scx" => PropertyKind.ScriptExtensions,
        _ => null,
    };

    /// <summary>
    /// u_getPropertyEnum + IsSupportedBinaryProperty + IsExactPropertyAlias
    /// for a lone \p{name}. Properties of strings are only supported with /v.
    /// </summary>
    public static Property? LookupBinaryProperty(string name, bool unicodeSets)
    {
        var props = UnicodeTables.BinaryProperties;
        for (int i = 0; i < props.Length; i++)
        {
            if (NameIs(props[i].Names, name)) return new Property(PropertyKind.Binary, i);
        }
        var strings = UnicodeTables.StringProperties;
        for (int i = 0; i < strings.Length; i++)
        {
            if (string.Equals(strings[i].Name, name, StringComparison.Ordinal))
            {
                return unicodeSets ? new Property(PropertyKind.BinaryOfStrings, i) : null;
            }
        }
        return null;
    }

    /// <summary>
    /// u_getPropertyValueEnum + IsExactPropertyValueAlias, then
    /// applyIntPropertyValue. Returns the code point ranges (flat pairs) and,
    /// for properties of strings, the strings; or false if the value name is
    /// not an exact alias.
    /// </summary>
    public static bool TryGetPropertyValueSet(PropertyKind kind, int binaryIndex, string valueName,
        out int[] ranges, out int[][]? strings)
    {
        strings = null;
        ranges = [];
        switch (kind)
        {
            case PropertyKind.GeneralCategoryMask:
                foreach (var (names, offset) in UnicodeTables.GeneralCategoryValues)
                {
                    if (NameIs(names, valueName))
                    {
                        ranges = UnicodeTables.GetRanges(offset);
                        return true;
                    }
                }
                return false;
            case PropertyKind.Script:
            case PropertyKind.ScriptExtensions:
                // For the property Script_Extensions, the property value name
                // lookup is done as if the property is Script.
                foreach (var (names, sc, scx) in UnicodeTables.ScriptValues)
                {
                    if (NameIs(names, valueName))
                    {
                        ranges = UnicodeTables.GetRanges(kind == PropertyKind.Script ? sc : scx);
                        return true;
                    }
                }
                return false;
            case PropertyKind.Binary:
            {
                // Binary property values: Y/Yes/T/True and N/No/F/False.
                bool? value = valueName switch
                {
                    "Y" or "Yes" or "T" or "True" => true,
                    "N" or "No" or "F" or "False" => false,
                    _ => null,
                };
                if (value is null) return false;
                int[] set = UnicodeTables.GetRanges(UnicodeTables.BinaryProperties[binaryIndex].Offset);
                if (value.Value)
                {
                    ranges = set;
                }
                else
                {
                    var complement = CodePointSet.FromRanges(set);
                    complement.Complement();
                    ranges = ToFlat(complement);
                }
                return true;
            }
            case PropertyKind.BinaryOfStrings:
            {
                if (valueName is not ("Y" or "Yes" or "T" or "True")) return false;
                var (_, rangesOffset, stringsOffset) = UnicodeTables.StringProperties[binaryIndex];
                ranges = UnicodeTables.GetRanges(rangesOffset);
                strings = UnicodeTables.GetStrings(stringsOffset);
                return true;
            }
        }
        return false;
    }

    public static int[] ToFlat(CodePointSet set)
    {
        int[] result = new int[set.RangeCount * 2];
        for (int i = 0; i < set.RangeCount; i++)
        {
            result[2 * i] = set.GetRangeStart(i);
            result[2 * i + 1] = set.GetRangeEnd(i);
        }
        return result;
    }
}
