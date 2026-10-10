// Port of third_party/v8/builtins/array-sort.tq (PowerSort, CPython's
// listobject.c algorithm) and src/builtins/array-to-sorted.tq. The order of
// the comparison calls is observable through a user comparefn, so the
// algorithm (binary insertion sort under kMaxInlineSortLength, run detection,
// minrun, node powers, galloping merges) is ported step by step.
using System.Numerics;
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArraySort()
    {
        Register(Builtin.ArrayPrototypeSort, BuiltinsArray.ArrayPrototypeSort);
        Register(Builtin.ArrayPrototypeToSorted, BuiltinsArray.ArrayPrototypeToSorted);
    }
}

public static partial class BuiltinsArray
{
    /// <summary>src/objects/sort-state.h SortState.</summary>
    sealed class SortState
    {
        public required JSReceiver Receiver;
        public required Map InitialReceiverMap;
        public required double InitialReceiverLength;
        public required JSValue UserCmpFn;
        public bool IsResetToGeneric;
        public int MinGallop = kMinGallopWins;
        public int PendingRunsSize;
        public int[] PendingRuns = [];
        public int[] PendingPowers = [];
        public required JSValue[] WorkArray;
        public JSValue[] TempArray = [];
        public int SortLength;
        public int NumberOfUndefined;
    }

    /// <summary>The maximum number of entries in a SortState's pending-runs stack.</summary>
    const int kMaxMergePending = 85;

    /// <summary>When we get into galloping mode, we stay there until both runs win less often than kMinGallop consecutive times.</summary>
    const int kMinGallopWins = 7;

    /// <summary>kMaxBinaryInsertionSortLength (JSArray::kMaxInlineSortLength).</summary>
    const int kMaxBinaryInsertionSortLength = JSArray.kMaxInlineSortLength;

    /// <summary>Default size of the temporary array.</summary>
    const int kSortStateTempSize = 32;

    const int kSmiMaxValue = JSValue.SmiMaxValue;

    // ---- Comparison ------------------------------------------------------------------------------

    static double Compare(Isolate isolate, SortState sortState, JSValue x, JSValue y) =>
        sortState.UserCmpFn.IsUndefined
            ? SortCompareDefault(isolate, x, y)
            : SortCompareUserFn(isolate, sortState.UserCmpFn, x, y);

    /// <summary>SortCompareDefault.</summary>
    static double SortCompareDefault(Isolate isolate, JSValue x, JSValue y)
    {
        if (x.IsSmi && y.IsSmi) return SmiLexicographicCompare((int)x.Number, (int)y.Number);
        // 5. Let xString be ? ToString(x).
        JSString xString = ObjectOps.ToString(isolate, x);
        // 6. Let yString be ? ToString(y).
        JSString yString = ObjectOps.ToString(isolate, y);
        // 7-11.
        return (int)JSString.Compare(xString, yString);
    }

    /// <summary>Smi::LexicographicCompare: compares the decimal representations.</summary>
    internal static int SmiLexicographicCompare(int xValue, int yValue)
    {
        if (xValue == yValue) return 0;
        // If one of the integers is zero the normal integer order is the
        // same as the lexicographic order of the string representations.
        if (xValue == 0 || yValue == 0) return xValue < yValue ? -1 : 1;
        // If only one of the integers is negative the negative number is
        // smallest because the char code of '-' is less than the char code
        // of any digit. Otherwise, we make both values positive.
        ulong xScaled = (uint)xValue;
        ulong yScaled = (uint)yValue;
        if (xValue < 0 || yValue < 0)
        {
            if (yValue >= 0) return -1;
            if (xValue >= 0) return 1;
            xScaled = (uint)-xValue;
            yScaled = (uint)-yValue;
        }
        int xLog10 = DecimalDigits(xScaled);
        int yLog10 = DecimalDigits(yScaled);
        int tie = 0;
        // Scale the value with fewer digits up so both have the same count; a tie
        // then goes to the shorter string.
        if (xLog10 < yLog10)
        {
            xScaled *= Pow10(yLog10 - xLog10);
            tie = -1;
        }
        else if (yLog10 < xLog10)
        {
            yScaled *= Pow10(xLog10 - yLog10);
            tie = 1;
        }
        if (xScaled < yScaled) return -1;
        if (xScaled > yScaled) return 1;
        return tie;
    }

    static int DecimalDigits(ulong v)
    {
        int n = 1;
        while (v >= 10) { v /= 10; n++; }
        return n;
    }

    static ulong Pow10(int e)
    {
        ulong r = 1;
        for (int i = 0; i < e; i++) r *= 10;
        return r;
    }

    /// <summary>SortCompareUserFn.</summary>
    static double SortCompareUserFn(Isolate isolate, JSValue comparefn, JSValue x, JSValue y)
    {
        // a. Let v be ? ToNumber(? Call(comparefn, undefined, x, y)).
        double v = ObjectOps.ToNumber(isolate, Execution.Call(isolate, comparefn, JSValue.Undefined, [x, y])).Number;
        // b. If v is NaN, return +0.
        if (double.IsNaN(v)) return 0;
        // c. return v.
        return v;
    }

    // ---- SortState --------------------------------------------------------------------------------

    /// <summary>CalculateWorkArrayLength.</summary>
    static int CalculateWorkArrayLength(JSReceiver receiver, double initialReceiverLength)
    {
        int workArrayLength = initialReceiverLength > kSmiMaxValue ? kSmiMaxValue : (int)initialReceiverLength;
        if (receiver is JSObject obj)
        {
            int elementsLength = obj.Elements.Length;
            // In some cases, elements are only on prototypes, but not on the receiver
            // itself. Do nothing then, as {workArrayLength} got initialized with the
            // {length} property.
            if (elementsLength != 0) workArrayLength = Math.Min(workArrayLength, elementsLength);
        }
        return workArrayLength;
    }

    /// <summary>NewSortState.</summary>
    static SortState NewSortState(Isolate isolate, JSReceiver receiver, JSValue comparefn, double initialReceiverLength,
        bool isToSorted)
    {
        Map map = receiver.Map;
        if (!isToSorted && IsFastJSArray(isolate, receiver, out JSArray a))
        {
            // Copy copy-on-write (COW) arrays if we're doing Array.prototype.sort.
            JSObject.EnsureWritableFastElements(isolate, a);
        }
        int workArrayLength = CalculateWorkArrayLength(receiver, initialReceiverLength);
        return new SortState
        {
            Receiver = receiver,
            InitialReceiverMap = map,
            InitialReceiverLength = initialReceiverLength,
            UserCmpFn = comparefn,
            WorkArray = new JSValue[workArrayLength],
        };
    }

    // ---- Accessors ---------------------------------------------------------------------------------

    /// <summary>CheckAccessor: the fast accessors can still be used.</summary>
    static bool CheckAccessor(Isolate isolate, SortState sortState)
    {
        if (!IsFastJSArray(isolate, sortState.Receiver, out JSArray array)) return false;
        // CanUseSameAccessor<Fast*Elements>.
        if (!ReferenceEquals(array.Map, sortState.InitialReceiverMap)) return false;
        return array.Length.Number == sortState.InitialReceiverLength;
    }

    // ---- Run stack -----------------------------------------------------------------------------------

    /// <summary>NodePower (64-bit path): the depth of a boundary in a merge tree.</summary>
    static int NodePower(int s1, int n1, int n2, int n)
    {
        long a = 2L * s1 + n1;
        long b = a + n1 + n2;
        // Compute (a << 30) / n and (b << 30) / n.
        long aScaled = (a << 30) / n;
        long bScaled = (b << 30) / n;
        uint a32 = (uint)(int)aScaled;
        uint b32 = (uint)(int)bScaled;
        uint diff32 = a32 ^ b32;
        return BitOperations.LeadingZeroCount(diff32);
    }

    static void PushRun(SortState sortState, int runBase, int length)
    {
        int stackSize = sortState.PendingRunsSize;
        sortState.PendingRuns[stackSize << 1] = runBase;
        sortState.PendingRuns[(stackSize << 1) + 1] = length;
        sortState.PendingRunsSize = stackSize + 1;
    }

    /// <summary>GetTempArray: the temporary array, at least <paramref name="requestedSize"/> long.</summary>
    static JSValue[] GetTempArray(SortState sortState, int requestedSize)
    {
        int minSize = Math.Max(kSortStateTempSize, requestedSize);
        if (sortState.TempArray.Length >= minSize) return sortState.TempArray;
        var tempArray = new JSValue[minSize];
        sortState.TempArray = tempArray;
        return tempArray;
    }

    /// <summary>Copy: overlapping-safe element copy.</summary>
    static void Copy(JSValue[] source, int srcPos, JSValue[] target, int dstPos, int length) =>
        Array.Copy(source, srcPos, target, dstPos, length);

    // ---- Sorting ---------------------------------------------------------------------------------------

    /// <summary>
    /// BinaryInsertionSort: [low, high) is sorted by binary insertion; on entry
    /// [low, start) is already sorted. Stable.
    /// </summary>
    static void BinaryInsertionSort(Isolate isolate, SortState sortState, int low, int startArg, int high)
    {
        JSValue[] workArray = sortState.WorkArray;
        int start = low == startArg ? startArg + 1 : startArg;
        for (; start < high; ++start)
        {
            // Set left to where a[start] belongs.
            int left = low;
            int right = start;
            JSValue pivot = workArray[right];
            // Find pivot insertion point.
            while (left < right)
            {
                int mid = left + ((right - left) >> 1);
                double order = Compare(isolate, sortState, pivot, workArray[mid]);
                if (order < 0) right = mid;
                else left = mid + 1;
            }
            // pivot belongs at left. Slide over to make room.
            for (int p = start; p > left; --p) workArray[p] = workArray[p - 1];
            workArray[left] = pivot;
        }
    }

    /// <summary>
    /// CountAndMakeRun: the length of the run beginning at lowArg in [lowArg, high);
    /// a strictly descending run is reversed in place.
    /// </summary>
    static int CountAndMakeRun(Isolate isolate, SortState sortState, int lowArg, int high)
    {
        JSValue[] workArray = sortState.WorkArray;
        int low = lowArg + 1;
        if (low == high) return 1;

        int runLength = 2;
        JSValue elementLow = workArray[low];
        JSValue elementLowPred = workArray[low - 1];
        double order = Compare(isolate, sortState, elementLow, elementLowPred);
        bool isDescending = order < 0;

        JSValue previousElement = elementLow;
        for (int idx = low + 1; idx < high; ++idx)
        {
            JSValue currentElement = workArray[idx];
            order = Compare(isolate, sortState, currentElement, previousElement);
            if (isDescending)
            {
                if (order >= 0) break;
            }
            else
            {
                if (order < 0) break;
            }
            previousElement = currentElement;
            ++runLength;
        }

        if (isDescending) workArray.AsSpan(lowArg, runLength).Reverse();
        return runLength;
    }

    /// <summary>MergeAt: merges the two runs at stack indices i and i + 1.</summary>
    static void MergeAt(Isolate isolate, SortState sortState, int i)
    {
        int stackSize = sortState.PendingRunsSize;
        JSValue[] workArray = sortState.WorkArray;
        int[] pendingRuns = sortState.PendingRuns;
        int baseA = pendingRuns[i << 1];
        int lengthA = pendingRuns[(i << 1) + 1];
        int baseB = pendingRuns[(i + 1) << 1];
        int lengthB = pendingRuns[((i + 1) << 1) + 1];

        // Record the length of the combined runs; if i is the 3rd-last run now,
        // also slide over the last run (which isn't involved in this merge).
        // The current run i + 1 goes away in any case.
        pendingRuns[(i << 1) + 1] = lengthA + lengthB;
        if (i == stackSize - 3)
        {
            pendingRuns[(i + 1) << 1] = pendingRuns[(i + 2) << 1];
            pendingRuns[((i + 1) << 1) + 1] = pendingRuns[((i + 2) << 1) + 1];
        }
        sortState.PendingRunsSize = stackSize - 1;

        // Where does b start in a? Elements in a before that can be ignored,
        // because they are already in place.
        JSValue keyRight = workArray[baseB];
        int k = GallopRight(isolate, sortState, workArray, keyRight, baseA, lengthA, 0);
        baseA += k;
        lengthA -= k;
        if (lengthA == 0) return;

        // Where does a end in b? Elements in b after that can be ignored,
        // because they are already in place.
        JSValue keyLeft = workArray[baseA + lengthA - 1];
        lengthB = GallopLeft(isolate, sortState, workArray, keyLeft, baseB, lengthB, lengthB - 1);
        if (lengthB == 0) return;

        // Merge what remains of the runs, using a temp array with
        // min(lengthA, lengthB) elements.
        if (lengthA <= lengthB) MergeLow(isolate, sortState, baseA, lengthA, baseB, lengthB);
        else MergeHigh(isolate, sortState, baseA, lengthA, baseB, lengthB);
    }

    /// <summary>
    /// GallopLeft: the offset in 0..length such that
    /// array[base + offset - 1] &lt; key &lt;= array[base + offset].
    /// </summary>
    static int GallopLeft(Isolate isolate, SortState sortState, JSValue[] array, JSValue key, int arrayBase, int length, int hint)
    {
        int lastOfs = 0;
        int offset = 1;

        JSValue baseHintElement = array[arrayBase + hint];
        double order = Compare(isolate, sortState, baseHintElement, key);

        if (order < 0)
        {
            // a[base + hint] < key: gallop right, until
            // a[base + hint + lastOfs] < key <= a[base + hint + offset].
            int maxOfs = length - hint;
            while (offset < maxOfs)
            {
                JSValue offsetElement = array[arrayBase + hint + offset];
                order = Compare(isolate, sortState, offsetElement, key);
                // a[base + hint + offset] >= key? Break.
                if (order >= 0) break;
                lastOfs = offset;
                offset = (offset << 1) + 1;
                // Integer overflow.
                if (offset <= 0) offset = maxOfs;
            }
            if (offset > maxOfs) offset = maxOfs;
            // Translate back to positive offsets relative to base.
            lastOfs += hint;
            offset += hint;
        }
        else
        {
            // key <= a[base + hint]: gallop left, until
            // a[base + hint - offset] < key <= a[base + hint - lastOfs].
            int maxOfs = hint + 1;
            while (offset < maxOfs)
            {
                JSValue offsetElement = array[arrayBase + hint - offset];
                order = Compare(isolate, sortState, offsetElement, key);
                if (order < 0) break;
                lastOfs = offset;
                offset = (offset << 1) + 1;
                // Integer overflow.
                if (offset <= 0) offset = maxOfs;
            }
            if (offset > maxOfs) offset = maxOfs;
            // Translate back to positive offsets relative to base.
            int tmp = lastOfs;
            lastOfs = hint - offset;
            offset = hint - tmp;
        }

        // Now a[base+lastOfs] < key <= a[base+offset], so key belongs
        // somewhere to the right of lastOfs but no farther right than offset.
        lastOfs++;
        while (lastOfs < offset)
        {
            int m = lastOfs + ((offset - lastOfs) >> 1);
            order = Compare(isolate, sortState, array[arrayBase + m], key);
            if (order < 0) lastOfs = m + 1; // a[base + m] < key.
            else offset = m; // key <= a[base + m].
        }
        return offset;
    }

    /// <summary>
    /// GallopRight: like GallopLeft, but returns the position to the right of
    /// the rightmost element equal to key.
    /// </summary>
    static int GallopRight(Isolate isolate, SortState sortState, JSValue[] array, JSValue key, int arrayBase, int length, int hint)
    {
        int lastOfs = 0;
        int offset = 1;

        JSValue baseHintElement = array[arrayBase + hint];
        double order = Compare(isolate, sortState, key, baseHintElement);

        if (order < 0)
        {
            // key < a[base + hint]: gallop left, until
            // a[base + hint - offset] <= key < a[base + hint - lastOfs].
            int maxOfs = hint + 1;
            while (offset < maxOfs)
            {
                JSValue offsetElement = array[arrayBase + hint - offset];
                order = Compare(isolate, sortState, key, offsetElement);
                if (order >= 0) break;
                lastOfs = offset;
                offset = (offset << 1) + 1;
                // Integer overflow.
                if (offset <= 0) offset = maxOfs;
            }
            if (offset > maxOfs) offset = maxOfs;
            // Translate back to positive offsets relative to base.
            int tmp = lastOfs;
            lastOfs = hint - offset;
            offset = hint - tmp;
        }
        else
        {
            // a[base + hint] <= key: gallop right, until
            // a[base + hint + lastOfs] <= key < a[base + hint + offset].
            int maxOfs = length - hint;
            while (offset < maxOfs)
            {
                JSValue offsetElement = array[arrayBase + hint + offset];
                order = Compare(isolate, sortState, key, offsetElement);
                // a[base + hint + ofs] <= key.
                if (order < 0) break;
                lastOfs = offset;
                offset = (offset << 1) + 1;
                // Integer overflow.
                if (offset <= 0) offset = maxOfs;
            }
            if (offset > maxOfs) offset = maxOfs;
            // Translate back to positive offests relative to base.
            lastOfs += hint;
            offset += hint;
        }

        // Now a[base + lastOfs] <= key < a[base + ofs], so key belongs
        // somewhere to the right of lastOfs but no farther right than ofs.
        lastOfs++;
        while (lastOfs < offset)
        {
            int m = lastOfs + ((offset - lastOfs) >> 1);
            order = Compare(isolate, sortState, key, array[arrayBase + m]);
            if (order < 0) offset = m; // key < a[base + m].
            else lastOfs = m + 1; // a[base + m] <= key.
        }
        return offset;
    }

    /// <summary>
    /// MergeLow: merges the lengthA elements at baseA with the lengthB elements
    /// at baseB (lengthA &lt;= lengthB) in a stable way, in place.
    /// </summary>
    static void MergeLow(Isolate isolate, SortState sortState, int baseA, int lengthAArg, int baseB, int lengthBArg)
    {
        int lengthA = lengthAArg;
        int lengthB = lengthBArg;

        JSValue[] workArray = sortState.WorkArray;
        JSValue[] tempArray = GetTempArray(sortState, lengthA);
        Copy(workArray, baseA, tempArray, 0, lengthA);

        int dest = baseA;
        int cursorTemp = 0;
        int cursorB = baseB;

        workArray[dest++] = workArray[cursorB++];

        if (--lengthB == 0) goto Succeed;
        if (lengthA == 1) goto CopyB;

        int minGallop = sortState.MinGallop;
        while (true)
        {
            int nofWinsA = 0; // # of times A won in a row.
            int nofWinsB = 0; // # of times B won in a row.

            // Do the straightforward thing until (if ever) one run appears to
            // win consistently.
            while (true)
            {
                double order = Compare(isolate, sortState, workArray[cursorB], tempArray[cursorTemp]);
                if (order < 0)
                {
                    workArray[dest++] = workArray[cursorB++];
                    ++nofWinsB;
                    --lengthB;
                    nofWinsA = 0;
                    if (lengthB == 0) goto Succeed;
                    if (nofWinsB >= minGallop) break;
                }
                else
                {
                    workArray[dest++] = tempArray[cursorTemp++];
                    ++nofWinsA;
                    --lengthA;
                    nofWinsB = 0;
                    if (lengthA == 1) goto CopyB;
                    if (nofWinsA >= minGallop) break;
                }
            }

            // One run is winning so consistently that galloping may be a huge
            // win. So try that, and continue galloping until (if ever) neither
            // run appears to be winning consistently anymore.
            ++minGallop;
            bool firstIteration = true;
            while (nofWinsA >= kMinGallopWins || nofWinsB >= kMinGallopWins || firstIteration)
            {
                firstIteration = false;
                minGallop = Math.Max(1, minGallop - 1);
                sortState.MinGallop = minGallop;

                nofWinsA = GallopRight(isolate, sortState, tempArray, workArray[cursorB], cursorTemp, lengthA, 0);
                if (nofWinsA > 0)
                {
                    Copy(tempArray, cursorTemp, workArray, dest, nofWinsA);
                    dest += nofWinsA;
                    cursorTemp += nofWinsA;
                    lengthA -= nofWinsA;
                    if (lengthA == 1) goto CopyB;
                    // lengthA == 0 is impossible now if the comparison function is
                    // consistent, but we can't assume that it is.
                    if (lengthA == 0) goto Succeed;
                }
                workArray[dest++] = workArray[cursorB++];
                if (--lengthB == 0) goto Succeed;

                nofWinsB = GallopLeft(isolate, sortState, workArray, tempArray[cursorTemp], cursorB, lengthB, 0);
                if (nofWinsB > 0)
                {
                    Copy(workArray, cursorB, workArray, dest, nofWinsB);
                    dest += nofWinsB;
                    cursorB += nofWinsB;
                    lengthB -= nofWinsB;
                    if (lengthB == 0) goto Succeed;
                }
                workArray[dest++] = tempArray[cursorTemp++];
                if (--lengthA == 1) goto CopyB;
            }
            ++minGallop; // Penalize it for leaving galloping mode
            sortState.MinGallop = minGallop;
        }

    Succeed:
        if (lengthA > 0) Copy(tempArray, cursorTemp, workArray, dest, lengthA);
        return;

    CopyB:
        // The last element of run A belongs at the end of the merge.
        Copy(workArray, cursorB, workArray, dest, lengthB);
        workArray[dest + lengthB] = tempArray[cursorTemp];
    }

    /// <summary>
    /// MergeHigh: merges the lengthA elements at baseA with the lengthB elements
    /// at baseB (lengthA &gt;= lengthB) in a stable way, in place, backwards.
    /// </summary>
    static void MergeHigh(Isolate isolate, SortState sortState, int baseA, int lengthAArg, int baseB, int lengthBArg)
    {
        int lengthA = lengthAArg;
        int lengthB = lengthBArg;

        JSValue[] workArray = sortState.WorkArray;
        JSValue[] tempArray = GetTempArray(sortState, lengthB);
        Copy(workArray, baseB, tempArray, 0, lengthB);

        // MergeHigh merges the two runs backwards.
        int dest = baseB + lengthB - 1;
        int cursorTemp = lengthB - 1;
        int cursorA = baseA + lengthA - 1;

        workArray[dest--] = workArray[cursorA--];

        if (--lengthA == 0) goto Succeed;
        if (lengthB == 1) goto CopyA;

        int minGallop = sortState.MinGallop;
        while (true)
        {
            int nofWinsA = 0; // # of times A won in a row.
            int nofWinsB = 0; // # of times B won in a row.

            // Do the straightforward thing until (if ever) one run appears to
            // win consistently.
            while (true)
            {
                double order = Compare(isolate, sortState, tempArray[cursorTemp], workArray[cursorA]);
                if (order < 0)
                {
                    workArray[dest--] = workArray[cursorA--];
                    ++nofWinsA;
                    --lengthA;
                    nofWinsB = 0;
                    if (lengthA == 0) goto Succeed;
                    if (nofWinsA >= minGallop) break;
                }
                else
                {
                    workArray[dest--] = tempArray[cursorTemp--];
                    ++nofWinsB;
                    --lengthB;
                    nofWinsA = 0;
                    if (lengthB == 1) goto CopyA;
                    if (nofWinsB >= minGallop) break;
                }
            }

            // One run is winning so consistently that galloping may be a huge
            // win. So try that, and continue galloping until (if ever) neither
            // run appears to be winning consistently anymore.
            ++minGallop;
            bool firstIteration = true;
            while (nofWinsA >= kMinGallopWins || nofWinsB >= kMinGallopWins || firstIteration)
            {
                firstIteration = false;
                minGallop = Math.Max(1, minGallop - 1);
                sortState.MinGallop = minGallop;

                int k = GallopRight(isolate, sortState, workArray, tempArray[cursorTemp], baseA, lengthA, lengthA - 1);
                nofWinsA = lengthA - k;
                if (nofWinsA > 0)
                {
                    dest -= nofWinsA;
                    cursorA -= nofWinsA;
                    Copy(workArray, cursorA + 1, workArray, dest + 1, nofWinsA);
                    lengthA -= nofWinsA;
                    if (lengthA == 0) goto Succeed;
                }
                workArray[dest--] = tempArray[cursorTemp--];
                if (--lengthB == 1) goto CopyA;

                k = GallopLeft(isolate, sortState, tempArray, workArray[cursorA], 0, lengthB, lengthB - 1);
                nofWinsB = lengthB - k;
                if (nofWinsB > 0)
                {
                    dest -= nofWinsB;
                    cursorTemp -= nofWinsB;
                    Copy(tempArray, cursorTemp + 1, workArray, dest + 1, nofWinsB);
                    lengthB -= nofWinsB;
                    if (lengthB == 1) goto CopyA;
                    // lengthB == 0 is impossible now if the comparison function is
                    // consistent, but we can't assume that it is.
                    if (lengthB == 0) goto Succeed;
                }
                workArray[dest--] = workArray[cursorA--];
                if (--lengthA == 0) goto Succeed;
            }
            ++minGallop;
            sortState.MinGallop = minGallop;
        }

    Succeed:
        if (lengthB > 0) Copy(tempArray, 0, workArray, dest - (lengthB - 1), lengthB);
        return;

    CopyA:
        // The first element of run B belongs at the front of the merge.
        dest -= lengthA;
        cursorA -= lengthA;
        Copy(workArray, cursorA + 1, workArray, dest + 1, lengthA);
        workArray[dest] = tempArray[cursorTemp];
    }

    /// <summary>ArrayPowerSortImpl.</summary>
    static void ArrayPowerSortImpl(Isolate isolate, SortState sortState, int length)
    {
        if (length < 2) return;
        int remaining = length;

        sortState.PendingRuns = new int[kMaxMergePending * 2];
        sortState.PendingPowers = new int[kMaxMergePending];

        // Port of CPython's minrun_next(): produces balanced run lengths that sum
        // exactly to length, each equal to floor(length/2^e) or ceil(length/2^e).
        // Find e such that floor(length/2^e) is in [32, 64).
        int mrStep = length;
        int mrMask = 0;
        while (mrStep >= 64)
        {
            mrStep >>= 1;
            mrMask = (mrMask << 1) | 1;
        }
        int mrRemainder = length & mrMask;
        int mrThreshold = mrMask + 1;
        int mrAccum = 0;

        int low = 0;
        // March over the array once, left to right, finding natural runs,
        // and extending short natural runs to minrun elements.
        while (remaining != 0)
        {
            mrAccum += mrRemainder;
            int minRunLength = mrStep;
            if (mrAccum >= mrThreshold)
            {
                mrAccum -= mrThreshold;
                minRunLength++;
            }
            int currentRunLength = CountAndMakeRun(isolate, sortState, low, low + remaining);

            if (currentRunLength < minRunLength)
            {
                int forcedRunLength = Math.Min(minRunLength, remaining);
                BinaryInsertionSort(isolate, sortState, low, low + currentRunLength, low + forcedRunLength);
                currentRunLength = forcedRunLength;
            }

            int stackSize = sortState.PendingRunsSize;
            if (stackSize == 0)
            {
                PushRun(sortState, low, currentRunLength);
            }
            else
            {
                int[] pendingRuns = sortState.PendingRuns;
                int[] pendingPowers = sortState.PendingPowers;
                int prevRunBase = pendingRuns[(stackSize - 1) << 1];
                int prevRunLen = pendingRuns[((stackSize - 1) << 1) + 1];

                int p = NodePower(prevRunBase, prevRunLen, currentRunLength, length);

                while (stackSize > 1 && pendingPowers[stackSize - 2] > p)
                {
                    MergeAt(isolate, sortState, stackSize - 2);
                    stackSize = sortState.PendingRunsSize;
                }

                pendingPowers[stackSize - 1] = p;
                PushRun(sortState, low, currentRunLength);
            }

            low += currentRunLength;
            remaining -= currentRunLength;
        }

        // Merge remaining runs.
        while (sortState.PendingRunsSize > 1)
        {
            int n = sortState.PendingRunsSize - 2;
            int[] pendingRuns = sortState.PendingRuns;
            if (n > 0 && pendingRuns[((n - 1) << 1) + 1] < pendingRuns[((n + 1) << 1) + 1]) --n;
            MergeAt(isolate, sortState, n);
        }
    }

    /// <summary>CompactReceiverElementsIntoWorkArray.</summary>
    static int CompactReceiverElementsIntoWorkArray(Isolate isolate, SortState sortState, bool isToSorted)
    {
        var growableWorkArray = new GrowableFixedArray(sortState.WorkArray);

        double receiverLength = sortState.InitialReceiverLength;
        int sortLength = receiverLength <= kSmiMaxValue ? (int)receiverLength : kSmiMaxValue;

        // Move all non-undefined elements into {sortState.work_array}, holes
        // are ignored.
        int numberOfUndefined = 0;

        if (!sortState.IsResetToGeneric && IsFastJSArray(isolate, sortState.Receiver, out JSArray fastArray))
        {
            ElementsKind elementsKind = fastArray.Map.ElementsKind;
            if (!ElementsKinds.IsDoubleElementsKind(elementsKind))
            {
                // Fast path for Smi and Object elements.
                JSValue[] elements = ((FixedArray)fastArray.Elements).Data;
                for (int i = 0; i < receiverLength; ++i)
                {
                    JSValue element = elements[i];
                    if (element.IsTheHole)
                    {
                        // Array.prototype.toSorted performs a GetProperty for each
                        // element; a hole in a fast JSArray reads undefined.
                        if (isToSorted) numberOfUndefined++;
                    }
                    else if (element.IsUndefined)
                    {
                        numberOfUndefined++;
                    }
                    else
                    {
                        growableWorkArray.Push(element);
                    }
                }
            }
            else
            {
                // Fast path for Double elements.
                var elements = (FixedDoubleArray)fastArray.Elements;
                for (int i = 0; i < receiverLength; ++i)
                {
                    if (elements.IsTheHole(i))
                    {
                        if (isToSorted) numberOfUndefined++;
                    }
                    else
                    {
                        growableWorkArray.Push(JSValue.FromNumber(elements.GetScalar(i)));
                    }
                }
            }
        }
        else
        {
            JSReceiver receiver = sortState.Receiver;
            for (int i = 0; i < receiverLength; ++i)
            {
                JSValue element;
                if (isToSorted)
                {
                    // LoadNoHasPropertyCheck<GenericElementsAccessor>.
                    element = ArrayBuiltinsUtils.GetProperty(isolate, receiver, i);
                }
                else
                {
                    // Load<GenericElementsAccessor>.
                    if (!ArrayBuiltinsUtils.HasProperty(isolate, receiver, i)) continue;
                    element = ArrayBuiltinsUtils.GetProperty(isolate, receiver, i);
                }
                if (element.IsUndefined) numberOfUndefined++;
                else growableWorkArray.Push(element);
            }
        }

        sortState.WorkArray = growableWorkArray.RawArray;
        sortState.SortLength = sortLength;
        sortState.NumberOfUndefined = numberOfUndefined;
        return growableWorkArray.Length;
    }

    /// <summary>CopyWorkArrayToReceiver.</summary>
    static void CopyWorkArrayToReceiver(Isolate isolate, SortState sortState, int numberOfNonUndefined)
    {
        JSValue[] workArray = sortState.WorkArray;
        bool fast = !sortState.IsResetToGeneric && IsFastJSArray(isolate, sortState.Receiver, out _);
        JSReceiver receiver = sortState.Receiver;

        // Writing the elements back is a 3 step process:
        //   1. Copy the sorted elements from the workarray to the receiver.
        //   2. Add {nOfUndefined} undefineds to the receiver.
        //   3. Depending on the backing store either delete properties or
        //      set them to the TheHole up to {sortState.sort_length}.
        int index = 0;
        for (; index < numberOfNonUndefined; ++index) Store(isolate, fast, receiver, index, workArray[index]);

        int end = sortState.SortLength;
        if (index == end) return;

        int numberOfUndefinedEnd = sortState.NumberOfUndefined + numberOfNonUndefined;
        for (; index < numberOfUndefinedEnd; ++index) Store(isolate, fast, receiver, index, JSValue.Undefined);

        fast = !sortState.IsResetToGeneric && IsFastJSArray(isolate, sortState.Receiver, out _);
        for (; index < end; ++index)
        {
            if (fast)
            {
                // Delete<Fast*Elements>.
                var obj = (JSObject)receiver;
                if (obj.Elements is FixedDoubleArray d) d.SetTheHole(index);
                else ((FixedArray)obj.Elements).SetTheHole(index);
            }
            else
            {
                // Delete<GenericElementsAccessor>.
                JSReceiver.DeletePropertyOrElement(isolate, receiver, new PropertyKey(isolate, (double)index), LanguageMode.Strict);
            }
        }
    }

    /// <summary>Store&lt;Fast*Elements | GenericElementsAccessor&gt;.</summary>
    static void Store(Isolate isolate, bool fast, JSReceiver receiver, int index, JSValue value)
    {
        if (fast)
        {
            var obj = (JSObject)receiver;
            if (obj.Elements is FixedDoubleArray d)
            {
                // With V8's undefined-double support an undefined would be stored
                // as a special NaN; V8Sharp's double arrays hold numbers only.
                if (value.IsNumber) d.Set(index, value.Number);
            }
            else
            {
                ((FixedArray)obj.Elements).Data[index] = value;
            }
            return;
        }
        ArrayBuiltinsUtils.SetProperty(isolate, receiver, index, value);
    }

    /// <summary>ArrayPowerSortTail.</summary>
    static void ArrayPowerSortTail(Isolate isolate, SortState sortState, int numberOfNonUndefined)
    {
        if (numberOfNonUndefined < kMaxBinaryInsertionSortLength)
        {
            // Faster for small arrays.
            BinaryInsertionSort(isolate, sortState, 0, 0, numberOfNonUndefined);
        }
        else
        {
            ArrayPowerSortImpl(isolate, sortState, numberOfNonUndefined);
        }
        // The comparison function or toString might have changed the
        // receiver, if that is the case, we switch to the slow path.
        if (!CheckAccessor(isolate, sortState)) sortState.IsResetToGeneric = true;
        CopyWorkArrayToReceiver(isolate, sortState, numberOfNonUndefined);
    }

    /// <summary>ES #sec-array.prototype.sort.</summary>
    public static JSValue ArrayPrototypeSort(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If comparefn is not undefined and IsCallable(comparefn) is false,
        //    throw a TypeError exception.
        JSValue comparefn = args.AtOrUndefined(1);
        if (!comparefn.IsUndefined && !ObjectOps.IsCallable(comparefn))
        {
            return isolate.ThrowTypeError(MessageTemplate.BadSortComparisonFunction, comparefn);
        }
        // 2. Let obj be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);
        // 3. Let len be ? ToLength(? Get(obj, "length")).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);
        if (len < 2) return obj;

        SortState sortState = NewSortState(isolate, obj, comparefn, len, false);
        int numberOfNonUndefined = CompactReceiverElementsIntoWorkArray(isolate, sortState, false);
        ArrayPowerSortTail(isolate, sortState, numberOfNonUndefined);
        return obj;
    }

    /// <summary>ES #sec-array.prototype.tosorted.</summary>
    public static JSValue ArrayPrototypeToSorted(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If comparefn is not undefined and IsCallable(comparefn) is false, throw
        //    a TypeError exception.
        JSValue comparefn = args.AtOrUndefined(1);
        if (!comparefn.IsUndefined && !ObjectOps.IsCallable(comparefn))
        {
            return isolate.ThrowTypeError(MessageTemplate.BadSortComparisonFunction, comparefn);
        }
        // 2. Let O be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);
        // 3. Let len be ? LengthOfArrayLike(O).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);

        if (len == 0) return ArrayBuiltinsUtils.ArrayCreate(isolate, 0);
        if (len == 1)
        {
            JSArray copy = ArrayBuiltinsUtils.ArrayCreate(isolate, 1);
            ArrayBuiltinsUtils.SetProperty(isolate, copy, 0, ArrayBuiltinsUtils.GetProperty(isolate, obj, 0));
            return copy;
        }

        // 4. Let A be ? ArrayCreate(𝔽(len)).
        // The actual array will be created later, but perform the range check.
        if (len > JSArray.kMaxArrayLength)
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidArrayLength, JSValue.FromNumber(len));
        }

        // 5-9. The implementation is shared with Array.prototype.sort.
        SortState sortState = NewSortState(isolate, obj, comparefn, len, true);
        return ArrayPowerSortIntoCopy(isolate, sortState);
    }

    /// <summary>ArrayPowerSortIntoCopy (array-to-sorted.tq).</summary>
    static JSArray ArrayPowerSortIntoCopy(Isolate isolate, SortState sortState)
    {
        int numberOfNonUndefined = CompactReceiverElementsIntoWorkArray(isolate, sortState, true);
        if (numberOfNonUndefined < kMaxBinaryInsertionSortLength)
        {
            BinaryInsertionSort(isolate, sortState, 0, 0, numberOfNonUndefined);
        }
        else
        {
            ArrayPowerSortImpl(isolate, sortState, numberOfNonUndefined);
        }

        int len = sortState.SortLength;
        JSValue[] workArray = sortState.WorkArray;
        if (len <= JSArray.kMaxFastArrayLength)
        {
            // The result copy of Array.prototype.toSorted is always packed.
            ElementsKind kind = ElementsKind.PACKED_SMI_ELEMENTS;
            if (sortState.NumberOfUndefined != 0)
            {
                kind = ElementsKind.PACKED_ELEMENTS;
            }
            else
            {
                for (int i = 0; i < numberOfNonUndefined; ++i)
                {
                    if (!workArray[i].IsSmi)
                    {
                        kind = ElementsKind.PACKED_ELEMENTS;
                        break;
                    }
                }
            }
            // CopyWorkArrayToNewFastJSArray.
            var copy = new JSValue[len];
            Array.Copy(workArray, copy, numberOfNonUndefined);
            copy.AsSpan(numberOfNonUndefined).Fill(JSValue.Undefined);
            return isolate.Factory.NewJSArrayWithElements(new FixedArray(copy), kind, len);
        }

        // CopyWorkArrayToNewJSArray.
        JSArray result = ArrayBuiltinsUtils.ArrayCreate(isolate, len);
        int j = 0;
        for (; j < numberOfNonUndefined; ++j) ArrayBuiltinsUtils.SetProperty(isolate, result, j, workArray[j]);
        for (; j < len; ++j) ArrayBuiltinsUtils.SetProperty(isolate, result, j, JSValue.Undefined);
        return result;
    }
}
