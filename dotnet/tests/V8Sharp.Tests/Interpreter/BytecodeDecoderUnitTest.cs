// Port of test/unittests/interpreter/bytecode-decoder-unittest.cc.
using System.Globalization;
using System.Text;
using V8Sharp.Interpreter;
using static V8Sharp.Tests.Interpreter.B;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeDecoderUnitTest
{
    [Fact]
    public void BytecodeDecoderTest_DecodeBytecodeAndOperands()
    {
        (byte[] Bytecode, string Output)[] cases =
        [
            ([Op(Bytecode.LdaSmi), U8(1)], "            LdaSmi [1]"),
            ([Op(Bytecode.Wide), Op(Bytecode.LdaSmi), .. U16(1000)], "      LdaSmi.Wide [1000]"),
            ([Op(Bytecode.ExtraWide), Op(Bytecode.LdaSmi), .. U32(100000)], "LdaSmi.ExtraWide [100000]"),
            ([Op(Bytecode.LdaSmi), U8(-1)], "            LdaSmi [-1]"),
            ([Op(Bytecode.Wide), Op(Bytecode.LdaSmi), .. U16(-1000)], "      LdaSmi.Wide [-1000]"),
            ([Op(Bytecode.ExtraWide), Op(Bytecode.LdaSmi), .. U32(-100000)], "LdaSmi.ExtraWide [-100000]"),
            ([Op(Bytecode.Star), R8(5)], "            Star r5"),
            ([Op(Bytecode.Wide), Op(Bytecode.Star), .. R16(136)], "      Star.Wide r136"),
            ([Op(Bytecode.Wide), Op(Bytecode.CallAnyReceiver), .. R16(134), .. R16(135), .. U16(10), .. U16(177)],
             "CallAnyReceiver.Wide r134, r135-r144, FBV[177]"),
            ([Op(Bytecode.ForInPrepare), R8(10), U8(11)], "         ForInPrepare r10-r12, FBV[11]"),
            ([Op(Bytecode.CallRuntime), .. U16((int)RuntimeFunctionId.IsSmi), R8(0), U8(0)],
             "   CallRuntime [IsSmi], r0-r0"),
            ([Op(Bytecode.Ldar), (byte)Register.FromParameterIndex(2).ToOperand()], "            Ldar a1"),
            ([Op(Bytecode.Wide), Op(Bytecode.CreateObjectLiteral), .. U16(513), .. U16(1027), U8(165)],
             "CreateObjectLiteral.Wide [513:0], FBV[1027], #a5"),
            ([Op(Bytecode.ExtraWide), Op(Bytecode.JumpIfNull), .. U32(123456789)], "JumpIfNull.ExtraWide [123456789]"),
            ([Op(Bytecode.CallJSRuntime), U8(NativeContextFields.BOOLEAN_FUNCTION_INDEX), R8(0), U8(0)],
             "      CallJSRuntime [boolean_function], r0-r0"),
            ([Op(Bytecode.TestEqualStrict), R8(0), U8(1)], "         TestEqualStrict r0, EmbeddedFeedback[Boolean]"),
            ([Op(Bytecode.Add), R8(0), U8(1)], "         Add r0, EmbeddedFeedback[SignedSmall]"),
            ([Op(Bytecode.Inc), U8(1)], "            Inc EmbeddedFeedback[SignedSmall]"),
        ];

        // V8's NewTrustedFixedArray(2000) is filled with Smi zero.
        var constant_pool = new object[2000];
        Array.Fill(constant_pool, Smi.Zero);

        foreach (var c in cases)
        {
            // Generate reference string by prepending formatted bytes.
            var expected_ss = new StringBuilder();
            foreach (byte b in c.Bytecode) expected_ss.Append(b.ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
            expected_ss.Append(c.Output);

            // Generate decoded byte output.
            string actual = BytecodeDecoder.Decode(c.Bytecode, constant_pool);

            // Compare.
            Assert.Equal(expected_ss.ToString(), actual);
        }
    }
}
