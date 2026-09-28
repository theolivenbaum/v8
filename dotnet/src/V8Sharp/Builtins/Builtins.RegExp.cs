// Port of the RegExp builtins: src/builtins/builtins-regexp-gen.{h,cc}
// (fast-path checks, RegExpExecInternal, ConstructNewResultFromMatchInfo,
// FlagsGetter, RegExpConstructor, RegExpPrototypeCompile, AdvanceStringIndex),
// src/builtins/regexp.tq (RegExpExec, RegExpPrototypeExecBody*, the flag
// getters, IsRegExp, RegExpCreate, last index helpers), regexp-exec.tq,
// regexp-test.tq, regexp-source.tq, src/builtins/builtins-regexp.cc
// (toString, the legacy static properties, RegExp.escape) and
// src/regexp/regexp-utils.cc.
//
// @@match, @@matchAll, @@replace, @@search and @@split are in
// Builtins.RegExp.Methods.cs.
using System.Buffers;
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using V8Sharp.RegExp;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterRegExp() => BuiltinsRegExp.Register();
}

/// <summary>The RegExp builtins.</summary>
public static partial class BuiltinsRegExp
{
    internal static void Register()
    {
        BuiltinRegistry.Register(Builtin.RegExpConstructor, RegExpConstructor);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeExec, RegExpPrototypeExec);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeTest, RegExpPrototypeTest);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeCompile, RegExpPrototypeCompile);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeToString, RegExpPrototypeToString);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeSourceGetter, RegExpPrototypeSourceGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeFlagsGetter, RegExpPrototypeFlagsGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeGlobalGetter, RegExpPrototypeGlobalGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeIgnoreCaseGetter, RegExpPrototypeIgnoreCaseGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeMultilineGetter, RegExpPrototypeMultilineGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeHasIndicesGetter, RegExpPrototypeHasIndicesGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeLinearGetter, RegExpPrototypeLinearGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeDotAllGetter, RegExpPrototypeDotAllGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeStickyGetter, RegExpPrototypeStickyGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeUnicodeGetter, RegExpPrototypeUnicodeGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeUnicodeSetsGetter, RegExpPrototypeUnicodeSetsGetter);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeMatch, RegExpPrototypeMatch);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeMatchAll, RegExpPrototypeMatchAll);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeReplace, RegExpPrototypeReplace);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeSearch, RegExpPrototypeSearch);
        BuiltinRegistry.Register(Builtin.RegExpPrototypeSplit, RegExpPrototypeSplit);
        BuiltinRegistry.Register(Builtin.RegExpStringIteratorPrototypeNext, RegExpStringIteratorPrototypeNext);
        BuiltinRegistry.Register(Builtin.RegExpEscape, RegExpEscape);

        BuiltinRegistry.Register(Builtin.RegExpInputGetter, RegExpInputGetter);
        BuiltinRegistry.Register(Builtin.RegExpInputSetter, RegExpInputSetter);
        BuiltinRegistry.Register(Builtin.RegExpLastMatchGetter, RegExpLastMatchGetter);
        BuiltinRegistry.Register(Builtin.RegExpLastParenGetter, RegExpLastParenGetter);
        BuiltinRegistry.Register(Builtin.RegExpLeftContextGetter, RegExpLeftContextGetter);
        BuiltinRegistry.Register(Builtin.RegExpRightContextGetter, RegExpRightContextGetter);
        BuiltinRegistry.Register(Builtin.RegExpCapture1Getter, RegExpCapture1Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture2Getter, RegExpCapture2Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture3Getter, RegExpCapture3Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture4Getter, RegExpCapture4Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture5Getter, RegExpCapture5Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture6Getter, RegExpCapture6Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture7Getter, RegExpCapture7Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture8Getter, RegExpCapture8Getter);
        BuiltinRegistry.Register(Builtin.RegExpCapture9Getter, RegExpCapture9Getter);
    }

    // ---- Fast-path checks (BranchIfFastRegExp and friends) ------------------------------

    /// <summary>PrototypeCheckAssembler::Flags.</summary>
    [Flags]
    internal enum PrototypeCheck
    {
        /// <summary>The prototype map is the initial one and the checked properties are const.</summary>
        kCheckPrototypePropertyConstness = 1 << 0,
        /// <summary>Otherwise, the checked properties still hold their initial values.</summary>
        kCheckPrototypePropertyIdentity = 1 << 1,
        kCheckFull = kCheckPrototypePropertyConstness | kCheckPrototypePropertyIdentity,
    }

    /// <summary>
    /// A property to check on %RegExp.prototype% (DescriptorIndexNameValue):
    /// its descriptor index, name, and the native context slot holding the
    /// expected value.
    /// </summary>
    internal readonly record struct DescriptorIndexNameValue(int DescriptorIndex, Name Name, Context.Field ExpectedValueContextIndex);

    internal static readonly DescriptorIndexNameValue kExecDescriptor =
        new(JSRegExp.kExecFunctionDescriptorIndex, ReadOnlyRoots.exec_string, Context.Field.REGEXP_EXEC_FUNCTION_INDEX);
    internal static readonly DescriptorIndexNameValue kMatchDescriptor =
        new(JSRegExp.kSymbolMatchFunctionDescriptorIndex, ReadOnlyRoots.match_symbol, Context.Field.REGEXP_MATCH_FUNCTION_INDEX);
    internal static readonly DescriptorIndexNameValue kMatchAllDescriptor =
        new(JSRegExp.kSymbolMatchAllFunctionDescriptorIndex, ReadOnlyRoots.match_all_symbol, Context.Field.REGEXP_MATCH_ALL_FUNCTION_INDEX);
    internal static readonly DescriptorIndexNameValue kReplaceDescriptor =
        new(JSRegExp.kSymbolReplaceFunctionDescriptorIndex, ReadOnlyRoots.replace_symbol, Context.Field.REGEXP_REPLACE_FUNCTION_INDEX);
    internal static readonly DescriptorIndexNameValue kSearchDescriptor =
        new(JSRegExp.kSymbolSearchFunctionDescriptorIndex, ReadOnlyRoots.search_symbol, Context.Field.REGEXP_SEARCH_FUNCTION_INDEX);
    internal static readonly DescriptorIndexNameValue kSplitDescriptor =
        new(JSRegExp.kSymbolSplitFunctionDescriptorIndex, ReadOnlyRoots.split_symbol, Context.Field.REGEXP_SPLIT_FUNCTION_INDEX);

    /// <summary>HasInitialRegExpMap: the object has %RegExp%'s initial map.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasInitialRegExpMap(Isolate isolate, HeapObject? obj) =>
        obj is JSRegExp regexp && ReferenceEquals(regexp.Map, isolate.NativeContext.RegExpFunction.InitialMap);

    /// <summary>TaggedIsPositiveSmi on the in-object lastIndex.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool LastIndexIsPositiveSmi(JSRegExp regexp)
    {
        JSValue lastIndex = regexp.LastIndex;
        return lastIndex.IsSmi && lastIndex.Number >= 0;
    }

    /// <summary>
    /// RegExpBuiltinsAssembler::IsFastRegExpNoPrototype: the initial map and a
    /// non-negative Smi lastIndex; the prototype is not checked.
    /// </summary>
    internal static bool IsFastRegExpNoPrototype(Isolate isolate, JSValue obj)
    {
        if (isolate.Flags.force_slow_path) return false;
        return HasInitialRegExpMap(isolate, obj.HeapObjectOrNull) && LastIndexIsPositiveSmi((JSRegExp)obj.Object);
    }

    /// <summary>
    /// RegExpBuiltinsAssembler::BranchIfFastRegExp: an unmodified JSRegExp
    /// (initial map, Smi lastIndex, intact species protector) whose prototype
    /// still has the initial exec (and the additional property).
    /// </summary>
    internal static bool IsFastRegExp(Isolate isolate, JSValue obj, PrototypeCheck flags,
        in DescriptorIndexNameValue? additionalPropertyToCheck = null)
    {
        if (isolate.Flags.force_slow_path) return false;
        // This should only be needed for String.p.(split||matchAll), but we are
        // conservative here.
        if (!Protectors.IsRegExpSpeciesLookupChainIntact(isolate)) return false;
        if (!HasInitialRegExpMap(isolate, obj.HeapObjectOrNull)) return false;
        var regexp = (JSRegExp)obj.Object;
        // The smi check is required to omit ToLength(lastIndex) calls with possible
        // user-code execution on the fast path.
        if (!LastIndexIsPositiveSmi(regexp)) return false;
        // Verify the prototype.
        if (regexp.Map.Prototype is not JSObject prototype) return false;
        NativeContext nativeContext = isolate.NativeContext;
        return PrototypeCheckAndBranch(nativeContext, prototype, flags, kExecDescriptor, additionalPropertyToCheck);
    }

    /// <summary>PrototypeCheckAssembler::CheckAndBranch.</summary>
    static bool PrototypeCheckAndBranch(NativeContext nativeContext, JSObject prototype, PrototypeCheck flags,
        in DescriptorIndexNameValue exec, in DescriptorIndexNameValue? additional)
    {
        Map prototypeMap = prototype.Map;
        if ((flags & PrototypeCheck.kCheckPrototypePropertyConstness) != 0)
        {
            // A simple prototype map identity check. Note that map identity does not
            // guarantee unmodified properties. It does guarantee that no new properties
            // have been added, or old properties deleted.
            if (ReferenceEquals(prototypeMap, nativeContext.RegExpPrototypeMap))
            {
                // We need to make sure that relevant properties in the prototype have
                // not been tampered with. We do this by checking that their slots
                // in the prototype's descriptor array are still marked as const.
                DescriptorArray descriptors = prototypeMap.InstanceDescriptors;
                bool allConst = descriptors.GetDetails(exec.DescriptorIndex).Constness == PropertyConstness.Const;
                if (additional is { } a)
                {
                    allConst &= descriptors.GetDetails(a.DescriptorIndex).Constness == PropertyConstness.Const;
                }
                // V8Sharp also compares the values, so a field whose constness
                // tracking was bypassed still fails the check (deviations.md).
                if (allConst && HasExpectedValue(nativeContext, prototype, exec) &&
                    (additional is not { } a2 || HasExpectedValue(nativeContext, prototype, a2)))
                {
                    return true;
                }
            }
            if ((flags & PrototypeCheck.kCheckPrototypePropertyIdentity) == 0) return false;
        }

        // The above checks have failed, for whatever reason (maybe the prototype
        // map has changed, or a property is no longer const). This block implements
        // a more thorough check that can also accept maps which 1. do not have the
        // initial map, 2. have mutable relevant properties, but 3. still match the
        // expected value for all relevant properties.
        return HasExpectedValue(nativeContext, prototype, exec) &&
               (additional is not { } p || HasExpectedValue(nativeContext, prototype, p));
    }

    /// <summary>The property identity check of PrototypeCheckAssembler for one property.</summary>
    static bool HasExpectedValue(NativeContext nativeContext, JSObject prototype, in DescriptorIndexNameValue p)
    {
        Map map = prototype.Map;
        // Logic below only handles maps with fast properties.
        if (map.IsDictionaryMap) return false;
        int descriptor = p.DescriptorIndex;
        // If the greatest descriptor index is out of bounds, the map cannot be fast.
        if (descriptor >= map.NumberOfOwnDescriptors) return false;
        DescriptorArray descriptors = map.InstanceDescriptors;
        // Check if the name is correct. This essentially checks that
        // the descriptor index corresponds to the insertion order in
        // the bootstrapper.
        if (!ReferenceEquals(descriptors.GetKey(descriptor), p.Name)) return false;
        PropertyDetails details = descriptors.GetDetails(descriptor);
        if (details.Kind != PropertyKind.Data) return false;
        JSValue value = details.Location == PropertyLocation.Field
            ? prototype.RawFastPropertyAt(FieldIndex.ForDescriptor(map, new InternalIndex(descriptor)))
            : descriptors.GetStrongValue(new InternalIndex(descriptor));
        return value.IsIdenticalTo(nativeContext.Slots[(int)p.ExpectedValueContextIndex]);
    }

    /// <summary>BranchIfFastRegExp_Strict (constness check only).</summary>
    internal static bool IsFastRegExpStrict(Isolate isolate, JSValue obj) =>
        IsFastRegExp(isolate, obj, PrototypeCheck.kCheckPrototypePropertyConstness);

    /// <summary>BranchIfFastRegExp_Permissive (constness or identity).</summary>
    internal static bool IsFastRegExpPermissive(Isolate isolate, JSValue obj) =>
        IsFastRegExp(isolate, obj, PrototypeCheck.kCheckFull);

    /// <summary>BranchIfFastRegExpForMatch.</summary>
    internal static bool IsFastRegExpForMatch(Isolate isolate, JSValue obj) =>
        IsFastRegExp(isolate, obj, PrototypeCheck.kCheckPrototypePropertyConstness, kMatchDescriptor);

    /// <summary>BranchIfFastRegExpForSearch.</summary>
    internal static bool IsFastRegExpForSearch(Isolate isolate, JSValue obj) =>
        IsFastRegExp(isolate, obj, PrototypeCheck.kCheckPrototypePropertyConstness, kSearchDescriptor);

    /// <summary>
    /// regexp::Utils::IsUnmodifiedRegExp: initial map, initial prototype map
    /// with a const exec, intact species protector and Smi lastIndex.
    /// </summary>
    internal static bool IsUnmodifiedRegExp(Isolate isolate, JSValue obj)
    {
        if (isolate.Flags.force_slow_path) return false;
        if (!HasInitialRegExpMap(isolate, obj.HeapObjectOrNull)) return false;
        var regexp = (JSRegExp)obj.Object;
        // Check the receiver's prototype's map.
        if (regexp.Map.Prototype is not JSObject proto) return false;
        Map protoMap = proto.Map;
        if (!ReferenceEquals(protoMap, isolate.NativeContext.RegExpPrototypeMap)) return false;
        // Check that the "exec" method is unmodified.
        if (protoMap.InstanceDescriptors.GetDetails(JSRegExp.kExecFunctionDescriptorIndex).Constness != PropertyConstness.Const)
        {
            return false;
        }
        if (!Protectors.IsRegExpSpeciesLookupChainIntact(isolate)) return false;
        // The smi check is required to omit ToLength(lastIndex) calls with possible
        // user-code execution on the fast path.
        return LastIndexIsPositiveSmi(regexp);
    }

    /// <summary>IsReceiverInitialRegExpPrototype (regexp.tq).</summary>
    static bool IsReceiverInitialRegExpPrototype(Isolate isolate, JSValue receiver) =>
        ReferenceEquals(receiver.HeapObjectOrNull, isolate.NativeContext.RegExpFunction.InitialMap.Prototype);

    /// <summary>BranchIfRegExpResult: the object has an initial JSRegExpResult map.</summary>
    static bool IsRegExpResult(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is not JSReceiver receiver) return false;
        NativeContext nc = isolate.NativeContext;
        return ReferenceEquals(receiver.Map, nc.RegExpResultMap) || ReferenceEquals(receiver.Map, nc.RegExpResultWithIndicesMap);
    }

    // ---- IsRegExp / RegExpCreate (regexp.tq) ------------------------------------------

    /// <summary>IsRegExp (ECMA-262 7.2.8).</summary>
    public static bool IsRegExp(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is not JSReceiver receiver) return false;
        // Check @match.
        JSValue value = JSReceiver.GetProperty(isolate, receiver, ReadOnlyRoots.match_symbol);
        if (value.IsUndefined) return receiver is JSRegExp;
        // The common path. Symbol.match exists, equals the RegExpPrototypeMatch
        // function (and is thus trueish), and the receiver is a JSRegExp.
        if (ObjectOps.BooleanValue(value))
        {
            if (receiver is not JSRegExp) isolate.CountUsage("kRegExpMatchIsTrueishOnNonJSRegExp");
            return true;
        }
        if (receiver is JSRegExp) isolate.CountUsage("kRegExpMatchIsFalseishOnJSRegExp");
        return false;
    }

    /// <summary>RegExpCreate (regexp.tq): new %RegExp%(pattern, flags) without the constructor dance.</summary>
    public static JSRegExp RegExpCreate(Isolate isolate, JSValue maybeString, JSString flags)
    {
        JSString pattern = maybeString.IsUndefined ? ReadOnlyRoots.empty_string : BuiltinsString.ToStringInline(isolate, maybeString);
        var regexp = (JSRegExp)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.RegExpFunction.InitialMap);
        return JSRegExp.Initialize(isolate, regexp, pattern, flags);
    }

    // ---- lastIndex (regexp.tq, regexp-utils.cc) --------------------------------------------

    /// <summary>SlowLoadLastIndex: Get(regexp, "lastIndex").</summary>
    internal static JSValue SlowLoadLastIndex(Isolate isolate, JSValue regexp) =>
        ObjectOps.GetProperty(isolate, regexp, ReadOnlyRoots.lastIndex_string);

    /// <summary>SlowStoreLastIndex: Set(regexp, "lastIndex", value, true).</summary>
    internal static void SlowStoreLastIndex(Isolate isolate, JSValue regexp, JSValue value) =>
        ObjectOps.SetProperty(isolate, regexp, ReadOnlyRoots.lastIndex_string, value, StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);

    /// <summary>LoadLastIndex.</summary>
    internal static JSValue LoadLastIndex(Isolate isolate, JSValue regexp, bool isFastPath) =>
        isFastPath ? ((JSRegExp)regexp.Object).LastIndex : SlowLoadLastIndex(isolate, regexp);

    /// <summary>LoadLastIndexAsLength: ToLength(lastIndex), omitted for non-negative Smis.</summary>
    internal static JSValue LoadLastIndexAsLength(Isolate isolate, JSRegExp regexp, bool isFastPath)
    {
        JSValue lastIndex = LoadLastIndex(isolate, regexp, isFastPath);
        if (isFastPath) return lastIndex;
        if (lastIndex.IsSmi && lastIndex.Number >= 0) return lastIndex;
        return ObjectOps.ToLength(isolate, lastIndex);
    }

    /// <summary>StoreLastIndex.</summary>
    internal static void StoreLastIndex(Isolate isolate, JSValue regexp, JSValue value, bool isFastPath)
    {
        if (isFastPath) ((JSRegExp)regexp.Object).LastIndex = value;
        else SlowStoreLastIndex(isolate, regexp, value);
    }

    /// <summary>regexp::Utils::SetLastIndex.</summary>
    internal static void UtilsSetLastIndex(Isolate isolate, JSReceiver recv, double value)
    {
        JSValue valueAsObject = JSValue.FromNumber(value);
        if (HasInitialRegExpMap(isolate, recv)) ((JSRegExp)recv).LastIndex = valueAsObject;
        else ObjectOps.SetProperty(isolate, recv, ReadOnlyRoots.lastIndex_string, valueAsObject, StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);
    }

    /// <summary>regexp::Utils::GetLastIndex.</summary>
    internal static JSValue UtilsGetLastIndex(Isolate isolate, JSReceiver recv) =>
        HasInitialRegExpMap(isolate, recv)
            ? ((JSRegExp)recv).LastIndex
            : JSReceiver.GetProperty(isolate, recv, ReadOnlyRoots.lastIndex_string);

    /// <summary>regexp::Utils::AdvanceStringIndex.</summary>
    internal static double AdvanceStringIndex(JSString s, double index, bool unicode)
    {
        int length = s.Length;
        if (unicode && index < length)
        {
            int i = (int)index;
            char first = s.Get(i);
            if (first >= 0xD800 && first <= 0xDBFF && i + 1 < length)
            {
                char second = s.Get(i + 1);
                if (second >= 0xDC00 && second <= 0xDFFF) return index + 2;
            }
        }
        return index + 1;
    }

    /// <summary>regexp::Utils::SetAdvancedStringIndex.</summary>
    internal static void SetAdvancedStringIndex(Isolate isolate, JSReceiver regexp, JSString s, bool unicode)
    {
        JSValue lastIndexObj = JSReceiver.GetProperty(isolate, regexp, ReadOnlyRoots.lastIndex_string);
        lastIndexObj = ObjectOps.ToLength(isolate, lastIndexObj);
        double lastIndex = lastIndexObj.Number;
        double newLastIndex = AdvanceStringIndex(s, lastIndex, unicode);
        UtilsSetLastIndex(isolate, regexp, newLastIndex);
    }

    // ---- RegExpExec -----------------------------------------------------------------------

    /// <summary>
    /// RegExpExec (regexp.tq): calls the "exec" property and checks its result,
    /// falling back to the builtin exec when it is not callable.
    /// </summary>
    public static JSValue RegExpExec(Isolate isolate, JSReceiver receiver, JSString s)
    {
        // Take the slow path of fetching the exec property, calling it, and
        // verifying its return value.
        JSValue exec = JSReceiver.GetProperty(isolate, receiver, ReadOnlyRoots.exec_string);
        if (ObjectOps.IsCallable(exec))
        {
            JSValue result = Execution.Call(isolate, exec, receiver, [s]);
            if (!result.IsNull && !result.IsJSReceiver)
            {
                isolate.ThrowTypeError(MessageTemplate.InvalidRegExpExecResult);
            }
            return result;
        }
        if (receiver is not JSRegExp regexp)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.exec",
                receiver);
        }
        return RegExpPrototypeExecBody(isolate, regexp, s, false);
    }

    /// <summary>
    /// RegExpBuiltinsAssembler::RegExpExecInternal_Single: one exec from
    /// lastIndex; fills the last match info on success, else returns null.
    /// </summary>
    internal static RegExpMatchInfo? RegExpExecInternalSingle(Isolate isolate, JSRegExp regexp, JSString s, JSValue lastIndex)
    {
        // At this point, last_index is definitely a canonicalized non-negative
        // number, which implies that any non-Smi last_index is greater than
        // the maximal string length. If lastIndex > string.length then the matcher
        // must fail.
        if (!lastIndex.IsSmi) return null;
        int intLastIndex = (int)lastIndex.Number;
        if ((uint)intLastIndex > (uint)s.Length) return null;
        return JSRegExp.ExecSingle(isolate, regexp, s, intLastIndex, RegExpMatchInfo.Get(isolate));
    }

    /// <summary>RegExpPrototypeExecBodyWithoutResult (regexp.tq).</summary>
    internal static RegExpMatchInfo? RegExpPrototypeExecBodyWithoutResult(Isolate isolate, JSRegExp regexp, JSString s,
        JSValue regexpLastIndex, bool isFastPath)
    {
        if (!isFastPath) isolate.CountUsage("kRegExpExecCalledOnSlowRegExp");
        // Check whether the regexp is global or sticky, which determines whether we
        // update last index later on.
        bool shouldUpdateLastIndex = (regexp.Flags & (RegExpFlags.Global | RegExpFlags.Sticky)) != 0;
        if (shouldUpdateLastIndex)
        {
            RegExpMatchInfo? matchIndices = RegExpExecInternalSingle(isolate, regexp, s, regexpLastIndex);
            if (matchIndices is null)
            {
                StoreLastIndex(isolate, regexp, JSValue.Zero, isFastPath);
                return null;
            }
            StoreLastIndex(isolate, regexp, JSValue.FromInt(matchIndices.Capture(1)), isFastPath);
            return matchIndices;
        }
        return RegExpExecInternalSingle(isolate, regexp, s, JSValue.Zero);
    }

    /// <summary>RegExpPrototypeExecBodyWithoutResultFast.</summary>
    internal static RegExpMatchInfo? RegExpPrototypeExecBodyWithoutResultFast(Isolate isolate, JSRegExp regexp, JSString s) =>
        RegExpPrototypeExecBodyWithoutResult(isolate, regexp, s, regexp.LastIndex, true);

    /// <summary>RegExpPrototypeExecBody (regexp.tq).</summary>
    internal static JSValue RegExpPrototypeExecBody(Isolate isolate, JSRegExp regexp, JSString s, bool isFastPath)
    {
        JSValue lastIndex = LoadLastIndexAsLength(isolate, regexp, isFastPath);
        RegExpMatchInfo? matchIndices = RegExpPrototypeExecBodyWithoutResult(isolate, regexp, s, lastIndex, isFastPath);
        if (matchIndices is null) return JSValue.Null;
        return ConstructNewResultFromMatchInfo(isolate, regexp, matchIndices, s, lastIndex);
    }

    /// <summary>RegExpPrototypeExec (regexp-exec.tq).</summary>
    public static JSValue RegExpPrototypeExec(Isolate isolate, in BuiltinArguments args)
    {
        // Ensure {receiver} is a JSRegExp.
        if (args.Receiver.HeapObjectOrNull is not JSRegExp receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.exec",
                args.Receiver);
        }
        JSString s = BuiltinsString.ToStringInline(isolate, args.AtOrUndefined(1));
        return RegExpPrototypeExecBody(isolate, receiver, s, IsFastRegExpNoPrototype(isolate, receiver));
    }

    /// <summary>
    /// RegExpBuiltinsAssembler::ConstructNewResultFromMatchInfo: the exec result
    /// array with index, input, groups (and indices for /d).
    /// </summary>
    internal static JSArray ConstructNewResultFromMatchInfo(Isolate isolate, JSRegExp regexp, RegExpMatchInfo matchInfo,
        JSString s, JSValue lastIndex)
    {
        int numIndices = matchInfo.NumberOfCaptureRegisters;
        int numResults = numIndices >> 1;
        int start = matchInfo.Capture(0);
        int end = matchInfo.Capture(1);
        Factory factory = isolate.Factory;

        // Calculate the substring of the first match before creating the result array
        // to avoid an unnecessary write barrier storing the first result.
        JSString first = factory.NewSubString(s, start, end);

        // Load flags and check if the result object needs to have indices.
        bool hasIndices = (regexp.Flags & RegExpFlags.HasIndices) != 0;
        JSArray result = AllocateRegExpResult(isolate, numResults, start, s, lastIndex, hasIndices, out FixedArray resultElements);
        resultElements[0] = first;

        // If no captures exist we can skip named capture handling as well.
        if (numResults > 1)
        {
            // Store all remaining captures.
            for (int fromCursor = 2, toCursor = 1; fromCursor < numIndices; fromCursor += 2, toCursor++)
            {
                int startCursor = matchInfo.Capture(fromCursor);
                if (startCursor == -1) continue;
                int endCursor = matchInfo.Capture(fromCursor + 1);
                resultElements[toCursor] = factory.NewSubString(s, startCursor, endCursor);
            }

            // Preparations for named capture properties. Exit early if the result does
            // not have any named captures to minimize performance impact.
            RegExpData data = regexp.Data!;
            if (data.HasCaptureNameMap)
            {
                result.RawFields[kRegExpResultGroupsIndex] = ConstructNamedCaptureGroupsObject(isolate, data, resultElements.Data);
            }
        }

        // Build indices if needed (i.e. if the /d flag is present) after named
        // capture groups are processed.
        if (hasIndices)
        {
            result.RawFields[kRegExpResultIndicesIndex] = JSRegExpResultIndices.BuildIndices(isolate, matchInfo, regexp.Data!);
        }
        return result;
    }

    // In-object fields of JSRegExpResult (Genesis::InitializeGlobal's result maps).
    const int kRegExpResultIndexIndex = 0;
    const int kRegExpResultInputIndex = 1;
    const int kRegExpResultGroupsIndex = 2;
    const int kRegExpResultRegExpInputIndex = 3;
    const int kRegExpResultRegExpLastIndexIndex = 4;
    const int kRegExpResultIndicesIndex = 5;

    /// <summary>RegExpBuiltinsAssembler::AllocateRegExpResult.</summary>
    static JSArray AllocateRegExpResult(Isolate isolate, int length, int index, JSString input, JSValue lastIndex,
        bool hasIndices, out FixedArray elements)
    {
        NativeContext nc = isolate.NativeContext;
        Map map = hasIndices ? nc.RegExpResultWithIndicesMap : nc.RegExpResultMap;
        var result = (JSArray)isolate.Factory.NewJSObjectFromMap(map);
        elements = new FixedArray(length);
        result.Elements = elements;
        result.Length = JSValue.FromInt(length);
        JSValue[] fields = result.RawFields;
        fields[kRegExpResultIndexIndex] = JSValue.FromInt(index);
        fields[kRegExpResultInputIndex] = input;
        fields[kRegExpResultGroupsIndex] = JSValue.Undefined;
        fields[kRegExpResultRegExpInputIndex] = input;
        // If non-smi last_index then store an SmiZero instead.
        fields[kRegExpResultRegExpLastIndexIndex] = lastIndex.IsSmi ? lastIndex : JSValue.Zero;
        if (hasIndices) fields[kRegExpResultIndicesIndex] = JSValue.Undefined;
        return result;
    }

    /// <summary>
    /// ConstructNamedCaptureGroupsObject (runtime-regexp.cc, and the groups
    /// loop of ConstructNewResultFromMatchInfo): a null-prototype dictionary
    /// object mapping each group name to its capture (for duplicate names, the
    /// one that matched).
    /// </summary>
    internal static JSObject ConstructNamedCaptureGroupsObject(Isolate isolate, RegExpData data, ReadOnlySpan<JSValue> captures)
    {
        JSString[] names = data.CaptureNames!;
        int[] indices = data.CaptureIndices!;
        JSObject groups = isolate.Factory.NewSlowJSObjectWithNullProto();
        for (int i = 0; i < names.Length; i++)
        {
            JSValue capture = captures[indices[i]];
            NameDictionary properties = groups.PropertyDictionary;
            InternalIndex entry = properties.FindEntry(names[i]);
            if (entry.IsFound)
            {
                // Duplicate name: at most one of the groups can have matched.
                if (!capture.IsUndefined) properties.ValueAtPut(entry, capture);
                continue;
            }
            JSObject.SetNormalizedProperty(isolate, groups, names[i], capture, PropertyDetails.Empty());
        }
        return groups;
    }

    // ---- test ---------------------------------------------------------------------------------

    /// <summary>RegExpPrototypeTest (regexp-test.tq).</summary>
    public static JSValue RegExpPrototypeTest(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "RegExp.prototype.test";
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, methodName, args.Receiver);
        }
        JSString str = BuiltinsString.ToStringInline(isolate, args.AtOrUndefined(1));
        if (IsFastRegExpPermissive(isolate, receiver))
        {
            return JSValue.FromBoolean(RegExpPrototypeExecBodyWithoutResultFast(isolate, (JSRegExp)receiver, str) is not null);
        }
        JSValue matchIndices = RegExpExec(isolate, receiver, str);
        return JSValue.FromBoolean(!matchIndices.IsNull);
    }

    // ---- Flag getters (regexp.tq) --------------------------------------------------------

    /// <summary>FlagGetter.</summary>
    static JSValue FlagGetter(Isolate isolate, JSValue receiver, RegExpFlags flag, string? counter, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSRegExp regexp) return JSValue.FromBoolean((regexp.Flags & flag) != 0);
        if (!IsReceiverInitialRegExpPrototype(isolate, receiver))
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.RegExpNonRegExp, methodName);
        }
        if (counter is not null) isolate.CountUsage(counter);
        return JSValue.Undefined;
    }

    public static JSValue RegExpPrototypeGlobalGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.Global, null, "RegExp.prototype.global");

    public static JSValue RegExpPrototypeIgnoreCaseGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.IgnoreCase, null, "RegExp.prototype.ignoreCase");

    public static JSValue RegExpPrototypeMultilineGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.Multiline, null, "RegExp.prototype.multiline");

    public static JSValue RegExpPrototypeHasIndicesGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.HasIndices, null, "RegExp.prototype.hasIndices");

    public static JSValue RegExpPrototypeLinearGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.Linear, null, "RegExp.prototype.linear");

    public static JSValue RegExpPrototypeDotAllGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.DotAll, null, "RegExp.prototype.dotAll");

    public static JSValue RegExpPrototypeStickyGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.Sticky, "kRegExpPrototypeStickyGetter", "RegExp.prototype.sticky");

    public static JSValue RegExpPrototypeUnicodeGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.Unicode, "kRegExpPrototypeUnicodeGetter", "RegExp.prototype.unicode");

    public static JSValue RegExpPrototypeUnicodeSetsGetter(Isolate isolate, in BuiltinArguments args) =>
        FlagGetter(isolate, args.Receiver, RegExpFlags.UnicodeSets, null, "RegExp.prototype.unicodeSets");

    /// <summary>RegExpBuiltinsAssembler::FlagsGetter.</summary>
    internal static JSString FlagsGetter(Isolate isolate, JSValue regexp, bool isFastPath)
    {
        RegExpFlags flags;
        if (isFastPath && !isolate.Flags.force_slow_path)
        {
            // Refer to JSRegExp's flag property on the fast-path.
            flags = ((JSRegExp)regexp.Object).Flags;
        }
        else if (isFastPath)
        {
            // RegExpStringFromFlags.
            return JSRegExp.StringFromFlags(isolate, ((JSRegExp)regexp.Object).Flags);
        }
        else
        {
            // Fall back to GetProperty stub on the slow-path.
            flags = RegExpFlags.None;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.hasIndices_string)) flags |= RegExpFlags.HasIndices;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.global_string)) flags |= RegExpFlags.Global;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.ignoreCase_string)) flags |= RegExpFlags.IgnoreCase;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.multiline_string)) flags |= RegExpFlags.Multiline;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.dotAll_string)) flags |= RegExpFlags.DotAll;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.unicode_string)) flags |= RegExpFlags.Unicode;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.sticky_string)) flags |= RegExpFlags.Sticky;
            if (GetFlag(isolate, regexp, ReadOnlyRoots.unicodeSets_string)) flags |= RegExpFlags.UnicodeSets;
            if (isolate.Flags.enable_experimental_regexp_engine && GetFlag(isolate, regexp, ReadOnlyRoots.linear_string))
            {
                flags |= RegExpFlags.Linear;
            }
        }
        Span<char> buffer = stackalloc char[JSRegExp.kFlagCount];
        int length = JSRegExp.FlagsToString(flags, buffer);
        return length == 0 ? ReadOnlyRoots.empty_string : isolate.Factory.NewStringFromUtf16(buffer[..length]);
    }

    static bool GetFlag(Isolate isolate, JSValue regexp, Name name) =>
        ObjectOps.BooleanValue(ObjectOps.GetProperty(isolate, regexp, name));

    /// <summary>RegExpPrototypeFlagsGetter (regexp.tq).</summary>
    public static JSValue RegExpPrototypeFlagsGetter(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (!receiver.IsJSReceiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.RegExpNonObject, "RegExp.prototype.flags", receiver);
        }
        // The check is strict because the following code relies on individual flag
        // getters on the regexp prototype (e.g.: global, sticky, ...). We don't
        // bother to check these individually.
        return FlagsGetter(isolate, receiver, IsFastRegExpStrict(isolate, receiver));
    }

    /// <summary>RegExpPrototypeSourceGetter (regexp-source.tq).</summary>
    public static JSValue RegExpPrototypeSourceGetter(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (receiver.HeapObjectOrNull is JSRegExp regexp) return regexp.Source;
        if (!IsReceiverInitialRegExpPrototype(isolate, receiver))
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.RegExpNonRegExp, "RegExp.prototype.source");
        }
        return isolate.Factory.NewStringFromAsciiChecked("(?:)");
    }

    /// <summary>RegExpPrototypeToString (builtins-regexp.cc).</summary>
    public static JSValue RegExpPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver recv)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.toString",
                args.Receiver);
        }
        if (ReferenceEquals(recv, isolate.NativeContext.RegExpFunction.InstancePrototype))
        {
            isolate.CountUsage("kRegExpPrototypeToString");
        }
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCharacter('/');
        JSValue source = JSReceiver.GetProperty(isolate, recv, ReadOnlyRoots.source_string);
        builder.AppendString(ObjectOps.ToString(isolate, source));
        builder.AppendCharacter('/');
        JSValue flags = JSReceiver.GetProperty(isolate, recv, ReadOnlyRoots.flags_string);
        builder.AppendString(ObjectOps.ToString(isolate, flags));
        return builder.Finish();
    }

    // ---- Constructor and compile ----------------------------------------------------------

    /// <summary>RegExpBuiltinsAssembler::RegExpInitialize (RegExpInitialize, ECMA-262 22.2.3.3).</summary>
    static JSValue RegExpInitialize(Isolate isolate, JSRegExp regexp, JSValue maybePattern, JSValue maybeFlags)
    {
        // Normalize pattern.
        JSString pattern = maybePattern.IsUndefined ? ReadOnlyRoots.empty_string : BuiltinsString.ToStringInline(isolate, maybePattern);
        // Normalize flags.
        JSString flags = maybeFlags.IsUndefined ? ReadOnlyRoots.empty_string : BuiltinsString.ToStringInline(isolate, maybeFlags);
        // Initialize.
        return JSRegExp.Initialize(isolate, regexp, pattern, flags);
    }

    /// <summary>RegExpConstructor (builtins-regexp-gen.cc, RegExp ( pattern, flags )).</summary>
    public static JSValue RegExpConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSValue pattern = args.AtOrUndefined(1);
        JSValue flags = args.AtOrUndefined(2);
        JSValue newTarget = args.NewTarget;
        JSValue varFlags = flags;
        JSValue varPattern = pattern;
        JSFunction regexpFunction = isolate.NativeContext.RegExpFunction;

        bool patternIsRegExp = IsRegExp(isolate, pattern);

        if (newTarget.IsUndefined)
        {
            newTarget = regexpFunction;
            if (patternIsRegExp && flags.IsUndefined)
            {
                JSValue value = ObjectOps.GetProperty(isolate, pattern, ReadOnlyRoots.constructor_string);
                if (ReferenceEquals(value.HeapObjectOrNull, regexpFunction)) return pattern;
            }
        }

        if (pattern.HeapObjectOrNull is JSRegExp patternRegExp)
        {
            varPattern = patternRegExp.Data!.OriginalSource;
            if (flags.IsUndefined) varFlags = FlagsGetter(isolate, pattern, true);
        }
        else if (patternIsRegExp)
        {
            varPattern = ObjectOps.GetProperty(isolate, pattern, ReadOnlyRoots.source_string);
            if (flags.IsUndefined) varFlags = ObjectOps.GetProperty(isolate, pattern, ReadOnlyRoots.flags_string);
        }

        // Allocate.
        JSRegExp regexp;
        if (ReferenceEquals(newTarget.HeapObjectOrNull, regexpFunction))
        {
            regexp = (JSRegExp)isolate.Factory.NewJSObjectFromMap(regexpFunction.InitialMap);
        }
        else
        {
            // ConstructorBuiltinsAssembler::FastNewObject.
            regexp = (JSRegExp)JSObject.New(isolate, regexpFunction, (JSReceiver)newTarget.Object);
        }
        return RegExpInitialize(isolate, regexp, varPattern, varFlags);
    }

    /// <summary>RegExpPrototypeCompile (builtins-regexp-gen.cc, Annex B).</summary>
    public static JSValue RegExpPrototypeCompile(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kRegExpCompile");
        JSValue maybeReceiver = args.Receiver;
        JSValue maybePattern = args.AtOrUndefined(1);
        JSValue maybeFlags = args.AtOrUndefined(2);
        if (maybeReceiver.HeapObjectOrNull is not JSRegExp receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.compile",
                maybeReceiver);
        }
        JSValue varFlags = maybeFlags;
        JSValue varPattern = maybePattern;
        // Handle a JSRegExp pattern.
        if (maybePattern.HeapObjectOrNull is JSRegExp pattern)
        {
            // {maybe_flags} must be undefined in this case, otherwise throw.
            if (!maybeFlags.IsUndefined) isolate.ThrowTypeError(MessageTemplate.RegExpFlags);
            varFlags = FlagsGetter(isolate, pattern, true);
            varPattern = pattern.Data!.OriginalSource;
        }
        return RegExpInitialize(isolate, receiver, varPattern, varFlags);
    }

    // ---- Legacy static properties (builtins-regexp.cc) ------------------------------------------

    /// <summary>regexp::Utils::GenericCaptureGetter.</summary>
    internal static JSString GenericCaptureGetter(Isolate isolate, RegExpMatchInfo matchInfo, int capture, out bool ok)
    {
        int captureStartIndex = RegExpMatchInfo.CaptureStartIndex(capture);
        if (captureStartIndex >= matchInfo.NumberOfCaptureRegisters)
        {
            ok = false;
            return ReadOnlyRoots.empty_string;
        }
        int matchStart = matchInfo.Capture(captureStartIndex);
        int matchEnd = matchInfo.Capture(RegExpMatchInfo.CaptureEndIndex(capture));
        if (matchStart == -1 || matchEnd == -1)
        {
            ok = false;
            return ReadOnlyRoots.empty_string;
        }
        ok = true;
        return isolate.Factory.NewSubString(matchInfo.LastSubject, matchStart, matchEnd);
    }

    /// <summary>regexp::Utils::IsMatchedCapture.</summary>
    internal static bool IsMatchedCapture(RegExpMatchInfo matchInfo, int capture)
    {
        // Sentinel used as failure indicator in other functions.
        if (capture == -1) return false;
        int captureStartIndex = RegExpMatchInfo.CaptureStartIndex(capture);
        if (captureStartIndex >= matchInfo.NumberOfCaptureRegisters) return false;
        return matchInfo.Capture(captureStartIndex) != -1 &&
               matchInfo.Capture(RegExpMatchInfo.CaptureEndIndex(capture)) != -1;
    }

    static JSValue CaptureGetter(Isolate isolate, int i)
    {
        isolate.CountUsage("kRegExpStaticProperties");
        return GenericCaptureGetter(isolate, RegExpMatchInfo.Get(isolate), i, out _);
    }

    public static JSValue RegExpCapture1Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 1);
    public static JSValue RegExpCapture2Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 2);
    public static JSValue RegExpCapture3Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 3);
    public static JSValue RegExpCapture4Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 4);
    public static JSValue RegExpCapture5Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 5);
    public static JSValue RegExpCapture6Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 6);
    public static JSValue RegExpCapture7Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 7);
    public static JSValue RegExpCapture8Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 8);
    public static JSValue RegExpCapture9Getter(Isolate isolate, in BuiltinArguments args) => CaptureGetter(isolate, 9);

    public static JSValue RegExpInputGetter(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kRegExpStaticProperties");
        JSValue obj = RegExpMatchInfo.Get(isolate).LastInput;
        return obj.IsUndefined ? ReadOnlyRoots.empty_string : obj;
    }

    public static JSValue RegExpInputSetter(Isolate isolate, in BuiltinArguments args)
    {
        JSString str = ObjectOps.ToString(isolate, args.AtOrUndefined(1));
        RegExpMatchInfo.Get(isolate).LastInput = str;
        return JSValue.Undefined;
    }

    public static JSValue RegExpLastMatchGetter(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kRegExpStaticPropertiesWithLastMatch");
        return GenericCaptureGetter(isolate, RegExpMatchInfo.Get(isolate), 0, out _);
    }

    public static JSValue RegExpLastParenGetter(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kRegExpStaticProperties");
        RegExpMatchInfo matchInfo = RegExpMatchInfo.Get(isolate);
        int length = matchInfo.NumberOfCaptureRegisters;
        if (length <= 2) return ReadOnlyRoots.empty_string;  // No captures.
        int lastCapture = (length / 2) - 1;
        // We match the SpiderMonkey behavior: return the substring defined by the
        // last pair (after the first pair) of elements of the capture array even if
        // it is empty.
        return GenericCaptureGetter(isolate, matchInfo, lastCapture, out _);
    }

    public static JSValue RegExpLeftContextGetter(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kRegExpStaticPropertiesWithLastMatch");
        RegExpMatchInfo matchInfo = RegExpMatchInfo.Get(isolate);
        int startIndex = matchInfo.Capture(0);
        return isolate.Factory.NewSubString(matchInfo.LastSubject, 0, startIndex);
    }

    public static JSValue RegExpRightContextGetter(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kRegExpStaticPropertiesWithLastMatch");
        RegExpMatchInfo matchInfo = RegExpMatchInfo.Get(isolate);
        int startIndex = matchInfo.Capture(1);
        JSString lastSubject = matchInfo.LastSubject;
        return isolate.Factory.NewSubString(lastSubject, startIndex, lastSubject.Length);
    }

    // ---- RegExp.escape (builtins-regexp.cc) -----------------------------------------------------

    const byte kNoEscape = 0;
    const byte kEscapeToHex = byte.MaxValue;

    static byte GetAsciiEscape(int c) => c switch
    {
        // SyntaxCharacter and U+002F (SOLIDUS) are escaped as-is.
        '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/' => (byte)c,
        // ControlEscape :: one of f n r t v
        '\f' => (byte)'f',
        '\n' => (byte)'n',
        '\r' => (byte)'r',
        '\t' => (byte)'t',
        '\v' => (byte)'v',
        // One of ",-=<>#&!%:;@~'`", the code unit 0x0022 (QUOTATION MARK), and
        // ASCII whitespace are escaped to hex.
        ',' or '-' or '=' or '<' or '>' or '#' or '&' or '!' or '%' or ':' or ';' or '@' or '~' or '\'' or '`' or '"' or ' '
            => kEscapeToHex,
        _ => kNoEscape,
    };

    static readonly byte[] s_asciiEscapes = BuildAsciiEscapes();

    static byte[] BuildAsciiEscapes()
    {
        var table = new byte[128];
        for (int c = 0; c < 128; c++) table[c] = GetAsciiEscape(c);
        return table;
    }

    static void AppendHex(IncrementalStringBuilder builder, int value)
    {
        Span<char> buffer = stackalloc char[8];
        value.TryFormat(buffer, out int written, "x", System.Globalization.CultureInfo.InvariantCulture);
        builder.AppendChars(buffer[..written]);
    }

    /// <summary>RegExpEscape (builtins-regexp.cc, RegExpEscapeImpl).</summary>
    public static JSValue RegExpEscape(Isolate isolate, in BuiltinArguments args)
    {
        JSValue value = args.AtOrUndefined(1);
        isolate.CountUsage("kRegExpEscape");
        // 1. If S is not a String, throw a TypeError exception.
        if (value.HeapObjectOrNull is not JSString str)
        {
            return isolate.ThrowTypeError(MessageTemplate.ArgumentIsNonString, ReadOnlyRoots.input_string);
        }
        if (str.Length == 0) return ReadOnlyRoots.empty_string;
        ReadOnlySpan<char> source = str.FlatSpan();

        // 2. Let escaped be the empty String.
        var escapedBuilder = new IncrementalStringBuilder(isolate);
        int start;
        char firstC = source[0];
        if (V8Sharp.Base.Strings.CharPredicates.IsAlphaNumeric(firstC))
        {
            // a. If escaped is the empty String and c is matched by either
            //    DecimalDigit or AsciiLetter, then escape it as \xHH.
            start = 1;
            escapedBuilder.AppendCStringLiteral("\\x");
            AppendHex(escapedBuilder, firstC);
        }
        else
        {
            start = 0;
        }

        for (int i = start; i < source.Length; i++)
        {
            char cu = source[i];
            int cp = cu;
            byte cmd = kNoEscape;
            if (cu < 128)
            {
                cmd = s_asciiEscapes[cu];
            }
            else
            {
                if (char.IsHighSurrogate(cu))
                {
                    if (i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                    {
                        // Surrogate pair. Combine them.
                        cp = char.ConvertToUtf32(cu, source[i + 1]);
                        i++;
                    }
                    else
                    {
                        // Lone lead surrogate.
                        cmd = kEscapeToHex;
                    }
                }
                else if (char.IsLowSurrogate(cu))
                {
                    // Lone trailing surrogate.
                    cmd = kEscapeToHex;
                }
                // ASCII whitespace and line terminators are hardcoded in the
                // kAsciiEscapes table.
                if (V8Sharp.Base.Strings.CharPredicates.IsWhiteSpaceOrLineTerminator(cp)) cmd = kEscapeToHex;
            }

            if (cmd == kNoEscape)
            {
                // Code point does not need to be escaped.
                if (cp == cu)
                {
                    escapedBuilder.AppendCharacter(cu);
                }
                else
                {
                    escapedBuilder.AppendCharacter(cu);
                    escapedBuilder.AppendCharacter(source[i]);
                }
            }
            else if (cmd == kEscapeToHex)
            {
                // An escape to hex. Output \x or \u depending on how many code units.
                escapedBuilder.AppendCStringLiteral(cp <= 0xFF ? "\\x" : "\\u");
                AppendHex(escapedBuilder, cp);
            }
            else
            {
                // A manual, non-hex escape. See table in kAsciiEscapes.
                escapedBuilder.AppendCharacter('\\');
                escapedBuilder.AppendCharacter((char)cmd);
            }
        }
        return escapedBuilder.Finish();
    }
}
