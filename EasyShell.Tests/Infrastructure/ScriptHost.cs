using EasyShell.Types;
using System;

namespace EasyShell.Tests.Infrastructure
{
    /// <summary>What a script left behind: its process exit code and everything it printed.</summary>
    public readonly record struct ScriptResult(int ExitCode, string Output)
    {
        public string[] Lines => Output.TrimEnd('\n').Split('\n');
        public string FirstLine => Lines.Length == 0 ? "" : Lines[0];
    }

    /// <summary>
    /// Runs EasyShell the two ways a host does, so tests can say what they mean.
    ///
    /// <para><see cref="Run"/> is `easy script.easy`: a whole script, non-interactive, exit code
    /// out. <see cref="Eval"/> is the REPL's evaluator: one unit in, the last command's value out.
    /// The two are genuinely different - a top-level command is a statement in one and an
    /// expression in the other - and mixing them up is how the "everything got captured" bug
    /// happened in the first place, so the tests keep them apart too.</para>
    /// </summary>
    public static class ScriptHost
    {
        /// <summary>Executes a script and captures its output, exactly as the CLI would.</summary>
        public static ScriptResult Run(string script, Runtime? runtime = null)
        {
            using ConsoleCapture console = new();
            EasyShellEngine engine = new(runtime ?? new Runtime());
            int code = engine.Run(script, origin: "<test>");
            return new ScriptResult(code, console.Text);
        }

        /// <summary>
        /// Evaluates one command as an expression and returns its value - the shape
        /// `$sha = (git rev-parse HEAD)` relies on. Output is discarded; use
        /// <see cref="EvaluateWithOutput"/> when it matters.
        /// </summary>
        public static Value Evaluate(string command, Runtime? runtime = null)
            => EvaluateWithOutput(command, runtime).Value;

        public static (Value Value, string Output) EvaluateWithOutput(string command, Runtime? runtime = null)
        {
            using ConsoleCapture console = new();
            EasyShellEngine engine = new(runtime ?? new Runtime());
            Value value = engine.RunUnit(command, origin: "<test>");
            return (value, console.Text);
        }

        /// <summary>The string a script command evaluates to - the assertion most tests want.</summary>
        public static string EvaluateText(string command, Runtime? runtime = null)
            => Evaluate(command, runtime).AsString();

        /// <summary>Runs a script inside a working directory, and puts the old one back.</summary>
        public static ScriptResult RunIn(string workingDirectory, string script, Runtime? runtime = null)
        {
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = workingDirectory;
                return Run(script, runtime);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
    }
}
