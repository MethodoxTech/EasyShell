using System;
using System.IO;

namespace EasyShell.Tests.Infrastructure
{
    /// <summary>
    /// Borrows the console for the duration of a test.
    ///
    /// <para>Half of what this shell does is observable only through <see cref="Console"/>:
    /// <c>print</c> is an alias for Console.WriteLine, a statement-context program streams through
    /// it, and the REPL both reads and writes it. Swapping the streams is therefore the only way to
    /// assert on any of that, and putting them back afterwards is what keeps the next test - and
    /// the test runner's own output - working.</para>
    /// </summary>
    public sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _originalOut;
        private readonly TextReader _originalIn;
        private readonly StringWriter _captured = new();

        /// <param name="input">Lines the test types. Null leaves stdin alone.</param>
        public ConsoleCapture(string? input = null)
        {
            _originalOut = Console.Out;
            _originalIn = Console.In;

            Console.SetOut(_captured);
            if (input is not null)
                Console.SetIn(new StringReader(input));
        }

        /// <summary>Everything written so far, with line endings normalized to '\n'.</summary>
        public string Text => _captured.ToString().ReplaceLineEndings("\n");

        /// <summary>The captured text as lines, without the empty tail a trailing newline leaves.</summary>
        public string[] Lines => Text.TrimEnd('\n').Split('\n');

        public void Dispose()
        {
            Console.SetOut(_originalOut);
            Console.SetIn(_originalIn);
        }

        /// <summary>Joins lines the way <see cref="Console.ReadLine"/> expects to receive them.</summary>
        public static string Typed(params string[] lines)
            => string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
