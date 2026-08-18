using EasyShell.Exceptions;
using EasyShell.Hosting;
using EasyShell.Parsing;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EasyShell
{
    /// <summary>
    /// Pipelines (<c>a | b</c>) and redirection (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;</c>).
    ///
    /// <para>The disambiguation rule is the whole design, and it is one sentence: <b>these symbols
    /// are operators only in NON-head position, and only unquoted.</b> EasyShell's comparisons and
    /// concatenation are ordinary commands that happen to be named <c>&gt;</c>, <c>&lt;</c> and
    /// <c>||</c>, and a command name is always the head of its argument list - so
    /// <c>(&gt; $a $b)</c> stays a comparison, <c>(|| "a" "b")</c> stays concatenation, and
    /// <c>echo "&gt;"</c> still prints a bracket, while <c>ls | wc</c> and <c>print hi &gt; f.txt</c>
    /// mean what every shell user expects. (<c>||</c> is additionally safe by construction: the
    /// tokenizer keeps it a single word, so it can never be read as two pipes.)</para>
    ///
    /// <para>Recognition happens in <see cref="Executor.ExecuteCommand"/>, the one funnel both
    /// statements and parenthesized expressions pass through, so <c>ls | wc -l</c> behaves
    /// identically as a statement and as <c>$n = (ls | wc -l)</c>.</para>
    /// </summary>
    internal static class Pipelines
    {
        public const string Pipe = "|";
        public const string Write = ">";
        public const string Append = ">>";
        public const string Read = "<";

        /// <summary>An operator token, as opposed to a quoted argument that merely looks like one.</summary>
        private static bool IsOperator(Arg arg, out string op)
        {
            op = arg is AtomArg { WasQuoted: false } atom ? atom.Text : "";
            return op is Pipe or Write or Append or Read;
        }

        /// <summary>Does this command line use any pipe or redirection? Head position never counts.</summary>
        public static bool Uses(List<Arg> args)
        {
            for (int i = 1; i < args.Count; i++)
                if (IsOperator(args[i], out _)) return true;
            return false;
        }

        private sealed class Stage
        {
            public List<Arg> Args = new();
            public Arg? StdinFrom;
            public Arg? StdoutTo;
            public bool AppendStdout;
        }

        // ------------------------------------------------------------------ splitting

        private static List<Stage> Split(List<Arg> args, int line)
        {
            List<Stage> stages = new() { new Stage() };

            for (int i = 0; i < args.Count; i++)
            {
                // Head position is never an operator: `(> $a $b)` is a comparison command.
                if (i > 0 && IsOperator(args[i], out string op))
                {
                    if (op == Pipe)
                    {
                        if (stages[^1].Args.Count == 0)
                            throw new EasyShellException($"{line}: '|' with no command before it.");
                        stages.Add(new Stage());
                        continue;
                    }

                    if (i + 1 >= args.Count)
                        throw new EasyShellException($"{line}: '{op}' needs a file name after it.");
                    if (IsOperator(args[i + 1], out _))
                        throw new EasyShellException($"{line}: '{op}' needs a file name after it, not '{op}'.");

                    Arg target = args[++i];
                    Stage stage = stages[^1];
                    if (op == Read)
                    {
                        if (stage.StdinFrom is not null)
                            throw new EasyShellException($"{line}: more than one '<' for one command.");
                        stage.StdinFrom = target;
                    }
                    else
                    {
                        if (stage.StdoutTo is not null)
                            throw new EasyShellException($"{line}: more than one output redirection for one command.");
                        stage.StdoutTo = target;
                        stage.AppendStdout = op == Append;
                    }
                    continue;
                }

                stages[^1].Args.Add(args[i]);
            }

            if (stages[^1].Args.Count == 0)
                throw new EasyShellException($"{line}: '|' with no command after it.");

            // Reading into a later stage or writing out of an earlier one would silently discard
            // the pipe that connects them; say so instead of doing something surprising.
            for (int i = 0; i < stages.Count; i++)
            {
                if (i > 0 && stages[i].StdinFrom is not null)
                    throw new EasyShellException(
                        $"{line}: '<' belongs on the first command of a pipeline - the pipe already feeds the rest.");
                if (i < stages.Count - 1 && stages[i].StdoutTo is not null)
                    throw new EasyShellException(
                        $"{line}: '>' belongs on the last command of a pipeline - the pipe already carries the rest.");
            }

            return stages;
        }

        // ------------------------------------------------------------------ execution

        public static Value Execute(Runtime rt, List<Arg> args, int line, bool statementContext)
        {
            List<Stage> stages = Split(args, line);
            IShellFileSystem fs = rt.Host.FileSystem;

            string? flowing = null;
            if (stages[0].StdinFrom is { } source)
            {
                string path = Executor.EvaluateArg(rt, source).AsString();
                if (!fs.FileExists(fs.GetFullPath(path)))
                    throw new EasyShellException($"{line}: cannot read '{path}': no such file.");
                flowing = fs.ReadAllText(fs.GetFullPath(path));
            }

            for (int i = 0; i < stages.Count; i++)
                flowing = RunStage(rt, stages[i], flowing, line);

            Stage last = stages[^1];
            if (last.StdoutTo is { } destination)
            {
                string path = fs.GetFullPath(Executor.EvaluateArg(rt, destination).AsString());
                string text = flowing ?? string.Empty;
                fs.WriteAllText(path, last.AppendStdout && fs.FileExists(path)
                    ? fs.ReadAllText(path) + text
                    : text);
                return Value.Null;
            }

            // No redirection: hand the text back. In statement context ExecuteStatement prints it;
            // in expression context it IS the value - so the trailing newline goes, exactly as it
            // does for a captured external program.
            string result = (flowing ?? string.Empty).TrimEnd('\n', '\r');
            return result.Length == 0 ? Value.Null : new Value(ValueKind.String, result);
        }

        /// <summary>
        /// Run one stage with its stdin supplied as text, returning its stdout as text. External
        /// programs go through the host's piped runner; everything else - built-ins, .NET calls,
        /// functions - runs with its console captured, which is what makes
        /// <c>print hello &gt; f.txt</c> work as naturally as it reads.
        /// </summary>
        private static string RunStage(Runtime rt, Stage stage, string? input, int line)
        {
            Value nameValue = Executor.EvaluateArg(rt, stage.Args[0]);
            string name = nameValue.AsString();

            if (!string.IsNullOrWhiteSpace(name) && rt.Host.Processes.Resolve(name) is not null)
            {
                List<string> arguments = stage.Args.Skip(1)
                    .Select(a => Executor.EvaluateArg(rt, a).AsString()).ToList();

                ProcessInvoker.ProcessResult result;
                try
                {
                    result = rt.Host.Processes.RunPiped(name, arguments, input, Executor.GetProcessTimeout(rt));
                }
                catch (Exception e)
                {
                    throw new EasyShellException($"{line}: Failed to execute '{name}': {e.Message}");
                }

                // A stage's stderr is NOT part of the pipe - it belongs on the terminal, or the
                // one message explaining why the pipeline failed disappears into the next stage.
                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    rt.Host.Console.WriteErrorLine(result.StdErr.TrimEnd('\n', '\r'));

                Executor.CheckExitCode(rt, name, result.ExitCode, line);
                return result.StdOut ?? string.Empty;
            }

            // Built-in (or function, or .NET call): capture what it writes.
            ShellHost saved = rt.Host;
            CapturingConsole capture = new(saved.Console, input);
            rt.Host = saved.WithConsole(capture);
            Value produced;
            try
            {
                produced = Executor.ExecuteCommand(rt, stage.Args, line, statementContext: false);
            }
            finally
            {
                rt.Host = saved;
            }

            // A built-in may write (print), return a value (arithmetic), or both.
            if (produced.Kind != ValueKind.Null)
            {
                string text = produced.AsString();
                if (text.Length > 0) capture.Output.Append(text).Append('\n');
            }
            return capture.Output.ToString();
        }

        /// <summary>
        /// A console whose output is collected rather than shown, and whose input is the text
        /// arriving from the previous stage. Errors still reach the real console: a diagnostic
        /// swallowed into a pipe is a diagnostic nobody reads.
        /// </summary>
        private sealed class CapturingConsole : IShellConsole
        {
            private readonly IShellConsole _inner;
            private readonly string[]? _lines;
            private int _next;

            public readonly StringBuilder Output = new();

            public CapturingConsole(IShellConsole inner, string? input)
            {
                _inner = inner;
                _lines = input?.Split('\n');
            }

            public void Write(string text) => Output.Append(text);
            public void WriteLine(string text) => Output.Append(text).Append('\n');
            public void WriteErrorLine(string text) => _inner.WriteErrorLine(text);

            public string? ReadLine()
            {
                if (_lines is null) return _inner.ReadLine();
                // A trailing newline produces one empty tail element; that is EOF, not a line.
                while (_next < _lines.Length)
                {
                    string line = _lines[_next++];
                    if (_next == _lines.Length && line.Length == 0) break;
                    return line.TrimEnd('\r');
                }
                return null;
            }
        }
    }
}
