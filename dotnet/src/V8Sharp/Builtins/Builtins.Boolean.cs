// Port of src/builtins/boolean.tq (Boolean, Boolean.prototype.toString,
// Boolean.prototype.valueOf) and CodeStubAssembler::ToThisValue
// (code-stub-assembler.cc), shared with the Symbol builtins.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterBoolean()
    {
        Register(Builtin.BooleanConstructor, BuiltinsBoolean.BooleanConstructor);
        Register(Builtin.BooleanPrototypeToString, BuiltinsBoolean.BooleanPrototypeToString);
        Register(Builtin.BooleanPrototypeValueOf, BuiltinsBoolean.BooleanPrototypeValueOf);
    }
}

/// <summary>The Boolean builtins.</summary>
public static class BuiltinsBoolean
{
    /// <summary>The primitive types of CodeStubAssembler::ToThisValue.</summary>
    public enum PrimitiveType { Boolean, Number, String, Symbol }

    /// <summary>ES #sec-boolean-constructor-boolean-value.</summary>
    public static JSValue BooleanConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSValue value = JSValue.FromBoolean(ObjectOps.BooleanValue(args.AtOrUndefined(1)));
        if (args.NewTarget.IsUndefined) return value;
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, args.NewTarget.As<JSReceiver>());
        var obj = (JSPrimitiveWrapper)JSObject.NewFastOrSlowJSObjectFromMap(isolate, map);
        obj.Value = value;
        return obj;
    }

    /// <summary>ES #sec-boolean.prototype.tostring.</summary>
    public static JSValue BooleanPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let b be ? thisBooleanValue(this value).
        JSValue b = ToThisValue(isolate, args.Receiver, PrimitiveType.Boolean, "Boolean.prototype.toString");
        // 2. If b is true, return "true"; else return "false".
        return b.IsTrue ? ReadOnlyRoots.true_string : ReadOnlyRoots.false_string;
    }

    /// <summary>ES #sec-boolean.prototype.valueof.</summary>
    public static JSValue BooleanPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        // 1. Return ? thisBooleanValue(this value).
        ToThisValue(isolate, args.Receiver, PrimitiveType.Boolean, "Boolean.prototype.valueOf");

    /// <summary>
    /// CodeStubAssembler::ToThisValue: the primitive value of
    /// <paramref name="value"/> (or of its wrapper) if it has the primitive
    /// type, else TypeError "% requires that 'this' be a %".
    /// </summary>
    public static JSValue ToThisValue(Isolate isolate, JSValue value, PrimitiveType primitiveType, string methodName)
    {
        JSValue v = value.HeapObjectOrNull is JSPrimitiveWrapper wrapper ? wrapper.Value : value;
        bool matches = primitiveType switch
        {
            PrimitiveType.Boolean => v.IsBoolean,
            PrimitiveType.Number => v.IsNumber,
            PrimitiveType.String => v.IsString,
            PrimitiveType.Symbol => v.IsSymbol,
            _ => false,
        };
        if (matches) return v;
        string primitiveName = primitiveType switch
        {
            PrimitiveType.Boolean => "Boolean",
            PrimitiveType.Number => "Number",
            PrimitiveType.String => "String",
            _ => "Symbol",
        };
        return isolate.ThrowTypeError(MessageTemplate.NotGeneric, isolate.Factory.NewStringFromAsciiChecked(methodName),
            isolate.Factory.NewStringFromAsciiChecked(primitiveName));
    }
}
