// Port of IterableForEach, IteratorStep, IteratorClose and
// CloseAndMarkIteratorDone from src/builtins/builtins-iterator-inl.h, as
// Math.sumPrecise uses them. The visitor lambdas become a struct implementing
// IVisitor (no closures); a visitor that throws does so with a JS exception,
// where V8's returns false with the exception pending.
//
// Not ported: the JSTypedArray fast path (typed arrays are not in the object
// model yet) and the JSSetIterator medium fast path; both only skip the
// iteration protocol when its lookup chain is intact, so the generic path
// produces the same result.

namespace V8Sharp.Builtins;

public static class IterableForEach
{
    /// <summary>The int / double / generic visitors of IterableForEach.</summary>
    public interface IVisitor
    {
        bool VisitInt(int value);
        bool VisitDouble(double value);
        bool VisitGeneric(JSValue value);
    }

    static bool Dispatch<TVisitor>(ref TVisitor visitor, JSValue obj) where TVisitor : struct, IVisitor =>
        obj.IsNumber ? visitor.VisitDouble(obj.Number) : visitor.VisitGeneric(obj);

    /// <summary>
    /// IterableForEach&lt;kAllowJSExecution&gt;: visits every value of
    /// <paramref name="items"/>, taking the array fast paths when the
    /// protectors allow it. Returns false when a visitor aborted.
    /// </summary>
    public static bool Run<TVisitor>(Isolate isolate, JSValue items, ref TVisitor visitor, bool allowJSExecution,
        out ulong maxCountOut, ulong? maxCount) where TVisitor : struct, IVisitor
    {
        maxCountOut = 0;

        // Fast path for JSArray.
        if (!allowJSExecution && items.HeapObjectOrNull is JSArray array && Protectors.IsArrayIteratorLookupChainIntact(isolate))
        {
            ElementsKind kind = array.GetElementsKind();
            if (ElementsKinds.IsFastElementsKind(kind) && array.HasArrayPrototype(isolate) &&
                (!ElementsKinds.IsHoleyElementsKind(kind) || Protectors.IsNoElementsIntact(isolate)))
            {
                if (ObjectOps.ToUint32(array.Length, out uint len))
                {
                    maxCountOut = len;
                    if (len == 0) return true;
                    return VisitElements(array.Elements, kind, 0, len, ref visitor) == len;
                }
            }
        }

        // Slow path: Iterator protocol.
        JSReceiver receiver = ObjectOps.ToObject(isolate, items);
        // https://tc39.es/ecma262/#sec-getiterator
        JSValue iteratorFn = ObjectOps.GetMethod(isolate, receiver, ReadOnlyRoots.iterator_symbol);

        if (iteratorFn.IsUndefined)
        {
            isolate.ThrowTypeError(MessageTemplate.NotIterable, receiver);
        }
        JSValue iteratorObj = Execution.Call(isolate, iteratorFn, receiver, []);
        if (!iteratorObj.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.NotAnIterator, iteratorObj);
        }
        var iterator = (JSReceiver)iteratorObj.Object;

        // Medium fast path for JSArrayIterator.
        if (iterator is JSArrayIterator arrayIterator && Protectors.IsArrayIteratorLookupChainIntact(isolate) &&
            Protectors.IsNoElementsIntact(isolate) && arrayIterator.Kind == IterationKind.Values &&
            arrayIterator.IteratedObject.HeapObjectOrNull is JSArray iterated)
        {
            ElementsKind kind = iterated.GetElementsKind();
            if (ElementsKinds.IsFastElementsKind(kind) &&
                (!ElementsKinds.IsHoleyElementsKind(kind) || iterated.HasArrayPrototype(isolate)) &&
                ObjectOps.ToUint32(iterated.Length, out uint len) &&
                ObjectOps.ToUint32(arrayIterator.NextIndex, out uint currentIndex))
            {
                maxCountOut = len;
                if (len == 0) return true;
                if (!allowJSExecution)
                {
                    uint visited;
                    try
                    {
                        visited = currentIndex < len
                            ? VisitElements(iterated.Elements, kind, currentIndex, len, ref visitor)
                            : currentIndex;
                    }
                    catch (JavaScriptException)
                    {
                        CloseAndMarkIteratorDone(isolate, iterator);
                        throw;
                    }
                    if (visited < len)
                    {
                        CloseAndMarkIteratorDone(isolate, iterator);
                        return false;
                    }
                    arrayIterator.NextIndex = JSValue.FromNumber(uint.MaxValue);
                    return true;
                }
            }
        }

        JSValue nextMethod = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.next_string);
        ulong count = 0;

        while (true)
        {
            JSReceiver? resultObj = IteratorStep(isolate, iterator, nextMethod);
            if (resultObj is null) break;  // Done

            // https://tc39.es/ecma262/#sec-iteratorvalue
            JSValue nextValue = ObjectOps.GetProperty(isolate, resultObj, ReadOnlyRoots.value_string);

            if (maxCount.HasValue && ++count > maxCount.Value)
            {
                maxCountOut = count;
                JSObject error = isolate.Factory.NewRangeError(MessageTemplate.StackOverflow);
                try
                {
                    isolate.Throw(error);
                }
                catch (JavaScriptException)
                {
                    CloseAndMarkIteratorDone(isolate, iterator);
                    throw;
                }
            }

            bool ok;
            try
            {
                ok = Dispatch(ref visitor, nextValue);
            }
            catch (JavaScriptException)
            {
                // 7.4.13 IfAbruptCloseIterator (value, iteratorRecord)
                CloseAndMarkIteratorDone(isolate, iterator, completionIsThrow: true);
                throw;
            }
            if (!ok)
            {
                CloseAndMarkIteratorDone(isolate, iterator);
                return false;
            }
        }
        maxCountOut = count;
        return true;
    }

    /// <summary>Visits elements [from, len) of a fast backing store; returns the index where it stopped.</summary>
    static uint VisitElements<TVisitor>(FixedArrayBase elements, ElementsKind kind, uint from, uint len, ref TVisitor visitor)
        where TVisitor : struct, IVisitor
    {
        uint i = from;
        switch (kind)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
            {
                var smiElements = (FixedArray)elements;
                for (; i < len; ++i)
                {
                    if (!visitor.VisitInt((int)smiElements.Get((int)i).Number)) return i;
                }
                break;
            }
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            {
                var smiElements = (FixedArray)elements;
                for (; i < len; ++i)
                {
                    JSValue obj = smiElements.Get((int)i);
                    bool ok = obj.IsTheHole ? visitor.VisitGeneric(JSValue.Undefined) : visitor.VisitInt((int)obj.Number);
                    if (!ok) return i;
                }
                break;
            }
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            {
                var doubleElements = (FixedDoubleArray)elements;
                for (; i < len; ++i)
                {
                    if (!visitor.VisitDouble(doubleElements.GetScalar((int)i))) return i;
                }
                break;
            }
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
            {
                var doubleElements = (FixedDoubleArray)elements;
                for (; i < len; ++i)
                {
                    bool ok = doubleElements.IsTheHole((int)i)
                        ? visitor.VisitGeneric(JSValue.Undefined)
                        : visitor.VisitDouble(doubleElements.GetScalar((int)i));
                    if (!ok) return i;
                }
                break;
            }
            default:
            {
                var fastElements = (FixedArray)elements;
                for (; i < len; ++i)
                {
                    JSValue obj = fastElements.Get((int)i);
                    bool ok = obj.IsTheHole ? visitor.VisitGeneric(JSValue.Undefined) : Dispatch(ref visitor, obj);
                    if (!ok) return i;
                }
                break;
            }
        }
        return i;
    }

    /// <summary>
    /// IteratorStep (https://tc39.es/ecma262/#sec-iteratorstep): the result
    /// object, or null when done.
    /// </summary>
    public static JSReceiver? IteratorStep(Isolate isolate, JSReceiver iterator, JSValue nextMethod)
    {
        // 1. Let result be ? IteratorNext(iteratorRecord).
        JSValue result = Execution.Call(isolate, nextMethod, iterator, []);
        if (!result.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, result);
        }
        var resultObj = (JSReceiver)result.Object;
        // 2. Let done be Completion(IteratorComplete(result)).
        JSValue done = ObjectOps.GetProperty(isolate, resultObj, ReadOnlyRoots.done_string);
        // 5. If done is true, return done.
        if (ObjectOps.BooleanValue(done)) return null;
        // 6. Return result.
        return resultObj;
    }

    /// <summary>
    /// IteratorClose (https://tc39.es/ecma262/#sec-iteratorclose). With a
    /// throw completion the errors of the return method are dropped.
    /// </summary>
    public static void IteratorClose(Isolate isolate, JSReceiver iterator, bool completionIsThrow)
    {
        JSValue returnMethod;
        try
        {
            // 3. Let innerResult be GetMethod(iterator, "return").
            returnMethod = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.return_string);
            if (returnMethod.IsNullOrUndefined) return;
            // 4.c. Set innerResult to Call(return, iterator).
            JSValue innerResult = Execution.Call(isolate, returnMethod, iterator, []);
            // 7. If innerResult.[[Value]] is not an Object, throw a TypeError exception.
            if (!completionIsThrow && !innerResult.IsJSReceiver)
            {
                isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, ReadOnlyRoots.return_string);
            }
        }
        catch (JavaScriptException) when (completionIsThrow)
        {
            // 5. If completion is a throw completion, return ? completion.
        }
    }

    /// <summary>CloseAndMarkIteratorDone: exhausts V8's native iterators, then IteratorClose.</summary>
    public static void CloseAndMarkIteratorDone(Isolate isolate, JSReceiver iterator, bool completionIsThrow = true)
    {
        switch (iterator)
        {
            case JSArrayIterator a:
                a.NextIndex = JSValue.FromNumber(uint.MaxValue);
                break;
            case JSMapIterator m:
                m.Table = ReadOnlyRoots.empty_ordered_hash_map;
                break;
            case JSSetIterator s:
                s.Table = ReadOnlyRoots.empty_ordered_hash_set;
                break;
            case JSStringIterator str:
                str.Index = str.String.Length;
                break;
            case JSRegExpStringIterator r:
                r.Done = true;
                break;
        }
        IteratorClose(isolate, iterator, completionIsThrow);
    }
}
