// Port of Name::ToFunctionName (src/objects/objects.cc).
namespace V8Sharp.Objects;

public abstract partial class Name
{
    /// <summary>Name::ToFunctionName: ES6 section 9.2.11 SetFunctionName, step 4.</summary>
    public static JSString ToFunctionName(Isolate isolate, Name name)
    {
        if (name is JSString s) return s;
        // ES6 section 9.2.11 SetFunctionName, step 4.
        JSValue description = ((Symbol)name).Description;
        if (description.IsUndefined) return ReadOnlyRoots.empty_string;
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCharacter('[');
        builder.AppendString((JSString)description.Object);
        builder.AppendCharacter(']');
        return builder.Finish();
    }

    /// <summary>Name::ToFunctionName(isolate, name, prefix).</summary>
    public static JSString ToFunctionName(Isolate isolate, Name name, JSString prefix)
    {
        JSString nameString = ToFunctionName(isolate, name);
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendString(prefix);
        builder.AppendCharacter(' ');
        builder.AppendString(nameString);
        return builder.Finish();
    }
}
