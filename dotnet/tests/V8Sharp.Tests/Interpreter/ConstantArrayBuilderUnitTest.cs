// Port of test/unittests/interpreter/constant-array-builder-unittest.cc.
//
// Heap constants are the DefaultConstantPoolMaterializer's: Smi values are
// boxed Smi, heap numbers boxed double, the hole a sentinel object.
// Object::SameValue / NumberValue are replaced by NumberValue() below.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class ConstantArrayBuilderUnitTest
{
    const int k8BitCapacity = ConstantArrayBuilder.k8BitCapacity;
    const int k16BitCapacity = ConstantArrayBuilder.k16BitCapacity;

    static readonly IConstantPoolMaterializer isolate = DefaultConstantPoolMaterializer.Instance;

    static double NumberValue(object? o) => o switch
    {
        Smi s => s.Value,
        double d => d,
        _ => throw new InvalidOperationException("not a number: " + o),
    };

    static bool SameNumber(object? value, double expected) =>
        value is Smi or double && NumberValue(value).Equals(expected);

    static bool IsTheHole(object? o) => ReferenceEquals(o, isolate.TheHole);

    [Fact]
    public void ConstantArrayBuilderTest_AllocateAllEntries()
    {
        var builder = new ConstantArrayBuilder();
        for (int i = 0; i < k16BitCapacity; i++) builder.Insert(i + 0.5);
        Assert.Equal(k16BitCapacity, builder.Size());
        for (int i = 0; i < k16BitCapacity; i++)
        {
            Assert.Equal(i + 0.5, (double)builder.At(i, isolate)!);
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_ToFixedArray()
    {
        var builder = new ConstantArrayBuilder();
        const int kNumberOfElements = 37;
        for (int i = 0; i < kNumberOfElements; i++) builder.Insert(i + 0.5);
        object[] constant_array = builder.ToFixedArray(isolate);
        Assert.Equal(kNumberOfElements, constant_array.Length);
        for (int i = 0; i < kNumberOfElements; i++)
        {
            Assert.Equal(NumberValue(builder.At(i, isolate)), NumberValue(constant_array[i]));
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_ToLargeFixedArray()
    {
        var builder = new ConstantArrayBuilder();
        const int kNumberOfElements = 37373;
        for (int i = 0; i < kNumberOfElements; i++) builder.Insert(i + 0.5);
        object[] constant_array = builder.ToFixedArray(isolate);
        Assert.Equal(kNumberOfElements, constant_array.Length);
        for (int i = 0; i < kNumberOfElements; i++)
        {
            Assert.Equal(NumberValue(builder.At(i, isolate)), NumberValue(constant_array[i]));
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_ToLargeFixedArrayWithReservations()
    {
        var builder = new ConstantArrayBuilder();
        const int kNumberOfElements = 37373;
        for (int i = 0; i < kNumberOfElements; i++)
        {
            builder.CommitReservedEntry(builder.CreateReservedEntry(), Smi.FromInt(i));
        }
        object[] constant_array = builder.ToFixedArray(isolate);
        Assert.Equal(kNumberOfElements, constant_array.Length);
        for (int i = 0; i < kNumberOfElements; i++)
        {
            Assert.Equal(NumberValue(builder.At(i, isolate)), NumberValue(constant_array[i]));
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_AllocateEntriesWithIdx8Reservations()
    {
        for (int reserved = 1; reserved < k8BitCapacity; reserved *= 3)
        {
            var builder = new ConstantArrayBuilder();
            for (int i = 0; i < reserved; i++)
            {
                OperandSize operand_size = builder.CreateReservedEntry();
                Assert.Equal(OperandSize.Byte, operand_size);
            }
            for (int i = 0; i < 2 * k8BitCapacity; i++)
            {
                builder.CommitReservedEntry(builder.CreateReservedEntry(), Smi.FromInt(i));
                if (i + reserved < k8BitCapacity)
                {
                    Assert.True(builder.Size() <= k8BitCapacity);
                    Assert.Equal(i + 1, builder.Size());
                }
                else
                {
                    Assert.True(builder.Size() >= k8BitCapacity);
                    Assert.Equal(i + reserved + 1, builder.Size());
                }
            }
            Assert.Equal(2 * k8BitCapacity + reserved, builder.Size());

            // Commit reserved entries with duplicates and check size does not change.
            Assert.Equal(reserved + 2 * k8BitCapacity, builder.Size());
            int duplicates_in_idx8_space = Math.Min(reserved, k8BitCapacity - reserved);
            for (int i = 0; i < duplicates_in_idx8_space; i++)
            {
                builder.CommitReservedEntry(OperandSize.Byte, Smi.FromInt(i));
                Assert.Equal(reserved + 2 * k8BitCapacity, builder.Size());
            }

            // Now make reservations, and commit them with unique entries.
            for (int i = 0; i < duplicates_in_idx8_space; i++)
            {
                OperandSize operand_size = builder.CreateReservedEntry();
                Assert.Equal(OperandSize.Byte, operand_size);
            }
            for (int i = 0; i < duplicates_in_idx8_space; i++)
            {
                Smi value = Smi.FromInt(2 * k8BitCapacity + i);
                int index = builder.CommitReservedEntry(OperandSize.Byte, value);
                Assert.Equal(k8BitCapacity - reserved + i, index);
            }

            // Clear any remaining uncommited reservations.
            for (int i = 0; i < reserved - duplicates_in_idx8_space; i++)
            {
                builder.DiscardReservedEntry(OperandSize.Byte);
            }

            object[] constant_array = builder.ToFixedArray(isolate);
            Assert.Equal(2 * k8BitCapacity + reserved, constant_array.Length);

            // Check all committed values match expected
            for (int i = 0; i < k8BitCapacity - reserved; i++)
            {
                Assert.True(SameNumber(constant_array[i], i));
            }
            for (int i = k8BitCapacity; i < 2 * k8BitCapacity + reserved; i++)
            {
                Assert.True(SameNumber(constant_array[i], i - reserved));
            }
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_AllocateEntriesWithWideReservations()
    {
        for (int reserved = 1; reserved < k8BitCapacity; reserved *= 3)
        {
            var builder = new ConstantArrayBuilder();
            for (int i = 0; i < k8BitCapacity; i++)
            {
                builder.CommitReservedEntry(builder.CreateReservedEntry(), Smi.FromInt(i));
                Assert.Equal(i + 1, builder.Size());
            }
            for (int i = 0; i < reserved; i++)
            {
                OperandSize operand_size = builder.CreateReservedEntry();
                Assert.Equal(OperandSize.Short, operand_size);
                Assert.Equal(k8BitCapacity, builder.Size());
            }
            for (int i = 0; i < reserved; i++)
            {
                builder.DiscardReservedEntry(OperandSize.Short);
                Assert.Equal(k8BitCapacity, builder.Size());
            }
            for (int i = 0; i < reserved; i++)
            {
                OperandSize operand_size = builder.CreateReservedEntry();
                Assert.Equal(OperandSize.Short, operand_size);
                builder.CommitReservedEntry(operand_size, Smi.FromInt(i));
                Assert.Equal(k8BitCapacity, builder.Size());
            }
            for (int i = k8BitCapacity; i < k8BitCapacity + reserved; i++)
            {
                OperandSize operand_size = builder.CreateReservedEntry();
                Assert.Equal(OperandSize.Short, operand_size);
                builder.CommitReservedEntry(operand_size, Smi.FromInt(i));
                Assert.Equal(i + 1, builder.Size());
            }

            object[] constant_array = builder.ToFixedArray(isolate);
            Assert.Equal(k8BitCapacity + reserved, constant_array.Length);
            for (int i = 0; i < k8BitCapacity + reserved; i++)
            {
                Assert.True(SameNumber(constant_array[i], i));
            }
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_GapFilledWhenLowReservationCommitted()
    {
        var builder = new ConstantArrayBuilder();
        for (int i = 0; i < k8BitCapacity; i++)
        {
            OperandSize operand_size = builder.CreateReservedEntry();
            Assert.Equal(OperandSize.Byte, operand_size);
            Assert.Equal(0, builder.Size());
        }
        for (int i = 0; i < k8BitCapacity; i++)
        {
            builder.CommitReservedEntry(builder.CreateReservedEntry(), Smi.FromInt(i));
            Assert.Equal(i + k8BitCapacity + 1, builder.Size());
        }
        for (int i = 0; i < k8BitCapacity; i++)
        {
            builder.CommitReservedEntry(OperandSize.Byte, Smi.FromInt(i));
            Assert.Equal(2 * k8BitCapacity, builder.Size());
        }
        object[] constant_array = builder.ToFixedArray(isolate);
        Assert.Equal(2 * k8BitCapacity, constant_array.Length);
        for (int i = 0; i < k8BitCapacity; i++)
        {
            object original = constant_array[k8BitCapacity + i];
            object duplicate = constant_array[i];
            Assert.Equal(NumberValue(original), NumberValue(duplicate));
            Assert.True(SameNumber(original, i));
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_GapNotFilledWhenLowReservationDiscarded()
    {
        var builder = new ConstantArrayBuilder();
        for (int i = 0; i < k8BitCapacity; i++)
        {
            OperandSize operand_size = builder.CreateReservedEntry();
            Assert.Equal(OperandSize.Byte, operand_size);
            Assert.Equal(0, builder.Size());
        }
        var values = new double[k8BitCapacity];
        for (int i = 0; i < k8BitCapacity; i++) values[i] = i + 0.5;

        for (int i = 0; i < k8BitCapacity; i++)
        {
            builder.Insert(values[i]);
            Assert.Equal(i + k8BitCapacity + 1, builder.Size());
        }
        for (int i = 0; i < k8BitCapacity; i++)
        {
            builder.DiscardReservedEntry(OperandSize.Byte);
            builder.Insert(values[i]);
            Assert.Equal(2 * k8BitCapacity, builder.Size());
        }
        for (int i = 0; i < k8BitCapacity; i++)
        {
            object? original = builder.At(k8BitCapacity + i, isolate);
            Assert.True(SameNumber(original, i + 0.5));
            object? duplicate = builder.At(i, isolate);
            Assert.Null(duplicate);
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_HolesWithUnusedReservations()
    {
        const int kNumberOfHoles = 128;
        var builder = new ConstantArrayBuilder();
        for (int i = 0; i < kNumberOfHoles; ++i)
        {
            Assert.Equal(OperandSize.Byte, builder.CreateReservedEntry());
        }
        // Values are placed before the reserved entries in the same slice.
        for (int i = 0; i < k8BitCapacity - kNumberOfHoles; ++i)
        {
            Assert.Equal(i, builder.Insert(i + 0.5));
        }
        // The next value is pushed into the next slice.
        Assert.Equal(k8BitCapacity, builder.Insert(k8BitCapacity + 0.5));

        // Discard the reserved entries.
        for (int i = 0; i < kNumberOfHoles; ++i) builder.DiscardReservedEntry(OperandSize.Byte);

        object[] constant_array = builder.ToFixedArray(isolate);
        Assert.Equal(k8BitCapacity + 1, constant_array.Length);
        for (int i = kNumberOfHoles; i < k8BitCapacity; i++)
        {
            Assert.True(IsTheHole(constant_array[i]));
        }
        Assert.False(IsTheHole(constant_array[kNumberOfHoles - 1]));
        Assert.False(IsTheHole(constant_array[k8BitCapacity]));
    }

    [Fact]
    public void ConstantArrayBuilderTest_ReservationsAtAllScales()
    {
        var builder = new ConstantArrayBuilder();
        for (int i = 0; i < 256; i++) Assert.Equal(OperandSize.Byte, builder.CreateReservedEntry());
        for (int i = 256; i < 65536; ++i) Assert.Equal(OperandSize.Short, builder.CreateReservedEntry());
        for (int i = 65536; i < 131072; ++i) Assert.Equal(OperandSize.Quad, builder.CreateReservedEntry());
        Assert.Equal(0, builder.CommitReservedEntry(OperandSize.Byte, Smi.FromInt(1)));
        Assert.Equal(256, builder.CommitReservedEntry(OperandSize.Short, Smi.FromInt(2)));
        Assert.Equal(65536, builder.CommitReservedEntry(OperandSize.Quad, Smi.FromInt(3)));
        for (int i = 1; i < 256; i++) builder.DiscardReservedEntry(OperandSize.Byte);
        for (int i = 257; i < 65536; ++i) builder.DiscardReservedEntry(OperandSize.Short);
        for (int i = 65537; i < 131072; ++i) builder.DiscardReservedEntry(OperandSize.Quad);

        object[] constant_array = builder.ToFixedArray(isolate);
        int constant_array_len = constant_array.Length;
        Assert.Equal(65537, constant_array_len);
        int count = 1;
        for (int i = 0; i < constant_array_len; ++i)
        {
            if (i == 0 || i == 256 || i == 65536)
                Assert.True(SameNumber(constant_array[i], count++));
            else
                Assert.True(IsTheHole(constant_array[i]));
        }
    }

    [Fact]
    public void ConstantArrayBuilderTest_AllocateEntriesWithFixedReservations()
    {
        var builder = new ConstantArrayBuilder();
        for (int i = 0; i < k16BitCapacity; i++)
        {
            if ((i % 2) == 0) Assert.Equal(i, builder.InsertDeferred());
            else builder.Insert(Smi.FromInt(i));
        }
        Assert.Equal(k16BitCapacity, builder.Size());

        // Check values before reserved entries are inserted.
        for (int i = 0; i < k16BitCapacity; i++)
        {
            if ((i % 2) == 0)
            {
                // Check reserved values are null.
                Assert.Null(builder.At(i, isolate));
            }
            else
            {
                Assert.Equal(i, ((Smi)builder.At(i, isolate)!).Value);
            }
        }

        // Insert reserved entries.
        for (int i = 0; i < k16BitCapacity; i += 2) builder.SetDeferredAt(i, Smi.FromInt(i));

        // Check values after reserved entries are inserted.
        for (int i = 0; i < k16BitCapacity; i++)
        {
            Assert.Equal(i, ((Smi)builder.At(i, isolate)!).Value);
        }
    }
}
