using System.Collections.Generic;

namespace EasyShell.Hosting
{
    /// <summary>
    /// Every file-system operation the language needs, as one seam.
    ///
    /// The default implementation passes through to System.IO. The reason this interface exists is
    /// the virtual case: a shell running INSIDE a virtual machine, where "/home/user/notes.txt"
    /// must resolve inside a portable image file rather than on the real disk. Everything the
    /// built-ins touch goes through here, so swapping this swaps the world the script sees.
    ///
    /// Path arithmetic (Combine/GetDirectoryName/...) is part of the interface on purpose: the
    /// host CLR's System.IO.Path follows the HOST platform's separator conventions, which are
    /// wrong for a virtual filesystem with its own ('/', say) - path math must come from the same
    /// place the paths do.
    /// </summary>
    public interface IShellFileSystem
    {
        // ------------------------------------------------------------------ path arithmetic

        /// <summary>
        /// Fold a caller-supplied path into this filesystem's separator convention. The default
        /// host maps both '/' and '\' onto the real platform separator, so a script written with
        /// Windows separators works on Linux and vice-versa. A virtual filesystem with its OWN
        /// separator ('/' always) returns the path unchanged - the whole point of a portable
        /// image is that host conventions never touch its paths, so this must NOT rewrite '/' to
        /// '\' just because the code happens to be running on Windows.
        /// </summary>
        string NormalizeSeparators(string path);

        string GetFullPath(string path);
        string Combine(string a, string b);
        string? GetDirectoryName(string path);
        string GetFileName(string path);
        string GetRelativePath(string relativeTo, string path);

        // ------------------------------------------------------------------ queries
        bool FileExists(string path);
        bool DirectoryExists(string path);
        IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive);
        IEnumerable<string> EnumerateDirectories(string directory);

        // ------------------------------------------------------------------ content
        string ReadAllText(string path);
        void WriteAllText(string path, string content);

        // ------------------------------------------------------------------ mutation
        void CreateDirectory(string path);
        void DeleteFile(string path);
        /// <summary>Delete a directory recursively.</summary>
        void DeleteDirectory(string path);
        void CopyFile(string source, string target);
        void MoveFile(string source, string target);
        void MoveDirectory(string source, string target);
    }
}
