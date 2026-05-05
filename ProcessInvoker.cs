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
        public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
        #endregion

        #region Methods
        public static string RunOnce(string exe, List<string> args)
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string a in args)
                psi.ArgumentList.Add(a);

            using Process p = new() { StartInfo = psi };
            p.Start();

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            // Shell-ish: return stdout, but if stdout empty and stderr has content, surface it.
            string outText = stdout.TrimEnd();
            if (string.IsNullOrEmpty(outText) && !string.IsNullOrWhiteSpace(stderr))
                outText = stderr.TrimEnd();

            return outText;
        }
        public static string Run(string exe, List<string> args)
           => RunStreaming(exe, args, onLine: null);
        /// <param name="onLine">Optional streaming hook. Callers can ignore it, or pass Console.WriteLine / logger / UI appender.</param>
        public static string RunStreaming(string exe, List<string> args, Action<string>? onLine)
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string a in args)
                psi.ArgumentList.Add(a);

            StringBuilder stdout = new();
            StringBuilder stderr = new();

            using Process p = new() { StartInfo = psi, EnableRaisingEvents = true };

            // Ensure event handlers are attached before Start.
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdout.AppendLine(e.Data);
                onLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderr.AppendLine(e.Data);
                onLine?.Invoke(e.Data); // optionally prefix with "ERR: " if you want
            };

            if (!p.Start())
                throw new InvalidOperationException($"Failed to start process: {exe}");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            p.WaitForExit();

            // Best-effort: stop async reads (prevents rare hangs if readers lag).
            try { p.CancelOutputRead(); } catch { }
            try { p.CancelErrorRead(); } catch { }

            string outText = stdout.ToString().TrimEnd();
            if (string.IsNullOrEmpty(outText) && !string.IsNullOrWhiteSpace(stderr.ToString()))
                outText = stderr.ToString().TrimEnd();

            return outText;
        }
        /// <summary>
        /// Convenience wrapper that mimics old behavior (returns "best" text).
        /// </summary>
        public static async Task<string> RunTextAsync(string exe, IEnumerable<string> args, Action<ProcessOutput>? onOutput = null, CancellationToken ct = default)
        {
            ProcessResult r = await RunAsync(exe, args, onOutput, ct).ConfigureAwait(false);

            string outText = r.StdOut.TrimEnd();
            if (string.IsNullOrEmpty(outText) && !string.IsNullOrWhiteSpace(r.StdErr))
                outText = r.StdErr.TrimEnd();

            return outText;
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
                CreateNoWindow = true
            };

            foreach (string a in args)
                psi.ArgumentList.Add(a);

            StringBuilder stdout = new();
            StringBuilder stderr = new();

            using Process p = new()
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                stdout.AppendLine(e.Data);
                onOutput?.Invoke(new ProcessOutput(ProcessStream.StdOut, e.Data));
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) 
                    return;
                stderr.AppendLine(e.Data);
                onOutput?.Invoke(new ProcessOutput(ProcessStream.StdErr, e.Data));
            };

            if (!p.Start())
                throw new InvalidOperationException($"Failed to start process: {exe}");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Cancellation: try to kill the whole process tree.
            using CancellationTokenRegistration reg = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch { /* best-effort */ }
            });

            await p.WaitForExitAsync(ct).ConfigureAwait(false);

            return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        }
        #endregion
    }
}
