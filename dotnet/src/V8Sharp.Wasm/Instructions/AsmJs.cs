// V8Sharp: the interpreter's implementation of V8's asm.js compatibility
// opcodes (OpCodes/AsmJs.cs). The semantics are those of V8 14.7's compilers
// for these opcodes (src/wasm/turboshaft-graph-interface.cc, AsmJs* and the
// asm.js memory accesses; liftoff-compiler.cc for the same opcodes):
//  - i32.asmjs_div_s/u and rem_s/u never trap: x / 0 and x % 0 are 0,
//    kMinInt / -1 is kMinInt and x % -1 is 0 (JavaScript's (a / b) | 0).
//  - asmjs loads out of bounds read 0 (integers) or NaN (floats); asmjs stores
//    out of bounds do nothing; stores leave the stored value on the stack.
//  - i32.asmjs_convert_* are JavaScript's ToInt32 (ToUint32 has the same bits).
//  - f64.acos ... f64.pow are V8's Math functions (base::ieee754, math::pow),
//    f64.mod is fmod.

using System;
using System.Runtime.InteropServices;
using V8Sharp.Base;
using V8Sharp.Base.Numbers;
using Wacs.Core.OpCodes;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using Wacs.Core.Validation;

namespace Wacs.Core.Instructions
{
    public sealed class InstAsmJs : InstructionBase
    {
        public readonly AsmJsCode Code;
        private MemoryInstance? _memory;

        public InstAsmJs(AsmJsCode code) : base(new ByteCode(code), StackDiffOf(code)) => Code = code;

        public static bool IsDefined(AsmJsCode code) => code >= AsmJsCode.F64Acos && code <= AsmJsCode.I32AsmjsUConvertF64;

        static int StackDiffOf(AsmJsCode code) => code switch
        {
            AsmJsCode.F64Atan2 or AsmJsCode.F64Pow or AsmJsCode.F64Mod => -1,
            >= AsmJsCode.I32AsmjsDivS and <= AsmJsCode.I32AsmjsRemU => -1,
            >= AsmJsCode.I32AsmjsStoreMem8 and <= AsmJsCode.F64AsmjsStoreMem => -1,
            _ => 0,
        };

        /// <summary>The access size of an asm.js load or store, or 0.</summary>
        public static int AccessSize(AsmJsCode code) => code switch
        {
            AsmJsCode.I32AsmjsLoadMem8S or AsmJsCode.I32AsmjsLoadMem8U or AsmJsCode.I32AsmjsStoreMem8 => 1,
            AsmJsCode.I32AsmjsLoadMem16S or AsmJsCode.I32AsmjsLoadMem16U or AsmJsCode.I32AsmjsStoreMem16 => 2,
            AsmJsCode.I32AsmjsLoadMem or AsmJsCode.F32AsmjsLoadMem or AsmJsCode.I32AsmjsStoreMem
                or AsmJsCode.F32AsmjsStoreMem => 4,
            AsmJsCode.F64AsmjsLoadMem or AsmJsCode.F64AsmjsStoreMem => 8,
            _ => 0,
        };

        public override void Validate(IWasmValidationContext context)
        {
            int size = AccessSize(Code);
            if (size != 0)
            {
                context.Assert(context.Mems.Contains((MemIdx)0),
                    "Instruction {0} failed with invalid context memory {1}.", Op.GetMnemonic(), 0);
            }
            switch (Code)
            {
                case <= AsmJsCode.F64Log:
                    context.OpStack.PopType(ValType.F64);
                    context.OpStack.PushType(ValType.F64);
                    break;
                case AsmJsCode.F64Atan2 or AsmJsCode.F64Pow or AsmJsCode.F64Mod:
                    context.OpStack.PopType(ValType.F64);
                    context.OpStack.PopType(ValType.F64);
                    context.OpStack.PushType(ValType.F64);
                    break;
                case >= AsmJsCode.I32AsmjsDivS and <= AsmJsCode.I32AsmjsRemU:
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.I32);
                    break;
                case >= AsmJsCode.I32AsmjsLoadMem8S and <= AsmJsCode.I32AsmjsLoadMem:
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.I32);
                    break;
                case AsmJsCode.F32AsmjsLoadMem:
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.F32);
                    break;
                case AsmJsCode.F64AsmjsLoadMem:
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.F64);
                    break;
                case >= AsmJsCode.I32AsmjsStoreMem8 and <= AsmJsCode.I32AsmjsStoreMem:
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.I32);
                    break;
                case AsmJsCode.F32AsmjsStoreMem:
                    context.OpStack.PopType(ValType.F32);
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.F32);
                    break;
                case AsmJsCode.F64AsmjsStoreMem:
                    context.OpStack.PopType(ValType.F64);
                    context.OpStack.PopType(ValType.I32);
                    context.OpStack.PushType(ValType.F64);
                    break;
                case AsmJsCode.I32AsmjsSConvertF32 or AsmJsCode.I32AsmjsUConvertF32:
                    context.OpStack.PopType(ValType.F32);
                    context.OpStack.PushType(ValType.I32);
                    break;
                case AsmJsCode.I32AsmjsSConvertF64 or AsmJsCode.I32AsmjsUConvertF64:
                    context.OpStack.PopType(ValType.F64);
                    context.OpStack.PushType(ValType.I32);
                    break;
            }
        }

        public override InstructionBase Link(ExecContext context, int pointer)
        {
            if (AccessSize(Code) != 0) _memory = context.Store[context.Frame.Module.MemAddrs[(MemIdx)0]];
            return base.Link(context, pointer);
        }

        public override void Execute(ExecContext context)
        {
            OpStack stack = context.OpStack;
            switch (Code)
            {
                case <= AsmJsCode.F64Log:
                    stack.PushF64(Unary(Code, stack.PopF64()));
                    return;
                case AsmJsCode.F64Atan2 or AsmJsCode.F64Pow or AsmJsCode.F64Mod:
                {
                    double y = stack.PopF64();
                    double x = stack.PopF64();
                    stack.PushF64(Binary(Code, x, y));
                    return;
                }
                case >= AsmJsCode.I32AsmjsDivS and <= AsmJsCode.I32AsmjsRemU:
                {
                    int b = stack.PopI32();
                    int a = stack.PopI32();
                    stack.PushI32(Code switch
                    {
                        AsmJsCode.I32AsmjsDivS => DivS(a, b),
                        AsmJsCode.I32AsmjsDivU => (int)DivU((uint)a, (uint)b),
                        AsmJsCode.I32AsmjsRemS => RemS(a, b),
                        _ => (int)RemU((uint)a, (uint)b),
                    });
                    return;
                }
                case AsmJsCode.I32AsmjsSConvertF32 or AsmJsCode.I32AsmjsUConvertF32:
                    stack.PushI32(Conversions.DoubleToInt32(stack.PopF32()));
                    return;
                case AsmJsCode.I32AsmjsSConvertF64 or AsmJsCode.I32AsmjsUConvertF64:
                    stack.PushI32(Conversions.DoubleToInt32(stack.PopF64()));
                    return;
            }

            MemoryInstance memory = _memory!;
            switch (Code)
            {
                case >= AsmJsCode.I32AsmjsLoadMem8S and <= AsmJsCode.I32AsmjsLoadMem:
                    stack.PushI32(LoadI32(memory, Code, (uint)stack.PopI32()));
                    return;
                case AsmJsCode.F32AsmjsLoadMem:
                    stack.PushF32(LoadF32(memory, (uint)stack.PopI32()));
                    return;
                case AsmJsCode.F64AsmjsLoadMem:
                    stack.PushF64(LoadF64(memory, (uint)stack.PopI32()));
                    return;
                case >= AsmJsCode.I32AsmjsStoreMem8 and <= AsmJsCode.I32AsmjsStoreMem:
                {
                    int value = stack.PopI32();
                    StoreI32(memory, Code, (uint)stack.PopI32(), value);
                    stack.PushI32(value);
                    return;
                }
                case AsmJsCode.F32AsmjsStoreMem:
                {
                    float value = stack.PopF32();
                    StoreF32(memory, (uint)stack.PopI32(), value);
                    stack.PushF32(value);
                    return;
                }
                case AsmJsCode.F64AsmjsStoreMem:
                {
                    double value = stack.PopF64();
                    StoreF64(memory, (uint)stack.PopI32(), value);
                    stack.PushF64(value);
                    return;
                }
            }
        }

        // ---- The operations (also called by compiled code) ---------------------------

        public static int DivS(int a, int b)
        {
            if (b == 0) return 0;
            if (b == -1) return unchecked(-a);
            return a / b;
        }

        public static uint DivU(uint a, uint b) => b == 0 ? 0 : a / b;

        public static int RemS(int a, int b)
        {
            if (b == 0 || b == -1) return 0;
            return a % b;
        }

        public static uint RemU(uint a, uint b) => b == 0 ? 0 : a % b;

        public static double Unary(AsmJsCode code, double x) => code switch
        {
            AsmJsCode.F64Acos => Ieee754.acos(x),
            AsmJsCode.F64Asin => Ieee754.asin(x),
            AsmJsCode.F64Atan => Ieee754.atan(x),
            AsmJsCode.F64Cos => Ieee754.cos(x),
            AsmJsCode.F64Sin => Ieee754.sin(x),
            AsmJsCode.F64Tan => Ieee754.tan(x),
            AsmJsCode.F64Exp => Ieee754.exp(x),
            _ => Ieee754.log(x),
        };

        public static double Binary(AsmJsCode code, double x, double y) => code switch
        {
            AsmJsCode.F64Atan2 => Ieee754.atan2(x, y),
            AsmJsCode.F64Pow => InternalMath.pow(x, y),
            _ => x % y,
        };

        public static int LoadI32(MemoryInstance memory, AsmJsCode code, uint index)
        {
            int size = AccessSize(code);
            if ((ulong)index + (ulong)size > (ulong)memory.ByteLength) return 0;
            ref byte p = ref memory.Data[index];
            return code switch
            {
                AsmJsCode.I32AsmjsLoadMem8S => (sbyte)p,
                AsmJsCode.I32AsmjsLoadMem8U => p,
                AsmJsCode.I32AsmjsLoadMem16S => MemoryMarshal.Read<short>(memory.Data.AsSpan((int)index, 2)),
                AsmJsCode.I32AsmjsLoadMem16U => MemoryMarshal.Read<ushort>(memory.Data.AsSpan((int)index, 2)),
                _ => MemoryMarshal.Read<int>(memory.Data.AsSpan((int)index, 4)),
            };
        }

        public static float LoadF32(MemoryInstance memory, uint index) =>
            (ulong)index + 4 > (ulong)memory.ByteLength ? float.NaN : MemoryMarshal.Read<float>(memory.Data.AsSpan((int)index, 4));

        public static double LoadF64(MemoryInstance memory, uint index) =>
            (ulong)index + 8 > (ulong)memory.ByteLength ? double.NaN : MemoryMarshal.Read<double>(memory.Data.AsSpan((int)index, 8));

        public static void StoreI32(MemoryInstance memory, AsmJsCode code, uint index, int value)
        {
            int size = AccessSize(code);
            if ((ulong)index + (ulong)size > (ulong)memory.ByteLength) return;
            Span<byte> span = memory.Data.AsSpan((int)index, size);
            switch (size)
            {
                case 1: span[0] = (byte)value; break;
                case 2: MemoryMarshal.Write(span, (short)value); break;
                default: MemoryMarshal.Write(span, value); break;
            }
        }

        public static void StoreF32(MemoryInstance memory, uint index, float value)
        {
            if ((ulong)index + 4 > (ulong)memory.ByteLength) return;
            MemoryMarshal.Write(memory.Data.AsSpan((int)index, 4), value);
        }

        public static void StoreF64(MemoryInstance memory, uint index, double value)
        {
            if ((ulong)index + 8 > (ulong)memory.ByteLength) return;
            MemoryMarshal.Write(memory.Data.AsSpan((int)index, 8), value);
        }
    }
}
