// The iteration bytecodes' builtins: GetIteratorWithFeedback and
// CallIteratorWithFeedback, ArrayDestructure (src/builtins/iterator.tq),
// ForOfNextHelper and CallIteratorNext (src/codegen/code-stub-assembler.cc),
// and the IterableToList flavours the spread calls and array literals use
// (src/builtins/builtins-iterator-gen.cc).
using V8Sharp.IC;

namespace V8Sharp.Interpreter;

public static class InterpreterIterators
{
    // FeedbackMetadata::GetSlotSize(kCall) and (kLoadProperty).
    const int kCallSlotSize = 2;
    const int kLoadSlotSize = 2;

    /// <summary>GetIterator: GetIteratorWithFeedback + CallIteratorWithFeedback.</summary>
    public static JSValue GetIterator(Isolate isolate, FeedbackVector? fv, int loadSlot, int callSlot, JSValue receiver)
    {
        JSValue iteratorMethod = fv is null
            ? RuntimeGetProperty(isolate, receiver, ReadOnlyRoots.iterator_symbol)
            : LoadIC.LoadNamed(isolate, fv, loadSlot, receiver, ReadOnlyRoots.iterator_symbol);

        InterpreterCalls.CollectCallFeedback(isolate, fv, callSlot, iteratorMethod);
        if (!ObjectOps.IsCallable(iteratorMethod))
        {
            // ThrowIteratorError(receiver).
            isolate.Throw(ErrorUtils.NewIteratorError(isolate, receiver));
        }
        JSValue iterator = InterpreterCalls.CallGeneric(isolate, iteratorMethod, receiver, []);
        if (!iterator.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.SymbolIteratorInvalid);
        }
        return iterator;
    }

    static JSValue RuntimeGetProperty(Isolate isolate, JSValue receiver, Name name)
    {
        if (receiver.IsNullOrUndefined) return ErrorUtils.ThrowLoadFromNullOrUndefined(isolate, receiver, name);
        return ObjectOps.GetProperty(isolate, receiver, name);
    }

    /// <summary>ForOfNext: CodeStubAssembler::ForOfNextHelper. Returns the hole when done.</summary>
    public static JSValue ForOfNext(Isolate isolate, FeedbackVector? fv, int callSlot, JSValue obj, JSValue next)
    {
        // object has already been checked to be an JSReceiver when building
        // the iterator record in bytecode generator (BuildGetIterator).
        if (obj.HeapObjectOrNull is JSArrayIterator arrayIterator && Protectors.IsArrayIteratorLookupChainIntact(isolate) &&
            Protectors.IsNoElementsIntact(isolate))
        {
            JSValue iteratedObject = arrayIterator.IteratedObject;
            if (fv is not null)
            {
                // Collect the map of the iterated object (a LoadIC of Symbol.iterator)
                // and the call feedback of the .next() call, as V8 does for the
                // optimizing tiers.
                int iteratedObjectSlot = callSlot + kCallSlotSize;
                LoadIC.LoadNamed(isolate, fv, iteratedObjectSlot, iteratedObject, ReadOnlyRoots.iterator_symbol);
                InterpreterCalls.CollectCallFeedback(isolate, fv, callSlot, next);
            }

            if (iteratedObject.HeapObjectOrNull is JSArray iteratedArray &&
                ElementsKinds.IsFastElementsKind(iteratedArray.Map.ElementsKind) &&
                (!ElementsKinds.IsHoleyElementsKind(iteratedArray.Map.ElementsKind) ||
                 ReferenceEquals(iteratedArray.Map.Prototype, isolate.NativeContext.InitialArrayPrototype)))
            {
                JSValue currentIndex = arrayIterator.NextIndex;
                if (!currentIndex.IsSmi) return JSValue.TheHole;
                int index = (int)currentIndex._num;
                int length = (int)iteratedArray.Length._num;
                if (index >= length) return JSValue.TheHole;

                JSValue elementValue;
                IterationKind kind = arrayIterator.Kind;
                if (kind == IterationKind.Keys)
                {
                    elementValue = currentIndex;
                }
                else
                {
                    FixedArrayBase elements = iteratedArray.Elements;
                    if (elements is FixedDoubleArray doubleArray)
                    {
                        if (doubleArray.IsTheHole(index)) goto slowPath;
                        elementValue = JSValue.FromNumber(doubleArray._data[index]);
                    }
                    else
                    {
                        elementValue = ((FixedArray)elements)._data[index];
                    }
                    if (elementValue.IsTheHole) elementValue = JSValue.Undefined;
                    if (kind == IterationKind.Entries)
                    {
                        elementValue = isolate.Factory.NewJSArrayWithElements(
                            new FixedArray([currentIndex, elementValue]));
                    }
                }
                arrayIterator.NextIndex = JSValue.FromInt(index + 1);
                return elementValue.IsTheHole ? JSValue.Undefined : elementValue;
            }
        }

    slowPath:
        return CallIteratorNext(isolate, fv, callSlot, obj, next);
    }

    /// <summary>CodeStubAssembler::CallIteratorNext. Returns the hole when done.</summary>
    static JSValue CallIteratorNext(Isolate isolate, FeedbackVector? fv, int callSlot, JSValue iterator, JSValue nextMethod)
    {
        if (!ObjectOps.IsCallable(nextMethod))
        {
            isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, nextMethod));
        }

        int valueSlot = callSlot + kCallSlotSize + kLoadSlotSize;
        int doneSlot = valueSlot + kLoadSlotSize;
        InterpreterCalls.CollectCallFeedback(isolate, fv, callSlot, nextMethod);
        JSValue result = InterpreterCalls.CallGeneric(isolate, nextMethod, iterator, []);

        if (!result.IsJSReceiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, result);
        }

        JSValue done = fv is null
            ? ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.done_string)
            : LoadIC.LoadNamed(isolate, fv, doneSlot, result, ReadOnlyRoots.done_string);
        if (ObjectOps.BooleanValue(done)) return JSValue.TheHole;

        return fv is null
            ? ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.value_string)
            : LoadIC.LoadNamed(isolate, fv, valueSlot, result, ReadOnlyRoots.value_string);
    }

    /// <summary>ArrayDestructure (iterator.tq): the first registers.Length values of the iterable.</summary>
    public static void ArrayDestructure(Isolate isolate, JSValue receiver, Span<JSValue> registers)
    {
        int count = registers.Length;
        if (receiver.HeapObjectOrNull is JSArray array && IteratorHelpers.IsFastJSArrayWithNoCustomIteration(isolate, array) &&
            array.Length.IsSmi)
        {
            int length = (int)array.Length._num;
            int copyLen = count < length ? count : length;
            FixedArrayBase elements = array.Elements;
            if (elements is FixedDoubleArray doubleArray)
            {
                for (int i = 0; i < copyLen; i++)
                {
                    registers[i] = doubleArray.IsTheHole(i) ? JSValue.Undefined : JSValue.FromNumber(doubleArray._data[i]);
                }
            }
            else if (elements is FixedArray fixedArray)
            {
                for (int i = 0; i < copyLen; i++)
                {
                    JSValue value = fixedArray._data[i];
                    registers[i] = value.IsTheHole ? JSValue.Undefined : value;
                }
            }
            else
            {
                goto slow;
            }
            for (int i = copyLen; i < count; i++) registers[i] = JSValue.Undefined;
            return;
        }

    slow:
        IteratorRecord iteratorRecord = IteratorHelpers.GetIterator(isolate, receiver);
        int k = 0;
        for (; k < count; k++)
        {
            if (!IteratorHelpers.IteratorStep(isolate, iteratorRecord, out JSReceiver next)) goto done;
            JSValue nextValue = IteratorHelpers.IteratorValue(isolate, next);
            // The iterator may have run user code; the register stack is stable
            // (the frame is live), so write through the span.
            registers[k] = nextValue;
        }
        IteratorClose(isolate, iteratorRecord.Object);
        return;
    done:
        for (; k < count; k++) registers[k] = JSValue.Undefined;
    }

    /// <summary>iterator::IteratorClose (the normal completion flavour).</summary>
    public static void IteratorClose(Isolate isolate, JSReceiver iterator)
    {
        // 3. Let innerResult be GetMethod(iterator, "return").
        JSValue method = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.return_string);
        if (method.IsNullOrUndefined) return;
        if (!ObjectOps.IsCallable(method))
        {
            isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, method));
        }
        JSValue innerResult = InterpreterCalls.CallGeneric(isolate, method, iterator, []);
        if (!innerResult.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, innerResult);
        }
    }

    /// <summary>CreateArrayFromIterable: IterableToListWithSymbolLookup.</summary>
    public static JSValue IterableToListWithSymbolLookup(Isolate isolate, JSValue iterable) =>
        IteratorHelpers.IterableToListWithSymbolLookup(isolate, iterable);

    /// <summary>
    /// The spread of CallWithSpread / ConstructWithSpread
    /// (CallOrConstructWithSpread: fast arrays are spread directly, holes
    /// read as undefined; everything else goes through the iterator).
    /// </summary>
    public static FixedArray IterableToList(Isolate isolate, JSValue spread)
    {
        if (spread.HeapObjectOrNull is JSArray array && IteratorHelpers.IsFastJSArrayWithNoCustomIteration(isolate, array))
        {
            int length = (int)array.Length.Number;
            var values = new FixedArray(length);
            FixedArrayBase elements = array.Elements;
            if (elements is FixedArray fixedArray)
            {
                for (int i = 0; i < length; i++)
                {
                    JSValue value = fixedArray._data[i];
                    values._data[i] = value.IsTheHole ? JSValue.Undefined : value;
                }
                return values;
            }
            if (elements is FixedDoubleArray doubleArray)
            {
                for (int i = 0; i < length; i++)
                {
                    values._data[i] = doubleArray.IsTheHole(i) ? JSValue.Undefined : JSValue.FromNumber(doubleArray._data[i]);
                }
                return values;
            }
        }

        if (spread.IsNullOrUndefined)
        {
            ErrorUtils.ThrowSpreadArgError(isolate, MessageTemplate.NotIterableNoSymbolLoad, spread);
        }
        JSValue iteratorFn = ObjectOps.GetProperty(isolate, spread, ReadOnlyRoots.iterator_symbol);
        if (!ObjectOps.IsCallable(iteratorFn))
        {
            ErrorUtils.ThrowSpreadArgError(isolate, MessageTemplate.SpreadIteratorSymbolNonCallable, spread);
        }

        IteratorRecord iteratorRecord = IteratorHelpers.GetIterator(isolate, spread, iteratorFn);
        var list = new List<JSValue>();
        while (IteratorHelpers.IteratorStep(isolate, iteratorRecord, out JSReceiver next))
        {
            list.Add(IteratorHelpers.IteratorValue(isolate, next));
        }
        return new FixedArray(list.ToArray());
    }
}
