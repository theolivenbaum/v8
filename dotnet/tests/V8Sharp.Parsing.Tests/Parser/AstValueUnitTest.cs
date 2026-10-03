// Port of test/unittests/parser/ast-value-unittest.cc.

using V8Sharp.Ast;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing.Tests.Parser;

public class AstValueTest
{
    private readonly AstValueFactory ast_value_factory_ = new();
    private readonly AstNodeFactory ast_node_factory_;

    public AstValueTest() => ast_node_factory_ = new AstNodeFactory(ast_value_factory_);

    private Literal NewBigInt(string str) => ast_node_factory_.NewBigIntLiteral(new AstBigInt(str), kNoSourcePosition);

    private AstRawString GetOneByteString(string str) => ast_value_factory_.GetOneByteString(str);

    [Fact]
    public void AstRawStringAsArrayIndex()
    {
        void test_as_array_index(string str, bool expected_success, uint expected_index)
        {
            AstRawString one_byte = GetOneByteString(str);
            Assert.True(one_byte.is_one_byte());
            Assert.Equal(expected_success, one_byte.AsArrayIndex(out uint index));
            if (expected_success)
            {
                Assert.Equal(expected_index, index);
            }
        }

        // Cached array index (length <= Name::kMaxCachedArrayIndexLength).
        test_as_array_index("0", true, 0);
        test_as_array_index("1", true, 1);
        test_as_array_index("1234567", true, 1234567);

        // Uncached array index (length > 7 and <= Name::kMaxArrayIndexSize).
        test_as_array_index("12345678", true, 12345678);
        test_as_array_index("1000000000", true, 1000000000);

        // Max valid array index (4294967294).
        test_as_array_index("4294967294", true, 4294967294u);

        // Overflow (length 10, > 4294967294).
        test_as_array_index("4294967295", false, 0);
        test_as_array_index("5000000000", false, 0);

        // Overflow (length > 10, > Name::kMaxArrayIndexSize).
        test_as_array_index("10000000000", false, 0);

        // Non-index strings.
        test_as_array_index("01", false, 0);
        test_as_array_index("foo", false, 0);
    }

    // V8 builds two-byte heap strings and passes them to
    // AstValueFactory::GetString(Tagged<String>); the port has no heap here,
    // so the string contents go through GetString(string), which is the path
    // heap strings take. The StringTable half of the V8 test (internalizing
    // two-byte strings) belongs to the object model and is not ported here.
    [Fact]
    public void GetStringNormalizesOneByteContent()
    {
        AstRawString raw = ast_value_factory_.GetString("1000000000");
        Assert.True(raw.is_one_byte());
        Assert.True(raw.AsArrayIndex(out uint index));
        Assert.Equal(1000000000u, index);

        AstRawString raw_non_one_byte = ast_value_factory_.GetString("ሴ");
        Assert.False(raw_non_one_byte.is_one_byte());
        Assert.False(raw_non_one_byte.AsArrayIndex(out _));
    }

    [Fact]
    public void BigIntToBooleanIsTrue()
    {
        Assert.False(NewBigInt("0").ToBooleanIsTrue());
        Assert.False(NewBigInt("0b0").ToBooleanIsTrue());
        Assert.False(NewBigInt("0o0").ToBooleanIsTrue());
        Assert.False(NewBigInt("0x0").ToBooleanIsTrue());
        Assert.False(NewBigInt("0b000").ToBooleanIsTrue());
        Assert.False(NewBigInt("0o00000").ToBooleanIsTrue());
        Assert.False(NewBigInt("0x000000000").ToBooleanIsTrue());

        Assert.True(NewBigInt("3").ToBooleanIsTrue());
        Assert.True(NewBigInt("0b1").ToBooleanIsTrue());
        Assert.True(NewBigInt("0o6").ToBooleanIsTrue());
        Assert.True(NewBigInt("0xA").ToBooleanIsTrue());
        Assert.True(NewBigInt("0b0000001").ToBooleanIsTrue());
        Assert.True(NewBigInt("0o00005000").ToBooleanIsTrue());
        Assert.True(NewBigInt("0x0000D00C0").ToBooleanIsTrue());
    }
}
