// Tests of Uint8Array.fromBase64/fromHex and the base64/hex prototype methods
// (builtins-typed-array.cc); results and error messages taken from V8 (the
// oracle).
using V8Sharp.Objects;

namespace V8Sharp.Tests.Builtins;

public class TypedArrayBase64Test : BuiltinsTestBase
{
    const string kInvalid = "SyntaxError: Found a character that cannot be part of a valid base64 string.";
    const string kRemainder = "SyntaxError: The base64 input terminates with a single character, excluding padding (=).";
    const string kExtraBits = "SyntaxError: The base64 input terminates with non-zero padding bits.";

    JSObject Options(string key, string value)
    {
        JSObject o = Obj();
        Set(o, key, S(value));
        return o;
    }

    string FromBase64(string input, string lastChunkHandling)
    {
        try
        {
            JSValue r = CallPath("Uint8Array.fromBase64", S(input), Options("lastChunkHandling", lastChunkHandling));
            return Str(Invoke(r, "join"));
        }
        catch (JavaScriptException)
        {
            return Throws(() => CallPath("Uint8Array.fromBase64", S(input), Options("lastChunkHandling", lastChunkHandling)));
        }
    }

    [Theory]
    [InlineData("=", "loose", kInvalid)]
    [InlineData("A==", "loose", kRemainder)]
    [InlineData("AAA==", "loose", kInvalid)]
    [InlineData("AB=\n=", "loose", "0")]
    [InlineData("AB=x", "loose", kInvalid)]
    [InlineData("AAA= ", "loose", "0,0")]
    [InlineData("AAAA AA", "loose", "0,0,0,0")]
    [InlineData("AAAA\tAAA=", "loose", "0,0,0,0,0")]
    [InlineData("AB\u00a0", "loose", kInvalid)]
    [InlineData("AB ", "loose", "0")]
    [InlineData("ABC　", "loose", kInvalid)]
    [InlineData("AB=\f", "loose", kInvalid)]
    [InlineData("AAAAA", "loose", kRemainder)]
    [InlineData("AAAAAA=", "loose", kInvalid)]
    [InlineData("AB=\n=", "strict", kExtraBits)]
    [InlineData("AAAA AA", "strict", kRemainder)]
    [InlineData("AB=\f", "strict", kRemainder)]
    [InlineData("AAAAAA=", "strict", kRemainder)]
    [InlineData("AAA= ", "strict", "0,0")]
    [InlineData("AB=\n=", "stop-before-partial", "0")]
    [InlineData("AAAA AA", "stop-before-partial", "0,0,0")]
    [InlineData("AB=\f", "stop-before-partial", "")]
    [InlineData("AAAAA", "stop-before-partial", "0,0,0")]
    [InlineData("AAAAA=", "stop-before-partial", kRemainder)]
    public void FromBase64Cases(string input, string lastChunkHandling, string expected) =>
        Assert.Equal(expected, FromBase64(input, lastChunkHandling));

    string SetFromBase64(string input, int length, JSValue options)
    {
        JSValue u = New("Uint8Array", N(length));
        string result;
        try
        {
            JSValue r = Invoke(u, "setFromBase64", S(input), options);
            result = "read " + Num(Get(r, "read")) + " written " + Num(Get(r, "written"));
        }
        catch (JavaScriptException)
        {
            result = Throws(() => Invoke(u, "setFromBase64", S(input), options));
        }
        return result + " | " + Str(Invoke(u, "join"));
    }

    [Fact]
    public void SetFromBase64Cases()
    {
        Assert.Equal("read 0 written 0 | 0", SetFromBase64("Zm9vYmFy", 1, Undefined));
        Assert.Equal("read 8 written 6 | 102,111,111,98,97,114,0", SetFromBase64("Zm9vYmFy", 7, Undefined));
        Assert.Equal("read 8 written 4 | 102,111,111,98,0", SetFromBase64("Zm9vYg==", 5, Undefined));
        Assert.Equal("read 4 written 3 | 102,111,111,0", SetFromBase64("Zm9vYmE=", 4, Undefined));
        Assert.Equal("read 12 written 5 | 102,111,111,98,97", SetFromBase64(" Zm9v Ym E= ", 5, Undefined));
        Assert.Equal(kRemainder + " | 102,111,111,0", SetFromBase64("Zm9vYmE", 4, Options("lastChunkHandling", "strict")));
        Assert.Equal("read 4 written 3 | 102,111,111,0,0",
            SetFromBase64("Zm9vYmE", 5, Options("lastChunkHandling", "stop-before-partial")));
        Assert.Equal(kInvalid + " | 102,111,111", SetFromBase64("Zm9v!", 3, Undefined));
        Assert.Equal(kRemainder + " | 102,111,111,0", SetFromBase64("Zm9vA", 4, Undefined));
        Assert.Equal("read 0 written 0 | 0", SetFromBase64("Zm9v!", 1, Undefined));

        // Past the point where the proposal's FromBase64 stops, V8 (simdutf) keeps
        // parsing chunk by chunk.
        JSValue strict = Options("lastChunkHandling", "strict");
        JSValue stop = Options("lastChunkHandling", "stop-before-partial");
        Assert.Equal(kInvalid + " | 102,111,111", SetFromBase64("Zm9vYmF!", 3, Undefined));
        Assert.Equal(kInvalid + " | 102,111,111", SetFromBase64("Zm9vY!", 3, Undefined));
        Assert.Equal(kInvalid + " | 102,111,111,0", SetFromBase64("Zm9vYm!y", 4, Undefined));
        Assert.Equal(kInvalid + " | 102,111,111,0", SetFromBase64("Zm9vYmE!", 4, Undefined));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vYmFyYmF6", 3, Undefined));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vYmFyYmF!", 3, Undefined));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vYmFyYmF", 3, strict));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vYmFyYmF=", 3, strict));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vYmFyYm=", 3, stop));
        Assert.Equal(kInvalid + " | 102,111,111,98,97,114", SetFromBase64("Zm9vYmFyYmE!", 6, Undefined));
        Assert.Equal("read 8 written 6 | 102,111,111,98,97,114,0", SetFromBase64("Zm9vYmFyYmE", 7, Undefined));
        Assert.Equal("read 11 written 8 | 102,111,111,98,97,114,98,97", SetFromBase64("Zm9vYmFyYmE", 8, Undefined));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vA", 3, stop));
        Assert.Equal(kInvalid + " | 102,111,111", SetFromBase64("Zm9v====", 3, Undefined));
        Assert.Equal(kRemainder + " | 102,111,111", SetFromBase64("Zm9v A", 3, Undefined));
        Assert.Equal("read 7 written 3 | 102,111,111", SetFromBase64("Zm9v   ", 3, Undefined));
        Assert.Equal("read 4 written 3 | 102,111,111", SetFromBase64("Zm9vYg==", 3, strict));
        Assert.Equal(kExtraBits + " | 102,111,111", SetFromBase64("Zm9vYh==", 3, strict));
        Assert.Equal(kInvalid + " | 102,111,111", SetFromBase64("Zm9vYg=", 3, Undefined));
        Assert.Equal(kInvalid + " | 102,111,111,0", SetFromBase64("Zm9vYg=", 4, Undefined));
    }

    [Fact]
    public void ToBase64AndHex()
    {
        JSValue bytes = New("Uint8Array", Arr(102, 111, 111, 98, 251, 255));
        Assert.Equal("Zm9vYvv/", Str(Invoke(bytes, "toBase64")));
        Assert.Equal("Zm9vYvv_", Str(Invoke(bytes, "toBase64", Options("alphabet", "base64url"))));
        JSObject omit = Obj();
        Set(omit, "omitPadding", N(1));
        Assert.Equal("AQ", Str(Invoke(New("Uint8Array", Arr(1)), "toBase64", omit)));
        Assert.Equal("", Str(Invoke(New("Uint8Array", N(0)), "toBase64")));
        Assert.Equal("666f6f62fbff", Str(Invoke(bytes, "toHex")));
        Assert.Equal("1,171", Str(Invoke(CallPath("Uint8Array.fromHex", S("01aB")), "join")));

        JSObject badAlphabet = Obj();
        Set(badAlphabet, "alphabet", N(1));
        Assert.Equal("TypeError: invalid option 1", Throws(() => Invoke(New("Uint8Array", N(1)), "toBase64", badAlphabet)));
        Assert.Equal("SyntaxError: Input string must contain hex characters in even length",
            Throws(() => Invoke(New("Uint8Array", N(1)), "setFromHex", S("0g"))));
        JSValue proto = Get(Global("Uint8Array"), "prototype");
        Assert.Equal("TypeError: Method Uint8Array.prototype.setFromHex called on incompatible receiver undefined",
            Throws(() => Call(Get(proto, "setFromHex"), New("Int8Array", N(1)), S("00"))));
        Assert.Equal("TypeError: Method Uint8Array.prototype.toBase64 called on incompatible receiver 1",
            Throws(() => Call(Get(proto, "toBase64"), N(1))));
    }
}
