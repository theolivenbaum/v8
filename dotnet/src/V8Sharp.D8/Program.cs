// Port of src/d8/d8.cc: the d8sharp shell. Filled in once the engine runs scripts.
namespace V8Sharp.D8;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.Error.WriteLine("d8sharp: the engine cannot run scripts yet; see dotnet/todo.md.");
        return 1;
    }
}
