// Port of src/objects/js-array.h (+ -inl.h) and the JSArray operations of
// src/objects/objects.cc (DefineOwnProperty, AnythingToArrayLength,
// ArraySetLength, SetLength, WouldChangeReadOnlyLength) and objects-inl.h
// (MayHaveReadOnlyLength, HasReadOnlyLength).
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>
/// V8's JSArray. An array is in one of two modes: fast (backing storage is a
/// FixedArray and length &lt;= elements.length) or slow (a NumberDictionary).
/// </summary>
public sealed class JSArray(Map map) : JSObject(map)
{
    /// <summary>Number of element slots to pre-allocate for an empty array.</summary>
    public const int kPreallocatedArrayElements = 4;

    public const int kLengthDescriptorIndex = 0;

    /// <summary>Max. number of elements being copied in Array builtins.</summary>
    public const int kMaxCopyElements = 100;

    public const int kMaxInlineSortLength = 16;

    /// <summary>Valid array indices range from +0 &lt;= i &lt; 2^32 - 1 (kMaxUInt32).</summary>
    public const uint kMaxArrayLength = kMaxElementCount;
    public const uint kMaxArrayIndex = kMaxElementIndex;

    /// <summary>This constant is somewhat arbitrary. Any large enough value would work.</summary>
    public const uint kMaxFastArrayLength = 32 * 1024 * 1024;

    /// <summary>Min. stack size for detecting an Array.prototype.join() call cycle.</summary>
    public const uint kMinJoinStackSize = 2;

    /// <summary>JSArray::kInitialMaxFastElementArray (kMaxRegularHeapObjectSize based).</summary>
    public const int kInitialMaxFastElementArray = (128 * 1024 - 8 - 16 - 8) >> 3;

    /// <summary>The length property (a number in uint32 range).</summary>
    public JSValue Length = JSValue.Zero;

    /// <summary>
    /// The AllocationMemento's site: V8 places a memento behind an array
    /// created from an AllocationSite; V8Sharp keeps the site on the array.
    /// </summary>
    public AllocationSite? AllocationMementoSite;

    /// <summary>JSArray::HasArrayPrototype.</summary>
    public bool HasArrayPrototype(Isolate isolate) => ReferenceEquals(Map.Prototype, isolate.NativeContext.InitialArrayPrototype);

    /// <summary>JSArray::MayHaveReadOnlyLength.</summary>
    public static bool MayHaveReadOnlyLength(Map jsArrayMap)
    {
        Debug.Assert(Map.IsJSArrayMap(jsArrayMap));
        if (jsArrayMap.InstanceDescriptors.NumberOfDescriptors == 0 || jsArrayMap.IsDictionaryMap) return true;

        // Fast path: "length" is the first fast property of arrays with non
        // dictionary properties. Since it's not configurable, it's guaranteed to be
        // the first in the descriptor array.
        var first = new InternalIndex(0);
        return jsArrayMap.InstanceDescriptors.GetDetails(first).IsReadOnly;
    }

    /// <summary>JSArray::HasReadOnlyLength.</summary>
    public static bool HasReadOnlyLength(JSArray array)
    {
        Map map = array.Map;

        // If map guarantees that there can't be a read-only length, we are done.
        if (!MayHaveReadOnlyLength(map)) return false;
        return HasReadOnlyLengthSlowPath(array);
    }

    /// <summary>JSArray::HasReadOnlyLengthSlowPath.</summary>
    public static bool HasReadOnlyLengthSlowPath(JSArray array)
    {
        // Look at the object.
        Isolate isolate = Isolate.Current!;
        var it = new LookupIterator(isolate, array, ReadOnlyRoots.length_string, array,
            LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        Debug.Assert(it.State == LookupIterator.StateKind.ACCESSOR);
        return it.IsReadOnly;
    }

    /// <summary>JSArray::WouldChangeReadOnlyLength.</summary>
    public static bool WouldChangeReadOnlyLength(Isolate isolate, JSArray array, uint index)
    {
        if (!ObjectOps.ToArrayLength(array.Length, out uint length)) throw new InvalidOperationException("invalid length");
        if (length <= index) return HasReadOnlyLength(array);
        return false;
    }

    /// <summary>JSArray::Initialize: storage for <paramref name="capacity"/> elements, filled with holes.</summary>
    public static void Initialize(Isolate isolate, JSArray array, int capacity, int length = 0) =>
        isolate.Factory.NewJSArrayStorage(array, length, capacity,
            Factory.ArrayStorageAllocationMode.INITIALIZE_ARRAY_ELEMENTS_WITH_HOLE);

    /// <summary>JSArray::SetLengthWouldNormalize(heap, new_length).</summary>
    public static bool SetLengthWouldNormalizeForLength(uint newLength) => newLength > kMaxFastArrayLength;

    /// <summary>JSArray::SetLengthWouldNormalize: fast elements and new_length would normalize.</summary>
    public bool SetLengthWouldNormalize(uint newLength)
    {
        if (!HasFastElements) return false;
        uint capacity = (uint)Elements.Length;
        return SetLengthWouldNormalizeForLength(newLength) &&
               ShouldConvertToSlowElements(this, capacity, newLength - 1, out _);
    }

    /// <summary>JSArray::SetLength: initializes the array to a certain length.</summary>
    public static bool SetLength(Isolate isolate, JSArray array, uint newLength)
    {
        if (array.SetLengthWouldNormalize(newLength)) NormalizeElements(isolate, array);
        return array.GetElementsAccessor().SetLength(isolate, array, newLength);
    }

    /// <summary>JSArray::SetContent: the content of the array becomes <paramref name="storage"/>.</summary>
    public static void SetContent(Isolate isolate, JSArray array, FixedArrayBase storage)
    {
        array.Elements = storage;
        array.Length = JSValue.FromInt(storage.Length);
    }

    static bool PropertyKeyToArrayLength(in JSValue value, out uint length)
    {
        Debug.Assert(value.IsNumber || value.IsName);
        if (ObjectOps.ToArrayLength(value, out length)) return true;
        if (value.HeapObjectOrNull is JSString s) return s.AsArrayIndex(out length);
        return false;
    }

    static bool PropertyKeyToArrayIndex(in JSValue indexObj, out uint output) =>
        PropertyKeyToArrayLength(indexObj, out output) && output != uint.MaxValue;

    /// <summary>JSArray::DefineOwnProperty (ES6 9.4.2.1).</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSArray o, JSValue name, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        if (name.HeapObjectOrNull is Name n) name = isolate.Factory.InternalizeName(n);

        // 1. Assert: IsPropertyKey(P) is true. ("P" is |name|.)
        // 2. If P is "length", then:
        if (ReferenceEquals(name.HeapObjectOrNull, ReadOnlyRoots.length_string))
        {
            // 2a. Return ArraySetLength(A, Desc).
            return ArraySetLength(isolate, o, ref desc, shouldThrow);
        }
        // 3. Else if P is an array index, then:
        if (PropertyKeyToArrayIndex(name, out uint index))
        {
            // 3a. Let oldLenDesc be OrdinaryGetOwnProperty(A, "length").
            var oldLenDesc = new PropertyDescriptor();
            GetOwnPropertyDescriptor(isolate, o, ReadOnlyRoots.length_string, ref oldLenDesc);
            // 3b. (Assert)
            // 3c. Let oldLen be oldLenDesc.[[Value]].
            if (!ObjectOps.ToArrayLength(oldLenDesc.Value, out uint oldLen)) throw new InvalidOperationException("invalid length");
            // 3d. Let index be ToUint32(P).
            // (Already done above.)
            // 3e. (Assert)
            // 3f. If index >= oldLen and oldLenDesc.[[Writable]] is false,
            //     return false.
            if (index >= oldLen && oldLenDesc.HasWritable && !oldLenDesc.Writable)
            {
                return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                    MessageTemplate.DefineDisallowed, name);
            }
            // 3g. Let succeeded be OrdinaryDefineOwnProperty(A, P, Desc).
            bool succeeded = OrdinaryDefineOwnProperty(isolate, o, name, ref desc, shouldThrow);
            // 3h. Assert: succeeded is not an abrupt completion.
            //     In our case, if should_throw == kThrowOnError, it can be!
            // 3i. If succeeded is false, return false.
            if (!succeeded) return false;
            // 3j. If index >= oldLen, then:
            if (index >= oldLen)
            {
                // 3j i. Set oldLenDesc.[[Value]] to index + 1.
                oldLenDesc.SetValue(JSValue.FromNumber(index + 1.0));
                // 3j ii. Let succeeded be
                //        OrdinaryDefineOwnProperty(A, "length", oldLenDesc).
                OrdinaryDefineOwnProperty(isolate, o, ReadOnlyRoots.length_string, ref oldLenDesc, shouldThrow);
                // 3j iii. Assert: succeeded is true.
            }
            // 3k. Return true.
            return true;
        }

        // 4. Return OrdinaryDefineOwnProperty(A, P, Desc).
        return OrdinaryDefineOwnProperty(isolate, o, name, ref desc, shouldThrow);
    }

    /// <summary>JSArray::AnythingToArrayLength (part of ES6 9.4.2.4 ArraySetLength).</summary>
    public static bool AnythingToArrayLength(Isolate isolate, JSValue lengthObject, out uint output)
    {
        // Fast path: check numbers and strings that can be converted directly
        // and unobservably.
        if (ObjectOps.ToArrayLength(lengthObject, out output)) return true;
        if (lengthObject.HeapObjectOrNull is JSString s && s.AsArrayIndex(out output)) return true;
        // Slow path: follow steps in ES6 9.4.2.4 "ArraySetLength".
        // 3. Let newLen be ToUint32(Desc.[[Value]]).
        JSValue uint32V = ObjectOps.ToUint32(isolate, lengthObject);
        // 5. Let numberLen be ToNumber(Desc.[[Value]]).
        JSValue numberV = ObjectOps.ToNumber(isolate, lengthObject);
        // 7. If newLen != numberLen, throw a RangeError exception.
        if (uint32V.Number != numberV.Number)
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.InvalidArrayLength));
        }
        if (!ObjectOps.ToArrayLength(uint32V, out output)) throw new InvalidOperationException("invalid length");
        return true;
    }

    /// <summary>JSArray::ArraySetLength (ES6 9.4.2.4).</summary>
    public static bool ArraySetLength(Isolate isolate, JSArray a, ref PropertyDescriptor desc, ShouldThrow? shouldThrow)
    {
        // 1. If the [[Value]] field of Desc is absent, then
        if (!desc.HasValue)
        {
            // 1a. Return OrdinaryDefineOwnProperty(A, "length", Desc).
            return OrdinaryDefineOwnProperty(isolate, a, ReadOnlyRoots.length_string, ref desc, shouldThrow);
        }
        // 2. Let newLenDesc be a copy of Desc.
        // (Actual copying is not necessary.)
        // 3. - 7. Convert Desc.[[Value]] to newLen.
        AnythingToArrayLength(isolate, desc.Value, out uint newLen);
        // 8. Set newLenDesc.[[Value]] to newLen.
        // (Done below, if needed.)
        // 9. Let oldLenDesc be OrdinaryGetOwnProperty(A, "length").
        var oldLenDesc = new PropertyDescriptor();
        GetOwnPropertyDescriptor(isolate, a, ReadOnlyRoots.length_string, ref oldLenDesc);
        // 10. (Assert)
        // 11. Let oldLen be oldLenDesc.[[Value]].
        if (!ObjectOps.ToArrayLength(oldLenDesc.Value, out uint oldLen)) throw new InvalidOperationException("invalid length");
        // 12. If newLen >= oldLen, then
        if (newLen >= oldLen)
        {
            // 8. Set newLenDesc.[[Value]] to newLen.
            // 12a. Return OrdinaryDefineOwnProperty(A, "length", newLenDesc).
            desc.SetValue(JSValue.FromNumber(newLen));
            return OrdinaryDefineOwnProperty(isolate, a, ReadOnlyRoots.length_string, ref desc, shouldThrow);
        }
        // 13. If oldLenDesc.[[Writable]] is false, return false.
        if (!oldLenDesc.Writable ||
            // Also handle the {configurable: true} and enumerable changes
            // since we later use JSArray::SetLength instead of
            // OrdinaryDefineOwnProperty to change the length,
            // and it doesn't have access to the descriptor anymore.
            desc.Configurable ||
            (desc.HasEnumerable && oldLenDesc.Enumerable != desc.Enumerable))
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.RedefineDisallowed, ReadOnlyRoots.length_string);
        }
        // 14. If newLenDesc.[[Writable]] is absent or has the value true,
        // let newWritable be true.
        bool newWritable = !desc.HasWritable || desc.Writable;
        // 15. Else,
        // 15a. Need to defer setting the [[Writable]] attribute to false in case
        //      any elements cannot be deleted.
        // 15b. Let newWritable be false. (It's initialized as "false" anyway.)
        // 15c. Set newLenDesc.[[Writable]] to true.
        // (Not needed.)
        // Most of steps 16 through 19 is implemented by JSArray::SetLength.
        SetLength(isolate, a, newLen);
        // Steps 19d-ii, 20.
        if (!newWritable)
        {
            var readOnly = new PropertyDescriptor();
            readOnly.SetWritable(false);
            OrdinaryDefineOwnProperty(isolate, a, ReadOnlyRoots.length_string, ref readOnly, shouldThrow);
        }
        if (!ObjectOps.ToArrayLength(a.Length, out uint actualNewLen)) throw new InvalidOperationException("invalid length");
        // Steps 19d-v, 21. Return false if there were non-deletable elements.
        bool result = actualNewLen == newLen;
        if (!result)
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.StrictCannotDeleteProperty, JSValue.FromNumber(actualNewLen - 1.0), a);
        }
        return result;
    }
}

/// <summary>V8's IterationKind (src/objects/js-array.h users).</summary>
public enum IterationKind { Keys, Values, Entries }

/// <summary>V8's JSArrayIterator (ES #sec-array-iterator-objects).</summary>
public sealed class JSArrayIterator(Map map) : JSObject(map)
{
    public JSValue IteratedObject;
    public JSValue NextIndex = JSValue.Zero;
    public IterationKind Kind;
}
