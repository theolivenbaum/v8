// The coordinator side of the worker processes: schedules tests on --jobs
// slots, keeps one worker per slot alive while consecutive tests share its
// flag set, and turns worker deaths and hangs into CRASH and TIMEOUT.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using V8Sharp.TestRunner.OutProc;
using V8Sharp.TestRunner.Shell;
using V8Sharp.TestRunner.Suites;

namespace V8Sharp.TestRunner.Execution;

public sealed class Executor(string engine, string v8Root, int jobs, TimeSpan timeout)
{
    /// <summary>Extra time a worker gets beyond the test timeout before it is killed.</summary>
    static readonly TimeSpan s_grace = TimeSpan.FromSeconds(30);

    sealed record Group(string Key, List<string> Flags, IReadOnlyDictionary<string, string> Env, Queue<TestCase> Tests);

    /// <summary>Assigns <see cref="TestCase.GroupKey"/>: tests share a worker
    /// when their process-level flags and environment are equal.</summary>
    public static void AssignGroups(IEnumerable<TestCase> tests)
    {
        foreach (var t in tests)
        {
            var flags = Worker.ProcessFlags(D8Options.Parse(t.CommandLine));
            var sb = new StringBuilder(string.Join('\u0001', flags));
            foreach (var (k, v) in t.Env.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append('\u0002').Append(k).Append('=').Append(v);
            t.GroupKey = sb.ToString();
        }
    }

    public async Task RunAsync(IReadOnlyList<TestCase> tests, Action<TestCase, RunOutput> onResult, CancellationToken cancel)
    {
        AssignGroups(tests);
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var t in tests)
        {
            if (!groups.TryGetValue(t.GroupKey, out var g))
            {
                g = new Group(t.GroupKey, Worker.ProcessFlags(D8Options.Parse(t.CommandLine)), t.Env, new Queue<TestCase>());
                groups[t.GroupKey] = g;
            }
            g.Tests.Enqueue(t);
        }
        var sync = new object();

        (Group, TestCase)? TakeNext(string? current)
        {
            lock (sync)
            {
                if (current is not null && groups.TryGetValue(current, out var cg) && cg.Tests.Count > 0) return (cg, cg.Tests.Dequeue());
                Group? best = null;
                foreach (var g in groups.Values)
                {
                    if (g.Tests.Count > 0 && (best is null || g.Tests.Count > best.Tests.Count)) best = g;
                }
                if (best is null) return null;
                return (best, best.Tests.Dequeue());
            }
        }

        async Task Slot()
        {
            WorkerProcess? w = null;
            try
            {
                while (!cancel.IsCancellationRequested && TakeNext(w?.Key) is var (g, t))
                {
                    if (w is null || w.Key != g.Key || w.IsDead)
                    {
                        w?.Dispose();
                        w = WorkerProcess.Start(engine, g.Key, g.Flags, g.Env, v8Root);
                    }
                    var output = await w.RunAsync(t, t.Timeout(timeout), cancel).ConfigureAwait(false);
                    onResult(t, output);
                }
            }
            finally
            {
                w?.Dispose();
            }
        }

        var slots = new Task[Math.Max(1, jobs)];
        for (int i = 0; i < slots.Length; i++) slots[i] = Task.Run(Slot, cancel);
        await Task.WhenAll(slots).ConfigureAwait(false);
    }

    sealed class WorkerProcess : IDisposable
    {
        readonly Process _process;
        readonly StringBuilder _stderr = new();
        int _nextId;

        public string Key { get; }
        public bool IsDead { get; private set; }

        WorkerProcess(string key, Process process)
        {
            Key = key;
            _process = process;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderr)
                {
                    _stderr.Append(e.Data).Append('\n');
                    if (_stderr.Length > 64 * 1024) _stderr.Remove(0, _stderr.Length - 32 * 1024);
                }
            };
            _process.BeginErrorReadLine();
        }

        public static WorkerProcess Start(string engine, string key, List<string> flags, IReadOnlyDictionary<string, string> env, string cwd)
        {
            var (file, prefix) = SelfCommand();
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = cwd,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in prefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--worker");
            psi.ArgumentList.Add("--engine");
            psi.ArgumentList.Add(engine);
            psi.ArgumentList.Add("--cwd");
            psi.ArgumentList.Add(cwd);
            psi.ArgumentList.Add("--worker-flags");
            psi.ArgumentList.Add(JsonSerializer.Serialize(flags.ToArray(), WorkerJson.Default.StringArray));
            foreach (var (k, v) in env) psi.Environment[k] = v;
            // Keep workers from each spinning up a full server GC heap.
            psi.Environment["DOTNET_gcServer"] = "0";
            psi.Environment["DOTNET_TieredPGO"] = "0";
            var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start worker");
            return new WorkerProcess(key, p);
        }

        /// <summary>How to start this program again: its apphost, or
        /// <c>dotnet V8Sharp.TestRunner.dll</c> when running under another host (tests).</summary>
        static (string File, string[] Prefix) SelfCommand()
        {
            string dll = typeof(Worker).Assembly.Location;
            string dir = Path.GetDirectoryName(dll)!;
            string apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "V8Sharp.TestRunner.exe" : "V8Sharp.TestRunner");
            if (File.Exists(apphost)) return (apphost, []);
            string? dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (dotnet is null || !File.Exists(dotnet))
            {
                string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
                dotnet = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            }
            return (dotnet, [dll]);
        }

        public async Task<RunOutput> RunAsync(TestCase test, TimeSpan testTimeout, CancellationToken cancel)
        {
            var req = new WorkerRequest(++_nextId, [.. test.CommandLine], testTimeout.TotalSeconds);
            var sw = Stopwatch.StartNew();
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req, WorkerJson.Default.WorkerRequest)).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancel).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return Died(sw.Elapsed);
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(testTimeout + s_grace);
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill();
                return new RunOutput(-1, "", StderrTail(), TimedOut: true, Crashed: false, sw.Elapsed);
            }
            if (line is null) return Died(sw.Elapsed);
            var resp = JsonSerializer.Deserialize(line, WorkerJson.Default.WorkerResponse)!;
            if (resp.TimedOut && resp.ExitCode == -1 && resp.Stderr.StartsWith("worker:", StringComparison.Ordinal)) IsDead = true;
            return new RunOutput(resp.ExitCode, resp.Stdout, resp.Stderr, resp.TimedOut, Crashed: false, TimeSpan.FromMilliseconds(resp.DurationMs));
        }

        RunOutput Died(TimeSpan elapsed)
        {
            IsDead = true;
            _process.WaitForExit(5000);
            int code = _process.HasExited ? _process.ExitCode : -1;
            // A process killed by a signal exits with 128+signal. V8's runner
            // treats SIGABRT (V8's fatal errors, CHECK failures) as a failure,
            // any other signal as a crash (objects/output.py HasCrashed).
            bool abort = code == 128 + 6;
            string stderr = StderrTail();
            return new RunOutput(abort ? 1 : code, "", $"worker exited with code {code}\n{stderr}",
                TimedOut: false, Crashed: !abort, elapsed);
        }

        string StderrTail()
        {
            Thread.Sleep(50);
            lock (_stderr)
            {
                string s = _stderr.ToString();
                _stderr.Clear();
                return s.Length > 8000 ? s[^8000..] : s;
            }
        }

        void Kill()
        {
            IsDead = true;
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            _process.Dispose();
        }
    }
}
