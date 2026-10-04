// Port of src/wasm/wasm-result.{h,cc}: ErrorThrower, which formats the
// JS API's errors as "<api name>: <message>" with the error constructor of
// the failure (TypeError, RangeError, CompileError, LinkError, RuntimeError).
// Also the mapping of the WACS engine's failures to V8's messages: decoder
// and validation errors (CompileError) and traps (RuntimeError).
using System.Diagnostics.CodeAnalysis;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.OpCodes;
using Wacs.Core.Validation;

namespace V8Sharp.Wasm;

/// <summary>V8's ErrorThrower (wasm-result.h).</summary>
public sealed class ErrorThrower(Isolate isolate, string context)
{
    public enum ErrorType { None, TypeError, RangeError, CompileError, LinkError, RuntimeError }

    public string ContextName { get; } = context;

    [DoesNotReturn]
    public void TypeError(string message) => Throw(ErrorType.TypeError, message);

    [DoesNotReturn]
    public void RangeError(string message) => Throw(ErrorType.RangeError, message);

    [DoesNotReturn]
    public void CompileError(string message) => Throw(ErrorType.CompileError, message);

    [DoesNotReturn]
    public void LinkError(string message) => Throw(ErrorType.LinkError, message);

    [DoesNotReturn]
    public void RuntimeError(string message) => Throw(ErrorType.RuntimeError, message);

    [DoesNotReturn]
    void Throw(ErrorType type, string message) => isolate.Throw(Reify(type, message));

    /// <summary>ErrorThrower::Reify: the error object, without throwing it.</summary>
    public JSObject Reify(ErrorType type, string message)
    {
        NativeContext nc = isolate.NativeContext;
        JSFunction constructor = type switch
        {
            ErrorType.TypeError => nc.TypeErrorFunction,
            ErrorType.RangeError => nc.RangeErrorFunction,
            ErrorType.CompileError => nc.WasmCompileErrorFunction,
            ErrorType.LinkError => nc.WasmLinkErrorFunction,
            _ => nc.WasmRuntimeErrorFunction,
        };
        string text = string.IsNullOrEmpty(ContextName) ? message : ContextName + ": " + message;
        return isolate.Factory.NewError(constructor, isolate.Factory.NewStringFromAsciiChecked(text));
    }
}

/// <summary>V8's messages for failures that WACS reports in its own words.</summary>
public static class WasmErrorMessages
{
    /// <summary>
    /// A decoding failure. V8's module decoder reports the byte offset
    /// ("@+N"); for the checks the JS API tests depend on (the header), the
    /// message is V8's; otherwise it is WACS's reason with V8's prefix
    /// (deviations.md, "WebAssembly").
    /// </summary>
    public static string DecodeError(byte[] bytes, Exception e)
    {
        if (bytes.Length < 4)
        {
            return "expected 4 bytes, fell off end @+0";
        }
        if (bytes[0] != 0x00 || bytes[1] != 0x61 || bytes[2] != 0x73 || bytes[3] != 0x6d)
        {
            return $"expected magic word 00 61 73 6d, found {bytes[0]:x2} {bytes[1]:x2} {bytes[2]:x2} {bytes[3]:x2} @+0";
        }
        if (bytes.Length < 8)
        {
            return "expected 4 bytes, fell off end @+4";
        }
        if (bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0)
        {
            return $"expected version 01 00 00 00, found {bytes[4]:x2} {bytes[5]:x2} {bytes[6]:x2} {bytes[7]:x2} @+4";
        }
        return e.Message;
    }

    /// <summary>A validation failure: "Compiling function #N failed: ..." as V8 reports it.</summary>
    public static string ValidationError(Exception e)
    {
        if (e is ValidationException ve && ve.FunctionIndex >= 0)
        {
            string detail = ve.Instruction is null ? ve.Message : ve.Message + " (" + ve.Instruction + ")";
            return $"Compiling function #{ve.FunctionIndex} failed: {detail}";
        }
        return e.Message;
    }

    /// <summary>
    /// The MessageTemplate of a trap. WACS raises traps with its own texts;
    /// V8 has one message per trap reason (wasm::TrapReason).
    /// </summary>
    // V8SHARP_TRACE_WASM_TRAPS=1 prints the interpreter's own trap message.
    static readonly bool s_traceTraps = Environment.GetEnvironmentVariable("V8SHARP_TRACE_WASM_TRAPS") is not null;

    public static MessageTemplate TrapTemplate(TrapException e)
    {
        string m = e.Message;
        if (s_traceTraps) Console.Error.WriteLine("[wasm trap] " + m);
        string op = e.WasmFrames is { Length: > 0 } frames && frames[0].Instruction is { } inst
            ? inst.Op.GetMnemonic()
            : "";
        if (e is OutOfBoundsTableAccessException) return MessageTemplate.WasmTrapTableOutOfBounds;
        if (m.Contains("too large", StringComparison.Ordinal)) return MessageTemplate.WasmTrapArrayTooLarge;
        if (m == WasmFutexPolicy.WaitNotAllowed) return MessageTemplate.AtomicsOperationNotAllowed;
        if (m.Contains("element segment out of bounds", StringComparison.Ordinal))
            return MessageTemplate.WasmTrapElementSegmentOutOfBounds;
        if (m.StartsWith("unreachable", StringComparison.Ordinal)) return MessageTemplate.WasmTrapUnreachable;
        if (m.Contains("divide by zero", StringComparison.OrdinalIgnoreCase))
        {
            return op.Contains("rem", StringComparison.Ordinal)
                ? MessageTemplate.WasmTrapRemByZero
                : MessageTemplate.WasmTrapDivByZero;
        }
        if (m.Contains("arithmetic overflow", StringComparison.Ordinal)) return MessageTemplate.WasmTrapDivUnrepresentable;
        if (m.Contains("trunc", StringComparison.Ordinal) || m.Contains("NaN or infinity", StringComparison.Ordinal) ||
            m.Contains("Integer overflow", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapFloatUnrepresentable;
        }
        if (m.Contains("throw_ref", StringComparison.Ordinal) || m.Contains("Exception reference is null", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapRethrowNull;
        }
        if (m.Contains("cast failure", StringComparison.Ordinal) || op.StartsWith("ref.cast", StringComparison.Ordinal) ||
            m.StartsWith("Instruction ref.cast", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapIllegalCast;
        }
        if (op.StartsWith("call_indirect", StringComparison.Ordinal) || op.StartsWith("return_call_indirect", StringComparison.Ordinal) ||
            m.StartsWith("call_indirect", StringComparison.Ordinal) || m.StartsWith("return_call_indirect", StringComparison.Ordinal) ||
            m.Contains("Instruction call_indirect", StringComparison.Ordinal))
        {
            if (m.Contains("could not find element", StringComparison.Ordinal) || m.Contains("undefined element", StringComparison.Ordinal))
                return MessageTemplate.WasmTrapTableOutOfBounds;
            if (m.Contains("NullReference", StringComparison.Ordinal))
                return MessageTemplate.WasmTrapNullFunc;
            return MessageTemplate.WasmTrapFuncSigMismatch;
        }
        if (m.Contains("type mismatch", StringComparison.Ordinal) || m.Contains("FunctionType differed", StringComparison.Ordinal) ||
            m.Contains("RecursiveType differed", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapFuncSigMismatch;
        }
        if (op.StartsWith("table.", StringComparison.Ordinal) || m.StartsWith("table.", StringComparison.Ordinal) ||
            m.Contains("Trap in table", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapTableOutOfBounds;
        }
        if (op.StartsWith("array.new_data", StringComparison.Ordinal) || op.StartsWith("array.init_data", StringComparison.Ordinal))
        {
            return m.Contains("ull", StringComparison.Ordinal) && !m.Contains("overflow", StringComparison.Ordinal)
                ? MessageTemplate.WasmTrapNullDereference
                : MessageTemplate.WasmTrapDataSegmentOutOfBounds;
        }
        if (op.StartsWith("array.new_elem", StringComparison.Ordinal) || op.StartsWith("array.init_elem", StringComparison.Ordinal))
        {
            return m.Contains("ull", StringComparison.Ordinal) && !m.Contains("overflow", StringComparison.Ordinal)
                ? MessageTemplate.WasmTrapNullDereference
                : MessageTemplate.WasmTrapElementSegmentOutOfBounds;
        }
        if (m.Contains("null", StringComparison.OrdinalIgnoreCase) || m.Contains("NullReference", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapNullDereference;
        }
        if (op.StartsWith("array.new", StringComparison.Ordinal) && m.Contains("size", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapArrayTooLarge;
        }
        if (op.StartsWith("array.", StringComparison.Ordinal) || m.StartsWith("array.", StringComparison.Ordinal) ||
            m.Contains("array bounds", StringComparison.Ordinal) || m.Contains("Array", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapArrayOutOfBounds;
        }
        if (m.Contains("emory", StringComparison.Ordinal) || m.Contains("out of bounds", StringComparison.Ordinal) ||
            m.Contains("Buffer overflow", StringComparison.Ordinal) || m.Contains("Data underflow", StringComparison.Ordinal))
        {
            return MessageTemplate.WasmTrapMemOutOfBounds;
        }
        if (m.Contains("unaligned", StringComparison.OrdinalIgnoreCase)) return MessageTemplate.WasmTrapUnalignedAccess;
        return MessageTemplate.WasmTrapUnreachable;
    }

    /// <summary>Whether a WACS runtime failure is the engine running out of stack (V8: RangeError).</summary>
    public static bool IsStackExhaustion(Exception e) =>
        e is WasmRuntimeException w && (w.Message.Contains("call stack exhausted", StringComparison.Ordinal) ||
                                        w.Message.Contains("Operand stack exhausted", StringComparison.Ordinal)) ||
        e is IndexOutOfRangeException;
}
