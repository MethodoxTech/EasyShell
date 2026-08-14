using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShell
{
    public static class ProcessInvoker
    {
        #region Subtypes
        public enum ProcessStream { StdOut, StdErr }
        public readonly record struct ProcessOutput(ProcessStream Stream, string Line);
        public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
        {
            /// <summary>
            /// Shell-ish: prefer stdout, but surface stderr when stdout produced nothing.
            /// </summary>
            public string BestText
            {
                get
                {
                    string outText = StdOut.TrimEnd();
                    if (string.IsNullOrEmpty(outText) && !string.IsNullOrWhiteSpace(StdErr))
                        return StdErr.TrimEnd();
                    return outText;
                }
            }
        }
        #endregion

        #region Configurations
        /// <summary>
        /// How long to keep draining stdout/stderr after the child process has already exited.
        ///
        /// This bound is the whole point: a grandchild process that inherited our pipe handles
        /// (for example the persistent MSBuild nodes that Godot's `dotnet build` leaves behind)
        /// keeps the pipe open long after the child is gone. `Process.WaitForExit()` with no
        /// argument waits for the pipes to reach EOF as well as for the process, so in that
        /// situation it blocks forever and the build looks "stuck". We wait for the process
        /// itself, then give the readers only this long to drain whatever is still buffered.
        /// </summary>
        public static TimeSpan StreamDrainGrace { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>
        /// Slice length used when waiting for a process with no overall timeout. See
        /// <see cref="WaitForProcessExit"/> for why we never wait "infinitely" in one call.
        /// </summary>
        private const int ProcessPollIntervalMilliseconds = 250;
        #endregion

        #region Methods
        public static ProcessResult RunOnce(string exe, List<string> args)
            => RunStreaming(exe, args, onLine: null);
        public static string Run(string exe, List<string> args)
           => RunStreaming(exe, args, onLine: null).BestText;
        /// <param name="onLine">Optional streaming hook. Callers can ignore it, or pass Console.WriteLine / logger / UI appender.</param>
        /// <param name="timeout">Optional wall-clock limit. When it elapses the whole process tree is killed and a TimeoutException is raised.</param>
        public static ProcessResult RunStreaming(string exe, List<string> args, Action<string>? onLine, TimeSpan? timeout = null)
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Redirect stdin and close it immediately (below). Without this the child inherits
                // our console: any tool that decides to read stdin - a prompt, a "press any key",
                // an interactive fallback - blocks forever instead of seeing EOF and moving on.
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            foreach (string a in args)
                psi.ArgumentList.Add(a);

            StringBuilder stdout = new();
            StringBuilder stderr = new();

            // Completed when the respective pipe reports EOF (a null Data payload).
            TaskCompletionSource outDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource errDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using Process p = new() { StartInfo = psi, EnableRaisingEvents = true };

            // Ensure event handlers are attached before Start.
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    outDone.TrySetResult();
                    return;
                }
                lock (stdout)
                    stdout.AppendLine(e.Data);
                onLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    errDone.TrySetResult();
                    return;
                }
                lock (stderr)
                    stderr.AppendLine(e.Data);
                onLine?.Invoke(e.Data); // optionally prefix with "ERR: " if you want
            };

            if (!p.Start())
                throw new InvalidOperationException($"Failed to start process: {exe}");

            // Hand the child an already-closed stdin so it reads EOF rather than blocking.
            try { p.StandardInput.Close(); } catch { /* best-effort */ }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Wait for the PROCESS ONLY; draining the pipes is bounded separately below.
            if (!WaitForProcessExit(p, timeout))
            {
                TryKillTree(p);
                throw new TimeoutException($"Process did not finish within {timeout!.Value.TotalSeconds:0} seconds and was terminated: {exe}");
            }

            // Bounded drain: collect whatever is still buffered, then move on regardless.
            Task.WaitAll([outDone.Task, errDone.Task], StreamDrainGrace);

            // Best-effort: stop async reads (prevents rare hangs if readers lag).
            try { p.CancelOutputRead(); } catch { }
            try { p.CancelErrorRead(); } catch { }

            lock (stdout)
                lock (stderr)
                    return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        }
        /// <summary>
        /// Convenience wrapper that mimics old behavior (returns "best" text).
        /// </summary>
        public static async Task<string> RunTextAsync(string exe, IEnumerable<string> args, Action<ProcessOutput>? onOutput = null, CancellationToken ct = default)
        {
            ProcessResult r = await RunAsync(exe, args, onOutput, ct).ConfigureAwait(false);
            return r.BestText;
        }
        /// <summary>
        /// Streams output as it arrives (stdout/stderr), and also returns captured text.
        /// </summary>
        public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, Action<ProcessOutput>? onOutput = null, CancellationToken ct = default)
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            foreach (string a in args)
                psi.ArgumentList.Add(a);

            StringBuilder stdout = new();
            StringBuilder stderr = new();

            TaskCompletionSource outDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource errDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using Process p = new()
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    outDone.TrySetResult();
                    return;
                }
                lock (stdout)
                    stdout.AppendLine(e.Data);
                onOutput?.Invoke(new ProcessOutput(ProcessStream.StdOut, e.Data));
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    errDone.TrySetResult();
                    return;
                }
                lock (stderr)
                    stderr.AppendLine(e.Data);
                onOutput?.Invoke(new ProcessOutput(ProcessStream.StdErr, e.Data));
            };

            if (!p.Start())
                throw new InvalidOperationException($"Failed to start process: {exe}");

            try { p.StandardInput.Close(); } catch { /* best-effort */ }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Cancellation: try to kill the whole process tree.
            using CancellationTokenRegistration reg = ct.Register(() => TryKillTree(p));

            // Same rationale as RunStreaming: wait for the process, not for the pipes.
            await Task.Run(() => WaitForProcessExit(p, timeout: null), CancellationToken.None).ConfigureAwait(false);

            // Bounded drain, same rationale as RunStreaming.
            await Task.WhenAny(
                Task.WhenAll(outDone.Task, errDone.Task),
                Task.Delay(StreamDrainGrace, CancellationToken.None)).ConfigureAwait(false);

            lock (stdout)
                lock (stderr)
                    return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        }
        #endregion

        #region Routines
        /// <summary>
        /// Waits for the process to exit, and ONLY for that.
        ///
        /// Subtlety worth preserving: Process.WaitForExit(int) additionally blocks until the
        /// redirected pipes reach EOF whenever the argument is Timeout.Infinite - the no-argument
        /// WaitForExit() is literally WaitForExit(Timeout.Infinite). So "wait forever for the
        /// process" and "wait forever for the pipes" are the same call, and a grandchild holding an
        /// inherited pipe hangs the build indefinitely. Waiting in finite slices avoids that branch
        /// entirely while still being an unbounded wait overall.
        /// </summary>
        /// <returns>False only when <paramref name="timeout"/> elapsed first.</returns>
        private static bool WaitForProcessExit(Process p, TimeSpan? timeout)
        {
            if (timeout.HasValue)
                return p.WaitForExit((int)timeout.Value.TotalMilliseconds);

            while (!p.WaitForExit(ProcessPollIntervalMilliseconds))
            {
                // Keep slicing: never pass Timeout.Infinite.
            }
            return true;
        }
        private static void TryKillTree(Process p)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
        }
        #endregion
    }
}
