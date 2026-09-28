// Installs the interpreter and the compiler on every new isolate (V8 has them
// built in; V8Sharp's Execution calls them through hooks on the isolate).
namespace V8Sharp
{
    public sealed partial class Isolate
    {
        partial void InitializeInterpreter()
        {
            Interpreter.InterpreterExecution.Install(this);
            Codegen.Compiler.Install(this);
        }
    }
}
