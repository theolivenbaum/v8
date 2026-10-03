// Port of src/interpreter/bytecode-generator.cc: function literals, classes,
// conditionals, literals, object and array literals.
using V8Sharp.Ast;
using V8Sharp.Codegen;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using static V8Sharp.Common.Globals;
using ToBooleanMode = V8Sharp.Interpreter.BytecodeArrayBuilder.ToBooleanMode;

namespace V8Sharp.Interpreter;

public sealed partial class BytecodeGenerator
{
    public override void VisitFunctionLiteral(FunctionLiteral expr)
    {
        if (!(info().literal().function_literal_id() < expr.function_literal_id()))
        {
            throw new InvalidOperationException("CHECK_LT(info_->literal()->function_literal_id(), expr->function_literal_id())");
        }
        if (expr.scope().outer_scope() != current_scope())
        {
            throw new InvalidOperationException("CHECK_EQ(expr->scope()->outer_scope(), current_scope())");
        }
        byte flags = CreateClosureFlags.Encode(expr.pretenure(), closure_scope().is_function_scope());
        int entry = builder().AllocateDeferredConstantPoolEntry();
        builder().CreateClosure(entry, GetNewClosureSlot(expr), flags);
        _functionLiterals.Add((expr, entry));
        AddToEagerLiteralsIfEager(expr);
    }

    void AddToEagerLiteralsIfEager(FunctionLiteral literal)
    {
        // V8 enqueues literal->should_parallel_compile() functions on the lazy
        // compile dispatcher (post_parallel_compile_tasks_*); V8Sharp has no
        // dispatcher, and the parser never marks literals for parallel compile
        // unless those flags are set.
        if (_eagerInnerLiterals is not null && literal.ShouldEagerCompile())
        {
            Debug.Assert(!_eagerInnerLiterals.Contains(literal));
            _eagerInnerLiterals.Add(literal);
        }
    }

    void BuildClassLiteral(ClassLiteral expr, Register name)
    {
        int class_boilerplate_entry = builder().AllocateDeferredConstantPoolEntry();
        _classLiterals.Add((expr, class_boilerplate_entry));

        VisitDeclarations(expr.scope().declarations());
        Register class_constructor = register_allocator().NewRegister();

        // Create the class brand symbol and store it on the context during class
        // evaluation. This will be stored in the instance later in the constructor.
        // We do this early so that invalid access to private methods or accessors
        // in computed property keys throw.
        if (expr.scope().brand() is not null)
        {
            Register brand = register_allocator().NewRegister();
            AstRawString class_name = expr.scope().class_variable() is not null
                ? expr.scope().class_variable()!.raw_name()
                : ast_string_constants().anonymous_string;
            builder()
                .LoadLiteral(class_name)
                .StoreAccumulatorInRegister(brand)
                .CallRuntime(FunctionId.CreatePrivateBrandSymbol, brand);
            register_allocator().ReleaseRegister(brand);

            BuildVariableAssignment(expr.scope().brand()!, Token.Init, HoleCheckMode.kElided);
        }

        var private_accessors = new AccessorTable<ClassLiteralProperty>();
        List<ClassLiteralProperty> private_members = expr.private_members() ?? s_noClassProperties;
        for (int i = 0; i < private_members.Count; i++)
        {
            ClassLiteralProperty property = private_members[i];
            Debug.Assert(property.is_private());
            switch (property.kind())
            {
                case ClassLiteralProperty.Kind.FIELD:
                {
                    // Initialize the private field variables early.
                    // Create the private name symbols for fields during class
                    // evaluation and store them on the context. These will be
                    // used as keys later during instance or static initialization.
                    using var private_name_register_scope = new RegisterAllocationScope(this);
                    Register private_name = register_allocator().NewRegister();
                    VisitForRegisterValue(property.key(), private_name);
                    builder()
                        .LoadLiteral(property.key().AsLiteral()!.AsRawPropertyName())
                        .StoreAccumulatorInRegister(private_name)
                        .CallRuntime(FunctionId.CreatePrivateNameSymbol, private_name);
                    BuildVariableAssignment(property.private_name_var(), Token.Init, HoleCheckMode.kElided);
                    break;
                }
                case ClassLiteralProperty.Kind.METHOD:
                {
                    using var register_scope = new RegisterAllocationScope(this);
                    VisitForAccumulatorValue(property.value());
                    BuildVariableAssignment(property.private_name_var(), Token.Init, HoleCheckMode.kElided);
                    break;
                }
                // Collect private accessors into a table to merge the creation of
                // those closures later.
                case ClassLiteralProperty.Kind.GETTER:
                {
                    Literal key = property.key().AsLiteral()!;
                    Debug.Assert(private_accessors.LookupOrInsert(key).getter is null);
                    private_accessors.LookupOrInsert(key).getter = property;
                    break;
                }
                case ClassLiteralProperty.Kind.SETTER:
                {
                    Literal key = property.key().AsLiteral()!;
                    Debug.Assert(private_accessors.LookupOrInsert(key).setter is null);
                    private_accessors.LookupOrInsert(key).setter = property;
                    break;
                }
                case ClassLiteralProperty.Kind.AUTO_ACCESSOR:
                {
                    Literal key = property.key().AsLiteral()!;
                    using var private_name_register_scope = new RegisterAllocationScope(this);
                    Register accessor_storage_private_name = register_allocator().NewRegister();
                    Variable accessor_storage_private_name_var =
                        property.auto_accessor_info().accessor_storage_name_proxy().var();
                    // We reuse the already internalized
                    // ".accessor-storage-<accessor_number>" strings that were defined in
                    // the parser instead of the "<name>accessor storage" string from the
                    // spec. The downsides are that is that these are the property names
                    // that will show up in devtools and in error messages.
                    // Additionally, a property can share a name with the corresponding
                    // property of their parent class, i.e. for classes defined as
                    // "class C {accessor x}" and "class D extends C {accessor y}",
                    // if "d = new D()", then d.x and d.y will share the name
                    // ".accessor-storage-0", (but a different private symbol).
                    // TODO(42202709): Get to a resolution on how to handle this naming
                    // issue before shipping the feature.
                    builder()
                        .LoadLiteral(accessor_storage_private_name_var.raw_name())
                        .StoreAccumulatorInRegister(accessor_storage_private_name)
                        .CallRuntime(FunctionId.CreatePrivateNameSymbol, accessor_storage_private_name);
                    BuildVariableAssignment(accessor_storage_private_name_var, Token.Init, HoleCheckMode.kElided);
                    Accessors<ClassLiteralProperty> accessor_pair = private_accessors.LookupOrInsert(key);
                    Debug.Assert(accessor_pair.getter is null);
                    accessor_pair.getter = property;
                    Debug.Assert(accessor_pair.setter is null);
                    accessor_pair.setter = property;
                    break;
                }
                default:
                    throw new UnreachableException();
            }
        }

        // Define private accessors early, using only a single call to the runtime for
        // each pair of corresponding getters and setters, in the order the first
        // component is declared.
        foreach ((Literal _, Accessors<ClassLiteralProperty> accessors) in private_accessors.ordered_accessors())
        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            RegisterList accessors_reg = register_allocator().NewRegisterList(2);
            ClassLiteralProperty? getter = accessors.getter;
            ClassLiteralProperty? setter = accessors.setter;
            Variable accessor_pair_var;
            if (getter is not null && getter.kind() == ClassLiteralProperty.Kind.AUTO_ACCESSOR)
            {
                Debug.Assert(setter == getter);
                AutoAccessorInfo auto_accessor_info = getter.auto_accessor_info();
                VisitForRegisterValue(auto_accessor_info.generated_getter(), accessors_reg[0]);
                VisitForRegisterValue(auto_accessor_info.generated_setter(), accessors_reg[1]);
                accessor_pair_var = auto_accessor_info.property_private_name_proxy().var();
            }
            else
            {
                VisitLiteralAccessor(getter, accessors_reg[0]);
                VisitLiteralAccessor(setter, accessors_reg[1]);
                accessor_pair_var = getter is not null ? getter.private_name_var() : setter!.private_name_var();
            }
            builder().CallRuntime(FunctionId.CreatePrivateAccessors, accessors_reg);
            BuildVariableAssignment(accessor_pair_var, Token.Init, HoleCheckMode.kElided);
        }

        {
            using var register_scope = new RegisterAllocationScope(this);
            RegisterList args = register_allocator().NewGrowableRegisterList();

            Register class_boilerplate = register_allocator().GrowRegisterList(ref args);
            Register class_constructor_in_args = register_allocator().GrowRegisterList(ref args);
            Register super_class = register_allocator().GrowRegisterList(ref args);
            Debug.Assert(kFirstDynamicArgumentIndex == args.RegisterCount);

            VisitForAccumulatorValueOrTheHole(expr.extends());
            builder().StoreAccumulatorInRegister(super_class);

            VisitFunctionLiteral(expr.constructor());
            builder()
                .StoreAccumulatorInRegister(class_constructor)
                .MoveRegister(class_constructor, class_constructor_in_args)
                .LoadConstantPoolEntry(class_boilerplate_entry)
                .StoreAccumulatorInRegister(class_boilerplate);

            // Create computed names and method values nodes to store into the literal.
            List<ClassLiteralProperty> public_members = expr.public_members() ?? s_noClassProperties;
            for (int i = 0; i < public_members.Count; i++)
            {
                ClassLiteralProperty property = public_members[i];
                if (property.is_computed_name())
                {
                    Register key = register_allocator().GrowRegisterList(ref args);

                    builder().SetExpressionAsStatementPosition(property.key());
                    BuildLoadPropertyKey(property, key);
                    if (property.is_static())
                    {
                        // The static prototype property is read only. We handle the non
                        // computed property name case in the parser. Since this is the only
                        // case where we need to check for an own read only property we
                        // special case this so we do not need to do this for every property.

                        var done = new BytecodeLabel();
                        builder()
                            .LoadLiteral(ast_string_constants().prototype_string)
                            .CompareOperation(Token.EqStrict, key, kFeedbackIsEmbedded)
                            .JumpIfFalse(ToBooleanMode.AlreadyBoolean, done)
                            .CallRuntime(FunctionId.ThrowStaticPrototypeError)
                            .Bind(done);
                    }

                    if (property.kind() == ClassLiteralProperty.Kind.FIELD)
                    {
                        Debug.Assert(!property.is_private());
                        // Initialize field's name variable with the computed name.
                        builder().LoadAccumulatorWithRegister(key);
                        BuildVariableAssignment(property.computed_name_var(), Token.Init, HoleCheckMode.kElided);
                    }
                }

                Debug.Assert(!property.is_private());

                if (property.kind() == ClassLiteralProperty.Kind.FIELD)
                {
                    // We don't compute field's value here, but instead do it in the
                    // initializer function.
                    continue;
                }

                if (property.kind() == ClassLiteralProperty.Kind.AUTO_ACCESSOR)
                {
                    {
                        using var private_name_register_scope = new RegisterAllocationScope(this);
                        Register name_register = register_allocator().NewRegister();
                        Variable accessor_storage_private_name_var =
                            property.auto_accessor_info().accessor_storage_name_proxy().var();
                        builder()
                            .LoadLiteral(accessor_storage_private_name_var.raw_name())
                            .StoreAccumulatorInRegister(name_register)
                            .CallRuntime(FunctionId.CreatePrivateNameSymbol, name_register);
                        BuildVariableAssignment(accessor_storage_private_name_var, Token.Init, HoleCheckMode.kElided);
                    }

                    Register getter = register_allocator().GrowRegisterList(ref args);
                    Register setter = register_allocator().GrowRegisterList(ref args);
                    AutoAccessorInfo auto_accessor_info = property.auto_accessor_info();
                    VisitForRegisterValue(auto_accessor_info.generated_getter(), getter);
                    VisitForRegisterValue(auto_accessor_info.generated_setter(), setter);
                    continue;
                }

                Register value = register_allocator().GrowRegisterList(ref args);
                VisitForRegisterValue(property.value(), value);
            }

            builder().CallRuntime(FunctionId.DefineClass, args);
        }

        // Assign to the home object variable. Accumulator already contains the
        // prototype.
        Variable? home_object_variable = expr.home_object();
        if (home_object_variable is not null)
        {
            Debug.Assert(home_object_variable.is_used());
            Debug.Assert(home_object_variable.IsContextSlot());
            BuildVariableAssignment(home_object_variable, Token.Init, HoleCheckMode.kElided);
        }
        Variable? static_home_object_variable = expr.static_home_object();
        if (static_home_object_variable is not null)
        {
            Debug.Assert(static_home_object_variable.is_used());
            Debug.Assert(static_home_object_variable.IsContextSlot());
            builder().LoadAccumulatorWithRegister(class_constructor);
            BuildVariableAssignment(static_home_object_variable, Token.Init, HoleCheckMode.kElided);
        }

        // Assign to class variable.
        Variable? class_variable = expr.scope().class_variable();
        if (class_variable is not null && class_variable.is_used())
        {
            Debug.Assert(class_variable.IsStackLocal() || class_variable.IsContextSlot());
            builder().LoadAccumulatorWithRegister(class_constructor);
            BuildVariableAssignment(class_variable, Token.Init, HoleCheckMode.kElided);
        }

        if (expr.instance_members_initializer_function() is not null)
        {
            VisitForAccumulatorValue(expr.instance_members_initializer_function()!);

            FeedbackSlot slot = feedback_spec().AddStoreICSlot(language_mode());
            builder()
                .StoreClassFieldsInitializer(class_constructor, feedback_index(slot))
                .LoadAccumulatorWithRegister(class_constructor);
        }

        if (expr.static_initializer() is not null)
        {
            // TODO(gsathya): This can be optimized away to be a part of the
            // class boilerplate in the future. The name argument can be
            // passed to the DefineClass runtime function and have it set
            // there.
            // TODO(v8:13451): Alternatively, port SetFunctionName to an ic so that we
            // can replace the runtime call to a dedicate bytecode here.
            if (name.IsValid)
            {
                using var name_register_scope = new RegisterAllocationScope(this);
                RegisterList name_args = register_allocator().NewRegisterList(2);
                builder()
                    .MoveRegister(class_constructor, name_args[0])
                    .MoveRegister(name, name_args[1])
                    .CallRuntime(FunctionId.SetFunctionName, name_args);
            }

            using var inner_register_scope = new RegisterAllocationScope(this);
            RegisterList args = register_allocator().NewRegisterList(1);
            Register initializer = VisitForRegisterValue(expr.static_initializer()!);

            builder()
                .MoveRegister(class_constructor, args[0])
                .CallProperty(initializer, args, feedback_index(feedback_spec().AddCallICSlot()));
        }
        builder().LoadAccumulatorWithRegister(class_constructor);
    }

    static readonly List<ClassLiteralProperty> s_noClassProperties = [];

    public override void VisitClassLiteral(ClassLiteral expr) => VisitClassLiteral(expr, Register.InvalidValue());

    void VisitClassLiteral(ClassLiteral expr, Register name)
    {
        using var current_scope = new CurrentScope(this, expr.scope());
        Debug.Assert(expr.scope() is not null);
        if (expr.scope().NeedsContext())
        {
            // Make sure to associate the source position for the class
            // after the block context is created. Otherwise we have a mismatch
            // between the scope and the context, where we already are in a
            // block context for the class, but not yet in the class scope. Only do
            // this if the current source position is inside the class scope though.
            // For example:
            //  * `var x = class {};` will break on `class` which is inside
            //    the class scope, so we expect the BlockContext to be pushed.
            //
            //  * `new class x {};` will break on `new` which is outside the
            //    class scope, so we expect the BlockContext to not be pushed yet.
            BytecodeSourceInfo? source_info = builder().MaybePopSourcePosition(expr.scope().start_position());
            BuildNewLocalBlockContext(expr.scope());
            using var scope = new ContextScope(this, expr.scope());
            if (source_info is { } si) builder().PushSourcePosition(si);
            BuildClassLiteral(expr, name);
        }
        else
        {
            BuildClassLiteral(expr, name);
        }
    }

    void BuildClassProperty(ClassLiteralProperty property)
    {
        using var register_scope = new RegisterAllocationScope(this);
        Register key = Register.InvalidValue();

        // Private methods are not initialized in BuildClassProperty.
        Debug.Assert(!property.is_private() || property.kind() == ClassLiteralProperty.Kind.FIELD ||
                     property.is_auto_accessor());
        builder().SetExpressionPosition(property.key());

        bool is_literal_store = property.key().IsPropertyName() && !property.is_computed_name() &&
                                !property.is_private() && !property.is_auto_accessor();

        if (!is_literal_store)
        {
            key = register_allocator().NewRegister();
            if (property.is_auto_accessor())
            {
                Variable var = property.auto_accessor_info().accessor_storage_name_proxy().var();
                BuildVariableLoad(var, HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(key);
            }
            else if (property.is_computed_name())
            {
                Debug.Assert(property.kind() == ClassLiteralProperty.Kind.FIELD);
                Debug.Assert(!property.is_private());
                Variable var = property.computed_name_var();
                // The computed name is already evaluated and stored in a variable at
                // class definition time.
                BuildVariableLoad(var, HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(key);
            }
            else if (property.is_private())
            {
                Variable private_name_var = property.private_name_var();
                BuildVariableLoad(private_name_var, HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(key);
            }
            else
            {
                VisitForRegisterValue(property.key(), key);
            }
        }

        builder().SetExpressionAsStatementPosition(property.value());

        if (is_literal_store)
        {
            VisitForAccumulatorValue(property.value());
            FeedbackSlot slot = feedback_spec().AddDefineNamedOwnICSlot();
            builder().DefineNamedOwnProperty(builder().Receiver(), property.key().AsLiteral()!.AsRawPropertyName(),
                                             feedback_index(slot));
        }
        else
        {
            DefineKeyedOwnPropertyFlags flags = DefineKeyedOwnPropertyFlags.NoFlags;
            if (property.NeedsSetFunctionName())
            {
                // Static class fields require the name property to be set on
                // the class, meaning we can't wait until the
                // DefineKeyedOwnProperty call later to set the name.
                if (property.value().IsClassLiteral() &&
                    property.value().AsClassLiteral()!.static_initializer() is not null)
                {
                    VisitClassLiteral(property.value().AsClassLiteral()!, key);
                }
                else
                {
                    VisitForAccumulatorValue(property.value());
                    flags |= DefineKeyedOwnPropertyFlags.SetFunctionName;
                }
            }
            else
            {
                VisitForAccumulatorValue(property.value());
            }
            FeedbackSlot slot = feedback_spec().AddDefineKeyedOwnICSlot();
            builder().DefineKeyedOwnProperty(builder().Receiver(), key, flags, feedback_index(slot));
        }
    }

    public override void VisitInitializeClassMembersStatement(InitializeClassMembersStatement stmt)
    {
        List<ClassLiteralProperty> fields = stmt.fields();
        for (int i = 0; i < fields.Count; i++)
        {
            BuildClassProperty(fields[i]);
        }
    }

    public override void VisitInitializeClassStaticElementsStatement(InitializeClassStaticElementsStatement stmt)
    {
        List<ClassLiteralStaticElement> elements = stmt.elements();
        for (int i = 0; i < elements.Count; i++)
        {
            ClassLiteralStaticElement element = elements[i];
            switch (element.kind())
            {
                case ClassLiteralStaticElement.Kind.PROPERTY:
                    BuildClassProperty(element.property());
                    break;
                case ClassLiteralStaticElement.Kind.STATIC_BLOCK:
                    VisitBlock(element.static_block());
                    break;
            }
        }
    }

    public override void VisitAutoAccessorGetterBody(AutoAccessorGetterBody stmt)
    {
        BuildVariableLoad(stmt.name_proxy().var(), HoleCheckMode.kElided);
        builder().LoadKeyedProperty(builder().Receiver(), feedback_index(feedback_spec().AddKeyedLoadICSlot()));
        BuildReturn(stmt.position());
    }

    public override void VisitAutoAccessorSetterBody(AutoAccessorSetterBody stmt)
    {
        Register key = register_allocator().NewRegister();
        Register value = builder().Parameter(0);
        FeedbackSlot slot = feedback_spec().AddKeyedStoreICSlot(language_mode());
        BuildVariableLoad(stmt.name_proxy().var(), HoleCheckMode.kElided);

        builder()
            .StoreAccumulatorInRegister(key)
            .LoadAccumulatorWithRegister(value)
            .SetKeyedProperty(builder().Receiver(), key, feedback_index(slot), language_mode());
    }

    void BuildInvalidPropertyAccess(MessageTemplate tmpl, Property property)
    {
        using var register_scope = new RegisterAllocationScope(this);
        AstRawString name = property.key().AsVariableProxy()!.raw_name();
        RegisterList args = register_allocator().NewRegisterList(2);
        builder()
            .LoadLiteral(Smi.FromInt((int)tmpl))
            .StoreAccumulatorInRegister(args[0])
            .LoadLiteral(name)
            .StoreAccumulatorInRegister(args[1])
            .CallRuntime(FunctionId.NewTypeError, args)
            .Throw();
    }

    void BuildPrivateBrandInitialization(Register receiver, Variable brand)
    {
        BuildVariableLoad(brand, HoleCheckMode.kElided);
        int depth = execution_context().ContextChainDepth(brand.scope()!);
        ContextScope? class_context = execution_context().Previous(depth);
        if (class_context is not null)
        {
            Register brand_reg = register_allocator().NewRegister();
            FeedbackSlot slot = feedback_spec().AddDefineKeyedOwnICSlot();
            builder()
                .StoreAccumulatorInRegister(brand_reg)
                .LoadAccumulatorWithRegister(class_context.reg())
                .DefineKeyedOwnProperty(receiver, brand_reg, DefineKeyedOwnPropertyFlags.NoFlags, feedback_index(slot));
        }
        else
        {
            // We are in the slow case where super() is called from a nested
            // arrow function or an eval(), so the class scope context isn't
            // tracked in a context register in the stack, and we have to
            // walk the context chain from the runtime to find it.
            Debug.Assert(info().literal().scope().outer_scope() != brand.scope());
            RegisterList brand_args = register_allocator().NewRegisterList(4);
            builder()
                .StoreAccumulatorInRegister(brand_args[1])
                .MoveRegister(receiver, brand_args[0])
                .MoveRegister(execution_context().reg(), brand_args[2])
                .LoadLiteral(Smi.FromInt(depth))
                .StoreAccumulatorInRegister(brand_args[3])
                .CallRuntime(FunctionId.AddPrivateBrand, brand_args);
        }
    }

    void BuildInstanceMemberInitialization(Register constructor, Register instance)
    {
        RegisterList args = register_allocator().NewRegisterList(1);
        Register initializer = register_allocator().NewRegister();

        FeedbackSlot slot = feedback_spec().AddLoadICSlot();
        var done = new BytecodeLabel();

        builder()
            .LoadClassFieldsInitializer(constructor, feedback_index(slot))
            // TODO(gsathya): This jump can be elided for the base
            // constructor and derived constructor. This is only required
            // when called from an arrow function.
            .JumpIfUndefined(done)
            .StoreAccumulatorInRegister(initializer)
            .MoveRegister(instance, args[0])
            .CallProperty(initializer, args, feedback_index(feedback_spec().AddCallICSlot()))
            .Bind(done);
    }

    public override void VisitNativeFunctionLiteral(NativeFunctionLiteral expr)
    {
        int entry = builder().AllocateDeferredConstantPoolEntry();
        // Native functions don't use argument adaption and so have the special
        // kDontAdaptArgumentsSentinel as their parameter count.
        const ushort kDontAdaptArgumentsSentinel = 0;
        int index = feedback_spec().AddCreateClosureParameterCount(kDontAdaptArgumentsSentinel);
        byte flags = CreateClosureFlags.Encode(false, false);
        builder().CreateClosure(entry, index, flags);
        _nativeFunctionLiterals.Add((expr, entry));
    }

    public override void VisitConditionalChain(ConditionalChain expr)
    {
        using var conditional_builder = new ConditionalChainControlFlowBuilder(
            builder(), _blockCoverageBuilder, expr, expr.conditional_chain_length());

        var merge_elider = new HoleCheckElisionMergeScope(this);
        {
            bool should_visit_else_expression = true;
            using var elider = new HoleCheckElisionScope(this);
            for (int i = 0; i < expr.conditional_chain_length(); ++i)
            {
                if (expr.condition_at(i).ToBooleanIsTrue())
                {
                    // Generate then block unconditionally as always true.
                    should_visit_else_expression = false;
                    using var branch = merge_elider.NewBranch();
                    conditional_builder.ThenAt(i);
                    VisitForAccumulatorValue(expr.then_expression_at(i));
                    break;
                }
                else if (expr.condition_at(i).ToBooleanIsFalse())
                {
                    // Generate else block unconditionally by skipping the then block.
                    using var branch = merge_elider.NewBranch();
                    conditional_builder.ElseAt(i);
                }
                else
                {
                    VisitForTest(expr.condition_at(i), conditional_builder.ThenLabelsAt(i),
                                 conditional_builder.ElseLabelsAt(i), TestFallthrough.kThen);
                    {
                        using var branch = merge_elider.NewBranch();
                        conditional_builder.ThenAt(i);
                        VisitForAccumulatorValue(expr.then_expression_at(i));
                    }
                    conditional_builder.JumpToEnd();
                    {
                        using var branch = merge_elider.NewBranch();
                        conditional_builder.ElseAt(i);
                    }
                }
            }

            if (should_visit_else_expression)
            {
                VisitForAccumulatorValue(expr.else_expression());
            }
        }
        merge_elider.Merge();
    }

    public override void VisitConditional(Conditional expr)
    {
        using var conditional_builder =
            new ConditionalControlFlowBuilder(builder(), _blockCoverageBuilder, expr, nodeIsIfStatement: false);

        if (expr.condition().ToBooleanIsTrue())
        {
            // Generate then block unconditionally as always true.
            conditional_builder.Then();
            VisitForAccumulatorValue(expr.then_expression());
        }
        else if (expr.condition().ToBooleanIsFalse())
        {
            // Generate else block unconditionally if it exists.
            conditional_builder.Else();
            VisitForAccumulatorValue(expr.else_expression());
        }
        else
        {
            VisitForTest(expr.condition(), conditional_builder.ThenLabels, conditional_builder.ElseLabels,
                         TestFallthrough.kThen);

            var merge_elider = new HoleCheckElisionMergeScope(this);
            conditional_builder.Then();
            {
                using var branch_elider = merge_elider.NewBranch();
                VisitForAccumulatorValue(expr.then_expression());
            }
            conditional_builder.JumpToEnd();

            conditional_builder.Else();
            {
                using var branch_elider = merge_elider.NewBranch();
                VisitForAccumulatorValue(expr.else_expression());
            }

            merge_elider.Merge();
        }
    }

    public override void VisitLiteral(Literal expr)
    {
        if (execution_result().IsEffect()) return;
        switch (expr.type())
        {
            case Literal.Type.kSmi:
                builder().LoadLiteral(Smi.FromInt(expr.AsSmiLiteral()));
                break;
            case Literal.Type.kHeapNumber:
                builder().LoadLiteral(expr.AsNumber());
                break;
            case Literal.Type.kUndefined:
                builder().LoadUndefined();
                break;
            case Literal.Type.kBoolean:
                builder().LoadBoolean(expr.ToBooleanIsTrue());
                execution_result().SetResultIsBoolean();
                break;
            case Literal.Type.kNull:
                builder().LoadNull();
                break;
            case Literal.Type.kTheHole:
                builder().LoadTheHole();
                break;
            case Literal.Type.kString:
                builder().LoadLiteral(expr.AsRawString());
                execution_result().SetResultIsInternalizedString();
                break;
            case Literal.Type.kConsString:
                builder().LoadLiteral(expr.AsConsString());
                break;
            case Literal.Type.kBigInt:
                builder().LoadLiteral(expr.AsBigInt());
                break;
        }
    }

    public override void VisitRegExpLiteral(RegExpLiteral expr)
    {
        // Materialize a regular expression literal.
        builder().CreateRegExpLiteral(expr.raw_pattern(), feedback_index(feedback_spec().AddLiteralSlot()),
                                      expr.flags());
    }

    void BuildCreateObjectLiteral(Register literal, byte flags, int entry)
    {
        // TODO(cbruni): Directly generate runtime call for literals we cannot
        // optimize once the CreateShallowObjectLiteral stub is in sync with the TF
        // optimizations.
        int literal_index = feedback_index(feedback_spec().AddLiteralSlot());
        builder()
            .CreateObjectLiteral(entry, literal_index, flags)
            .StoreAccumulatorInRegister(literal);
    }

    public override void VisitObjectLiteral(ObjectLiteral expr)
    {
        expr.builder().InitDepthAndFlags();

        // Fast path for the empty object literal which doesn't need an
        // AllocationSite.
        if (expr.builder().IsEmptyObjectLiteral())
        {
            Debug.Assert(expr.builder().IsFastCloningSupported());
            builder().CreateEmptyObjectLiteral();
            return;
        }

        Variable? home_object = expr.home_object();
        if (home_object is not null)
        {
            Debug.Assert(home_object.is_used());
            Debug.Assert(home_object.IsContextSlot());
        }
        var object_literal_context_scope = new MultipleEntryBlockContextScope(this, home_object?.scope());

        // Deep-copy the literal boilerplate.
        byte flags = CreateObjectLiteralFlags.Encode(expr.builder().ComputeFlags(),
                                                     expr.builder().IsFastCloningSupported());

        Register literal = register_allocator().NewRegister();

        // Create literal object.
        int property_index = 0;
        List<ObjectLiteralProperty> properties = expr.properties();
        bool clone_object_spread = properties[0].kind() == ObjectLiteralProperty.Kind.SPREAD;
        if (clone_object_spread)
        {
            // Avoid the slow path for spreads in the following common cases:
            //   1) `let obj = { ...source }`
            //   2) `let obj = { ...source, override: 1 }`
            //   3) `let obj = { ...source, ...overrides }`
            using var register_scope = new RegisterAllocationScope(this);
            Expression property = properties[0].value();
            Register from_value = VisitForRegisterValue(property);
            int clone_index = feedback_index(feedback_spec().AddCloneObjectSlot());
            builder().CloneObject(from_value, flags, clone_index);
            builder().StoreAccumulatorInRegister(literal);
            property_index++;
        }
        else
        {
            int entry;
            // If constant properties is an empty fixed array, use a cached empty fixed
            // array to ensure it's only added to the constant pool once.
            if (expr.builder().properties_count() == 0)
            {
                entry = builder().EmptyObjectBoilerplateDescriptionConstantPoolEntry();
            }
            else
            {
                entry = builder().AllocateDeferredConstantPoolEntry();
                _objectLiterals.Add((expr.builder(), entry));
            }
            BuildCreateObjectLiteral(literal, flags, entry);
        }

        // Store computed values into the literal.
        var accessor_table = new AccessorTable<ObjectLiteralProperty>();
        for (; property_index < properties.Count; property_index++)
        {
            ObjectLiteralProperty property = properties[property_index];
            if (property.is_computed_name()) break;
            if (!clone_object_spread && property.IsCompileTimeValue()) continue;

            using var inner_register_scope = new RegisterAllocationScope(this);
            Literal key = property.key().AsLiteral()!;
            switch (property.kind())
            {
                case ObjectLiteralProperty.Kind.SPREAD:
                    throw new UnreachableException();
                case ObjectLiteralProperty.Kind.CONSTANT:
                case ObjectLiteralProperty.Kind.MATERIALIZED_LITERAL:
                case ObjectLiteralProperty.Kind.COMPUTED:
                {
                    Debug.Assert(property.kind() == ObjectLiteralProperty.Kind.COMPUTED || clone_object_spread ||
                                 !property.value().IsCompileTimeValue());
                    // It is safe to use [[Put]] here because the boilerplate already
                    // contains computed properties with an uninitialized value.
                    Register key_reg = Register.InvalidValue();
                    if (key.IsStringLiteral())
                    {
                        Debug.Assert(key.IsPropertyName());
                    }
                    else
                    {
                        key_reg = register_allocator().NewRegister();
                        builder().SetExpressionPosition(property.key());
                        VisitForRegisterValue(property.key(), key_reg);
                    }

                    object_literal_context_scope.SetEnteredIf(property.value().IsConciseMethodDefinition());
                    builder().SetExpressionPosition(property.value());

                    if (property.emit_store())
                    {
                        VisitForAccumulatorValue(property.value());
                        if (key.IsStringLiteral())
                        {
                            FeedbackSlot slot = feedback_spec().AddDefineNamedOwnICSlot();
                            builder().DefineNamedOwnProperty(literal, key.AsRawPropertyName(), feedback_index(slot));
                        }
                        else
                        {
                            FeedbackSlot slot = feedback_spec().AddDefineKeyedOwnICSlot();
                            builder().DefineKeyedOwnProperty(literal, key_reg, DefineKeyedOwnPropertyFlags.NoFlags,
                                                             feedback_index(slot));
                        }
                    }
                    else
                    {
                        VisitForEffect(property.value());
                    }
                    break;
                }
                case ObjectLiteralProperty.Kind.PROTOTYPE:
                {
                    // __proto__:null is handled by CreateObjectLiteral.
                    if (property.IsNullPrototype()) break;
                    Debug.Assert(property.emit_store());
                    Debug.Assert(!property.NeedsSetFunctionName());
                    RegisterList args = register_allocator().NewRegisterList(2);
                    builder().MoveRegister(literal, args[0]);
                    object_literal_context_scope.SetEnteredIf(false);
                    builder().SetExpressionPosition(property.value());
                    VisitForRegisterValue(property.value(), args[1]);
                    builder().CallRuntime(FunctionId.InternalSetPrototype, args);
                    break;
                }
                case ObjectLiteralProperty.Kind.GETTER:
                    if (property.emit_store())
                    {
                        accessor_table.LookupOrInsert(key).getter = property;
                    }
                    break;
                case ObjectLiteralProperty.Kind.SETTER:
                    if (property.emit_store())
                    {
                        accessor_table.LookupOrInsert(key).setter = property;
                    }
                    break;
            }
        }

        // Define accessors, using only a single call to the runtime for each pair
        // of corresponding getters and setters.
        object_literal_context_scope.SetEnteredIf(true);
        foreach ((Literal key, Accessors<ObjectLiteralProperty> accessors) in accessor_table.ordered_accessors())
        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            RegisterList args = register_allocator().NewRegisterList(5);
            builder().MoveRegister(literal, args[0]);
            VisitForRegisterValue(key, args[1]);
            VisitLiteralAccessor(accessors.getter, args[2]);
            VisitLiteralAccessor(accessors.setter, args[3]);
            builder()
                .LoadLiteral(Smi.FromInt(NONE))
                .StoreAccumulatorInRegister(args[4])
                .CallRuntime(FunctionId.DefineAccessorPropertyUnchecked, args);
        }

        // Object literals have two parts. The "static" part on the left contains no
        // computed property names, and so we can compute its map ahead of time; see
        // Runtime_CreateObjectLiteralBoilerplate. The second "dynamic" part starts
        // with the first computed property name and continues with all properties to
        // its right. All the code from above initializes the static component of the
        // object literal, and arranges for the map of the result to reflect the
        // static order in which the keys appear. For the dynamic properties, we
        // compile them into a series of "SetOwnProperty" runtime calls. This will
        // preserve insertion order.
        for (; property_index < properties.Count; property_index++)
        {
            ObjectLiteralProperty property = properties[property_index];
            using var inner_register_scope = new RegisterAllocationScope(this);

            bool should_be_in_object_literal_scope =
                property.value().IsConciseMethodDefinition() || property.value().IsAccessorFunctionDefinition();

            if (property.IsPrototype())
            {
                // __proto__:null is handled by CreateObjectLiteral.
                if (property.IsNullPrototype()) continue;
                Debug.Assert(property.emit_store());
                Debug.Assert(!property.NeedsSetFunctionName());
                RegisterList args = register_allocator().NewRegisterList(2);
                builder().MoveRegister(literal, args[0]);

                Debug.Assert(!should_be_in_object_literal_scope);
                object_literal_context_scope.SetEnteredIf(false);
                builder().SetExpressionPosition(property.value());
                VisitForRegisterValue(property.value(), args[1]);
                builder().CallRuntime(FunctionId.InternalSetPrototype, args);
                continue;
            }

            switch (property.kind())
            {
                case ObjectLiteralProperty.Kind.CONSTANT:
                case ObjectLiteralProperty.Kind.COMPUTED:
                case ObjectLiteralProperty.Kind.MATERIALIZED_LITERAL:
                {
                    // Computed property keys don't belong to the object literal scope (even
                    // if they're syntactically inside it).
                    if (property.is_computed_name())
                    {
                        object_literal_context_scope.SetEnteredIf(false);
                    }
                    Register key = register_allocator().NewRegister();
                    BuildLoadPropertyKey(property, key);

                    object_literal_context_scope.SetEnteredIf(should_be_in_object_literal_scope);
                    builder().SetExpressionPosition(property.value());

                    DefineKeyedOwnPropertyInLiteralFlags data_property_flags =
                        DefineKeyedOwnPropertyInLiteralFlags.NoFlags;
                    if (property.NeedsSetFunctionName())
                    {
                        // Static class fields require the name property to be set on
                        // the class, meaning we can't wait until the
                        // DefineKeyedOwnPropertyInLiteral call later to set the name.
                        if (property.value().IsClassLiteral() &&
                            property.value().AsClassLiteral()!.static_initializer() is not null)
                        {
                            VisitClassLiteral(property.value().AsClassLiteral()!, key);
                        }
                        else
                        {
                            data_property_flags |= DefineKeyedOwnPropertyInLiteralFlags.SetFunctionName;
                            VisitForAccumulatorValue(property.value());
                        }
                    }
                    else
                    {
                        VisitForAccumulatorValue(property.value());
                    }

                    FeedbackSlot slot = feedback_spec().AddDefineKeyedOwnPropertyInLiteralICSlot();
                    builder().DefineKeyedOwnPropertyInLiteral(literal, key, data_property_flags, feedback_index(slot));
                    break;
                }
                case ObjectLiteralProperty.Kind.GETTER:
                case ObjectLiteralProperty.Kind.SETTER:
                {
                    // Computed property keys don't belong to the object literal scope (even
                    // if they're syntactically inside it).
                    if (property.is_computed_name())
                    {
                        object_literal_context_scope.SetEnteredIf(false);
                    }
                    RegisterList args = register_allocator().NewRegisterList(4);
                    builder().MoveRegister(literal, args[0]);
                    BuildLoadPropertyKey(property, args[1]);

                    Debug.Assert(should_be_in_object_literal_scope);
                    object_literal_context_scope.SetEnteredIf(true);
                    builder().SetExpressionPosition(property.value());
                    VisitForRegisterValue(property.value(), args[2]);
                    builder()
                        .LoadLiteral(Smi.FromInt(NONE))
                        .StoreAccumulatorInRegister(args[3]);
                    FunctionId function_id = property.kind() == ObjectLiteralProperty.Kind.GETTER
                        ? FunctionId.DefineGetterPropertyUnchecked
                        : FunctionId.DefineSetterPropertyUnchecked;
                    builder().CallRuntime(function_id, args);
                    break;
                }
                case ObjectLiteralProperty.Kind.SPREAD:
                {
                    // TODO(olivf, chrome:1204540) This can be slower than the Babel
                    // translation. Should we compile this to a copying loop in bytecode?
                    RegisterList args = register_allocator().NewRegisterList(2);
                    builder().MoveRegister(literal, args[0]);
                    builder().SetExpressionPosition(property.value());
                    object_literal_context_scope.SetEnteredIf(false);
                    VisitForRegisterValue(property.value(), args[1]);
                    builder().CallRuntime(FunctionId.InlineCopyDataProperties, args);
                    break;
                }
                case ObjectLiteralProperty.Kind.PROTOTYPE:
                    throw new UnreachableException(); // Handled specially above.
            }
        }

        if (home_object is not null)
        {
            object_literal_context_scope.SetEnteredIf(true);
            builder().LoadAccumulatorWithRegister(literal);
            BuildVariableAssignment(home_object, Token.Init, HoleCheckMode.kElided);
        }
        // Make sure to exit the scope before materialising the value into the
        // accumulator, to prevent the context scope from clobbering it.
        object_literal_context_scope.SetEnteredIf(false);
        builder().LoadAccumulatorWithRegister(literal);
    }

    // Fill an array with values from an iterator, starting at a given index. It is
    // guaranteed that the loop will only terminate if the iterator is exhausted, or
    // if one of iterator.next(), value.done, or value.value fail.
    //
    // In pseudocode:
    //
    // loop {
    //   value = iterator.next()
    //   if (value.done) break;
    //   value = value.value
    //   array[index++] = value
    // }
    void BuildFillArrayWithIterator(IteratorRecord iterator, Register array, Register index, Register value,
                                    FeedbackSlot next_value_slot, FeedbackSlot next_done_slot,
                                    FeedbackSlot element_slot)
    {
        Debug.Assert(array.IsValid);
        Debug.Assert(index.IsValid);
        Debug.Assert(value.IsValid);

        using LoopBuilder loop_builder = NewLoopBuilder(null);
        using var loop_scope = new LoopScope(this, loop_builder);

        // Call the iterator's .next() method. Break from the loop if the `done`
        // property is truthy, otherwise load the value from the iterator result and
        // append the argument.
        BuildIteratorNext(iterator, value);
        builder().LoadNamedProperty(value, ast_string_constants().done_string,
                                    feedback_index(feedback_spec().AddLoadICSlot()));
        loop_builder.BreakIfTrue(ToBooleanMode.ConvertToBoolean);

        loop_builder.LoopBody();
        builder()
            // value = value.value
            .LoadNamedProperty(value, ast_string_constants().value_string, feedback_index(next_value_slot))
            // array[index] = value
            .StoreInArrayLiteral(array, index, feedback_index(element_slot))
            // index++
            .LoadAccumulatorWithRegister(index)
            .UnaryOperation(Token.Inc, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(index);
        loop_builder.BindContinueTarget();
    }

    void BuildCreateArrayLiteral(List<Expression> elements, ArrayLiteral? expr)
    {
        using var register_scope = new RegisterAllocationScope(this);
        // Make this the first register allocated so that it has a chance of aliasing
        // the next register allocated after returning from this function.
        Register array = register_allocator().NewRegister();
        Register index = register_allocator().NewRegister();
        var element_slot = new SharedFeedbackSlot(feedback_spec(), FeedbackSlotKind.kStoreInArrayLiteral);
        int current = 0;
        int end = elements.Count;
        bool is_empty = elements.Count == 0;

        if (!is_empty && elements[current].IsSpread())
        {
            // If we have a leading spread, use CreateArrayFromIterable to create
            // an array from it and then add the remaining components to that array.
            VisitForAccumulatorValue(elements[current]);
            builder().SetExpressionPosition(elements[current].AsSpread()!.expression());
            builder().CreateArrayFromIterable().StoreAccumulatorInRegister(array);

            if (++current != end)
            {
                // If there are remaining elements, prepare the index register that is
                // used for adding those elements. The next index is the length of the
                // newly created array.
                AstRawString length = ast_string_constants().length_string;
                int length_load_slot = feedback_index(feedback_spec().AddLoadICSlot());
                builder()
                    .LoadNamedProperty(array, length, length_load_slot)
                    .StoreAccumulatorInRegister(index);
            }
        }
        else
        {
            // There are some elements before the first (if any) spread, and we can
            // use a boilerplate when creating the initial array from those elements.

            // First, allocate a constant pool entry for the boilerplate that will
            // be created during finalization, and will contain all the constant
            // elements before the first spread. This also handle the empty array case
            // and one-shot optimization.

            ArrayLiteralBoilerplateBuilder array_literal_builder;
            if (expr is not null)
            {
                array_literal_builder = expr.builder();
            }
            else
            {
                Debug.Assert(elements.Count != 0);

                // get first_spread_index
                int first_spread_index = -1;
                for (int i = 0; i < elements.Count; i++)
                {
                    if (elements[i].IsSpread())
                    {
                        first_spread_index = i;
                        break;
                    }
                }

                array_literal_builder = new ArrayLiteralBoilerplateBuilder(elements, first_spread_index);
                array_literal_builder.InitDepthAndFlags();
            }

            byte flags = CreateArrayLiteralFlags.Encode(array_literal_builder.IsFastCloningSupported(),
                                                        array_literal_builder.ComputeFlags());
            if (is_empty)
            {
                // Empty array literal fast-path.
                int literal_index = feedback_index(feedback_spec().AddLiteralSlot());
                Debug.Assert(array_literal_builder.IsFastCloningSupported());
                builder().CreateEmptyArrayLiteral(literal_index);
            }
            else
            {
                // Create array literal from boilerplate.
                int entry = builder().AllocateDeferredConstantPoolEntry();
                _arrayLiterals.Add((array_literal_builder, entry));
                int literal_index = feedback_index(feedback_spec().AddLiteralSlot());
                builder().CreateArrayLiteral(entry, literal_index, flags);
            }
            builder().StoreAccumulatorInRegister(array);

            int first_spread_or_end = array_literal_builder.first_spread_index() >= 0
                ? current + array_literal_builder.first_spread_index()
                : end;

            // Insert the missing non-constant elements, up until the first spread
            // index, into the initial array (the remaining elements will be inserted
            // below).
            Debug.Assert(current == 0);
            int array_index = 0;
            for (; current != first_spread_or_end; ++current, array_index++)
            {
                Expression subexpr = elements[current];
                Debug.Assert(!subexpr.IsSpread());
                // Skip the constants.
                if (subexpr.IsCompileTimeValue()) continue;

                builder()
                    .LoadLiteral(Smi.FromInt(array_index))
                    .StoreAccumulatorInRegister(index);
                VisitForAccumulatorValue(subexpr);
                builder().StoreInArrayLiteral(array, index, feedback_index(element_slot.Get()));
            }

            if (current != end)
            {
                // If there are remaining elements, prepare the index register
                // to store the next element, which comes from the first spread.
                builder()
                    .LoadLiteral(Smi.FromInt(array_index))
                    .StoreAccumulatorInRegister(index);
            }
        }

        // Now build insertions for the remaining elements from current to end.
        var length_slot = new SharedFeedbackSlot(feedback_spec(), FeedbackVectorSpec.GetStoreICSlot(LanguageMode.Strict));
        for (; current != end; ++current)
        {
            Expression subexpr = elements[current];
            if (subexpr.IsSpread())
            {
                using var scope = new RegisterAllocationScope(this);
                builder().SetExpressionPosition(subexpr.AsSpread()!.expression());
                VisitForAccumulatorValue(subexpr.AsSpread()!.expression());
                builder().SetExpressionPosition(subexpr.AsSpread()!.expression());
                IteratorRecord iterator = BuildGetIteratorRecord(IteratorType.kNormal);

                Register value = register_allocator().NewRegister();
                FeedbackSlot next_value_load_slot = feedback_spec().AddLoadICSlot();
                FeedbackSlot next_done_load_slot = feedback_spec().AddLoadICSlot();
                FeedbackSlot real_element_slot = element_slot.Get();
                BuildFillArrayWithIterator(iterator, array, index, value, next_value_load_slot, next_done_load_slot,
                                           real_element_slot);
            }
            else if (!subexpr.IsTheHoleLiteral())
            {
                // literal[index++] = subexpr
                VisitForAccumulatorValue(subexpr);
                builder()
                    .StoreInArrayLiteral(array, index, feedback_index(element_slot.Get()))
                    .LoadAccumulatorWithRegister(index);
                // Only increase the index if we are not the last element.
                if (current + 1 != end)
                {
                    builder()
                        .UnaryOperation(Token.Inc, kFeedbackIsEmbedded)
                        .StoreAccumulatorInRegister(index);
                }
            }
            else
            {
                // literal.length = ++index
                // length_slot is only used when there are holes.
                AstRawString length = ast_string_constants().length_string;
                builder()
                    .LoadAccumulatorWithRegister(index)
                    .UnaryOperation(Token.Inc, kFeedbackIsEmbedded)
                    .StoreAccumulatorInRegister(index)
                    .SetNamedProperty(array, length, feedback_index(length_slot.Get()), LanguageMode.Strict);
            }
        }

        builder().LoadAccumulatorWithRegister(array);
    }

    public override void VisitArrayLiteral(ArrayLiteral expr)
    {
        expr.builder().InitDepthAndFlags();
        BuildCreateArrayLiteral(expr.values(), expr);
    }
}
