using System;
using System.IO;

namespace EasyShell
{
    /// <summary>
    /// Answers one question: does this name refer to a program we could actually run?
    ///
    /// <para>It exists because of the dotted-name ambiguity. `System.DateTime.Now` is a .NET member
    /// invocation and `vim.tiny` is a program, and they are the same shape. The original rule -
    /// "a dotted name is a .NET call unless it is a file in the working directory" - decided that
    /// question by looking in the one place a program is least likely to be, so `vim.tiny`,
    /// `python3.12`, `node.exe` and every `.cmd`/`.bat` on Windows were unreachable. Asking PATH
    /// instead answers it correctly in both directions: nothing named `System.DateTime.Now` is
    /// going to be on PATH.</para>
    ///
    /// <para>Not cached deliberately: a build script that produces an executable and then runs it
    /// is an ordinary thing to write, and a stale negative would be a genuinely baffling failure.
    /// A lookup is a handful of File.Exists calls, far cheaper than the reflection it guards.</para>
    /// </summary>
    public static class ProgramResolver
    {
        /// <summary>The executable this name refers to, or null when it is not a program.</summary>
        public static string? Resolve(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            if (command.Contains('/', StringComparison.Ordinal) ||
                command.Contains('\\', StringComparison.Ordinal))
            {
                try { return WithExtensions(Path.GetFullPath(command)); }
                catch { return null; }              // malformed path
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string? hit;
                try { hit = WithExtensions(Path.Combine(dir.Trim(), command)); }
                catch { continue; }                 // malformed PATH entries happen
                if (hit is not null) return hit;
            }
            return null;
        }

        public static bool Exists(string command) => Resolve(command) is not null;

        /// <summary>Windows resolves a bare name through PATHEXT; Unix wants the execute bit.</summary>
        private static string? WithExtensions(string candidate)
        {
            if (!OperatingSystem.IsWindows())
                return IsExecutableFile(candidate) ? candidate : null;

            if (Path.HasExtension(candidate) && File.Exists(candidate)) return candidate;

            string pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            foreach (string ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string withExt = candidate + ext.Trim();
                if (File.Exists(withExt)) return withExt;
            }
            return File.Exists(candidate) ? candidate : null;
        }

        private static bool IsExecutableFile(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows() || !File.Exists(path)) return false;
                const UnixFileMode anyExecute =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                return (File.GetUnixFileMode(path) & anyExecute) != 0;
            }
            catch { return false; }
        }
    }
}
