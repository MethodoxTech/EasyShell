using System;
using System.Collections.Generic;
using System.IO;

namespace EasyShell.Hosting
{
    /// <summary>System.Console, verbatim.</summary>
    public sealed class HostConsole : IShellConsole
    {
        public void Write(string text) => Console.Write(text);
        public void WriteLine(string text) => Console.WriteLine(text);
        public void WriteErrorLine(string text) => Console.Error.WriteLine(text);
        public string? ReadLine() => Console.ReadLine();
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
