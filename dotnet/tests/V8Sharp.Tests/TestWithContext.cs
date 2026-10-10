// Port of the fixtures of test/unittests/test-utils.h (TestWithIsolate,
// TestWithContext) and the helpers the object tests use.
using V8Sharp.Objects;

namespace V8Sharp.Tests;

/// <summary>
/// V8's TestWithContext: an isolate with a native context, entered on the
/// test's thread for the duration of the test.
/// </summary>
public abstract class TestWithContext : IDisposable
{
    readonly Isolate.IsolateScope _scope;

    protected TestWithContext()
    {
        i_isolate = Isolate.New();
        _scope = i_isolate.Enter();
    }

    /// <summary>The internal isolate (V8's i_isolate()).</summary>
    protected Isolate i_isolate { get; }

    protected Factory factory => i_isolate.Factory;

    public virtual void Dispose()
    {
        _scope.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>TestWithIsolate::MakeString: a fresh (non-internalized) string.</summary>
    protected JSString MakeString(string s) => factory.NewStringFromUtf16(s);

    /// <summary>TestWithIsolate::MakeName: an internalized "prefix" + index.</summary>
    protected JSString MakeName(string prefix, int index) =>
        factory.InternalizeString(prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    protected JSString MakeName(string prefix, uint index) =>
        factory.InternalizeString(prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The length of V8's out-of-object PropertyArray.</summary>
    protected static int PropertyArrayLength(JSObject obj) => obj.OutOfObjectPropertyArrayLength;

    /// <summary>The EQUALS helper of the object tests: identity or Object::Equals.</summary>
    protected bool EQUALS(JSValue left, JSValue right) =>
        left.IsIdenticalTo(right) || ObjectOps.Equals(i_isolate, left, right);
}
