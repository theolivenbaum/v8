// Port of the CloneObjectIC of src/ic/ic.cc (Runtime_CloneObjectIC_Miss,
// CloneObjectSlowPath) used by the CloneObject bytecode (object spread
// `{...source}`).
//
// Deviation: V8's fast path clones objects whose map it has cached in the
// feedback (identical map or an object literal map, with side-step
// transitions); V8Sharp always takes CloneObjectSlowPath and records the
// slot as megamorphic, which is what V8 does for every unsupported source.
namespace V8Sharp.IC;

public static class CloneObjectIC
{
    const int kHasNullPrototype = 1 << 4;  // ObjectLiteral::kHasNullPrototype

    /// <summary>CloneObject: CloneObjectIC.</summary>
    public static JSValue Clone(Isolate isolate, FeedbackVector? vector, int slot, JSValue source, int flags)
    {
        if (vector is not null && !ReferenceEquals(vector.Slots[slot]._obj, ReadOnlyRoots.megamorphic_symbol))
        {
            new FeedbackNexus(isolate, vector, new FeedbackSlot(slot)).ConfigureMegamorphic();
        }
        return CloneObjectSlowPath(isolate, source, flags);
    }

    /// <summary>CloneObjectSlowPath (ic.cc).</summary>
    public static JSObject CloneObjectSlowPath(Isolate isolate, JSValue source, int flags)
    {
        JSObject newObject;
        if ((flags & kHasNullPrototype) != 0)
        {
            newObject = isolate.Factory.NewJSObjectWithNullProto();
        }
        else if (source.HeapObjectOrNull is JSObject sourceObject && sourceObject.Map.OnlyHasSimpleProperties())
        {
            Map sourceMap = sourceObject.Map;
            // TODO(olivf, chrome:1204540) It might be interesting to pick a map with
            // more properties, depending how many properties are added by the
            // surrounding literal.
            int properties = sourceMap.GetInObjectProperties() - sourceMap.UnusedInObjectProperties();
            Map map = isolate.Factory.ObjectLiteralMapFromCache(isolate.NativeContext, properties);
            newObject = map.IsDictionaryMap
                ? isolate.Factory.NewSlowJSObjectFromMap(map)
                : isolate.Factory.NewJSObjectFromMap(map);
        }
        else
        {
            newObject = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        }

        if (source.IsNullOrUndefined) return newObject;

        JSReceiver.SetOrCopyDataProperties(isolate, newObject, source, PropertiesEnumerationMode.PropertyAdditionOrder,
            default, false);
        return newObject;
    }
}
