// Port of src/runtime/runtime-literals.cc (CreateObjectLiteral,
// CreateArrayLiteral, CreateRegExpLiteral, the boilerplate / AllocationSite
// scheme with DeepWalk and DeepCopy, SetPrototypeProperties) and of the
// literal builtins the bytecode handlers call (CreateEmptyArrayLiteral,
// CreateEmptyObjectLiteral, CreateShallow*Literal via the same runtime
// path), plus TemplateObjectDescription::GetTemplateObject
// (src/objects/template-objects.cc) behind GetTemplateObject.
//
// V8's AllocationMementos are the JSArray.AllocationMementoSite field (see
// deviations.md).
using V8Sharp.Interpreter;
using V8Sharp.RegExp;

namespace V8Sharp.Runtime;

public static class RuntimeLiterals
{
    // AggregateLiteral::Flags and ObjectLiteral::Flags (src/ast/ast.h).
    const int kIsShallow = 1;
    const int kDisableMementos = 1 << 1;
    const int kNeedsInitialAllocationSite = 1 << 2;
    const int kFastElements = 1 << 3;
    const int kHasNullPrototype = 1 << 4;

    static bool IsUninitializedLiteralSite(in JSValue literalSite) => literalSite.IsNumber && literalSite._num == 0;

    static bool HasBoilerplate(in JSValue literalSite) => !literalSite.IsNumber;

    static void PreInitializeLiteralSite(FeedbackVector vector, int slot) => vector.Slots[slot] = JSValue.FromInt(1);

    // ---- Object and array literals -------------------------------------------------------

    /// <summary>CreateObjectLiteral bytecode: Runtime_CreateObjectLiteral (CreateLiteral&lt;ObjectLiteralHelper&gt;).</summary>
    public static JSObject CreateObjectLiteral(Isolate isolate, FeedbackVector? vector, int slot,
        ObjectBoilerplateDescription description, int flags) =>
        CreateLiteral(isolate, vector, slot, description, flags);

    /// <summary>CreateArrayLiteral bytecode: Runtime_CreateArrayLiteral (CreateLiteral&lt;ArrayLiteralHelper&gt;).</summary>
    public static JSObject CreateArrayLiteral(Isolate isolate, FeedbackVector? vector, int slot,
        ArrayBoilerplateDescription description, int flags) =>
        CreateLiteral(isolate, vector, slot, description, flags);

    static JSObject CreateFromDescription(Isolate isolate, HeapObject description, int flags) =>
        description is ObjectBoilerplateDescription objectDescription
            ? CreateObjectLiteral(isolate, objectDescription, flags)
            : CreateArrayLiteral(isolate, (ArrayBoilerplateDescription)description);

    /// <summary>CreateLiteral (runtime-literals.cc).</summary>
    static JSObject CreateLiteral(Isolate isolate, FeedbackVector? vector, int slot, HeapObject description, int flags)
    {
        if (vector is null)
        {
            // CreateLiteralWithoutAllocationSite.
            return CreateFromDescription(isolate, description, flags);
        }
        JSValue literalSite = vector.Slots[slot];
        AllocationSite site;
        JSObject boilerplate;

        if (HasBoilerplate(literalSite))
        {
            site = literalSite.As<AllocationSite>();
            boilerplate = site.Boilerplate!;
        }
        else
        {
            // Eagerly create AllocationSites for literals that contain an Array.
            bool needsInitialAllocationSite = (flags & kNeedsInitialAllocationSite) != 0;
            if (!needsInitialAllocationSite && IsUninitializedLiteralSite(literalSite))
            {
                PreInitializeLiteralSite(vector, slot);
                return CreateFromDescription(isolate, description, flags);
            }
            boilerplate = CreateFromDescription(isolate, description, flags);

            // Install AllocationSite objects.
            site = new AllocationSite();
            DeepWalk(isolate, boilerplate, site);
            site.Boilerplate = boilerplate;
            if (boilerplate is JSArray array) site.ElementsKind = array.GetElementsKind();
            vector.Slots[slot] = site;
        }

        // Copy the existing boilerplate.
        bool enableMementos = (flags & kDisableMementos) == 0;
        var usageContext = new AllocationSiteUsageContext(site, enableMementos);
        return DeepCopy(isolate, boilerplate, ref usageContext);
    }

    /// <summary>
    /// DeepWalk with the AllocationSiteCreationContext: creates the nested
    /// AllocationSites of the arrays inside the boilerplate.
    /// </summary>
    static void DeepWalk(Isolate isolate, JSObject obj, AllocationSite current)
    {
        isolate.StackGuard.StackCheck(isolate);
        if (obj.Map.IsDeprecated) JSObject.MigrateInstance(isolate, obj);

        if (obj is not JSArray)
        {
            if (obj.HasFastProperties)
            {
                Map map = obj.Map;
                DescriptorArray descriptors = map.InstanceDescriptors;
                int count = map.NumberOfOwnDescriptors;
                for (int i = 0; i < count; i++)
                {
                    PropertyDetails details = descriptors.GetDetails(new InternalIndex(i));
                    if (details.Location != PropertyLocation.Field) continue;
                    FieldIndex index = FieldIndex.ForDetails(map, details);
                    if (obj.RawFastPropertyAt(index).HeapObjectOrNull is JSObject value)
                    {
                        current = VisitForSites(isolate, value, current);
                    }
                }
            }
            else
            {
                NameDictionary dict = obj.PropertyDictionary;
                for (int k = 0; k < dict.Capacity; k++)
                {
                    if (!dict.IsKey(k)) continue;
                    var i = new InternalIndex(k);
                    if (dict.ValueAt(i).HeapObjectOrNull is JSObject value) current = VisitForSites(isolate, value, current);
                }
            }
            if (obj.Elements.Length == 0) return;
        }

        if (obj.Elements is FixedArray elements && !elements.IsCowArray)
        {
            for (int i = 0; i < elements.Length; i++)
            {
                if (elements._data[i].HeapObjectOrNull is JSObject value) current = VisitForSites(isolate, value, current);
            }
        }
        else if (obj.Elements is NumberDictionary elementDictionary)
        {
            for (int k = 0; k < elementDictionary.Capacity; k++)
            {
                if (!elementDictionary.IsKey(k)) continue;
                var i = new InternalIndex(k);
                if (elementDictionary.ValueAt(i).HeapObjectOrNull is JSObject value) current = VisitForSites(isolate, value, current);
            }
        }
    }

    static AllocationSite VisitForSites(Isolate isolate, JSObject value, AllocationSite current)
    {
        // Dont create allocation sites for nested object literals
        if (value is not JSArray)
        {
            DeepWalk(isolate, value, current);
            return current;
        }
        var nested = new AllocationSite();
        current.NestedSite = nested;
        DeepWalk(isolate, value, nested);
        nested.Boilerplate = value;
        nested.ElementsKind = value.GetElementsKind();
        return nested;
    }

    /// <summary>
    /// AllocationSiteUsageContext: walks the nested sites in the order
    /// DeepWalk created them, and decides which copies get a memento.
    /// </summary>
    struct AllocationSiteUsageContext(AllocationSite topSite, bool activated)
    {
        AllocationSite? _current;

        public AllocationSite EnterNewScope()
        {
            // Advance to the next nested site (the first scope is the top site).
            _current = _current is null ? topSite : _current.NestedSite!;
            return _current;
        }

        public readonly bool ShouldCreateMemento(JSObject obj) =>
            activated && AllocationSite.CanTrack(obj.Map.InstanceType) && AllocationSite.ShouldTrack(obj.GetElementsKind());
    }

    /// <summary>DeepCopy with the AllocationSiteUsageContext (JSObjectWalkVisitor with kCopying).</summary>
    static JSObject DeepCopy(Isolate isolate, JSObject obj, ref AllocationSiteUsageContext siteContext) =>
        StructureWalk(isolate, obj, ref siteContext, siteContext.EnterNewScope());

    /// <summary>JSObjectWalkVisitor::VisitElementOrProperty: nested object literals get no site of their own.</summary>
    static JSObject VisitElementOrProperty(Isolate isolate, JSObject value, ref AllocationSiteUsageContext siteContext) =>
        value is JSArray
            ? StructureWalk(isolate, value, ref siteContext, siteContext.EnterNewScope())
            : StructureWalk(isolate, value, ref siteContext, null);

    /// <summary>JSObjectWalkVisitor::StructureWalk (kCopying).</summary>
    static JSObject StructureWalk(Isolate isolate, JSObject obj, ref AllocationSiteUsageContext siteContext, AllocationSite? site)
    {
        isolate.StackGuard.StackCheck(isolate);
        if (obj.Map.IsDeprecated) JSObject.MigrateInstance(isolate, obj);

        JSObject copy = isolate.Factory.CopyJSObject(obj);
        // CopyJSObjectWithAllocationSite: the memento of the copy.
        if (copy is JSArray copyArray)
        {
            copyArray.AllocationMementoSite = site is not null && siteContext.ShouldCreateMemento(obj) ? site : null;
        }

        // Deep copy own properties. Arrays only have 1 property "length".
        if (copy is not JSArray)
        {
            if (copy.HasFastProperties)
            {
                Map map = copy.Map;
                DescriptorArray descriptors = map.InstanceDescriptors;
                int count = map.NumberOfOwnDescriptors;
                for (int i = 0; i < count; i++)
                {
                    PropertyDetails details = descriptors.GetDetails(new InternalIndex(i));
                    if (details.Location != PropertyLocation.Field) continue;
                    FieldIndex index = FieldIndex.ForDetails(map, details);
                    if (copy.RawFastPropertyAt(index).HeapObjectOrNull is JSObject value)
                    {
                        copy.FastPropertyAtPut(index, VisitElementOrProperty(isolate, value, ref siteContext));
                    }
                }
            }
            else
            {
                NameDictionary dict = copy.PropertyDictionary;
                for (int k = 0; k < dict.Capacity; k++)
                {
                    if (!dict.IsKey(k)) continue;
                    var i = new InternalIndex(k);
                    if (dict.ValueAt(i).HeapObjectOrNull is JSObject value)
                    {
                        dict.ValueAtPut(i, VisitElementOrProperty(isolate, value, ref siteContext));
                    }
                }
            }

            // Assume non-arrays don't end up having elements.
            if (copy.Elements.Length == 0) return copy;
        }

        // Deep copy own elements.
        if (copy.Elements is FixedArray elements)
        {
            if (!elements.IsCowArray)
            {
                for (int i = 0; i < elements.Length; i++)
                {
                    if (elements._data[i].HeapObjectOrNull is JSObject value)
                    {
                        elements._data[i] = VisitElementOrProperty(isolate, value, ref siteContext);
                    }
                }
            }
        }
        else if (copy.Elements is NumberDictionary elementDictionary)
        {
            for (int k = 0; k < elementDictionary.Capacity; k++)
            {
                if (!elementDictionary.IsKey(k)) continue;
                var i = new InternalIndex(k);
                if (elementDictionary.ValueAt(i).HeapObjectOrNull is JSObject value)
                {
                    elementDictionary.ValueAtPut(i, VisitElementOrProperty(isolate, value, ref siteContext));
                }
            }
        }
        return copy;
    }

    /// <summary>A nested boilerplate value materialized (CreateArrayLiteral / CreateObjectLiteral), else the value.</summary>
    static JSValue MaterializeNested(Isolate isolate, JSValue value) => value.HeapObjectOrNull switch
    {
        ArrayBoilerplateDescription arrayDescription => CreateArrayLiteral(isolate, arrayDescription),
        ObjectBoilerplateDescription objectDescription => CreateObjectLiteral(isolate, objectDescription, objectDescription.Flags),
        _ => value,
    };

    /// <summary>CreateObjectLiteralWithNullProto.</summary>
    static JSObject CreateObjectLiteralWithNullProto(Isolate isolate, ObjectBoilerplateDescription description, int flags)
    {
        NativeContext nativeContext = isolate.NativeContext;
        bool useFastElements = (flags & kFastElements) != 0;
        int numberOfProperties = description.BackingStoreSize;

        // Ignoring number_of_properties for force dictionary map with
        // __proto__:null.
        Map map = nativeContext.SlowObjectWithNullPrototypeMap;
        JSObject boilerplate = isolate.Factory.NewSlowJSObjectFromMap(map,
            Math.Max(numberOfProperties, NameDictionary.kInitialCapacity));

        // Normalize the elements of the boilerplate to save space if needed.
        if (!useFastElements) JSObject.NormalizeElements(isolate, boilerplate);

        int length = description.BoilerplatePropertiesCount;
        for (int index = 0; index < length; index++)
        {
            JSValue key = description.Name(index);
            JSValue value = MaterializeNested(isolate, description.Value(index));
            if (ObjectOps.ToArrayIndex(key, out uint elementIndex))
            {
                // Array index (uint32).
                JSObject.SetOwnElementIgnoreAttributes(isolate, boilerplate, elementIndex, value, PropertyAttributes.NONE);
            }
            else
            {
                JSObject.SetOwnPropertyIgnoreAttributes(isolate, boilerplate, key.As<Name>(), value, PropertyAttributes.NONE);
            }
        }
        return boilerplate;
    }

    /// <summary>
    /// CreateObjectLiteral (runtime-literals.cc). V8 builds the object with
    /// JSDataObjectBuilder; V8Sharp defines the properties in order on an
    /// object literal map from the cache, which yields the same map chain.
    /// </summary>
    public static JSObject CreateObjectLiteral(Isolate isolate, ObjectBoilerplateDescription description, int flags)
    {
        NativeContext nativeContext = isolate.NativeContext;
        bool hasNullPrototype = (flags & kHasNullPrototype) != 0;
        if (hasNullPrototype) return CreateObjectLiteralWithNullProto(isolate, description, flags);

        bool useFastElements = (flags & kFastElements) != 0;
        int numberOfProperties = description.BackingStoreSize;

        Map map = isolate.Factory.ObjectLiteralMapFromCache(nativeContext, numberOfProperties);
        JSObject boilerplate = map.IsDictionaryMap
            ? isolate.Factory.NewSlowJSObjectFromMap(map, Math.Max(numberOfProperties, NameDictionary.kInitialCapacity))
            : isolate.Factory.NewJSObjectFromMap(map);

        // Normalize the elements of the boilerplate to save space if needed.
        if (!useFastElements) JSObject.NormalizeElements(isolate, boilerplate);

        int length = description.BoilerplatePropertiesCount;
        for (int index = 0; index < length; index++)
        {
            JSValue key = description.Name(index);
            JSValue value = MaterializeNested(isolate, description.Value(index));
            if (ObjectOps.ToArrayIndex(key, out uint elementIndex))
            {
                // Array index (uint32).
                if (ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized)) value = JSValue.Zero;
                JSObject.SetOwnElementIgnoreAttributes(isolate, boilerplate, elementIndex, value, PropertyAttributes.NONE);
            }
            else
            {
                JSObject.SetOwnPropertyIgnoreAttributes(isolate, boilerplate, key.As<Name>(), value, PropertyAttributes.NONE);
            }
        }

        if (map.IsDictionaryMap)
        {
            // TODO(cbruni): avoid making the boilerplate fast again, the clone stub
            // supports dict-mode objects directly.
            JSObject.MigrateSlowToFast(isolate, boilerplate, boilerplate.Map.UnusedPropertyFields(), "FastLiteral");
        }
        return boilerplate;
    }

    /// <summary>CreateArrayLiteral (runtime-literals.cc).</summary>
    public static JSArray CreateArrayLiteral(Isolate isolate, ArrayBoilerplateDescription description)
    {
        ElementsKind constantElementsKind = description.ElementsKind;
        FixedArrayBase constantElementsValues = description.ConstantElements;

        // Create the JSArray.
        FixedArrayBase copiedElementsValues;
        if (ElementsKinds.IsDoubleElementsKind(constantElementsKind))
        {
            var source = (FixedDoubleArray)constantElementsValues;
            var copy = new FixedDoubleArray(source.Length);
            source.Data.AsSpan().CopyTo(copy.Data);
            copiedElementsValues = copy;
        }
        else if (constantElementsValues.IsCowArray)
        {
            copiedElementsValues = constantElementsValues;
        }
        else
        {
            var fixedArrayValues = (FixedArray)constantElementsValues;
            var fixedArrayValuesCopy = new FixedArray((JSValue[])fixedArrayValues.Data.Clone());
            copiedElementsValues = fixedArrayValuesCopy;
            JSValue[] data = fixedArrayValuesCopy.Data;
            for (int i = 0; i < data.Length; i++)
            {
                HeapObject? valueHeapObject = data[i].HeapObjectOrNull;
                if (valueHeapObject is ArrayBoilerplateDescription arrayDescription)
                {
                    data[i] = CreateArrayLiteral(isolate, arrayDescription);
                }
                else if (valueHeapObject is ObjectBoilerplateDescription objectDescription)
                {
                    data[i] = CreateObjectLiteral(isolate, objectDescription, objectDescription.Flags);
                }
            }
        }
        return isolate.Factory.NewJSArrayWithElements(copiedElementsValues, constantElementsKind, copiedElementsValues.Length);
    }

    /// <summary>CreateEmptyArrayLiteral: the builtin with an AllocationSite in the literal slot.</summary>
    public static JSArray CreateEmptyArrayLiteral(Isolate isolate, FeedbackVector? vector, int slot)
    {
        ElementsKind kind = ElementsKind.PACKED_SMI_ELEMENTS;
        AllocationSite? site = null;
        if (vector is not null)
        {
            // Array literals always have a valid AllocationSite to properly track
            // elements transitions.
            site = vector.Slots[slot].HeapObjectOrNull as AllocationSite;
            if (site is null)
            {
                site = new AllocationSite { ElementsKind = kind };
                vector.Slots[slot] = site;
            }
            kind = site.GetElementsKind();
        }
        JSArray array = isolate.Factory.NewJSArray(kind, 0, 0);
        // AllocateJSArray with the site: the array gets a memento.
        if (vector is not null && AllocationSite.ShouldTrack(kind)) array.AllocationMementoSite = vector.Slots[slot].As<AllocationSite>();
        return array;
    }

    /// <summary>CreateEmptyObjectLiteral: an object with the Object function's initial map.</summary>
    public static JSObject CreateEmptyObjectLiteral(Isolate isolate, NativeContext nativeContext) =>
        isolate.Factory.NewJSObjectFromMap(nativeContext.ObjectFunction.InitialMap);

    // ---- RegExp literals ----------------------------------------------------------------------

    /// <summary>
    /// CreateRegExpLiteral bytecode: the CSA fast path of
    /// ConstructorBuiltinsAssembler::CreateRegExpLiteral (copy the boilerplate
    /// when the slot has one) and Runtime_CreateRegExpLiteral (a fresh regexp;
    /// literal sites go Uninitialized, Preinitialized, then Initialized with a
    /// RegExpBoilerplateDescription). The flags operand is JSRegExp::Flags,
    /// which has RegExpFlags' bit layout.
    /// </summary>
    public static JSValue CreateRegExpLiteral(Isolate isolate, FeedbackVector? vector, int slot, JSString pattern, int flags)
    {
        if (vector is null) return JSRegExp.New(isolate, pattern, (RegExpFlags)flags);
        JSValue literalSite = vector.Slots[slot];
        if (literalSite.HeapObjectOrNull is RegExpBoilerplateDescription boilerplate)
        {
            return JSRegExp.CreateFromBoilerplate(isolate, (RegExpData)boilerplate.Data, (RegExpFlags)boilerplate.Flags);
        }

        JSRegExp regexp = JSRegExp.New(isolate, pattern, (RegExpFlags)flags);
        if (IsUninitializedLiteralSite(literalSite))
        {
            PreInitializeLiteralSite(vector, slot);
            return regexp;
        }
        vector.Slots[slot] = new RegExpBoilerplateDescription(regexp.Data!, pattern, (int)regexp.Flags);
        return regexp;
    }

    // ---- Template objects ------------------------------------------------------------------------

    /// <summary>The native context's template_weakmap: template objects by (script, function literal id, slot).</summary>
    sealed class TemplateObjectCache() : HeapObject(InstanceType.FixedArrayType)
    {
        public readonly Dictionary<(Script, int, int), JSArray> Entries = [];
    }

    /// <summary>
    /// GetTemplateObject: the bytecode handler (the cached object in the
    /// feedback slot) and TemplateObjectDescription::GetTemplateObject.
    /// </summary>
    public static JSValue GetTemplateObject(Isolate isolate, SharedFunctionInfo sharedInfo, TemplateObjectDescription description,
        FeedbackVector? vector, int slot)
    {
        if (vector is not null && vector.Slots[slot].HeapObjectOrNull is JSArray cached) return cached;

        JSArray templateObject = GetTemplateObject(isolate, isolate.NativeContext, description, sharedInfo, slot);
        if (vector is not null) vector.Slots[slot] = templateObject;
        return templateObject;
    }

    /// <summary>TemplateObjectDescription::GetTemplateObject.</summary>
    public static JSArray GetTemplateObject(Isolate isolate, NativeContext nativeContext, TemplateObjectDescription description,
        SharedFunctionInfo sharedInfo, int slotId)
    {
        int functionLiteralId = sharedInfo.FunctionLiteralId;
        Script script = sharedInfo.Script!;

        // Check the template weakmap to see if the template object already exists.
        if (nativeContext.TemplateWeakMap.HeapObjectOrNull is not TemplateObjectCache cache)
        {
            cache = new TemplateObjectCache();
            nativeContext.TemplateWeakMap = cache;
        }
        if (cache.Entries.TryGetValue((script, functionLiteralId, slotId), out JSArray? existing)) return existing;

        // Create the raw object from the {raw_strings}.
        JSArray templateObject = NewJSArrayForTemplateLiteralArray(isolate, description.CookedStrings, description.RawStrings);
        cache.Entries.Add((script, functionLiteralId, slotId), templateObject);
        return templateObject;
    }

    /// <summary>Factory::NewJSArrayForTemplateLiteralArray: frozen cooked array with a frozen `raw` array.</summary>
    static JSArray NewJSArrayForTemplateLiteralArray(Isolate isolate, FixedArray cookedStrings, FixedArray rawStrings)
    {
        JSArray rawObject = isolate.Factory.NewJSArrayWithElements(
            new FixedArray((JSValue[])rawStrings.Data.Clone()), ElementsKind.PACKED_ELEMENTS, rawStrings.Length);
        JSReceiver.SetIntegrityLevel(isolate, rawObject, JSReceiver.IntegrityLevel.FROZEN, ShouldThrow.ThrowOnError);

        JSArray templateObject = isolate.Factory.NewJSArrayWithElements(
            new FixedArray((JSValue[])cookedStrings.Data.Clone()), ElementsKind.PACKED_ELEMENTS, cookedStrings.Length);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, templateObject, ReadOnlyRoots.raw_string, rawObject,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY);
        JSReceiver.SetIntegrityLevel(isolate, templateObject, JSReceiver.IntegrityLevel.FROZEN, ShouldThrow.ThrowOnError);
        return templateObject;
    }

    // ---- SetPrototypeProperties ------------------------------------------------------------------

    /// <summary>SetPrototypePropertiesSlow (runtime-literals.cc).</summary>
    static JSValue SetPrototypePropertiesSlow(Isolate isolate, Context context, JSValue obj,
        ObjectBoilerplateDescription description, ClosureFeedbackCellArray feedbackCellArray, ref int currentSlot,
        int startIndex = 0)
    {
        JSValue result = JSValue.Undefined;
        int length = description.BoilerplatePropertiesCount;
        for (int index = startIndex; index < length; index++)
        {
            JSValue proto = RuntimeObject.GetObjectProperty(isolate, obj, ReadOnlyRoots.prototype_string, obj, out _);
            JSValue key = description.Name(index);
            JSValue value = description.Value(index);
            if (value.HeapObjectOrNull is SharedFunctionInfo shared)
            {
                value = RuntimeClosures.NewClosure(isolate, shared, context, feedbackCellArray.Get(currentSlot++));
            }
            RuntimeObject.SetObjectProperty(isolate, proto, key, value, StoreOrigin.Named);
            result = value;
        }
        return result;
    }

    static bool IsDefaultFunctionPrototype(Isolate isolate, JSObject jsProto)
    {
        // Object function prototype's map.
        Map protoMap = jsProto.Map;

        // Check that given function.prototype object has a default initial state:
        // it's extensible.
        if (!protoMap.IsExtensible) return false;

        // it's in dictionary mode.
        if (!protoMap.IsDictionaryMap) return false;

        // it has exactly one "constructor" property installed.
        if (jsProto.PropertyDictionary.NumberOfElements != 1) return false;
        if (jsProto.PropertyDictionary.FindEntry(ReadOnlyRoots.constructor_string).IsNotFound) return false;

        // its prototype is the original and unmodified Object.prototype object.
        JSReceiver? prototype = protoMap.Prototype;
        return prototype is not null && ReferenceEquals(prototype.Map, isolate.NativeContext.ObjectFunctionPrototypeMap);
    }

    /// <summary>
    /// SetPrototypeProperties bytecode: Runtime_SetPrototypeProperties with
    /// --no-proto-assign-seq-lazy-func-opt (V8's default): every closure is
    /// instantiated eagerly.
    /// </summary>
    public static JSValue SetPrototypeProperties(Isolate isolate, Context context, JSValue obj,
        ObjectBoilerplateDescription description, ClosureFeedbackCellArray feedbackCellArray, int currentSlot)
    {
        // Proxy and any non-function not welcome
        if (obj.HeapObjectOrNull is not JSFunction accFun || !accFun.HasPrototypeSlot)
        {
            return SetPrototypePropertiesSlow(isolate, context, obj, description, feedbackCellArray, ref currentSlot);
        }

        JSValue prototype = JSFunction.GetFunctionPrototype(isolate, accFun);
        if (prototype.HeapObjectOrNull is not JSObject jsProto || Map.IsSpecialReceiverMap(jsProto.Map) ||
            !JSObject.IsExtensible(isolate, jsProto))
        {
            return SetPrototypePropertiesSlow(isolate, context, obj, description, feedbackCellArray, ref currentSlot);
        }

        bool isDefaultFuncPrototype = IsDefaultFunctionPrototype(isolate, jsProto);

        // It should now be safe to perform a fast merge
        JSValue result = JSValue.Undefined;
        int length = description.BoilerplatePropertiesCount;
        if (isDefaultFuncPrototype)
        {
            for (int index = 0; index < length; index++)
            {
                var name = description.Name(index).As<Name>();
                JSValue value = description.Value(index);
                if (value.HeapObjectOrNull is SharedFunctionInfo shared)
                {
                    value = RuntimeClosures.NewClosure(isolate, shared, context, feedbackCellArray.Get(currentSlot++));
                }
                var it = new LookupIterator(isolate, jsProto, name, LookupIterator.Configuration.OWN);
                ObjectOps.TransitionAndWriteDataProperty(ref it, value, PropertyAttributes.NONE, ShouldThrow.DontThrow,
                    StoreOrigin.Named);
                result = value;
            }
        }
        else
        {
            // Make sure None of the keys we are writing to are setters/getters
            for (int index = 0; index < length; index++)
            {
                JSValue key = description.Name(index);
                JSValue value = description.Value(index);
                var lookupKey = new PropertyKey(isolate, key);
                var it = new LookupIterator(isolate, jsProto, lookupKey, LookupIterator.Configuration.PROTOTYPE_CHAIN);

                LookupIterator.StateKind itState = it.State;
                if (itState != LookupIterator.StateKind.NOT_FOUND &&
                    (itState != LookupIterator.StateKind.DATA || it.IsReadOnly))
                {
                    return SetPrototypePropertiesSlow(isolate, context, obj, description, feedbackCellArray, ref currentSlot,
                        index);
                }

                if (value.HeapObjectOrNull is SharedFunctionInfo shared)
                {
                    value = RuntimeClosures.NewClosure(isolate, shared, context, feedbackCellArray.Get(currentSlot++));
                }
                if (itState == LookupIterator.StateKind.DATA && it.HolderIsReceiverOrHiddenPrototype())
                {
                    it.UpdateProtector();
                    ObjectOps.SetDataProperty(ref it, value);
                }
                else
                {
                    ObjectOps.TransitionAndWriteDataProperty(ref it, value, PropertyAttributes.NONE, ShouldThrow.DontThrow,
                        StoreOrigin.Named);
                }
                result = value;
            }
        }
        return result;
    }
}
