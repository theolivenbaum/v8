// The shapes (data layout) of the special JSObject kinds, with the object-level
// operations V8 keeps next to them:
//   src/objects/arguments.h            JSArgumentsObject, SloppyArgumentsElements,
//                                      AliasedArgumentsEntry
//   src/objects/js-date.h              JSDate (the date math is in the Date builtins)
//   src/objects/js-regexp.h            JSRegExp
//   src/objects/js-generator.h         JSGeneratorObject and the async variants
//   JSStringIterator, JSRegExpStringIterator
// (JSPromise is in JSPromise.cs, the collections in JSCollection.cs, the weak
// references in JSWeakRefs.cs, the iterator helpers in JSIteratorHelpers.cs and
// the disposable stacks in JSDisposableStack.cs.)
//   src/objects/module.{h,cc}          JSModuleNamespace (exports as a name -> Cell table)
//   src/objects/js-raw-json.h, js-shadow-realm.h, js-external-object.h
//   src/objects/allocation-site.h      AllocationSite
//   src/builtins/builtins-shadow-realm-gen.cc  CallWrappedFunction, GetWrappedValue
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

// ---- Arguments --------------------------------------------------------------------

/// <summary>V8's JSArgumentsObject (sloppy and strict arguments objects).</summary>
public sealed class JSArgumentsObject(Map map) : JSObject(map)
{
    /// <summary>JSSloppyArgumentsObject::kLengthIndex / kCalleeIndex: in-object property indices.</summary>
    public const int kLengthIndex = 0;
    public const int kCalleeIndex = 1;
}

/// <summary>
/// V8's SloppyArgumentsElements: the elements of a mapped (sloppy) arguments
/// object. Entry i aliases context slot mapped_entries[i] while it is not the
/// hole; everything else lives in <see cref="Arguments"/>.
/// </summary>
public sealed class SloppyArgumentsElements(Context context, FixedArrayBase arguments, int length)
    : FixedArrayBase(InstanceType.FixedArrayType)
{
    readonly JSValue[] _mappedEntries = length == 0 ? [] : new JSValue[length];

    public Context Context = context;
    public FixedArrayBase Arguments = arguments;

    /// <summary>The number of mapped entries.</summary>
    public override int Length => _mappedEntries.Length;

    public JSValue MappedEntries(int index) => _mappedEntries[index];

    public void SetMappedEntries(int index, JSValue value) => _mappedEntries[index] = value;

    public void SetMappedEntry(int index, JSValue value) => _mappedEntries[index] = value;
}

/// <summary>V8's AliasedArgumentsEntry: a slow-mode arguments element that aliases a context slot.</summary>
public sealed class AliasedArgumentsEntry(int aliasedContextSlot) : HeapObject(InstanceType.FixedArrayType)
{
    public readonly int AliasedContextSlot = aliasedContextSlot;
}

// ---- Date and RegExp ----------------------------------------------------------------------

/// <summary>V8's JSDate: the time value and the cached local-time fields.</summary>
public sealed partial class JSDate(Map map) : JSObject(map)
{
    /// <summary>JSDate::FieldIndex.</summary>
    public enum FieldIndex
    {
        kYear,
        kMonth,
        kDay,
        kWeekday,
        kHour,
        kMinute,
        kSecond,
        kFirstUncachedField,
        kMillisecond = kFirstUncachedField,
        kDays,
        kTimeInDay,
        kFirstUTCField,
        kYearUTC = kFirstUTCField,
        kMonthUTC,
        kDayUTC,
        kWeekdayUTC,
        kHourUTC,
        kMinuteUTC,
        kSecondUTC,
        kMillisecondUTC,
        kDaysUTC,
        kTimeInDayUTC,
        kTimezoneOffset,
    }

    /// <summary>The time value (NaN for an invalid date).</summary>
    public double Value = double.NaN;

    public JSValue Year;
    public JSValue Month;
    public JSValue Day;
    public JSValue Weekday;
    public JSValue Hour;
    public JSValue Min;
    public JSValue Sec;

    /// <summary>The DateCache stamp the cached fields belong to (NaN: not cached).</summary>
    public JSValue CacheStamp = JSValue.NaN;

    /// <summary>JSDate::SetNanValue.</summary>
    public void SetNanValue()
    {
        Value = double.NaN;
        JSValue nan = JSValue.NaN;
        CacheStamp = nan;
        Year = nan;
        Month = nan;
        Day = nan;
        Hour = nan;
        Min = nan;
        Sec = nan;
        Weekday = nan;
    }
}

/// <summary>
/// V8's JSRegExp. The fields, RegExpData and the operations of
/// src/objects/js-regexp.{h,cc} are in JSRegExp.cs.
/// </summary>
public sealed partial class JSRegExp(Map map) : JSObject(map)
{
    /// <summary>In-object fields of a JSRegExp: lastIndex.</summary>
    public const int kInObjectFieldCount = 1;
}

// ---- Generators ------------------------------------------------------------------------

/// <summary>V8's JSGeneratorObject.</summary>
public class JSGeneratorObject(Map map) : JSObject(map)
{
    /// <summary>JSGeneratorObject::ResumeMode.</summary>
    public enum ResumeMode { kNext, kReturn, kThrow, kRethrow }

    public const int kGeneratorExecuting = -2;
    public const int kGeneratorClosed = -1;

    public JSFunction Function = null!;
    public Context Context = null!;
    public JSValue Receiver;
    public JSValue InputOrDebugPos;
    public ResumeMode Mode;
    /// <summary>The bytecode offset to resume at, or kGeneratorExecuting / kGeneratorClosed.</summary>
    public int ContinuationValue;
    /// <summary>The saved registers and parameters (V8's parameters_and_registers).</summary>
    public FixedArray ParametersAndRegisters = FixedArray.Empty;

    public bool IsClosed => ContinuationValue == kGeneratorClosed;
    public bool IsExecuting => ContinuationValue == kGeneratorExecuting;
    public bool IsSuspended => ContinuationValue >= 0;
}

/// <summary>V8's JSAsyncFunctionObject.</summary>
public sealed class JSAsyncFunctionObject(Map map) : JSGeneratorObject(map)
{
    public JSPromise Promise = null!;
    /// <summary>The await closures, allocated on the first await and reused (undefined until then).</summary>
    public JSValue AwaitResolveClosure;
    public JSValue AwaitRejectClosure;
}

/// <summary>V8's JSAsyncGeneratorObject.</summary>
public sealed class JSAsyncGeneratorObject(Map map) : JSGeneratorObject(map)
{
    /// <summary>The AsyncGeneratorRequest queue (undefined when empty).</summary>
    public JSValue Queue;
    public bool IsAwaiting;
}

// ---- String iterators ----------------------------------------------------------------

/// <summary>V8's JSStringIterator.</summary>
public sealed class JSStringIterator(Map map) : JSObject(map)
{
    public JSString String = ReadOnlyRoots.empty_string;
    public int Index;
}

/// <summary>V8's JSRegExpStringIterator (String.prototype.matchAll).</summary>
public sealed class JSRegExpStringIterator(Map map) : JSObject(map)
{
    public JSValue IteratingRegExp;
    public JSString IteratedString = ReadOnlyRoots.empty_string;
    public bool Done;
    public bool Global;
    public bool Unicode;
}


// ---- Modules and the rest ------------------------------------------------------------------

/// <summary>
/// V8's JSModuleNamespace. The module record belongs to the module system;
/// the namespace keeps the export table V8 reads through module()->exports():
/// name -> Cell whose value is the export binding (TheHole while in TDZ).
/// </summary>
public sealed class JSModuleNamespace(Map map) : JSObject(map)
{
    /// <summary>The module.</summary>
    public Module Module = null!;

    /// <summary>The module's exports (module()->exports()).</summary>
    public ObjectHashTable Exports => Module.Exports;

    /// <summary>JSModuleNamespace::HasExport.</summary>
    public bool HasExport(Isolate isolate, JSString name) => !Exports.Lookup(isolate, name).IsTheHole;

    /// <summary>JSModuleNamespace::GetExport.</summary>
    public JSValue GetExport(Isolate isolate, JSString name)
    {
        JSValue obj = Exports.Lookup(isolate, name);
        if (obj.IsTheHole) return JSValue.Undefined;

        JSValue value = ((Cell)obj.Object).Value;
        if (value.IsTheHole)
        {
            // According to https://tc39.es/ecma262/#sec-InnerModuleLinking
            // step 10 and
            // https://tc39.es/ecma262/#sec-source-text-module-record-initialize-environment
            // step 8-25, variables must be declared in Link. And according to
            // https://tc39.es/ecma262/#sec-module-namespace-exotic-objects-get-p-receiver,
            // here accessing uninitialized variable error should be thrown.
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.AccessedUninitializedVariable, name));
        }
        return value;
    }

    /// <summary>JSModuleNamespace::GetPropertyAttributes.</summary>
    public static new PropertyAttributes GetPropertyAttributes(ref LookupIterator it)
    {
        JSModuleNamespace obj = it.GetHolder<JSModuleNamespace>();
        var name = (JSString)it.GetName();
        Isolate isolate = it.Isolate;

        JSValue lookup = obj.Exports.Lookup(isolate, name);
        if (lookup.IsTheHole) return PropertyAttributes.ABSENT;

        JSValue value = ((Cell)lookup.Object).Value;
        if (value.IsTheHole)
        {
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.NotDefined, name));
        }
        return it.PropertyAttributes();
    }

    /// <summary>JSModuleNamespace::DefineOwnProperty (ES #sec-module-namespace-exotic-objects-defineownproperty-p-desc).</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSModuleNamespace obj, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        // 1. If Type(P) is Symbol, return OrdinaryDefineOwnProperty(O, P, Desc).
        if (key.IsSymbol) return JSReceiver.OrdinaryDefineOwnProperty(isolate, obj, key, ref desc, shouldThrow);

        // 2. Let current be ? O.[[GetOwnProperty]](P).
        var lookupKey = new PropertyKey(isolate, key);
        var it = new LookupIterator(isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
        var current = new PropertyDescriptor();
        bool hasOwn = JSReceiver.GetOwnPropertyDescriptor(ref it, ref current);

        // 3. If current is undefined, return false.
        // 4. If Desc.[[Configurable]] is present and has value true, return false.
        // 5. If Desc.[[Enumerable]] is present and has value false, return false.
        // 6. If ! IsAccessorDescriptor(Desc) is true, return false.
        // 7. If Desc.[[Writable]] is present and has value false, return false.
        // 8. If Desc.[[Value]] is present, return
        //    SameValue(Desc.[[Value]], current.[[Value]]).
        if (!hasOwn || (desc.HasConfigurable && desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) ||
            PropertyDescriptor.IsAccessorDescriptor(in desc) || (desc.HasWritable && !desc.Writable) ||
            (desc.HasValue && !ObjectOps.SameValue(desc.Value, current.Value)))
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.RedefineDisallowed, key);
        }

        return true;
    }
}

/// <summary>V8's JSRawJson (JSON.rawJSON).</summary>
public sealed class JSRawJson(Map map) : JSObject(map)
{
    public const int kRawJsonInitialIndex = 0;
}

/// <summary>V8's JSShadowRealm.</summary>
public sealed class JSShadowRealm(Map map) : JSObject(map)
{
    public NativeContext NativeContext = null!;
}

/// <summary>V8's JSExternalObject (v8::External).</summary>
public sealed class JSExternalObject(Map map) : JSObject(map)
{
    public object? Value;
}

/// <summary>
/// V8's AllocationSite: allocation feedback for array and object literals
/// (elements-kind transitions and pretenuring). V8Sharp has no mementos or
/// pretenuring; the site records the transition feedback only.
/// </summary>
public sealed class AllocationSite() : HeapObject(InstanceType.AllocationSiteType)
{
    /// <summary>AllocationSite::PretenureDecision.</summary>
    public enum PretenureDecision { kUndecided = 0, kDontTenure = 1, kMaybeTenure = 2, kTenure = 3, kZombie = 4 }

    /// <summary>The boilerplate object, or null.</summary>
    public JSObject? Boilerplate;

    /// <summary>The transition info: the elements kind for array sites.</summary>
    public ElementsKind ElementsKind = ElementsKind.PACKED_SMI_ELEMENTS;
    public bool DoNotInlineCall;
    public AllocationSite? NestedSite;
    public PretenureDecision Decision;

    /// <summary>AllocationSite::ShouldTrack: only more-general transitions of array sites are tracked.</summary>
    public static bool ShouldTrack(ElementsKind from, ElementsKind to) => ElementsKinds.IsMoreGeneralElementsKindTransition(from, to);

    /// <summary>AllocationSite::kMaximumArrayBytesToPretransition.</summary>
    public const uint kMaximumArrayBytesToPretransition = 8 * 1024;

    /// <summary>AllocationSite::ShouldTrack(boilerplate elements kind).</summary>
    public static bool ShouldTrack(ElementsKind boilerplateElementsKind) => ElementsKinds.IsSmiElementsKind(boilerplateElementsKind);

    /// <summary>AllocationSite::CanTrack.</summary>
    public static bool CanTrack(InstanceType type) => type == InstanceType.JSArrayType;

    /// <summary>AllocationSite::DigestTransitionFeedback (kUpdate, or kCheckOnly when <paramref name="checkOnly"/>).</summary>
    public static bool DigestTransitionFeedback(Isolate isolate, AllocationSite site, ElementsKind toKind, bool checkOnly = false)
    {
        if (site.Boilerplate is JSArray boilerplate)
        {
            // The site points to an array literal: transition its boilerplate.
            ElementsKind kind = boilerplate.GetElementsKind();
            // if kind is holey ensure that to_kind is as well.
            if (ElementsKinds.IsHoleyElementsKind(kind)) toKind = ElementsKinds.GetHoleyElementsKind(toKind);
            if (!ElementsKinds.IsMoreGeneralElementsKindTransition(kind, toKind)) return false;
            // If the array is huge, it's not likely to be defined in a local
            // function, so we shouldn't make new instances of it very often.
            if (!ObjectOps.ToArrayLength(boilerplate.Length, out uint length) || length > kMaximumArrayBytesToPretransition) return false;
            if (checkOnly) return true;
            JSObject.TransitionElementsKind(isolate, boilerplate, toKind);
            site.ElementsKind = boilerplate.GetElementsKind();
            return true;
        }
        {
            // The AllocationSite is for a constructed Array.
            ElementsKind kind = site.ElementsKind;
            // if kind is holey ensure that to_kind is as well.
            if (ElementsKinds.IsHoleyElementsKind(kind)) toKind = ElementsKinds.GetHoleyElementsKind(toKind);
            if (!ElementsKinds.IsMoreGeneralElementsKindTransition(kind, toKind)) return false;
            if (checkOnly) return true;
            site.ElementsKind = toKind;
            return true;
        }
    }
}

/// <summary>ShadowRealm wrapped functions (builtins-shadow-realm-gen.cc, js-function.cc).</summary>
public sealed partial class JSWrappedFunction
{
    /// <summary>JSWrappedFunction::Create (ES #sec-wrappedfunctioncreate).</summary>
    public static JSWrappedFunction Create(Isolate isolate, NativeContext creationContext, JSReceiver value)
    {
        // The value must be a callable according to the specification.
        Debug.Assert(ObjectOps.IsCallable(value));
        // The intermediate wrapped functions are not user-visible. And calling a
        // wrapped function won't cause a side effect in the creation realm.
        // Unwrap here to avoid nested unwrapping at the call site.
        if (value is JSWrappedFunction targetWrapped) value = targetWrapped.WrappedTargetFunction;

        // 1. Let internalSlotsList be the internal slots listed in Table 2, plus
        // [[Prototype]] and [[Extensible]].
        // 2. Let wrapped be ! MakeBasicObject(internalSlotsList).
        // 3. Set wrapped.[[Prototype]] to
        // callerRealm.[[Intrinsics]].[[%Function.prototype%]].
        // 4. Set wrapped.[[Call]] as described in 2.1.
        // 5. Set wrapped.[[WrappedTargetFunction]] to Target.
        // 6. Set wrapped.[[Realm]] to callerRealm.
        var wrapped = new JSWrappedFunction(creationContext.WrappedFunctionMap, value, creationContext);

        // 7. Let result be CopyNameAndLength(wrapped, Target, "wrapped").
        try
        {
            CopyNameAndLength(isolate, wrapped, value, null, 0);
        }
        catch (JavaScriptException e)
        {
            // 8. If result is an Abrupt Completion, throw a TypeError exception.
            // The TypeError thrown is created with creation Realm's TypeError
            // constructor instead of the executing Realm's.
            JSString str = ObjectOps.NoSideEffectsToString(isolate, e.Value);
            isolate.Throw(isolate.Factory.NewError(creationContext.TypeErrorFunction, MessageTemplate.CannotWrap, [str]));
        }

        // 9. Return wrapped.
        return wrapped;
    }

    /// <summary>ShadowRealmGetWrappedValue (https://tc39.es/proposal-shadowrealm/#sec-getwrappedvalue).</summary>
    public static JSValue GetWrappedValue(Isolate isolate, NativeContext creationContext, JSValue value)
    {
        // 2. Return value.
        if (value.HeapObjectOrNull is not JSReceiver receiver) return value;

        // 1a. If IsCallable(value) is false, throw a TypeError exception.
        if (!receiver.Map.IsCallable) isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NotCallable, value));
        // 1b. Return ? WrappedFunctionCreate(callerRealm, value).
        return Create(isolate, creationContext, receiver);
    }

    /// <summary>The CallWrappedFunction builtin ([[Call]] of a wrapped function exotic object).</summary>
    public static JSValue Call(Isolate isolate, JSWrappedFunction function, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        isolate.StackGuard.StackCheck(isolate);

        // 1. Let target be F.[[WrappedTargetFunction]].
        JSReceiver target = function.WrappedTargetFunction;
        // 4. Let callerRealm be ? GetFunctionRealm(F).
        NativeContext callerContext = function.Context;
        // 3. Let targetRealm be ? GetFunctionRealm(target).
        NativeContext targetContext = JSReceiver.GetFunctionRealm(isolate, target);
        // 5. NOTE: Any exception objects produced after this point are associated
        // with callerRealm.

        // 8. Let wrappedThisArgument to ? GetWrappedValue(targetRealm, thisArgument).
        JSValue wrappedReceiver = GetWrappedValue(isolate, targetContext, receiver);
        // 6. Let wrappedArgs be a new empty List.
        var wrappedArgs = new JSValue[args.Length];
        // 7. For each element arg of argumentsList, do
        for (int i = 0; i < args.Length; i++)
        {
            // 7a. Let wrappedValue be ? GetWrappedValue(targetRealm, arg).
            // 7b. Append wrappedValue to wrappedArgs.
            wrappedArgs[i] = GetWrappedValue(isolate, targetContext, args[i]);
        }

        JSValue result;
        try
        {
            // 9. Let result be the Completion Record of Call(target,
            // wrappedThisArgument, wrappedArgs).
            result = Execution.Call(isolate, target, wrappedReceiver, wrappedArgs);
        }
        catch (JavaScriptException e)
        {
            // 11. Else,
            // 11a. Throw a TypeError exception.
            JSString str = ObjectOps.NoSideEffectsToString(isolate, e.Value);
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.CallWrappedFunctionThrew, str));
            return default;
        }

        // 10. If result.[[Type]] is normal or result.[[Type]] is return, then
        // 10a. Return ? GetWrappedValue(callerRealm, result.[[Value]]).
        return GetWrappedValue(isolate, callerContext, result);
    }
}
