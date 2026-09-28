// Port of src/extensions/externalize-string-extension.{h,cc}: the test
// functions externalizeString, createExternalizableString,
// createExternalizableTwoByteString and isOneByteString that
// Bootstrapper::InstallExtensions installs when --expose-externalize-string is
// set. V8Sharp has no external strings and no one-byte representation
// (deviations.md): externalizing is a no-op on the contents, the
// "externalizable" strings are flat copies, and isOneByteString answers by
// content, as the rest of the engine does.
namespace V8Sharp.Init;

public static class ExternalizeStringExtension
{
    /// <summary>Installs the functions on the global object when the flag asks for them.</summary>
    public static void InstallIfExposed(Isolate isolate, NativeContext nativeContext)
    {
        if (!isolate.Flags.expose_externalize_string) return;
        Install(isolate, nativeContext, "externalizeString", Externalize, 1);
        Install(isolate, nativeContext, "createExternalizableString", CreateExternalizableString, 1);
        Install(isolate, nativeContext, "createExternalizableTwoByteString", CreateExternalizableString, 1);
        Install(isolate, nativeContext, "isOneByteString", IsOneByte, 1);
    }

    static void Install(Isolate isolate, NativeContext nativeContext, string name, BuiltinFunction callback, int length)
    {
        Factory factory = isolate.Factory;
        JSString internalizedName = factory.InternalizeString(name);
        var data = new FunctionTemplateInfo(callback) { Length = length };
        SharedFunctionInfo info = factory.NewSharedFunctionInfo(internalizedName, data, Builtin.HandleApiCallOrConstruct, length, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        JSFunction function = factory.NewFunction(info, nativeContext, nativeContext.StrictFunctionWithoutPrototypeMap);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, nativeContext.GlobalObject, internalizedName, function,
            PropertyAttributes.DONT_ENUM);
    }

    static JSString StringArgument(Isolate isolate, in BuiltinArguments args, string functionName)
    {
        if (args.ArgcWithoutReceiver < 1 || args.AtOrUndefined(1).HeapObjectOrNull is not JSString s)
        {
            isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                isolate.Factory.NewStringFromAsciiChecked("First parameter to " + functionName + "() must be a string.")));
            return null!;
        }
        return s;
    }

    /// <summary>ExternalizeStringExtension::Externalize.</summary>
    static JSValue Externalize(Isolate isolate, in BuiltinArguments args)
    {
        JSString.Flatten(isolate, StringArgument(isolate, args, "externalizeString"));
        return JSValue.Undefined;
    }

    /// <summary>ExternalizeStringExtension::CreateExternalizableString (and the two-byte variant).</summary>
    static JSValue CreateExternalizableString(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = StringArgument(isolate, args, "createExternalizableString");
        return isolate.Factory.NewStringFromUtf16(JSString.Flatten(isolate, s).ToCString());
    }

    /// <summary>ExternalizeStringExtension::IsOneByte.</summary>
    static JSValue IsOneByte(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = StringArgument(isolate, args, "isOneByteString");
        string flat = JSString.Flatten(isolate, s).ToCString();
        foreach (char c in flat)
        {
            if (c > 0xFF) return JSValue.False;
        }
        return JSValue.True;
    }
}
