// Port of the compilation-job half of src/interpreter/interpreter.h/.cc
// (InterpreterCompilationJob, Interpreter::NewCompilationJob) and of the
// unoptimized-compilation driver of src/codegen/compiler.cc
// (IterativelyExecuteAndFinalizeUnoptimizedCompilationJobs).
//
// TODO(merge): the compiler pipeline (src/codegen/compiler.cc) belongs to the
// engine; UnoptimizedCompiler is the part of it that needs no heap and is what
// the engine's Compiler::Compile calls after parsing.
using V8Sharp.Ast;
using V8Sharp.Codegen;
using V8Sharp.Parsing;

namespace V8Sharp.Interpreter;

/// <summary>InterpreterCompilationJob: generates and finalizes the bytecode of one function.</summary>
public sealed class InterpreterCompilationJob
{
    readonly ParseInfo _parseInfo;
    readonly UnoptimizedCompilationInfo _compilationInfo;
    readonly BytecodeGenerator _generator;

    public enum Status { SUCCEEDED, FAILED }

    public InterpreterCompilationJob(ParseInfo parse_info, FunctionLiteral literal,
                                     List<FunctionLiteral>? eager_inner_literals,
                                     BytecodeGeneratorFlags? flags = null)
    {
        _parseInfo = parse_info;
        _compilationInfo = new UnoptimizedCompilationInfo(parse_info, literal);
        _generator = new BytecodeGenerator(_compilationInfo, parse_info.ast_string_constants(), eager_inner_literals,
                                           flags);
    }

    public ParseInfo parse_info() => _parseInfo;
    public UnoptimizedCompilationInfo compilation_info() => _compilationInfo;
    public BytecodeGenerator generator() => _generator;

    /// <summary>ExecuteJobImpl: runs the bytecode generator.</summary>
    public Status ExecuteJob()
    {
        _generator.GenerateBytecode();

        if (_generator.HasStackOverflow())
        {
            return Status.FAILED;
        }

        return Status.SUCCEEDED;
    }

    /// <summary>FinalizeJobImpl: allocates the deferred constants and builds the
    /// BytecodeArray (with its source position table).</summary>
    public Status FinalizeJob(IBytecodeGeneratorHeap heap)
    {
        BytecodeArray? bytecodes = _compilationInfo.bytecode_array();
        if (bytecodes is null)
        {
            bytecodes = _generator.FinalizeBytecode(heap);
            if (_generator.HasStackOverflow() || bytecodes is null)
            {
                return Status.FAILED;
            }
            _compilationInfo.SetBytecodeArray(bytecodes);
        }

        if (_compilationInfo.SourcePositionRecordingMode() ==
            SourcePositionTableBuilder.RecordingMode.RECORD_SOURCE_POSITIONS)
        {
            bytecodes.SourcePositionTableOrNull = _generator.FinalizeSourcePositionTable();
        }

        return Status.SUCCEEDED;
    }
}

/// <summary>The output of compiling one function.</summary>
public sealed class CompiledFunction(FunctionLiteral literal, BytecodeArray bytecodeArray,
                                     FeedbackVectorSpec feedbackVectorSpec, object? coverageInfo)
{
    public FunctionLiteral Literal { get; } = literal;
    public BytecodeArray BytecodeArray { get; } = bytecodeArray;
    public FeedbackVectorSpec FeedbackVectorSpec { get; } = feedbackVectorSpec;
    public object? CoverageInfo { get; } = coverageInfo;
}

public static class UnoptimizedCompiler
{
    /// <summary>
    /// IterativelyExecuteAndFinalizeUnoptimizedCompilationJobs: compiles the
    /// parsed (and analyzed) literal of |parse_info| and, iteratively, every
    /// inner function the generator reports as eagerly compiled. When
    /// |scope_info_provider| is given, ScopeInfos are allocated first
    /// (DeclarationScope::AllocateScopeInfos). |on_compiled| receives each
    /// function as it is finalized (V8 installs the bytecode on the
    /// SharedFunctionInfo there). Returns false when a job failed.
    /// </summary>
    public static bool Compile(ParseInfo parse_info, IBytecodeGeneratorHeap heap,
                               Action<CompiledFunction>? on_compiled = null,
                               IScopeInfoProvider? scope_info_provider = null,
                               BytecodeGeneratorFlags? flags = null)
    {
        if (scope_info_provider is not null)
        {
            DeclarationScope.AllocateScopeInfos(parse_info, scope_info_provider);
        }

        var functions_to_compile = new List<FunctionLiteral> { parse_info.literal()! };
        var compiled = new HashSet<FunctionLiteral>(ReferenceEqualityComparer.Instance);

        bool compilation_succeeded = true;
        while (functions_to_compile.Count != 0)
        {
            FunctionLiteral literal = functions_to_compile[^1];
            functions_to_compile.RemoveAt(functions_to_compile.Count - 1);
            if (!compiled.Add(literal)) continue;
            if (heap.GetSharedFunctionInfo(literal) is SharedFunctionInfoDescription { IsCompiled: true }) continue;

            var job = new InterpreterCompilationJob(parse_info, literal, functions_to_compile, flags);
            if (job.ExecuteJob() != InterpreterCompilationJob.Status.SUCCEEDED)
            {
                compilation_succeeded = false;
                continue;
            }

            if (job.FinalizeJob(heap) != InterpreterCompilationJob.Status.SUCCEEDED)
            {
                compilation_succeeded = false;
                continue;
            }

            UnoptimizedCompilationInfo info = job.compilation_info();
            var result = new CompiledFunction(literal, info.bytecode_array()!, info.feedback_vector_spec(),
                                              info.coverage_info());
            if (heap.GetSharedFunctionInfo(literal) is SharedFunctionInfoDescription sfi)
            {
                sfi.BytecodeArray = result.BytecodeArray;
                sfi.FeedbackVectorSpec = result.FeedbackVectorSpec;
                sfi.CoverageInfo = result.CoverageInfo;
            }
            on_compiled?.Invoke(result);
        }

        return compilation_succeeded;
    }
}
