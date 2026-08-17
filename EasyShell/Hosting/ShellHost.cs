using System;

namespace EasyShell.Hosting
{
    /// <summary>
    /// The world a Runtime executes in: console, filesystem, processes, environment - plus the
    /// reflection policy. One Runtime, one host.
    ///
    /// <para><b>Why this exists.</b> EasyShell's parser, value system, control flow and .NET
    /// binder never touch the machine; everything that does was concentrated behind these four
    /// interfaces. The default host (<see cref="Default"/>) reproduces the historical behavior
    /// exactly - System.IO, System.Console, real processes, process-global cwd - so `easy` and
    /// every existing script are unaffected. A virtualized host (a VM whose filesystem is a
    /// portable image, whose processes are a virtual process table, whose console is a tty byte
    /// stream) substitutes all four and gets the ENTIRE language, REPL included, inside its
    /// world - same parser, same semantics, zero drift.</para>
    /// </summary>
    public sealed class ShellHost
    {
        public required IShellConsole Console { get; init; }
        public required IShellFileSystem FileSystem { get; init; }
        public required IShellProcessRunner Processes { get; init; }
        public required IShellEnvironment Environment { get; init; }

        /// <summary>
        /// Reflection policy for fully-qualified .NET invocation (`System.IO.File.WriteAllText ...`
        /// and the reflection-backed aliases). Null - the default - permits everything, which is
        /// the right posture for a build tool running with the user's own authority. A sandboxing
        /// host supplies a predicate over the fully-qualified member name; anything refused
        /// becomes a normal script error. Direct .NET access is EasyShell's superpower on the
        /// host and its biggest escape hatch in a sandbox - this is the one switch that
        /// reconciles the two.
        /// </summary>
        public Func<string, bool>? CanInvokeQualified { get; init; }

        /// <summary>The historical behavior: real console, real filesystem, real processes.</summary>
        public static ShellHost Default { get; } = new()
        {
            Console = new HostConsole(),
            FileSystem = new HostFileSystem(),
            Processes = new HostProcessRunner(),
            Environment = new HostEnvironment(),
        };
    }
}
