using EasyShell.Exceptions;
using EasyShell.Types;
using System;
using System.Text;

namespace EasyShell
{
    public static class EasyShellRepl
    {
        public static int Run(string helpText, string version, Runtime? runtime)
        {
            Console.WriteLine($"Easy Shell {version}  (:help for help)");

            // Create runtime
            EasyShellEngine engine = new(runtime);

            StringBuilder buffer = new();
            int openBlocks = 0;
            while (true)
            {
                Console.Write(openBlocks > 0 ? "... " : "es> ");
                string? line = Console.ReadLine();
                if (line is null) return 0; // Ctrl+Z / EOF

                string trimmed = line.Trim();
                if (openBlocks == 0 && trimmed.StartsWith(":"))
                {
                    if (HandleReplCommand(trimmed, helpText, engine))
                        return 0;
                    continue;
                }

                // Accumulate
                buffer.AppendLine(line);

                // Update block depth using lightweight keyword scan (case-insensitive, ignores comments)
                UpdateBlockDepth(trimmed, ref openBlocks);

                // If not in a block, execute current buffer as a unit
                if (openBlocks == 0)
                {
                    string unit = buffer.ToString();
                    buffer.Clear();

                    try
                    {
                        // Execute and print last expression value
                        Value result = engine.RunUnit(unit, origin: "<repl>");
                        if (result.Kind != ValueKind.Null)
                            Console.WriteLine(result.AsString());
                    }
                    catch (EasyShellException ex)
                    {
                        Console.Error.WriteLine($"(Error) {ex.Message}");
                    }
                    catch (ScriptExitException)
                    {
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Unhandled error: {ex}");
                    }
                }
            }
        }

        private static bool HandleReplCommand(string cmd, string helpText, EasyShellEngine engine)
        {
            switch (cmd.ToLowerInvariant())
            {
                case ":help":
                    Console.WriteLine(helpText);
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

        private static void UpdateBlockDepth(string trimmed, ref int openBlocks)
        {
            // Strip comments quickly
            int hash = trimmed.IndexOf('#');
            if (hash >= 0) trimmed = trimmed[..hash].Trim();
            if (trimmed.Length == 0) return;

            // Very simple: if a line starts with IF/WHILE/FUNC, increment. If starts with END, decrement.
            // This matches your grammar where blocks end with END.
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
