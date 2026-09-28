// Registration of the C# builtin implementations, one hook per area.
//
// Each builtins area (a V8 builtins-<area>.cc / <area>.tq set) implements its
// partial method in its own file, e.g.
//     static partial void RegisterArray() { Register(Builtin.ArrayPrototypePush, ArrayPrototypePush); ... }
// so parallel ports never edit a shared file. An area that is not ported yet
// simply has no implementation, and its builtins throw NotImplementedException
// when called.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static int s_registered;

    /// <summary>Registers every area's builtins once per process.</summary>
    public static void RegisterAll()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        BuiltinsApi.Register();
        RegisterObject();
        RegisterFunction();
        RegisterReflect();
        RegisterProxy();
        RegisterGlobal();
        RegisterError();
        RegisterBoolean();
        RegisterSymbol();
        RegisterArray();
        RegisterArrayBuffer();
        RegisterTypedArray();
        RegisterDataView();
        RegisterAtomics();
        RegisterString();
        RegisterRegExp();
        RegisterNumber();
        RegisterMath();
        RegisterBigInt();
        RegisterJson();
        RegisterDate();
        RegisterCollections();
        RegisterWeakRefs();
        RegisterPromise();
        RegisterIterator();
        RegisterGenerator();
        RegisterAsync();
        RegisterDisposableStack();
        RegisterInternal();
    }

    static partial void RegisterObject();
    static partial void RegisterFunction();
    static partial void RegisterReflect();
    static partial void RegisterProxy();
    static partial void RegisterGlobal();
    static partial void RegisterError();
    static partial void RegisterBoolean();
    static partial void RegisterSymbol();
    static partial void RegisterArray();
    static partial void RegisterArrayBuffer();
    static partial void RegisterTypedArray();
    static partial void RegisterDataView();
    static partial void RegisterAtomics();
    static partial void RegisterString();
    static partial void RegisterRegExp();
    static partial void RegisterNumber();
    static partial void RegisterMath();
    static partial void RegisterBigInt();
    static partial void RegisterJson();
    static partial void RegisterDate();
    static partial void RegisterCollections();
    static partial void RegisterWeakRefs();
    static partial void RegisterPromise();
    static partial void RegisterIterator();
    static partial void RegisterGenerator();
    static partial void RegisterAsync();
    static partial void RegisterDisposableStack();
    /// <summary>Builtins used by bytecode handlers and the interpreter (InterpreterEntryTrampoline, Call/Construct stubs, ...).</summary>
    static partial void RegisterInternal();
}
