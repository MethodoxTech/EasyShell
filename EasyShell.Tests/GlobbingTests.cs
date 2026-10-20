using EasyShell.Hosting;
using EasyShell.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// Wildcard expansion: the matcher on its own, expansion against an in-memory filesystem, and the rule for when the language applies it at all - which is the part that could break existing scripts, so it gets the most cases.
    /// </summary>
    public class GlobbingTests
    {
        #region Matching one name
        [Theory]
        [InlineData("*", "cat.txt", true)]
        [InlineData("*.txt", "cat.txt", true)]
        [InlineData("*.txt", "cat.md", false)]
        [InlineData("c*t", "cat", true)]
        [InlineData("c*t", "coat", true)]
        [InlineData("c*t", "cats", false)]
        [InlineData("*a*b*", "xaybz", true)]
        [InlineData("*a*b*", "xbya", false)]
        [InlineData("?at", "cat", true)]
        [InlineData("?at", "at", false)]
        [InlineData("???", "dog", true)]
        [InlineData("[cb]at", "bat", true)]
        [InlineData("[cb]at", "rat", false)]
        [InlineData("[a-c]at", "bat", true)]
        [InlineData("[a-c]at", "rat", false)]
        [InlineData("[!a-c]at", "rat", true)]
        [InlineData("[!a-c]at", "bat", false)]
        [InlineData("[^a-c]at", "rat", true)]
        [InlineData("[]x]", "]", true)]          // ']' first is a member, not the end
        [InlineData("[a-]", "-", true)]          // '-' last is literal
        [InlineData("note[", "note[", true)]     // an unclosed '[' is an ordinary character
        [InlineData("note[1", "note[1", true)]
        [InlineData("Cat", "cat", false)]        // case-sensitive, on every host
        public void MatchesOneName(string pattern, string name, bool expected)
            => Assert.Equal(expected, Globbing.Matches(pattern, name));

        [Theory]
        [InlineData("*", ".profile", false)]
        [InlineData("?profile", ".profile", false)]
        [InlineData("[.]profile", ".profile", false)]
        [InlineData(".*", ".profile", true)]
        [InlineData(".p*", ".profile", true)]
        [InlineData("*e", "profile", true)]
        public void HiddenNamesNeedAPatternThatStartsWithADot(string pattern, string name, bool expected)
            => Assert.Equal(expected, Globbing.Matches(pattern, name));

        [Theory]
        [InlineData("pets/*", true)]
        [InlineData("file?.txt", true)]
        [InlineData("[ab].txt", true)]
        [InlineData("plain.txt", false)]
        [InlineData("note[", false)]
        [InlineData("", false)]
        public void KnowsAPatternWhenItSeesOne(string word, bool expected)
            => Assert.Equal(expected, Globbing.HasPattern(word));
        #endregion

        #region Expanding against a filesystem
        /// <summary>A small home folder: two pets, a hidden file, and documents two levels deep.</summary>
        private static (Runtime Runtime, HostingTests.FakeFileSystem Fs, HostingTests.FakeConsole Console, HostingTests.FakeProcessRunner Procs) Home()
        {
            var (rt, fs, console, env, procs) = HostingTests.CreateWorld();
            env.CurrentDirectory = "/home/user";
            foreach (string dir in new[] { "/home/user/pets", "/home/user/docs/2024", "/home/user/docs/2025", "/home/user/docs/empty" })
                fs.CreateDirectory(dir);
            fs.Files["/home/user/pets/dog.txt"] = "woof";
            fs.Files["/home/user/pets/cat.txt"] = "meow";
            fs.Files["/home/user/pets/.secret"] = "hidden";
            fs.Files["/home/user/docs/2024/notes-a.txt"] = "a";
            fs.Files["/home/user/docs/2025/notes-b.txt"] = "b";
            fs.Files["/home/user/docs/2025/todo.txt"] = "c";
            fs.Files["/home/user/a.txt"] = "1";
            fs.Files["/home/user/b.md"] = "2";
            procs.KnownPrograms.Add("cat");
            procs.KnownPrograms.Add("ls");
            procs.KnownPrograms.Add("echo");
            procs.KnownPrograms.Add("wc");
            return (rt, fs, console, procs);
        }

        private static List<string> Expand(string word) => Globbing.Expand(Home().Fs, word);

        [Fact]
        public void StarListsAFolderSortedAndKeepsTheTypedPrefix()
            => Assert.Equal(["pets/cat.txt", "pets/dog.txt"], Expand("pets/*"));

        [Fact]
        public void HiddenFilesComeOnlyWhenAskedFor()
            => Assert.Equal(["pets/.secret"], Expand("pets/.*"));

        [Fact]
        public void PatternsWorkInAnySegment()
            => Assert.Equal(["docs/2024/notes-a.txt", "docs/2025/notes-b.txt"], Expand("docs/*/notes*.txt"));

        [Fact]
        public void ClassesAndQuestionMarksWorkInAFolderName()
            => Assert.Equal(["docs/2025/todo.txt"], Expand("docs/202[5-9]/t?do.txt"));

        [Fact]
        public void ALiteralTailMustExist()
            => Assert.Equal(["docs/2025/todo.txt"], Expand("docs/*/todo.txt"));

        [Fact]
        public void ATrailingSeparatorAsksForFolders()
            => Assert.Equal(["docs/2024/", "docs/2025/", "docs/empty/"], Expand("docs/*/"));

        [Fact]
        public void AbsolutePatternsStayAbsolute()
            => Assert.Equal(["/home/user/a.txt"], Expand("/home/user/*.txt"));

        [Fact]
        public void TheWorkingDirectoryIsWhereARelativePatternLooks()
            => Assert.Equal(["a.txt", "b.md", "docs", "pets"], Expand("*"));

        [Fact]
        public void NothingMatchedIsAnEmptyAnswer()
        {
            Assert.Empty(Expand("x*"));
            Assert.Empty(Expand("missing/*"));
            Assert.Empty(Expand("pets/*.png"));
        }

        [Fact]
        public void AWordWithoutAPatternIsNeverLookedUp()
            => Assert.Empty(Expand("pets/cat.txt"));

        [Fact]
        public void ABackslashIsACharacterOnAFilesystemThatSaysSo()
        {
            // The fake is '/'-only, as a pocket disk is on every host: a '\' is part of a name there.
            var (_, fs, _, _) = Home();
            fs.Files["/home/user/back\\slash.txt"] = "x";
            Assert.Equal(["back\\slash.txt"], Globbing.Expand(fs, "back\\*"));
        }
        #endregion

        #region When the language expands
        [Fact]
        public void ProgramArgumentsAreExpanded()
        {
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("cat pets/*", "<glob>");
            Assert.Equal("cap:cat pets/cat.txt pets/dog.txt", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void ANonMatchingPatternIsPassedOnAsTyped()
        {
            // POSIX: the program sees the word and says, in its own words, that there is no such file.
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("cat x*", "<glob>");
            Assert.Equal("cap:cat x*", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void QuotedWordsAreNeverExpanded()
        {
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("""echo "*" "pets/*" """, "<glob>");
            Assert.Equal("cap:echo * pets/*", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void VariablesAndExpressionsAreNeverExpanded()
        {
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("""
                $p = "pets/*"
                cat $p
                cat (|| "pets/" "*")
                """, "<glob>");
            Assert.Equal(["cap:cat pets/*", "cap:cat pets/*"], procs.Invocations);
        }

        [Fact]
        public void MultiplicationIsStillMultiplication()
        {
            // Files in the working directory are exactly what would make `*` expand if the rule were wrong; there are four of them here.
            var (rt, _, console, _) = Home();
            new EasyShellEngine(rt).Run("""
                print (* 2 3)
                $n = 4
                $n *= 5
                *= $n 2
                print $n
                print (* 2 (* 3 4))
                """, "<glob>");
            Assert.Equal(["6", "40", "24"], console.Output);
        }

        [Fact]
        public void LanguageCommandsGetTheirWordsVerbatim()
        {
            // print, removeall and the comparisons take values, not file lists. removeall in particular has always taken its pattern unquoted, and must keep meaning the pattern.
            var (rt, fs, console, _) = Home();
            new EasyShellEngine(rt).Run("""
                print *
                print (== * "*")
                $removed = (removeall pets *.txt FALSE)
                print $removed
                """, "<glob>");
            Assert.Equal(["*", "TRUE", "2"], console.Output);
            Assert.False(fs.Files.ContainsKey("/home/user/pets/cat.txt"));
        }

        [Fact]
        public void ThePatternInHeadPositionIsTheCommandName()
        {
            var (rt, _, _, procs) = Home();
            procs.KnownPrograms.Add("c*");
            new EasyShellEngine(rt).Run("c* pets/c*", "<glob>");
            Assert.Equal("cap:c* pets/cat.txt", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void PipelineStagesAreExpanded()
        {
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("cat pets/* | wc", "<glob>");
            Assert.Equal(["pipe:cat pets/cat.txt pets/dog.txt", "pipe:wc"], procs.Invocations);
        }

        [Fact]
        public void RunExpandsItsProgramsArguments()
        {
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("RUN ls *.txt", "<glob>");
            Assert.Equal("cap:ls a.txt", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void ExpressionContextExpandsToo()
        {
            var (rt, _, _, procs) = Home();
            new EasyShellEngine(rt).Run("$x = (ls docs/*/*.txt)", "<glob>");
            Assert.Equal("cap:ls docs/2024/notes-a.txt docs/2025/notes-b.txt docs/2025/todo.txt", Assert.Single(procs.Invocations));
        }
        #endregion

        #region The real filesystem
        [Fact]
        public void TheDefaultHostExpandsAgainstTheRealDisk()
        {
            using TempDirectory temp = new("EasyShellGlob");
            temp.WriteFile("sub/b.txt");
            temp.WriteFile("sub/a.txt");
            temp.WriteFile("sub/c.log");
            temp.WriteFile("sub/.hidden.txt");

            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = temp.Root;
                HostFileSystem fs = new();

                // The separator the user typed is the one they get back, on every platform.
                Assert.Equal(["sub/a.txt", "sub/b.txt"], Globbing.Expand(fs, "sub/*.txt"));

                string absolute = temp.Root + "/sub/?.log";
                Assert.Equal([temp.Root + "/sub/c.log"], Globbing.Expand(fs, absolute));

                // On Windows '\' separates too, because the real filesystem says it does.
                if (OperatingSystem.IsWindows())
                    Assert.Equal(["sub\\a.txt", "sub\\b.txt"], Globbing.Expand(fs, "sub\\*.txt"));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        #endregion
    }
}
