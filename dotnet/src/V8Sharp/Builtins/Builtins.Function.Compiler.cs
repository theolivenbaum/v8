// The compiler hook of the dynamic function builtins (builtins-function.cc
// CreateDynamicFunction, builtins-global.cc GlobalEval): the entry points of
// src/codegen/compiler.cc they call, registered by the compiler port.
using V8Sharp.Objects;
using V8Sharp.Parsing;

namespace V8Sharp;

/// <summary>
/// The part of V8's Compiler (src/codegen/compiler.cc) that the dynamic
/// function builtins (Function constructors, eval) call. The compiler port
/// registers an implementation on <see cref="Isolate.DynamicFunctionCompiler"/>.
/// </summary>
public interface IDynamicFunctionCompiler
{
    /// <summary>
    /// Compiler::GetFunctionFromString: compiles <paramref name="source"/>
    /// (an expression "(function anonymous(...\n) {\n...\n})") as a top-level
    /// script of <paramref name="nativeContext"/> and returns its closure,
    /// which evaluates to the function. Throws the SyntaxError of the source.
    /// </summary>
    JSFunction GetFunctionFromString(Isolate isolate, NativeContext nativeContext, JSString source, int parametersEndPos,
        bool isCodeLike);

    /// <summary>
    /// Compiler::GetFunctionFromValidatedString: compiles an indirect eval
    /// source as a global eval script and returns its closure.
    /// </summary>
    JSFunction GetFunctionFromValidatedString(Isolate isolate, NativeContext nativeContext, JSString source,
        ParseRestriction restriction, int parametersEndPos);
}

public sealed partial class Isolate
{
    /// <summary>
    /// The compiler entry points for dynamic code (new Function, indirect
    /// eval). Null until the compiler registers itself.
    /// </summary>
    public IDynamicFunctionCompiler? DynamicFunctionCompiler;

    /// <summary>
    /// The proxy_revoke_shared_fun root (setup-heap-internal.cc), created on
    /// first use by Proxy.revocable (Builtins.Proxy.cs).
    /// </summary>
    internal SharedFunctionInfo? ProxyRevokeSharedFun;
}
