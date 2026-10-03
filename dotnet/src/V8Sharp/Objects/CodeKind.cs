// Port of src/objects/code-kind.h: the kinds of code a JSFunction can run.
// V8Sharp has no Code objects; the kinds name the tiers (the interpreter,
// baseline IL) for the tiering manager and the test runtime functions.
namespace V8Sharp.Objects;

/// <summary>CodeKind (CODE_KIND_LIST), in V8's order.</summary>
public enum CodeKind : byte
{
    BYTECODE_HANDLER,
    FOR_TESTING,
    FOR_TESTING_JS,
    BUILTIN,
    REGEXP,
    WASM_FUNCTION,
    WASM_TO_CAPI_FUNCTION,
    WASM_TO_JS_FUNCTION,
    JS_TO_WASM_FUNCTION,
    C_WASM_ENTRY,
    INTERPRETED_FUNCTION,
    BASELINE,
    MAGLEV,
    TURBOFAN_JS,
    WASM_STACK_ENTRY,
}

public static class CodeKindHelpers
{
    /// <summary>CodeKindIsUnoptimizedJSFunction.</summary>
    public static bool IsUnoptimizedJSFunction(CodeKind kind) =>
        kind is CodeKind.INTERPRETED_FUNCTION or CodeKind.BASELINE;
}
