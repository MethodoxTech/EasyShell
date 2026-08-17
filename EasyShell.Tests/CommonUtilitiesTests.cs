using EasyShell.Commands;
using EasyShell.Exceptions;
using EasyShell.Tests.Infrastructure;
using System;
using System.IO;
using Xunit;

namespace EasyShell.Tests
{
    public class CommonUtilitiesTests
    {
        #region Paths
        [Fact]
        public void SeparatorsAreNormalizedToThisPlatforms()
        {
            char s = Path.DirectorySeparatorChar;

            Assert.Equal($"a{s}b{s}c", CommonUtilities.NormalizeSeparators(@"a\b/c"));
            Assert.Equal("", CommonUtilities.NormalizeSeparators(""));
        }

        [Fact]
        public void JoinPathTakesUpToSixParts()
        {
            char s = Path.DirectorySeparatorChar;

            Assert.Equal($"a{s}b", CommonUtilities.JoinPath("a", "b"));
            Assert.Equal($"a{s}b{s}c{s}d{s}e{s}f", CommonUtilities.JoinPath("a", "b", "c", "d", "e", "f"));
        }

        [Fact]
        public void JoinPathSkipsEmptyParts()
        {
            // `(JoinPath $Root $Optional "file.txt")` with an unset middle part has to produce a
            // usable path rather than one with a doubled separator in it.
            char s = Path.DirectorySeparatorChar;
            Assert.Equal($"a{s}c", CommonUtilities.JoinPath("a", "", "c"));
        }

        [Fact]
        public void JoinPathAcceptsAWindowsStyleScriptOnLinux()
        {
            // Path.Join only treats '\' as a separator on Windows, so `"..\..\Publish"` used to
            // become one file name containing backslashes - a wrong path that still looks valid.
            char s = Path.DirectorySeparatorChar;
            Assert.Equal($"..{s}..{s}Publish{s}bin", CommonUtilities.JoinPath(@"..\..\Publish", "bin"));
        }
        #endregion

        #region Existence and removal
        [Fact]
        public void ExistsCoversFilesDirectoriesAndNothing()
        {
            using TempDirectory temp = new();
            temp.WriteFile("a.txt");

            Assert.True(CommonUtilities.Exists(temp.PathTo("a.txt")));
            Assert.True(CommonUtilities.Exists(temp.Root));
            Assert.False(CommonUtilities.Exists(temp.PathTo("b.txt")));
            Assert.False(CommonUtilities.Exists(""));
            Assert.False(CommonUtilities.Exists("   "));
        }

        [Fact]
        public void ExistsExpandsEnvironmentVariables()
        {
            using TempDirectory temp = new();
            temp.WriteFile("a.txt");
            const string name = "EASYSHELL_TEST_ROOT";
            try
            {
                // %NAME% is expanded on every platform, which is what lets one script read
                // %USERPROFILE% or %HOME% without the shell needing its own substitution syntax.
                Environment.SetEnvironmentVariable(name, temp.Root);
                Assert.True(CommonUtilities.Exists($"%{name}%/a.txt"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [Fact]
        public void RemoveDeletesFilesAndWholeTrees()
        {
            using TempDirectory temp = new();
            temp.WriteFile("tree/nested/deep.txt");

            Assert.True(CommonUtilities.Remove(temp.PathTo("tree", "nested", "deep.txt")));
            Assert.True(CommonUtilities.Remove(temp.PathTo("tree")));
            Assert.False(Directory.Exists(temp.PathTo("tree")));
        }

        [Fact]
        public void RemovingSomethingThatIsNotThereIsNotAnError()
        {
            using TempDirectory temp = new();
            Assert.False(CommonUtilities.Remove(temp.PathTo("nothing")));
            Assert.False(CommonUtilities.Remove(""));
        }

        [Fact]
        public void AReadOnlyFileIsStillRemoved()
        {
            // Build outputs arrive read-only often enough - from a NuGet cache, from source control
            // - that failing on one would make cleanup unusable.
            using TempDirectory temp = new();
            string file = temp.WriteFile("locked.txt");
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

            Assert.True(CommonUtilities.Remove(file));
            Assert.False(File.Exists(file));
        }

        [Fact]
        public void RemoveAllIsRecursiveByDefaultAndCountsWhatItDid()
        {
            using TempDirectory temp = new();
            temp.WriteFile("a.xml");
            temp.WriteFile("keep.txt");
            temp.WriteFile("nested/b.xml");

            Assert.Equal(2, CommonUtilities.RemoveAll(temp.Root, "*.xml"));
            Assert.True(File.Exists(temp.PathTo("keep.txt")));
        }

        [Fact]
        public void RemoveAllCanStayInTheTopFolder()
        {
            using TempDirectory temp = new();
            temp.WriteFile("a.log");
            temp.WriteFile("nested/b.log");

            Assert.Equal(1, CommonUtilities.RemoveAll(temp.Root, "*.log", recursive: false));
            Assert.True(File.Exists(temp.PathTo("nested", "b.log")));
        }

        [Fact]
        public void RemoveAllNeverTakesADirectoryWithIt()
        {
            // A pattern of "*" must not quietly delete folders; only files are matched.
            using TempDirectory temp = new();
            temp.WriteFile("a.txt");
            temp.CreateDirectory("subfolder");

            CommonUtilities.RemoveAll(temp.Root, "*");

            Assert.True(Directory.Exists(temp.PathTo("subfolder")));
        }

        [Fact]
        public void RemoveAllOnAMissingFolderDeletesNothing()
        {
            using TempDirectory temp = new();

            Assert.Equal(0, CommonUtilities.RemoveAll(temp.PathTo("no-such-folder"), "*.xml"));
            Assert.Equal(0, CommonUtilities.RemoveAll(temp.Root, ""));
            Assert.Equal(0, CommonUtilities.RemoveAll("", "*"));
        }
        #endregion

        #region Copy and move
        [Fact]
        public void CopyCreatesTheTargetFolder()
        {
            using TempDirectory temp = new();
            temp.WriteFile("source.txt", "payload");

            Assert.True(CommonUtilities.Copy(temp.PathTo("source.txt"), temp.PathTo("new", "folder", "target.txt")));
            Assert.Equal("payload", File.ReadAllText(temp.PathTo("new", "folder", "target.txt")));
        }

        [Fact]
        public void CopyOverwritesAndRecursesIntoDirectories()
        {
            using TempDirectory temp = new();
            temp.WriteFile("tree/a.txt", "a");
            temp.WriteFile("tree/nested/b.txt", "b");
            temp.WriteFile("destination/a.txt", "stale");

            Assert.True(CommonUtilities.Copy(temp.PathTo("tree"), temp.PathTo("destination")));

            Assert.Equal("a", File.ReadAllText(temp.PathTo("destination", "a.txt")));
            Assert.Equal("b", File.ReadAllText(temp.PathTo("destination", "nested", "b.txt")));
        }

        [Fact]
        public void MoveReplacesWhateverWasThere()
        {
            using TempDirectory temp = new();
            temp.WriteFile("from.txt", "new");
            temp.WriteFile("to.txt", "old");

            Assert.True(CommonUtilities.Move(temp.PathTo("from.txt"), temp.PathTo("to.txt")));

            Assert.Equal("new", File.ReadAllText(temp.PathTo("to.txt")));
            Assert.False(File.Exists(temp.PathTo("from.txt")));
        }

        [Fact]
        public void MoveHandlesDirectories()
        {
            using TempDirectory temp = new();
            temp.WriteFile("tree/a.txt", "a");

            Assert.True(CommonUtilities.Move(temp.PathTo("tree"), temp.PathTo("moved", "tree")));

            Assert.False(Directory.Exists(temp.PathTo("tree")));
            Assert.Equal("a", File.ReadAllText(temp.PathTo("moved", "tree", "a.txt")));
        }

        [Fact]
        public void CopyingOrMovingSomethingThatIsNotThereAnswersFalse()
        {
            using TempDirectory temp = new();

            Assert.False(CommonUtilities.Copy(temp.PathTo("nope"), temp.PathTo("target")));
            Assert.False(CommonUtilities.Move(temp.PathTo("nope"), temp.PathTo("target")));
            Assert.False(CommonUtilities.Copy("", "target"));
        }
        #endregion

        #region Text rewriting
        [Fact]
        public void ReplaceRewritesEveryOccurrence()
        {
            using TempDirectory temp = new();
            string file = temp.WriteFile("f.txt", "one two one");

            CommonUtilities.Replace(file, "one", "1");

            Assert.Equal("1 two 1", File.ReadAllText(file));
        }

        [Fact]
        public void RegexReplaceUsesGroups()
        {
            using TempDirectory temp = new();
            string file = temp.WriteFile("f.txt", "version 1.2.3");

            CommonUtilities.RegexReplace(file, @"(\d+)\.(\d+)\.(\d+)", "$1.$2.99");

            Assert.Equal("version 1.2.99", File.ReadAllText(file));
        }

        [Fact]
        public void RewritingAMissingFileIsAScriptError()
        {
            using TempDirectory temp = new();

            Assert.Contains("doesn't exist", Assert.Throws<EasyShellException>(
                () => CommonUtilities.Replace(temp.PathTo("nope.txt"), "a", "b")).Message);
            Assert.Throws<EasyShellException>(
                () => CommonUtilities.RegexReplace(temp.PathTo("nope.txt"), "a", "b"));
        }
        #endregion

        #region Script arguments
        [Fact]
        public void ArgumentsAreCaseInsensitiveAndOutOfRangeIsEmpty()
        {
            try
            {
                CommonUtilities.SetScriptArguments(["--Incremental", "value"]);

                Assert.True(CommonUtilities.HasArgument("--incremental"));
                Assert.False(CommonUtilities.HasArgument("--other"));
                Assert.False(CommonUtilities.HasArgument(""));
                Assert.Equal("--Incremental", CommonUtilities.Argument(0));
                Assert.Equal("", CommonUtilities.Argument(2));
                Assert.Equal("", CommonUtilities.Argument(-1));
            }
            finally
            {
                CommonUtilities.SetScriptArguments([]);
            }
        }

        [Fact]
        public void NoArgumentsIsAnEmptyList()
        {
            CommonUtilities.SetScriptArguments(null!);

            Assert.False(CommonUtilities.HasArgument("--anything"));
            Assert.Equal("", CommonUtilities.Argument(0));
        }
        #endregion
    }
}
