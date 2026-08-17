using System;
using System.IO;

namespace EasyShell.Tests.Infrastructure
{
    /// <summary>
    /// A scratch folder that cleans up after itself, so the file-system commands can be tested
    /// against real files without any test being able to touch anything of the user's.
    /// </summary>
    public sealed class TempDirectory : IDisposable
    {
        public string Root { get; }

        public TempDirectory(string label = "EasyShellTests")
        {
            Root = Path.Combine(Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string PathTo(params string[] parts)
        {
            string result = Root;
            foreach (string part in parts)
                result = Path.Combine(result, part);
            return result;
        }

        /// <summary>Writes a file, creating any intermediate folders. Returns the full path.</summary>
        public string WriteFile(string relativePath, string contents = "x")
        {
            string full = PathTo(relativePath.Split('/'));
            string? parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(full, contents);
            return full;
        }

        public string CreateDirectory(string relativePath)
        {
            string full = PathTo(relativePath.Split('/'));
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            // Best-effort: a test that already deleted the folder is the normal case here, and a
            // failed cleanup must never be the thing that fails a run.
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }
}
