// Port of src/regexp/experimental/experimental-bytecode.h and
// experimental-bytecode.cc.
//
// The experimental bytecode describes a non-deterministic finite automaton. It
// runs on a multithreaded virtual machine (VM), i.e. in several threads
// concurrently. See the V8 header for the full description of the instruction
// semantics and of thread priorities.

using System.Globalization;
using System.Text;

namespace V8Sharp.RegExp.Experimental;

// Bytecode format.
// Currently very simple fixed-size: The opcode is encoded in the first 4
// bytes, the payload takes another 4 bytes.
public readonly struct Instruction
{
    public enum Opcode
    {
        ACCEPT,
        ASSERTION,
        CLEAR_REGISTER,
        CONSUME_RANGE,
        RANGE_COUNT,
        FORK,
        JMP,
        SET_REGISTER_TO_CP,
        SET_QUANTIFIER_TO_CLOCK,
        FILTER_QUANTIFIER,
        FILTER_GROUP,
        FILTER_LOOKAROUND,
        FILTER_CHILD,
        BEGIN_LOOP,
        END_LOOP,
        START_LOOKAROUND,
        END_LOOKAROUND,
        WRITE_LOOKAROUND_TABLE,
        READ_LOOKAROUND_TABLE,
    }

    public readonly struct LookaroundPayload
    {
        // IsPositive: bit 0; Type: bit 1; LookaroundIndex: bits 2..31.
        readonly uint _payload;

        public LookaroundPayload(uint raw) => _payload = raw;

        public LookaroundPayload(uint lookaroundIndex, bool isPositive, RegExpLookaround.Type type) =>
            _payload = (lookaroundIndex << 2) | ((uint)type << 1) | (isPositive ? 1u : 0u);

        public int Index => (int)(_payload >> 2);
        public bool IsPositive => (_payload & 1) != 0;
        public RegExpLookaround.Type Type => (RegExpLookaround.Type)((_payload >> 1) & 1);
        public uint Raw => _payload;

        public override string ToString() =>
            $"{Index} ({(Type == RegExpLookaround.Type.LOOKAHEAD ? "ahead" : "behind")}, " +
            $"{(IsPositive ? "positive" : "negative")})";
    }

    public readonly Opcode opcode;
    // union payload (4 bytes).
    readonly int _payload;

    Instruction(Opcode op, int payload)
    {
        opcode = op;
        _payload = payload;
    }

    // Payload of CONSUME_RANGE: min in the low half, max in the high half.
    public char ConsumeRangeMin => (char)(_payload & 0xffff);
    public char ConsumeRangeMax => (char)((uint)_payload >> 16);
    // Payload of RANGE_COUNT
    public int NumRanges => _payload;
    // Payload of FORK, JMP and FILTER_CHILD, the next/forked program counter:
    public int Pc => _payload;
    // Payload of SET_REGISTER_TO_CP and CLEAR_REGISTER:
    public int RegisterIndex => _payload;
    // Payload of ASSERTION:
    public RegExpAssertion.Type AssertionType => (RegExpAssertion.Type)_payload;
    // Payload of SET_QUANTIFIER_TO_CLOCK and FILTER_QUANTIFIER:
    public int QuantifierId => _payload;
    // Payload of FILTER_GROUP:
    public int GroupId => _payload;
    // Payload of WRITE_LOOKAROUND_TABLE and FILTER_LOOKAROUND:
    public int LookaroundId => _payload;
    // Payload of READ_LOOKAROUND_TABLE and START_LOOKAROUND:
    public LookaroundPayload Lookaround => new((uint)_payload);

    internal Instruction WithPc(int pc) => new(opcode, pc);

    public static Instruction ConsumeRange(char min, char max) => new(Opcode.CONSUME_RANGE, min | (max << 16));
    public static Instruction ConsumeAnyChar() => ConsumeRange('\0', '￿');
    // This is encoded as the empty CONSUME_RANGE of characters 0xFFFF <= c <=
    // 0x0000.
    public static Instruction Fail() => ConsumeRange('￿', '\0');
    public static Instruction RangeCount(int numRanges) => new(Opcode.RANGE_COUNT, numRanges);
    public static Instruction Fork(int altIndex) => new(Opcode.FORK, altIndex);
    public static Instruction Jmp(int altIndex) => new(Opcode.JMP, altIndex);
    public static Instruction Accept() => new(Opcode.ACCEPT, 0);
    public static Instruction SetRegisterToCp(int registerIndex) => new(Opcode.SET_REGISTER_TO_CP, registerIndex);
    public static Instruction Assertion(RegExpAssertion.Type t) => new(Opcode.ASSERTION, (int)t);
    public static Instruction ClearRegister(int registerIndex) => new(Opcode.CLEAR_REGISTER, registerIndex);
    public static Instruction SetQuantifierToClock(int quantifierId) =>
        new(Opcode.SET_QUANTIFIER_TO_CLOCK, quantifierId);
    public static Instruction FilterQuantifier(int quantifierId) => new(Opcode.FILTER_QUANTIFIER, quantifierId);
    public static Instruction FilterGroup(int groupId) => new(Opcode.FILTER_GROUP, groupId);
    public static Instruction FilterLookaround(int lookaroundId) => new(Opcode.FILTER_LOOKAROUND, lookaroundId);
    public static Instruction FilterChild(int pc) => new(Opcode.FILTER_CHILD, pc);
    public static Instruction BeginLoop() => new(Opcode.BEGIN_LOOP, 0);
    public static Instruction EndLoop() => new(Opcode.END_LOOP, 0);
    public static Instruction StartLookaround(int lookaroundIndex, bool isPositive, RegExpLookaround.Type type) =>
        new(Opcode.START_LOOKAROUND, (int)new LookaroundPayload((uint)lookaroundIndex, isPositive, type).Raw);
    public static Instruction EndLookaround() => new(Opcode.END_LOOKAROUND, 0);
    public static Instruction WriteLookTable(int index) => new(Opcode.WRITE_LOOKAROUND_TABLE, index);
    public static Instruction ReadLookTable(int index, bool isPositive, RegExpLookaround.Type type) =>
        new(Opcode.READ_LOOKAROUND_TABLE, (int)new LookaroundPayload((uint)index, isPositive, type).Raw);

    // Returns whether an instruction is `FILTER_GROUP`, `FILTER_QUANTIFIER` or
    // `FILTER_CHILD`.
    public static bool IsFilter(in Instruction instruction) =>
        instruction.opcode is Opcode.FILTER_GROUP or Opcode.FILTER_QUANTIFIER or Opcode.FILTER_CHILD;

    static string AsciiOrHex(char c) =>
        c < 128 && c >= 0x20 && c < 0x7f ? c.ToString() : "0x" + ((int)c).ToString("x", CultureInfo.InvariantCulture);

    public override string ToString()
    {
        switch (opcode)
        {
            case Opcode.CONSUME_RANGE:
                return $"CONSUME_RANGE [{AsciiOrHex(ConsumeRangeMin)}, {AsciiOrHex(ConsumeRangeMax)}]";
            case Opcode.RANGE_COUNT:
                return $"RANGE_COUNT {NumRanges}";
            case Opcode.ASSERTION:
                return "ASSERTION " + AssertionType switch
                {
                    RegExpAssertion.Type.START_OF_INPUT => "START_OF_INPUT",
                    RegExpAssertion.Type.END_OF_INPUT => "END_OF_INPUT",
                    RegExpAssertion.Type.START_OF_LINE => "START_OF_LINE",
                    RegExpAssertion.Type.END_OF_LINE => "END_OF_LINE",
                    RegExpAssertion.Type.BOUNDARY => "BOUNDARY",
                    RegExpAssertion.Type.NON_BOUNDARY => "NON_BOUNDARY",
                    _ => throw new InvalidOperationException("UNREACHABLE"),
                };
            case Opcode.FORK: return $"FORK {Pc}";
            case Opcode.JMP: return $"JMP {Pc}";
            case Opcode.ACCEPT: return "ACCEPT";
            case Opcode.SET_REGISTER_TO_CP: return $"SET_REGISTER_TO_CP {RegisterIndex}";
            case Opcode.CLEAR_REGISTER: return $"CLEAR_REGISTER {RegisterIndex}";
            case Opcode.SET_QUANTIFIER_TO_CLOCK: return $"SET_QUANTIFIER_TO_CLOCK {QuantifierId}";
            case Opcode.FILTER_QUANTIFIER: return $"FILTER_QUANTIFIER {QuantifierId}";
            case Opcode.FILTER_GROUP: return $"FILTER_GROUP {GroupId}";
            case Opcode.FILTER_LOOKAROUND: return $"FILTER_LOOKAROUND {LookaroundId}";
            case Opcode.FILTER_CHILD: return $"FILTER_CHILD {Pc}";
            case Opcode.BEGIN_LOOP: return "BEGIN_LOOP";
            case Opcode.END_LOOP: return "END_LOOP";
            case Opcode.START_LOOKAROUND: return $"START_LOOKAROUND {Lookaround}";
            case Opcode.END_LOOKAROUND: return "END_LOOKAROUND";
            case Opcode.WRITE_LOOKAROUND_TABLE: return $"WRITE_LOOKAROUND_TABLE {LookaroundId}";
            case Opcode.READ_LOOKAROUND_TABLE: return $"READ_LOOKAROUND_TABLE {Lookaround}";
        }
        return "";
    }

    // The maximum number of digits required to display a non-negative number < n
    // in base 10.
    static int DigitsRequiredBelow(int n)
    {
        int result = 1;
        for (int i = 10; i < n; i *= 10) result += 1;
        return result;
    }

    public static string Print(ReadOnlySpan<Instruction> insts)
    {
        var sb = new StringBuilder();
        int lineDigitNum = DigitsRequiredBelow(insts.Length);
        for (int i = 0; i != insts.Length; ++i)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(lineDigitNum, '0'));
            sb.Append(": ").Append(insts[i].ToString()).Append('\n');
        }
        return sb.ToString();
    }
}
