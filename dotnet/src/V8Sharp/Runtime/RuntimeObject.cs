// Port of src/runtime/runtime-object.cc: Runtime::GetObjectProperty,
// HasProperty, SetObjectProperty, DefineObjectOwnProperty,
// DeleteObjectProperty and the runtime functions the interpreter and the
// ICs call (DeleteProperty, DefineKeyedOwnPropertyInLiteral,
// SetFunctionName, the private member helpers ...).
using V8Sharp.Interpreter;

namespace V8Sharp.Runtime;

public static class RuntimeObject
{
    /// <summary>Runtime::GetObjectProperty.</summary>
    public static JSValue GetObjectProperty(Isolate isolate, JSValue lookupStartObject, JSValue key, JSValue receiver,
        out bool isFound)
    {
        isFound = false;
        if (lookupStartObject.IsNullOrUndefined)
        {
            return ErrorUtils.ThrowLoadFromNullOrUndefined(isolate, lookupStartObject, key);
        }

        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);
        var it = new LookupIterator(isolate, receiver, lookupKey, lookupStartObject);

        JSValue result = ObjectOps.GetProperty(ref it);
        isFound = it.State != LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND &&
                  it.State != LookupIterator.StateKind.NOT_FOUND;
        return result;
    }

    /// <summary>Runtime::GetObjectProperty with a name key.</summary>
    public static JSValue GetObjectProperty(Isolate isolate, JSValue lookupStartObject, Name name, JSValue receiver,
        out bool isFound) => GetObjectProperty(isolate, lookupStartObject, (JSValue)name, receiver, out isFound);

    /// <summary>Runtime::HasProperty.</summary>
    public static JSValue HasProperty(Isolate isolate, JSValue obj, JSValue key)
    {
        // Check that {object} is actually a receiver.
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidInOperatorUse, key, obj);
        }

        // Convert the {key} to a name.
        Name name = ObjectOps.ToName(isolate, key);

        // Lookup the {name} on {receiver}.
        return JSValue.FromBoolean(JSReceiver.HasProperty(isolate, receiver, name));
    }

    /// <summary>Runtime::SetObjectProperty.</summary>
    public static JSValue SetObjectProperty(Isolate isolate, JSValue lookupStartObject, JSValue key, JSValue value,
        StoreOrigin storeOrigin, ShouldThrow? shouldThrow = null) =>
        SetObjectProperty(isolate, lookupStartObject, key, value, lookupStartObject, storeOrigin, shouldThrow);

    /// <summary>Runtime::SetObjectProperty with an explicit receiver.</summary>
    public static JSValue SetObjectProperty(Isolate isolate, JSValue lookupStartObject, JSValue key, JSValue value,
        JSValue receiver, StoreOrigin storeOrigin, ShouldThrow? shouldThrow = null)
    {
        if (lookupStartObject.IsNullOrUndefined) return ThrowNonObjectPropertyStore(isolate, lookupStartObject, key);

        // Check if the given key is an array index.
        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);
        var it = new LookupIterator(isolate, receiver, lookupKey, lookupStartObject);
        if (key.HeapObjectOrNull is Symbol symbol && symbol.IsAnyPrivateName)
        {
            if (!JSReceiver.CheckPrivateNameStore(ref it, false)) return JSValue.Undefined;
        }

        ObjectOps.SetProperty(ref it, value, storeOrigin, shouldThrow);
        return value;
    }

    static JSValue ThrowNonObjectPropertyStore(Isolate isolate, JSValue obj, JSValue key)
    {
        JSString? propertyName = ObjectOps.NoSideEffectsToMaybeString(isolate, key);
        if (propertyName is not null)
        {
            return isolate.ThrowTypeError(MessageTemplate.NonObjectPropertyStoreWithProperty, obj, propertyName);
        }
        return isolate.ThrowTypeError(MessageTemplate.NonObjectPropertyStore, obj);
    }

    /// <summary>Runtime::DefineObjectOwnProperty.</summary>
    public static JSValue DefineObjectOwnProperty(Isolate isolate, JSValue obj, JSValue key, JSValue value, StoreOrigin storeOrigin)
    {
        if (obj.IsNullOrUndefined) return ThrowNonObjectPropertyStore(isolate, obj, key);

        // Check if the given key is an array index.
        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);

        if (key.HeapObjectOrNull is Symbol symbol && symbol.IsAnyPrivateName)
        {
            var it = new LookupIterator(isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
            if (!JSReceiver.CheckPrivateNameStore(ref it, true)) return JSValue.Undefined;
            JSReceiver.AddPrivateField(ref it, value, null);
        }
        else
        {
            JSReceiver.CreateDataProperty(isolate, obj, lookupKey, value, null);
        }
        return value;
    }

    /// <summary>Runtime::DeleteObjectProperty.</summary>
    public static bool DeleteObjectProperty(Isolate isolate, JSReceiver receiver, JSValue key, LanguageMode languageMode)
    {
        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);
        var it = new LookupIterator(isolate, receiver, lookupKey, LookupIterator.Configuration.OWN);
        return JSReceiver.DeleteProperty(ref it, languageMode);
    }

    /// <summary>DeleteProperty (runtime-object.cc, ES6 section 12.5.4).</summary>
    public static JSValue DeleteProperty(Isolate isolate, JSValue obj, JSValue key, LanguageMode languageMode)
    {
        JSReceiver receiver = ObjectOps.ToObject(isolate, obj);
        return JSValue.FromBoolean(DeleteObjectProperty(isolate, receiver, key, languageMode));
    }

    /// <summary>Runtime_SetFunctionName.</summary>
    public static JSValue SetFunctionName(Isolate isolate, JSValue value, JSValue name)
    {
        var function = value.As<JSFunction>();
        Name key = name.HeapObjectOrNull as Name ?? ObjectOps.ToName(isolate, name);
        JSFunction.SetName(isolate, function, key, ReadOnlyRoots.empty_string);
        return value;
    }

    /// <summary>Runtime_DefineKeyedOwnPropertyInLiteral.</summary>
    public static JSValue DefineKeyedOwnPropertyInLiteral(Isolate isolate, JSValue obj, JSValue name, JSValue value,
        DefineKeyedOwnPropertyInLiteralFlags flags, FeedbackVector? vector, int slot)
    {
        var receiver = obj.As<JSReceiver>();
        if (vector is not null)
        {
            var nexus = new FeedbackNexus(isolate, vector, new FeedbackSlot(slot));
            InlineCacheState state = nexus.IcState();
            if (state == InlineCacheState.UNINITIALIZED)
            {
                if (name.HeapObjectOrNull is Name uniqueName && uniqueName.IsUniqueName)
                {
                    nexus.ConfigureMonomorphic(uniqueName, receiver.Map, JSValue.Undefined);
                }
                else
                {
                    nexus.ConfigureMegamorphic(IcCheckType.kProperty);
                }
            }
            else if (state == InlineCacheState.MONOMORPHIC)
            {
                if (!ReferenceEquals(nexus.GetFirstMap(), receiver.Map) ||
                    !ReferenceEquals(nexus.GetName(), name.HeapObjectOrNull))
                {
                    nexus.ConfigureMegamorphic(IcCheckType.kProperty);
                }
            }
        }

        if ((flags & DefineKeyedOwnPropertyInLiteralFlags.SetFunctionName) != 0)
        {
            JSFunction.SetName(isolate, value.As<JSFunction>(), name.As<Name>(), ReadOnlyRoots.empty_string);
        }

        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, receiver, key, receiver, LookupIterator.Configuration.OWN);

        // Cannot fail since this should only be called when creating an object literal.
        JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, PropertyAttributes.NONE, ShouldThrow.DontThrow);
        return value;
    }

    /// <summary>Runtime_InternalSetPrototype.</summary>
    public static JSValue InternalSetPrototype(Isolate isolate, JSValue obj, JSValue prototype)
    {
        JSReceiver.SetPrototype(isolate, obj.As<JSReceiver>(), prototype, true, ShouldThrow.ThrowOnError);
        return obj;
    }

    /// <summary>Runtime_DefineAccessorPropertyUnchecked.</summary>
    public static JSValue DefineAccessorPropertyUnchecked(Isolate isolate, JSValue obj, JSValue name, JSValue getter,
        JSValue setter, JSValue attrs)
    {
        var receiver = obj.As<JSObject>();
        JSObject.DefineOwnAccessorIgnoreAttributes(isolate, receiver, name.As<Name>(), getter, setter,
            (PropertyAttributes)(int)attrs.Number);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_DefineGetterPropertyUnchecked / DefineSetterPropertyUnchecked.</summary>
    public static JSValue DefineAccessorComponentUnchecked(Isolate isolate, JSValue obj, JSValue name, JSValue function,
        JSValue attrs, bool isGetter)
    {
        var receiver = obj.As<JSObject>();
        var key = name.As<Name>();
        var fn = function.As<JSFunction>();
        if (fn.Shared.Name().Length == 0)
        {
            JSFunction.SetName(isolate, fn, key, isGetter ? ReadOnlyRoots.get_string : ReadOnlyRoots.set_string);
        }
        JSObject.DefineOwnAccessorIgnoreAttributes(isolate, receiver, key, isGetter ? function : JSValue.Null,
            isGetter ? JSValue.Null : function, (PropertyAttributes)(int)attrs.Number);
        return JSValue.Undefined;
    }

    enum PrivateMemberType { kPrivateField, kPrivateAccessor, kPrivateMethod }

    readonly record struct PrivateMember(PrivateMemberType Type, JSValue BrandOrFieldSymbol, JSValue Value);

    static bool IsPrivateMethodOrAccessorVariableMode(VariableMode mode) =>
        mode is VariableMode.PrivateMethod or VariableMode.PrivateSetterOnly or VariableMode.PrivateGetterOnly
            or VariableMode.PrivateGetterAndSetter;

    /// <summary>CollectPrivateMethodsAndAccessorsFromContext (runtime-object.cc).</summary>
    static void CollectPrivateMethodsAndAccessorsFromContext(Isolate isolate, Context context, JSString desc, JSValue brand,
        IsStaticFlag isStaticFlag, List<PrivateMember> results)
    {
        ScopeInfo scopeInfo = context.ScopeInfo;
        int contextIndex = scopeInfo.ContextSlotIndex(desc, out ScopeInfo.VariableLookupResult lookupResult);
        if (contextIndex == -1 || !IsPrivateMethodOrAccessorVariableMode(lookupResult.Mode) ||
            lookupResult.IsStaticFlag != isStaticFlag)
        {
            return;
        }

        JSValue slotValue = context[contextIndex];
        results.Add(new PrivateMember(
            lookupResult.Mode == VariableMode.PrivateMethod ? PrivateMemberType.kPrivateMethod : PrivateMemberType.kPrivateAccessor,
            brand, slotValue));
    }

    /// <summary>CollectPrivateMembersFromReceiver (runtime-object.cc).</summary>
    static void CollectPrivateMembersFromReceiver(Isolate isolate, JSReceiver receiver, JSString desc,
        List<PrivateMember> results)
    {
        FixedArray keys = KeyAccumulator.GetKeys(isolate, receiver, KeyCollectionMode.OwnOnly,
            PropertyFilter.PRIVATE_NAMES_ONLY, GetKeysConversion.ConvertToString);

        if (receiver is JSFunction func)
        {
            SharedFunctionInfo shared = func.Shared;
            if (shared.IsClassConstructor && shared.HasStaticPrivateMethodsOrAccessors)
            {
                CollectPrivateMethodsAndAccessorsFromContext(isolate, func.Context, desc, func, IsStaticFlag.Static,
                    results);
            }
        }

        for (int i = 0; i < keys.Length; ++i)
        {
            var symbol = keys.Get(i).As<Symbol>();
            Debug.Assert(symbol.IsAnyPrivateName);
            JSValue value = ObjectOps.GetProperty(isolate, receiver, symbol);

            if (symbol.IsPrivateBrand)
            {
                CollectPrivateMethodsAndAccessorsFromContext(isolate, value.As<Context>(), desc, symbol,
                    IsStaticFlag.NotStatic, results);
            }
            else if (symbol.Description.HeapObjectOrNull is JSString symbolDesc && JSString.Equals(symbolDesc, desc))
            {
                results.Add(new PrivateMember(PrivateMemberType.kPrivateField, symbol, value));
            }
        }
    }

    /// <summary>FindPrivateMembersFromReceiver (runtime-object.cc).</summary>
    static PrivateMember FindPrivateMembersFromReceiver(Isolate isolate, JSReceiver receiver, JSString desc,
        MessageTemplate notFoundMessage)
    {
        var results = new List<PrivateMember>();
        CollectPrivateMembersFromReceiver(isolate, receiver, desc, results);

        if (results.Count == 0)
        {
            isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, notFoundMessage, [desc]));
        }
        else if (results.Count > 1)
        {
            isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, MessageTemplate.ConflictingPrivateName,
                [desc]));
        }
        return results[0];
    }

    /// <summary>
    /// Runtime_GetPrivateMember and Runtime::GetPrivateMember: reads the private
    /// member named <paramref name="name"/> (its description) of the receiver,
    /// for debug-evaluate and eval code that could not resolve the private name.
    /// </summary>
    public static JSValue GetPrivateMember(Isolate isolate, JSValue receiver, JSValue name)
    {
        var desc = name.As<JSString>();
        if (receiver.HeapObjectOrNull is not JSReceiver target)
        {
            return isolate.ThrowTypeError(MessageTemplate.NonObjectPrivateNameAccess, desc, receiver);
        }
        PrivateMember result = FindPrivateMembersFromReceiver(isolate, target, desc, MessageTemplate.InvalidPrivateMemberRead);

        switch (result.Type)
        {
            case PrivateMemberType.kPrivateField:
            case PrivateMemberType.kPrivateMethod:
                return result.Value;
            default:
            {
                // The accessors are collected from the contexts, so there is no need to
                // perform brand checks.
                var pair = result.Value.As<AccessorPair>();
                if (pair.Getter.IsNull)
                {
                    return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                        MessageTemplate.InvalidPrivateGetterAccess, [desc]));
                }
                return Execution.Call(isolate, pair.Getter, target, []);
            }
        }
    }

    /// <summary>Runtime_SetPrivateMember and Runtime::SetPrivateMember.</summary>
    public static JSValue SetPrivateMember(Isolate isolate, JSValue receiver, JSValue name, JSValue value)
    {
        var desc = name.As<JSString>();
        if (receiver.HeapObjectOrNull is not JSReceiver target)
        {
            return isolate.ThrowTypeError(MessageTemplate.NonObjectPrivateNameAccess, desc, receiver);
        }
        PrivateMember result = FindPrivateMembersFromReceiver(isolate, target, desc, MessageTemplate.InvalidPrivateMemberRead);

        switch (result.Type)
        {
            case PrivateMemberType.kPrivateField:
            {
                var symbol = result.BrandOrFieldSymbol.As<Symbol>();
                return ObjectOps.SetProperty(isolate, target, symbol, value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
            }
            case PrivateMemberType.kPrivateMethod:
                return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                    MessageTemplate.InvalidPrivateMethodWrite, [desc]));
            default:
            {
                // The accessors are collected from the contexts, so there is no need to
                // perform brand checks.
                var pair = result.Value.As<AccessorPair>();
                if (pair.Setter.IsNull)
                {
                    return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                        MessageTemplate.InvalidPrivateSetterAccess, [desc]));
                }
                return Execution.Call(isolate, pair.Setter, target, [value]);
            }
        }
    }

    /// <summary>Runtime_LoadPrivateGetter / LoadPrivateSetter: the component of an AccessorPair.</summary>
    public static JSValue LoadPrivateAccessorComponent(JSValue pair, bool getter)
    {
        var accessorPair = pair.As<AccessorPair>();
        return getter ? accessorPair.Getter : accessorPair.Setter;
    }

    /// <summary>Runtime_CreatePrivateAccessors.</summary>
    public static JSValue CreatePrivateAccessors(Isolate isolate, JSValue getter, JSValue setter)
    {
        var pair = new AccessorPair();
        pair.SetComponents(getter, setter);
        return pair;
    }

    /// <summary>Runtime_AddPrivateBrand.</summary>
    public static JSValue AddPrivateBrand(Isolate isolate, JSValue receiver, JSValue brand, JSValue context, JSValue depth)
    {
        var target = receiver.As<JSReceiver>();
        var brandSymbol = brand.As<Symbol>();
        var it = new LookupIterator(isolate, target, new PropertyKey(isolate, brandSymbol), LookupIterator.Configuration.OWN);

        if (it.IsFound)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidPrivateBrandReinitialization, brand);
        }

        const PropertyAttributes attributes =
            PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;

        // Look for the context in |depth| in the context chain to store it
        // in the instance with the brand variable as key, which is needed by
        // the debugger for retrieving names of private methods.
        var ctx = context.As<Context>();
        for (int d = (int)depth.Number; d > 0; d--) ctx = ctx.Previous!;
        ObjectOps.AddDataProperty(ref it, ctx, attributes, ShouldThrow.ThrowOnError, StoreOrigin.MaybeKeyed);
        return receiver;
    }

    /// <summary>Runtime_CopyDataProperties / Runtime_SetDataProperties.</summary>
    public static JSValue CopyDataProperties(Isolate isolate, JSValue target, JSValue source, bool useSet)
    {
        if (source.IsNullOrUndefined) return JSValue.Undefined;
        JSReceiver.SetOrCopyDataProperties(isolate, target.As<JSReceiver>(), source,
            JSReceiver.PropertiesEnumerationMode.PropertyAdditionOrder, default, useSet);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_CopyDataPropertiesWithExcludedPropertiesOnStack.</summary>
    public static JSValue CopyDataPropertiesWithExcludedProperties(Isolate isolate, JSValue source,
        ReadOnlySpan<JSValue> excluded)
    {
        if (source.IsNullOrUndefined)
        {
            return ErrorUtils.ThrowLoadFromNullOrUndefined(isolate, source, null);
        }

        var excludedProperties = new JSValue[excluded.Length];
        for (int i = 0; i < excluded.Length; i++)
        {
            JSValue property = excluded[i];
            // We convert string to number if possible, in cases of computed
            // properties resolving to numbers, which would've been strings
            // instead because of our call to %ToName() in the desugaring for
            // computed properties.
            if (property.HeapObjectOrNull is JSString str && str.AsArrayIndex(out uint propertyNum))
            {
                property = JSValue.FromNumber(propertyNum);
            }
            excludedProperties[i] = property;
        }

        JSObject target = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        JSReceiver.SetOrCopyDataProperties(isolate, target, source, JSReceiver.PropertiesEnumerationMode.PropertyAdditionOrder,
            excludedProperties, false);
        return target;
    }

    /// <summary>Runtime_CreateIterResultObject.</summary>
    public static JSValue CreateIterResultObject(Isolate isolate, JSValue value, JSValue done) =>
        InterpreterRuntime.NewJSIteratorResult(isolate, value, ObjectOps.BooleanValue(done));
}
