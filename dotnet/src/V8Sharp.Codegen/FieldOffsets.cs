// Field offsets of managed objects as native code addresses them: from the
// object's address (its method table pointer) to a field. V8 knows its
// layouts statically (the Torque class definitions); V8Sharp measures the
// CLR's layout of its classes at run time.
using System.Runtime.CompilerServices;

namespace V8Sharp.Codegen;

public static class FieldOffsets
{
    sealed class RawObject
    {
        // Only its address is used: the first field of any object.
#pragma warning disable CS0649
        public byte Data;
#pragma warning restore CS0649
    }

    /// <summary>The offset of <paramref name="field"/> (a field of <paramref name="obj"/>) from the object's address.</summary>
    public static int Of<TField>(object obj, ref TField field) =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<RawObject>(obj).Data, ref Unsafe.As<TField, byte>(ref field)) + IntPtr.Size;

    /// <summary>The distance in bytes from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static int Distance<TA, TB>(ref TA a, ref TB b) =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<TA, byte>(ref a), ref Unsafe.As<TB, byte>(ref b));
}
