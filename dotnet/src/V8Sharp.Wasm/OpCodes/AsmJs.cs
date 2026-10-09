// V8Sharp: V8's asm.js compatibility opcodes (FOREACH_ASMJS_COMPAT_OPCODE in
// src/wasm/wasm-opcodes.h of V8 14.7). They are not part of WebAssembly: V8
// emits them only when it translates asm.js (src/asmjs) and its decoder
// accepts them only in modules of asm.js origin. They give asm.js's JavaScript
// semantics (division by zero is 0, out-of-bounds loads read 0 or NaN and
// stores do nothing, float-to-int conversions are ToInt32) and the
// Math functions without wasm equivalents. Encoded as the prefix 0xFA and an
// LEB128 index (below 0x80, so one byte).

using Wacs.Core.Attributes;

namespace Wacs.Core.OpCodes
{
    public enum AsmJsCode : byte
    {
        [OpCode("f64.acos")]                F64Acos             = 0x3c,
        [OpCode("f64.asin")]                F64Asin             = 0x3d,
        [OpCode("f64.atan")]                F64Atan             = 0x3e,
        [OpCode("f64.cos")]                 F64Cos              = 0x3f,
        [OpCode("f64.sin")]                 F64Sin              = 0x40,
        [OpCode("f64.tan")]                 F64Tan              = 0x41,
        [OpCode("f64.exp")]                 F64Exp              = 0x42,
        [OpCode("f64.log")]                 F64Log              = 0x43,
        [OpCode("f64.atan2")]               F64Atan2            = 0x44,
        [OpCode("f64.pow")]                 F64Pow              = 0x45,
        [OpCode("f64.mod")]                 F64Mod              = 0x46,
        [OpCode("i32.asmjs_div_s")]         I32AsmjsDivS        = 0x47,
        [OpCode("i32.asmjs_div_u")]         I32AsmjsDivU        = 0x48,
        [OpCode("i32.asmjs_rem_s")]         I32AsmjsRemS        = 0x49,
        [OpCode("i32.asmjs_rem_u")]         I32AsmjsRemU        = 0x4a,
        [OpCode("i32.asmjs_load8_s")]       I32AsmjsLoadMem8S   = 0x4b,
        [OpCode("i32.asmjs_load8_u")]       I32AsmjsLoadMem8U   = 0x4c,
        [OpCode("i32.asmjs_load16_s")]      I32AsmjsLoadMem16S  = 0x4d,
        [OpCode("i32.asmjs_load16_u")]      I32AsmjsLoadMem16U  = 0x4e,
        [OpCode("i32.asmjs_load32")]        I32AsmjsLoadMem     = 0x4f,
        [OpCode("f32.asmjs_load")]          F32AsmjsLoadMem     = 0x50,
        [OpCode("f64.asmjs_load")]          F64AsmjsLoadMem     = 0x51,
        [OpCode("i32.asmjs_store8")]        I32AsmjsStoreMem8   = 0x52,
        [OpCode("i32.asmjs_store16")]       I32AsmjsStoreMem16  = 0x53,
        [OpCode("i32.asmjs_store")]         I32AsmjsStoreMem    = 0x54,
        [OpCode("f32.asmjs_store")]         F32AsmjsStoreMem    = 0x55,
        [OpCode("f64.asmjs_store")]         F64AsmjsStoreMem    = 0x56,
        [OpCode("i32.asmjs_convert_f32_s")] I32AsmjsSConvertF32 = 0x57,
        [OpCode("i32.asmjs_convert_f32_u")] I32AsmjsUConvertF32 = 0x58,
        [OpCode("i32.asmjs_convert_f64_s")] I32AsmjsSConvertF64 = 0x59,
        [OpCode("i32.asmjs_convert_f64_u")] I32AsmjsUConvertF64 = 0x5a,
    }
}
