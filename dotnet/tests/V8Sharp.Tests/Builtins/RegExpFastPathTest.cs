// Tests of the fast-path checks of the RegExp builtins (BranchIfFastRegExp,
// regexp::Utils::IsUnmodifiedRegExp) and of the slow paths they select.
namespace V8Sharp.Tests.Builtins;

public class RegExpFastPathTest : BuiltinsTestBase
{
    [Fact]
    public void UnmodifiedRegExpIsFast()
    {
        JSValue re = Re("a", "g");
        Assert.True(BuiltinsRegExp.IsFastRegExpStrict(isolate, re));
        Assert.True(BuiltinsRegExp.IsFastRegExpPermissive(isolate, re));
        Assert.True(BuiltinsRegExp.IsFastRegExpForMatch(isolate, re));
        Assert.True(BuiltinsRegExp.IsFastRegExpForSearch(isolate, re));
        Assert.True(BuiltinsRegExp.IsUnmodifiedRegExp(isolate, re));
    }

    [Fact]
    public void NonSmiLastIndexIsSlow()
    {
        JSValue re = Re("a", "g");
        Set(re, "lastIndex", S("1"));
        Assert.False(BuiltinsRegExp.IsFastRegExpPermissive(isolate, re));
        Assert.False(BuiltinsRegExp.IsFastRegExpNoPrototype(isolate, re));
    }

    [Fact]
    public void OwnExecIsSlow()
    {
        JSValue re = Re("a", "g");
        Set(re, "exec", Get(RegExpProto, "exec"));
        Assert.False(BuiltinsRegExp.IsFastRegExpPermissive(isolate, re));
    }

    [Fact]
    public void ReplacedPrototypeExecIsSlow()
    {
        JSValue re = Re("a", "g");
        Set(RegExpProto, "exec", Get(StringProto, "toString"));
        Assert.False(BuiltinsRegExp.IsFastRegExpStrict(isolate, re));
        Assert.False(BuiltinsRegExp.IsFastRegExpPermissive(isolate, re));
        Assert.False(BuiltinsRegExp.IsUnmodifiedRegExp(isolate, re));
    }

    [Fact]
    public void RestoredPrototypeExecIsFastForPermissive()
    {
        JSValue re = Re("a", "g");
        JSValue exec = Get(RegExpProto, "exec");
        Set(RegExpProto, "exec", Get(StringProto, "toString"));
        Set(RegExpProto, "exec", exec);
        // The identity check accepts the original value again.
        Assert.True(BuiltinsRegExp.IsFastRegExpPermissive(isolate, re));
    }

    [Fact]
    public void ForceSlowPathFlag()
    {
        isolate.Flags.force_slow_path = true;
        JSValue re = Re("(a)", "g");
        Assert.False(BuiltinsRegExp.IsFastRegExpPermissive(isolate, re));
        Assert.Equal("b-n-n-", Str(StrCall("banana", "replace", re, S("-"))));
        Assert.Equal("[\"a\",\"a\",\"a\"]", Show(StrCall("banana", "match", re)));
        Assert.Equal("b[a]n[a]n[a]", Str(StrCall("banana", "replace", re, S("[$1]"))));
        Assert.Equal("[\"b\",\"a\",\"n\",\"a\",\"n\",\"a\",\"\"]", Show(StrCall("banana", "split", re)));
        Assert.Equal(1.0, Num(StrCall("banana", "search", re)));
    }
}
