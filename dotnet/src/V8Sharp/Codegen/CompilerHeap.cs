// The heap side of bytecode generation for the engine: V8's
// Compiler::GetSharedFunctionInfo (src/codegen/compiler.cc),
// Factory::NewSharedFunctionInfoForLiteral with
// SharedFunctionInfo::InitFromFunctionLiteral (src/objects/shared-function-info.cc),
// the boilerplate factories (Factory::NewObjectBoilerplateDescription,
// NewArrayBoilerplateDescription, NewTemplateObjectDescription,
// ClassBoilerplate::New) and ConstantArrayBuilder::Entry::ToHandle.
using V8Sharp.Ast;
using V8Sharp.Interpreter;

namespace V8Sharp.Codegen;

/// <summary>An IBytecodeGeneratorHeap that allocates the engine's heap objects for one Script.</summary>
public sealed class CompilerHeap(Isolate isolate, Script script) : IBytecodeGeneratorHeap
{
    static readonly ObjectBoilerplateDescription s_emptyObjectBoilerplate = new(0, 0, 0);
    static readonly ArrayBoilerplateDescription s_emptyArrayBoilerplate =
        new(Objects.ElementsKind.PACKED_SMI_ELEMENTS, FixedArray.Empty);

    public Script Script => script;

    // ---- SharedFunctionInfos -----------------------------------------------------------

    /// <summary>Compiler::GetSharedFunctionInfo(literal, script, isolate).</summary>
    public object? GetSharedFunctionInfo(FunctionLiteral literal) => GetOrCreateSharedFunctionInfo(literal, isToplevel: false);

    /// <summary>The Script's SharedFunctionInfo for the literal, created when missing.</summary>
    public SharedFunctionInfo GetOrCreateSharedFunctionInfo(FunctionLiteral literal, bool isToplevel)
    {
        SharedFunctionInfo? existing = script.FindSharedFunctionInfo(literal.function_literal_id());
        if (existing is not null)
        {
            // If the function has been uncompiled (bytecode flushed) it will have lost
            // any preparsed data. If we produced preparsed data during this compile for
            // this function, replace the uncompiled data with one that includes it.
            if (literal.produced_preparse_data() is { } produced &&
                existing.FunctionData is UncompiledData { PreparseData: null } existingUncompiledData)
            {
                // Use existing uncompiled data's inferred name as it may be more
                // accurate than the literal we preparsed.
                existing.FunctionData = new UncompiledData(existingUncompiledData.InferredName,
                    existingUncompiledData.StartPosition, existingUncompiledData.EndPosition, produced.Serialize());
            }
            literal.set_shared_function_info(existing);
            return existing;
        }
        // Allocate a shared function info object which will be compiled lazily.
        return NewSharedFunctionInfoForLiteral(literal, isToplevel);
    }

    /// <summary>Factory::NewSharedFunctionInfoForLiteral.</summary>
    SharedFunctionInfo NewSharedFunctionInfoForLiteral(FunctionLiteral literal, bool isToplevel)
    {
        FunctionKind kind = literal.kind();
        // FunctionLiteral::GetName: no shared name without a raw name.
        JSString? name = literal.raw_name() is { } rawName ? InternalizedName(isolate, rawName) : null;
        SharedFunctionInfo shared = isolate.Factory.NewSharedFunctionInfo(name, null, Builtin.CompileLazy, 0, false, kind);
        if (name is null) shared.ClearName();
        shared.BuiltinId = Builtin.CompileLazy;
        literal.set_shared_function_info(shared);
        InitFromFunctionLiteral(isolate, shared, literal, isToplevel);
        shared.SetScript(script, literal.function_literal_id());
        return shared;
    }

    /// <summary>SharedFunctionInfo::InitFromFunctionLiteral.</summary>
    public static void InitFromFunctionLiteral(Isolate isolate, SharedFunctionInfo shared, FunctionLiteral lit, bool isToplevel)
    {
        // When adding fields here, make sure DeclarationScope::AnalyzePartially is
        // updated accordingly.
        shared.SetInternalFormalParameterCount(lit.parameter_count() + 1);
        shared.SetFunctionTokenPosition(lit.function_token_position(), lit.start_position());
        shared.SyntaxKind = lit.syntax_kind();
        shared.AllowsLazyCompilation = lit.AllowsLazyCompilation();
        shared.LanguageMode = lit.language_mode();
        shared.Kind = lit.kind();
        shared.RequiresInstanceMembersInitializer = lit.requires_instance_members_initializer();
        shared.ClassScopeHasPrivateBrand = lit.class_scope_has_private_brand();
        shared.HasStaticPrivateMethodsOrAccessors = lit.has_static_private_methods_or_accessors();
        shared.IsHoistedInContext = lit.scope().is_hoisted_in_context();
        shared.IsToplevel = isToplevel;
        Scope? outerScope = lit.scope().GetOuterScopeWithContext();
        if (outerScope is not null)
        {
            shared.OuterScopeInfo = outerScope.scope_info() as ScopeInfo;
            shared.PrivateNameLookupSkipsOuterClass = lit.scope().private_name_lookup_skips_outer_class();
        }
        if (lit.scope().from_scope_info()) shared.SetScopeInfo((ScopeInfo)lit.scope().scope_info()!);

        shared.Length = (ushort)lit.function_length();
        shared.HasDuplicateParameters = lit.has_duplicate_parameters();
        shared.ExpectedNofProperties = (byte)Math.Min(lit.expected_property_count(), byte.MaxValue);
        shared.UpdateFunctionMapIndex();

        // V8 skips the UncompiledData for functions it is about to compile
        // eagerly; V8Sharp always creates it (the SFI stays "not compiled" until
        // its bytecode is installed).
        JSString inferredName = lit.raw_inferred_name() is { } rawInferredName
            ? InternalizedName(isolate, rawInferredName)
            : isolate.Factory.InternalizeString("");
        // CreateAndSetUncompiledData: with the preparse data of a skipped
        // function, so its lazy compile can skip its inner functions too.
        shared.FunctionData = new UncompiledData(inferredName, lit.start_position(), lit.end_position(),
            lit.produced_preparse_data()?.Serialize());
    }

    public object GetNativeFunctionSharedFunctionInfo(NativeFunctionLiteral literal) =>
        throw new NotSupportedException("V8Sharp: v8::Extension native functions are not supported");

    // ---- Boilerplates ---------------------------------------------------------------------------

    static JSValue ToValue(Isolate isolate, object? entry) => InterpreterRuntime.ToValue(isolate, entry);

    public object NewObjectBoilerplateDescription(ObjectBoilerplateDescriptionData description)
    {
        var result = new ObjectBoilerplateDescription(description.BoilerplatePropertyCount, description.BackingStoreSize,
            description.Flags);
        for (int i = 0; i < description.BoilerplatePropertyCount; i++)
        {
            result.SetKeyValue(i, ToValue(isolate, description.Keys[i]), ToValue(isolate, description.Values[i]));
        }
        return result;
    }

    public object NewArrayBoilerplateDescription(ArrayBoilerplateDescriptionData description)
    {
        var kind = (Objects.ElementsKind)(int)description.ElementsKind;
        FixedArrayBase elements;
        if (description.UsesDoubles)
        {
            double[] doubles = description.DoubleElements!;
            bool[] holes = description.DoubleHoles!;
            var array = new FixedDoubleArray(doubles.Length);
            for (int i = 0; i < doubles.Length; i++)
            {
                if (holes[i]) array.Data[i] = FixedDoubleArray.HoleNaN;
                else array.Set(i, doubles[i]);
            }
            elements = array;
        }
        else
        {
            object?[] values = description.Elements!;
            var array = new FixedArray(values.Length);
            for (int i = 0; i < values.Length; i++) array.Data[i] = values[i] is null ? JSValue.TheHole : ToValue(isolate, values[i]);
            if (description.IsCopyOnWrite) array.IsCowArray = true;
            elements = array;
        }
        return new ArrayBoilerplateDescription(kind, elements);
    }

    public object NewClassBoilerplate(ClassLiteral literal) => ClassBoilerplate.New(isolate, literal);

    public object NewTemplateObjectDescription(TemplateObjectDescriptionData description)
    {
        var raw = new FixedArray(description.RawStrings.Length);
        for (int i = 0; i < raw.Length; i++) raw.Data[i] = ToValue(isolate, description.RawStrings[i]);
        FixedArray cooked = raw;
        if (!ReferenceEquals(description.CookedStrings, description.RawStrings))
        {
            cooked = new FixedArray(description.CookedStrings.Length);
            for (int i = 0; i < cooked.Length; i++) cooked.Data[i] = ToValue(isolate, description.CookedStrings[i]);
        }
        return new TemplateObjectDescription(raw, cooked);
    }

    public object NewFixedArray(object[] elements)
    {
        var array = new FixedArray(elements.Length);
        for (int i = 0; i < elements.Length; i++) array.Data[i] = ToValue(isolate, elements[i]);
        return array;
    }

    public object NewCoverageInfo(IReadOnlyList<SourceRange> slots) => new CoverageInfoDescription(slots);

    public void RecordEvalScopeInfo(int evalScopeInfoIndex, Scope scope)
    {
        // V8 records the ScopeInfo weakly in script->infos() for the CHECK in
        // CompileGlobalEval; V8Sharp does not need it.
    }

    public object Oddball(BoilerplateOddball oddball) => oddball switch
    {
        BoilerplateOddball.True => JSValue.True,
        BoilerplateOddball.False => JSValue.False,
        BoilerplateOddball.Null => JSValue.Null,
        BoilerplateOddball.Undefined => JSValue.Undefined,
        BoilerplateOddball.TheHole => JSValue.TheHole,
        BoilerplateOddball.Uninitialized => new JSValue(Objects.Oddball.Uninitialized),
        _ => throw new UnreachableException(),
    };

    // ---- IConstantPoolMaterializer ----------------------------------------------------------------

    public object RawString(object rawString) => rawString switch
    {
        // AstRawString::Internalize: the string is internalized once and kept.
        AstRawString s => s.string_ ?? InternalizeRawString(s),
        string s => isolate.Factory.InternalizeString(s),
        _ => throw new InvalidOperationException("V8Sharp: unexpected raw string " + rawString.GetType().Name),
    };

    object InternalizeRawString(AstRawString s) => InternalizeRawString(isolate, s);

    static JSString InternalizeRawString(Isolate isolate, AstRawString s)
    {
        JSString result = isolate.Factory.InternalizeString(s.Value);
        s.set_string(result);
        return result;
    }

    /// <summary>
    /// A function name as an internalized string. A one-segment name (most of
    /// them) is its raw string, internalized once (AstRawString::Internalize);
    /// a longer one is flattened and the result kept on the cons string.
    /// </summary>
    static JSString InternalizedName(Isolate isolate, AstConsString name)
    {
        if (name.string_ is JSString cached) return cached;
        IReadOnlyList<AstRawString> segments = name.ToRawStrings();
        JSString result = segments.Count == 1
            ? segments[0].string_ as JSString ?? InternalizeRawString(isolate, segments[0])
            : isolate.Factory.InternalizeString(name.ToFlatString());
        name.string_ = result;
        return result;
    }

    public object ConsString(object consString) => consString switch
    {
        AstConsString s => isolate.Factory.NewStringFromUtf16(s.ToFlatString()),
        string s => isolate.Factory.NewStringFromUtf16(s),
        _ => throw new InvalidOperationException("V8Sharp: unexpected cons string " + consString.GetType().Name),
    };

    public object BigInt(object bigint)
    {
        string digits = bigint is AstBigInt b ? b.c_str() : (string)bigint;
        return Objects.BigInt.StringToBigInt(isolate, isolate.Factory.NewStringFromUtf16(digits))
               ?? throw new InvalidOperationException("V8Sharp: invalid BigInt literal " + digits);
    }

    public object ScopeInfo(object scope) => scope switch
    {
        Scope s => (Objects.ScopeInfo)s.scope_info()!,
        Objects.ScopeInfo si => si,
        _ => throw new InvalidOperationException("V8Sharp: unexpected scope " + scope.GetType().Name),
    };

    public object Singleton(SingletonConstant singleton) => singleton switch
    {
        SingletonConstant.AsyncIteratorSymbol => ReadOnlyRoots.async_iterator_symbol,
        SingletonConstant.ClassFieldsSymbol => ReadOnlyRoots.class_fields_symbol,
        SingletonConstant.EmptyObjectBoilerplateDescription => s_emptyObjectBoilerplate,
        SingletonConstant.EmptyArrayBoilerplateDescription => s_emptyArrayBoilerplate,
        SingletonConstant.EmptyFixedArray => FixedArray.Empty,
        SingletonConstant.IteratorSymbol => ReadOnlyRoots.iterator_symbol,
        SingletonConstant.InterpreterTrampolineSymbol => ReadOnlyRoots.interpreter_trampoline_symbol,
        SingletonConstant.NaN => JSValue.NaN,
        _ => throw new UnreachableException(),
    };

    public object HeapNumber(double value) => JSValue.FromNumber(value);

    public object Smi(Smi value) => JSValue.FromInt(value.Value);

    public object TheHole => JSValue.TheHole;
}
