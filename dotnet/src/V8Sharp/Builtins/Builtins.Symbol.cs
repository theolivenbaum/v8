// Port of the Symbol builtins: src/builtins/builtins-symbol.cc (Symbol,
// Symbol.for, Symbol.keyFor), symbol.tq (description, @@toPrimitive,
// toString, valueOf), Runtime_SymbolDescriptiveString
// (src/runtime/runtime-symbol.cc) and Isolate::SymbolFor (isolate.cc).
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterSymbol()
    {
        Register(Builtin.SymbolConstructor, BuiltinsSymbol.SymbolConstructor);
        Register(Builtin.SymbolFor, BuiltinsSymbol.SymbolFor);
        Register(Builtin.SymbolKeyFor, BuiltinsSymbol.SymbolKeyFor);
        Register(Builtin.SymbolPrototypeDescriptionGetter, BuiltinsSymbol.SymbolPrototypeDescriptionGetter);
        Register(Builtin.SymbolPrototypeToPrimitive, BuiltinsSymbol.SymbolPrototypeToPrimitive);
        Register(Builtin.SymbolPrototypeToString, BuiltinsSymbol.SymbolPrototypeToString);
        Register(Builtin.SymbolPrototypeValueOf, BuiltinsSymbol.SymbolPrototypeValueOf);
    }
}

/// <summary>The Symbol builtins.</summary>
public static class BuiltinsSymbol
{
    /// <summary>https://tc39.es/ecma262/#sec-symbol-constructor</summary>
    public static JSValue SymbolConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (!args.NewTarget.IsUndefined)
        {
            // [[Construct]]
            return isolate.ThrowTypeError(MessageTemplate.NotConstructor, ReadOnlyRoots.Symbol_string);
        }
        // [[Call]]
        Symbol result = isolate.Factory.NewSymbol();
        JSValue description = args.AtOrUndefined(1);
        if (!description.IsUndefined)
        {
            result.Description = ObjectOps.ToString(isolate, description);
        }
        return result;
    }

    /// <summary>https://tc39.es/ecma262/#sec-symbol.for</summary>
    public static JSValue SymbolFor(Isolate isolate, in BuiltinArguments args)
    {
        JSString key = ObjectOps.ToString(isolate, args.AtOrUndefined(1));
        return SymbolForPublic(isolate, key);
    }

    /// <summary>Isolate::SymbolFor(RootIndex::kPublicSymbolTable, name, false).</summary>
    public static Symbol SymbolForPublic(Isolate isolate, JSString name)
    {
        JSString key = isolate.Factory.InternalizeString(name);
        string lookup = key.Flatten();
        if (!isolate.PublicSymbolTable.TryGetValue(lookup, out Symbol? symbol))
        {
            symbol = isolate.Factory.NewSymbol();
            symbol.Description = key;
            symbol.IsInPublicSymbolTable = true;
            isolate.PublicSymbolTable.Add(lookup, symbol);
        }
        return symbol;
    }

    /// <summary>https://tc39.es/ecma262/#sec-symbol.keyfor</summary>
    public static JSValue SymbolKeyFor(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        if (obj.HeapObjectOrNull is not Symbol symbol) return isolate.ThrowTypeError(MessageTemplate.SymbolKeyFor, obj);
        return symbol.IsInPublicSymbolTable ? symbol.Description : JSValue.Undefined;
    }

    static Symbol ThisSymbolValue(Isolate isolate, JSValue receiver, string method) =>
        BuiltinsBoolean.ToThisValue(isolate, receiver, BuiltinsBoolean.PrimitiveType.Symbol, method).As<Symbol>();

    /// <summary>https://tc39.es/ecma262/#sec-symbol.prototype.description</summary>
    public static JSValue SymbolPrototypeDescriptionGetter(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let s be the this value.
        // 2. Let sym be ? thisSymbolValue(s).
        Symbol sym = ThisSymbolValue(isolate, args.Receiver, "Symbol.prototype.description");
        // 3. Return sym.[[Description]].
        return sym.Description;
    }

    /// <summary>https://tc39.es/ecma262/#sec-symbol.prototype-@@toprimitive</summary>
    public static JSValue SymbolPrototypeToPrimitive(Isolate isolate, in BuiltinArguments args) =>
        // 1. Return ? thisSymbolValue(this value).
        ThisSymbolValue(isolate, args.Receiver, "Symbol.prototype [ @@toPrimitive ]");

    /// <summary>https://tc39.es/ecma262/#sec-symbol.prototype.tostring</summary>
    public static JSValue SymbolPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let sym be ? thisSymbolValue(this value).
        Symbol sym = ThisSymbolValue(isolate, args.Receiver, "Symbol.prototype.toString");
        // 2. Return SymbolDescriptiveString(sym).
        return SymbolDescriptiveString(isolate, sym);
    }

    /// <summary>https://tc39.es/ecma262/#sec-symbol.prototype.valueof</summary>
    public static JSValue SymbolPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        // 1. Return ? thisSymbolValue(this value).
        ThisSymbolValue(isolate, args.Receiver, "Symbol.prototype.valueOf");

    /// <summary>Runtime_SymbolDescriptiveString: "Symbol(" + description + ")".</summary>
    public static JSString SymbolDescriptiveString(Isolate isolate, Symbol symbol)
    {
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("Symbol(");
        if (symbol.Description.HeapObjectOrNull is JSString description) builder.AppendString(description);
        builder.AppendCharacter(')');
        return builder.Finish();
    }
}
