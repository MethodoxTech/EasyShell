using EasyShell.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EasyShell.Interactive
{
    /// <summary>
    /// What Tab offers on a real machine: the language's own commands, the variables this session
    /// has bound, the programs on PATH, and the files and folders around the working directory.
    ///
    /// <para>Everything is read through <see cref="Runtime.Host"/> rather than through
    /// System.IO/System.Environment directly, so a host that virtualizes the world gets a
    /// completion source that completes ITS world - the same reason the built-ins moved onto the
    /// host in the first place.</para>
    ///
    /// <para>Deliberately one flat pool for every word, with no special case for the first one. A
    /// shell that only completes commands in head position is guessing at a grammar it cannot
    /// actually see (a program name can come from a variable, and `RUN` takes one as an argument),
    /// and guessing wrong is worse than offering a candidate the user did not want.</para>
    /// </summary>
    public sealed class ShellCompletionSource : ICompletionSource
    {
        #region Construction
        private readonly Runtime _runtime;
        public ShellCompletionSource(Runtime runtime)
            => _runtime = runtime;
        #endregion

        #region Configurations
        /// <summary>Names are case-sensitive on Unix and are not on Windows; completion follows the filesystem it completes.</summary>
        private static readonly StringComparer NameComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly StringComparison NameComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static readonly char[] PathSeparators = ['/', '\\'];
        #endregion

        #region Methods
        public IReadOnlyList<string> Complete(string line, int caret)
        {
            if (line is null || caret < 0 || caret > line.Length)
                return [];

            string word = line[LineEditor.WordStart(line, caret)..caret];

            // A '$' word can only ever be a variable, so offering anything else would be noise.
            if (word.StartsWith('$'))
                return Collect(results => AddVariables(results, word));

            // A word that already names a directory is a path and nothing else: `sub/gi` is a file
            // inside `sub`, never the program `git`.
            int separator = word.LastIndexOfAny(PathSeparators);

            return Collect(results =>
            {
                // An empty word would otherwise dump every command and every program on PATH -
                // several thousand lines on a normal machine, which is a worse answer than the
                // working directory the user is standing in.
                if (separator < 0 && word.Length != 0)
                {
                    AddCommands(results, word);
                    AddProgramsOnPath(results, word);
                }
                AddPathEntries(results, word, separator);
            });
        }
        #endregion

        #region Routines
        private static IReadOnlyList<string> Collect(Action<SortedSet<string>> fill)
        {
            SortedSet<string> results = new(NameComparer);
            fill(results);
            return [.. results];
        }

        /// <summary>Variables are global and case-insensitive, so completion is too.</summary>
        private void AddVariables(SortedSet<string> results, string word)
        {
            string prefix = word[1..];
            foreach (string name in _runtime.VariableNames)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add("$" + name);
        }

        /// <summary>The language's own command set - case-insensitive, because the language is.</summary>
        private static void AddCommands(SortedSet<string> results, string word)
        {
            foreach (string name in Executor.BuiltinCommandNames)
                if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    results.Add(name);
        }

        private void AddProgramsOnPath(SortedSet<string> results, string word)
        {
            IShellFileSystem fs = _runtime.Host.FileSystem;
            string path = _runtime.Host.Environment.GetVariable("PATH") ?? string.Empty;

            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (string file in Safely(() => fs.EnumerateFiles(entry.Trim(), word + "*", recursive: false)))
                    results.Add(TypedName(fs.GetFileName(file)));
        }

        private void AddPathEntries(SortedSet<string> results, string word, int separator)
        {
            IShellFileSystem fs = _runtime.Host.FileSystem;

            // The directory half is echoed back verbatim, because the candidate must be the WHOLE
            // replacement word - `sub/fi` completes to `sub/file.txt`, not to `file.txt`.
            string typedDirectory = separator >= 0 ? word[..(separator + 1)] : string.Empty;
            string namePrefix = word[(separator + 1)..];
            char separatorCharacter = separator >= 0 ? word[separator] : Path.DirectorySeparatorChar;

            string root = _runtime.Host.Environment.CurrentDirectory;
            if (typedDirectory.Length != 0)
            {
                string? resolved = Safely<string?>(() => fs.GetFullPath(fs.Combine(root, typedDirectory)), null);
                if (resolved is null || !Safely(() => fs.DirectoryExists(resolved), false))
                    return;
                root = resolved;
            }

            // A directory keeps its trailing separator so the next Tab descends into it instead of
            // stopping at the name - see LineEditor.Complete.
            foreach (string directory in Safely(() => fs.EnumerateDirectories(root)))
            {
                string name = fs.GetFileName(directory);
                if (name.StartsWith(namePrefix, NameComparison))
                    results.Add(typedDirectory + name + separatorCharacter);
            }

            foreach (string file in Safely(() => fs.EnumerateFiles(root, namePrefix + "*", recursive: false)))
                results.Add(typedDirectory + fs.GetFileName(file));
        }

        /// <summary>
        /// The name you would actually type, which on Windows is the name without the PATHEXT
        /// suffix the launcher supplies for you: `git`, not `git.exe`.
        /// </summary>
        private string TypedName(string fileName)
        {
            if (!OperatingSystem.IsWindows())
                return fileName;

            string pathext = _runtime.Host.Environment.GetVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            foreach (string raw in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string extension = raw.Trim();
                if (extension.Length != 0 && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return fileName[..^extension.Length];
            }
            return fileName;
        }

        /// <summary>
        /// A prompt is not the place to report that one PATH entry is unreadable or that a folder
        /// vanished between two keystrokes. Anything that goes wrong here simply offers nothing.
        /// Materialized rather than lazy, so a failure inside the enumerator is caught too.
        /// </summary>
        private static string[] Safely(Func<IEnumerable<string>> enumerate)
        {
            try { return [.. enumerate()]; }
            catch { return []; }
        }
        private static T Safely<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }
        #endregion
    }
}
