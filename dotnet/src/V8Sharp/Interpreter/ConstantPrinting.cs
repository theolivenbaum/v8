// V8's printing of constant-pool values, as used by the disassembler
// (Brief(), src/diagnostics/objects-printer.cc HeapObjectShortPrint) and by the
// bytecode expectations printer (PrintConstant, PrintV8String).
using System.Globalization;
using System.Text;

namespace V8Sharp.Interpreter;

public static class ConstantPrinting
{
    /// <summary>PrintDouble (objects-printer.cc): -0.0, integers in the safe
    /// range as "n.0", otherwise ostream's default format (%g, precision 6).</summary>
    public static string PrintDouble(double val)
    {
        if (double.IsNegative(val) && val == 0) return "-0.0";
        const double kMaxSafeInteger = 9007199254740991.0;
        if (val == Math.Truncate(val) && val >= -kMaxSafeInteger && val <= kMaxSafeInteger)
        {
            return ((long)val).ToString(CultureInfo.InvariantCulture) + ".0";
        }
        if (double.IsNaN(val))
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(val);
            return (double.IsNegative(val) ? "-nan" : "nan") + " (0x" + bits.ToString("x", CultureInfo.InvariantCulture) + ")";
        }
        return FormatG6(val);
    }

    /// <summary>C++ ostream default floating point output (printf "%g" with precision 6).</summary>
    public static string FormatG6(double val)
    {
        if (double.IsPositiveInfinity(val)) return "inf";
        if (double.IsNegativeInfinity(val)) return "-inf";
        if (double.IsNaN(val)) return "nan";
        // .NET's "G6" follows the same rule as %g for choosing exponential
        // notation; it differs only in the exponent letter case.
        string s = val.ToString("G6", CultureInfo.InvariantCulture);
        return s.Replace('E', 'e');
    }

    /// <summary>Brief(obj) for a constant-pool entry.</summary>
    public static string Brief(object? constant) => constant switch
    {
        Smi smi => smi.Value.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => "<HeapNumber " + PrintDouble(d) + ">",
        string s => "#" + s,
        IPrintableConstant p => p.Brief(),
        null => "<null>",
        _ => constant.ToString() ?? "",
    };

    /// <summary>BytecodeExpectationsPrinter::PrintV8String: '"' + each code unit
    /// as AsEscapedUC16ForJSON + '"'.</summary>
    public static void PrintV8String(StringBuilder stream, string str)
    {
        stream.Append('"');
        foreach (char c in str) AppendEscapedUC16ForJSON(stream, c);
        stream.Append('"');
    }

    // operator<<(AsEscapedUC16ForJSON) in src/utils/ostreams.cc.
    static void AppendEscapedUC16ForJSON(StringBuilder os, char c)
    {
        switch (c)
        {
            case '\n': os.Append("\\n"); return;
            case '\r': os.Append("\\r"); return;
            case '\t': os.Append("\\t"); return;
            case '"': os.Append("\\\""); return;
        }
        // PrintUC16ForJSON with IsOK: printable ASCII or whitespace, but not '\\'.
        bool is_print = c >= 0x20 && c <= 0x7E;
        bool is_space = (c >= 0x9 && c <= 0xD) || c == 0x20;
        if ((is_print || is_space) && c != '\\')
        {
            os.Append(c);
        }
        else
        {
            os.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>BytecodeExpectationsPrinter::PrintConstant: "Smi [n]", or the
    /// instance type name followed by " [value]" for heap numbers and strings.</summary>
    public static void PrintConstant(StringBuilder stream, object constant)
    {
        switch (constant)
        {
            case Smi smi:
                stream.Append("Smi [").Append(smi.Value.ToString(CultureInfo.InvariantCulture)).Append(']');
                break;
            case double d:
                stream.Append("HEAP_NUMBER_TYPE [").Append(PrintDouble(d)).Append(']');
                break;
            case string s:
                stream.Append(StringInstanceTypeName(s)).Append(" [");
                PrintV8String(stream, s);
                stream.Append(']');
                break;
            case IPrintableConstant p:
                stream.Append(p.InstanceTypeName);
                if (p.PrintedValue is { } value) stream.Append(" [").Append(value).Append(']');
                break;
            default:
                stream.Append(constant.ToString());
                break;
        }
    }

    /// <summary>The instance type of an internalized string constant: one-byte
    /// when every code unit fits in Latin-1, as V8 represents it.</summary>
    public static string StringInstanceTypeName(string s)
    {
        foreach (char c in s)
        {
            if (c > 0xFF) return "INTERNALIZED_TWO_BYTE_STRING_TYPE";
        }
        return "INTERNALIZED_ONE_BYTE_STRING_TYPE";
    }
}
