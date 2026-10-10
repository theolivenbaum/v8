// Port of src/builtins/builtins-json.cc (JSON.parse, JSON.stringify,
// JSON.rawJSON, JSON.isRawJSON) and src/objects/js-raw-json.cc
// (JSRawJson::Create).
using V8Sharp.Json;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterJson()
    {
        Register(Builtin.JsonParse, BuiltinsJson.JsonParse);
        Register(Builtin.JsonStringify, BuiltinsJson.JsonStringify);
        Register(Builtin.JsonRawJson, BuiltinsJson.JsonRawJson);
        Register(Builtin.JsonIsRawJson, BuiltinsJson.JsonIsRawJson);
    }
}

/// <summary>The JSON builtins.</summary>
public static class BuiltinsJson
{
    /// <summary>ES6 section 24.3.1 JSON.parse.</summary>
    public static JSValue JsonParse(Isolate isolate, in BuiltinArguments args)
    {
        JSValue source = args.AtOrUndefined(1);
        JSValue reviver = args.AtOrUndefined(2);
        JSString str = ObjectOps.ToString(isolate, source);
        str = JSString.Flatten(isolate, str);
        return JsonParser.Parse(isolate, str, reviver);
    }

    /// <summary>ES6 section 24.3.2 JSON.stringify.</summary>
    public static JSValue JsonStringify(Isolate isolate, in BuiltinArguments args) =>
        JsonStringifier.JsonStringify(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), args.AtOrUndefined(3));

    /// <summary>https://tc39.es/proposal-json-parse-with-source/#sec-json.rawjson</summary>
    public static JSValue JsonRawJson(Isolate isolate, in BuiltinArguments args) =>
        CreateRawJson(isolate, args.AtOrUndefined(1));

    /// <summary>https://tc39.es/proposal-json-parse-with-source/#sec-json.israwjson</summary>
    public static JSValue JsonIsRawJson(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(args.AtOrUndefined(1).HeapObjectOrNull is JSRawJson);

    /// <summary>JSRawJson::Create.</summary>
    public static JSRawJson CreateRawJson(Isolate isolate, JSValue text)
    {
        JSString jsonString = ObjectOps.ToString(isolate, text);
        JSString flat = JSString.Flatten(isolate, jsonString);
        JsonParser.CheckRawJson(isolate, flat);
        var result = (JSRawJson)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.JSRawJsonMap);
        result.InObjectPropertyRef(JSRawJson.kRawJsonInitialIndex) = flat;
        JSObject.SetIntegrityLevel(isolate, result, JSReceiver.IntegrityLevel.FROZEN, ShouldThrow.ThrowOnError);
        return result;
    }
}
