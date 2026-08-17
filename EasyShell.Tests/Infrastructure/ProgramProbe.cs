using System;
using System.Collections.Generic;
using System.IO;

namespace EasyShell.Tests.Infrastructure
{
    /// <summary>
    /// Real programs on disk, in a folder this test owns and (optionally) puts on PATH.
    ///
    /// <para>Several of the behaviours worth protecting are only observable against an actual
    /// executable: PATH resolution including dotted names, <c>RUN</c> reaching past an alias of the
    /// same name, capture versus foreground, exit codes, and the timeout that kills a process tree.
    /// Borrowing a system program instead would make those tests depend on what happens to be
    /// installed; a two-line script we wrote ourselves behaves identically everywhere.</para>
    ///
    /// <para>The scripts are <c>sh</c> on Unix and <c>.cmd</c> on Windows. The name a script is
    /// <i>typed</i> as is the same on both - the <c>.cmd</c> suffix is what PATHEXT exists to
    /// supply - which is what lets one test body cover both platforms.</para>
    /// </summary>
    public sealed class ProgramProbe : IDisposable
    {
        private readonly TempDirectory _directory = new("EasyShellPrograms");
        private readonly string? _originalPath;

        /// <param name="onPath">Prepend this folder to PATH, so bare names resolve to these programs.</param>
        public ProgramProbe(bool onPath = true)
        {
            if (!onPath)
                return;

            _originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", _directory.Root + Path.PathSeparator + _originalPath);
        }

        public string Directory => _directory.Root;

        /// <summary>Writes a runnable program and returns the path of the file actually created.</summary>
        /// <param name="name">The name the script is typed as - without the Windows extension.</param>
        public string Create(string name, string unixBody, string windowsBody)
        {
            if (OperatingSystem.IsWindows())
            {
                string cmd = Path.Combine(_directory.Root, name + ".cmd");
                File.WriteAllText(cmd, "@echo off" + Environment.NewLine + windowsBody + Environment.NewLine);
                return cmd;
            }

            string sh = Path.Combine(_directory.Root, name);
            File.WriteAllText(sh, "#!/bin/sh\n" + unixBody + "\n");
            File.SetUnixFileMode(sh,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return sh;
        }

        /// <summary>A program that prints its own arguments, one per line, after a fixed banner.</summary>
        public string Echo(string name, string banner)
            => Create(name,
                unixBody: $"echo '{banner}'\nfor a in \"$@\"; do echo \"$a\"; done",
                windowsBody: $"echo {banner}\r\n:loop\r\nif \"%~1\"==\"\" goto :eof\r\necho %~1\r\nshift\r\ngoto :loop");

        /// <summary>A program that writes to stderr and nothing to stdout.</summary>
        public string Stderr(string name, string text)
            => Create(name, $"echo '{text}' 1>&2", $"echo {text} 1>&2");

        /// <summary>A program that prints a line and then fails, the way a build tool does.</summary>
        public string Fail(string name, int exitCode, string message = "boom")
            => Create(name, $"echo '{message}'\nexit {exitCode}", $"echo {message}\r\nexit /b {exitCode}");

        /// <summary>A program that stays alive, for testing the wall-clock limit.</summary>
        public string Sleep(string name, int seconds)
            => Create(name, $"sleep {seconds}", $"ping -n {seconds + 1} 127.0.0.1 >nul");

        /// <summary>
        /// A program that reads stdin to EOF before printing. It hangs forever if it is handed a
        /// stdin that never closes, which is exactly the regression the captured invocation guards.
        /// </summary>
        public string ReadStdin(string name)
            => Create(name, "cat > /dev/null\necho drained", "set /p LINE=\r\necho drained");

        /// <summary>Files created here, for tests that want to assert on the real name on disk.</summary>
        public IEnumerable<string> Files => System.IO.Directory.EnumerateFiles(_directory.Root);

        public void Dispose()
        {
            if (_originalPath is not null)
                Environment.SetEnvironmentVariable("PATH", _originalPath);
            _directory.Dispose();
        }
    }
}
