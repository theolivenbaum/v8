// Port of JsonParseInternalizer (src/json/json-parser.cc): the JSON.parse
// reviver walk, with the context argument ({source}) of the JSON.parse source
// text access proposal.

namespace V8Sharp.Json;

sealed class JsonParseInternalizer
{
    enum ReviverMode
    {
        kWithoutContext,  // Two-arg reviver callback, no context argument.
        kWithoutSource,   // Three-arg reviver callback, context argument has no source property.
        kWithSource,      // Three-arg reviver callback, context object has source property.
    }

    static ReviverMode NoSource(ReviverMode oldMode) => oldMode == ReviverMode.kWithSource ? ReviverMode.kWithoutSource : oldMode;

    readonly Isolate _isolate;
    readonly JSReceiver _reviver;

    JsonParseInternalizer(Isolate isolate, JSReceiver reviver)
    {
        _isolate = isolate;
        _reviver = reviver;
    }

    public static JSValue Internalize(Isolate isolate, JSValue result, JSReceiver reviver, JSString source,
        JsonValNode? valNode, bool passContextArgument)
    {
        var internalizer = new JsonParseInternalizer(isolate, reviver);
        JSObject holder = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        JSString name = ReadOnlyRoots.empty_string;
        JSObject.AddProperty(isolate, holder, name, result, PropertyAttributes.NONE);
        if (passContextArgument)
        {
            // Three-argument reviver, so we add a context argument to each
            // callback to the reviver, and that context object will normally
            // have a source property.
            return internalizer.InternalizeJsonProperty(ReviverMode.kWithSource, holder, name, valNode, result, true);
        }
        // Faster two-argument reviver.
        return internalizer.InternalizeJsonProperty(ReviverMode.kWithoutContext, holder, name, null, default, false);
    }

    JSValue InternalizeJsonProperty(ReviverMode initialReviverMode, JSReceiver holder, JSString name, JsonValNode? valNode,
        JSValue snapshot, bool hasSnapshot)
    {
        JSValue value = ObjectOps.GetPropertyOrElement(_isolate, holder, name);

        // When reviver_mode == kWithSource, the source text is passed
        // to the reviver if the reviver has not mucked with the originally parsed
        // value.
        ReviverMode reviverMode = initialReviverMode == ReviverMode.kWithSource && !(hasSnapshot && ObjectOps.SameValue(value, snapshot))
            ? NoSource(initialReviverMode)
            : initialReviverMode;

        if (value.HeapObjectOrNull is JSReceiver obj)
        {
            // Value is non-primitive, so we may have to recurse deeper.
            if (ObjectOps.IsArray(_isolate, value))
            {
                double length = ObjectOps.GetLengthFromArrayLike(_isolate, obj).Number;
                if (reviverMode == ReviverMode.kWithSource)
                {
                    var arrayNode = valNode as JsonValNode.Array;
                    int snapshotLength = arrayNode?.Nodes.Length ?? 0;
                    for (uint i = 0; i < length; i++)
                    {
                        JSString indexName = _isolate.Factory.NumberToString(JSValue.FromNumber(i));
                        // Even if the array pointer snapshot matched, it's possible the
                        // array had new elements added that are not in the snapshotted
                        // elements.
                        if (i < snapshotLength)
                        {
                            RecurseAndApply(ReviverMode.kWithSource, obj, indexName, arrayNode!.Nodes[i], arrayNode.Snapshots[i], true);
                        }
                        else
                        {
                            RecurseAndApply(ReviverMode.kWithoutSource, obj, indexName, null, default, false);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        JSString indexName = _isolate.Factory.NumberToString(JSValue.FromNumber(i));
                        RecurseAndApply(NoSource(initialReviverMode), obj, indexName, null, default, false);
                    }
                }
            }
            else
            {
                FixedArray contents = KeyAccumulator.GetKeys(_isolate, obj, KeyCollectionMode.OwnOnly,
                    PropertyFilter.ENUMERABLE_STRINGS, GetKeysConversion.ConvertToString);
                if (reviverMode == ReviverMode.kWithSource)
                {
                    var objectNode = valNode as JsonValNode.Object;
                    for (int i = 0; i < contents.Length; i++)
                    {
                        var keyName = (JSString)contents.Get(i).Object;
                        // Even if the object pointer snapshot matched, it's possible the
                        // object had new properties added that are not in the snapshotted
                        // contents.
                        if (objectNode is not null && objectNode.Properties.TryGetValue(
                                _isolate.Factory.InternalizeString(keyName), out (JsonValNode? Node, JSValue Snapshot) entry))
                        {
                            RecurseAndApply(ReviverMode.kWithSource, obj, keyName, entry.Node, entry.Snapshot, true);
                        }
                        else
                        {
                            RecurseAndApply(ReviverMode.kWithoutSource, obj, keyName, null, default, false);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < contents.Length; i++)
                    {
                        var keyName = (JSString)contents.Get(i).Object;
                        RecurseAndApply(NoSource(initialReviverMode), obj, keyName, null, default, false);
                    }
                }
            }
        }
        // Done recursing.

        if (reviverMode == ReviverMode.kWithoutContext)
        {
            return Execution.Call(_isolate, _reviver, holder, [name, value]);
        }

        JSObject context = _isolate.Factory.NewJSObject(_isolate.NativeContext.ObjectFunction);
        if (reviverMode == ReviverMode.kWithSource && valNode is JsonValNode.Primitive primitive)
        {
            JSReceiver.CreateDataProperty(_isolate, context, ReadOnlyRoots.source_string, primitive.Source, ShouldThrow.ThrowOnError);
        }
        return Execution.Call(_isolate, _reviver, holder, [name, value, context]);
    }

    void RecurseAndApply(ReviverMode reviverMode, JSReceiver holder, JSString name, JsonValNode? valNode, JSValue snapshot,
        bool hasSnapshot)
    {
        _isolate.StackGuard.StackCheck(_isolate);
        JSValue result = InternalizeJsonProperty(reviverMode, holder, name, valNode, snapshot, hasSnapshot);
        if (result.IsUndefined)
        {
            JSReceiver.DeletePropertyOrElement(_isolate, holder, _isolate.Factory.InternalizeString(name));
        }
        else
        {
            var desc = new PropertyDescriptor();
            desc.SetValue(result);
            desc.SetConfigurable(true);
            desc.SetEnumerable(true);
            desc.SetWritable(true);
            JSReceiver.DefineOwnProperty(_isolate, holder, _isolate.Factory.InternalizeString(name), ref desc, ShouldThrow.DontThrow);
        }
    }
}
