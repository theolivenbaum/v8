// Port of src/numbers/conversions.cc: StringToBigIntHelper (StringToBigInt,
// BigIntLiteral, BigIntLiteralToDecimal), producing sign + digits instead of
// a heap BigInt. The engine allocates its BigInt from the result.

using V8Sharp.Base.BigInts;

namespace V8Sharp.Base.Numbers;

public enum BigIntParseStatus
{
    /// <summary>The string is a valid BigInt; sign and digits are set.</summary>
    kOk,
    /// <summary>Not a valid StringIntegerLiteral (V8 returns an empty
    /// MaybeHandle; the caller throws a SyntaxError).</summary>
    kJunk,
    /// <summary>The value exceeds BigInt::kMaxLength digits (V8's State::kError).</summary>
    kMaxSizeExceeded,
}

public static partial class Conversions
{
    /// <summary>BigInt::kMaxLength on 64-bit: ((1 &lt;&lt; kMaxBitsBits) - 1) / 64 digits.</summary>
    public const int kBigIntMaxLength = ((1 << 30) - 1) / 64;

    [ThreadStatic] static Processor? t_processor;

    static Processor DefaultProcessor => t_processor ??= new Processor();

    /// <summary>
    /// https://tc39.es/ecma262/#sec-stringtobigint (the BigInt constructor
    /// and the == operator): "" => 0n; allows 0x/0o/0b prefixes, but no sign
    /// with them; no trailing junk. Digits are little-endian and normalized
    /// (zero is an empty array, never negative).
    /// </summary>
    public static BigIntParseStatus StringToBigInt(ReadOnlySpan<char> str, out bool negative, out ulong[] digits, Processor? processor = null) =>
        StringToBigIntHelper(str, literal: false, processor ?? DefaultProcessor, out negative, out digits);

    /// <summary>
    /// Parses a BigInt literal's text (without the trailing 'n'); the radix
    /// is inferred from a 0x / 0o / 0b prefix (case-insensitive).
    /// </summary>
    public static BigIntParseStatus BigIntLiteral(ReadOnlySpan<char> str, out bool negative, out ulong[] digits, Processor? processor = null) =>
        StringToBigIntHelper(str, literal: true, processor ?? DefaultProcessor, out negative, out digits);

    /// <summary>
    /// Converts a BigInt literal (as checked by the scanner) to its decimal
    /// string, e.g. "0x10" => "16".
    /// </summary>
    public static string BigIntLiteralToDecimal(ReadOnlySpan<char> literal, Processor? processor = null)
    {
        processor ??= DefaultProcessor;
        BigIntParseStatus status = StringToBigIntHelper(literal, literal: true, processor, out _, out ulong[] digits);
        if (status != BigIntParseStatus.kOk) throw new ArgumentException("invalid BigInt literal", nameof(literal));
        // Input may have been "0x0" or similar.
        if (digits.Length == 0) return "0";
        return processor.ToString(digits, 10, false);
    }

    static BigIntParseStatus StringToBigIntHelper(ReadOnlySpan<char> str, bool literal, Processor processor,
                                                  out bool negative, out ulong[] digits)
    {
        negative = false;
        digits = [];
        StringToIntHelper helper = new(str, 0);
        helper.AllowBinaryAndOctalPrefixes = true;
        // Used for StringToBigInt operation (BigInt constructor and == operator);
        // literals keep the default (trailing junk allowed: the scanner has
        // already checked them).
        if (!literal) helper.AllowTrailingJunk = false;
        helper.DetectRadix();

        FromStringAccumulator? accumulator = null;
        if (helper.State == IntParseState.kRunning)
        {
            // ParseInternal
            accumulator = new FromStringAccumulator(kBigIntMaxLength);
            int current = accumulator.Parse(str, helper.Cursor, (ulong)helper.Radix);
            if (accumulator.result == FromStringAccumulator.Result.kMaxSizeExceeded)
            {
                helper.State = IntParseState.kError;
            }
            else if (!helper.AllowTrailingJunk && AdvanceToNonspace(str, ref current))
            {
                helper.State = IntParseState.kJunk;
            }
            else
            {
                helper.State = IntParseState.kDone;
            }
        }

        if (!literal && helper.Sign != IntParseSign.kNone && helper.Radix != 10) return BigIntParseStatus.kJunk;
        if (helper.State == IntParseState.kEmpty)
        {
            Debug.Assert(!literal);
            helper.State = IntParseState.kZero;
        }
        switch (helper.State)
        {
            case IntParseState.kJunk:
                return BigIntParseStatus.kJunk;
            case IntParseState.kError:
                return BigIntParseStatus.kMaxSizeExceeded;
            case IntParseState.kZero:
                return BigIntParseStatus.kOk;
            case IntParseState.kDone:
            {
                ulong[] result = new ulong[accumulator!.ResultLength()];
                processor.FromString(result, accumulator);
                int len = Bigint.Normalize((ReadOnlySpan<ulong>)result).Length;
                digits = len == result.Length ? result : result.AsSpan(0, len).ToArray();
                negative = helper.Negative && len != 0;
                return BigIntParseStatus.kOk;
            }
            default:
                throw new UnreachableException();
        }
    }
}
