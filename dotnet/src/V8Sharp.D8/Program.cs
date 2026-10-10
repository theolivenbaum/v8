// Port of src/d8/d8.cc: the d8sharp shell.
namespace V8Sharp.D8;

public static class Program
{
    public static int Main(string[] args)
    {
        int result = 0;
        // d8 runs on a large stack; V8Sharp's interpreter recursion needs it too.
        var thread = new Thread(() => result = Shell.Run(args), 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }
}
