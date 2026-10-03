// Port of src/builtins/builtins-arraybuffer.cc and src/builtins/arraybuffer.tq:
// the ArrayBuffer and SharedArrayBuffer constructors and prototype methods
// (slice, resize/grow, transfer, transferToFixedLength, transferToImmutable,
// sliceToImmutable) and getters (byteLength, maxByteLength, resizable,
// growable, detached, immutable), and ArrayBuffer.isView.
using V8Sharp.Base.Numbers;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArrayBuffer()
    {
        Register(Builtin.ArrayBufferConstructor, BuiltinsArrayBuffer.ArrayBufferConstructor);
        Register(Builtin.ArrayBufferConstructor_DoNotInitialize, BuiltinsArrayBuffer.ArrayBufferConstructor_DoNotInitialize);
        Register(Builtin.ArrayBufferPrototypeSlice, BuiltinsArrayBuffer.ArrayBufferPrototypeSlice);
        Register(Builtin.SharedArrayBufferPrototypeSlice, BuiltinsArrayBuffer.SharedArrayBufferPrototypeSlice);
        Register(Builtin.ArrayBufferPrototypeResize, BuiltinsArrayBuffer.ArrayBufferPrototypeResize);
        Register(Builtin.SharedArrayBufferPrototypeGrow, BuiltinsArrayBuffer.SharedArrayBufferPrototypeGrow);
        Register(Builtin.SharedArrayBufferPrototypeGetByteLength, BuiltinsArrayBuffer.SharedArrayBufferPrototypeGetByteLength);
        Register(Builtin.ArrayBufferPrototypeTransfer, BuiltinsArrayBuffer.ArrayBufferPrototypeTransfer);
        Register(Builtin.ArrayBufferPrototypeTransferToFixedLength, BuiltinsArrayBuffer.ArrayBufferPrototypeTransferToFixedLength);
        Register(Builtin.ArrayBufferPrototypeTransferToImmutable, BuiltinsArrayBuffer.ArrayBufferPrototypeTransferToImmutable);
        Register(Builtin.ArrayBufferPrototypeSliceToImmutable, BuiltinsArrayBuffer.ArrayBufferPrototypeSliceToImmutable);
        Register(Builtin.ArrayBufferPrototypeGetByteLength, BuiltinsArrayBuffer.ArrayBufferPrototypeGetByteLength);
        Register(Builtin.ArrayBufferPrototypeGetMaxByteLength, BuiltinsArrayBuffer.ArrayBufferPrototypeGetMaxByteLength);
        Register(Builtin.ArrayBufferPrototypeGetResizable, BuiltinsArrayBuffer.ArrayBufferPrototypeGetResizable);
        Register(Builtin.ArrayBufferPrototypeGetDetached, BuiltinsArrayBuffer.ArrayBufferPrototypeGetDetached);
        Register(Builtin.ArrayBufferPrototypeGetImmutable, BuiltinsArrayBuffer.ArrayBufferPrototypeGetImmutable);
        Register(Builtin.SharedArrayBufferPrototypeGetMaxByteLength, BuiltinsArrayBuffer.SharedArrayBufferPrototypeGetMaxByteLength);
        Register(Builtin.SharedArrayBufferPrototypeGetGrowable, BuiltinsArrayBuffer.SharedArrayBufferPrototypeGetGrowable);
        Register(Builtin.ArrayBufferIsView, BuiltinsArrayBuffer.ArrayBufferIsView);
    }
}

/// <summary>The ArrayBuffer and SharedArrayBuffer builtins.</summary>
public static class BuiltinsArrayBuffer
{
    // CHECK_RECEIVER(JSArrayBuffer, name, method)
    static JSArrayBuffer CheckReceiver(Isolate isolate, in BuiltinArguments args, string method)
    {
        if (args.Receiver.HeapObjectOrNull is JSArrayBuffer buffer) return buffer;
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(method),
            args.Receiver);
        return null!;
    }

    // CHECK_SHARED(expected, name, method)
    static void CheckShared(Isolate isolate, bool expected, JSArrayBuffer buffer, string method)
    {
        if (buffer.IsShared != expected)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(method),
                buffer);
        }
    }

    // CHECK_RESIZABLE(expected, name, method)
    static void CheckResizable(Isolate isolate, bool expected, JSArrayBuffer buffer, string method)
    {
        if (buffer.IsResizableByJs != expected)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(method),
                buffer);
        }
    }

    static JSValue ThrowDetached(Isolate isolate, string method) =>
        isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation, isolate.Factory.NewStringFromAsciiChecked(method));

    static JSValue ThrowImmutable(Isolate isolate, string method) =>
        isolate.ThrowTypeError(MessageTemplate.TypedArrayImmutableBufferErrorOperation,
            isolate.Factory.NewStringFromAsciiChecked(method));

    /// <summary>TryNumberToSize for a Number value.</summary>
    static bool TryNumberToSize(JSValue number, out ulong result)
    {
        if (Conversions.TryNumberToSize(number.Number, out nuint size))
        {
            result = size;
            return true;
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// TryAllocateBackingStore: either the BackingStore or the message
    /// template to throw as RangeError. <paramref name="maxLength"/> is
    /// undefined for non-resizable buffers (V8's null handle).
    /// </summary>
    static BackingStore? TryAllocateBackingStore(Isolate isolate, bool shared, bool resizable, JSValue length,
        JSValue maxLength, bool initialized, out MessageTemplate rangeError)
    {
        rangeError = default;
        ulong maxByteLength = 0;
        BackingStore? backingStore;

        const ulong maxAllocatable = JSArrayBuffer.kMaxByteLength;
        if (!TryNumberToSize(length, out ulong byteLength) || byteLength > maxAllocatable)
        {
            rangeError = MessageTemplate.InvalidArrayBufferLength;
            return null;
        }

        if (!resizable)
        {
            backingStore = BackingStore.Allocate(isolate, byteLength, shared, initialized);
        }
        else
        {
            if (!TryNumberToSize(maxLength, out maxByteLength) || maxByteLength > maxAllocatable)
            {
                rangeError = MessageTemplate.InvalidArrayBufferMaxLength;
                return null;
            }
            if (byteLength > maxByteLength)
            {
                rangeError = MessageTemplate.InvalidArrayBufferMaxLength;
                return null;
            }
            // GetResizableBackingStorePageConfigurationImpl only fails for
            // lengths above kMaxByteLength, which are rejected above.
            backingStore = BackingStore.TryAllocateAndPartiallyCommitMemory(isolate, byteLength, maxByteLength, shared);
        }

        // Range errors bailed out earlier; only the failing allocation needs to be
        // caught here.
        if (backingStore is null)
        {
            rangeError = MessageTemplate.ArrayBufferAllocationFailed;
            return null;
        }
        return backingStore;
    }

    /// <summary>ConstructBuffer.</summary>
    static JSValue ConstructBuffer(Isolate isolate, JSFunction target, JSReceiver newTarget, JSValue length, JSValue maxLength,
        bool hasMaxLength, bool initialized)
    {
        // We first try to convert the sizes and collect any possible range errors. If
        // no errors are observable we create the BackingStore before the
        // JSArrayBuffer to avoid a complex dance during setup. We then always create
        // the AB before throwing a possible error as the creation is observable.
        bool shared = !ReferenceEquals(target, target.Context.NativeContext.ArrayBufferFun);
        bool resizable = hasMaxLength;
        BackingStore? backingStore = TryAllocateBackingStore(isolate, shared, resizable, length, maxLength, initialized,
            out MessageTemplate rangeError);
        var arrayBuffer = (JSArrayBuffer)JSObject.New(isolate, target, newTarget, null);
        bool backingStoreCreationFailed = backingStore is null;
        arrayBuffer.Setup(shared, resizable, backingStore, isolate);
        if (backingStoreCreationFailed) return isolate.ThrowRangeError(rangeError);
        return arrayBuffer;
    }

    /// <summary>ES #sec-arraybuffer-constructor.</summary>
    public static JSValue ArrayBufferConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSFunction target = args.Target;
        if (args.NewTarget.IsUndefined)
        {  // [[Call]]
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, target.Shared.Name());
        }
        // [[Construct]]
        var newTarget = args.NewTarget.As<JSReceiver>();
        JSValue length = args.AtOrUndefined(1);

        JSValue numberLength = ObjectOps.ToInteger(isolate, length);
        if (numberLength.Number < 0.0) return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferLength);

        JSValue numberMaxLength = JSValue.Undefined;
        JSValue options = args.AtOrUndefined(2);
        JSValue maxLength = JSObject.ReadFromOptionsBag(options, ReadOnlyRoots.max_byte_length_string, isolate);

        bool hasMaxLength = !maxLength.IsUndefined;
        if (hasMaxLength)
        {
            numberMaxLength = ObjectOps.ToInteger(isolate, maxLength);
            if (numberLength.Number > numberMaxLength.Number)
            {
                return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferMaxLength);
            }
        }
        return ConstructBuffer(isolate, target, newTarget, numberLength, numberMaxLength, hasMaxLength, initialized: true);
    }

    /// <summary>
    /// ArrayBufferConstructor_DoNotInitialize: an ArrayBuffer with uninitialized
    /// memory (managed arrays are always zeroed).
    /// </summary>
    public static JSValue ArrayBufferConstructor_DoNotInitialize(Isolate isolate, in BuiltinArguments args)
    {
        JSFunction target = isolate.NativeContext.ArrayBufferFun;
        JSValue length = args.AtOrUndefined(1);
        return ConstructBuffer(isolate, target, target, length, JSValue.Undefined, false, initialized: false);
    }

    static JSValue SliceHelper(Isolate isolate, in BuiltinArguments args, string methodName, bool isShared, bool toImmutable)
    {
        JSValue start = args.AtOrUndefined(1);
        JSValue end = args.AtOrUndefined(2);

        // * If Type(O) is not Object, throw a TypeError exception.
        // * If O does not have an [[ArrayBufferData]] internal slot, throw a
        //   TypeError exception.
        JSArrayBuffer arrayBuffer = CheckReceiver(isolate, args, methodName);
        // * [AB] If IsSharedArrayBuffer(O) is true, throw a TypeError exception.
        // * [SAB] If IsSharedArrayBuffer(O) is false, throw a TypeError exception.
        CheckShared(isolate, isShared, arrayBuffer, methodName);

        // * [AB] If IsDetachedBuffer(buffer) is true, throw a TypeError exception.
        if (!isShared && arrayBuffer.WasDetached) return ThrowDetached(isolate, methodName);

        double newLen, final, first;
        {
            // * [AB] Let len be O.[[ArrayBufferByteLength]].
            // * [SAB] Let len be O.[[ArrayBufferByteLength]].
            double len = arrayBuffer.GetByteLength();

            // * Let relativeStart be ? ToInteger(start).
            double relativeStart = ObjectOps.IntegerValue(isolate, start);

            // * If relativeStart < 0, let first be max((len + relativeStart), 0); else
            //   let first be min(relativeStart, len).
            first = relativeStart < 0 ? Math.Max(len + relativeStart, 0.0) : Math.Min(relativeStart, len);

            // * If end is undefined, let relativeEnd be len; else let relativeEnd be ?
            //   ToInteger(end).
            double relativeEnd = end.IsUndefined ? len : ObjectOps.IntegerValue(isolate, end);

            // * If relativeEnd < 0, let final be max((len + relativeEnd), 0); else let
            //   final be min(relativeEnd, len).
            final = relativeEnd < 0 ? Math.Max(len + relativeEnd, 0.0) : Math.Min(relativeEnd, len);

            // * Let newLen be max(final-first, 0).
            newLen = Math.Max(final - first, 0.0);
        }

        JSValue newLenObj = JSValue.FromNumber(newLen);

        // * [AB] Let ctor be ? SpeciesConstructor(O, %ArrayBuffer%).
        // * [SAB] Let ctor be ? SpeciesConstructor(O, %SharedArrayBuffer%).
        JSFunction constructorFun = isShared ? isolate.NativeContext.SharedArrayBufferFun : isolate.NativeContext.ArrayBufferFun;
        JSValue ctor = toImmutable
            // In the sliceToImmutable case we unconditionally create an immutable
            // buffer with the default ctr.
            ? constructorFun
            : ObjectOps.SpeciesConstructor(isolate, args.Receiver.As<JSReceiver>(), constructorFun);

        // * Let new be ? Construct(ctor, newLen).
        JSValue newObj = Execution.New(isolate, ctor, [newLenObj]);

        // * NOTE: Side-effects of the above steps may have detached or resized O.

        // * If new does not have an [[ArrayBufferData]] internal slot, throw a
        //   TypeError exception.
        if (newObj.HeapObjectOrNull is not JSArrayBuffer newArrayBuffer)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked(methodName), newObj);
        }

        // * [AB] If IsSharedArrayBuffer(new) is true, throw a TypeError exception.
        // * [SAB] If IsSharedArrayBuffer(new) is false, throw a TypeError exception.
        CheckShared(isolate, isShared, newArrayBuffer, methodName);

        if (toImmutable)
        {
            newArrayBuffer.MakeImmutable(isolate);

            // * If IsDetachedBuffer(O) is true, throw a TypeError exception.
            // * Let fromBuf be O.[[ArrayBufferData]].
            // * Let currentLen be O.[[ArrayBufferByteLength]].
            // * If currentLen < final, throw a RangeError exception.
            Debug.Assert(!isShared);
            if (arrayBuffer.WasDetached) return ThrowDetached(isolate, methodName);
            ulong currentLen = arrayBuffer.GetByteLength();
            if (currentLen < final) return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferLength);
        }
        else
        {
            // The created ArrayBuffer might or might not be resizable, since the
            // species constructor might return a non-resizable or a resizable buffer.

            // * [AB] If IsDetachedBuffer(new) is true, throw a TypeError exception.
            if (!isShared && newArrayBuffer.WasDetached) return ThrowDetached(isolate, methodName);

            if (!isShared && newArrayBuffer.IsImmutable) return ThrowImmutable(isolate, methodName);

            // * [AB] If SameValue(new, O) is true, throw a TypeError exception.
            if (!isShared && ReferenceEquals(newArrayBuffer, args.Receiver.Object))
            {
                return isolate.ThrowTypeError(MessageTemplate.ArrayBufferSpeciesThis);
            }

            // * [SAB] If new.[[ArrayBufferData]] and O.[[ArrayBufferData]] are the same
            //         Shared Data Block values, throw a TypeError exception.
            if (isShared && ReferenceEquals(newArrayBuffer.BackingStoreBuffer, arrayBuffer.BackingStoreBuffer))
            {
                return isolate.ThrowTypeError(MessageTemplate.SharedArrayBufferSpeciesThis);
            }

            // * If new.[[ArrayBufferByteLength]] < newLen, throw a TypeError exception.
            ulong newArrayBufferByteLength = newArrayBuffer.GetByteLength();
            if (newArrayBufferByteLength < newLen)
            {
                return isolate.ThrowTypeError(isShared ? MessageTemplate.SharedArrayBufferTooShort : MessageTemplate.ArrayBufferTooShort);
            }

            // * [AB] NOTE: Side-effects of the above steps may have detached O.
            // * [AB] If IsDetachedBuffer(O) is true, throw a TypeError exception.
            if (!isShared && arrayBuffer.WasDetached) return ThrowDetached(isolate, methodName);
        }

        // * Let fromBuf be O.[[ArrayBufferData]].
        // * Let toBuf be new.[[ArrayBufferData]].
        // * Perform CopyDataBlockBytes(toBuf, 0, fromBuf, first, newLen).
        ulong firstSize = (ulong)first;
        ulong newLenSize = (ulong)newLen;
        Debug.Assert(newArrayBuffer.GetByteLength() >= newLenSize);

        if (newLenSize != 0)
        {
            ulong fromByteLength = arrayBuffer.GetByteLength();
            if (!toImmutable && !isShared && arrayBuffer.IsResizableByJs)
            {
                // The above steps might have resized the underlying buffer. In that case,
                // only copy the still-accessible portion of the underlying data.
                if (firstSize > fromByteLength) return newArrayBuffer;  // Nothing to copy.
                if (newLenSize > fromByteLength - firstSize) newLenSize = fromByteLength - firstSize;
            }
            Debug.Assert(firstSize <= fromByteLength);
            Debug.Assert(fromByteLength - firstSize >= newLenSize);
            // CopyBytes, or base::Relaxed_Memcpy for shared buffers: a managed
            // block copy has the same (non-atomic, tear-free per byte) semantics.
            arrayBuffer.BackingStoreBuffer.AsSpan((int)firstSize, (int)newLenSize)
                .CopyTo(newArrayBuffer.BackingStoreBuffer.AsSpan(0, (int)newLenSize));
        }

        return newArrayBuffer;
    }

    /// <summary>ES #sec-sharedarraybuffer.prototype.slice.</summary>
    public static JSValue SharedArrayBufferPrototypeSlice(Isolate isolate, in BuiltinArguments args) =>
        SliceHelper(isolate, args, "SharedArrayBuffer.prototype.slice", true, false);

    /// <summary>ES #sec-arraybuffer.prototype.slice.</summary>
    public static JSValue ArrayBufferPrototypeSlice(Isolate isolate, in BuiltinArguments args) =>
        SliceHelper(isolate, args, "ArrayBuffer.prototype.slice", false, false);

    static JSValue ResizeHelper(Isolate isolate, in BuiltinArguments args, string methodName, bool isShared)
    {
        // 1 Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[ArrayBufferMaxByteLength]]).
        JSArrayBuffer arrayBuffer = CheckReceiver(isolate, args, methodName);
        CheckResizable(isolate, true, arrayBuffer, methodName);

        // [RAB] 3. If IsSharedArrayBuffer(O) is true, throw a *TypeError* exception
        // [GSAB] 3. If IsSharedArrayBuffer(O) is false, throw a *TypeError* exception
        CheckShared(isolate, isShared, arrayBuffer, methodName);

        // Let newByteLength to ? ToIntegerOrInfinity(newLength).
        JSValue newLength = args.AtOrUndefined(1);
        JSValue numberNewByteLength = ObjectOps.ToInteger(isolate, newLength);

        // [RAB] If IsDetachedBuffer(O) is true, throw a TypeError exception.
        if (!isShared)
        {
            if (arrayBuffer.WasDetached) return ThrowDetached(isolate, methodName);
            if (arrayBuffer.IsImmutable) return ThrowImmutable(isolate, methodName);
        }

        // [RAB] If newByteLength < 0 or newByteLength >
        // O.[[ArrayBufferMaxByteLength]], throw a RangeError exception.

        // [GSAB] If newByteLength < currentByteLength or newByteLength >
        // O.[[ArrayBufferMaxByteLength]], throw a RangeError exception.
        if (!TryNumberToSize(numberNewByteLength, out ulong newByteLength))
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferResizeLength,
                isolate.Factory.NewStringFromAsciiChecked(methodName));
        }

        BackingStore backingStore = arrayBuffer.GetBackingStore()!;
        Debug.Assert(backingStore.IsResizableByJs);
        Debug.Assert(backingStore.IsShared == isShared);

        if (isShared && newByteLength < backingStore.ByteLength)
        {
            // GrowableSharedArrayBuffer is only allowed to grow.
            return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferResizeLength,
                isolate.Factory.NewStringFromAsciiChecked(methodName));
        }

        if (newByteLength > arrayBuffer.MaxByteLength)
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferResizeLength,
                isolate.Factory.NewStringFromAsciiChecked(methodName));
        }

        // [RAB] Let hostHandled be ? HostResizeArrayBuffer(O, newByteLength).
        // [GSAB] Let hostHandled be ? HostGrowArrayBuffer(O, newByteLength).
        // If hostHandled is handled, return undefined.
        // (No WebAssembly memories in V8Sharp.)

        if (!isShared)
        {
            // [RAB] Let oldBlock be O.[[ArrayBufferData]].
            // [RAB] Let newBlock be ? CreateByteDataBlock(newByteLength).
            // [RAB] Let copyLength be min(newByteLength, O.[[ArrayBufferByteLength]]).
            // [RAB] Perform CopyDataBlockBytes(newBlock, 0, oldBlock, 0, copyLength).
            // [RAB] NOTE: Neither creation of the new Data Block nor copying from the
            // old Data Block are observable. Implementations reserve the right to
            // implement this method as in-place growth or shrinkage.
            if (backingStore.ResizeInPlace(isolate, newByteLength) != BackingStore.ResizeOrGrowResult.kSuccess)
            {
                return isolate.ThrowRangeError(MessageTemplate.OutOfMemory, isolate.Factory.NewStringFromAsciiChecked(methodName));
            }

            // TypedsArrays in optimized code may go out of bounds. Trigger deopts
            // through the ArrayBufferDetaching protector.
            if (newByteLength < arrayBuffer.ByteLength && Protectors.IsArrayBufferDetachingIntact(isolate))
            {
                Protectors.InvalidateArrayBufferDetaching(isolate);
            }

            // [RAB] Set O.[[ArrayBufferByteLength]] to newLength.
            arrayBuffer.ByteLength = newByteLength;
        }
        else
        {
            // [GSAB] (Detailed description of the algorithm omitted.)
            BackingStore.ResizeOrGrowResult result = backingStore.GrowInPlace(isolate, newByteLength);
            if (result == BackingStore.ResizeOrGrowResult.kFailure)
            {
                return isolate.ThrowRangeError(MessageTemplate.OutOfMemory, isolate.Factory.NewStringFromAsciiChecked(methodName));
            }
            if (result == BackingStore.ResizeOrGrowResult.kRace)
            {
                return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferResizeLength,
                    isolate.Factory.NewStringFromAsciiChecked(methodName));
            }
            // Invariant: byte_length for a GSAB is 0 (it needs to be read from the
            // BackingStore).
            Debug.Assert(arrayBuffer.ByteLength == 0);
        }
        return JSValue.Undefined;
    }

    /// <summary>ES #sec-get-sharedarraybuffer.prototype.bytelength.</summary>
    public static JSValue SharedArrayBufferPrototypeGetByteLength(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "get SharedArrayBuffer.prototype.byteLength";
        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[ArrayBufferData]]).
        JSArrayBuffer arrayBuffer = CheckReceiver(isolate, args, kMethodName);
        // 3. If IsSharedArrayBuffer(O) is false, throw a TypeError exception.
        CheckShared(isolate, true, arrayBuffer, kMethodName);

        // 4. Let length be ArrayBufferByteLength(O, SeqCst).
        ulong byteLength = arrayBuffer.GetByteLength();
        // 5. Return F(length).
        return JSValue.FromNumber(byteLength);
    }

    /// <summary>ES #sec-arraybuffer.prototype.resize.</summary>
    public static JSValue ArrayBufferPrototypeResize(Isolate isolate, in BuiltinArguments args) =>
        ResizeHelper(isolate, args, "ArrayBuffer.prototype.resize", false);

    /// <summary>ES #sec-sharedarraybuffer.prototype.grow.</summary>
    public static JSValue SharedArrayBufferPrototypeGrow(Isolate isolate, in BuiltinArguments args) =>
        ResizeHelper(isolate, args, "SharedArrayBuffer.prototype.grow", true);

    enum PreserveResizability { kToFixedLength, kPreserveResizability, kToImmutable }

    static JSValue ArrayBufferTransfer(Isolate isolate, JSArrayBuffer arrayBuffer, JSValue newLength,
        PreserveResizability preserveResizability, string methodName)
    {
        const ulong maxAllocatable = JSArrayBuffer.kMaxByteLength;
        // 2. If IsSharedArrayBuffer(arrayBuffer) is true, throw a TypeError
        // exception.
        CheckShared(isolate, false, arrayBuffer, methodName);

        ulong newByteLength;
        if (newLength.IsUndefined)
        {
            // 3. If newLength is undefined, then
            //   a. Let newByteLength be arrayBuffer.[[ArrayBufferByteLength]].
            newByteLength = arrayBuffer.GetByteLength();
        }
        else
        {
            // 4. Else,
            //   a. Let newByteLength be ? ToIndex(newLength).
            JSValue numberNewByteLength = ObjectOps.ToInteger(isolate, newLength);
            if (numberNewByteLength.Number < 0.0) return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferLength);
            if (!TryNumberToSize(numberNewByteLength, out newByteLength) || newByteLength > maxAllocatable)
            {
                return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferResizeLength,
                    isolate.Factory.NewStringFromAsciiChecked(methodName));
            }
        }

        // 5. If IsDetachedBuffer(arrayBuffer) is true, throw a TypeError exception.
        if (arrayBuffer.WasDetached) return ThrowDetached(isolate, methodName);

        if (arrayBuffer.IsImmutable) return ThrowImmutable(isolate, methodName);

        bool resizable;
        ulong newMaxByteLength;
        if (preserveResizability == PreserveResizability.kPreserveResizability && arrayBuffer.IsResizableByJs)
        {
            // 6. If preserveResizability is preserve-resizability and
            //    IsResizableArrayBuffer(arrayBuffer) is true, then
            //   a. Let newMaxByteLength be arrayBuffer.[[ArrayBufferMaxByteLength]].
            newMaxByteLength = arrayBuffer.MaxByteLength;
            resizable = true;
        }
        else
        {
            // 7. Else,
            //   a. Let newMaxByteLength be empty.
            newMaxByteLength = newByteLength;
            resizable = false;
        }

        // 8. If arrayBuffer.[[ArrayBufferDetachKey]] is not undefined, throw a
        //     TypeError exception.
        if (!arrayBuffer.DetachKey.IsUndefined || !arrayBuffer.IsDetachable)
        {
            return isolate.ThrowTypeError(MessageTemplate.DataCloneErrorNonDetachableArrayBuffer);
        }

        // After this point the steps are not observable and are performed out of
        // spec order.

        JSArrayBuffer resultBuffer;

        // Case 1: We don't need a BackingStore.
        if (newByteLength == 0)
        {
            // 15. Perform ! DetachArrayBuffer(arrayBuffer).
            JSArrayBuffer.Detach(isolate, arrayBuffer);

            // 9. Let newBuffer be ? AllocateArrayBuffer(%ArrayBuffer%, newByteLength,
            //    newMaxByteLength).
            //
            // Nothing to do for steps 10-14.
            resultBuffer = isolate.Factory.NewJSArrayBufferAndBackingStore(0, newMaxByteLength, false, resizable)
                ?? throw new InvalidOperationException("NewJSArrayBufferAndBackingStore(0) failed");
        }
        else
        {
            // Case 2: We can reuse the same BackingStore.
            BackingStore? fromBackingStore = arrayBuffer.GetBackingStore();
            if (fromBackingStore is not null && !fromBackingStore.IsResizableByJs && !resizable &&
                newByteLength == arrayBuffer.GetByteLength())
            {
                // 15. Perform ! DetachArrayBuffer(arrayBuffer).
                JSArrayBuffer.Detach(isolate, arrayBuffer);

                // 9. Let newBuffer be ? AllocateArrayBuffer(%ArrayBuffer%, newByteLength,
                //    newMaxByteLength).
                resultBuffer = isolate.Factory.NewJSArrayBuffer(fromBackingStore);
            }
            else
            {
                // Case 3: We can't reuse the same BackingStore. Copy the buffer.

                if (newByteLength > newMaxByteLength) return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferLength);

                // 9. Let newBuffer be ? AllocateArrayBuffer(%ArrayBuffer%, newByteLength,
                //    newMaxByteLength).
                JSArrayBuffer? newBuffer = isolate.Factory.NewJSArrayBufferAndBackingStore(newByteLength, newMaxByteLength,
                    false, resizable);
                if (newBuffer is null) return isolate.ThrowRangeError(MessageTemplate.ArrayBufferAllocationFailed);

                // 10. Let copyLength be min(newByteLength,
                //    arrayBuffer.[[ArrayBufferByteLength]]).
                // 11. Let fromBlock be arrayBuffer.[[ArrayBufferData]].
                // 12. Let toBlock be newBuffer.[[ArrayBufferData]].
                // 13. Perform CopyDataBlockBytes(toBlock, 0, fromBlock, 0, copyLength).
                // 14. NOTE: Neither creation of the new Data Block nor copying from the
                //     old Data Block are observable. Implementations reserve the right to
                //     implement this method as a zero-copy move or a realloc.
                ulong fromByteLength = arrayBuffer.GetByteLength();
                ulong copyLength = Math.Min(newByteLength, fromByteLength);
                arrayBuffer.BackingStoreBuffer.AsSpan(0, (int)copyLength).CopyTo(newBuffer.BackingStoreBuffer);
                // (The rest of the new block is already zero.)

                // 15. Perform ! DetachArrayBuffer(arrayBuffer).
                JSArrayBuffer.Detach(isolate, arrayBuffer);

                resultBuffer = newBuffer;
            }
        }

        if (preserveResizability == PreserveResizability.kToImmutable) resultBuffer.MakeImmutable(isolate);

        // 16. Return newBuffer.
        return resultBuffer;
    }

    /// <summary>ES #sec-arraybuffer.prototype.transfer.</summary>
    public static JSValue ArrayBufferPrototypeTransfer(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "ArrayBuffer.prototype.transfer";
        // 1. Perform ? RequireInternalSlot(arrayBuffer, [[ArrayBufferData]]).
        JSArrayBuffer arrayBuffer = CheckReceiver(isolate, args, kMethodName);
        return ArrayBufferTransfer(isolate, arrayBuffer, args.AtOrUndefined(1), PreserveResizability.kPreserveResizability,
            kMethodName);
    }

    /// <summary>ES #sec-arraybuffer.prototype.transferToFixedLength.</summary>
    public static JSValue ArrayBufferPrototypeTransferToFixedLength(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "ArrayBuffer.prototype.transferToFixedLength";
        JSArrayBuffer arrayBuffer = CheckReceiver(isolate, args, kMethodName);
        return ArrayBufferTransfer(isolate, arrayBuffer, args.AtOrUndefined(1), PreserveResizability.kToFixedLength, kMethodName);
    }

    /// <summary>ES #sec-arraybuffer.prototype.transferToImmutable.</summary>
    public static JSValue ArrayBufferPrototypeTransferToImmutable(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "ArrayBuffer.prototype.transferToImmutable";
        JSArrayBuffer arrayBuffer = CheckReceiver(isolate, args, kMethodName);
        return ArrayBufferTransfer(isolate, arrayBuffer, args.AtOrUndefined(1), PreserveResizability.kToImmutable, kMethodName);
    }

    /// <summary>ES #sec-arraybuffer.prototype.sliceToImmutable.</summary>
    public static JSValue ArrayBufferPrototypeSliceToImmutable(Isolate isolate, in BuiltinArguments args) =>
        SliceHelper(isolate, args, "ArrayBuffer.prototype.sliceToImmutable", false, true);

    // ---- arraybuffer.tq ----------------------------------------------------------------

    static JSArrayBuffer CastOrThrow(Isolate isolate, JSValue receiver, string functionName)
    {
        if (receiver.HeapObjectOrNull is JSArrayBuffer o) return o;
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(functionName),
            receiver);
        return null!;
    }

    static JSArrayBuffer CastNonSharedOrThrow(Isolate isolate, JSValue receiver, string functionName)
    {
        JSArrayBuffer o = CastOrThrow(isolate, receiver, functionName);
        // 3. If IsSharedArrayBuffer(O) is true, throw a TypeError exception.
        if (o.IsShared)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(functionName),
                receiver);
        }
        return o;
    }

    static JSArrayBuffer CastSharedOrThrow(Isolate isolate, JSValue receiver, string functionName)
    {
        JSArrayBuffer o = CastOrThrow(isolate, receiver, functionName);
        // 3. If IsSharedArrayBuffer(O) is false, throw a TypeError exception.
        if (!o.IsShared)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(functionName),
                receiver);
        }
        return o;
    }

    /// <summary>ES #sec-get-arraybuffer.prototype.bytelength.</summary>
    public static JSValue ArrayBufferPrototypeGetByteLength(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastNonSharedOrThrow(isolate, args.Receiver, "get ArrayBuffer.prototype.byteLength");
        // 4. Let length be O.[[ArrayBufferByteLength]].
        return JSValue.FromNumber(o.ByteLength);
    }

    /// <summary>ES #sec-get-arraybuffer.prototype.maxbytelength.</summary>
    public static JSValue ArrayBufferPrototypeGetMaxByteLength(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastNonSharedOrThrow(isolate, args.Receiver, "get ArrayBuffer.prototype.maxByteLength");
        // 4. If IsDetachedBuffer(O) is true, return 0_F.
        if (o.WasDetached) return JSValue.Zero;
        // 5. If IsResizableArrayBuffer(O) is true, then
        //   a. Let length be O.[[ArrayBufferMaxByteLength]].
        // 6. Else,
        //   a. Let length be O.[[ArrayBufferByteLength]].
        // 7. Return F(length);
        return JSValue.FromNumber(o.IsResizableByJs ? o.MaxByteLength : o.ByteLength);
    }

    /// <summary>ES #sec-get-arraybuffer.prototype.resizable.</summary>
    public static JSValue ArrayBufferPrototypeGetResizable(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastNonSharedOrThrow(isolate, args.Receiver, "get ArrayBuffer.prototype.resizable");
        // 4. Return IsResizableArrayBuffer(O).
        return JSValue.FromBoolean(o.IsResizableByJs);
    }

    /// <summary>ES #sec-get-arraybuffer.prototype.detached.</summary>
    public static JSValue ArrayBufferPrototypeGetDetached(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastNonSharedOrThrow(isolate, args.Receiver, "get ArrayBuffer.prototype.detached");
        // 4. Return IsDetachedBuffer(O).
        return JSValue.FromBoolean(o.WasDetached);
    }

    /// <summary>ES #sec-get-arraybuffer.prototype.immutable.</summary>
    public static JSValue ArrayBufferPrototypeGetImmutable(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastNonSharedOrThrow(isolate, args.Receiver, "get ArrayBuffer.prototype.immutable");
        // 4. Return IsImmutableBuffer(O).
        return JSValue.FromBoolean(o.IsImmutable);
    }

    /// <summary>ES #sec-get-growablesharedarraybuffer.prototype.maxbytelength.</summary>
    public static JSValue SharedArrayBufferPrototypeGetMaxByteLength(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastSharedOrThrow(isolate, args.Receiver, "get SharedArrayBuffer.prototype.maxByteLength");
        // 4. If IsResizableArrayBuffer(O) is true, then
        //   a. Let length be O.[[ArrayBufferMaxByteLength]].
        // 5. Else,
        //   a. Let length be O.[[ArrayBufferByteLength]].
        // 6. Return F(length);
        return JSValue.FromNumber(o.MaxByteLength);
    }

    /// <summary>ES #sec-get-sharedarraybuffer.prototype.growable.</summary>
    public static JSValue SharedArrayBufferPrototypeGetGrowable(Isolate isolate, in BuiltinArguments args)
    {
        JSArrayBuffer o = CastSharedOrThrow(isolate, args.Receiver, "get SharedArrayBuffer.prototype.growable");
        // 4. Return IsResizableArrayBuffer(O).
        return JSValue.FromBoolean(o.IsResizableByJs);
    }

    /// <summary>ES #sec-arraybuffer.isview.</summary>
    public static JSValue ArrayBufferIsView(Isolate isolate, in BuiltinArguments args) =>
        // 1. If Type(arg) is not Object, return false.
        // 2. If arg has a [[ViewedArrayBuffer]] internal slot, return true.
        // 3. Return false.
        JSValue.FromBoolean(args.AtOrUndefined(1).HeapObjectOrNull is JSArrayBufferView);
}
