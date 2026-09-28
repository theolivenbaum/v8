// Port of the Atomics builtins:
//   src/builtins/builtins-sharedarraybuffer-gen.cc  Atomics.load/store/
//     exchange/compareExchange/add/sub/and/or/xor (ValidateIntegerTypedArray,
//     ValidateAtomicAccess, AtomicBinopBuiltinCommon)
//   src/builtins/builtins-sharedarraybuffer.cc      Atomics.isLockFree/
//     notify/wait/waitAsync/pause (DoWait)
//   src/execution/futex-emulation.{h,cc}            FutexEmulation (sync
//     waiters only, see FutexEmulation below)
// The atomic operations use System.Threading.Interlocked / Volatile on the
// backing store's managed array; the typed array's elements are aligned to
// their size, so the reinterpreted references are naturally aligned.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterAtomics()
    {
        Register(Builtin.AtomicsLoad, BuiltinsAtomics.AtomicsLoad);
        Register(Builtin.AtomicsStore, BuiltinsAtomics.AtomicsStore);
        Register(Builtin.AtomicsExchange, BuiltinsAtomics.AtomicsExchange);
        Register(Builtin.AtomicsCompareExchange, BuiltinsAtomics.AtomicsCompareExchange);
        Register(Builtin.AtomicsAdd, static (Isolate i, in BuiltinArguments a) => BuiltinsAtomics.AtomicBinop(i, a, BuiltinsAtomics.BinOp.Add, "Atomics.add"));
        Register(Builtin.AtomicsSub, static (Isolate i, in BuiltinArguments a) => BuiltinsAtomics.AtomicBinop(i, a, BuiltinsAtomics.BinOp.Sub, "Atomics.sub"));
        Register(Builtin.AtomicsAnd, static (Isolate i, in BuiltinArguments a) => BuiltinsAtomics.AtomicBinop(i, a, BuiltinsAtomics.BinOp.And, "Atomics.and"));
        Register(Builtin.AtomicsOr, static (Isolate i, in BuiltinArguments a) => BuiltinsAtomics.AtomicBinop(i, a, BuiltinsAtomics.BinOp.Or, "Atomics.or"));
        Register(Builtin.AtomicsXor, static (Isolate i, in BuiltinArguments a) => BuiltinsAtomics.AtomicBinop(i, a, BuiltinsAtomics.BinOp.Xor, "Atomics.xor"));
        Register(Builtin.AtomicsIsLockFree, BuiltinsAtomics.AtomicsIsLockFree);
        Register(Builtin.AtomicsNotify, BuiltinsAtomics.AtomicsNotify);
        Register(Builtin.AtomicsWait, BuiltinsAtomics.AtomicsWait);
        Register(Builtin.AtomicsWaitAsync, BuiltinsAtomics.AtomicsWaitAsync);
        Register(Builtin.AtomicsPause, BuiltinsAtomics.AtomicsPause);
    }
}

/// <summary>The Atomics builtins.</summary>
public static class BuiltinsAtomics
{
    public enum BinOp { Add, Sub, And, Or, Xor, Exchange }

    static JSString Str(Isolate isolate, string s) => isolate.Factory.NewStringFromAsciiChecked(s);

    /// <summary>
    /// SharedArrayBufferBuiltinsAssembler::ValidateIntegerTypedArray: a typed array
    /// that is valid for <paramref name="accessMode"/> and of an integer kind
    /// (not Float16/32/64 or Uint8Clamped).
    /// </summary>
    static JSTypedArray ValidateIntegerTypedArray(Isolate isolate, JSValue maybeArray, string methodName,
        TypedArrayAccessMode accessMode, out ElementsKind kind)
    {
        if (maybeArray.HeapObjectOrNull is not JSTypedArray array)
        {
            isolate.ThrowTypeError(MessageTemplate.NotIntegerTypedArray, maybeArray);
            kind = default;
            return null!;
        }
        ThrowIfInvalid(isolate, array, accessMode, methodName);
        kind = TypedArrayElementsOps.BaseKind(array.Kind);
        if (kind is ElementsKind.FLOAT16_ELEMENTS or ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS or
            ElementsKind.UINT8_CLAMPED_ELEMENTS)
        {
            isolate.ThrowTypeError(MessageTemplate.NotIntegerTypedArray, maybeArray);
        }
        return array;
    }

    /// <summary>IsJSArrayBufferViewValid, failing into the validate_failed label.</summary>
    static void ThrowIfInvalid(Isolate isolate, JSTypedArray array, TypedArrayAccessMode accessMode, string methodName)
    {
        if (TypedArrayElementsOps.TryGetLengthAndValidate(array, accessMode, out _)) return;
        isolate.ThrowTypeError(JSTypedArray.ValidateErrorMessage(isolate, accessMode), Str(isolate, methodName));
    }

    /// <summary>ValidateAtomicAccess: ToIndex(index) &lt; length, else kInvalidAtomicAccessIndex.</summary>
    static int ValidateAtomicAccess(Isolate isolate, JSTypedArray array, JSValue index)
    {
        ulong arrayLength = array.GetLength();
        if (!BuiltinsTypedArray.ToIndex(isolate, index, out ulong accessIndex) || accessIndex >= arrayLength)
        {
            isolate.ThrowRangeError(MessageTemplate.InvalidAtomicAccessIndex);
        }
        return (int)accessIndex;
    }

    /// <summary>CheckJSTypedArrayIndex: the index is still in bounds after the value conversions.</summary>
    static void CheckJSTypedArrayIndex(Isolate isolate, JSTypedArray array, int index, TypedArrayAccessMode accessMode,
        string methodName)
    {
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, accessMode, out ulong length) || (ulong)index >= length)
        {
            isolate.ThrowTypeError(JSTypedArray.ValidateErrorMessage(isolate, accessMode), Str(isolate, methodName));
        }
    }

    /// <summary>The element's bytes in the backing store.</summary>
    static Span<byte> ElementBytes(JSTypedArray array, int index, int size) =>
        array.Buffer.BackingStoreBuffer.AsSpan((int)array.ByteOffset + index * size, size);

    /// <summary>ToInteger_Inline: ToIntegerOrInfinity with -0 as +0.</summary>
    static double ToIntegerValue(Isolate isolate, JSValue value)
    {
        double d = ObjectOps.IntegerValue(isolate, value);
        return d == 0 ? 0 : d;
    }

    static int ElementSize(ElementsKind kind) => kind switch
    {
        ElementsKind.INT8_ELEMENTS or ElementsKind.UINT8_ELEMENTS => 1,
        ElementsKind.INT16_ELEMENTS or ElementsKind.UINT16_ELEMENTS => 2,
        ElementsKind.INT32_ELEMENTS or ElementsKind.UINT32_ELEMENTS => 4,
        _ => 8,
    };

    static bool IsBigIntKind(ElementsKind kind) => kind is ElementsKind.BIGINT64_ELEMENTS or ElementsKind.BIGUINT64_ELEMENTS;

    /// <summary>The loaded element as a JS value (Smi/HeapNumber/BigInt).</summary>
    static JSValue ToJS(Isolate isolate, ElementsKind kind, long raw) => kind switch
    {
        ElementsKind.INT8_ELEMENTS => JSValue.FromInt((sbyte)raw),
        ElementsKind.UINT8_ELEMENTS => JSValue.FromInt((byte)raw),
        ElementsKind.INT16_ELEMENTS => JSValue.FromInt((short)raw),
        ElementsKind.UINT16_ELEMENTS => JSValue.FromInt((ushort)raw),
        ElementsKind.INT32_ELEMENTS => JSValue.FromInt((int)raw),
        ElementsKind.UINT32_ELEMENTS => JSValue.FromNumber((uint)raw),
        ElementsKind.BIGINT64_ELEMENTS => BigInt.FromInt64(isolate, raw),
        _ => BigInt.FromUint64(isolate, (ulong)raw),
    };

    // ---- Raw atomic primitives on the backing store ---------------------------------------------------

    static long AtomicLoad(Span<byte> bytes) => bytes.Length switch
    {
        1 => Volatile.Read(ref bytes[0]),
        2 => Volatile.Read(ref Unsafe.As<byte, ushort>(ref bytes[0])),
        4 => Volatile.Read(ref Unsafe.As<byte, uint>(ref bytes[0])),
        _ => Interlocked.Read(ref Unsafe.As<byte, long>(ref bytes[0])),
    };

    static void AtomicStore(Span<byte> bytes, long value)
    {
        switch (bytes.Length)
        {
            case 1: Interlocked.Exchange(ref bytes[0], (byte)value); break;
            case 2: Interlocked.Exchange(ref Unsafe.As<byte, ushort>(ref bytes[0]), (ushort)value); break;
            case 4: Interlocked.Exchange(ref Unsafe.As<byte, int>(ref bytes[0]), (int)value); break;
            default: Interlocked.Exchange(ref Unsafe.As<byte, long>(ref bytes[0]), value); break;
        }
    }

    /// <summary>AtomicCompareExchange: returns the old raw value.</summary>
    static long AtomicCompareExchange(Span<byte> bytes, long expected, long replacement) => bytes.Length switch
    {
        1 => Interlocked.CompareExchange(ref bytes[0], (byte)replacement, (byte)expected),
        2 => Interlocked.CompareExchange(ref Unsafe.As<byte, ushort>(ref bytes[0]), (ushort)replacement, (ushort)expected),
        4 => (uint)Interlocked.CompareExchange(ref Unsafe.As<byte, int>(ref bytes[0]), (int)replacement, (int)expected),
        _ => Interlocked.CompareExchange(ref Unsafe.As<byte, long>(ref bytes[0]), replacement, expected),
    };

    /// <summary>The read-modify-write operations: returns the old raw value.</summary>
    static long AtomicRmw(Span<byte> bytes, BinOp op, long operand)
    {
        switch (bytes.Length)
        {
            case 4:
            {
                ref int cell = ref Unsafe.As<byte, int>(ref bytes[0]);
                int v = (int)operand;
                return (uint)(op switch
                {
                    BinOp.Add => Interlocked.Add(ref cell, v) - v,
                    BinOp.Sub => Interlocked.Add(ref cell, -v) + v,
                    BinOp.And => Interlocked.And(ref cell, v),
                    BinOp.Or => Interlocked.Or(ref cell, v),
                    BinOp.Xor => XorLoop32(ref cell, v),
                    _ => Interlocked.Exchange(ref cell, v),
                });
            }
            case 8:
            {
                ref long cell = ref Unsafe.As<byte, long>(ref bytes[0]);
                return op switch
                {
                    BinOp.Add => Interlocked.Add(ref cell, operand) - operand,
                    BinOp.Sub => Interlocked.Add(ref cell, -operand) + operand,
                    BinOp.And => Interlocked.And(ref cell, operand),
                    BinOp.Or => Interlocked.Or(ref cell, operand),
                    BinOp.Xor => XorLoop64(ref cell, operand),
                    _ => Interlocked.Exchange(ref cell, operand),
                };
            }
            default:
            {
                // 8- and 16-bit: a compare-exchange loop.
                while (true)
                {
                    long old = AtomicLoad(bytes);
                    long updated = op switch
                    {
                        BinOp.Add => old + operand,
                        BinOp.Sub => old - operand,
                        BinOp.And => old & operand,
                        BinOp.Or => old | operand,
                        BinOp.Xor => old ^ operand,
                        _ => operand,
                    };
                    if (AtomicCompareExchange(bytes, old, updated) == old) return old;
                }
            }
        }
    }

    static int XorLoop32(ref int cell, int v)
    {
        while (true)
        {
            int old = Volatile.Read(ref cell);
            if (Interlocked.CompareExchange(ref cell, old ^ v, old) == old) return old;
        }
    }

    static long XorLoop64(ref long cell, long v)
    {
        while (true)
        {
            long old = Interlocked.Read(ref cell);
            if (Interlocked.CompareExchange(ref cell, old ^ v, old) == old) return old;
        }
    }

    // ---- Builtins -------------------------------------------------------------------------------------

    /// <summary>ES #sec-atomics.load.</summary>
    public static JSValue AtomicsLoad(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "Atomics.load";
        JSTypedArray array = ValidateIntegerTypedArray(isolate, args.AtOrUndefined(1), kMethodName, TypedArrayAccessMode.kRead,
            out ElementsKind kind);
        int index = ValidateAtomicAccess(isolate, array, args.AtOrUndefined(2));
        CheckJSTypedArrayIndex(isolate, array, index, TypedArrayAccessMode.kRead, kMethodName);
        return ToJS(isolate, kind, AtomicLoad(ElementBytes(array, index, ElementSize(kind))));
    }

    /// <summary>The operand of a store/RMW: the JS return value and the raw bits.</summary>
    static JSValue ConvertOperand(Isolate isolate, ElementsKind kind, JSValue value, out long raw)
    {
        if (IsBigIntKind(kind))
        {
            BigInt bigint = BigInt.FromObject(isolate, value);
            raw = (long)BigInt.AsUint64(bigint, out _);
            return bigint;
        }
        double integer = ToIntegerValue(isolate, value);
        raw = (uint)Conversions.DoubleToInt32(integer);
        return JSValue.FromNumber(integer);
    }

    /// <summary>ES #sec-atomics.store: returns the converted value.</summary>
    public static JSValue AtomicsStore(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "Atomics.store";
        JSTypedArray array = ValidateIntegerTypedArray(isolate, args.AtOrUndefined(1), kMethodName, TypedArrayAccessMode.kWrite,
            out ElementsKind kind);
        int index = ValidateAtomicAccess(isolate, array, args.AtOrUndefined(2));
        JSValue result = ConvertOperand(isolate, kind, args.AtOrUndefined(3), out long raw);
        CheckJSTypedArrayIndex(isolate, array, index, TypedArrayAccessMode.kWrite, kMethodName);
        AtomicStore(ElementBytes(array, index, ElementSize(kind)), raw);
        return result;
    }

    /// <summary>ES #sec-atomics.exchange.</summary>
    public static JSValue AtomicsExchange(Isolate isolate, in BuiltinArguments args) =>
        AtomicBinop(isolate, args, BinOp.Exchange, "Atomics.exchange");

    /// <summary>AtomicBinopBuiltinCommon (add, sub, and, or, xor, exchange).</summary>
    internal static JSValue AtomicBinop(Isolate isolate, in BuiltinArguments args, BinOp op, string methodName)
    {
        JSTypedArray array = ValidateIntegerTypedArray(isolate, args.AtOrUndefined(1), methodName, TypedArrayAccessMode.kWrite,
            out ElementsKind kind);
        int index = ValidateAtomicAccess(isolate, array, args.AtOrUndefined(2));
        ConvertOperand(isolate, kind, args.AtOrUndefined(3), out long raw);
        CheckJSTypedArrayIndex(isolate, array, index, TypedArrayAccessMode.kWrite, methodName);
        long old = AtomicRmw(ElementBytes(array, index, ElementSize(kind)), op, raw);
        return ToJS(isolate, kind, old);
    }

    /// <summary>ES #sec-atomics.compareexchange.</summary>
    public static JSValue AtomicsCompareExchange(Isolate isolate, in BuiltinArguments args)
    {
        // V8 reports a failed validation of compareExchange as "Atomics.store".
        const string kMethodName = "Atomics.store";
        JSTypedArray array = ValidateIntegerTypedArray(isolate, args.AtOrUndefined(1), kMethodName, TypedArrayAccessMode.kWrite,
            out ElementsKind kind);
        int index = ValidateAtomicAccess(isolate, array, args.AtOrUndefined(2));
        ConvertOperand(isolate, kind, args.AtOrUndefined(3), out long expected);
        ConvertOperand(isolate, kind, args.AtOrUndefined(4), out long replacement);
        CheckJSTypedArrayIndex(isolate, array, index, TypedArrayAccessMode.kWrite, kMethodName);
        long old = AtomicCompareExchange(ElementBytes(array, index, ElementSize(kind)), expected, replacement);
        return ToJS(isolate, kind, old);
    }

    /// <summary>ES #sec-atomics.islockfree.</summary>
    public static JSValue AtomicsIsLockFree(Isolate isolate, in BuiltinArguments args)
    {
        double size = ObjectOps.ToNumber(isolate, args.AtOrUndefined(1)).Number;
        // V8 has lock free atomics for all sizes on all supported first-class architectures.
        return JSValue.FromBoolean(size is 1 or 2 or 4 or 8);
    }

    /// <summary>ValidateIntegerTypedArray (builtins-sharedarraybuffer.cc) for wait/notify: Int32Array or BigInt64Array.</summary>
    static JSTypedArray ValidateWaitableTypedArray(Isolate isolate, JSValue obj, string methodName)
    {
        if (obj.HeapObjectOrNull is JSTypedArray typedArray)
        {
            if (typedArray.IsDetachedOrOutOfBounds)
            {
                isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, Str(isolate, methodName));
            }
            ElementsKind kind = TypedArrayElementsOps.BaseKind(typedArray.Kind);
            if (kind is ElementsKind.INT32_ELEMENTS or ElementsKind.BIGINT64_ELEMENTS) return typedArray;
        }
        isolate.ThrowTypeError(MessageTemplate.NotInt32OrBigInt64TypedArray, obj);
        return null!;
    }

    /// <summary>ValidateAtomicAccess (builtins-sharedarraybuffer.cc): Object::ToIndex, then the bounds check.</summary>
    static int ValidateAtomicAccessSlow(Isolate isolate, JSTypedArray typedArray, JSValue requestIndex)
    {
        ulong typedArrayLength = typedArray.GetLength();
        JSValue accessIndexObj = ObjectOps.ToIndex(isolate, requestIndex, MessageTemplate.InvalidAtomicAccessIndex);
        double accessIndex = accessIndexObj.Number;
        if (accessIndex >= typedArrayLength) isolate.ThrowRangeError(MessageTemplate.InvalidAtomicAccessIndex);
        return (int)accessIndex;
    }

    /// <summary>ES #sec-atomics.notify.</summary>
    public static JSValue AtomicsNotify(Isolate isolate, in BuiltinArguments args)
    {
        JSValue array = args.AtOrUndefined(1);
        JSValue index = args.AtOrUndefined(2);
        JSValue count = args.AtOrUndefined(3);

        JSTypedArray sta = ValidateWaitableTypedArray(isolate, array, "Atomics.notify");
        // 2. Let i be ? ValidateAtomicAccess(typedArray, index).
        int i = ValidateAtomicAccessSlow(isolate, sta, index);

        // 3-4. Let c be +∞, or max(ToInteger(count), 0).
        uint c;
        if (count.IsUndefined)
        {
            c = uint.MaxValue;
        }
        else
        {
            double countDouble = ObjectOps.IntegerValue(isolate, count);
            if (countDouble < 0) countDouble = 0;
            else if (countDouble > uint.MaxValue) countDouble = uint.MaxValue;
            c = (uint)countDouble;
        }

        // 10. If IsSharedArrayBuffer(buffer) is false, return 0.
        JSArrayBuffer arrayBuffer = sta.Buffer;
        if (!arrayBuffer.IsShared) return JSValue.Zero;

        int size = TypedArrayElementsOps.BaseKind(sta.Kind) == ElementsKind.BIGINT64_ELEMENTS ? 8 : 4;
        long wakeAddr = (long)sta.ByteOffset + (long)i * size;
        return JSValue.FromInt(FutexEmulation.Wake(arrayBuffer.GetBackingStore()!, wakeAddr, c));
    }

    /// <summary>DoWait.</summary>
    static JSValue DoWait(Isolate isolate, bool isAsync, JSValue array, JSValue index, JSValue value, JSValue timeout)
    {
        // 1. Let buffer be ? ValidateIntegerTypedArray(typedArray, true).
        JSTypedArray sta = ValidateWaitableTypedArray(isolate, array, "Atomics.wait");
        // 2. If IsSharedArrayBuffer(buffer) is false, throw a TypeError exception.
        if (!sta.Buffer.IsShared) return isolate.ThrowTypeError(MessageTemplate.NotSharedTypedArray, array);
        // 3. Let i be ? ValidateAtomicAccess(typedArray, index).
        int i = ValidateAtomicAccessSlow(isolate, sta, index);

        // 4-6. Let v be ? ToBigInt64(value) or ? ToInt32(value).
        bool is64 = TypedArrayElementsOps.BaseKind(sta.Kind) == ElementsKind.BIGINT64_ELEMENTS;
        long v;
        if (is64)
        {
            v = BigInt.AsInt64(BigInt.FromObject(isolate, value), out _);
        }
        else
        {
            v = Conversions.DoubleToInt32(ObjectOps.ToNumber(isolate, value).Number);
        }

        // 7-8. Let q be ? ToNumber(timeout); t is +∞ for NaN, else max(q, 0).
        double timeoutNumber;
        if (timeout.IsUndefined)
        {
            timeoutNumber = double.PositiveInfinity;
        }
        else
        {
            timeoutNumber = ObjectOps.ToNumber(isolate, timeout).Number;
            if (double.IsNaN(timeoutNumber)) timeoutNumber = double.PositiveInfinity;
            else if (timeoutNumber < 0) timeoutNumber = 0;
        }

        // 9. If mode is sync and AgentCanSuspend() is false, throw a TypeError exception.
        if (!isAsync && !isolate.AllowAtomicsWait)
        {
            isolate.ThrowTypeError(MessageTemplate.AtomicsOperationNotAllowed, Str(isolate, "Atomics.wait"));
        }

        JSArrayBuffer arrayBuffer = sta.Buffer;
        int size = is64 ? 8 : 4;
        long addr = (long)sta.ByteOffset + (long)i * size;
        BackingStore store = arrayBuffer.GetBackingStore()!;
        if (!isAsync)
        {
            return Str(isolate, FutexEmulation.WaitSync(store, addr, v, is64, timeoutNumber));
        }
        return WaitAsync(isolate, store, addr, v, is64, timeoutNumber);
    }

    /// <summary>
    /// FutexEmulation::WaitAsync: the { async, value } result object. The
    /// "not-equal" and immediate "timed-out" results are complete.
    /// </summary>
    static JSValue WaitAsync(Isolate isolate, BackingStore store, long addr, long value, bool is64, double relTimeoutMs)
    {
        JSObject result = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        JSString asyncKey = isolate.Factory.InternalizeString("async");
        JSString valueKey = isolate.Factory.InternalizeString("value");
        if (FutexEmulation.LoadValue(store, addr, is64) != value)
        {
            JSReceiver.CreateDataProperty(isolate, result, new PropertyKey(isolate, asyncKey), JSValue.False, ShouldThrow.ThrowOnError);
            JSReceiver.CreateDataProperty(isolate, result, new PropertyKey(isolate, valueKey), Str(isolate, "not-equal"), ShouldThrow.ThrowOnError);
            return result;
        }
        if (relTimeoutMs == 0)
        {
            JSReceiver.CreateDataProperty(isolate, result, new PropertyKey(isolate, asyncKey), JSValue.False, ShouldThrow.ThrowOnError);
            JSReceiver.CreateDataProperty(isolate, result, new PropertyKey(isolate, valueKey), Str(isolate, "timed-out"), ShouldThrow.ThrowOnError);
            return result;
        }
        JSPromise promiseCapability = PromiseBuiltins.NewJSPromise(isolate);
        FutexEmulation.AddAsyncWaiter(isolate, store, addr, promiseCapability,
            double.IsPositiveInfinity(relTimeoutMs) ? -1 : relTimeoutMs);
        // 26. Perform ! CreateDataPropertyOrThrow(resultObject, "async", true).
        // 27. Perform ! CreateDataPropertyOrThrow(resultObject, "value",
        // promiseCapability.[[Promise]]).
        // 28. Return resultObject.
        JSReceiver.CreateDataProperty(isolate, result, new PropertyKey(isolate, asyncKey), JSValue.True, ShouldThrow.ThrowOnError);
        JSReceiver.CreateDataProperty(isolate, result, new PropertyKey(isolate, valueKey), promiseCapability, ShouldThrow.ThrowOnError);
        return result;
    }

    /// <summary>ES #sec-atomics.wait.</summary>
    public static JSValue AtomicsWait(Isolate isolate, in BuiltinArguments args) =>
        DoWait(isolate, false, args.AtOrUndefined(1), args.AtOrUndefined(2), args.AtOrUndefined(3), args.AtOrUndefined(4));

    /// <summary>ES #sec-atomics.waitasync.</summary>
    public static JSValue AtomicsWaitAsync(Isolate isolate, in BuiltinArguments args) =>
        DoWait(isolate, true, args.AtOrUndefined(1), args.AtOrUndefined(2), args.AtOrUndefined(3), args.AtOrUndefined(4));

    /// <summary>ES #sec-atomics.pause.</summary>
    public static JSValue AtomicsPause(Isolate isolate, in BuiltinArguments args)
    {
        JSValue iterationNumber = args.AtOrUndefined(1);
        // 1. If N is neither undefined nor an integral Number, throw a TypeError exception.
        if (!iterationNumber.IsUndefined && !iterationNumber.IsSmi)
        {
            bool integral = iterationNumber.IsNumber && double.IsFinite(iterationNumber.Number) &&
                            Math.Round(iterationNumber.Number, MidpointRounding.ToEven) == iterationNumber.Number;
            if (!integral)
            {
                return isolate.ThrowTypeError(MessageTemplate.ArgumentIsNotUndefinedOrInteger, Str(isolate, "Atomics.pause"));
            }
        }
        // 2. Signal a spin-wait loop.
        Thread.SpinWait(1);
        // 3. Return undefined.
        return JSValue.Undefined;
    }
}

/// <summary>
/// src/execution/futex-emulation.{h,cc} FutexEmulation: a process-wide list of
/// synchronous waiters keyed by backing store and byte address, woken by
/// Atomics.notify. V8 keeps a node per waiting isolate in a global wait list
/// guarded by a mutex; V8Sharp does the same with Monitor.
/// </summary>
public static class FutexEmulation
{
    sealed class Waiter(BackingStore store, long address)
    {
        public readonly BackingStore Store = store;
        public readonly long Address = address;
        public bool Waiting = true;

        // FutexWaitListNode::AsyncState of an Atomics.waitAsync waiter.
        public Isolate? AsyncIsolate;
        public NativeContext? AsyncNativeContext;
        public JSPromise? AsyncPromise;
        public bool IsAsync => AsyncIsolate is not null;
    }

    static readonly object s_mutex = new();
    static readonly List<Waiter> s_waitList = [];

    // FutexWaitList::isolate_promises_to_resolve_: woken async waiters whose
    // promise-resolving task has not run yet.
    static readonly List<Waiter> s_toResolve = [];

    internal static long LoadValue(BackingStore store, long addr, bool is64)
    {
        Span<byte> bytes = store.Buffer.AsSpan((int)addr, is64 ? 8 : 4);
        return is64
            ? Interlocked.Read(ref Unsafe.As<byte, long>(ref bytes[0]))
            : Volatile.Read(ref Unsafe.As<byte, int>(ref bytes[0]));
    }

    /// <summary>FutexEmulation::WaitSync: "ok", "not-equal" or "timed-out".</summary>
    internal static string WaitSync(BackingStore store, long addr, long value, bool is64, double relTimeoutMs)
    {
        var waiter = new Waiter(store, addr);
        lock (s_mutex)
        {
            // Compare under the mutex, so a notify between the check and the
            // wait cannot be lost.
            if (LoadValue(store, addr, is64) != value) return "not-equal";

            s_waitList.Add(waiter);
            bool timedOut = false;
            try
            {
                if (double.IsPositiveInfinity(relTimeoutMs))
                {
                    while (waiter.Waiting) Monitor.Wait(s_mutex);
                }
                else
                {
                    long deadline = Environment.TickCount64 + (long)Math.Ceiling(relTimeoutMs);
                    while (waiter.Waiting)
                    {
                        long remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0)
                        {
                            timedOut = true;
                            break;
                        }
                        Monitor.Wait(s_mutex, (int)Math.Min(remaining, int.MaxValue));
                    }
                }
            }
            finally
            {
                s_waitList.Remove(waiter);
            }
            return timedOut ? "timed-out" : "ok";
        }
    }

    /// <summary>
    /// The kAsync case of FutexEmulation::WaitAsync (the caller has compared
    /// the value): adds a waiter node for <paramref name="promise"/> and posts
    /// the AsyncWaiterTimeoutTask when <paramref name="relTimeoutMs"/> is not
    /// negative (infinite).
    /// </summary>
    internal static void AddAsyncWaiter(Isolate isolate, BackingStore store, long addr, JSPromise promise, double relTimeoutMs)
    {
        var node = new Waiter(store, addr)
        {
            AsyncIsolate = isolate,
            AsyncNativeContext = isolate.NativeContext,
            AsyncPromise = promise,
        };
        lock (s_mutex) s_waitList.Add(node);
        if (relTimeoutMs >= 0)
        {
            isolate.PostNonNestableDelayedTask(_ => HandleAsyncWaiterTimeout(node), relTimeoutMs / 1000);
        }
    }

    /// <summary>FutexEmulation::HandleAsyncWaiterTimeout: resolves with "timed-out" if still waiting.</summary>
    static void HandleAsyncWaiterTimeout(Waiter node)
    {
        lock (s_mutex)
        {
            if (!node.Waiting) return;
            node.Waiting = false;
            s_waitList.Remove(node);
        }
        ResolveAsyncWaiterPromise(node, "timed-out");
    }

    /// <summary>FutexEmulation::ResolveAsyncWaiterPromise: resolves in the waiter's native context.</summary>
    static void ResolveAsyncWaiterPromise(Waiter node, string resultString)
    {
        lock (s_mutex) s_toResolve.Remove(node);
        Isolate isolate = node.AsyncIsolate!;
        Context? saved = isolate.Context;
        isolate.Context = node.AsyncNativeContext;
        try
        {
            PromiseBuiltins.ResolvePromise(isolate, node.AsyncPromise!, isolate.Factory.NewStringFromAsciiChecked(resultString));
        }
        finally
        {
            isolate.Context = saved;
        }
    }

    /// <summary>
    /// FutexEmulation::IsolateDeinit: deletes the nodes belonging to a dying
    /// isolate. Its promises are not resolved and its timeout tasks are
    /// cancelled by <see cref="Isolate.Deinit"/>.
    /// </summary>
    internal static void IsolateDeinit(Isolate isolate)
    {
        lock (s_mutex)
        {
            s_waitList.RemoveAll(node => ReferenceEquals(node.AsyncIsolate, isolate));
            s_toResolve.RemoveAll(node => ReferenceEquals(node.AsyncIsolate, isolate));
        }
    }

    /// <summary>FutexEmulation::NumUnresolvedAsyncPromisesForTesting.</summary>
    internal static int NumUnresolvedAsyncPromisesForTesting(BackingStore store, long addr)
    {
        int count = 0;
        lock (s_mutex)
        {
            foreach (Waiter node in s_toResolve)
            {
                if (ReferenceEquals(node.Store, store) && node.Address == addr) count++;
            }
        }
        return count;
    }

    /// <summary>FutexEmulation::NumWaitersForTesting / NumAsyncWaitersForTesting.</summary>
    internal static int NumWaitersForTesting(BackingStore store, long addr, bool asyncOnly)
    {
        int count = 0;
        lock (s_mutex)
        {
            foreach (Waiter waiter in s_waitList)
            {
                if (!waiter.Waiting || !ReferenceEquals(waiter.Store, store) || waiter.Address != addr) continue;
                if (asyncOnly && !waiter.IsAsync) continue;
                count++;
            }
        }
        return count;
    }

    /// <summary>FutexEmulation::Wake: wakes up to <paramref name="numWaitersToWake"/> waiters; returns the count.</summary>
    internal static int Wake(BackingStore store, long addr, uint numWaitersToWake)
    {
        int wokenCount = 0;
        List<Waiter> woken = [];
        lock (s_mutex)
        {
            foreach (Waiter waiter in s_waitList)
            {
                if (numWaitersToWake == 0) break;
                if (!waiter.Waiting || !ReferenceEquals(waiter.Store, store) || waiter.Address != addr) continue;
                waiter.Waiting = false;
                if (waiter.IsAsync) woken.Add(waiter);
                wokenCount++;
                if (numWaitersToWake != uint.MaxValue) numWaitersToWake--;
            }
            foreach (Waiter node in woken)
            {
                s_waitList.Remove(node);
                s_toResolve.Add(node);
            }
            if (wokenCount > 0) Monitor.PulseAll(s_mutex);
        }
        // NotifyAsyncWaiter: resolve each woken async waiter's promise with "ok"
        // in a task on its isolate's foreground task runner.
        foreach (Waiter node in woken)
        {
            node.AsyncIsolate!.PostNonNestableTask(_ => ResolveAsyncWaiterPromise(node, "ok"));
        }
        return wokenCount;
    }
}
