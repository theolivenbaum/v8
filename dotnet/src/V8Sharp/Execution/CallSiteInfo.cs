// Port of src/objects/call-site-info.{h,cc}: one frame of a captured stack
// trace and its serialization ("at f (file.js:1:2)").
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's CallSiteInfo: receiver, function, position and flags of a stack frame.</summary>
public sealed class CallSiteInfo : HeapObject
{
    // CallSiteInfo::Flag.
    public const int kIsWasm = 1 << 0;
    public const int kIsAsmJsWasm = 1 << 1;
    public const int kIsAsmJsAtNumberConversion = 1 << 2;
    public const int kIsWasmInterpretedFrame = 1 << 3;
    public const int kIsBuiltin = 1 << 4;
    public const int kIsStrict = 1 << 5;
    public const int kIsConstructor = 1 << 6;
    public const int kIsAsync = 1 << 7;
    public const int kIsSourcePositionComputed = 1 << 8;

    public const int kNoLineNumberInfo = 0;
    public const int kNoColumnInfo = 0;
    public const int kNoScriptIdInfo = 0;

    public CallSiteInfo(JSValue receiverOrInstance, JSFunction function, int codeOffsetOrSourcePosition, int flags)
        : base(InstanceType.CallSiteInfoType)
    {
        ReceiverOrInstance = receiverOrInstance;
        Function = function;
        CodeOffsetOrSourcePosition = codeOffsetOrSourcePosition;
        Flags = flags;
    }

    public JSValue ReceiverOrInstance;
    public JSFunction Function;
    public int CodeOffsetOrSourcePosition;
    public int Flags;

    public bool IsWasm => false;
    public bool IsBuiltin => (Flags & kIsBuiltin) != 0;
    public bool IsStrict => (Flags & kIsStrict) != 0;
    public bool IsConstructor => (Flags & kIsConstructor) != 0;
    public bool IsAsync => (Flags & kIsAsync) != 0;

    public bool IsPromiseAll() => IsAsync && ReferenceEquals(Function, Function.NativeContext.PromiseAll);
    public bool IsPromiseAllSettled() => IsAsync && ReferenceEquals(Function, Function.NativeContext.PromiseAllSettled);
    public bool IsPromiseAny() => IsAsync && ReferenceEquals(Function, Function.NativeContext.PromiseAny);

    public bool IsNative() => GetScript() is Script script && script.ScriptType == Script.Type.Native;
    public bool IsEval() => GetScript() is Script script && script.HasEvalOrigin;
    public bool IsUserJavaScript() => GetSharedFunctionInfo().IsUserJavaScript();
    public bool IsMethodCall() => !IsToplevel() && !IsConstructor;

    public bool IsToplevel() => ReceiverOrInstance.HeapObjectOrNull is JSGlobalProxy || ReceiverOrInstance.IsNullOrUndefined;

    public SharedFunctionInfo GetSharedFunctionInfo() => Function.Shared;

    public Script? GetScript() => Function.Shared.Script;

    public static int GetLineNumber(Isolate isolate, CallSiteInfo info)
    {
        if (info.GetScript() is Script script)
        {
            int position = GetSourcePosition(info);
            int lineNumber = script.GetLineNumber(position) + 1;
            if (script.SourceUrl.HeapObjectOrNull is JSString url && url.Length != 0)
            {
                lineNumber -= script.LineOffset;
            }
            return lineNumber;
        }
        return kNoLineNumberInfo;
    }

    public static int GetColumnNumber(Isolate isolate, CallSiteInfo info)
    {
        int position = GetSourcePosition(info);
        if (info.GetScript() is Script script)
        {
            script.GetPositionInfo(position, out Script.PositionInfo positionInfo);
            int columnNumber = positionInfo.Column + 1;
            if (script.SourceUrl.HeapObjectOrNull is JSString url && url.Length != 0 && positionInfo.Line == script.LineOffset)
            {
                columnNumber -= script.ColumnOffset;
            }
            return columnNumber;
        }
        return kNoColumnInfo;
    }

    public static int GetEnclosingLineNumber(Isolate isolate, CallSiteInfo info)
    {
        if (info.GetScript() is not Script script) return kNoLineNumberInfo;
        int position = info.GetSharedFunctionInfo().FunctionTokenPosition();
        return script.GetLineNumber(position) + 1;
    }

    public static int GetEnclosingColumnNumber(Isolate isolate, CallSiteInfo info)
    {
        if (info.GetScript() is not Script script) return kNoColumnInfo;
        int position = info.GetSharedFunctionInfo().FunctionTokenPosition();
        return script.GetColumnNumber(position) + 1;
    }

    public int GetScriptId() => GetScript() is Script script ? script.Id : kNoScriptIdInfo;

    public JSValue GetScriptName() => GetScript() is Script script ? script.Name : JSValue.Null;

    public JSValue GetScriptNameOrSourceURL() => GetScript() is Script script ? script.GetNameOrSourceURL() : JSValue.Null;

    public JSValue GetScriptSource() => GetScript() is Script script ? script.Source : JSValue.Null;

    public JSValue GetScriptSourceMappingURL() => GetScript() is Script script ? script.SourceMappingUrl : JSValue.Null;

    static JSString FormatEvalOrigin(Isolate isolate, Script script)
    {
        JSValue sourceURL = script.GetNameOrSourceURL();
        if (sourceURL.HeapObjectOrNull is JSString s) return s;

        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("eval at ");
        if (script.EvalFromShared is SharedFunctionInfo evalShared)
        {
            JSString evalName = SharedFunctionInfo.DebugName(isolate, evalShared);
            if (evalName.Length != 0) builder.AppendString(evalName);
            else builder.AppendCStringLiteral("<anonymous>");
            if (evalShared.Script is Script evalScript)
            {
                builder.AppendCStringLiteral(" (");
                if (evalScript.HasEvalOrigin)
                {
                    // Eval script originated from another eval.
                    builder.AppendString(FormatEvalOrigin(isolate, evalScript));
                }
                else
                {
                    // eval script originated from "real" source.
                    if (evalScript.Name.HeapObjectOrNull is JSString evalScriptName)
                    {
                        builder.AppendString(evalScriptName);
                        if (evalScript.GetPositionInfo(script.EvalFromPosition, out Script.PositionInfo info, Script.OffsetFlag.NoOffset))
                        {
                            builder.AppendCharacter(':');
                            builder.AppendInt(info.Line + 1);
                            builder.AppendCharacter(':');
                            builder.AppendInt(info.Column + 1);
                        }
                    }
                    else
                    {
                        builder.AppendCStringLiteral("unknown source");
                    }
                }
                builder.AppendCharacter(')');
            }
        }
        else
        {
            builder.AppendCStringLiteral("<anonymous>");
        }
        return builder.Finish();
    }

    public static JSValue GetEvalOrigin(Isolate isolate, CallSiteInfo info)
    {
        if (info.GetScript() is not Script script || !script.HasEvalOrigin) return JSValue.Undefined;
        return FormatEvalOrigin(isolate, script);
    }

    /// <summary>Builtins::NameForStackTrace: builtins shown with a qualified name.</summary>
    public static string? NameForStackTrace(Isolate isolate, Builtin builtin) => builtin switch
    {
        Builtin.DataViewPrototypeGetBigInt64 => "DataView.prototype.getBigInt64",
        Builtin.DataViewPrototypeGetBigUint64 => "DataView.prototype.getBigUint64",
        Builtin.DataViewPrototypeGetFloat16 => "DataView.prototype.getFloat16",
        Builtin.DataViewPrototypeGetFloat32 => "DataView.prototype.getFloat32",
        Builtin.DataViewPrototypeGetFloat64 => "DataView.prototype.getFloat64",
        Builtin.DataViewPrototypeGetInt8 => "DataView.prototype.getInt8",
        Builtin.DataViewPrototypeGetInt16 => "DataView.prototype.getInt16",
        Builtin.DataViewPrototypeGetInt32 => "DataView.prototype.getInt32",
        Builtin.DataViewPrototypeGetUint8 => "DataView.prototype.getUint8",
        Builtin.DataViewPrototypeGetUint16 => "DataView.prototype.getUint16",
        Builtin.DataViewPrototypeGetUint32 => "DataView.prototype.getUint32",
        Builtin.DataViewPrototypeSetBigInt64 => "DataView.prototype.setBigInt64",
        Builtin.DataViewPrototypeSetBigUint64 => "DataView.prototype.setBigUint64",
        Builtin.DataViewPrototypeSetFloat16 => "DataView.prototype.setFloat16",
        Builtin.DataViewPrototypeSetFloat32 => "DataView.prototype.setFloat32",
        Builtin.DataViewPrototypeSetFloat64 => "DataView.prototype.setFloat64",
        Builtin.DataViewPrototypeSetInt8 => "DataView.prototype.setInt8",
        Builtin.DataViewPrototypeSetInt16 => "DataView.prototype.setInt16",
        Builtin.DataViewPrototypeSetInt32 => "DataView.prototype.setInt32",
        Builtin.DataViewPrototypeSetUint8 => "DataView.prototype.setUint8",
        Builtin.DataViewPrototypeSetUint16 => "DataView.prototype.setUint16",
        Builtin.DataViewPrototypeSetUint32 => "DataView.prototype.setUint32",
        Builtin.DataViewPrototypeGetByteLength => "get DataView.prototype.byteLength",
        Builtin.StringPrototypeToLocaleLowerCase => "String.toLocaleLowerCase",
        // V8 also maps the wasm builtins ThrowIndexOfCalledOnNull and
        // ThrowToLowerCaseCalledOnNull here; V8Sharp has no wasm.
        Builtin.StringPrototypeIndexOf => "String.indexOf",
        _ => null,
    };

    public static JSValue GetFunctionName(Isolate isolate, CallSiteInfo info)
    {
        JSFunction function = info.Function;
        if (function.Shared.HasBuiltinId)
        {
            string? maybeKnownName = NameForStackTrace(isolate, function.Shared.BuiltinId);
            // This is for cases where using the builtin's name allows us to print
            // e.g. "String.indexOf", instead of just "indexOf" which is what we
            // would infer below.
            if (maybeKnownName is not null) return isolate.Factory.NewStringFromAsciiChecked(maybeKnownName);
        }
        JSString name = JSFunction.GetDebugName(isolate, function);
        if (name.Length != 0) return name;
        if (info.IsEval()) return ReadOnlyRoots.eval_string;
        return JSValue.Null;
    }

    public static JSString GetFunctionDebugName(Isolate isolate, CallSiteInfo info)
    {
        JSString name = JSFunction.GetDebugName(isolate, info.Function);
        if (name.Length == 0 && info.IsEval()) name = ReadOnlyRoots.eval_string;
        return name;
    }

    static JSValue InferMethodNameFromFastObject(Isolate isolate, JSObject receiver, JSFunction fun, JSValue name)
    {
        Map map = receiver.Map;
        DescriptorArray descriptors = map.InstanceDescriptors;
        int n = map.NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            Name key = descriptors.GetKey(i);
            if (key is Symbol) continue;
            PropertyDetails details = descriptors.GetDetails(i);
            if (details.IsDontEnum) continue;
            JSValue value;
            if (details.Location == PropertyLocation.Field)
            {
                FieldIndex fieldIndex = FieldIndex.ForDetails(map, details);
                if (fieldIndex.IsDouble) continue;
                value = receiver.RawFastPropertyAt(fieldIndex);
            }
            else
            {
                value = descriptors.GetStrongValue(new InternalIndex(i));
            }
            if (!ReferenceEquals(value.HeapObjectOrNull, fun))
            {
                if (value.HeapObjectOrNull is not AccessorPair pair) continue;
                if (!ReferenceEquals(pair.Getter.HeapObjectOrNull, fun) && !ReferenceEquals(pair.Setter.HeapObjectOrNull, fun)) continue;
            }
            if (!ReferenceEquals(name.HeapObjectOrNull, key))
            {
                name = name.IsUndefined ? key : JSValue.Null;
            }
        }
        return name;
    }

    static JSValue InferMethodNameFromDictionary(Isolate isolate, HashTableBase dictionary, JSFunction fun, JSValue name)
    {
        int capacity = dictionary.Capacity;
        for (int i = 0; i < capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out JSValue key)) continue;
            if (key.IsSymbol) continue;
            PropertyDetails details;
            JSValue value;
            if (dictionary is GlobalDictionary global)
            {
                details = global.DetailsAt(entry);
                value = global.ValueAt(entry);
            }
            else
            {
                var names = (NameDictionary)dictionary;
                details = names.DetailsAt(entry);
                value = names.ValueAt(entry);
            }
            if (details.IsDontEnum) continue;
            if (!ReferenceEquals(value.HeapObjectOrNull, fun))
            {
                if (value.HeapObjectOrNull is not AccessorPair pair) continue;
                if (!ReferenceEquals(pair.Getter.HeapObjectOrNull, fun) && !ReferenceEquals(pair.Setter.HeapObjectOrNull, fun)) continue;
            }
            if (!name.IsIdenticalTo(key))
            {
                name = name.IsUndefined ? key : JSValue.Null;
            }
        }
        return name;
    }

    static JSValue InferMethodName(Isolate isolate, JSReceiver receiver, JSFunction fun)
    {
        JSValue name = JSValue.Undefined;
        for (var it = new PrototypeIterator(isolate, receiver, WhereToStart.StartAtReceiver); !it.IsAtEnd; it.Advance())
        {
            JSReceiver? current = it.GetCurrent();
            if (current is not JSObject obj) break;
            if (obj.Map.IsAccessCheckNeeded) break;
            if (obj.HasFastProperties) name = InferMethodNameFromFastObject(isolate, obj, fun, name);
            else if (obj is JSGlobalObject global) name = InferMethodNameFromDictionary(isolate, global.GlobalDictionary, fun, name);
            else name = InferMethodNameFromDictionary(isolate, obj.PropertyDictionary, fun, name);
        }
        if (name.IsUndefined) return JSValue.Null;
        return name;
    }

    public static JSValue GetMethodName(Isolate isolate, CallSiteInfo info)
    {
        JSValue receiverOrInstance = info.ReceiverOrInstance;
        if (receiverOrInstance.IsNullOrUndefined) return JSValue.Null;

        JSFunction function = info.Function;
        // Class members initializer function is not a method.
        if (Globals.IsClassInitializerFunction(function.Shared.Kind)) return JSValue.Null;

        JSReceiver receiver = ObjectOps.ToObject(isolate, receiverOrInstance);
        JSString name = function.Shared.Name();

        // ES2015 gives getters and setters name prefixes which must
        // be stripped to find the property name.
        if (name.HasPrefix("get ") || name.HasPrefix("set "))
        {
            name = isolate.Factory.NewProperSubString(name, 4, name.Length);
        }
        else if (name.Length == 0)
        {
            // The function doesn't have a meaningful "name" property, however
            // the parser does store an inferred name "o.foo" for the common
            // case of `o.foo = function() {...}`, so see if we can derive a
            // property name to guess from that.
            name = function.Shared.InferredName();
            for (int index = name.Length; --index >= 0;)
            {
                if (name.Get(index) == '.')
                {
                    name = isolate.Factory.NewProperSubString(name, index + 1, name.Length);
                    break;
                }
            }
        }

        if (name.Length != 0)
        {
            var key = new PropertyKey(isolate, isolate.Factory.InternalizeString(name));
            var it = new LookupIterator(isolate, receiver, key, receiver, LookupIterator.Configuration.PROTOTYPE_CHAIN_SKIP_INTERCEPTOR);
            if (it.State == LookupIterator.StateKind.DATA)
            {
                if (ReferenceEquals(it.GetDataValue().HeapObjectOrNull, function)) return name;
            }
            else if (it.State == LookupIterator.StateKind.ACCESSOR)
            {
                if (it.GetAccessors() is AccessorPair pair &&
                    (ReferenceEquals(pair.Getter.HeapObjectOrNull, function) || ReferenceEquals(pair.Setter.HeapObjectOrNull, function)))
                {
                    return name;
                }
            }
        }

        return InferMethodName(isolate, receiver, function);
    }

    public static JSValue GetTypeName(Isolate isolate, CallSiteInfo info)
    {
        if (!info.IsMethodCall()) return JSValue.Null;
        JSReceiver receiver = ObjectOps.ToObject(isolate, info.ReceiverOrInstance);
        if (receiver is JSProxy) return ReadOnlyRoots.Proxy_string;
        if (receiver is JSFunction function)
        {
            JSString className = JSFunction.GetDebugName(isolate, function);
            if (className.Length != 0) return className;
        }
        return JSReceiver.GetConstructorName(isolate, receiver);
    }

    public static int GetSourcePosition(CallSiteInfo info)
    {
        // The frame provider resolves source positions when capturing, so the
        // kIsSourcePositionComputed bit is always set.
        return info.CodeOffsetOrSourcePosition;
    }

    /// <summary>CallSiteInfo::ComputeLocation.</summary>
    public static bool ComputeLocation(Isolate isolate, CallSiteInfo info, out MessageLocation location)
    {
        location = null!;
        SharedFunctionInfo shared = info.GetSharedFunctionInfo();
        if (!shared.IsSubjectToDebugging()) return false;
        Script script = shared.Script!;
        if (script.Source.IsUndefined) return false;
        int pos = GetSourcePosition(info);
        location = new MessageLocation(script, pos, pos + 1, shared);
        return true;
    }

    static bool IsNonEmptyString(JSValue obj) => obj.HeapObjectOrNull is JSString s && s.Length > 0;

    static void AppendFileLocation(Isolate isolate, CallSiteInfo frame, ref IncrementalStringBuilder builder)
    {
        JSValue scriptNameOrSourceUrl = frame.GetScriptNameOrSourceURL();
        if (!scriptNameOrSourceUrl.IsString && frame.IsEval())
        {
            builder.AppendString(GetEvalOrigin(isolate, frame).As<JSString>());
            // Expecting source position to follow.
            builder.AppendCStringLiteral(", ");
        }

        if (IsNonEmptyString(scriptNameOrSourceUrl))
        {
            builder.AppendString(scriptNameOrSourceUrl.As<JSString>());
        }
        else
        {
            // Source code does not originate from a file and is not native, but we
            // can still get the source position inside the source string, e.g. in
            // an eval string.
            builder.AppendCStringLiteral("<anonymous>");
        }

        int lineNumber = GetLineNumber(isolate, frame);
        if (lineNumber != kNoLineNumberInfo)
        {
            builder.AppendCharacter(':');
            builder.AppendInt(lineNumber);

            int columnNumber = GetColumnNumber(isolate, frame);
            if (columnNumber != kNoColumnInfo)
            {
                builder.AppendCharacter(':');
                builder.AppendInt(columnNumber);
            }
        }
    }

    // Returns true iff
    // 1. the subject ends with '.' + pattern or ' ' + pattern, or
    // 2. subject == pattern.
    static bool StringEndsWithMethodName(JSString subject, JSString pattern)
    {
        if (JSString.Equals(subject, pattern)) return true;
        ReadOnlySpan<char> s = subject.FlatSpan();
        ReadOnlySpan<char> p = pattern.FlatSpan();
        int patternIndex = p.Length - 1;
        int subjectIndex = s.Length - 1;
        // Iterate over len + 1.
        for (int i = 0; i <= p.Length; i++)
        {
            if (subjectIndex < 0) return false;
            char subjectChar = s[subjectIndex];
            if (i == p.Length)
            {
                if (subjectChar != '.' && subjectChar != ' ') return false;
            }
            else if (subjectChar != p[patternIndex])
            {
                return false;
            }
            patternIndex--;
            subjectIndex--;
        }
        return true;
    }

    static void AppendMethodCall(Isolate isolate, CallSiteInfo frame, ref IncrementalStringBuilder builder)
    {
        JSValue typeName = GetTypeName(isolate, frame);
        JSValue methodName = GetMethodName(isolate, frame);
        JSValue functionName = GetFunctionName(isolate, frame);

        if (IsNonEmptyString(functionName))
        {
            var functionString = functionName.As<JSString>();
            if (IsNonEmptyString(typeName))
            {
                var typeString = typeName.As<JSString>();
                if (JSString.IsIdentifier(functionString) && !JSString.Equals(functionString, typeString))
                {
                    builder.AppendString(typeString);
                    builder.AppendCharacter('.');
                }
            }
            builder.AppendString(functionString);

            if (IsNonEmptyString(methodName))
            {
                var methodString = methodName.As<JSString>();
                if (!StringEndsWithMethodName(functionString, methodString))
                {
                    builder.AppendCStringLiteral(" [as ");
                    builder.AppendString(methodString);
                    builder.AppendCharacter(']');
                }
            }
        }
        else
        {
            if (IsNonEmptyString(typeName))
            {
                builder.AppendString(typeName.As<JSString>());
                builder.AppendCharacter('.');
            }
            if (IsNonEmptyString(methodName)) builder.AppendString(methodName.As<JSString>());
            else builder.AppendCStringLiteral("<anonymous>");
        }
    }

    /// <summary>SerializeCallSiteInfo (SerializeJSStackFrame).</summary>
    public static void SerializeCallSiteInfo(Isolate isolate, CallSiteInfo frame, ref IncrementalStringBuilder builder)
    {
        JSValue functionName = GetFunctionName(isolate, frame);
        if (frame.IsAsync)
        {
            builder.AppendCStringLiteral("async ");
            if (frame.IsPromiseAll() || frame.IsPromiseAny() || frame.IsPromiseAllSettled())
            {
                if (IsNonEmptyString(functionName))
                {
                    builder.AppendCStringLiteral("Promise.");
                    builder.AppendString(functionName.As<JSString>());
                }
                else
                {
                    builder.AppendCStringLiteral("<anonymous>");
                }
                builder.AppendCStringLiteral(" (index ");
                builder.AppendInt(GetSourcePosition(frame));
                builder.AppendCharacter(')');
                return;
            }
        }
        if (frame.IsMethodCall())
        {
            AppendMethodCall(isolate, frame, ref builder);
        }
        else if (frame.IsConstructor)
        {
            builder.AppendCStringLiteral("new ");
            if (IsNonEmptyString(functionName)) builder.AppendString(functionName.As<JSString>());
            else builder.AppendCStringLiteral("<anonymous>");
        }
        else if (IsNonEmptyString(functionName))
        {
            builder.AppendString(functionName.As<JSString>());
        }
        else
        {
            AppendFileLocation(isolate, frame, ref builder);
            return;
        }
        builder.AppendCStringLiteral(" (");
        AppendFileLocation(isolate, frame, ref builder);
        builder.AppendCharacter(')');
    }

    public static JSString SerializeCallSiteInfo(Isolate isolate, CallSiteInfo frame)
    {
        var builder = new IncrementalStringBuilder(isolate);
        SerializeCallSiteInfo(isolate, frame, ref builder);
        return builder.Finish();
    }
}
