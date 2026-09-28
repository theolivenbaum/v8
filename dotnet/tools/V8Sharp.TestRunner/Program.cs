// The equivalent of tools/run-tests.py. Filled in by the test-infrastructure port.
namespace V8Sharp.TestRunner;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.Error.WriteLine("usage: V8Sharp.TestRunner <suite> --engine v8sharp|oracle");
        return 1;
    }
}
