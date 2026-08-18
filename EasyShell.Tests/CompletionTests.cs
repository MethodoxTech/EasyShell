using EasyShell.Hosting;
using EasyShell.Interactive;
using EasyShell.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// What Tab offers at the `easy` prompt.
    ///
    /// <para>Testable without a terminal on purpose: the editor needs keys and a tty, but the
    /// question worth protecting - "given this line and this caret, what could the word become" -
    /// is a pure one, and <see cref="ShellCompletionSource"/> keeps it that way.</para>
    /// </summary>
    public class CompletionTests : IDisposable
    {
        // Several of these tests stand the runtime in a folder of their own, and the working
        // directory is process-global - see AssemblyInfo. Putting it back is what keeps the next
        // test, and the runner itself, where they were.
        private readonly string _originalDirectory = Environment.CurrentDirectory;
        public void Dispose() => Environment.CurrentDirectory = _originalDirectory;

        /// <summary>Completes the word ending at the end of <paramref name="line"/>, which is where the caret is while typing.</summary>
        private static IReadOnlyList<string> Complete(Runtime runtime, string line)
            => new ShellCompletionSource(runtime).Complete(line, line.Length);

        /// <summary>A runtime whose working directory is a folder this test owns.</summary>
        private static Runtime In(TempDirectory directory)
        {
            Runtime runtime = new();
            runtime.Host = ShellHost.Default;
            Environment.CurrentDirectory = directory.Root;
            return runtime;
        }

        #region Commands
        [Fact]
        public void ABuiltinCompletesFromItsPrefix()
        {
            Assert.Contains("print", Complete(new Runtime(), "pri"));
        }

        [Fact]
        public void TheLanguageIsCaseInsensitiveAndSoIsCompletingIt()
        {
            // WHILE and while are the same command, so typing either has to reach it.
            Assert.Contains("WHILE", Complete(new Runtime(), "whi"));
        }

        [Fact]
        public void BlockKeywordsAreOfferedBecauseTheyAreTypedAtThePromptToo()
        {
            Assert.Contains("ELSEIF", Complete(new Runtime(), "ELSE"));
        }

        [Fact]
        public void AnUnknownPrefixOffersNothingRatherThanEverything()
        {
            using TempDirectory directory = new();
            Runtime runtime = In(directory);

            Assert.Empty(Complete(runtime, "zzzznosuchthing"));
        }
        #endregion

        #region Variables
        [Fact]
        public void ADollarWordCompletesToAVariable()
        {
            Runtime runtime = new();
            runtime.InjectString("$EasyScriptRoot", "/somewhere");

            Assert.Contains("$EasyScriptRoot", Complete(runtime, "print $EasyScr"));
        }

        [Fact]
        public void ADollarWordOffersVariablesAndNothingElse()
        {
            Runtime runtime = new();
            runtime.InjectInt("$printer", 1);

            // 'print' the built-in starts with the same letters; '$' rules it out.
            Assert.Equal(["$printer"], Complete(runtime, "$print"));
        }

        [Fact]
        public void VariableCompletionIsCaseInsensitiveLikeTheVariablesThemselves()
        {
            Runtime runtime = new();
            runtime.InjectBool("$IsWindows", true);

            Assert.Contains("$IsWindows", Complete(runtime, "$iswin"));
        }
        #endregion

        #region Files
        [Fact]
        public void AFileInTheWorkingDirectoryCompletes()
        {
            using TempDirectory directory = new();
            directory.WriteFile("Publish.easy");
            Runtime runtime = In(directory);

            Assert.Contains("Publish.easy", Complete(runtime, "easy Pub"));
        }

        [Fact]
        public void ADirectoryKeepsATrailingSeparatorSoTheNextTabDescends()
        {
            using TempDirectory directory = new();
            directory.CreateDirectory("Scripts");
            Runtime runtime = In(directory);

            Assert.Contains("Scripts" + Path.DirectorySeparatorChar, Complete(runtime, "Scri"));
        }

        [Fact]
        public void CompletingInsideADirectoryReplacesTheWholeWordNotJustTheName()
        {
            // The editor substitutes the candidate for the word under the caret, so a candidate
            // that dropped the directory half would turn `Scripts/Pub` into `Publish.easy`.
            using TempDirectory directory = new();
            directory.WriteFile("Scripts/Publish.easy");
            Runtime runtime = In(directory);

            Assert.Contains("Scripts/Publish.easy", Complete(runtime, "easy Scripts/Pub"));
        }

        [Fact]
        public void AWordWithADirectoryInItIsAPathAndNotACommand()
        {
            using TempDirectory directory = new();
            directory.CreateDirectory("bin");
            Runtime runtime = In(directory);

            // `bin/pri` must not offer the built-in `print`.
            Assert.Empty(Complete(runtime, "bin/pri"));
        }

        [Fact]
        public void AnEmptyWordListsTheWorkingDirectoryRatherThanEveryCommandOnTheMachine()
        {
            using TempDirectory directory = new();
            directory.WriteFile("only.txt");
            Runtime runtime = In(directory);

            // Offering every built-in and every program on PATH here would print several thousand
            // lines; the folder the user is standing in is the useful answer.
            Assert.Equal(["only.txt"], Complete(runtime, "easy "));
        }
        #endregion

        #region Programs on PATH
        [Fact]
        public void AProgramOnPathCompletes()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-complete-me", "hi");

            Assert.Contains("es-complete-me", Complete(new Runtime(), "es-complete"));
        }

        [Fact]
        public void AProgramIsOfferedUnderTheNameYouWouldType()
        {
            // On Windows the file on disk is `.cmd` and the name typed is not - PATHEXT is exactly
            // the difference, and completing to `es-complete-ext.cmd` would be completing to a
            // spelling the shell never needs.
            using ProgramProbe probe = new();
            probe.Echo("es-complete-ext", "hi");

            Assert.Contains("es-complete-ext", Complete(new Runtime(), "es-complete-ext"));
        }
        #endregion

        #region Host wiring
        [Fact]
        public void TheProcessConsoleReportsItselfNonInteractiveWhenTheStreamsAreRedirected()
        {
            // The REPL asks this before it hands the line over to the editor. Under a test runner
            // the streams are redirected, so the whole-line path is the correct answer - if this
            // ever flips, every REPL test would block on a key that is never coming, and this is
            // the test that says so first.
            Assert.False(new HostConsole().IsInteractive);
        }

        [Fact]
        public void AHostThatOnlyEverRunsOnATerminalNeedNotAnswerIsInteractive()
        {
            // The default member is reachable only through the interface, which is the point:
            // a host opts out by declaring the property, and says nothing to opt in.
            Assert.True(((IShellLineInput)new AlwaysTerminal()).IsInteractive);
        }

        private sealed class AlwaysTerminal : IShellLineInput
        {
            public EditorKeyPress? ReadKey() => null;
            public void SetRawMode(bool raw) { }
        }
        #endregion
    }
}
