// The IBytecodeExpectationsCompiler of the golden-file tests: what
// BytecodeExpectationsPrinter::PrintExpectation does with a live isolate
// (compile the script or module with --no-lazy, run it, and fetch the bytecode
// of the top-level code, of the global test function, or of the callee),
// done with the ported parser and bytecode generator.
//
// Without an interpreter the script is not run: GoldenFunctionResolver
// finds the function the global name (or the script's completion value)
// refers to by evaluating the few forms the golden snippets use.
using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Interpreter;
using V8Sharp.Parsing;

namespace V8Sharp.Tests.Interpreter;

public sealed class GoldenBytecodeCompiler : IBytecodeExpectationsCompiler
{
    // The bytecode of the global test function after the previous snippet of
    // the same golden file: V8 runs all snippets of a file in one context, so
    // a snippet that does not assign the global (it throws first) prints the
    // previous value.
    BytecodeArray? _previousGlobal;

    /// <summary>The flags generate-bytecode-expectations and the unit test set
    /// (allow_natives_syntax, no lazy source positions, no function context
    /// cells, --no-lazy) plus the header's extra flags.</summary>
    public static (ParsingFlags, BytecodeGeneratorFlags) FlagsFor(BytecodeExpectationsHeaderOptions options)
    {
        bool array_destructure_bytecode = false, for_of_optimization = false, private_field_bytecodes = false;
        bool proto_assign_seq_opt = true, ignition_elide_redundant_tdz_checks = true;
        foreach (string raw in options.extra_flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string flag = raw.TrimStart('-').Replace('_', '-');
            bool value = true;
            if (flag.StartsWith("no-", StringComparison.Ordinal))
            {
                value = false;
                flag = flag[3..];
            }
            switch (flag)
            {
                case "array-destructure-bytecode": array_destructure_bytecode = value; break;
                case "for-of-optimization": for_of_optimization = value; break;
                case "private-field-bytecodes": private_field_bytecodes = value; break;
                case "proto-assign-seq-opt": proto_assign_seq_opt = value; break;
                case "ignition-elide-redundant-tdz-checks": ignition_elide_redundant_tdz_checks = value; break;
                default: throw new NotSupportedException("golden extra flag " + raw);
            }
        }
        var parsing = new ParsingFlags
        {
            allow_natives_syntax = true,
            lazy = false,
            enable_lazy_source_positions = false,
            function_context_cells = false,
            ignition_elide_redundant_tdz_checks = ignition_elide_redundant_tdz_checks,
        };
        var generator = new BytecodeGeneratorFlags
        {
            array_destructure_bytecode = array_destructure_bytecode,
            for_of_optimization = for_of_optimization,
            private_field_bytecodes = private_field_bytecodes,
            proto_assign_seq_opt = proto_assign_seq_opt,
            ignition_elide_redundant_tdz_checks = ignition_elide_redundant_tdz_checks,
        };
        return (parsing, generator);
    }

    public sealed class CompiledScript
    {
        public required ParseInfo ParseInfo { get; init; }
        public required Dictionary<FunctionLiteral, BytecodeArray> Functions { get; init; }
    }

    public static CompiledScript CompileScript(string source, BytecodeExpectationsHeaderOptions options)
    {
        (ParsingFlags parsing_flags, BytecodeGeneratorFlags generator_flags) = FlagsFor(options);
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForScriptCompile(
            parsing_flags,
            new UnoptimizedCompileFlags.ScriptDetails(1, true, LanguageMode.Sloppy, false, options.module, false,
                                                      false, false));
        var info = new ParseInfo(flags, parsing_flags);
        var script = new SourceScript(source, 1);
        if (!ParsingEntry.ParseProgram(info, script))
        {
            throw new InvalidOperationException("parse failed: " +
                                                info.pending_error_handler().ToString());
        }

        var functions = new Dictionary<FunctionLiteral, BytecodeArray>(ReferenceEqualityComparer.Instance);
        var heap = new DefaultBytecodeGeneratorHeap();
        if (!UnoptimizedCompiler.Compile(info, heap, f => functions[f.Literal] = f.BytecodeArray,
                                         flags: generator_flags))
        {
            throw new InvalidOperationException("bytecode generation failed");
        }
        return new CompiledScript { ParseInfo = info, Functions = functions };
    }

    public BytecodeArray Compile(string sourceCode, BytecodeExpectationsHeaderOptions options, string functionName)
    {
        CompiledScript compiled = CompileScript(sourceCode, options);
        FunctionLiteral top = compiled.ParseInfo.literal()!;

        if (options.module)
        {
            return compiled.Functions[top];
        }

        var resolver = new GoldenFunctionResolver(compiled, options);
        if (options.print_callee)
        {
            FunctionLiteral callee = resolver.ResolveCompletionValue()
                                     ?? throw new InvalidOperationException("callee not found");
            return resolver.BytecodeOf(callee);
        }
        if (options.top_level)
        {
            return compiled.Functions[top];
        }

        FunctionLiteral? global = resolver.ResolveGlobal(functionName);
        if (global is null)
        {
            return _previousGlobal ?? throw new InvalidOperationException("global " + functionName + " not found");
        }
        _previousGlobal = resolver.BytecodeOf(global);
        return _previousGlobal;
    }
}
