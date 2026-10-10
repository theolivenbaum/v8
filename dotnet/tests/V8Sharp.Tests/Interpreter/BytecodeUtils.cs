// Port of the operand macros of test/unittests/interpreter/bytecode-utils.h
// (little-endian: V8_TARGET_LITTLE_ENDIAN).
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

static class B
{
    public static byte U8(int i) => (byte)i;
    public static byte[] U16(int i) => [(byte)i, (byte)(i >> 8)];
    public static byte[] U32(int i) => [(byte)i, (byte)(i >> 8), (byte)(i >> 16), (byte)(i >> 24)];

    // REG_OPERAND(i) = InterpreterFrameConstants::kRegisterFileFromFp / kSystemPointerSize - i.
    public static int RegOperand(int i) => Register.kRegisterFileStartOffset - i;
    public static byte R8(int i) => (byte)RegOperand(i);
    public static byte[] R16(int i) => U16(RegOperand(i));
    public static byte[] R32(int i) => U32(RegOperand(i));

    public static byte Op(Bytecode bytecode) => (byte)bytecode;

    public static uint R(int i) => (uint)new Register(i).ToOperand();
}
