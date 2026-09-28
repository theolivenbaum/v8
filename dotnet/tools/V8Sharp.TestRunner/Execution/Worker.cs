// A worker process runs tests that share one set of process-global V8 flags,
// one at a time, each in a fresh isolate, and answers over a line protocol.
// Crashes and hangs only take down the worker; the coordinator restarts it.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using V8Sharp.TestRunner.Engines;
using V8Sharp.TestRunner.Shell;

namespace V8Sharp.TestRunner.Execution;

public sealed record WorkerRequest(int Id, string[] Args, double TimeoutSeconds);

public sealed record WorkerResponse(int Id, int ExitCode, string Stdout, string Stderr, bool TimedOut, double DurationMs);

public static class EngineFactory
{
    public static IJsEngine Create(string name) => name switch
    {
        "oracle" => new OracleEngine(),
        "v8sharp" => new V8SharpEngine(),
        _ => throw new ArgumentException($"unknown engine '{name}' (oracle, v8sharp)"),
    };
}

public static class Worker
{
    /// <summary>The flags a worker applies once: the V8 flags of the d8 command
    /// line plus the isolate-level d8 options (--no-can-block).</summary>
    public static List<string> ProcessFlags(D8Options o)
    {
        var flags = new List<string>(o.V8Flags);
        if (o.NoCanBlock) flags.Add("--no-can-block");
        return flags;
    }

    /// <summary>Runs one d8 command line on an engine whose flags are already
    /// set, with a watchdog. Used by worker processes and in-process runs.</summary>
    public static ShellResult RunOne(IJsEngine engine, string[] args, string workingDirectory, TimeSpan timeout)
    {
        var options = D8Options.Parse(args);
        var shell = new D8Shell(engine, options, workingDirectory);
        ShellResult? result = null;
        Exception? failure = null;
        // V8 needs a deep native stack for its own limit (--stack-size) to be the one that trips.
        var thread = new Thread(() =>
        {
            try { result = shell.Run(); }
            catch (Exception e) { failure = e; }
        }, 256 * 1024 * 1024)
        { IsBackground = true, Name = "d8" };
        thread.Start();
        if (!thread.Join(timeout))
        {
            // Termination can be cancelled when nested script calls unwind, so keep asking.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            do
            {
                shell.Timeout();
            }
            while (!thread.Join(TimeSpan.FromMilliseconds(200)) && DateTime.UtcNow < deadline);
            if (thread.IsAlive) throw new TimeoutException("the engine did not stop after termination");
        }
        if (failure is not null) throw failure;
        return result!;
    }

    /// <summary>The worker process: <c>--worker --engine E --flags JSON --cwd DIR</c>.</summary>
    public static int Serve(string engineName, string[] flags, string workingDirectory)
    {
        var protocol = NativeStdout.RedirectToFile(out var nativeOut);
        var engine = EngineFactory.Create(engineName);
        // V8 reports bad flags ("Error: unrecognized flag") on stderr; keep that
        // with every result of this worker, where d8 would have printed it.
        string startup = nativeOut?.CaptureStderr(() => engine.SetFlags(flags)) ?? Run(() => engine.SetFlags(flags));
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var req = JsonSerializer.Deserialize(line, WorkerJson.Default.WorkerRequest)!;
            WorkerResponse resp;
            try
            {
                var r = RunOne(engine, req.Args, workingDirectory, TimeSpan.FromSeconds(req.TimeoutSeconds));
                string native = nativeOut?.ReadNew() ?? "";
                resp = new WorkerResponse(req.Id, r.ExitCode, r.Stdout + native, startup + r.Stderr, r.TimedOut, r.Duration.TotalMilliseconds);
            }
            catch (TimeoutException)
            {
                // The coordinator kills and restarts us.
                resp = new WorkerResponse(req.Id, -1, "", "worker: engine did not stop", true, req.TimeoutSeconds * 1000);
                Write(protocol, resp);
                Environment.Exit(3);
                return 3;
            }
            Write(protocol, resp);
        }
        return 0;
    }

    static string Run(Action a)
    {
        a();
        return "";
    }

    static void Write(Stream protocol, WorkerResponse resp)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, WorkerJson.Default.WorkerResponse);
        protocol.Write(bytes);
        protocol.WriteByte((byte)'\n');
        protocol.Flush();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(WorkerRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(WorkerResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class WorkerJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>
/// Native code (V8's --print-bytecode, flag warnings) writes to file
/// descriptor 1 with C stdio. In a worker that descriptor carries the
/// protocol, so it is moved: the protocol keeps a duplicate of the original
/// stdout and fd 1 is pointed at a temporary file whose new content is
/// appended to each test's stdout.
/// </summary>
sealed class NativeStdout
{
    readonly string _path;
    long _offset;

    NativeStdout(string path) => _path = path;

    public static Stream RedirectToFile(out NativeStdout? capture)
    {
        capture = null;
        // The open(2) flag values below are Linux's.
        if (!OperatingSystem.IsLinux()) return Console.OpenStandardOutput();
        int saved = Libc.dup(1);
        if (saved < 0) return Console.OpenStandardOutput();
        string path = PathFor(Environment.ProcessId);
        int fd = Libc.open(path, 0x2 | 0x40 | 0x200 /* O_RDWR|O_CREAT|O_TRUNC */, 0x180 /* 0600 */);
        if (fd < 0 || Libc.dup2(fd, 1) < 0) return Console.OpenStandardOutput();
        Libc.close(fd);
        capture = new NativeStdout(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { File.Delete(path); } catch { } };
        return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(saved, ownsHandle: true), FileAccess.Write, 1);
    }

    /// <summary>Runs <paramref name="action"/> with file descriptor 2 also
    /// pointing at the capture file and returns what native code wrote.</summary>
    public string CaptureStderr(Action action)
    {
        ReadNew();
        int saved = Libc.dup(2);
        if (saved < 0 || Libc.dup2(1, 2) < 0)
        {
            action();
            return "";
        }
        try
        {
            action();
        }
        finally
        {
            Libc.fflush(IntPtr.Zero);
            Libc.dup2(saved, 2);
            Libc.close(saved);
        }
        return ReadNew();
    }

    public static string PathFor(int processId) => Path.Combine(Path.GetTempPath(), $"v8sharp-worker-{processId}.out");

    public string ReadNew()
    {
        Libc.fflush(IntPtr.Zero);
        using var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (f.Length <= _offset) return "";
        f.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[f.Length - _offset];
        f.ReadExactly(buf);
        _offset = f.Length;
        return Encoding.UTF8.GetString(buf);
    }

    static class Libc
    {
        [DllImport("libc", SetLastError = true)] public static extern int dup(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int dup2(int fd, int fd2);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int fflush(IntPtr stream);
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags, int mode);
    }
}
