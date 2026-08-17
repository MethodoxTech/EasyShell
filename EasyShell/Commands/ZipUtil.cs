using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace EasyShell.Commands
{
    public static class ZipUtil
    {
        #region Methods
        public static void CompressArchive(string path, string destinationZip)
            => CompressArchive(path, destinationZip, true);
        public static void CompressArchive(string path, string destinationZip, bool overwrite, CompressionLevel level = CompressionLevel.Optimal)
        {
            if (string.IsNullOrWhiteSpace(path)) 
                throw new ArgumentException("path is required.", nameof(path));
            if (string.IsNullOrWhiteSpace(destinationZip)) 
                throw new ArgumentException("destinationZip is required.", nameof(destinationZip));

            destinationZip = Path.GetFullPath(destinationZip);
            string? destDir = Path.GetDirectoryName(destinationZip);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            if (overwrite && File.Exists(destinationZip))
                File.Delete(destinationZip);

            if (Directory.Exists(path))
            {
                string dir = Path.GetFullPath(path);
                ZipFile.CreateFromDirectory(dir, destinationZip, level, includeBaseDirectory: false);
                return;
            }

            // Wildcards or single file
            List<string> expanded = ExpandPath(path).ToList();
            if (expanded.Count == 0)
                throw new FileNotFoundException($"No files matched path: {path}");

            using FileStream fs = new(destinationZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using ZipArchive zip = new(fs, ZipArchiveMode.Create);

            string? baseDir = GetWildcardBaseDirectory(path);

            foreach (string? file in expanded)
            {
                string full = Path.GetFullPath(file);
                string entryName = baseDir is null
                    ? Path.GetFileName(full)
                    : Path.GetRelativePath(baseDir, full).Replace('\\', '/');

                zip.CreateEntryFromFile(full, entryName, level);
            }
        }
        public static void CompressArchiveDirectory(string path, string destinationZip, bool overwrite = true, CompressionLevel level = CompressionLevel.Optimal)
        {
            if (string.IsNullOrWhiteSpace(path)) 
                throw new ArgumentException("path is required.", nameof(path));
            if (string.IsNullOrWhiteSpace(destinationZip)) 
                throw new ArgumentException("destinationZip is required.", nameof(destinationZip));

            path = Path.GetFullPath(path);
            destinationZip = Path.GetFullPath(destinationZip);

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Directory not found: {path}");

            string? destDir = Path.GetDirectoryName(destinationZip);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            if (overwrite && File.Exists(destinationZip))
                File.Delete(destinationZip);

            // Includes the *contents* of 'path' at the zip root.
            ZipFile.CreateFromDirectory(path, destinationZip, level, includeBaseDirectory: false);
        }
        #endregion

        #region Helpers
        private static IEnumerable<string> ExpandPath(string path)
        {
            // No wildcards: treat as single file
            if (!path.Contains('*') && !path.Contains('?'))
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"File not found: {path}");
                yield return path;
                yield break;
            }

            string baseDir = GetWildcardBaseDirectory(path) ?? Directory.GetCurrentDirectory();
            string pattern = Path.GetFileName(path);

            foreach (string f in Directory.EnumerateFiles(baseDir, pattern, SearchOption.TopDirectoryOnly))
                yield return f;
        }
        private static string? GetWildcardBaseDirectory(string path)
        {
            if (!path.Contains('*') && !path.Contains('?'))
                return null;

            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return Directory.GetCurrentDirectory();

            return Path.GetFullPath(dir);
        }
        #endregion
    }
}
