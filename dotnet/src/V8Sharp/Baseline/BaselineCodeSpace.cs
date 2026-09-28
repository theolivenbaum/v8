// Where baseline code lives: V8's code space, for IL. Each isolate gets one
// collectible dynamic assembly (AssemblyBuilderAccess.Run); each
// baseline-compiled function is a static method of its own type in it.
//
// Why not DynamicMethod: RyuJIT compiles a DynamicMethod once, fully
// optimized, with no tiering. Baseline methods inline the IC and arithmetic
// fast paths, which makes that compile cost milliseconds per function, far
// more than V8's Sparkplug (which exists to compile fast). Methods of a
// dynamic assembly take part in RyuJIT's tiered compilation: they start as
// quickly jitted tier-0 code, hot ones are recompiled with full optimization
// (and dynamic PGO) in the background, and long loops in tier-0 code move to
// optimized code through RyuJIT's own OSR. This is the layering of
// architecture.md section 9: V8's tiers above, RyuJIT's tiers below.
//
// Deviation: RyuJIT does not tier methods of collectible assemblies, so the
// code space is one non-collectible assembly per process, shared by all
// isolates: baseline code is never freed (V8 collects code objects). The
// V8SHARP_BASELINE_DYNAMICMETHOD=1 environment variable switches to
// collectible DynamicMethods (compiled once with full optimization).
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace V8Sharp.Baseline
{
internal sealed class BaselineCodeSpace
{
    readonly ModuleBuilder _module;
    int _counter;

    BaselineCodeSpace()
    {
        var name = new AssemblyName("V8Sharp.BaselineCode." + Interlocked.Increment(ref s_spaces).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        // The generated code calls into V8Sharp's internals (as V8's code calls
        // builtins); the runtime honours IgnoresAccessChecksTo for dynamic assemblies.
        ConstructorInfo ignoreAccess = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute)
            .GetConstructor([typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(ignoreAccess, [typeof(BaselineCodeSpace).Assembly.GetName().Name!]));
        _module = assembly.DefineDynamicModule(name.Name!);
    }

    static int s_spaces;
    static BaselineCodeSpace? s_current;
    static readonly Lock s_lock = new();

    /// <summary>
    /// Types per assembly: TypeBuilder.CreateType gets slower as a module grows
    /// (one module: 1.5 ms per function at 1000 types, 6 ms at 10000), while each
    /// new module resolves its member tokens again; 1024 measured best (about
    /// 1 ms per function to emit, create and tier-0 jit).
    /// </summary>
    const int kTypesPerAssembly = 1024;

    /// <summary>The current code space (one at a time per process, shared by all isolates).</summary>
    public static BaselineCodeSpace For(Isolate isolate)
    {
        lock (s_lock) return Current();
    }

    static BaselineCodeSpace Current()
    {
        if (s_current is null || s_current._counter >= kTypesPerAssembly) s_current = new BaselineCodeSpace();
        return s_current;
    }

    /// <summary>Defines a type holding one static method with the baseline entry signature.</summary>
    public (TypeBuilder Type, MethodBuilder Method) DefineMethod(string name)
    {
        lock (s_lock) return DefineMethodLocked(name);
    }

    /// <summary>Finishes a type defined by <see cref="DefineMethod"/> (TypeBuilder.CreateType, under the module's lock).</summary>
    public static Type CreateType(TypeBuilder type)
    {
        lock (s_lock) return type.CreateType();
    }

    (TypeBuilder Type, MethodBuilder Method) DefineMethodLocked(string name)
    {
        int id = ++_counter;
        TypeBuilder type = _module.DefineType("Baseline" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class);
        MethodBuilder method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(JSValue),
            [typeof(Isolate), typeof(Interpreter.InterpreterState).MakeByRefType()]);
        return (type, method);
    }
}
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Lets the dynamic baseline code assembly access V8Sharp's internal members
    /// (the attribute is recognised by name by the runtime; it is not in the BCL
    /// reference assemblies).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
