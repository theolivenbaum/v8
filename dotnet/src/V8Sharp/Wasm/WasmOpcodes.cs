// Port of src/wasm/wasm-opcodes.{h,cc}: the opcode encoding (prefixed
// opcodes as (prefix << 8) | index, or (prefix << 12) | index for indices
// above 0xff), names and signatures. The opcode list itself is generated
// from wasm-opcodes.h (WasmOpcodes.Generated.cs).
namespace V8Sharp.Wasm;

public static partial class WasmOpcodes
{
    /// <summary>
    /// V8 14.7's prefix of the asm.js compatibility opcodes (FOREACH_ASMJS_COMPAT_OPCODE),
    /// removed from this tree's wasm-opcodes.h with src/asmjs; kept for the
    /// asm.js translator (AsmJs/, deviations.md "asm.js").
    /// </summary>
    public const byte kAsmJsPrefix = 0xfa;
    public const byte kGCPrefix = 0xfb;
    public const byte kNumericPrefix = 0xfc;
    public const byte kSimdPrefix = 0xfd;
    public const byte kAtomicPrefix = 0xfe;

    static Dictionary<int, (string Signature, string Signature64, string Name)>? s_infoTable;
    static Dictionary<int, (string Signature, string Signature64, string Name)> s_info => s_infoTable ??= BuildInfo();

    static Dictionary<int, (string, string, string)> BuildInfo()
    {
        var info = new Dictionary<int, (string, string, string)>(s_opcodes.Length);
        foreach (var (opcode, sig, sig64, name) in s_opcodes) info.TryAdd((int)opcode, (sig, sig64, name));
        return info;
    }

    /// <summary>WasmOpcodes::IsPrefixOpcode.</summary>
    public static bool IsPrefixOpcode(byte b) => b is kGCPrefix or kNumericPrefix or kSimdPrefix or kAtomicPrefix;

    /// <summary>The internal encoding of a prefixed opcode (wasm-opcodes.h, FOREACH_PREFIX).</summary>
    public static WasmOpcode Prefixed(byte prefix, uint index) =>
        (WasmOpcode)(index > 0xff ? (prefix << 12) | (int)index : (prefix << 8) | (int)index);

    /// <summary>WasmOpcodes::OpcodeName.</summary>
    public static string OpcodeName(WasmOpcode opcode) =>
        s_info.TryGetValue((int)opcode, out var info) ? info.Name : "unknown";

    /// <summary>
    /// The signature of a simple opcode (V8's "x_yz" notation: result, then
    /// operands; i: i32, l: i64, f: f32, d: f64, s: s128, v: none), or null
    /// for opcodes whose operands depend on immediates.
    /// </summary>
    public static string? Signature(WasmOpcode opcode, bool memory64 = false)
    {
        if (!s_info.TryGetValue((int)opcode, out var info)) return null;
        string sig = memory64 && info.Signature64.Length > 0 ? info.Signature64 : info.Signature;
        return sig == "_" ? null : sig;
    }
}
