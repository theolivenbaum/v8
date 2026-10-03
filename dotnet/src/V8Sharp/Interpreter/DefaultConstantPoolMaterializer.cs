// A stand-in for the isolate's factory when materializing constant pools
// before the object model exists (and in unit tests).
//
// TODO(merge): the BytecodeGenerator should pass a materializer backed by the
// Factory (internalized JSStrings, ScopeInfo, BigInt, the real symbols and
// boilerplate descriptions); this one produces printable placeholders.
using System.Globalization;

namespace V8Sharp.Interpreter;

/// <summary>A constant that only knows how V8 prints it.</summary>
public sealed class PrintableConstant(string instanceTypeName, string? printedValue, string brief) : IPrintableConstant
{
    public string InstanceTypeName => instanceTypeName;
    public string? PrintedValue => printedValue;
    public string Brief() => brief;
    public override string ToString() => brief;
}

public sealed class DefaultConstantPoolMaterializer : IConstantPoolMaterializer
{
    public static readonly DefaultConstantPoolMaterializer Instance = new();

    static readonly PrintableConstant s_theHole = new("HOLE_TYPE", null, "<the_hole_value>");
    static readonly PrintableConstant s_asyncIteratorSymbol = new("SYMBOL_TYPE", null, "<Symbol: Symbol.asyncIterator>");
    static readonly PrintableConstant s_classFieldsSymbol = new("SYMBOL_TYPE", null, "<Symbol: (class_fields_symbol)>");
    static readonly PrintableConstant s_iteratorSymbol = new("SYMBOL_TYPE", null, "<Symbol: Symbol.iterator>");
    static readonly PrintableConstant s_interpreterTrampolineSymbol =
        new("SYMBOL_TYPE", null, "<Symbol: (interpreter_trampoline_symbol)>");
    static readonly PrintableConstant s_emptyObjectBoilerplate =
        new("OBJECT_BOILERPLATE_DESCRIPTION_TYPE", null, "<ObjectBoilerplateDescription[0]>");
    static readonly PrintableConstant s_emptyArrayBoilerplate =
        new("ARRAY_BOILERPLATE_DESCRIPTION_TYPE", null, "<ArrayBoilerplateDescription PACKED_SMI_ELEMENTS, <FixedArray[0]>>");
    static readonly PrintableConstant s_emptyFixedArray = new("FIXED_ARRAY_TYPE", null, "<FixedArray[0]>");

    public object RawString(object rawString) => rawString as string ?? rawString.ToString() ?? "";

    public object ConsString(object consString) => consString as string ?? consString.ToString() ?? "";

    public object BigInt(object bigint) =>
        new PrintableConstant("BIGINT_TYPE", null, "<BigInt " + bigint + ">");

    public object ScopeInfo(object scope) => new PrintableConstant("SCOPE_INFO_TYPE", null, "<ScopeInfo>");

    public object Singleton(SingletonConstant singleton) => singleton switch
    {
        SingletonConstant.AsyncIteratorSymbol => s_asyncIteratorSymbol,
        SingletonConstant.ClassFieldsSymbol => s_classFieldsSymbol,
        SingletonConstant.EmptyObjectBoilerplateDescription => s_emptyObjectBoilerplate,
        SingletonConstant.EmptyArrayBoilerplateDescription => s_emptyArrayBoilerplate,
        SingletonConstant.EmptyFixedArray => s_emptyFixedArray,
        SingletonConstant.IteratorSymbol => s_iteratorSymbol,
        SingletonConstant.InterpreterTrampolineSymbol => s_interpreterTrampolineSymbol,
        SingletonConstant.NaN => double.NaN,
        _ => throw new UnreachableException(),
    };

    public object HeapNumber(double value) => value;

    public object Smi(Smi value) => value;

    public object TheHole => s_theHole;

    public override string ToString() => nameof(DefaultConstantPoolMaterializer) + "@" +
                                         GetHashCode().ToString(CultureInfo.InvariantCulture);
}
