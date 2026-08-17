using EasyShell.Exceptions;
using EasyShell.Parsing;
using EasyShell.Types;
using System;
using System.Text;

namespace EasyShell.Interactive
{
    /// <summary>
    /// A host built-in, tried before the engine sees the line. Return true when the input was
    /// handled; set <paramref name="exitCode"/> to a value to leave the REPL with that code.
    /// </summary>
    public delegate bool ReplBuiltin(string input, EasyShellEngine engine, out int? exitCode);

    /// <summary>
    /// Everything a host can vary about the prompt loop. The parts that are actually the same
    /// for every host - block accumulation, error handling, result printing, `exit` codes - are
    /// deliberately not on here.
    /// </summary>
    public sealed class ReplOptions
    {
        /// <summary>Pre-populated runtime. One is created when this is null.</summary>
        public Runtime? Runtime { get; init; }

        /// <summary>Printed once before the first prompt.</summary>
        public Action? Banner { get; init; }

        /// <summary>Evaluated per line, so a prompt can show the working directory.</summary>
        public Func<string>? Prompt { get; init; }

        /// <summary>Shown instead of <see cref="Prompt"/> while an IF/WHILE/FUNC block is open.</summary>
        public Func<string>? ContinuationPrompt { get; init; }

        /// <summary>Host commands, tried at top level before the engine. See <see cref="ReplBuiltin"/>.</summary>
        public ReplBuiltin? Builtins { get; init; }

        /// <summary>Text for the built-in <c>:help</c>.</summary>
        public string? HelpText { get; init; }

        /// <summary>
        /// How a diagnostic reaches the user. Defaults to stderr, which is right for a CLI; a
        /// terminal shell usually wants its own styling on stdout instead.
        /// </summary>
        public Action<string>? WriteError { get; init; }

        /// <summary>
        /// Run external programs in the foreground, on this terminal, rather than capturing them
        /// through pipes - see <see cref="Executor.InteractiveVariable"/>. On by default, because a
        /// REPL is by definition a person at a terminal.
        /// </summary>
        public bool Interactive { get; init; } = true;
    }

    /// <summary>
    /// The interactive prompt loop, shared rather than reimplemented.
    ///
    /// A host that wants an EasyShell prompt - the `easy` CLI, or HeadlessTerm's RetroShell - needs
    /// its own banner, prompt and built-ins, and needs nothing else of its own: block depth
    /// tracking, `exit` codes, error reporting and result printing are the same problem every time,
    /// and a copy of them in each host is a copy that drifts. Those live here; the differences are
    /// <see cref="ReplOptions"/>.
    /// </summary>
    public static class EasyShellRepl
    {
        public static int Run(ReplOptions options)
        {
            Runtime rt = options.Runtime ?? new Runtime();
            EasyShellEngine engine = new(rt);

            if (options.Interactive)
                rt.AssignOrDeclare(Executor.InteractiveVariable, new Value(ValueKind.Bool, true));

            Action<string> writeError = options.WriteError ?? Console.Error.WriteLine;
            Func<string> prompt = options.Prompt ?? (() => "es> ");
            Func<string> continuation = options.ContinuationPrompt ?? (() => "... ");

            options.Banner?.Invoke();

            StringBuilder buffer = new();
            int openBlocks = 0;

            while (true)
            {
                Console.Write(openBlocks > 0 ? continuation() : prompt());

                string? line;
                try { line = Console.ReadLine(); }
                catch (System.IO.IOException) { return 0; }   // stdin went away under us
                if (line is null) return 0;                   // Ctrl+D / Ctrl+Z / EOF

                string trimmed = line.Trim();

                // Host built-ins and REPL commands apply at the top level only: inside an open
                // block every line is body text, including one that happens to read like a command.
                if (openBlocks == 0)
                {
                    if (trimmed.Length == 0) continue;

                    if (options.Builtins is not null && options.Builtins(trimmed, engine, out int? hostExit))
                    {
                        if (hostExit is { } code) return code;
                        continue;
                    }

                    if (trimmed.StartsWith(':'))
                    {
                        if (HandleReplCommand(trimmed, options.HelpText, engine))
                            return 0;
                        continue;
                    }
                }

                buffer.AppendLine(line);
                UpdateBlockDepth(trimmed, ref openBlocks);
                if (openBlocks > 0) continue;

                string unit = buffer.ToString();
                buffer.Clear();

                try
                {
                    // Print the last expression's value, so the prompt doubles as a calculator.
                    Value result = engine.RunUnit(unit, origin: "<repl>");
                    if (result.Kind != ValueKind.Null)
                        Console.WriteLine(result.AsString());
                }
                catch (ScriptExitException ex)
                {
                    // `exit 3` at the prompt should still be worth 3 to whoever launched us.
                    return ex.ExitCode;
                }
                catch (EasyShellException ex)
                {
                    writeError($"(Error) {ex.Message}");
                }
                catch (Exception ex)
                {
                    writeError($"Unhandled error: {ex.Message}");
                }
            }
        }

        private static bool HandleReplCommand(string cmd, string? helpText, EasyShellEngine engine)
        {
            switch (cmd.ToLowerInvariant())
            {
                case ":help":
                    Console.WriteLine(helpText ?? "No help text was supplied by this host.");
                    return false;

                case ":exit":
                case ":quit":
                    return true;

                case ":vars":
                    foreach ((string? name, string? kind, string? value) in engine.DumpVariables())
                        Console.WriteLine($"{kind} {name} = {value}");
                    return false;

                case ":funcs":
                    foreach (string fn in engine.DumpFunctions())
                        Console.WriteLine(fn);
                    return false;

                default:
                    Console.WriteLine("Unknown REPL command. Try :help");
                    return false;
            }
        }

        /// <summary>
        /// Track IF/WHILE/FUNC ... END so a block can be typed across several lines. Comments are
        /// stripped the same way the parser strips them, so a '#' inside a string is not mistaken
        /// for one.
        /// </summary>
        private static void UpdateBlockDepth(string trimmed, ref int openBlocks)
        {
            trimmed = Parser.StripComment(trimmed).Trim();
            if (trimmed.Length == 0) return;

            string head = FirstWord(trimmed);

            if (head.Equals("IF", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("WHILE", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("FUNC", StringComparison.OrdinalIgnoreCase))
            {
                openBlocks++;
            }
            else if (head.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                openBlocks = Math.Max(0, openBlocks - 1);
            }
        }

        private static string FirstWord(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            return s[start..i];
        }
    }
}
