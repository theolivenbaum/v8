// Builtins the interpreter itself relies on (src/builtins/builtins-internal-gen.cc
// and friends) that are reachable as JS functions: ReturnReceiver (the
// %IteratorPrototype%[@@iterator] of generators and iterators).
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterInternal()
    {
        Register(Builtin.ReturnReceiver, BuiltinsInternal.ReturnReceiver);
    }
}

/// <summary>The internal builtins.</summary>
public static class BuiltinsInternal
{
    /// <summary>ReturnReceiver (builtins-internal-gen.cc): returns the receiver unchanged.</summary>
    public static JSValue ReturnReceiver(Isolate isolate, in BuiltinArguments args) => args.Receiver;
}
