using System;
using System.Text.RegularExpressions;
using EasyShell.Exceptions;

namespace EasyShell.Hosting
{
    /// <summary>
    /// The file-system and environment commands of the language, implemented against
    /// <see cref="ShellHost"/> instead of against System.IO directly.
    ///
    /// These used to be reflection aliases onto CommonUtilities/System.IO statics, which made them
    /// impossible to virtualize - a static cannot know which Runtime is asking. The behavior here
    /// mirrors the originals (CommonUtilitiesTests documents it); CommonUtilities itself remains,
    /// unchanged and host-only, for scripts that invoke it as a fully-qualified .NET call.
    /// </summary>
    internal static class ShellBuiltins
    {
        private static string Expand(ShellHost host, string path)
            => host.FileSystem.NormalizeSeparators(host.Environment.ExpandVariables(path));

        public static bool Exists(ShellHost host, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = Expand(host, path);
            return host.FileSystem.FileExists(path) || host.FileSystem.DirectoryExists(path);
        }

        public static bool Remove(ShellHost host, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = Expand(host, path);
            if (host.FileSystem.FileExists(path)) { host.FileSystem.DeleteFile(path); return true; }
            if (host.FileSystem.DirectoryExists(path)) { host.FileSystem.DeleteDirectory(path); return true; }
            return false;
        }

        public static int RemoveAll(ShellHost host, string folder, string pattern, bool recursive)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(pattern)) return 0;
            folder = Expand(host, folder);
            if (!host.FileSystem.DirectoryExists(folder)) return 0;

            int count = 0;
            foreach (string match in host.FileSystem.EnumerateFiles(folder, pattern, recursive))
            {
                host.FileSystem.DeleteFile(match);
                count++;
            }
            return count;
        }

        public static bool Copy(ShellHost host, string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return false;
            source = Expand(host, source);
            target = Expand(host, target);

            IShellFileSystem fs = host.FileSystem;
            if (fs.FileExists(source))
            {
                string? targetDir = fs.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) fs.CreateDirectory(targetDir);
                fs.CopyFile(source, target);
                return true;
            }
            if (fs.DirectoryExists(source))
            {
                CopyDirectoryRecursive(fs, source, target);
                return true;
            }
            return false;
        }

        public static bool Move(ShellHost host, string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return false;
            source = Expand(host, source);
            target = Expand(host, target);

            IShellFileSystem fs = host.FileSystem;
            if (fs.FileExists(source))
            {
                string? targetDir = fs.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) fs.CreateDirectory(targetDir);
                fs.MoveFile(source, target);
                return true;
            }
            if (fs.DirectoryExists(source))
            {
                string? parent = fs.GetDirectoryName(target.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(parent)) fs.CreateDirectory(parent);
                if (fs.DirectoryExists(target) || fs.FileExists(target)) Remove(host, target);
                fs.MoveDirectory(source, target);
                return true;
            }
            return false;
        }

        public static void Replace(ShellHost host, string path, string source, string replacement)
        {
            path = Expand(host, path);
            if (!host.FileSystem.FileExists(path))
                throw new EasyShellException($"File {path} doesn't exist.");
            host.FileSystem.WriteAllText(path, host.FileSystem.ReadAllText(path).Replace(source, replacement));
        }

        public static void RegexReplace(ShellHost host, string path, string pattern, string replacement)
        {
            path = Expand(host, path);
            if (!host.FileSystem.FileExists(path))
                throw new EasyShellException($"File {path} doesn't exist.");
            host.FileSystem.WriteAllText(path, Regex.Replace(host.FileSystem.ReadAllText(path), pattern, replacement));
        }

        private static void CopyDirectoryRecursive(IShellFileSystem fs, string source, string target)
        {
            fs.CreateDirectory(target);
            foreach (string file in fs.EnumerateFiles(source, "*", recursive: false))
                fs.CopyFile(file, fs.Combine(target, fs.GetFileName(file)));
            foreach (string dir in fs.EnumerateDirectories(source))
                CopyDirectoryRecursive(fs, dir, fs.Combine(target, fs.GetFileName(dir.TrimEnd('/', '\\'))));
        }
    }
}
