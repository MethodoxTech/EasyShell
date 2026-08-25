using System;
using System.Collections.Generic;
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

        /// <summary>
        /// One line of free-text input, with the prompt written to the host console.
        ///
        /// End-of-input is not an error here: a piped or redirected session simply takes the
        /// default, which is what lets a script that asks an optional question still run unattended.
        /// <see cref="Choose"/> makes the opposite call, and says why.
        /// </summary>
        public static string Prompt(ShellHost host, string message, string fallback)
        {
            if (!string.IsNullOrEmpty(message))
                host.Console.Write(message.EndsWith(' ') ? message : message + " ");

            string? typed = host.Console.ReadLine();
            return string.IsNullOrWhiteSpace(typed) ? fallback : typed.Trim();
        }

        /// <summary>
        /// A numbered menu over <paramref name="options"/>, answered by index, by full name or by
        /// any unambiguous prefix, and re-asked until one of those lands.
        ///
        /// Unlike <see cref="Prompt"/> there is deliberately no default. The question this exists
        /// for - which store is this build for - has no safe guess: silently picking the first
        /// option would let an unattended run upload a Steam build to Itch.io. So an empty answer
        /// re-asks, and end-of-input fails with a message naming the options, which is a build that
        /// stops rather than a build that goes to the wrong place.
        /// </summary>
        public static string Choose(ShellHost host, string message, IReadOnlyList<string> options)
        {
            if (options.Count == 0)
                throw new EasyShellException("CHOOSE expects at least one option.");

            if (!string.IsNullOrEmpty(message))
                host.Console.WriteLine(message);
            for (int i = 0; i < options.Count; i++)
                host.Console.WriteLine($"  {i + 1}) {options[i]}");

            while (true)
            {
                host.Console.Write($"Enter 1-{options.Count} or a name: ");

                string? typed = host.Console.ReadLine();
                if (typed is null)
                    throw new EasyShellException(
                        $"CHOOSE reached end-of-input with nothing chosen. This session is not interactive, " +
                        $"so pass the answer as a script argument instead. Options were: {string.Join(", ", options)}.");

                typed = typed.Trim();
                if (typed.Length == 0)
                    continue;

                if (int.TryParse(typed, out int index) && index >= 1 && index <= options.Count)
                    return options[index - 1];

                string? resolved = ResolveOption(options, typed);
                if (resolved is not null)
                    return resolved;

                host.Console.WriteErrorLine($"'{typed}' is not one of the options.");
            }
        }

        /// <summary>
        /// Exact match first, then a unique prefix. A prefix shared by two options is rejected
        /// rather than resolved to whichever comes first.
        /// </summary>
        private static string? ResolveOption(IReadOnlyList<string> options, string typed)
        {
            foreach (string option in options)
                if (string.Equals(option, typed, StringComparison.OrdinalIgnoreCase))
                    return option;

            string? candidate = null;
            foreach (string option in options)
            {
                if (!option.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate is not null)
                    return null;
                candidate = option;
            }
            return candidate;
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
