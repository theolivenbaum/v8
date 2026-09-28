// Port of the unoptimized parts of src/codegen/compiler.{h,cc}: toplevel
// script compilation (CompileToplevel, IterativelyExecuteAndFinalize-
// UnoptimizedCompilationJobs, InstallUnoptimizedCode), lazy function
// compilation (Compiler::Compile), eval and dynamic functions
// (GetFunctionFromEval, GetFunctionFromValidatedString,
// Runtime_ResolvePossiblyDirectEval / CompileGlobalEval), SyntaxError
// reporting (PendingCompilationErrorHandler::ReportErrors) and the script
// context setup of Execution::CallScript (NewScriptContext,
// src/execution/execution.cc).
//
// Deviations: source positions are collected eagerly; there is no
// compilation cache (every script and eval compiles afresh); inner functions
// are reparsed without preparse data.
using System.Runtime.CompilerServices;
using V8Sharp.Ast;
using V8Sharp.Interpreter;
using V8Sharp.Parsing;

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        /// <summary>The ParsingFlags derived from <see cref="Flags"/> (cached by the compiler).</summary>
        internal ParsingFlags? CompilerParsingFlags;

        /// <summary>The ScopeInfo provider of the compiler.</summary>
        internal Objects.ScopeInfoProvider? CompilerScopeInfoProvider;
    }
}

namespace V8Sharp.Codegen
{
    public static partial class Compiler
    {
        /// <summary>Registers the compiler on an isolate (lazy compilation, dynamic functions, the call printer).</summary>
        public static void Install(Isolate isolate)
        {
            isolate.CompileLazyHook = CompileLazy;
            isolate.DynamicFunctionCompiler = DynamicFunctionCompiler.Instance;
            isolate.CallPrinter = CompilerCallPrinter.Instance;
        }

        // ---- Flags --------------------------------------------------------------------------

        static ParsingFlags ParsingFlagsFor(Isolate isolate)
        {
            if (isolate.CompilerParsingFlags is { } cached) return cached;
            FlagList f = isolate.Flags;
            var flags = new ParsingFlags
            {
                allow_natives_syntax = f.allow_natives_syntax,
                lazy = f.lazy,
                max_lazy = f.max_lazy,
                // Deviation: source positions are always collected (V8 collects them lazily).
                enable_lazy_source_positions = false,
                use_strict = f.use_strict,
                fuzzing = f.fuzzing,
                allow_natives_for_differential_fuzzing = f.allow_natives_for_differential_fuzzing,
                hole_fuzzing = f.hole_fuzzing,
                sandbox_testing = f.sandbox_testing,
                sandbox_fuzzing = f.sandbox_fuzzing,
                verify_bytecode_full = f.verify_bytecode_full,
                js_decorators = f.js_decorators,
            };
            isolate.CompilerParsingFlags = flags;
            return flags;
        }

        static ScopeInfoProvider ScopeInfoProviderFor(Isolate isolate) =>
            isolate.CompilerScopeInfoProvider ??= new ScopeInfoProvider(isolate);

        static UnoptimizedCompileFlags.ScriptDetails DetailsOf(Isolate isolate, Script script) => new(
            script.Id,
            script.IsUserJavaScript(),
            isolate.Flags.use_strict ? LanguageMode.Strict : LanguageMode.Sloppy,
            script.IsReplMode,
            script.OriginOptionsIsModule,
            false,
            script.IsWrapped,
            script.HasEvalOrigin);

        static UnoptimizedCompileFlags.FunctionDetails DetailsOf(SharedFunctionInfo shared) => new(
            shared.LanguageMode,
            shared.Kind,
            shared.SyntaxKind,
            shared.RequiresInstanceMembersInitializer,
            shared.ClassScopeHasPrivateBrand,
            shared.HasStaticPrivateMethodsOrAccessors,
            shared.PrivateNameLookupSkipsOuterClass,
            shared.IsToplevel,
            shared.IsHoistedInContext);

        static ParseInfo NewParseInfo(Isolate isolate, UnoptimizedCompileFlags flags)
        {
            var parseInfo = new ParseInfo(flags, ParsingFlagsFor(isolate));
            parseInfo.set_scope_info_provider(ScopeInfoProviderFor(isolate));
            parseInfo.set_regexp_syntax_validator(RegExpSyntaxValidator.Instance);
            return parseInfo;
        }

        // ---- Toplevel scripts ------------------------------------------------------------------

        /// <summary>
        /// Compiler::GetSharedFunctionInfoForScript: compiles a classic script and
        /// returns its toplevel SharedFunctionInfo. Throws the SyntaxError.
        /// </summary>
        public static SharedFunctionInfo CompileScript(Isolate isolate, Script script, bool isReplMode = false)
        {
            script.IsReplMode = isReplMode;
            UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForScriptCompile(ParsingFlagsFor(isolate),
                DetailsOf(isolate, script));
            ParseInfo parseInfo = NewParseInfo(isolate, flags);
            return CompileToplevel(isolate, parseInfo, script, null);
        }

        /// <summary>
        /// Creates the Script for a source and compiles it; returns the toplevel
        /// closure in the current native context (ScriptCompiler::Compile +
        /// UnboundScript::BindToCurrentContext).
        /// </summary>
        public static JSFunction CompileScript(Isolate isolate, JSString source, JSValue name, int lineOffset = 0,
            int columnOffset = 0, bool isReplMode = false)
        {
            Script script = isolate.Factory.NewScript(source);
            script.Name = name;
            script.LineOffset = lineOffset;
            script.ColumnOffset = columnOffset;
            SharedFunctionInfo shared = CompileScript(isolate, script, isReplMode);
            return isolate.Factory.NewFunction(shared, isolate.NativeContext);
        }

        /// <summary>
        /// Execution::CallScript: creates the script context (NewScriptContext)
        /// and runs the toplevel code with the global proxy as receiver.
        /// </summary>
        public static JSValue RunScript(Isolate isolate, JSFunction scriptFunction)
        {
            if (scriptFunction.Context.IsNativeContext && scriptFunction.Shared.IsScript)
            {
                scriptFunction.Context = NewScriptContext(isolate, scriptFunction);
            }
            return Execution.Call(isolate, scriptFunction, scriptFunction.Context.NativeContext.GlobalProxyObject, []);
        }

        /// <summary>Compiles and runs a script in the current native context.</summary>
        public static JSValue CompileAndRun(Isolate isolate, string source, string name = "")
        {
            JSFunction function = CompileScript(isolate, isolate.Factory.NewStringFromUtf16(source),
                isolate.Factory.NewStringFromUtf16(name));
            return RunScript(isolate, function);
        }

        /// <summary>NewScriptContext (execution.cc).</summary>
        static Context NewScriptContext(Isolate isolate, JSFunction function)
        {
            SharedFunctionInfo sfi = function.Shared;
            Script script = sfi.Script!;
            ScopeInfo scopeInfo = sfi.ScopeInfo;
            var nativeContext = (NativeContext)function.Context;
            JSGlobalObject globalObject = nativeContext.GlobalObject;
            ScriptContextTable scriptContext = nativeContext.ScriptContextTable;

            using (isolate.EnterContext(nativeContext))
            {
                // Find name clashes.
                for (int i = 0; i < scopeInfo.ContextLocalCount; i++)
                {
                    JSString name = scopeInfo.ContextLocalName(i);
                    VariableMode mode = scopeInfo.ContextLocalMode(i);
                    if (scriptContext.Lookup(name, out ScopeInfo.VariableLookupResult lookup))
                    {
                        if (Globals.IsLexicalVariableMode(mode) || Globals.IsLexicalVariableMode(lookup.Mode))
                        {
                            Context context = scriptContext.Get(lookup.ContextIndex);
                            // If we are trying to redeclare a REPL-mode let as a let, REPL-mode const
                            // as a const, allow it.
                            if (!(mode == lookup.Mode && Globals.IsLexicalVariableMode(mode) && scopeInfo.IsReplModeScope &&
                                  context.ScopeInfo.IsReplModeScope))
                            {
                                // https://tc39.es/ecma262/#sec-globaldeclarationinstantiation:
                                // If envRec.HasLexicalDeclaration(name) is true, throw a SyntaxError
                                // exception.
                                isolate.ThrowAt(isolate.Factory.NewSyntaxError(MessageTemplate.VarRedeclaration, name),
                                    new MessageLocation(script, 0, 1));
                            }
                        }
                    }

                    if (Globals.IsLexicalVariableMode(mode))
                    {
                        if (JSGlobalObject.HasRestrictedGlobalProperty(isolate, globalObject, name))
                        {
                            // https://tc39.es/ecma262/#sec-globaldeclarationinstantiation 3.a:
                            // If envRec.HasVarDeclaration(name) is true, throw a SyntaxError
                            // exception.
                            isolate.ThrowAt(isolate.Factory.NewSyntaxError(MessageTemplate.VarRedeclaration, name),
                                new MessageLocation(script, 0, 1));
                        }
                        JSGlobalObject.InvalidatePropertyCell(isolate, globalObject, name);
                    }
                }

                Context result = isolate.Factory.NewScriptContext(nativeContext, scopeInfo);
                result.Initialize();
                // In REPL mode, we are allowed to add/modify let/const variables.
                // We use the previous defined script context for those.
                bool ignoreDuplicates = scopeInfo.IsReplModeScope;
                nativeContext.ScriptContextTable = ScriptContextTable.Add(isolate, scriptContext, result, ignoreDuplicates);
                return result;
            }
        }

        /// <summary>CompileToplevel (compiler.cc).</summary>
        static SharedFunctionInfo CompileToplevel(Isolate isolate, ParseInfo parseInfo, Script script, ScopeInfo? outerScopeInfo)
        {
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();

            if (!ParsingEntry.ParseProgram(parseInfo, script, outerScopeInfo))
            {
                ReportPendingMessages(isolate, parseInfo, script);
            }
            // Parser::HandleDebugMagicComments.
            if (parseInfo.source_url_magic_comment is { } sourceUrl)
            {
                script.SourceUrl = isolate.Factory.InternalizeString(sourceUrl);
            }

            var heap = new CompilerHeap(isolate, script);
            FunctionLiteral literal = parseInfo.literal()!;
            SharedFunctionInfo shared = heap.GetOrCreateSharedFunctionInfo(literal, isToplevel: true);

            if (!IterativelyExecuteAndFinalizeUnoptimizedCompilationJobs(isolate, parseInfo, heap))
            {
                ReportPendingMessages(isolate, parseInfo, script);
            }
            script.State = Script.CompilationState.Compiled;
            return shared;
        }

        /// <summary>
        /// IterativelyExecuteAndFinalizeUnoptimizedCompilationJobs: compiles the
        /// literal and every inner function the generator compiles eagerly.
        /// </summary>
        static bool IterativelyExecuteAndFinalizeUnoptimizedCompilationJobs(Isolate isolate, ParseInfo parseInfo,
            CompilerHeap heap)
        {
            DeclarationScope.AllocateScopeInfos(parseInfo, ScopeInfoProviderFor(isolate));

            var functionsToCompile = new List<FunctionLiteral> { parseInfo.literal()! };
            while (functionsToCompile.Count != 0)
            {
                FunctionLiteral literal = functionsToCompile[^1];
                functionsToCompile.RemoveAt(functionsToCompile.Count - 1);
                SharedFunctionInfo shared = heap.GetOrCreateSharedFunctionInfo(literal, literal.is_toplevel());
                if (shared.IsCompiled) continue;

                var job = new InterpreterCompilationJob(parseInfo, literal, functionsToCompile);
                if (job.ExecuteJob() != InterpreterCompilationJob.Status.SUCCEEDED) return false;
                if (job.FinalizeJob(heap) != InterpreterCompilationJob.Status.SUCCEEDED) return false;
                InstallUnoptimizedCode(isolate, job.compilation_info(), shared, literal);
            }
            return true;
        }

        /// <summary>InstallUnoptimizedCode + UpdateSharedFunctionFlagsAfterCompilation.</summary>
        static void InstallUnoptimizedCode(Isolate isolate, UnoptimizedCompilationInfo info, SharedFunctionInfo shared,
            FunctionLiteral literal)
        {
            shared.FeedbackMetadata = FeedbackMetadata.New(info.feedback_vector_spec());

            // UpdateSharedFunctionFlagsAfterCompilation.
            shared.HasDuplicateParameters = literal.has_duplicate_parameters();
            shared.ExpectedNofProperties = (byte)Math.Min(literal.expected_property_count(), byte.MaxValue);
            shared.SetScopeInfo((ScopeInfo)literal.scope().scope_info()!);
            shared.UpdateFunctionMapIndex();

            // InstallBytecodeArray.
            shared.FunctionData = info.bytecode_array();
            shared.BuiltinId = Builtin.NoBuiltinId;
        }

        /// <summary>
        /// Reports the parser's pending error as a SyntaxError at its location
        /// (PendingCompilationErrorHandler::ReportErrors / ThrowPendingError), or
        /// a stack overflow. Always throws.
        /// </summary>
        static void ReportPendingMessages(Isolate isolate, ParseInfo parseInfo, Script script)
        {
            PendingCompilationErrorHandler handler = parseInfo.pending_error_handler();
            if (handler.stack_overflow() || !handler.has_pending_error())
            {
                isolate.StackOverflow();
                return;
            }
            PendingCompilationErrorHandler.MessageDetails details = handler.error_details();
            int count = details.ArgCount();
            var args = new JSValue[count];
            for (int i = 0; i < count; i++)
            {
                string? arg = details.ArgString(i);
                args[i] = arg is null ? JSValue.Undefined : isolate.Factory.NewStringFromUtf16(arg);
            }
            JSObject error = isolate.Factory.NewSyntaxError(details.message(), args);
            isolate.ThrowAt(error, new MessageLocation(script, details.start_pos(), details.end_pos()));
        }

        // ---- Lazy compilation ----------------------------------------------------------------------

        /// <summary>Runtime_CompileLazy: compiles the function's SharedFunctionInfo if needed.</summary>
        public static bool CompileLazy(Isolate isolate, JSFunction function)
        {
            SharedFunctionInfo shared = function.Shared;
            if (!shared.IsCompiled && !Compile(isolate, shared)) return false;
            JSFunctionFeedback.InitializeFeedbackCell(isolate, function, true);
            return true;
        }

        /// <summary>Compiles the function or throws its SyntaxError (Compiler::Compile with KEEP_EXCEPTION).</summary>
        public static void CompileLazyOrThrow(Isolate isolate, JSFunction function) => CompileLazy(isolate, function);

        /// <summary>Compiler::Compile(isolate, shared_info).</summary>
        public static bool Compile(Isolate isolate, SharedFunctionInfo shared)
        {
            if (shared.IsCompiled) return true;
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();

            Script script = shared.Script!;
            UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForFunctionCompile(ParsingFlagsFor(isolate), DetailsOf(shared),
                DetailsOf(isolate, script));
            ParseInfo parseInfo = NewParseInfo(isolate, flags);

            // Parse and update ParseInfo with the results.
            if (!ParsingEntry.ParseAny(parseInfo, new ParsingSharedFunctionInfo(shared)))
            {
                ReportPendingMessages(isolate, parseInfo, script);
            }

            var heap = new CompilerHeap(isolate, script);
            if (!IterativelyExecuteAndFinalizeUnoptimizedCompilationJobs(isolate, parseInfo, heap))
            {
                ReportPendingMessages(isolate, parseInfo, script);
            }
            return shared.IsCompiled;
        }

        // ---- eval and dynamic functions -------------------------------------------------------------------

        /// <summary>Compiler::GetFunctionFromEval.</summary>
        public static JSFunction GetFunctionFromEval(Isolate isolate, JSString source, SharedFunctionInfo outerInfo,
            Context context, LanguageMode languageMode, ParseRestriction restriction, int parametersEndPos, int evalPosition)
        {
            Script script = isolate.Factory.NewScript(source);
            script.Compilation = Script.CompilationType.Eval;
            script.EvalFromShared = outerInfo;
            if (evalPosition == Globals.kNoSourcePosition) evalPosition = 0;
            script.EvalFromPosition = evalPosition;
            if (outerInfo.Script is { } outerScript)
            {
                script.OriginOptionsIsSharedCrossOrigin = outerScript.OriginOptionsIsSharedCrossOrigin;
            }

            UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForToplevelCompile(ParsingFlagsFor(isolate), script.Id,
                true, languageMode, REPLMode.No, ScriptType.Classic, isolate.Flags.lazy_eval);
            flags.set_is_eval(true);
            flags.set_parse_restriction(restriction);
            ParseInfo parseInfo = NewParseInfo(isolate, flags);
            parseInfo.set_parameters_end_pos(parametersEndPos);

            ScopeInfo? outerScopeInfo = context.IsNativeContext ? null : context.ScopeInfo;
            SharedFunctionInfo shared = CompileToplevel(isolate, parseInfo, script, outerScopeInfo);
            JSFunction result = isolate.Factory.NewFunction(shared, context);
            JSFunctionFeedback.InitializeFeedbackCell(isolate, result, false);
            return result;
        }

        /// <summary>
        /// Runtime_ResolvePossiblyDirectEval: args = [callee, source, outer function,
        /// language mode, eval scope position, eval position].
        /// </summary>
        public static JSValue ResolvePossiblyDirectEval(Isolate isolate, ReadOnlySpan<JSValue> args)
        {
            JSValue callee = args[0];

            // If "eval" didn't refer to the original GlobalEval, it's not a
            // direct call to eval.
            if (!ReferenceEquals(callee.HeapObjectOrNull, isolate.NativeContext.GlobalEvalFun)) return callee;

            var languageMode = (LanguageMode)(int)args[3].Number;
            SharedFunctionInfo outerInfo = args[2].As<JSFunction>().Shared;
            return CompileGlobalEval(isolate, args[1], outerInfo, languageMode, (int)args[5].Number);
        }

        /// <summary>CompileGlobalEval (runtime-compiler.cc).</summary>
        static JSValue CompileGlobalEval(Isolate isolate, JSValue sourceObject, SharedFunctionInfo outerInfo,
            LanguageMode languageMode, int evalPosition)
        {
            NativeContext nativeContext = isolate.NativeContext;

            // Check if native context allows code generation from
            // strings. Throw an exception if it doesn't.
            JSString? source = Builtins.BuiltinsGlobal.ValidateDynamicCompilationSource(isolate, nativeContext, sourceObject,
                out bool unknownObject);
            // If the argument is an unhandled string time, bounce to GlobalEval.
            if (unknownObject) return nativeContext.GlobalEvalFun;
            if (source is null)
            {
                return isolate.Throw(isolate.Factory.NewEvalError(MessageTemplate.CodeGenFromStrings,
                    Builtins.BuiltinsGlobal.ErrorMessageForCodeGenerationFromStrings(isolate, nativeContext)));
            }

            // Deal with a normal eval call with a string argument. Compile it
            // and return the compiled function bound in the local context.
            Context context = isolate.Context!;
            return GetFunctionFromEval(isolate, source, outerInfo, context, languageMode, ParseRestriction.NO_PARSE_RESTRICTION,
                Globals.kNoSourcePosition, evalPosition);
        }

        /// <summary>Compiler::GetFunctionFromValidatedString: code compiled in the native context.</summary>
        public static JSFunction GetFunctionFromValidatedString(Isolate isolate, NativeContext nativeContext, JSString source,
            ParseRestriction restriction, int parametersEndPos)
        {
            // Compile source string in the native context.
            SharedFunctionInfo outerInfo = nativeContext.EmptyFunction.Shared;
            using (isolate.EnterContext(nativeContext))
            {
                return GetFunctionFromEval(isolate, source, outerInfo, nativeContext, LanguageMode.Sloppy, restriction,
                    parametersEndPos, Globals.kNoSourcePosition);
            }
        }

        /// <summary>The compiler entry points of the dynamic function builtins (new Function, indirect eval).</summary>
        sealed class DynamicFunctionCompiler : IDynamicFunctionCompiler
        {
            public static readonly DynamicFunctionCompiler Instance = new();

            public JSFunction GetFunctionFromString(Isolate isolate, NativeContext nativeContext, JSString source,
                int parametersEndPos, bool isCodeLike)
            {
                // Compiler::GetFunctionFromString: ValidateDynamicCompilationSource,
                // then GetFunctionFromValidatedString, which throws the EvalError
                // for a null (disallowed) source.
                JSString? validated = Builtins.BuiltinsGlobal.ValidateDynamicCompilationSource(isolate, nativeContext, source, out _);
                if (validated is null)
                {
                    isolate.Throw(isolate.Factory.NewEvalError(MessageTemplate.CodeGenFromStrings,
                        Builtins.BuiltinsGlobal.ErrorMessageForCodeGenerationFromStrings(isolate, nativeContext)));
                }
                return GetFunctionFromValidatedString(isolate, nativeContext, validated!, ParseRestriction.ONLY_SINGLE_FUNCTION_LITERAL,
                    parametersEndPos);
            }

            JSFunction IDynamicFunctionCompiler.GetFunctionFromValidatedString(Isolate isolate, NativeContext nativeContext,
                JSString source, ParseRestriction restriction, int parametersEndPos) =>
                Compiler.GetFunctionFromValidatedString(isolate, nativeContext, source, restriction, parametersEndPos);
        }

        /// <summary>RenderCallSite's reparse + CallPrinter (messages.cc).</summary>
        sealed class CompilerCallPrinter : ICallPrinter
        {
            public static readonly CompilerCallPrinter Instance = new();

            public CallPrinterResult Print(Isolate isolate, SharedFunctionInfo shared, int position, bool spreadErrorInArgs)
            {
                if (shared.Script is null) return new CallPrinterResult { CallSite = "" };
                UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForFunctionCompile(ParsingFlagsFor(isolate),
                    DetailsOf(shared), DetailsOf(isolate, shared.Script));
                flags.set_is_reparse(true);
                ParseInfo info = NewParseInfo(isolate, flags);
                if (!ParsingEntry.ParseAny(info, new ParsingSharedFunctionInfo(shared)))
                {
                    return new CallPrinterResult { CallSite = "", DestructuringPropertyPosition = -1, DestructuringValuePosition = -1,
                        SpreadArgPosition = -1 };
                }
                var printer = new CallPrinter(shared.IsUserJavaScript(),
                    spreadErrorInArgs ? CallPrinter.SpreadErrorInArgsHint.kErrorInArgs : CallPrinter.SpreadErrorInArgsHint.kNoErrorInArgs);
                string callSite = printer.Print(info.literal()!, position);
                ObjectLiteralProperty? prop = printer.destructuring_prop();
                Assignment? assignment = printer.destructuring_assignment();
                Expression? spreadArg = printer.spread_arg();
                string? propertyName = null;
                if (prop is not null && prop.key().IsPropertyName())
                {
                    propertyName = prop.key().AsLiteral()!.AsRawPropertyName().Value;
                }
                return new CallPrinterResult
                {
                    CallSite = callSite,
                    Hint = (CallPrinterErrorHint)(int)printer.GetErrorHint(),
                    IsDestructuring = assignment is not null,
                    DestructuringPropertyName = propertyName,
                    DestructuringPropertyPosition = prop is not null ? prop.key().position() : -1,
                    DestructuringValuePosition = assignment is not null ? assignment.value().position() : -1,
                    SpreadArgPosition = spreadArg is not null ? spreadArg.position() : -1,
                };
            }
        }
    }
}
