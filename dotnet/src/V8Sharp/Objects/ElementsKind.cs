// Port of src/objects/elements-kind.{h,cc}: the elements-kind lattice.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>V8's ElementsKind (member names and order are V8's).</summary>
public enum ElementsKind : byte
{
    PACKED_SMI_ELEMENTS,
    HOLEY_SMI_ELEMENTS,
    PACKED_ELEMENTS,
    HOLEY_ELEMENTS,
    PACKED_DOUBLE_ELEMENTS,
    HOLEY_DOUBLE_ELEMENTS,
    PACKED_NONEXTENSIBLE_ELEMENTS,
    HOLEY_NONEXTENSIBLE_ELEMENTS,
    PACKED_SEALED_ELEMENTS,
    HOLEY_SEALED_ELEMENTS,
    PACKED_FROZEN_ELEMENTS,
    HOLEY_FROZEN_ELEMENTS,
    SHARED_ARRAY_ELEMENTS,
    DICTIONARY_ELEMENTS,
    FAST_SLOPPY_ARGUMENTS_ELEMENTS,
    SLOW_SLOPPY_ARGUMENTS_ELEMENTS,
    FAST_STRING_WRAPPER_ELEMENTS,
    SLOW_STRING_WRAPPER_ELEMENTS,
    UINT8_ELEMENTS,
    INT8_ELEMENTS,
    UINT16_ELEMENTS,
    INT16_ELEMENTS,
    UINT32_ELEMENTS,
    INT32_ELEMENTS,
    BIGUINT64_ELEMENTS,
    BIGINT64_ELEMENTS,
    UINT8_CLAMPED_ELEMENTS,
    FLOAT32_ELEMENTS,
    FLOAT64_ELEMENTS,
    FLOAT16_ELEMENTS,
    RAB_GSAB_UINT8_ELEMENTS,
    RAB_GSAB_INT8_ELEMENTS,
    RAB_GSAB_UINT16_ELEMENTS,
    RAB_GSAB_INT16_ELEMENTS,
    RAB_GSAB_UINT32_ELEMENTS,
    RAB_GSAB_INT32_ELEMENTS,
    RAB_GSAB_BIGUINT64_ELEMENTS,
    RAB_GSAB_BIGINT64_ELEMENTS,
    RAB_GSAB_UINT8_CLAMPED_ELEMENTS,
    RAB_GSAB_FLOAT32_ELEMENTS,
    RAB_GSAB_FLOAT64_ELEMENTS,
    RAB_GSAB_FLOAT16_ELEMENTS,
    WASM_ARRAY_ELEMENTS,
    NO_ELEMENTS,

    FIRST_ELEMENTS_KIND = PACKED_SMI_ELEMENTS,
    LAST_ELEMENTS_KIND = RAB_GSAB_FLOAT16_ELEMENTS,
    FIRST_FAST_ELEMENTS_KIND = PACKED_SMI_ELEMENTS,
    LAST_FAST_ELEMENTS_KIND = HOLEY_DOUBLE_ELEMENTS,
    FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND = UINT8_ELEMENTS,
    LAST_FIXED_TYPED_ARRAY_ELEMENTS_KIND = FLOAT16_ELEMENTS,
    FIRST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND = RAB_GSAB_UINT8_ELEMENTS,
    LAST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND = RAB_GSAB_FLOAT16_ELEMENTS,
    TERMINAL_FAST_ELEMENTS_KIND = HOLEY_ELEMENTS,
    FIRST_ANY_NONEXTENSIBLE_ELEMENTS_KIND = PACKED_NONEXTENSIBLE_ELEMENTS,
    LAST_ANY_NONEXTENSIBLE_ELEMENTS_KIND = SHARED_ARRAY_ELEMENTS,
    FIRST_VALID_ATOMICS_TYPED_ARRAY_ELEMENTS_KIND = UINT8_ELEMENTS,
    LAST_VALID_ATOMICS_TYPED_ARRAY_ELEMENTS_KIND = BIGINT64_ELEMENTS,
}

/// <summary>The elements-kind predicates and lattice operations (elements-kind.h).</summary>
public static class ElementsKinds
{
    public const int kElementsKindCount = ElementsKind.LAST_ELEMENTS_KIND - ElementsKind.FIRST_ELEMENTS_KIND + 1;
    public const int kFastElementsKindCount = ElementsKind.LAST_FAST_ELEMENTS_KIND - ElementsKind.FIRST_FAST_ELEMENTS_KIND + 1;
    public const int kFastElementsKindPackedToHoley = ElementsKind.HOLEY_SMI_ELEMENTS - ElementsKind.PACKED_SMI_ELEMENTS;
    public const int kElementsKindBits = 6;

    static readonly ElementsKind[] kFastElementsKindSequence =
    [
        ElementsKind.PACKED_SMI_ELEMENTS,
        ElementsKind.HOLEY_SMI_ELEMENTS,
        ElementsKind.PACKED_DOUBLE_ELEMENTS,
        ElementsKind.HOLEY_DOUBLE_ELEMENTS,
        ElementsKind.PACKED_ELEMENTS,
        ElementsKind.HOLEY_ELEMENTS,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool InRange(ElementsKind k, ElementsKind lo, ElementsKind hi) => (uint)(k - lo) <= (uint)(hi - lo);

    public static int ElementsKindToShiftSize(ElementsKind kind)
    {
        switch (kind)
        {
            case ElementsKind.UINT8_ELEMENTS:
            case ElementsKind.INT8_ELEMENTS:
            case ElementsKind.UINT8_CLAMPED_ELEMENTS:
            case ElementsKind.RAB_GSAB_UINT8_ELEMENTS:
            case ElementsKind.RAB_GSAB_INT8_ELEMENTS:
            case ElementsKind.RAB_GSAB_UINT8_CLAMPED_ELEMENTS:
                return 0;
            case ElementsKind.UINT16_ELEMENTS:
            case ElementsKind.INT16_ELEMENTS:
            case ElementsKind.FLOAT16_ELEMENTS:
            case ElementsKind.RAB_GSAB_FLOAT16_ELEMENTS:
            case ElementsKind.RAB_GSAB_UINT16_ELEMENTS:
            case ElementsKind.RAB_GSAB_INT16_ELEMENTS:
                return 1;
            case ElementsKind.UINT32_ELEMENTS:
            case ElementsKind.INT32_ELEMENTS:
            case ElementsKind.FLOAT32_ELEMENTS:
            case ElementsKind.RAB_GSAB_UINT32_ELEMENTS:
            case ElementsKind.RAB_GSAB_INT32_ELEMENTS:
            case ElementsKind.RAB_GSAB_FLOAT32_ELEMENTS:
                return 2;
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
            case ElementsKind.FLOAT64_ELEMENTS:
            case ElementsKind.BIGINT64_ELEMENTS:
            case ElementsKind.BIGUINT64_ELEMENTS:
            case ElementsKind.RAB_GSAB_FLOAT64_ELEMENTS:
            case ElementsKind.RAB_GSAB_BIGINT64_ELEMENTS:
            case ElementsKind.RAB_GSAB_BIGUINT64_ELEMENTS:
                return 3;
            case ElementsKind.WASM_ARRAY_ELEMENTS:
            case ElementsKind.NO_ELEMENTS:
                throw new InvalidOperationException("unreachable");
            default:
                return 2; // kTaggedSizeLog2 with pointer compression
        }
    }

    public static int ElementsKindToByteSize(ElementsKind kind) => 1 << ElementsKindToShiftSize(kind);

    public static string ElementsKindToString(ElementsKind kind) => kind switch
    {
        >= ElementsKind.UINT8_ELEMENTS and <= ElementsKind.RAB_GSAB_FLOAT16_ELEMENTS =>
            // V8 prints the typed-array kinds as TYPE "ELEMENTS" (no underscore).
            kind.ToString().Replace("_ELEMENTS", "ELEMENTS", StringComparison.Ordinal),
        _ => kind.ToString(),
    };

    public static ElementsKind GetInitialFastElementsKind() => ElementsKind.PACKED_SMI_ELEMENTS;

    public static ElementsKind GetFastElementsKindFromSequenceIndex(int sequenceNumber) => kFastElementsKindSequence[sequenceNumber];

    public static int GetSequenceIndexFromFastElementsKind(ElementsKind kind)
    {
        for (int i = 0; i < kFastElementsKindCount; ++i)
        {
            if (kFastElementsKindSequence[i] == kind) return i;
        }
        throw new InvalidOperationException("unreachable");
    }

    public static ElementsKind GetNextTransitionElementsKind(ElementsKind kind) =>
        GetFastElementsKindFromSequenceIndex(GetSequenceIndexFromFastElementsKind(kind) + 1);

    public static bool IsDictionaryElementsKind(ElementsKind kind) => kind == ElementsKind.DICTIONARY_ELEMENTS;
    public static bool IsFastArgumentsElementsKind(ElementsKind kind) => kind == ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS;
    public static bool IsSlowArgumentsElementsKind(ElementsKind kind) => kind == ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS;
    public static bool IsSloppyArgumentsElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS, ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS);
    public static bool IsStringWrapperElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.FAST_STRING_WRAPPER_ELEMENTS, ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS);
    public static bool IsTypedArrayElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND, ElementsKind.LAST_FIXED_TYPED_ARRAY_ELEMENTS_KIND);
    public static bool IsRabGsabTypedArrayElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.FIRST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND, ElementsKind.LAST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND);
    public static bool IsTypedArrayOrRabGsabTypedArrayElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND, ElementsKind.LAST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND);
    public static bool IsBigIntTypedArrayElementsKind(ElementsKind kind) =>
        kind is ElementsKind.BIGINT64_ELEMENTS or ElementsKind.BIGUINT64_ELEMENTS or
            ElementsKind.RAB_GSAB_BIGINT64_ELEMENTS or ElementsKind.RAB_GSAB_BIGUINT64_ELEMENTS;
    public static bool IsFloat16TypedArrayElementsKind(ElementsKind kind) =>
        kind is ElementsKind.FLOAT16_ELEMENTS or ElementsKind.RAB_GSAB_FLOAT16_ELEMENTS;
    public static bool IsFloatTypedArrayElementsKind(ElementsKind kind) =>
        kind is ElementsKind.FLOAT16_ELEMENTS or ElementsKind.RAB_GSAB_FLOAT16_ELEMENTS or
            ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS or
            ElementsKind.RAB_GSAB_FLOAT32_ELEMENTS or ElementsKind.RAB_GSAB_FLOAT64_ELEMENTS;
    public static bool IsSignedIntTypedArrayElementsKind(ElementsKind kind) =>
        kind is ElementsKind.INT8_ELEMENTS or ElementsKind.RAB_GSAB_INT8_ELEMENTS or
            ElementsKind.INT16_ELEMENTS or ElementsKind.RAB_GSAB_INT16_ELEMENTS or
            ElementsKind.INT32_ELEMENTS or ElementsKind.RAB_GSAB_INT32_ELEMENTS;
    public static bool IsUnsignedIntTypedArrayElementsKind(ElementsKind kind) =>
        kind is ElementsKind.UINT8_CLAMPED_ELEMENTS or ElementsKind.RAB_GSAB_UINT8_CLAMPED_ELEMENTS or
            ElementsKind.UINT8_ELEMENTS or ElementsKind.RAB_GSAB_UINT8_ELEMENTS or
            ElementsKind.UINT16_ELEMENTS or ElementsKind.RAB_GSAB_UINT16_ELEMENTS or
            ElementsKind.UINT32_ELEMENTS or ElementsKind.RAB_GSAB_UINT32_ELEMENTS;
    public static bool IsWasmArrayElementsKind(ElementsKind kind) => kind == ElementsKind.WASM_ARRAY_ELEMENTS;
    public static bool IsSharedArrayElementsKind(ElementsKind kind) => kind == ElementsKind.SHARED_ARRAY_ELEMENTS;

    public static bool IsTerminalElementsKind(ElementsKind kind) =>
        kind == ElementsKind.TERMINAL_FAST_ELEMENTS_KIND || IsTypedArrayOrRabGsabTypedArrayElementsKind(kind);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFastElementsKind(ElementsKind kind) => kind <= ElementsKind.LAST_FAST_ELEMENTS_KIND;

    public static bool IsTransitionElementsKind(ElementsKind kind) =>
        IsFastElementsKind(kind) || IsTypedArrayOrRabGsabTypedArrayElementsKind(kind) ||
        kind == ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS || kind == ElementsKind.FAST_STRING_WRAPPER_ELEMENTS;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDoubleElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.PACKED_DOUBLE_ELEMENTS, ElementsKind.HOLEY_DOUBLE_ELEMENTS);

    public static bool IsAnyNonextensibleElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.FIRST_ANY_NONEXTENSIBLE_ELEMENTS_KIND, ElementsKind.LAST_ANY_NONEXTENSIBLE_ELEMENTS_KIND);
    public static bool IsNonextensibleElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS, ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS);
    public static bool IsSealedElementsKind(ElementsKind kind) =>
        IsSharedArrayElementsKind(kind) || InRange(kind, ElementsKind.PACKED_SEALED_ELEMENTS, ElementsKind.HOLEY_SEALED_ELEMENTS);
    public static bool IsFrozenElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.PACKED_FROZEN_ELEMENTS, ElementsKind.HOLEY_FROZEN_ELEMENTS);
    public static bool IsFastOrNonextensibleOrSealedElementsKind(ElementsKind kind) => kind <= ElementsKind.HOLEY_SEALED_ELEMENTS;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSmiOrObjectElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.PACKED_SMI_ELEMENTS, ElementsKind.HOLEY_ELEMENTS);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSmiElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.PACKED_SMI_ELEMENTS, ElementsKind.HOLEY_SMI_ELEMENTS);
    public static bool IsFastNumberElementsKind(ElementsKind kind) => IsSmiElementsKind(kind) || IsDoubleElementsKind(kind);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsObjectElementsKind(ElementsKind kind) =>
        InRange(kind, ElementsKind.PACKED_ELEMENTS, ElementsKind.HOLEY_ELEMENTS);
    public static bool IsAnyHoleyNonextensibleElementsKind(ElementsKind kind) =>
        kind is ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS or ElementsKind.HOLEY_SEALED_ELEMENTS or ElementsKind.HOLEY_FROZEN_ELEMENTS;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHoleyElementsKind(ElementsKind kind) => ((int)kind & 1) == 1 && kind <= ElementsKind.HOLEY_DOUBLE_ELEMENTS;
    public static bool IsHoleyElementsKindForRead(ElementsKind kind) => ((int)kind & 1) == 1 && kind <= ElementsKind.HOLEY_FROZEN_ELEMENTS;
    public static bool IsHoleyOrDictionaryElementsKind(ElementsKind kind) =>
        IsHoleyElementsKindForRead(kind) || kind == ElementsKind.DICTIONARY_ELEMENTS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFastPackedElementsKind(ElementsKind kind) => ((int)kind & 1) == 0 && kind <= ElementsKind.PACKED_DOUBLE_ELEMENTS;

    public static ElementsKind GetPackedElementsKind(ElementsKind holeyKind) => holeyKind switch
    {
        ElementsKind.HOLEY_SMI_ELEMENTS => ElementsKind.PACKED_SMI_ELEMENTS,
        ElementsKind.HOLEY_DOUBLE_ELEMENTS => ElementsKind.PACKED_DOUBLE_ELEMENTS,
        ElementsKind.HOLEY_ELEMENTS => ElementsKind.PACKED_ELEMENTS,
        _ => holeyKind,
    };

    public static ElementsKind GetHoleyElementsKind(ElementsKind packedKind) => packedKind switch
    {
        ElementsKind.PACKED_SMI_ELEMENTS => ElementsKind.HOLEY_SMI_ELEMENTS,
        ElementsKind.PACKED_DOUBLE_ELEMENTS => ElementsKind.HOLEY_DOUBLE_ELEMENTS,
        ElementsKind.PACKED_ELEMENTS => ElementsKind.HOLEY_ELEMENTS,
        ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS => ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS,
        _ => packedKind,
    };

    public static ElementsKind GetCorrespondingRabGsabElementsKind(ElementsKind kind) =>
        kind - ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND + ElementsKind.FIRST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND;

    public static ElementsKind GetCorrespondingNonRabGsabElementsKind(ElementsKind kind) =>
        kind - ElementsKind.FIRST_RAB_GSAB_FIXED_TYPED_ARRAY_ELEMENTS_KIND + ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND;

    public static bool UnionElementsKindUptoPackedness(ref ElementsKind a, ElementsKind b)
    {
        switch (a)
        {
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            case ElementsKind.PACKED_SMI_ELEMENTS:
                if (b is ElementsKind.PACKED_SMI_ELEMENTS or ElementsKind.HOLEY_SMI_ELEMENTS)
                {
                    a = a > b ? a : b;
                    return true;
                }
                break;
            case ElementsKind.PACKED_ELEMENTS:
            case ElementsKind.HOLEY_ELEMENTS:
                if (b is ElementsKind.PACKED_ELEMENTS or ElementsKind.HOLEY_ELEMENTS)
                {
                    a = a > b ? a : b;
                    return true;
                }
                break;
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                if (b is ElementsKind.PACKED_DOUBLE_ELEMENTS or ElementsKind.HOLEY_DOUBLE_ELEMENTS)
                {
                    a = a > b ? a : b;
                    return true;
                }
                break;
        }
        return false;
    }

    public static bool UnionElementsKindUptoSize(ref ElementsKind a, ElementsKind b)
    {
        switch (a)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
                switch (b)
                {
                    case ElementsKind.PACKED_SMI_ELEMENTS:
                    case ElementsKind.HOLEY_SMI_ELEMENTS:
                    case ElementsKind.PACKED_ELEMENTS:
                    case ElementsKind.HOLEY_ELEMENTS:
                        a = b;
                        return true;
                    default:
                        return false;
                }
            case ElementsKind.HOLEY_SMI_ELEMENTS:
                switch (b)
                {
                    case ElementsKind.PACKED_SMI_ELEMENTS:
                    case ElementsKind.HOLEY_SMI_ELEMENTS:
                        a = ElementsKind.HOLEY_SMI_ELEMENTS;
                        return true;
                    case ElementsKind.PACKED_ELEMENTS:
                    case ElementsKind.HOLEY_ELEMENTS:
                        a = ElementsKind.HOLEY_ELEMENTS;
                        return true;
                    default:
                        return false;
                }
            case ElementsKind.PACKED_ELEMENTS:
                switch (b)
                {
                    case ElementsKind.PACKED_SMI_ELEMENTS:
                    case ElementsKind.PACKED_ELEMENTS:
                        a = ElementsKind.PACKED_ELEMENTS;
                        return true;
                    case ElementsKind.HOLEY_SMI_ELEMENTS:
                    case ElementsKind.HOLEY_ELEMENTS:
                        a = ElementsKind.HOLEY_ELEMENTS;
                        return true;
                    default:
                        return false;
                }
            case ElementsKind.HOLEY_ELEMENTS:
                switch (b)
                {
                    case ElementsKind.PACKED_SMI_ELEMENTS:
                    case ElementsKind.HOLEY_SMI_ELEMENTS:
                    case ElementsKind.PACKED_ELEMENTS:
                    case ElementsKind.HOLEY_ELEMENTS:
                        a = ElementsKind.HOLEY_ELEMENTS;
                        return true;
                    default:
                        return false;
                }
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                switch (b)
                {
                    case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                    case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                        a = b;
                        return true;
                    default:
                        return false;
                }
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                switch (b)
                {
                    case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                    case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                        a = ElementsKind.HOLEY_DOUBLE_ELEMENTS;
                        return true;
                    default:
                        return false;
                }
        }
        return false;
    }

    public static ElementsKind FastSmiToObjectElementsKind(ElementsKind fromKind) =>
        fromKind == ElementsKind.PACKED_SMI_ELEMENTS ? ElementsKind.PACKED_ELEMENTS : ElementsKind.HOLEY_ELEMENTS;

    public static bool IsSimpleMapChangeTransition(ElementsKind fromKind, ElementsKind toKind) =>
        GetHoleyElementsKind(fromKind) == toKind || (IsSmiElementsKind(fromKind) && IsObjectElementsKind(toKind));

    static bool IsFastTransitionTarget(ElementsKind kind) => IsFastElementsKind(kind) || kind == ElementsKind.DICTIONARY_ELEMENTS;

    public static bool IsMoreGeneralElementsKindTransition(ElementsKind fromKind, ElementsKind toKind)
    {
        if (!IsFastElementsKind(fromKind)) return false;
        if (!IsFastTransitionTarget(toKind)) return false;
        switch (fromKind)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
                return toKind != ElementsKind.PACKED_SMI_ELEMENTS;
            case ElementsKind.HOLEY_SMI_ELEMENTS:
                return toKind != ElementsKind.PACKED_SMI_ELEMENTS && toKind != ElementsKind.HOLEY_SMI_ELEMENTS;
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                return toKind != ElementsKind.PACKED_SMI_ELEMENTS && toKind != ElementsKind.HOLEY_SMI_ELEMENTS &&
                       toKind != ElementsKind.PACKED_DOUBLE_ELEMENTS;
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                return toKind is ElementsKind.PACKED_ELEMENTS or ElementsKind.HOLEY_ELEMENTS;
            case ElementsKind.PACKED_ELEMENTS:
                return toKind == ElementsKind.HOLEY_ELEMENTS;
            default:
                return false;
        }
    }

    public static ElementsKind GetMoreGeneralElementsKind(ElementsKind fromKind, ElementsKind toKind) =>
        IsMoreGeneralElementsKindTransition(fromKind, toKind) ? toKind : fromKind;

    public static bool IsTransitionableFastElementsKind(ElementsKind fromKind) =>
        IsFastElementsKind(fromKind) && fromKind != ElementsKind.TERMINAL_FAST_ELEMENTS_KIND;
}
