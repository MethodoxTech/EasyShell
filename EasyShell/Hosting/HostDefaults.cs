using EasyShell.Interactive;
using System;
using System.Collections.Generic;
using System.IO;

namespace EasyShell.Hosting
{
    /// <summary>
    /// System.Console, verbatim - plus the key-at-a-time input that line editing and Tab
    /// completion require, because the CLI is the one host whose terminal is the process's own.
    /// </summary>
    public sealed class HostConsole : IShellConsole, IShellLineInput
    {
        public void Write(string text) => Console.Write(text);
        public void WriteLine(string text) => Console.WriteLine(text);
        public void WriteErrorLine(string text) => Console.Error.WriteLine(text);
        public string? ReadLine() => Console.ReadLine();

        #region Line input
        /// <summary>
        /// Only a person at a terminal can be sent keys. Piped input (`echo ... | easy --repl`),
        /// a redirected run and a test harness all have to keep the whole-line path, so the check
        /// is on the real handles rather than on <see cref="Console.In"/> - which a caller may
        /// have swapped without the terminal having gone anywhere.
        /// </summary>
        public bool IsInteractive
        {
            get
            {
                try { return !Console.IsInputRedirected && !Console.IsOutputRedirected; }
                catch (IOException) { return false; }   // no console attached at all
            }
        }

        /// <summary>
        /// The line discipline needs no help here: Console.ReadKey(intercept) already delivers
        /// characters one at a time on every platform .NET supports, and on Unix it drives termios
        /// itself for the duration of the call. The one thing that does have to change is Ctrl+C,
        /// which otherwise kills the shell instead of reaching the editor as an interrupt - and it
        /// must change back, because <see cref="ProcessInvoker.RunForeground"/> relies on the
        /// ordinary handler while a child program owns the terminal.
        /// </summary>
        public void SetRawMode(bool raw)
        {
            try { Console.TreatControlCAsInput = raw; }
            catch (IOException) { /* not a terminal; the editor will find that out on its first key */ }
        }

        public EditorKeyPress? ReadKey()
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return null; }   // input is not a terminal after all

            bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
            if (control && key.Key == ConsoleKey.C) return new(EditorKey.Interrupt, '\0');
            if (control && key.Key == ConsoleKey.D) return new(EditorKey.EndOfInput, '\0');

            return key.Key switch
            {
                ConsoleKey.Enter => new(EditorKey.Enter, '\0'),
                ConsoleKey.Tab => new(EditorKey.Tab, '\0'),
                ConsoleKey.Backspace => new(EditorKey.Backspace, '\0'),
                ConsoleKey.Delete => new(EditorKey.Delete, '\0'),
                ConsoleKey.LeftArrow => new(EditorKey.Left, '\0'),
                ConsoleKey.RightArrow => new(EditorKey.Right, '\0'),
                ConsoleKey.Home => new(EditorKey.Home, '\0'),
                ConsoleKey.End => new(EditorKey.End, '\0'),
                ConsoleKey.UpArrow => new(EditorKey.Up, '\0'),
                ConsoleKey.DownArrow => new(EditorKey.Down, '\0'),
                // Anything else is text when it is printable, and dropped when it is not - a key
                // with no mapping must never leave a control character in the line.
                _ => new(EditorKey.Character, key.KeyChar >= ' ' ? key.KeyChar : '\0'),
            };
        }
        #endregion
    }

    /// <summary>System.IO, verbatim - including the retrying deletes scripts rely on.</summary>
    public sealed class HostFileSystem : IShellFileSystem
    {
        public string NormalizeSeparators(string path) => Commands.CommonUtilities.NormalizeSeparators(path);
        public string GetFullPath(string path) => Path.GetFullPath(path);
        public string Combine(string a, string b) => Path.Combine(a, b);
        public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);
        public string GetFileName(string path) => Path.GetFileName(path);
        public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);

        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive) =>
            Directory.EnumerateFiles(directory, pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        public IEnumerable<string> EnumerateDirectories(string directory) =>
            Directory.EnumerateDirectories(directory);

        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        // Locked-file retries live in CommonUtilities; deletes here get the same resilience so a
        // virtual host is not obliged to reproduce host-specific lock behavior.
        public void DeleteFile(string path) => Commands.CommonUtilities.DeleteFileResilient(path);
        public void DeleteDirectory(string path) => Commands.CommonUtilities.DeleteDirectoryResilient(path);

        public void CopyFile(string source, string target) => File.Copy(source, target, overwrite: true);

        public void MoveFile(string source, string target)
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(source, target);
        }

        public void MoveDirectory(string source, string target) => Directory.Move(source, target);
    }

    /// <summary>ProgramResolver + ProcessInvoker, verbatim.</summary>
    public sealed class HostProcessRunner : IShellProcessRunner
    {
        public string? Resolve(string command) => ProgramResolver.Resolve(command);

        public int RunForeground(string program, List<string> arguments, TimeSpan? timeout) =>
            ProcessInvoker.RunForeground(program, arguments, timeout);

        public ProcessInvoker.ProcessResult RunCaptured(string program, List<string> arguments, Action<string>? onLine, TimeSpan? timeout) =>
            ProcessInvoker.RunStreaming(program, arguments, onLine, timeout);

        public ProcessInvoker.ProcessResult RunPiped(string program, List<string> arguments, string? standardInput, TimeSpan? timeout) =>
            ProcessInvoker.RunPiped(program, arguments, standardInput, timeout);
    }

    /// <summary>Process-global cwd and environment, verbatim - one `easy` process, one world.</summary>
    public sealed class HostEnvironment : IShellEnvironment
    {
        public string CurrentDirectory
        {
            get => Directory.GetCurrentDirectory();
            set => Directory.SetCurrentDirectory(value);
        }

        public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
        public void SetVariable(string name, string? value) => Environment.SetEnvironmentVariable(name, value);
        public string ExpandVariables(string text) => Environment.ExpandEnvironmentVariables(text);
    }
}
