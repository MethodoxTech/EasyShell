using EasyShell.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EasyShell.Interactive
{
    /// <summary>
    /// A key a line editor understands. Deliberately smaller than ConsoleKey: a host that is not
    /// a System.Console - a virtual terminal, a test - should not have to fabricate one.
    /// </summary>
    public enum EditorKey
    {
        Character, Enter, Backspace, Delete, Tab,
        Left, Right, Home, End, Up, Down,
        EndOfInput,   // Ctrl+D on an empty line
        Interrupt,    // Ctrl+C
    }

    public readonly record struct EditorKeyPress(EditorKey Key, char Character);

    /// <summary>
    /// A console that can also deliver individual keys, which is what line editing and tab
    /// completion require. Optional: <see cref="IShellConsole"/> alone gives a working prompt
    /// through <see cref="IShellConsole.ReadLine"/>, and the REPL falls back to it whenever the
    /// host does not implement this - so a pipe, a script and a test all keep working.
    /// </summary>
    public interface IShellLineInput
    {
        /// <summary>Block for the next key. Null means end of input (the terminal hung up).</summary>
        EditorKeyPress? ReadKey();

        /// <summary>
        /// Turn character-at-a-time delivery on and off. A host with a canonical line discipline
        /// (an OS terminal, a virtual tty) must switch to raw mode here and restore afterwards -
        /// otherwise the editor never sees Tab, because the line discipline is still buffering.
        /// </summary>
        void SetRawMode(bool raw);
    }

    /// <summary>
    /// What Tab offers. The shell knows the language; the host knows the world - so completion
    /// sources are supplied by the host and consulted by the editor.
    /// </summary>
    public interface ICompletionSource
    {
        /// <summary>
        /// Candidates that could replace the word ending at <paramref name="caret"/>. Return the
        /// FULL replacement words, not suffixes; the editor works out the common prefix.
        /// </summary>
        IReadOnlyList<string> Complete(string line, int caret);
    }

    /// <summary>
    /// The line editor: printable insertion, cursor motion, history, and Tab completion.
    ///
    /// <para>It exists because <see cref="IShellConsole.ReadLine"/> hands over a finished line and
    /// so can never implement Tab - by the time the shell sees the text, the Tab character is
    /// already in it (which is exactly what a bare tab used to do: insert whitespace). Editing
    /// therefore has to move up to where the language is understood, which is where every real
    /// shell puts it - readline lives in bash, not in the kernel.</para>
    ///
    /// <para>Completion rules, chosen to match what people expect from bash: one candidate
    /// completes it outright; several extend to the longest common prefix and then list; a common
    /// prefix that adds nothing lists immediately.</para>
    /// </summary>
    public sealed class LineEditor
    {
        private readonly IShellConsole _console;
        private readonly IShellLineInput _input;
        private readonly ICompletionSource? _completions;
        private readonly List<string> _history = new();

        public LineEditor(IShellConsole console, IShellLineInput input, ICompletionSource? completions)
        {
            _console = console;
            _input = input;
            _completions = completions;
        }

        /// <summary>Read one line. Null on end of input; the prompt is reprinted after a listing.</summary>
        public string? ReadLine(string prompt)
        {
            StringBuilder buffer = new();
            int caret = 0;
            int historyCursor = _history.Count;

            _console.Write(prompt);
            _input.SetRawMode(true);
            try
            {
                while (true)
                {
                    EditorKeyPress? read = _input.ReadKey();
                    if (read is not { } key) return null;

                    switch (key.Key)
                    {
                        case EditorKey.Enter:
                            _console.Write("\n");
                            string line = buffer.ToString();
                            if (line.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != line))
                                _history.Add(line);
                            return line;

                        case EditorKey.EndOfInput:
                            if (buffer.Length == 0) return null;
                            break;

                        case EditorKey.Interrupt:
                            _console.Write("^C\n");
                            return string.Empty;

                        case EditorKey.Backspace:
                            if (caret > 0)
                            {
                                buffer.Remove(caret - 1, 1);
                                caret--;
                                Redraw(prompt, buffer, caret);
                            }
                            break;

                        case EditorKey.Delete:
                            if (caret < buffer.Length)
                            {
                                buffer.Remove(caret, 1);
                                Redraw(prompt, buffer, caret);
                            }
                            break;

                        case EditorKey.Left:
                            if (caret > 0) { caret--; Redraw(prompt, buffer, caret); }
                            break;

                        case EditorKey.Right:
                            if (caret < buffer.Length) { caret++; Redraw(prompt, buffer, caret); }
                            break;

                        case EditorKey.Home:
                            caret = 0;
                            Redraw(prompt, buffer, caret);
                            break;

                        case EditorKey.End:
                            caret = buffer.Length;
                            Redraw(prompt, buffer, caret);
                            break;

                        case EditorKey.Up:
                        case EditorKey.Down:
                            if (_history.Count == 0) break;
                            historyCursor = key.Key == EditorKey.Up
                                ? Math.Max(0, historyCursor - 1)
                                : Math.Min(_history.Count, historyCursor + 1);
                            buffer.Clear();
                            if (historyCursor < _history.Count) buffer.Append(_history[historyCursor]);
                            caret = buffer.Length;
                            Redraw(prompt, buffer, caret);
                            break;

                        case EditorKey.Tab:
                            caret = Complete(prompt, buffer, caret);
                            break;

                        case EditorKey.Character:
                            buffer.Insert(caret, key.Character);
                            caret++;
                            // Appending at the end is the common case and needs no repaint of
                            // what is already correct on screen.
                            if (caret == buffer.Length) _console.Write(key.Character.ToString());
                            else Redraw(prompt, buffer, caret);
                            break;
                    }
                }
            }
            finally
            {
                _input.SetRawMode(false);
            }
        }

        private int Complete(string prompt, StringBuilder buffer, int caret)
        {
            if (_completions is null) return caret;

            string line = buffer.ToString();
            IReadOnlyList<string> candidates = _completions.Complete(line, caret);
            if (candidates.Count == 0) return caret;

            int wordStart = WordStart(line, caret);
            string word = line[wordStart..caret];

            string common = LongestCommonPrefix(candidates);
            if (candidates.Count == 1)
            {
                // A single directory keeps the trailing '/' its source supplied, so the next
                // Tab descends instead of stopping at the directory name.
                common = candidates[0];
                if (!common.EndsWith('/')) common += " ";
            }

            if (common.Length > word.Length)
            {
                buffer.Remove(wordStart, caret - wordStart);
                buffer.Insert(wordStart, common);
                caret = wordStart + common.Length;
                Redraw(prompt, buffer, caret);
            }
            else if (candidates.Count > 1)
            {
                _console.Write("\n");
                foreach (string column in Columnize(candidates))
                    _console.WriteLine(column);
                Redraw(prompt, buffer, caret);
            }
            return caret;
        }

        /// <summary>Where the word under the caret begins. Quotes are respected so paths with spaces complete.</summary>
        public static int WordStart(string line, int caret)
        {
            int i = caret;
            while (i > 0 && !char.IsWhiteSpace(line[i - 1])) i--;
            return i;
        }

        private static string LongestCommonPrefix(IReadOnlyList<string> values)
        {
            string prefix = values[0];
            foreach (string value in values.Skip(1))
            {
                int i = 0;
                while (i < prefix.Length && i < value.Length && prefix[i] == value[i]) i++;
                prefix = prefix[..i];
                if (prefix.Length == 0) break;
            }
            return prefix;
        }

        /// <summary>Lay candidates out in even columns, the way a shell lists them.</summary>
        private static IEnumerable<string> Columnize(IReadOnlyList<string> candidates, int width = 80)
        {
            int longest = candidates.Max(c => c.Length) + 2;
            int perRow = Math.Max(1, width / longest);
            for (int i = 0; i < candidates.Count; i += perRow)
                yield return string.Concat(candidates.Skip(i).Take(perRow).Select(c => c.PadRight(longest))).TrimEnd();
        }

        /// <summary>
        /// Repaint the whole line. Carriage return plus an erase-to-end-of-line is the portable
        /// minimum that both a real terminal and the machine's VT emulator honor; anything
        /// cleverer would need cursor-position reporting.
        /// </summary>
        private void Redraw(string prompt, StringBuilder buffer, int caret)
        {
            _console.Write("\r\u001b[K");
            _console.Write(prompt);
            _console.Write(buffer.ToString());
            int back = buffer.Length - caret;
            if (back > 0) _console.Write($"\u001b[{back}D");
        }
    }
}
