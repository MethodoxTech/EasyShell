using EasyShell.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace EasyShell.Commands
{
    public static class CommonUtilities
    {
        #region Configurations
        /// <summary>
        /// How many times a delete is retried before giving up. Locks from cloud sync clients,
        /// indexers, antivirus and just-exited child processes are usually transient.
        /// </summary>
        public const int DeleteAttempts = 5;
        /// <summary>Base backoff between delete attempts; doubles each round.</summary>
        public const int DeleteRetryBaseDelayMilliseconds = 150;
        #endregion

        #region Path
        /// <summary>
        /// JOINPATH &lt;part&gt; &lt;part&gt; [...] -> a single path using this platform's separator.
        ///
        /// Unlike System.IO.Path.Join, separators are normalized first: "\" and "/" are BOTH
        /// accepted as separators on every platform. Path.Join only recognizes "\" on Windows, so
        /// a script written as "\..\..\Publish" silently produced a path with literal backslashes
        /// in the file name on Linux/macOS rather than failing - a trap that is very hard to spot,
        /// because the result is still a "valid" (but wrong) path.
        /// </summary>
        public static string JoinPath(string a, string b)
            => JoinParts([a, b]);
        public static string JoinPath(string a, string b, string c)
            => JoinParts([a, b, c]);
        public static string JoinPath(string a, string b, string c, string d)
            => JoinParts([a, b, c, d]);
        public static string JoinPath(string a, string b, string c, string d, string e)
            => JoinParts([a, b, c, d, e]);
        public static string JoinPath(string a, string b, string c, string d, string e, string f)
            => JoinParts([a, b, c, d, e, f]);
        private static string JoinParts(string?[] parts)
        {
            IEnumerable<string> normalized = parts
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => NormalizeSeparators(p!));

            return Path.Join([.. normalized]);
        }
        /// <summary>
        /// Rewrites the foreign directory separator to this platform's separator, so that scripts
        /// authored with either convention behave identically everywhere.
        /// </summary>
        public static string NormalizeSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Remark: On Unix "\" is technically a legal character in a file name. Treating it as a
            // separator is the deliberate trade-off - build scripts are written on Windows far more
            // often than anyone deliberately puts a backslash in a file name.
            return path
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }
        #endregion

        #region File System
        /// <summary>
        /// EXISTS <path>  -> true if file OR directory exists
        /// </summary>
        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = ExpandPath(path);

            return File.Exists(path) || Directory.Exists(path);
        }
        /// <summary>
        /// REMOVE <path>  -> true if file OR directory exists and is removed
        ///
        /// Deleting a directory that something else is holding open used to throw a raw IOException
        /// straight out of the interpreter. Instead: clear read-only attributes, retry a few times
        /// with backoff (transient locks from cloud sync / indexers / a just-exited build usually
        /// clear on their own), and if it still fails, report which files are locked as a normal
        /// script error rather than an unhandled crash.
        /// </summary>
        public static bool Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = ExpandPath(path);

            // File
            if (File.Exists(path))
            {
                DeleteFileWithRetry(path);
                return true;
            }

            // Directory
            if (Directory.Exists(path))
            {
                DeleteDirectoryWithRetry(path);
                return true;
            }

            // Nothing to remove
            return false;
        }
        /// <summary>
        /// COPY <source> -> <target>
        /// - If source is a file: copies file to target
        /// - If source is a directory: recursively copies directory to target
        /// - Returns true if copy succeeds, false otherwise
        /// </summary>
        public static bool Copy(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return false;

            source = ExpandPath(source);
            target = ExpandPath(target);

            // File
            if (File.Exists(source))
            {
                string? targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);

                File.Copy(source, target, overwrite: true);
                return true;
            }

            // Directory
            if (Directory.Exists(source))
            {
                CopyDirectoryRecursive(source, target);
                return true;
            }

            return false;
        }
        /// <summary>
        /// MOVE <source> -> <target>
        /// - If source is a file: moves file to target (overwrites if target exists)
        /// - If source is a directory: moves directory to target (replaces target if it exists)
        /// - Returns true if move succeeds, false otherwise
        /// </summary>
        public static bool Move(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return false;

            source = ExpandPath(source);
            target = ExpandPath(target);

            // File
            if (File.Exists(source))
            {
                string? targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);

                // Ensure overwrite behavior
                if (File.Exists(target))
                    File.Delete(target);

                File.Move(source, target);
                return true;
            }

            // Directory
            if (Directory.Exists(source))
            {
                string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target));
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                // Replace target if it already exists
                if (Directory.Exists(target) || File.Exists(target))
                    Remove(target);

                Directory.Move(source, target);
                return true;
            }

            return false;
        }
        public static void Replace(string path, string source, string replacement)
        {
            path = ExpandPath(path);
            if (!File.Exists(path))
                throw new EasyShellException($"File {path} doesn't exist.");

            string content = File.ReadAllText(path);
            string replace = content.Replace(source, replacement);
            File.WriteAllText(path, replace);
        }
        public static void RegexReplace(string path, string pattern, string replacement)
        {
            path = ExpandPath(path);
            if (!File.Exists(path))
                throw new EasyShellException($"File {path} doesn't exist.");

            string content = File.ReadAllText(path);
            string replace = Regex.Replace(content, pattern, replacement);
            File.WriteAllText(path, replace);
        }
        #endregion

        #region Routines
        private static string ExpandPath(string path)
            => NormalizeSeparators(Environment.ExpandEnvironmentVariables(path));
        private static void DeleteFileWithRetry(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    ClearReadOnly(path);
                    File.Delete(path);
                    return;
                }
                catch (Exception e) when (IsTransientIOFailure(e))
                {
                    if (attempt >= DeleteAttempts)
                        throw new EasyShellException($"Cannot delete file '{path}' after {DeleteAttempts} attempts - it is in use by another process. ({e.Message})");

                    Thread.Sleep(DeleteRetryBaseDelayMilliseconds * (1 << (attempt - 1)));
                }
            }
        }
        private static void DeleteDirectoryWithRetry(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    ClearReadOnlyRecursive(path);
                    Directory.Delete(path, recursive: true);

                    // On Windows the directory entry can linger briefly after Delete returns, which
                    // makes an immediately following `mkdir` fail. Wait for it to actually go away.
                    WaitUntilGone(path);
                    return;
                }
                catch (Exception e) when (IsTransientIOFailure(e))
                {
                    if (attempt >= DeleteAttempts)
                        throw new EasyShellException(DescribeUndeletableDirectory(path, e));

                    Thread.Sleep(DeleteRetryBaseDelayMilliseconds * (1 << (attempt - 1)));
                }
            }
        }
        /// <summary>
        /// Builds an actionable message naming the files that are actually locked, instead of the
        /// framework's generic "The process cannot access the file because it is being used".
        /// </summary>
        private static string DescribeUndeletableDirectory(string path, Exception cause)
        {
            List<string> locked = [];
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (locked.Count >= 10)
                        break;
                    if (IsFileLocked(file))
                        locked.Add(file);
                }
            }
            catch { /* diagnostics are best-effort */ }

            string detail = locked.Count == 0
                ? "No specific locked file could be identified (the directory itself may be open, e.g. as a shell/Explorer working directory)."
                : $"Locked file(s):{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", locked)}";

            return $"""
                Cannot delete directory '{path}' after {DeleteAttempts} attempts.
                {detail}
                Close anything using this folder - a running build, an open terminal sitting inside it,
                a file explorer window, or a cloud sync client (Dropbox/OneDrive) - then run again.
                ({cause.Message})
                """;
        }
        private static bool IsFileLocked(string file)
        {
            try
            {
                using FileStream _ = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
        private static bool IsTransientIOFailure(Exception e)
            => e is IOException or UnauthorizedAccessException;
        private static void ClearReadOnly(string file)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            catch { /* best-effort */ }
        }
        private static void ClearReadOnlyRecursive(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    ClearReadOnly(file);
            }
            catch { /* best-effort */ }
        }
        private static void WaitUntilGone(string path)
        {
            for (int i = 0; i < 20 && Directory.Exists(path); i++)
                Thread.Sleep(25);
        }
        private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            // Copy files
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            // Copy subdirectories
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, destSubDir);
            }
        }
        #endregion
    }
}
