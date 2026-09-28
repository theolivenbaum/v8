// Port of the Set methods: src/builtins/set-union.tq, set-intersection.tq,
// set-difference.tq, set-symmetric-difference.tq, set-is-subset-of.tq,
// set-is-superset-of.tq, set-is-disjoint-from.tq, and the SetRecord helpers
// of collections.tq (GetSetRecord, GetKeysIterator,
// CheckSetRecordHas{JSSet,JSMap}Methods, ShrinkOrderedHashSetIfNeeded).
//
// The order of the observable steps (the size/has/keys lookups of
// GetSetRecord, the calls of has and keys().next) and the fast paths for
// Set and Map arguments with unmodified iteration follow the .tq files.
namespace V8Sharp.Builtins;

public static partial class CollectionsBuiltins
{
    /// <summary>collections::SetRecord.</summary>
    readonly record struct SetRecord(JSReceiver Object, double Size, JSValue Has, JSValue Keys);

    /// <summary>GetSetRecord (#sec-getsetrecord).</summary>
    static SetRecord GetSetRecord(Isolate isolate, JSValue obj, string methodName)
    {
        // 1. If obj is not an Object, throw a TypeError exception.
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            isolate.ThrowTypeError(MessageTemplate.ArgumentIsNonObject, isolate.Factory.NewStringFromAsciiChecked(methodName));
            return default;
        }

        // 2. Let rawSize be ? Get(obj, "size").
        JSValue rawSize = JSReceiver.GetProperty(isolate, receiver, ReadOnlyRoots.size_string);

        // 3. Let numSize be ? ToNumber(rawSize).
        double numSize = ObjectOps.ToNumber(isolate, rawSize).Number;
        if (double.IsNaN(numSize))
        {
            // 4. NOTE: If rawSize is undefined, then numSize will be NaN.
            // 5. If numSize is NaN, throw a TypeError exception.
            isolate.ThrowTypeError(MessageTemplate.SizeIsNaN);
        }

        // 6. Let intSize be ! ToIntegerOrInfinity(numSize).
        double intSize = Math.Truncate(numSize);
        if (intSize == 0) intSize = 0;

        // 7. If intSize < 0, throw a RangeError exception.
        if (intSize < 0) isolate.ThrowRangeError(MessageTemplate.InvalidSizeValue, JSValue.FromNumber(intSize));

        // 8. Let has be ? Get(obj, "has").
        JSValue has = JSReceiver.GetProperty(isolate, receiver, ReadOnlyRoots.has_string);

        // 9. If IsCallable(has) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(has)) isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, ReadOnlyRoots.has_string));

        // 10. Let keys be ? Get(obj, "keys").
        JSValue keys = JSReceiver.GetProperty(isolate, receiver, ReadOnlyRoots.keys_string);

        // 11. If IsCallable(keys) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(keys)) isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, ReadOnlyRoots.keys_string));

        // 12. Return a new Set Record { [[Set]]: obj, [[Size]]: intSize, [[Has]]:
        // has, [[Keys]]: keys }.
        return new SetRecord(receiver, intSize, has, keys);
    }

    /// <summary>GetKeysIterator (#sec-getkeysiterator).</summary>
    static IteratorRecord GetKeysIterator(Isolate isolate, JSReceiver set, JSValue keys)
    {
        // 1. Let keysIter be ? Call(setRec.[[Keys]], setRec.[[Set]]).
        JSValue keysIter = Execution.Call(isolate, keys, set, []);

        // 2. If keysIter is not an Object, throw a TypeError exception.
        if (keysIter.HeapObjectOrNull is not JSReceiver keysIterObj)
        {
            isolate.ThrowTypeError(MessageTemplate.KeysMethodInvalid);
            return default;
        }

        // 3. Let nextMethod be ? Get(keysIter, "next").
        JSValue nextMethod = JSReceiver.GetProperty(isolate, keysIterObj, ReadOnlyRoots.next_string);

        // 4. If IsCallable(nextMethod) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(nextMethod)) isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, ReadOnlyRoots.next_string));

        // 5. Return a new Iterator Record { [[Iterator]]: keysIter, [[NextMethod]]:
        // nextMethod, [[Done]]: false }.
        return new IteratorRecord(keysIterObj, nextMethod);
    }

    static bool IsBuiltin(JSValue function, Builtin builtin) =>
        function.HeapObjectOrNull is JSFunction f && f.Shared.BuiltinId == builtin;

    /// <summary>
    /// The fast-path "other" of the Set methods: a JSSetWithNoCustomIteration
    /// or JSMapWithNoCustomIteration whose has/keys are the builtins
    /// (CheckSetRecordHasJSSetMethods / CheckSetRecordHasJSMapMethods). Returns
    /// its table (an OrderedHashSet, or an OrderedHashMap whose keys count), or
    /// null for the slow path.
    /// </summary>
    static OrderedHashTable? GetFastOtherTable(Isolate isolate, JSValue other, in SetRecord otherRec)
    {
        switch (other.HeapObjectOrNull)
        {
            case JSSet otherSet when Protectors.IsSetIteratorLookupChainIntact(isolate):
                if (!IsBuiltin(otherRec.Keys, Builtin.SetPrototypeValues) || !IsBuiltin(otherRec.Has, Builtin.SetPrototypeHas)) return null;
                return otherSet.Table;
            case JSMap otherMap when Protectors.IsMapIteratorLookupChainIntact(isolate):
                if (!IsBuiltin(otherRec.Keys, Builtin.MapPrototypeKeys) || !IsBuiltin(otherRec.Has, Builtin.MapPrototypeHas)) return null;
                return otherMap.Table;
            default:
                return null;
        }
    }

    /// <summary>A new JSSet with the initial set map and <paramref name="table"/>.</summary>
    static JSSet NewJSSetWithTable(Isolate isolate, OrderedHashSet table)
    {
        var set = (JSSet)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.JSSetMap);
        set.Table = table;
        return set;
    }

    /// <summary>ShrinkOrderedHashSetIfNeeded.</summary>
    static OrderedHashSet ShrinkOrderedHashSetIfNeeded(Isolate isolate, int numberOfElements, OrderedHashSet resultSetData)
    {
        // Shrink the result table if # of element is less than # buckets/2
        if (numberOfElements < resultSetData.NumberOfBuckets / 2)
        {
            resultSetData = OrderedHashSet.Shrink(isolate, resultSetData);
        }
        return resultSetData;
    }

    /// <summary>OrderedHashSetIterator::Next: follows table transitions (the table may change during iteration).</summary>
    static bool NextOrderedHashSetIterator(ref OrderedHashTable table, ref int index, out JSValue key)
    {
        // Transition the table and index in case it was modified during iteration.
        table = OrderedHashTable.TransitionIterator(table, ref index);
        int entry = NextSkipHashTableHoles(table, ref index);
        if (entry < 0)
        {
            key = JSValue.Undefined;
            return false;
        }
        key = table.KeyAtRaw(entry);
        return true;
    }

    // ---- Set.prototype.union (set-union.tq) -------------------------------------------------------

    /// <summary>Set.prototype.union ( other ).</summary>
    public static JSValue SetPrototypeUnion(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.union";
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        OrderedHashSet resultSetData;
        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            resultSetData = o.Table.Clone();
            int usedCapacity = otherTable.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (otherTable.IsDeletedEntry(i)) continue;
                resultSetData = AddToSetTable(isolate, resultSetData, otherTable.KeyAtRaw(i), methodName);
            }
        }
        else
        {
            // 4. Let keysIter be ? GetKeysIterator(otherRec).
            IteratorRecord keysIter = GetKeysIterator(isolate, otherRec.Object, otherRec.Keys);

            // 5. Let resultSetData be a copy of O.[[SetData]].
            resultSetData = o.Table.Clone();

            // 6. Let next be true.
            // 7. Repeat, while next is not false,
            //  a. Set next to ? IteratorStep(keysIter).
            while (IteratorBuiltins.IteratorStep(isolate, keysIter, out JSReceiver nextRecord, fastIteratorResultMap))
            {
                //  b. If next is not false, then
                //      i. Let nextValue be ? IteratorValue(next).
                JSValue nextValue = IteratorBuiltins.IteratorValue(isolate, nextRecord, fastIteratorResultMap);

                //      ii. If nextValue is -0𝔽, set nextValue to +0𝔽.
                //      iii. If SetDataHas(resultSetData, nextValue) is false, then
                //          1. Append nextValue to resultSetData.
                resultSetData = AddToSetTable(isolate, resultSetData, nextValue, methodName);
            }
        }

        // 8. Let result be
        // OrdinaryObjectCreate(%Set.prototype%, « [[SetData]]»).
        // 9. Set result.[[SetData]] to resultSetData.
        // 10. Return result.
        return NewJSSetWithTable(isolate, resultSetData);
    }

    // ---- Set.prototype.intersection (set-intersection.tq) ------------------------------------------

    /// <summary>Set.prototype.intersection ( other ).</summary>
    public static JSValue SetPrototypeIntersection(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.intersection";
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        // 4. Let resultSetData be a new empty List.
        OrderedHashSet resultSetData = isolate.Factory.NewOrderedHashSet();

        // 5. Let thisSize be the number of elements in O.[[SetData]].
        OrderedHashSet table = o.Table;
        int thisSize = table.NumberOfElements;

        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            // FastIntersect: iterate the smaller collection, look up in the other.
            int otherSize = otherTable.NumberOfElements;
            OrderedHashTable toIterate = thisSize <= otherSize ? table : otherTable;
            OrderedHashTable toLookup = thisSize <= otherSize ? otherTable : table;
            int usedCapacity = toIterate.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (toIterate.IsDeletedEntry(i)) continue;
                JSValue nextValue = toIterate.KeyAtRaw(i);
                if (TableHasKey(toLookup, nextValue))
                {
                    resultSetData = AddToSetTable(isolate, resultSetData, nextValue, methodName);
                }
            }
            return NewJSSetWithTable(isolate, resultSetData);
        }

        // 6. If thisSize ≤ otherRec.[[Size]], then
        if (thisSize <= otherRec.Size)
        {
            // a. Let index be 0.
            OrderedHashTable thisTable = table;
            int index = 0;

            // b. Repeat, while index < thisSize,
            //   i. Let e be O.[[SetData]][index].
            while (NextOrderedHashSetIterator(ref thisTable, ref index, out JSValue key))
            {
                // ii. Set index to index + 1.
                // iii. If e is not empty, then
                //   1. Let inOther be ToBoolean(? Call(otherRec.[[Has]],
                // otherRec.[[Set]], « e »)).
                bool inOther = ObjectOps.BooleanValue(Execution.Call(isolate, otherRec.Has, otherRec.Object, [key]));

                //   2. If inOther is true, then
                if (inOther)
                {
                    //  a. NOTE: It is possible for earlier calls to otherRec.[[Has]] to
                    // remove and re-add an element of O.[[SetData]], which can cause the
                    // same element to be visited twice during this iteration.
                    //  b. Let alreadyInResult be SetDataHas(resultSetData, e).
                    //  c. If alreadyInResult is false, then
                    //    i. Append e to resultSetData.
                    resultSetData = AddToSetTable(isolate, resultSetData, key, methodName);
                }
                // 3. NOTE: The number of elements in O.[[SetData]] may have increased
                // during execution of otherRec.[[Has]].
                // 4. Set thisSize to the number of elements of O.[[SetData]].
                // We used iterator so we do not need to update thisSize and index.
            }
        }
        else
        {
            // a. Let keysIter be ? GetKeysIterator(otherRec).
            IteratorRecord keysIter = GetKeysIterator(isolate, otherRec.Object, otherRec.Keys);

            // b. Let next be true.
            // c. Repeat, while next is not false,
            //   i. Set next to ? IteratorStep(keysIter).
            while (IteratorBuiltins.IteratorStep(isolate, keysIter, out JSReceiver nextRecord, fastIteratorResultMap))
            {
                // ii. If next is not false, then
                // 1. Let nextValue be ? IteratorValue(next).
                JSValue nextValue = IteratorBuiltins.IteratorValue(isolate, nextRecord, fastIteratorResultMap);

                // 2. If nextValue is -0𝔽, set nextValue to +0𝔽.
                // 3. NOTE: Because other is an arbitrary object, it is possible for its
                // "keys" iterator to produce the same value more than once.
                // 4. Let alreadyInResult be SetDataHas(resultSetData, nextValue).
                // 5. Let inThis be SetDataHas(O.[[SetData]], nextValue).
                if (TableHasKey(o.Table, nextValue))
                {
                    // 6. If alreadyInResult is false and inThis is true, then
                    // a. Append nextValue to resultSetData.
                    resultSetData = AddToSetTable(isolate, resultSetData, nextValue, methodName);
                }
            }
        }
        return NewJSSetWithTable(isolate, resultSetData);
    }

    // ---- Set.prototype.difference (set-difference.tq) ----------------------------------------------

    /// <summary>Set.prototype.difference ( other ).</summary>
    public static JSValue SetPrototypeDifference(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.difference";
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        OrderedHashSet table = o.Table;

        // 4. Let resultSetData be a copy of O.[[SetData]].
        OrderedHashSet resultSetData = table.Clone();

        // 5. Let thisSize be the number of elements in O.[[SetData]].
        int thisSize = table.NumberOfElements;

        int numberOfElements = thisSize;

        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            // FastDifference: iterate the smaller collection; delete from the result
            // what the other contains.
            int otherSize = otherTable.NumberOfElements;
            OrderedHashTable toIterate = thisSize <= otherSize ? table : otherTable;
            OrderedHashTable toLookup = thisSize <= otherSize ? otherTable : resultSetData;
            int usedCapacity = toIterate.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (toIterate.IsDeletedEntry(i)) continue;
                JSValue nextValue = toIterate.KeyAtRaw(i);
                if (TableHasKey(toLookup, nextValue))
                {
                    int n = DeleteFromSetTable(resultSetData, nextValue);
                    if (n >= 0) numberOfElements = n;
                }
            }
        }
        else if (thisSize <= otherRec.Size)
        {
            // 6. If thisSize ≤ otherRec.[[Size]], then
            // a. Let index be 0.
            OrderedHashTable thisTable = resultSetData;
            int index = 0;

            // b. Repeat, while index < thisSize,
            //   i. Let e be O.[[resultSetData]][index].
            while (NextOrderedHashSetIterator(ref thisTable, ref index, out JSValue key))
            {
                // ii. Set index to index + 1.
                // iii. If e is not empty, then
                //   1. Let inOther be ToBoolean(? Call(otherRec.[[Has]],
                // otherRec.[[Set]], « e »)).
                bool inOther = ObjectOps.BooleanValue(Execution.Call(isolate, otherRec.Has, otherRec.Object, [key]));

                //   2. If inOther is true, then
                //     a. Set resultSetData[index] to empty.
                if (inOther)
                {
                    int n = DeleteFromSetTable(resultSetData, key);
                    if (n >= 0) numberOfElements = n;
                }
            }
        }
        else
        {
            // a. Let keysIter be ? GetKeysIterator(otherRec).
            IteratorRecord keysIter = GetKeysIterator(isolate, otherRec.Object, otherRec.Keys);

            // b. Let next be true.
            // c. Repeat, while next is not false,
            //   i. Set next to ? IteratorStep(keysIter).
            while (IteratorBuiltins.IteratorStep(isolate, keysIter, out JSReceiver nextRecord, fastIteratorResultMap))
            {
                // ii. If next is not false, then
                //   1. Let nextValue be ? IteratorValue(next).
                //   2. If nextValue is -0𝔽, set nextValue to +0𝔽.
                JSValue nextValue = NormalizeNumberKey(IteratorBuiltins.IteratorValue(isolate, nextRecord, fastIteratorResultMap));

                //   3. If SetDataHas(resultSetData, nextValue) is true, then
                //     a. Remove nextValue from resultSetData.
                int n = DeleteFromSetTable(resultSetData, nextValue);
                if (n >= 0) numberOfElements = n;
            }
        }

        resultSetData = ShrinkOrderedHashSetIfNeeded(isolate, numberOfElements, resultSetData);
        return NewJSSetWithTable(isolate, resultSetData);
    }

    // ---- Set.prototype.symmetricDifference (set-symmetric-difference.tq) ---------------------------

    /// <summary>Set.prototype.symmetricDifference ( other ).</summary>
    public static JSValue SetPrototypeSymmetricDifference(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.symmetricDifference";
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        // 4. Let keysIter be ? GetKeysIterator(otherRec).
        IteratorRecord keysIter = GetKeysIterator(isolate, otherRec.Object, otherRec.Keys);

        // 5. Let resultSetData be a copy of O.[[SetData]].
        OrderedHashSet resultSetData = o.Table.Clone();
        int numberOfElements = resultSetData.NumberOfElements;

        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            int usedCapacity = otherTable.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (otherTable.IsDeletedEntry(i)) continue;
                // FastSymmetricDifference.
                JSValue key = NormalizeNumberKey(otherTable.KeyAtRaw(i));
                if (TableHasKey(resultSetData, key))
                {
                    numberOfElements = DeleteFromSetTable(resultSetData, key);
                }
                else
                {
                    resultSetData = AddToSetTable(isolate, resultSetData, key, methodName);
                    numberOfElements++;
                }
            }
        }
        else
        {
            // 6. Let next be true.
            // 7. Repeat, while next is not false,
            //  a. Set next to ? IteratorStep(keysIter).
            while (IteratorBuiltins.IteratorStep(isolate, keysIter, out JSReceiver nextRecord, fastIteratorResultMap))
            {
                //  b. If next is not false, then
                //      i. Let nextValue be ? IteratorValue(next).
                //      ii. If nextValue is -0𝔽, set nextValue to +0𝔽.
                JSValue nextValue = NormalizeNumberKey(IteratorBuiltins.IteratorValue(isolate, nextRecord, fastIteratorResultMap));

                //      iii. Let inResult be SetDataHas(resultSetData, nextValue).
                bool inResult = TableHasKey(resultSetData, nextValue);

                //      iv. If SetDataHas(O.[[SetData]], nextValue) is true, then
                if (TableHasKey(o.Table, nextValue))
                {
                    //  1. If inResult is true, remove nextValue from resultSetData.
                    if (inResult) numberOfElements = DeleteFromSetTable(resultSetData, nextValue);
                }
                else
                {
                    // v. Else,
                    //    1. If inResult is false, append nextValue to resultSetData.
                    if (!inResult)
                    {
                        resultSetData = AddToSetTable(isolate, resultSetData, nextValue, methodName);
                        numberOfElements++;
                    }
                }
            }
        }

        resultSetData = ShrinkOrderedHashSetIfNeeded(isolate, numberOfElements, resultSetData);
        return NewJSSetWithTable(isolate, resultSetData);
    }

    // ---- Set.prototype.isSubsetOf (set-is-subset-of.tq) --------------------------------------------

    /// <summary>Set.prototype.isSubsetOf ( other ).</summary>
    public static JSValue SetPrototypeIsSubsetOf(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.isSubsetOf";
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        OrderedHashSet table = o.Table;

        // 4. Let thisSize be the number of elements in O.[[SetData]].
        int thisSize = table.NumberOfElements;

        // 5. If thisSize > otherRec.[[Size]], return false.
        if (thisSize > otherRec.Size) return JSValue.False;

        // 6. Let index be 0.
        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            int usedCapacity = table.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (table.IsDeletedEntry(i)) continue;
                if (!TableHasKey(otherTable, table.KeyAtRaw(i))) return JSValue.False;
            }
            return JSValue.True;
        }

        // 7. Repeat, while index < thisSize,
        OrderedHashTable thisTable = table;
        int index = 0;
        //   a. Let e be O.[[SetData]][index].
        while (NextOrderedHashSetIterator(ref thisTable, ref index, out JSValue key))
        {
            // b. Set index to index + 1.
            // c. Let inOther be ToBoolean(? Call(otherRec.[[Has]], otherRec.[[Set]],
            // « e »)).
            bool inOther = ObjectOps.BooleanValue(Execution.Call(isolate, otherRec.Has, otherRec.Object, [key]));

            // d. If inOther is false, return false.
            if (!inOther) return JSValue.False;
            // e. NOTE: The number of elements in O.[[SetData]] may have increased
            // during execution of otherRec.[[Has]].
            // f. Set thisSize to the number of elements of O.[[SetData]].
        }

        // 8. Return true.
        return JSValue.True;
    }

    // ---- Set.prototype.isSupersetOf (set-is-superset-of.tq) ----------------------------------------

    /// <summary>Set.prototype.isSupersetOf ( other ).</summary>
    public static JSValue SetPrototypeIsSupersetOf(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.isSupersetOf";
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        // 4. Let thisSize be the number of elements in O.[[SetData]].
        int thisSize = o.Table.NumberOfElements;

        // 5. If thisSize < otherRec.[[Size]], return false.
        if (thisSize < otherRec.Size) return JSValue.False;

        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            OrderedHashSet table = o.Table;
            int usedCapacity = otherTable.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (otherTable.IsDeletedEntry(i)) continue;
                if (!TableHasKey(table, otherTable.KeyAtRaw(i))) return JSValue.False;
            }
            return JSValue.True;
        }

        // 6. Let keysIter be ? GetKeysIterator(otherRec).
        IteratorRecord keysIter = GetKeysIterator(isolate, otherRec.Object, otherRec.Keys);

        // 7. Let next be true.
        // 8. Repeat, while next is not false,
        //   a. Set next to ? IteratorStep(keysIter).
        while (IteratorBuiltins.IteratorStep(isolate, keysIter, out JSReceiver nextRecord, fastIteratorResultMap))
        {
            //   b. If next is not false, then
            //      i. Let nextValue be ? IteratorValue(next).
            JSValue nextValue = IteratorBuiltins.IteratorValue(isolate, nextRecord, fastIteratorResultMap);
            //      ii. If SetDataHas(O.[[SetData]], nextValue) is false, then
            if (!TableHasKey(o.Table, nextValue))
            {
                //          1. Perform ? IteratorClose(keysIter,
                //          NormalCompletion(unused)).
                //          2. Return false.
                IteratorBuiltins.IteratorClose(isolate, keysIter);
                return JSValue.False;
            }
        }

        // 9. Return true.
        return JSValue.True;
    }

    // ---- Set.prototype.isDisjointFrom (set-is-disjoint-from.tq) ------------------------------------

    /// <summary>Set.prototype.isDisjointFrom ( other ).</summary>
    public static JSValue SetPrototypeIsDisjointFrom(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Set.prototype.isDisjointFrom";
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue other = args.AtOrUndefined(1);

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[SetData]]).
        JSSet o = CheckSet(isolate, args.Receiver, methodName);

        // 3. Let otherRec be ? GetSetRecord(other).
        SetRecord otherRec = GetSetRecord(isolate, other, methodName);

        OrderedHashSet table = o.Table;

        // 4. Let thisSize be the number of elements in O.[[SetData]].
        int thisSize = table.NumberOfElements;

        OrderedHashTable? otherTable = GetFastOtherTable(isolate, other, otherRec);
        if (otherTable is not null)
        {
            // FastIsDisjointFrom: iterate the smaller collection.
            int otherSize = otherTable.NumberOfElements;
            OrderedHashTable toIterate = thisSize <= otherSize ? table : otherTable;
            OrderedHashTable toLookup = thisSize <= otherSize ? otherTable : table;
            int usedCapacity = toIterate.UsedCapacity;
            for (int i = 0; i < usedCapacity; i++)
            {
                if (toIterate.IsDeletedEntry(i)) continue;
                if (TableHasKey(toLookup, toIterate.KeyAtRaw(i))) return JSValue.False;
            }
            return JSValue.True;
        }

        // 5. If thisSize ≤ otherRec.[[Size]], then
        if (thisSize <= otherRec.Size)
        {
            // a. Let index be 0.
            OrderedHashTable thisTable = table;
            int index = 0;

            // b. Repeat, while index < thisSize,
            //   i. Let e be O.[[SetData]][index].
            while (NextOrderedHashSetIterator(ref thisTable, ref index, out JSValue key))
            {
                // ii. Set index to index + 1.
                // iii. If e is not empty, then
                //   1. Let inOther be ToBoolean(? Call(otherRec.[[Has]],
                // otherRec.[[Set]], « e »)).
                bool inOther = ObjectOps.BooleanValue(Execution.Call(isolate, otherRec.Has, otherRec.Object, [key]));

                //   2. If inOther is true, return false
                if (inOther) return JSValue.False;
            }
        }
        else
        {
            // a. Let keysIter be ? GetKeysIterator(otherRec).
            IteratorRecord keysIter = GetKeysIterator(isolate, otherRec.Object, otherRec.Keys);

            // b. Let next be true.
            // c. Repeat, while next is not false,
            //   i. Set next to ? IteratorStep(keysIter).
            while (IteratorBuiltins.IteratorStep(isolate, keysIter, out JSReceiver nextRecord, fastIteratorResultMap))
            {
                // ii. If next is not false, then
                // 1. Let nextValue be ? IteratorValue(next).
                JSValue nextValue = IteratorBuiltins.IteratorValue(isolate, nextRecord, fastIteratorResultMap);

                // 2. If SetDataHas(O.[[SetData]], nextValue) is true, then
                if (TableHasKey(o.Table, nextValue))
                {
                    //   a. Perform ? IteratorClose(keysIter, NormalCompletion(unused)).
                    //   b. Return false.
                    IteratorBuiltins.IteratorClose(isolate, keysIter);
                    return JSValue.False;
                }
            }
        }

        // 7. Return true.
        return JSValue.True;
    }
}
