// The operations Maglev code calls, in the role of the builtins and the
// out-of-line code maglev-code-generator.cc / maglev-ir.cc emit for the
// nodes (GenerateCode of CheckMaps, LoadTaggedField, StoreTaggedField...,
// the deferred code of the checks, and the runtime calls of
// CallKnownJSFunction, EnterInlinedFrame, ...). Small ones are inlined into
// the generated IL by RyuJIT; the IL itself does the int32/float64
// arithmetic, the comparisons and the branches.
//
// Each helper relies on the checks the graph put before it (a map check
// guarantees the object's class), as V8's machine code does: they use
// Unsafe.As where the check makes the cast valid.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Builtins;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public static class MaglevBuiltins
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;
    // Out-of-line helpers Maglev code calls are compiled fully optimized at
    // once (no RyuJIT tier 0 or instrumented tier): optimized code calls them
    // from its first run, as baseline code its call paths (BaselineCalls).
    const MethodImplOptions Outline = MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization;

    // ---- Type tests (CheckSmi, CheckNumber, CheckHeapObject, CheckString ...) -------------------------

    [MethodImpl(Inline)]
    public static bool IsNumber(JSValue v) => ReferenceEquals(v._obj, NumberTag.Instance);

    /// <summary>A Smi (31-bit, integral, not -0), as JSValue.IsSmi.</summary>
    [MethodImpl(Inline)]
    public static bool IsSmi(JSValue v) => ReferenceEquals(v._obj, NumberTag.Instance) && JSValue.IsSmiDouble(v._num);

    [MethodImpl(Inline)]
    public static bool IsHeapObject(JSValue v) => v._obj is not null && !ReferenceEquals(v._obj, NumberTag.Instance);

    [MethodImpl(Inline)]
    public static bool IsString(JSValue v) => v._obj is { } o && o.InstanceType <= InstanceTypeChecks.LastString;

    [MethodImpl(Inline)]
    public static bool IsInternalizedString(JSValue v) =>
        v._obj is { } o && o.InstanceType <= InstanceTypeChecks.LastString && Unsafe.As<JSString>(o).IsInternalized;

    [MethodImpl(Inline)]
    public static bool IsSymbol(JSValue v) => v._obj is Symbol;

    [MethodImpl(Inline)]
    public static bool IsJSReceiver(JSValue v) => v._obj is { } o && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver;

    [MethodImpl(Inline)]
    public static bool IsStringOrStringWrapper(JSValue v) =>
        v._obj is { } o && (o.InstanceType <= InstanceTypeChecks.LastString || o is JSPrimitiveWrapper { Value._obj: JSString });

    [MethodImpl(Inline)]
    public static JSValue UnwrapStringWrapper(JSValue v) => v._obj is JSPrimitiveWrapper w ? w.Value : v;

    [MethodImpl(Inline)]
    public static bool IsJSReceiverOrNullOrUndefined(JSValue v) =>
        v._obj is not { } o || ReferenceEquals(o, Oddball.Null) || o.InstanceType >= InstanceTypeChecks.FirstJSReceiver;

    [MethodImpl(Inline)]
    public static bool IsNumberOrOddball(JSValue v) =>
        v._obj is null || ReferenceEquals(v._obj, NumberTag.Instance) || ReferenceEquals(v._obj, Oddball.Null) ||
        ReferenceEquals(v._obj, Oddball.True) || ReferenceEquals(v._obj, Oddball.False);

    [MethodImpl(Inline)]
    public static bool IsNumberOrBoolean(JSValue v) =>
        ReferenceEquals(v._obj, NumberTag.Instance) || ReferenceEquals(v._obj, Oddball.True) || ReferenceEquals(v._obj, Oddball.False);

    /// <summary>The number value of a number or oddball (undefined is NaN).</summary>
    [MethodImpl(Inline)]
    public static double NumberOrOddballToFloat64(JSValue v)
    {
        if (ReferenceEquals(v._obj, NumberTag.Instance)) return v._num;
        return v._obj is null ? double.NaN : Unsafe.As<Oddball>(v._obj).ToNumberValue;
    }

    /// <summary>The elements are writable in place (not copy-on-write).</summary>
    [MethodImpl(Inline)]
    public static bool IsWritableElements(JSValue elements) => !Unsafe.As<FixedArrayBase>(elements._obj!).IsCowArray;

    /// <summary>CheckValidityCell: the prototype chain validity cell is still valid.</summary>
    [MethodImpl(Inline)]
    public static bool IsValidCell(Cell cell) => cell.Value._num == 0 && ReferenceEquals(cell.Value._obj, NumberTag.Instance);

    [MethodImpl(Inline)]
    public static bool IsHoleNaN(double d) => BitConverter.DoubleToInt64Bits(d) == EngineGlobals.kHoleNanInt64;

    /// <summary>The map of a JSReceiver, or null (LoadTaggedField(object, kMapOffset)).</summary>
    [MethodImpl(Inline)]
    public static Map? MapOf(JSValue v) =>
        v._obj is { } o && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver ? Unsafe.As<JSReceiver>(o).Map : null;

    // ---- Conversions ------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue Int32ToNumber(int value) => JSValue.FromInt(value);

    [MethodImpl(Inline)]
    public static JSValue Uint32ToNumber(int value) => JSValue.FromNumber((uint)value);

    [MethodImpl(Inline)]
    public static JSValue Float64ToTagged(double value) => JSValue.FromNumber(value);

    /// <summary>HoleyFloat64ToTagged: the hole NaN is undefined.</summary>
    [MethodImpl(Inline)]
    public static JSValue HoleyFloat64ToTagged(double value) => IsHoleNaN(value) ? JSValue.Undefined : JSValue.FromNumber(value);

    /// <summary>
    /// Float64Min: NaN if either is NaN, and -0 below +0 (JS Math.min; the
    /// JIT's Math.Min intrinsic can return +0 for (-0, +0)).
    /// </summary>
    public static double Float64Min(double a, double b)
    {
        if (a < b) return a;
        if (b < a) return b;
        if (a == b) return BitConverter.DoubleToInt64Bits(a) < 0 ? a : b;
        return double.NaN;
    }

    /// <summary>Float64Max: NaN if either is NaN, and +0 above -0.</summary>
    public static double Float64Max(double a, double b)
    {
        if (a > b) return a;
        if (b > a) return b;
        if (a == b) return BitConverter.DoubleToInt64Bits(a) < 0 ? b : a;
        return double.NaN;
    }

    // ---- Deopt exits: the values of a frame state into the scratch buffer --------------------------------

    public static void Spill1(JSValue[] s, int i, JSValue a) => s[i] = a;

    // The end of a deopt exit's spill chain: Deoptimizer::Deoptimize; the exit
    // returns the result.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue Deopt0(Isolate isolate, ref InterpreterState state, MaglevCode code, int index, int reason)
    {
        V8Sharp.Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, code, index, reason);
        return JSValue.Undefined;
    }

    public static void Spill2(JSValue[] s, int i, JSValue a, JSValue b)
    {
        s[i] = a;
        s[i + 1] = b;
    }

    public static void Spill4(JSValue[] s, int i, JSValue a, JSValue b, JSValue c, JSValue d)
    {
        s[i] = a;
        s[i + 1] = b;
        s[i + 2] = c;
        s[i + 3] = d;
    }

    public static void Spill8(JSValue[] s, int i, JSValue a, JSValue b, JSValue c, JSValue d, JSValue e, JSValue f, JSValue g,
        JSValue h)
    {
        s[i] = a;
        s[i + 1] = b;
        s[i + 2] = c;
        s[i + 3] = d;
        s[i + 4] = e;
        s[i + 5] = f;
        s[i + 6] = g;
        s[i + 7] = h;
    }

    /// <summary>ConvertHoleToUndefined.</summary>
    [MethodImpl(Inline)]
    public static JSValue ConvertHoleToUndefined(JSValue value) => value.IsTheHole ? JSValue.Undefined : value;

    [MethodImpl(Inline)]
    public static int TruncateFloat64ToInt32(double value) => Conversions.DoubleToInt32(value);

    [MethodImpl(Inline)]
    public static JSValue Boolean(bool value) => value ? JSValue.True : JSValue.False;

    // ---- Tests -----------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue TaggedEqual(JSValue a, JSValue b) => a.IsIdenticalTo(b) ? JSValue.True : JSValue.False;

    [MethodImpl(Inline)]
    public static bool ToBoolean(JSValue v)
    {
        // Receivers first (`if (node)`), by instance type: ObjectOps.BooleanValue
        // is not inlined.
        HeapObject? o = v._obj;
        if (o is null) return false;
        if (o.InstanceType >= InstanceTypeChecks.FirstJSReceiver) return !Unsafe.As<JSReceiver>(o).Map.IsUndetectable;
        return InterpreterOps.ToBoolean(v);
    }

    [MethodImpl(Inline)]
    public static bool Float64ToBoolean(double d) => d != 0 && !double.IsNaN(d);

    [MethodImpl(Inline)]
    public static bool IsTrue(JSValue v) => ReferenceEquals(v._obj, Oddball.True);

    [MethodImpl(Inline)]
    public static bool IsUndefinedOrNull(JSValue v) => v._obj is null || ReferenceEquals(v._obj, Oddball.Null);

    [MethodImpl(Inline)]
    public static bool IsIdentical(JSValue a, JSValue b) => a.IsIdenticalTo(b);

    /// <summary>
    /// TestUndetectable: null, undefined and receivers with undetectable maps
    /// (the instance type range test instead of a class type test).
    /// </summary>
    [MethodImpl(Inline)]
    public static JSValue TestUndetectable(JSValue v)
    {
        HeapObject? o = v._obj;
        bool undetectable = o is null || ReferenceEquals(o, Oddball.Null) ||
                            o.InstanceType >= InstanceTypeChecks.FirstJSReceiver && Unsafe.As<JSReceiver>(o).Map.IsUndetectable;
        return undetectable ? JSValue.True : JSValue.False;
    }

    /// <summary>
    /// OrdinaryHasInstance for the checked constructor of an instanceof
    /// (TryBuildFastInstanceOf): the prototype chain walk of
    /// ObjectOps.FastInstanceOf, or the generic InstanceOf when it does not apply.
    /// </summary>
    /// <summary>
    /// BuildOrdinaryHasInstance with the constructor's prototype known at
    /// compile time (HasInPrototypeChain): valid while the constructor keeps
    /// its map and its prototype (V8 depends on both); otherwise, and for
    /// proxies and access-checked objects on the chain, InstanceOfFunction.
    /// </summary>
    [MethodImpl(Inline)]
    public static JSValue OrdinaryHasInstance(Isolate isolate, JSValue obj, JSFunction function, Map functionMap, HeapObject protoOrMap,
        JSReceiver prototype)
    {
        if (ReferenceEquals(function.Map, functionMap) && ReferenceEquals(function.PrototypeOrInitialMap, protoOrMap))
        {
            HeapObject? o = obj._obj;
            if (o is null || o.InstanceType < InstanceTypeChecks.FirstJSReceiver) return JSValue.False;
            Map map = Unsafe.As<JSReceiver>(o).Map;
            while (!Map.IsSpecialReceiverMap(map))
            {
                JSReceiver? next = map.Prototype;
                if (next is null) return JSValue.False;
                if (ReferenceEquals(next, prototype)) return JSValue.True;
                map = next.Map;
            }
        }
        return InstanceOfFunction(isolate, obj, function);
    }

    /// <summary>
    /// HasInPrototypeChain: 1 when <paramref name="prototype"/> is on the
    /// prototype chain of <paramref name="obj"/>, 0 when not, 2 when the
    /// optimized code must deoptimize (the constructor's map or prototype slot
    /// changed, or a proxy or an access-checked object is on the chain).
    /// </summary>
    [MethodImpl(Inline)]
    public static int HasInPrototypeChain(JSValue obj, JSFunction function, Map functionMap, HeapObject protoOrMap, JSReceiver prototype)
    {
        if (!ReferenceEquals(function.Map, functionMap) || !ReferenceEquals(function.PrototypeOrInitialMap, protoOrMap)) return 2;
        HeapObject? o = obj._obj;
        if (o is null || o.InstanceType < InstanceTypeChecks.FirstJSReceiver) return 0;
        Map map = Unsafe.As<JSReceiver>(o).Map;
        while (true)
        {
            // HasInPrototypeChain::GenerateCode: special receivers continue
            // through their map's prototype, except proxies and objects that
            // need access checks (V8's deferred runtime call).
            if (Map.IsSpecialReceiverMap(map) && (map.InstanceType == InstanceType.JSProxyType || map.IsAccessCheckNeeded)) return 2;
            JSReceiver? next = map.Prototype;
            if (next is null) return 0;
            if (ReferenceEquals(next, prototype)) return 1;
            map = next.Map;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue InstanceOfFunction(Isolate isolate, JSValue obj, JSValue constructor)
    {
        int fast = ObjectOps.FastInstanceOf(obj, constructor);
        if (fast >= 0) return fast != 0 ? JSValue.True : JSValue.False;
        return ObjectOps.InstanceOf(isolate, obj, constructor) ? JSValue.True : JSValue.False;
    }

    /// <summary>
    /// CreateObjectLiteral for a shallow boilerplate (no nested objects, which
    /// boilerplates never get): a copy of it; the runtime's path once its map
    /// is deprecated (the deep copy migrates it).
    /// </summary>
    public static JSValue CloneObjectLiteral(Isolate isolate, FeedbackVector fv, int slot, JSValue description, int flags, LiteralShape shape)
    {
        if (shape.IsValid()) return shape.Clone(isolate);
        return Baseline.BaselineBuiltins.CreateObjectLiteral(isolate, fv, slot, description, flags);
    }

    /// <summary>TestUndetectable under the NoUndetectableObjects protector: null or undefined.</summary>
    [MethodImpl(Inline)]
    public static JSValue TestUndefinedOrNull(JSValue v) =>
        v._obj is null || ReferenceEquals(v._obj, Oddball.Null) ? JSValue.True : JSValue.False;

    public static JSValue TestTypeOf(JSValue v, int literal) => BaselineBuiltinsBridge.TestTypeOf(v, literal);

    // ---- Fields -----------------------------------------------------------------------------------------

    /// <summary>LoadTaggedField: the field at a storage index (FieldIndex.StorageIndex) of a map-checked JSObject.</summary>
    [MethodImpl(Inline)]
    public static JSValue LoadField(JSValue obj, int storageIndex) => Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex);

    /// <summary>StoreTaggedField.</summary>
    [MethodImpl(Inline)]
    public static void StoreField(JSValue obj, int storageIndex, JSValue value) =>
        Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex) = value;

    /// <summary>A store to a Double field: NaNs are canonicalized (StoreIC's CanonicalizeDouble).</summary>
    [MethodImpl(Inline)]
    public static void StoreDoubleField(JSValue obj, int storageIndex, JSValue value)
    {
        if (double.IsNaN(value._num)) value = JSValue.NaN;
        Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex) = value;
    }

    // ---- for-in (MaglevGraphBuilder.ForIn.cs) -----------------------------------------------------------------

    /// <summary>LoadEnumCacheKeys: the keys of the enum cache of a map.</summary>
    public static JSValue EnumCacheKeys(JSValue map) => Unsafe.As<Map>(map._obj!).InstanceDescriptors.EnumCache.Keys;

    /// <summary>LoadEnumCacheIndices: the field indices of the enum cache of a map.</summary>
    public static JSValue EnumCacheIndices(JSValue map) => Unsafe.As<Map>(map._obj!).InstanceDescriptors.EnumCache.Indices;

    /// <summary>LoadEnumCacheLength.</summary>
    public static int EnumCacheLength(JSValue map) => Unsafe.As<Map>(map._obj!).EnumLength;

    /// <summary>CheckCacheIndicesNotCleared: false (deopt) when the indices do not cover the length.</summary>
    public static bool CacheIndicesCover(JSValue indices, int length) => Unsafe.As<FixedArray>(indices._obj!).Length >= length;

    /// <summary>
    /// LoadTaggedFieldByFieldIndex: <paramref name="encoded"/> is
    /// FieldIndex::GetLoadByFieldIndex's encoding (in-object indices positive,
    /// out-of-object ones -index-1, shifted left by one, the low bit marking a
    /// double field, whose value V8Sharp also keeps as a JSValue).
    /// </summary>
    public static JSValue LoadFieldByFieldIndex(JSValue obj, int encoded)
    {
        var o = Unsafe.As<JSObject>(obj._obj!);
        int index = encoded >> 1;
        int propertyIndex = index >= 0 ? index : o.Map.GetInObjectProperties() - index - 1;
        return o.RawFastPropertyAt(FieldIndex.ForPropertyIndex(o.Map, propertyIndex));
    }

    /// <summary>StoreDoubleField's payload: the double's bits, NaNs canonicalized (StoreIC's CanonicalizeDouble).</summary>
    [MethodImpl(Inline)]
    public static long DoubleFieldBits(double value) => double.IsNaN(value) ? JSValue.NaN._bits : BitConverter.DoubleToInt64Bits(value);

    /// <summary>StoreDoubleField without an inline field address.</summary>
    public static void StoreDoubleFieldFloat64(JSValue obj, int storageIndex, double value) =>
        JSValue.StoreSlot(ref Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex), double.IsNaN(value) ? JSValue.NaN : new JSValue(value));

    /// <summary>
    /// A field-adding map transition (StoreMap + ExtendPropertiesBackingStore +
    /// StoreTaggedField, as StoreIC's TryStoreTransition does it).
    /// </summary>
    public static void StoreTransition(JSValue obj, Map transition, int storageIndex, JSValue value, bool isDouble)
    {
        JSObject o = Unsafe.As<JSObject>(obj._obj!);
        int arrayIndex = storageIndex - JSObject.kPropertyArrayStorageBase;
        if (arrayIndex >= o._fields.Length) o.EnsurePropertyArrayLength(arrayIndex + transition.UnusedPropertyFields() + 1);
        if (isDouble && double.IsNaN(value._num) && ReferenceEquals(value._obj, NumberTag.Instance)) value = JSValue.NaN;
        o.FieldAt(storageIndex) = value;
        o.Map = transition;
    }

    /// <summary>A store to a const field succeeds only when it does not change the value (StoreHandler kConstField).</summary>
    public static bool CheckConstFieldValue(JSValue obj, int storageIndex, JSValue value)
    {
        JSValue current = Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex);
        if (current.IsNumber && value.IsNumber)
        {
            return BitConverter.DoubleToInt64Bits(current._num) == BitConverter.DoubleToInt64Bits(value._num);
        }
        return current.IsIdenticalTo(value);
    }

    // ---- Elements ---------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue LoadElements(JSValue obj) => JSValue.FromObject(Unsafe.As<JSObject>(obj._obj!).Elements);

    [MethodImpl(Inline)]
    public static JSValue LoadJSArrayLength(JSValue array) => Unsafe.As<JSArray>(array._obj!).Length;

    [MethodImpl(Inline)]
    public static int LoadFixedArrayLength(JSValue elements) =>
        elements._obj is FixedArray f ? f._data.Length : Unsafe.As<FixedArrayBase>(elements._obj!).Length;

    [MethodImpl(Inline)]
    public static JSValue LoadFixedArrayElement(JSValue elements, int index) => Unsafe.As<FixedArray>(elements._obj!)._data[index];

    /// <summary>The array of a FixedArray, or null for other elements (no access follows then).</summary>
    [MethodImpl(Inline)]
    public static JSValue[]? FixedArrayDataOf(JSValue elements) => (elements._obj as FixedArray)?._data;

    [MethodImpl(Inline)]
    public static double[]? FixedDoubleArrayDataOf(JSValue elements) => (elements._obj as FixedDoubleArray)?._data;

    [MethodImpl(Inline)]
    public static void StoreDoubleData(double[] data, int index, double value)
    {
        // Every NaN but the hole is stored canonical (FixedDoubleArray::set).
        if (double.IsNaN(value)) value = double.NaN;
        data[index] = value;
    }

    [MethodImpl(Inline)]
    public static double LoadFixedDoubleArrayElement(JSValue elements, int index) =>
        Unsafe.As<FixedDoubleArray>(elements._obj!)._data[index];

    [MethodImpl(Inline)]
    public static void StoreFixedArrayElement(JSValue elements, int index, JSValue value) =>
        Unsafe.As<FixedArray>(elements._obj!)._data[index] = value;

    [MethodImpl(Inline)]
    public static void StoreFixedDoubleArrayElement(JSValue elements, int index, double value)
    {
        // Every NaN but the hole is stored canonical (FixedDoubleArray::set).
        if (double.IsNaN(value)) value = double.NaN;
        Unsafe.As<FixedDoubleArray>(elements._obj!)._data[index] = value;
    }

    // ---- Typed arrays ---------------------------------------------------------------------------------------

    /// <summary>
    /// KeyedStoreIC_Megamorphic: the fast path of a Number stored at an
    /// in-bounds integer index of an attached, fixed-length typed array (the
    /// builtin's typed array case); everything else through the keyed store IC.
    /// </summary>
    [MethodImpl(Outline)]
    public static void KeyedStoreICMegamorphic(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue key, JSValue value)
    {
        if (obj._obj is JSTypedArray a && key.IsNumber && value.IsNumber && !a.IsVariableLength && !a.IsBigIntArray)
        {
            JSArrayBuffer buffer = a.Buffer;
            double k = key.Number;
            uint index = (uint)k;
            if (index == k && index < a.RawLength && !buffer.WasDetached && !buffer.IsImmutable)
            {
                int size = a.ElementSize;
                TypedArrayElementsOps.StoreDoubleToBytes(a.Kind,
                    buffer.BackingStoreBuffer.AsSpan((int)(a.ByteOffset + (ulong)index * (ulong)size), size), value.Number);
                return;
            }
        }
        IC.KeyedStoreIC.Store(isolate, fv, slot, obj, key, value);
    }

    /// <summary>LoadTypedArrayLength: the length, 0 when detached (or out of bounds of a resizable buffer).</summary>
    [MethodImpl(Inline)]
    public static int TypedArrayLength(JSValue obj)
    {
        var a = Unsafe.As<JSTypedArray>(obj._obj!);
        ulong length = a.Buffer.WasDetached ? 0 : a.IsVariableLength ? a.GetLength() : a.RawLength;
        return length > int.MaxValue ? int.MaxValue : (int)length;
    }

    /// <summary>CheckTypedArrayValid: not detached, and for a write not immutable.</summary>
    [MethodImpl(Inline)]
    public static bool IsTypedArrayValid(JSValue obj, bool write)
    {
        JSArrayBuffer buffer = Unsafe.As<JSTypedArray>(obj._obj!).Buffer;
        return !buffer.WasDetached && !(write && buffer.IsImmutable);
    }

    /// <summary>LoadTypedArrayLength of the length property: the whole length (V8: an IntPtr).</summary>
    [MethodImpl(Inline)]
    public static double TypedArrayLengthAsFloat64(JSValue obj)
    {
        var a = Unsafe.As<JSTypedArray>(obj._obj!);
        return a.Buffer.WasDetached ? 0 : a.IsVariableLength ? a.GetLength() : a.RawLength;
    }

    [MethodImpl(Inline)]
    public static bool TypedArrayIndexInBounds(JSValue obj, int index) => (uint)index < (uint)TypedArrayLength(obj);

    [MethodImpl(Inline)]
    static ref byte TypedElement(JSValue obj, int index, int size)
    {
        var a = Unsafe.As<JSTypedArray>(obj._obj!);
        return ref a.Buffer.BackingStoreBuffer[(int)a.ByteOffset + index * size];
    }

    [MethodImpl(Inline)]
    public static int LoadInt8Element(JSValue obj, int index) => (sbyte)TypedElement(obj, index, 1);
    [MethodImpl(Inline)]
    public static int LoadUint8Element(JSValue obj, int index) => TypedElement(obj, index, 1);
    [MethodImpl(Inline)]
    public static int LoadInt16Element(JSValue obj, int index) => Unsafe.ReadUnaligned<short>(ref TypedElement(obj, index, 2));
    [MethodImpl(Inline)]
    public static int LoadUint16Element(JSValue obj, int index) => Unsafe.ReadUnaligned<ushort>(ref TypedElement(obj, index, 2));
    /// <summary>Int32 and Uint32 elements (a Uint32 value is the int32 of the same bits).</summary>
    [MethodImpl(Inline)]
    public static int LoadInt32Element(JSValue obj, int index) => Unsafe.ReadUnaligned<int>(ref TypedElement(obj, index, 4));
    [MethodImpl(Inline)]
    public static double LoadFloat32Element(JSValue obj, int index) => Unsafe.ReadUnaligned<float>(ref TypedElement(obj, index, 4));
    [MethodImpl(Inline)]
    public static double LoadFloat64Element(JSValue obj, int index) => Unsafe.ReadUnaligned<double>(ref TypedElement(obj, index, 8));

    [MethodImpl(Inline)]
    public static void StoreInt8Element(JSValue obj, int index, int value) => TypedElement(obj, index, 1) = (byte)value;
    [MethodImpl(Inline)]
    public static void StoreInt16Element(JSValue obj, int index, int value) =>
        Unsafe.WriteUnaligned(ref TypedElement(obj, index, 2), (short)value);
    [MethodImpl(Inline)]
    public static void StoreInt32Element(JSValue obj, int index, int value) => Unsafe.WriteUnaligned(ref TypedElement(obj, index, 4), value);
    [MethodImpl(Inline)]
    public static void StoreFloat32Element(JSValue obj, int index, double value) =>
        Unsafe.WriteUnaligned(ref TypedElement(obj, index, 4), (float)value);
    [MethodImpl(Inline)]
    public static void StoreFloat64Element(JSValue obj, int index, double value) => Unsafe.WriteUnaligned(ref TypedElement(obj, index, 8), value);
    [MethodImpl(Inline)]
    public static void StoreUint8ClampedInt32(JSValue obj, int index, int value) =>
        TypedElement(obj, index, 1) = (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    [MethodImpl(Inline)]
    public static void StoreUint8ClampedFloat64(JSValue obj, int index, double value) =>
        TypedElement(obj, index, 1) = TypedArrayScalars.ClampDouble(value);

    // The same on the array and byte offset of a typed array loaded with its
    // length (MaglevCodeGenerator's typed array data locals).
    [MethodImpl(Inline)]
    public static byte[] TypedArrayDataOf(JSValue obj) => Unsafe.As<JSTypedArray>(obj._obj!).Buffer.BackingStoreBuffer;

    [MethodImpl(Inline)]
    public static int TypedArrayByteOffsetOf(JSValue obj) => (int)Unsafe.As<JSTypedArray>(obj._obj!).ByteOffset;

    [MethodImpl(Inline)]
    public static int LoadInt8Data(byte[] data, int byteIndex) => (sbyte)data[byteIndex];
    [MethodImpl(Inline)]
    public static int LoadUint8Data(byte[] data, int byteIndex) => data[byteIndex];
    [MethodImpl(Inline)]
    public static int LoadInt16Data(byte[] data, int byteIndex) => Unsafe.ReadUnaligned<short>(ref data[byteIndex]);
    [MethodImpl(Inline)]
    public static int LoadUint16Data(byte[] data, int byteIndex) => Unsafe.ReadUnaligned<ushort>(ref data[byteIndex]);
        [MethodImpl(Inline)]
    public static int LoadInt32Data(byte[] data, int byteIndex) => Unsafe.ReadUnaligned<int>(ref data[byteIndex]);
    [MethodImpl(Inline)]
    public static double LoadFloat32Data(byte[] data, int byteIndex) => Unsafe.ReadUnaligned<float>(ref data[byteIndex]);
    [MethodImpl(Inline)]
    public static double LoadFloat64Data(byte[] data, int byteIndex) => Unsafe.ReadUnaligned<double>(ref data[byteIndex]);

    [MethodImpl(Inline)]
    public static void StoreInt8Data(byte[] data, int byteIndex, int value) => data[byteIndex] = (byte)value;
    [MethodImpl(Inline)]
    public static void StoreInt16Data(byte[] data, int byteIndex, int value) =>
        Unsafe.WriteUnaligned(ref data[byteIndex], (short)value);
    [MethodImpl(Inline)]
    public static void StoreInt32Data(byte[] data, int byteIndex, int value) => Unsafe.WriteUnaligned(ref data[byteIndex], value);
    [MethodImpl(Inline)]
    public static void StoreFloat32Data(byte[] data, int byteIndex, double value) =>
        Unsafe.WriteUnaligned(ref data[byteIndex], (float)value);
    [MethodImpl(Inline)]
    public static void StoreFloat64Data(byte[] data, int byteIndex, double value) => Unsafe.WriteUnaligned(ref data[byteIndex], value);
    [MethodImpl(Inline)]
    public static void StoreUint8ClampedInt32Data(byte[] data, int byteIndex, int value) =>
        data[byteIndex] = (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    [MethodImpl(Inline)]
    public static void StoreUint8ClampedFloat64Data(byte[] data, int byteIndex, double value) =>
        data[byteIndex] = TypedArrayScalars.ClampDouble(value);

    /// <summary>The slow path of CheckMapsWithMigration: migrates a deprecated map, then checks the maps again.</summary>
    public static bool MigrateAndCheckMaps(Isolate isolate, JSValue value, Map[] maps)
    {
        if (value._obj is not JSObject o || !o.Map.IsDeprecated) return false;
        if (!JSObject.TryMigrateInstance(isolate, o)) return false;
        return Array.IndexOf(maps, o.Map) >= 0;
    }

    /// <summary>
    /// MigrateMapIfNeeded: an object whose map is deprecated is migrated
    /// (Runtime_TryMigrateInstance); the result is its map then. (V8 deopts
    /// when the migration fails; V8Sharp returns the deprecated map, which the
    /// dispatch's last map check rejects.)
    /// </summary>
    public static JSValue MigrateMapIfNeeded(Isolate isolate, JSValue map, JSValue obj)
    {
        if (map._obj is not Map { IsDeprecated: true } || obj._obj is not JSObject o) return map;
        JSObject.TryMigrateInstance(isolate, o);
        return o.Map;
    }

    /// <summary>
    /// Runtime_TryMigrateInstanceAndMarkMapAsMigrationTarget (the deferred code of
    /// CheckMapsWithMigrationAndDeopt): migrates an object with a deprecated map.
    /// </summary>
    public static void TryMigrateInstanceAndMarkMapAsMigrationTarget(Isolate isolate, JSValue value)
    {
        if (value._obj is not JSObject o || !o.Map.IsDeprecated) return;
        if (JSObject.TryMigrateInstance(isolate, o)) o.Map.IsMigrationTarget = true;
    }

    /// <summary>TransitionElementsKind to <paramref name="target"/>; false (deopt) if the object ends up with another map.</summary>
    public static bool TransitionElementsKind(Isolate isolate, JSValue obj, Map target)
    {
        var o = Unsafe.As<JSObject>(obj._obj!);
        JSObject.TransitionElementsKind(isolate, o, target.ElementsKind);
        return ReferenceEquals(o.Map, target);
    }

    /// <summary>CheckValueEqualsString (and the keyed name's primitive, the hole if none).</summary>
    public static bool ValueEqualsString(JSValue value, JSString expected, JSValue primitive)
    {
        if (ReferenceEquals(value._obj, expected)) return true;
        if (value._obj is JSString s) return JSString.Equals(s, expected);
        return !primitive.IsTheHole && value.IsIdenticalTo(primitive);
    }

    /// <summary>charCodeAt, NaN for an index outside the string.</summary>
    public static double StringCharCodeAtOrNaN(JSValue s, int index)
    {
        var str = Unsafe.As<JSString>(s._obj!);
        return (uint)index < (uint)str.Length ? StringCharCodeAt(s, index) : double.NaN;
    }

    /// <summary>
    /// CheckedObjectToIndex: the int32 index of a Smi, an integral HeapNumber
    /// or an array index String, or long.MinValue (a result instead of an out
    /// parameter: the IL local an out parameter writes would be address
    /// exposed, which keeps RyuJIT from enregistering it anywhere in the method).
    /// </summary>
    public static long ObjectToIndexOrMin(JSValue value)
    {
        if (value.IsNumber)
        {
            double d = value.Number;
            int index = (int)d;
            return index == d ? index : long.MinValue;
        }
        if (value._obj is JSString s && s.AsArrayIndex(out uint u) && u <= int.MaxValue) return (int)u;
        return long.MinValue;
    }

    /// <summary>
    /// The start of an exception trampoline (Isolate::UnwindAndFindHandler
    /// for a Maglev frame): drops the frames and registers the exception left
    /// above this frame, sets the pending message, and returns the exception.
    /// </summary>
    public static JSValue EnterCatchBlock(object exception, Isolate isolate, int frameIndex, int registerTop)
    {
        var e = (JavaScriptException)exception;
        if (isolate.InterpreterFrameDepth > frameIndex + 1) isolate.PopFramesTo(frameIndex + 1);
        if (isolate.RegisterStackTop > registerTop) isolate.ReleaseRegisters(registerTop);
        isolate.PendingMessage = e.MessageObject is { } message ? JSValue.FromObject(message) : JSValue.TheHole;
        return e.Value;
    }

    /// <summary>
    /// MaybeGrowFastElements + EnsureWritableFastElements for a store at
    /// <paramref name="index"/> of a map-checked object with fast elements
    /// of <paramref name="kind"/>: makes the elements writable, grows them
    /// for an append (index == length), and updates a JSArray's length.
    /// Returns the elements, or undefined to deoptimize (a store that would
    /// leave the fast kinds, a non-append beyond the length).
    /// </summary>
    public static JSValue MaybeGrowFastElements(Isolate isolate, JSValue obj, int index, int isJSArray, int kind)
    {
        JSObject o = Unsafe.As<JSObject>(obj._obj!);
        // The object's own kind: a map check over several maps may have
        // passed a map of a packed kind where the group's kind is holey.
        ElementsKind e = o.Map.ElementsKind;
        _ = kind;
        int elementsLength = o.Elements.Length;
        int length = isJSArray != 0 ? (int)Unsafe.As<JSArray>(o).Length._num : elementsLength;
        // TryBuildElementStoreOnJSArrayOrJSObject's limit: holey kinds may
        // grow by up to kMaxGap past the capacity, packed arrays only append,
        // and packed non-arrays never grow (that needs a map transition).
        long limit = ElementsKinds.IsHoleyElementsKind(e) ? (long)elementsLength + JSObject.kMaxGap
            : isJSArray != 0 ? length + 1L : elementsLength;
        if ((uint)index >= limit) return default;
        if (o.Elements.IsCowArray) JSObject.EnsureWritableFastElements(isolate, o);
        if (index >= o.Elements.Length && !GrowFastElements(o, e, index + 1)) return default;
        if (isJSArray != 0 && index >= length) Unsafe.As<JSArray>(o).Length = JSValue.FromInt(index + 1);
        return JSValue.FromObject(o.Elements);
    }

    /// <summary>
    /// MaybeGrowFastElements of TryReduceArrayPrototypePush: room for
    /// <paramref name="count"/> elements appended at <paramref name="oldLength"/>
    /// of a map-checked fast JSArray, and the new length. Returns the writable
    /// elements, or undefined to deoptimize (the length would leave the Smi range).
    /// </summary>
    public static JSValue MaybeGrowFastElementsForPush(Isolate isolate, JSValue obj, int oldLength, int count)
    {
        var a = Unsafe.As<JSArray>(obj._obj!);
        long newLength = (long)oldLength + count;
        if (newLength > JSValue.SmiMaxValue || newLength >= FixedArrayBase.kMaxLength) return default;
        if (a.Elements.IsCowArray) JSObject.EnsureWritableFastElements(isolate, a);
        if (newLength > a.Elements.Length && !GrowFastElements(a, a.Map.ElementsKind, (int)newLength)) return default;
        a.Length = JSValue.FromInt((int)newLength);
        return JSValue.FromObject(a.Elements);
    }

    /// <summary>Grows fast elements to hold <paramref name="needed"/> elements (Runtime_GrowArrayElements / the CSA grow path).</summary>
    static bool GrowFastElements(JSObject o, ElementsKind e, int needed)
    {
        if (needed > FixedArrayBase.kMaxLength) return false;
        FixedArrayBase elements = o.Elements;
        // New capacity from NewElementsCapacity.
        int capacity = JSObject.NewElementsCapacity(needed);
        if (ElementsKinds.IsDoubleElementsKind(e))
        {
            var grown = new FixedDoubleArray(capacity);
            // An empty double array's elements are the empty FixedArray.
            int oldLength = 0;
            if (elements is FixedDoubleArray old)
            {
                Array.Copy(old._data, grown._data, old.Length);
                oldLength = old.Length;
            }
            grown._data.AsSpan(oldLength).Fill(FixedDoubleArray.HoleNaN);
            o.Elements = grown;
        }
        else
        {
            var grown = new FixedArray(capacity);
            var old = (FixedArray)elements;
            Array.Copy(old._data, grown._data, old.Length);
            grown._data.AsSpan(old.Length).Fill(JSValue.TheHole);
            o.Elements = grown;
        }
        return true;
    }

    /// <summary>
    /// TryReduceArrayPrototypePop's nodes for a map-checked fast JSArray: the
    /// last element (a hole is undefined) with the hole stored in its place and
    /// the length decremented; undefined for an empty array.
    /// </summary>
    public static JSValue ArrayPop(Isolate isolate, JSValue obj)
    {
        var a = Unsafe.As<JSArray>(obj._obj!);
        int length = (int)a.Length._num;
        if (length == 0) return JSValue.Undefined;
        int newLength = length - 1;
        ElementsKind kind = a.Map.ElementsKind;
        JSValue value;
        if (ElementsKinds.IsDoubleElementsKind(kind))
        {
            var elements = Unsafe.As<FixedDoubleArray>(a.Elements);
            value = elements.IsTheHole(newLength) ? JSValue.Undefined : JSValue.FromNumber(elements._data[newLength]);
            elements.SetTheHole(newLength);
        }
        else
        {
            if (a.Elements.IsCowArray) JSObject.EnsureWritableFastElements(isolate, a);
            JSValue[] data = Unsafe.As<FixedArray>(a.Elements)._data;
            value = data[newLength];
            if (value.IsTheHole) value = JSValue.Undefined;
            data[newLength] = JSValue.TheHole;
        }
        a.Length = JSValue.FromInt(newLength);
        return value;
    }

    // ---- Strings -------------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static int StringLength(JSValue s) => Unsafe.As<JSString>(s._obj!).Length;

    [MethodImpl(Inline)]
    public static int StringCharCodeAt(JSValue s, int index)
    {
        JSString str = Unsafe.As<JSString>(s._obj!);
        return str is SeqString seq ? seq.Value[index] : str.Get(index);
    }

    /// <summary>StringAt: the one-character string at an index the code checked (as LoadIndexedString).</summary>
    public static JSValue StringAt(Isolate isolate, JSValue s, int index) =>
        isolate.Factory.LookupSingleCharacterStringFromCode(StringCharCodeAt(s, index));

    /// <summary>BuiltinStringFromCharCode: the one-character string of a code unit (ToUint16 of the truncated value).</summary>
    public static JSValue StringFromCharCode(Isolate isolate, int code) =>
        isolate.Factory.LookupSingleCharacterStringFromCode(code & 0xFFFF);

    public static JSValue StringAdd(Isolate isolate, JSValue left, JSValue right) =>
        InterpreterOps.StringAdd(isolate, Unsafe.As<JSString>(left._obj!), Unsafe.As<JSString>(right._obj!));

    /// <summary>A comparison of two strings (StringEqual / StringLessThan ...).</summary>
    public static JSValue StringCompare(JSValue left, JSValue right, int op)
    {
        JSString l = Unsafe.As<JSString>(left._obj!), r = Unsafe.As<JSString>(right._obj!);
        ComparisonResult result = JSString.Compare(l, r);
        bool value = (CompareOperation)op switch
        {
            CompareOperation.kEqual or CompareOperation.kStrictEqual => result == ComparisonResult.Equal,
            CompareOperation.kLessThan => result == ComparisonResult.LessThan,
            CompareOperation.kLessThanOrEqual => result != ComparisonResult.GreaterThan,
            CompareOperation.kGreaterThan => result == ComparisonResult.GreaterThan,
            _ => result != ComparisonResult.LessThan,
        };
        return value ? JSValue.True : JSValue.False;
    }

    // ---- Contexts -----------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue LoadContextSlot(JSValue context, int depth, int index)
    {
        Context c = Unsafe.As<Context>(context._obj!);
        while (depth-- > 0) c = Unsafe.As<Context>(c.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
        return c.Slots[index];
    }

    [MethodImpl(Inline)]
    public static void StoreContextSlot(JSValue context, int depth, int index, JSValue value)
    {
        Context c = Unsafe.As<Context>(context._obj!);
        while (depth-- > 0) c = Unsafe.As<Context>(c.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
        c.Slots[index] = value;
    }

    /// <summary>The current context changes (PushContext / PopContext): isolate.Context and the frame's slot.</summary>
    [MethodImpl(Inline)]
    public static void SetCurrentContext(Isolate isolate, ref JSValue frameContextSlot, JSValue context)
    {
        frameContextSlot = context;
        isolate.Context = Unsafe.As<Context>(context._obj!);
    }

    // ---- Generators (LoadTaggedField / StoreTaggedFieldNoWriteBarrier of JSGeneratorObject fields,
    //      GeneratorStore, GeneratorRestoreRegister) ------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue LoadGeneratorContext(JSValue generator) => Unsafe.As<JSGeneratorObject>(generator._obj!).Context;

    [MethodImpl(Inline)]
    public static JSValue LoadGeneratorInputOrDebugPos(JSValue generator) =>
        Unsafe.As<JSGeneratorObject>(generator._obj!).InputOrDebugPos;

    [MethodImpl(Inline)]
    public static int LoadGeneratorContinuation(JSValue generator) =>
        Unsafe.As<JSGeneratorObject>(generator._obj!).ContinuationValue;

    [MethodImpl(Inline)]
    public static void StoreGeneratorContinuation(JSValue generator, int value) =>
        Unsafe.As<JSGeneratorObject>(generator._obj!).ContinuationValue = value;

    /// <summary>The generator's parameters_and_registers (GeneratorStore writes it element by element).</summary>
    [MethodImpl(Inline)]
    public static JSValue[] GeneratorRegisterFile(JSValue generator) =>
        Unsafe.As<JSGeneratorObject>(generator._obj!).ParametersAndRegisters.Data;

    /// <summary>GeneratorStore's fixed part: the context, the continuation and input_or_debug_pos.</summary>
    [MethodImpl(Inline)]
    public static void GeneratorSuspend(JSValue generator, JSValue context, int suspendId, int bytecodeOffset)
    {
        var g = Unsafe.As<JSGeneratorObject>(generator._obj!);
        g.Context = Unsafe.As<Context>(context._obj!);
        g.ContinuationValue = suspendId;
        g.InputOrDebugPos = JSValue.FromInt(bytecodeOffset);
    }

    /// <summary>
    /// GeneratorRestoreRegister: the saved value, and the slot cleared so the
    /// generator does not keep it alive (V8 writes the stale register sentinel).
    /// </summary>
    [MethodImpl(Inline)]
    public static JSValue GeneratorRestoreRegister(JSValue generator, int index)
    {
        JSValue[] data = Unsafe.As<JSGeneratorObject>(generator._obj!).ParametersAndRegisters.Data;
        JSValue value = data[index];
        data[index] = default;
        return value;
    }

    // ---- Math ----------------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static double Float64Round(double x, int kind) => kind switch
    {
        0 => Math.Floor(x),
        1 => Math.Ceiling(x),
        2 => BuiltinsMath.Float64Round(x),
        _ => Math.Truncate(x),
    };

    [MethodImpl(Inline)]
    public static double Float64Exponentiate(double x, double y) => InternalMath.pow(x, y);

    public static double Float64Ieee754Unary(double x, int builtin) => (Builtin)builtin switch
    {
        Builtin.MathSin => Base.Ieee754.sin(x),
        Builtin.MathCos => Base.Ieee754.cos(x),
        Builtin.MathTan => Base.Ieee754.tan(x),
        Builtin.MathExp => Base.Ieee754.exp(x),
        Builtin.MathLog => Base.Ieee754.log(x),
        Builtin.MathAtan => Base.Ieee754.atan(x),
        Builtin.MathAsin => Base.Ieee754.asin(x),
        Builtin.MathAcos => Base.Ieee754.acos(x),
        Builtin.MathLog2 => Base.Ieee754.log2(x),
        Builtin.MathLog10 => Base.Ieee754.log10(x),
        Builtin.MathCbrt => Base.Ieee754.cbrt(x),
        _ => throw new InvalidOperationException("not an ieee754 unary builtin"),
    };

    [MethodImpl(Inline)]
    public static double Float64Atan2(double y, double x) => Base.Ieee754.atan2(y, x);

    /// <summary>Int32DivideWithOverflow: long.MinValue (deopt) for a fraction, -0, overflow or division by zero.</summary>
    [MethodImpl(Inline)]
    public static long Int32Divide(int a, int b)
    {
        if (b == 0) return long.MinValue;
        if (a == 0 && b < 0) return long.MinValue;
        if (a == int.MinValue && b == -1) return long.MinValue;
        int q = a / b;
        if (q * b != a) return long.MinValue;
        return q;
    }

    /// <summary>Int32ModulusWithOverflow: long.MinValue (deopt) for a -0 result or a zero divisor.</summary>
    [MethodImpl(Inline)]
    public static long Int32Modulus(int a, int b)
    {
        if (b == 0) return long.MinValue;
        if (b == -1 || b == int.MinValue && a == int.MinValue)
        {
            // The result is 0 (or -0 for a negative dividend).
            return a < 0 ? long.MinValue : 0;
        }
        int r = a % b;
        if (r == 0 && a < 0) return long.MinValue;
        return r;
    }

    // ---- Calls ---------------------------------------------------------------------------------------------

    /// <summary>
    /// CallKnownJSFunction: a call of the target the feedback named (the
    /// graph checked it), with the arguments in the frame's registers; no call
    /// feedback is collected.
    /// </summary>
    [MethodImpl(Outline)]
    public static JSValue CallKnownJSFunction(Isolate isolate, JSValue target, JSValue receiver, int argsStart, int argc, int mode) =>
        MaglevCalls.Call(isolate, target, receiver, argsStart, argc, (ConvertReceiverMode)mode);

    /// <summary>FastCreateClosure (FastNewClosure): a closure of a map without in-object fields.</summary>
    [MethodImpl(Inline)]
    public static JSValue FastNewClosure(Map map, SharedFunctionInfo shared, Context context, FeedbackCell cell) =>
        new JSFunction(map, shared, context, cell);

    /// <summary>
    /// The InlinedAllocation of a function or block context (CreateContext):
    /// <paramref name="length"/> slots, undefined after the header.
    /// </summary>
    [MethodImpl(Inline)]
    public static JSValue NewContext(Context previous, ScopeInfo scopeInfo, int kind, int length) =>
        new Context((ContextKind)kind, length, previous.NativeContext) { ScopeInfo = scopeInfo, Previous = previous };

    /// <summary>
    /// An escaping InlinedAllocation of an object literal: an object of the
    /// boilerplate's map with its in-object fields.
    /// </summary>
    public static JSValue AllocateObjectLiteral(Map map, JSValue[] fields)
    {
        JSObject obj = JSObject.NewWithInObjectSlots(map);
        for (int i = 0; i < fields.Length; i++) obj.InObjectSlot(i) = fields[i];
        return obj;
    }

    /// <summary>
    /// CreateMappedArguments / CreateUnmappedArguments in an inlined function:
    /// the arguments object of its frame (pushed before this call), whose
    /// receiver slot is the register stack index <paramref name="receiverIndex"/>.
    /// </summary>
    public static JSValue CreateInlinedArguments(Isolate isolate, int receiverIndex, JSFunction function, bool mapped)
    {
        int fp = receiverIndex - InterpreterRuntime.kReceiverOffset;
        int argc = InterpreterRuntime.FrameArgc(isolate, fp);
        return mapped
            ? InterpreterArguments.NewSloppyArguments(isolate, function,
                isolate.RegisterStack[fp + InterpreterRuntime.kContextOffset].As<Context>(), fp, argc)
            : InterpreterArguments.NewStrictArguments(isolate, function, fp, argc);
    }

    /// <summary>
    /// CallForwardVarargs: f.apply(thisArg, arguments) with the frame's
    /// arguments object. An elided object (undefined here) means the frame's
    /// actual arguments are passed; otherwise CallWithArrayLike.
    /// </summary>
    [MethodImpl(Outline)]
    public static JSValue CallForwardArguments(Isolate isolate, ref InterpreterState state, JSValue target, JSValue receiver,
        JSValue argumentsObject)
    {
        if (argumentsObject._obj is not null)
        {
            return Builtins.BuiltinsFunction.CallWithArrayLike(isolate, target, receiver, argumentsObject);
        }
        int argc = state.Argc;
        int fp = state.Fp;
        int start = isolate.AllocateRegisters(argc);
        JSValue[] stack = isolate.RegisterStack;
        for (int i = 0; i < argc; i++) stack[start + i] = stack[fp + InterpreterRuntime.kFirstArgumentOffset - i];
        try
        {
            return MaglevCalls.Call(isolate, target, receiver, start, argc, ConvertReceiverMode.Any);
        }
        finally
        {
            isolate.RegisterStackTop = start;
        }
    }

    /// <summary>Construct of a known base constructor with the receiver FastNewObject allocated.</summary>
    [MethodImpl(Outline)]
    public static JSValue ConstructKnownJSFunction(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, int argsStart,
        int argc) =>
        MaglevCalls.ConstructWithReceiver(isolate, target, receiver, newTarget, argsStart, argc);

    // ConstructKnownJSFunction and the generic Construct with up to three
    // arguments as values (no register window in the frame: lazy frames have
    // none).
    public static JSValue ConstructKnownJSFunction0(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 0, default, default, default);

    public static JSValue ConstructKnownJSFunction1(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, JSValue a0) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 1, a0, default, default);

    public static JSValue ConstructKnownJSFunction2(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, JSValue a0,
        JSValue a1) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 2, a0, a1, default);

    public static JSValue ConstructKnownJSFunction3(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 3, a0, a1, a2);

    public static JSValue ConstructKnownJSFunction4(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 4, a0, a1, a2, a3, default, default);

    public static JSValue ConstructKnownJSFunction5(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3, JSValue a4) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 5, a0, a1, a2, a3, a4, default);

    public static JSValue ConstructKnownJSFunction6(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3, JSValue a4, JSValue a5) =>
        MaglevCalls.ConstructWithReceiverValues(isolate, target, receiver, newTarget, 6, a0, a1, a2, a3, a4, a5);

    public static JSValue ConstructValues0(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 0, default, default, default);

    public static JSValue ConstructValues1(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, JSValue a0) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 1, a0, default, default);

    public static JSValue ConstructValues2(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, JSValue a0,
        JSValue a1) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 2, a0, a1, default);

    public static JSValue ConstructValues3(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 3, a0, a1, a2);

    public static JSValue ConstructValues4(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 4, a0, a1, a2, a3);

    public static JSValue ConstructValues5(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3, JSValue a4) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 5, a0, a1, a2, a3, a4);

    public static JSValue ConstructValues6(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3, JSValue a4, JSValue a5) =>
        ConstructValues(isolate, fv, slot, constructor, newTarget, 6, a0, a1, a2, a3, a4, a5);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ConstructValues(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, int argc,
        JSValue a0, JSValue a1, JSValue a2, JSValue a3 = default, JSValue a4 = default, JSValue a5 = default)
    {
        int window = isolate.AllocateRegisters(argc);
        JSValue[] stack = isolate.RegisterStack;
        if (argc > 0) stack[window] = a0;
        if (argc > 1) stack[window + 1] = a1;
        if (argc > 2) stack[window + 2] = a2;
        if (argc > 3) stack[window + 3] = a3;
        if (argc > 4) stack[window + 4] = a4;
        if (argc > 5) stack[window + 5] = a5;
        try
        {
            return Baseline.BaselineCalls.Construct(isolate, fv, slot, constructor, newTarget, window, argc);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    /// <summary>
    /// `new Array()` with an AllocationSite (MaglevGraphBuilder.TryReduceConstructArrayConstructor):
    /// Runtime_NewArray without arguments, frame-free (no JavaScript runs).
    /// </summary>
    public static JSValue NewArrayFromSite(Isolate isolate, NativeContext nativeContext, JSFunction arrayFunction, AllocationSite site)
    {
        ElementsKind kind = site.GetElementsKind();
        if (nativeContext.GetInitialJSArrayMap(kind) is not { } map || !ReferenceEquals(arrayFunction.InitialMap, nativeContext.GetInitialJSArrayMap(ElementsKind.PACKED_SMI_ELEMENTS)))
        {
            return Builtins.BuiltinsArray.NewArray(isolate, arrayFunction, arrayFunction, [], site);
        }
        var array = (JSArray)isolate.Factory.NewJSObjectFromMap(map);
        if (AllocationSite.ShouldTrack(kind)) array.AllocationMementoSite = site;
        isolate.Factory.NewJSArrayStorage(array, 0, 0, Factory.ArrayStorageAllocationMode.DONT_INITIALIZE_ARRAY_ELEMENTS);
        JSArray.Initialize(isolate, array, JSArray.kPreallocatedArrayElements);
        return array;
    }

    /// <summary>CheckConstructResult: an object result replaces the constructed receiver.</summary>
    [MethodImpl(Inline)]
    public static JSValue ConstructResult(JSValue result, JSValue receiver) => result.IsJSReceiver ? result : receiver;

    /// <summary>FastNewObject.</summary>
    public static JSValue FastNewObject(Isolate isolate, Map initialMap) => isolate.Factory.NewJSObjectFromMap(initialMap);

    // ---- Throw, interrupts ---------------------------------------------------------------------------------

    public static void Throw(Isolate isolate, JSValue exception)
    {
        JSMessageObject? message = InterpreterExecution.CreateMessageForThrow(isolate, exception);
        throw new JavaScriptException(exception, message);
    }

    public static void ReThrow(Isolate isolate, JSValue exception)
    {
        JSMessageObject? message = isolate.PendingMessage.HeapObjectOrNull as JSMessageObject;
        throw new JavaScriptException(exception, message);
    }

    // HandleNoHeapWritesInterrupt: serves pending interrupts at a loop back
    // edge (loops with calls), or exits to the interpreter for the ones a
    // loop without calls cannot defer (kInterrupt).
    [MethodImpl(Inline)]
    public static bool HasPendingInterrupts(Isolate isolate) => isolate.StackGuard.HasPendingInterrupts;

    [MethodImpl(Inline)]
    public static bool HasUndeferrableInterrupts(Isolate isolate) => isolate.StackGuard.HasUndeferrableInterrupts;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void HandleInterruptsSlow(Isolate isolate) => isolate.StackGuard.HandleInterrupts();

    /// <summary>The global proxy of a function's native context (the receiver a sloppy callee sees for undefined).</summary>
    /// <summary>The context of a function (a closure of a feedback cell inlined: its frame's context).</summary>
    public static JSValue ContextOfFunction(JSValue function) => ((JSFunction)function._obj!).Context;

    public static JSValue GlobalProxyOfFunction(JSValue function) =>
        ((JSFunction)function._obj!).Context.NativeContext.GlobalProxyObject;

    public static void Unreachable() => throw new InvalidOperationException("V8Sharp: unreachable Maglev code");

    // ---- Inlined frames ------------------------------------------------------------------------------------

    /// <summary>
    /// EnterInlinedFrame: pushes the interpreter frame of an inlined function
    /// (its parameter slots, fixed slots and register window above the stack
    /// top, and a frame record) and returns its frame pointer. The caller
    /// writes the receiver and arguments.
    /// </summary>
    public static int EnterInlinedFrame(Isolate isolate, JSFunction function, BytecodeArray bytecode, FeedbackVector? vector, int argc,
        bool isConstruct)
    {
        int formal = bytecode.ParameterCount - 1;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + bytecode.RegisterCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;
        // The slots compared before they are stored: a frame pushed again at
        // the same position finds the same values (no GC write barrier).
        ref JSValue fpRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), fp);
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), default);
        Context context = function.Context;
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset), JSValue.FromObject(vector));
        InterpreterRuntime.InitializeFrameSlots(ref fpRef, bytecode, argc);
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Fp = fp;
        frame.Flags = isConstruct ? InterpreterFrameFlags.Maglev | InterpreterFrameFlags.Constructor : InterpreterFrameFlags.Maglev;
        frame.ReturnPc = 0;
        frame.RegisterStart = start;
        return fp;
    }

    /// <summary>LeaveInlinedFrame: pops the frame EnterInlinedFrame (or EnterLazyInlinedFrame) pushed.</summary>
    [MethodImpl(Inline)]
    public static void LeaveInlinedFrame(Isolate isolate, int frameIndex, JSValue callerContext)
    {
        ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[frameIndex];
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = frame.RegisterStart;
        isolate.InterpreterFrameDepth = frameIndex;
        Context context = Unsafe.As<Context>(callerContext._obj!);
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
    }
}

/// <summary>Access to baseline builtins whose signatures the IL cannot use directly.</summary>
internal static class BaselineBuiltinsBridge
{
    public static JSValue TestTypeOf(JSValue v, int literal) => Baseline.BaselineBuiltins.TestTypeOf(v, literal);
}

/// <summary>
/// The shape of a literal boilerplate Maglev code copies (CloneObjectLiteral):
/// a fast-mode object without elements whose object-valued fields are such
/// boilerplates or arrays of primitives (their copy-on-write elements shared).
/// </summary>
/// <remarks>
/// Deviation: the runtime's copy (StructureWalk) gives the nested arrays
/// allocation mementos of their sites; these copies do not, so elements kind
/// changes of arrays Maglev code created do not reach the site.
/// </remarks>
public sealed class LiteralShape
{
    public JSObject Boilerplate = null!;
    public (FieldIndex Index, LiteralShape Shape)[] Nested = [];

    public static LiteralShape? TryCreate(JSObject boilerplate, int depth)
    {
        if (depth > 3 || boilerplate.Map.IsDeprecated || !boilerplate.HasFastProperties) return null;
        if (boilerplate is JSArray array)
        {
            if (!ElementsKinds.IsFastElementsKind(array.Map.ElementsKind)) return null;
            if (array.Elements is FixedArray elements)
            {
                for (int i = 0; i < elements.Length; i++) if (elements._data[i].HeapObjectOrNull is JSObject) return null;
            }
            return new LiteralShape { Boilerplate = boilerplate };
        }
        if (boilerplate.Map.InstanceType != InstanceType.JSObjectType || boilerplate.Elements.Length != 0) return null;
        Map map = boilerplate.Map;
        DescriptorArray descriptors = map.InstanceDescriptors;
        int count = map.NumberOfOwnDescriptors;
        List<(FieldIndex, LiteralShape)>? nested = null;
        for (int i = 0; i < count; i++)
        {
            PropertyDetails details = descriptors.GetDetails(new InternalIndex(i));
            if (details.Location != PropertyLocation.Field) continue;
            FieldIndex index = FieldIndex.ForDetails(map, details);
            if (boilerplate.RawFastPropertyAt(index).HeapObjectOrNull is JSObject value)
            {
                if (TryCreate(value, depth + 1) is not { } child) return null;
                (nested ??= []).Add((index, child));
            }
        }
        return new LiteralShape { Boilerplate = boilerplate, Nested = nested?.ToArray() ?? [] };
    }

    /// <summary>The boilerplates are as when the shape was made (none migrated since).</summary>
    public bool IsValid()
    {
        if (Boilerplate.Map.IsDeprecated) return false;
        foreach ((FieldIndex _, LiteralShape child) in Nested) if (!child.IsValid()) return false;
        return true;
    }

    public JSObject Clone(Isolate isolate)
    {
        JSObject copy = isolate.Factory.CopyJSObject(Boilerplate);
        foreach ((FieldIndex index, LiteralShape child) in Nested) copy.FastPropertyAtPut(index, child.Clone(isolate));
        return copy;
    }
}

/// <summary>
/// V8Sharp diagnostics (V8SHARP_MAGLEV_COUNT_GENERIC=1): how often Maglev code
/// calls each builtin and runtime helper (CallBuiltin nodes: generic nodes,
/// calls, allocations), which TierProfiler reports for the measured part of a
/// warm run. The code generator emits a counter increment before each call.
/// </summary>
public static class MaglevGenericCallCounts
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_COUNT_GENERIC") is "1" or "2";
    /// <summary>V8SHARP_MAGLEV_COUNT_GENERIC=2: generic property accesses are counted per site, with their feedback.</summary>
    public static readonly bool BySite = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_COUNT_GENERIC") == "2";
    static readonly Dictionary<string, int> s_ids = new(StringComparer.Ordinal);
    static string[] s_names = new string[64];
    public static long[] Counts = new long[64];

    /// <summary>The counter of <paramref name="name"/> (code generation, any thread).</summary>
    public static int Id(string name)
    {
        lock (s_ids)
        {
            if (s_ids.TryGetValue(name, out int id)) return id;
            id = s_ids.Count;
            if (id >= s_names.Length)
            {
                Array.Resize(ref s_names, id * 2);
                long[] counts = Counts;
                Array.Resize(ref counts, id * 2);
                Counts = counts;
            }
            s_names[id] = name;
            s_ids[name] = id;
            return id;
        }
    }

    public static void Count(int id) => Counts[id]++;

    /// <summary>A copy of the counts and their names.</summary>
    public static (string Name, long Count)[] Snapshot()
    {
        lock (s_ids)
        {
            var result = new (string, long)[s_ids.Count];
            long[] counts = Counts;
            for (int i = 0; i < result.Length; i++) result[i] = (s_names[i], i < counts.Length ? counts[i] : 0);
            return result;
        }
    }
}
