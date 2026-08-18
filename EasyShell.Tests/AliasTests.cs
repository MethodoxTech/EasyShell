using EasyShell.Commands;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using System;
using System.IO;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// The alias table is the shell's whole vocabulary: every one of these is a short name standing
    /// in for a fully-qualified .NET member, and a typo in that table is a command that silently
    /// becomes an attempt to run a program of the same name instead.
    /// </summary>
    public class AliasTests
    {
        #region Output
        [Fact]
        public void PrintWritesALine()
            => Assert.Equal("hello", ScriptHost.Run("""print "hello" """).FirstLine);

        [Fact]
        public void PrintAcceptsAnyKind()
            => Assert.Equal(["42", "TRUE", "1.5"], ScriptHost.Run("""
                print 42
                print (|| TRUE)
                print 1.5
                """).Lines);

        [Fact]
        public void FormatIsStringFormat()
            => Assert.Equal("x=42", ScriptHost.EvaluateText("""format "x={0}" 42"""));
        #endregion

        #region Working directory
        [Fact]
        public void CwdReportsTheWorkingDirectory()
            => Assert.Equal(Directory.GetCurrentDirectory(), ScriptHost.EvaluateText("cwd"));

        [Fact]
        public void CdChangesIt()
        {
            using TempDirectory temp = new();
            string previous = Directory.GetCurrentDirectory();
            try
            {
                string reported = ScriptHost.EvaluateText($"""
                    cd "{Escape(temp.Root)}"
                    cwd
                    """);

                // Compared by leaf name rather than in full: on macOS the temp folder is reached
                // through a /var -> /private/var symlink and the two spellings never match.
                Assert.Equal(Path.GetFileName(temp.Root), Path.GetFileName(reported));
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
        }

        [Fact]
        public void ResolveMakesAPathAbsolute()
        {
            string resolved = ScriptHost.EvaluateText("""resolve "." """);
            Assert.True(Path.IsPathRooted(resolved));
        }
        #endregion

        #region Paths
        [Fact]
        public void JoinPathAcceptsEitherSeparatorOnEveryPlatform()
        {
            // Path.Join only recognizes '\' on Windows, so a script written as "out\bin" produced a
            // single file literally named "out\bin" on Linux - a wrong path that still looks valid.
            char s = Path.DirectorySeparatorChar;
            Assert.Equal($"a{s}b{s}c{s}d", ScriptHost.EvaluateText("""joinpath "a\\b" "c/d" """));
        }

        [Fact]
        public void ExistsAnswersForFilesAndDirectories()
        {
            using TempDirectory temp = new();
            temp.WriteFile("file.txt");

            Assert.True(ScriptHost.Evaluate($"""exists "{Escape(temp.PathTo("file.txt"))}" """).AsBool());
            Assert.True(ScriptHost.Evaluate($"""exists "{Escape(temp.Root)}" """).AsBool());
            Assert.False(ScriptHost.Evaluate($"""exists "{Escape(temp.PathTo("nope.txt"))}" """).AsBool());
        }
        #endregion

        #region Environment
        [Fact]
        public void SetenvAndGetenvRoundTrip()
        {
            const string name = "EASYSHELL_ALIAS_TEST";
            try
            {
                Assert.Equal("from-script", ScriptHost.EvaluateText($"""
                    setenv "{name}" "from-script"
                    getenv "{name}"
                    """));
                Assert.Equal("from-script", Environment.GetEnvironmentVariable(name));
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
        #endregion

        #region File system
        [Fact]
        public void MkdirRemoveCopyAndMove()
        {
            using TempDirectory temp = new();
            string root = Escape(temp.Root);

            ScriptHost.Run($"""
                mkdir "{root}/made"
                System.IO.File.WriteAllText "{root}/made/one.txt" "content"
                cp "{root}/made/one.txt" "{root}/copied/two.txt"
                mv "{root}/copied/two.txt" "{root}/moved.txt"
                rm "{root}/made/one.txt"
                """);

            Assert.True(Directory.Exists(temp.PathTo("made")));
            Assert.False(File.Exists(temp.PathTo("made", "one.txt")));
            Assert.False(File.Exists(temp.PathTo("copied", "two.txt")));
            Assert.Equal("content", File.ReadAllText(temp.PathTo("moved.txt")));
        }

        [Fact]
        public void RemoveAnswersWhetherThereWasAnythingToRemove()
        {
            using TempDirectory temp = new();
            temp.WriteFile("gone.txt");

            Assert.True(ScriptHost.Evaluate($"""remove "{Escape(temp.PathTo("gone.txt"))}" """).AsBool());
            Assert.False(ScriptHost.Evaluate($"""remove "{Escape(temp.PathTo("gone.txt"))}" """).AsBool());
        }

        [Fact]
        public void RemoveAllCountsWhatItDeleted()
        {
            using TempDirectory temp = new();
            temp.WriteFile("keep.txt");
            temp.WriteFile("notes.xml");
            temp.WriteFile("nested/deep.xml");

            Assert.Equal(2, ScriptHost.Evaluate($"""removeall "{Escape(temp.Root)}" "*.xml" """).AsInt());
            Assert.True(File.Exists(temp.PathTo("keep.txt")));
            Assert.True(Directory.Exists(temp.PathTo("nested")));
        }

        [Fact]
        public void ReplaceAndRegexReplaceRewriteAFileInPlace()
        {
            using TempDirectory temp = new();
            string file = temp.WriteFile("version.txt", "version = 0.1.0 (build 7)");
            string path = Escape(file);

            ScriptHost.Run($"""
                rpl "{path}" "0.1.0" "0.2.0"
                regrpl "{path}" "\(build \d+\)" "(build 8)"
                """);

            Assert.Equal("version = 0.2.0 (build 8)", File.ReadAllText(file));
        }

        [Fact]
        public void ZipProducesAnArchive()
        {
            using TempDirectory temp = new();
            temp.WriteFile("payload/a.txt", "a");
            string archive = temp.PathTo("out.zip");

            ScriptHost.Run($"""zip "{Escape(temp.PathTo("payload"))}" "{Escape(archive)}" """);

            Assert.True(File.Exists(archive));
            using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(archive);
            Assert.Equal("a.txt", Assert.Single(zip.Entries).FullName);
        }
        #endregion

        #region Script arguments
        [Fact]
        public void HasArgAndArgSeeWhatTheScriptWasGiven()
        {
            // Script arguments are per-Runtime state now, so two embedded sessions cannot see
            // each other's flags. (The CommonUtilities statics remain for direct .NET calls.)
            Runtime rt = new() { ScriptArguments = ["--incremental", "release"] };

            Assert.True(ScriptHost.Evaluate("""hasarg "--INCREMENTAL" """, rt).AsBool());   // case-insensitive
            Assert.False(ScriptHost.Evaluate("""hasarg "--nope" """, rt).AsBool());
            Assert.Equal("release", ScriptHost.EvaluateText("arg 1", rt));
            Assert.Equal("", ScriptHost.EvaluateText("arg 99", rt));                        // never an error
        }
        #endregion

        #region Miscellaneous
        [Fact]
        public void SqrtIsMathSqrt()
            => Assert.Equal("4", ScriptHost.EvaluateText("sqrt 16"));

        [Fact]
        public void GetDateHandsBackARealDateTime()
        {
            Value now = ScriptHost.Evaluate("getdate");

            Assert.Equal(ValueKind.Handle, now.Kind);
            Assert.IsType<DateTime>(now.AsHandle());
        }

        [Fact]
        public void AliasesAreCaseInsensitive()
            => Assert.Equal("hello", ScriptHost.Run("""PRINT "hello" """).FirstLine);
        #endregion

        #region Helpers
        /// <summary>Makes a path safe to paste inside a double-quoted script literal.</summary>
        internal static string Escape(string path) => path.Replace("\\", "\\\\");
        #endregion
    }
}
