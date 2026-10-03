// Port of src/objects/js-iterator-helpers.{h,tq} (JSIteratorHelper and its
// subclasses: map, filter, take, drop, flatMap, concat, zip, zipKeyed),
// JSValidIteratorWrapper (Iterator.from) and JSAsyncFromSyncIterator
// (src/objects/js-objects.h).
using V8Sharp.Builtins;

namespace V8Sharp.Objects;

/// <summary>JSIteratorHelperState.</summary>
public enum JSIteratorHelperState
{
    kSuspendedStart,
    kSuspendedYield,
    kExecuting,
    kCompleted,
}

/// <summary>V8's JSIteratorHelper: the state shared by all helpers (a generator-like state machine).</summary>
public abstract class JSIteratorHelper(Map map) : JSObject(map)
{
    public JSIteratorHelperState State = JSIteratorHelperState.kSuspendedStart;
}

/// <summary>V8's JSIteratorHelperSimple: a helper over one underlying iterator.</summary>
public abstract class JSIteratorHelperSimple(Map map) : JSIteratorHelper(map)
{
    /// <summary>underlying_iterator.[[Iterator]]; null once exhausted where V8 stores undefined.</summary>
    public JSReceiver? UnderlyingObject;
    /// <summary>underlying_iterator.[[NextMethod]].</summary>
    public JSValue UnderlyingNext;

    public IteratorRecord UnderlyingIterator
    {
        get => new(UnderlyingObject!, UnderlyingNext);
        set
        {
            UnderlyingObject = value.Object;
            UnderlyingNext = value.Next;
        }
    }
}

/// <summary>V8's JSIteratorMapHelper.</summary>
public sealed class JSIteratorMapHelper(Map map) : JSIteratorHelperSimple(map)
{
    public JSValue Mapper;
    public double Counter;
}

/// <summary>V8's JSIteratorFilterHelper.</summary>
public sealed class JSIteratorFilterHelper(Map map) : JSIteratorHelperSimple(map)
{
    public JSValue Predicate;
    public double Counter;
}

/// <summary>V8's JSIteratorTakeHelper.</summary>
public sealed class JSIteratorTakeHelper(Map map) : JSIteratorHelperSimple(map)
{
    public double Remaining;
}

/// <summary>V8's JSIteratorDropHelper.</summary>
public sealed class JSIteratorDropHelper(Map map) : JSIteratorHelperSimple(map)
{
    public double Remaining;
}

/// <summary>V8's JSIteratorFlatMapHelper.</summary>
public sealed class JSIteratorFlatMapHelper(Map map) : JSIteratorHelperSimple(map)
{
    public JSValue Mapper;
    public double Counter;
    /// <summary>inner_iterator.[[Iterator]]; null while there is no inner iterator.</summary>
    public JSReceiver? InnerObject;
    public JSValue InnerNext;
}

/// <summary>V8's JSIteratorConcatHelper (Iterator.concat).</summary>
public sealed class JSIteratorConcatHelper(Map map) : JSIteratorHelperSimple(map)
{
    /// <summary>[iterable, method] pairs.</summary>
    public FixedArray Iterables = FixedArray.Empty;
    /// <summary>The index of the current pair (a multiple of 2).</summary>
    public int Current;
}

/// <summary>JSIteratorZipHelperMode.</summary>
public enum JSIteratorZipHelperMode { kShortest, kLongest, kStrict }

/// <summary>V8's JSIteratorZipHelper (Iterator.zip).</summary>
public class JSIteratorZipHelper(Map map) : JSIteratorHelper(map)
{
    /// <summary>[iterator, next] pairs; a closed/exhausted iterator is undefined.</summary>
    public FixedArray UnderlyingIterators = FixedArray.Empty;
    public JSIteratorZipHelperMode Mode;
    public int ActiveCount;
    public FixedArray Padding = FixedArray.Empty;
}

/// <summary>V8's JSIteratorZipKeyedHelper (Iterator.zipKeyed).</summary>
public sealed class JSIteratorZipKeyedHelper(Map map) : JSIteratorZipHelper(map)
{
    public FixedArray Keys = FixedArray.Empty;
}

/// <summary>V8's JSValidIteratorWrapper (Iterator.from).</summary>
public sealed class JSValidIteratorWrapper(Map map) : JSObject(map)
{
    public JSReceiver UnderlyingObject = null!;
    public JSValue UnderlyingNext;
}

/// <summary>V8's JSAsyncFromSyncIterator.</summary>
public sealed class JSAsyncFromSyncIterator(Map map) : JSObject(map)
{
    public JSReceiver SyncIterator = null!;
    /// <summary>The "next" method of the sync iterator.</summary>
    public JSValue Next;
}
