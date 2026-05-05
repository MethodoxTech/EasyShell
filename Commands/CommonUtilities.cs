using System;
using System.IO;
using System.Text.RegularExpressions;

namespace EasyShell.Commands
{
    public static class CommonUtilities
    {
        #region File System
        /// <summary>
        /// EXISTS <path>  -> true if file OR directory exists
        /// </summary>
        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = Environment.ExpandEnvironmentVariables(path);

            return File.Exists(path) || Directory.Exists(path);
        }
        /// <summary>
        /// REMOVE <path>  -> true if file OR directory exists and is removed
        /// </summary>
        public static bool Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = Environment.ExpandEnvironmentVariables(path);

            // File
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            // Directory
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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

            source = Environment.ExpandEnvironmentVariables(source);
            target = Environment.ExpandEnvironmentVariables(target);

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

            source = Environment.ExpandEnvironmentVariables(source);
            target = Environment.ExpandEnvironmentVariables(target);

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
            if (!File.Exists(path))
                throw new ArgumentException($"File {path} doesn't exist.");

            string content = File.ReadAllText(path);
            string replace = content.Replace(source, replacement);
            File.WriteAllText(path, replace);
        }
        public static void RegexReplace(string path, string pattern, string replacement)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"File {path} doesn't exist.");

            string content = File.ReadAllText(path);
            string replace = Regex.Replace(content, pattern, replacement);
            File.WriteAllText(path, replace);
        }
        #endregion

        #region Routines
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
