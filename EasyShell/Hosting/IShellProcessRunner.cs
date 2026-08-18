using System;
using System.Collections.Generic;

namespace EasyShell.Hosting
{
    /// <summary>
    /// How the language runs external programs and decides whether a name IS a program.
    ///
    /// The default implementation is ProgramResolver + ProcessInvoker - real PATH, real processes,
    /// real terminal. A virtual machine substitutes its process table: Resolve consults the image's
    /// /bin instead of the host PATH, and the Run methods spawn virtual processes wired to virtual
    /// stdio. The captured/foreground split carries over unchanged, because it is a semantic
    /// distinction (expression wants text; interactive statement wants the terminal), not a
    /// host detail.
    /// </summary>
    public interface IShellProcessRunner
    {
        /// <summary>The executable this name refers to, or null when it is not a program.</summary>
        string? Resolve(string command);

        /// <summary>Run with inherited/attached stdio, blocking until exit. Returns the exit code.</summary>
        int RunForeground(string program, List<string> arguments, TimeSpan? timeout);

        /// <summary>Run captured through pipes; stdout lines stream to <paramref name="onLine"/> when given.</summary>
        ProcessInvoker.ProcessResult RunCaptured(string program, List<string> arguments, Action<string>? onLine, TimeSpan? timeout);

        /// <summary>
        /// Run captured with standard input supplied as text - one stage of a pipeline. Separate
        /// from <see cref="RunCaptured"/> because feeding stdin is the whole difference between
        /// running a program and piping into one, and a host that virtualizes processes wires it
        /// to a virtual pipe rather than a real one.
        /// </summary>
        ProcessInvoker.ProcessResult RunPiped(string program, List<string> arguments, string? standardInput, TimeSpan? timeout);
    }
}
