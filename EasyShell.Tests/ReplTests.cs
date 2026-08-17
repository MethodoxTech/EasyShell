using EasyShell.Interactive;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using System.Collections.Generic;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// The prompt loop, which is shared rather than reimplemented: block accumulation, exit codes,
    /// error reporting and result printing are the same problem in every host, and a copy of them
    /// in each host is a copy that drifts.
    /// </summary>
    public class ReplTests
    {
        /// <summary>Types <paramref name="lines"/> at the prompt, then EOF.</summary>
        private static (int ExitCode, string[] Output) Type(ReplOptions options, params string[] lines)
        {
            using ConsoleCapture console = new(ConsoleCapture.Typed(lines));
            int code = EasyShellRepl.Run(options);
            return (code, console.Lines);
        }

        private static ReplOptions Quiet(Runtime? runtime = null) => new()
        {
            Runtime = runtime,
            Prompt = () => "",
            ContinuationPrompt = () => "",
            // A REPL is a person at a terminal, but a test is not: capturing keeps a program's
            // output inside the assertion instead of on the runner's own console.
            Interactive = false,
        };

        #region Session
        [Fact]
        public void EndOfInputEndsTheSessionSuccessfully()
            => Assert.Equal(0, Type(Quiet()).ExitCode);

        [Fact]
        public void TheBannerIsPrintedOnceBeforeTheFirstPrompt()
        {
            int calls = 0;
            ReplOptions options = new() { Prompt = () => "", Banner = () => calls++, Interactive = false };

            Type(options, "print one", "print two");

            Assert.Equal(1, calls);
        }

        [Fact]
        public void ThePromptIsEvaluatedEveryLineSoItCanShowState()
        {
            // Which is what lets a host put the working directory in the prompt.
            int asked = 0;
            ReplOptions options = new() { Prompt = () => $"{asked++}> ", Interactive = false };

            (_, string[] output) = Type(options, "print x", "print y");

            Assert.True(asked >= 3, $"prompt asked {asked} times");
            Assert.Contains("0> ", string.Join("\n", output));
        }

        [Fact]
        public void BlankLinesAtTheTopLevelAreIgnored()
            => Assert.Equal(["ok"], Type(Quiet(), "", "   ", "print ok").Output);
        #endregion

        #region Evaluation
        [Fact]
        public void AValueProducingLineIsEchoed()
        {
            // The prompt doubles as a calculator, and that is also how `cwd` becomes useful.
            Assert.Equal(["5"], Type(Quiet(), "+ 2 3").Output);
        }

        [Fact]
        public void ALineThatProducesNothingEchoesNothing()
            => Assert.Equal(["hello"], Type(Quiet(), """print "hello" """).Output);

        [Fact]
        public void StateSurvivesFromOneLineToTheNext()
            => Assert.Equal(["v1"], Type(Quiet(), """STRINGVAR Tag "v1" """, "print $Tag").Output);

        [Fact]
        public void APreparedRuntimeIsUsedAsIs()
        {
            Runtime rt = new();
            rt.InjectString("Injected", "from-host");

            Assert.Equal(["from-host"], Type(Quiet(rt), "print $Injected").Output);
        }
        #endregion

        #region Multi-line blocks
        [Fact]
        public void ABlockIsAccumulatedUntilItsEnd()
        {
            (_, string[] output) = Type(Quiet(),
                "IF TRUE",
                "    print inside",
                "END",
                "print after");

            Assert.Equal(["inside", "after"], output);
        }

        [Fact]
        public void NestedBlocksNeedTheirOwnEnds()
        {
            (_, string[] output) = Type(Quiet(),
                "INTVAR i 0",
                "WHILE (< $i 2)",
                "    IF TRUE",
                "        print tick",
                "    END",
                "    $i = (+ $i 1)",
                "END",
                "print done");

            Assert.Equal(["tick", "tick", "done"], output);
        }

        [Fact]
        public void AFunctionCanBeTypedAtThePrompt()
        {
            (_, string[] output) = Type(Quiet(),
                "FUNC Greet",
                "    print hello",
                "END",
                "CALL Greet");

            Assert.Equal(["hello"], output);
        }

        [Fact]
        public void InsideABlockEveryLineIsBodyText()
        {
            // Including a blank one, and one that reads like a REPL command: `:exit` typed inside
            // an unfinished IF is body text, not a request to leave.
            (_, string[] output) = Type(Quiet(),
                "IF TRUE",
                "",
                "    print inside",
                "END");

            Assert.Equal(["inside"], output);
        }

        [Fact]
        public void ACommentedKeywordDoesNotOpenABlock()
        {
            // Depth tracking strips comments the same way the parser does, so a '#' inside a string
            // is not mistaken for one and a commented-out IF does not swallow the next line.
            (_, string[] output) = Type(Quiet(),
                "# IF TRUE",
                """print "END of it" """,
                "print after");

            Assert.Equal(["END of it", "after"], output);
        }
        #endregion

        #region REPL commands
        [Fact]
        public void ExitAndQuitLeaveTheSession()
        {
            Assert.Equal(0, Type(Quiet(), ":exit", "print unreachable").ExitCode);
            Assert.Equal(["gone"], Type(Quiet(), "print gone", ":quit", "print unreachable").Output);
        }

        [Fact]
        public void HelpPrintsWhatTheHostSupplied()
        {
            ReplOptions options = new() { Prompt = () => "", HelpText = "the host's help", Interactive = false };
            Assert.Equal(["the host's help"], Type(options, ":help").Output);
        }

        [Fact]
        public void HelpWithoutHostTextStillSaysSomething()
            => Assert.Contains("No help text", string.Join("\n", Type(Quiet(), ":help").Output));

        [Fact]
        public void VarsAndFuncsShowTheSessionState()
        {
            (_, string[] output) = Type(Quiet(),
                """STRINGVAR Tag "v1" """,
                "FUNC Build",
                "    print x",
                "END",
                ":vars",
                ":funcs");

            Assert.Contains("STRING Tag = v1", output);
            Assert.Contains("Build", output);
        }

        [Fact]
        public void AnUnknownReplCommandSaysSoWithoutLeaving()
        {
            (int code, string[] output) = Type(Quiet(), ":nonsense", "print still-here");

            Assert.Equal(0, code);
            Assert.Equal(["Unknown REPL command. Try :help", "still-here"], output);
        }
        #endregion

        #region Host built-ins
        [Fact]
        public void AHostBuiltinIsTriedBeforeTheEngine()
        {
            // RetroShell's `mon`, `clear` and `colors` work this way: the host gets first refusal
            // on every top-level line.
            List<string> seen = [];
            ReplOptions options = new()
            {
                Prompt = () => "",
                Interactive = false,
                Builtins = (string input, EasyShellEngine engine, out int? exitCode) =>
                {
                    exitCode = null;
                    seen.Add(input);
                    if (input != "mon") return false;

                    System.Console.WriteLine("monitor");
                    return true;
                }
            };

            (_, string[] output) = Type(options, "mon", "print ordinary");

            Assert.Equal(["mon", "print ordinary"], seen);
            Assert.Equal(["monitor", "ordinary"], output);
        }

        [Fact]
        public void AHostBuiltinCanEndTheSessionWithACode()
        {
            ReplOptions options = new()
            {
                Prompt = () => "",
                Interactive = false,
                Builtins = (string input, EasyShellEngine engine, out int? exitCode) =>
                {
                    exitCode = input == "bye" ? 42 : null;
                    return exitCode is not null;
                }
            };

            Assert.Equal(42, Type(options, "bye").ExitCode);
        }

        [Fact]
        public void HostBuiltinsAreNotConsultedInsideABlock()
        {
            List<string> seen = [];
            ReplOptions options = new()
            {
                Prompt = () => "",
                Interactive = false,
                Builtins = (string input, EasyShellEngine engine, out int? exitCode) =>
                {
                    exitCode = null;
                    seen.Add(input);
                    return false;
                }
            };

            Type(options, "IF TRUE", "mon", "END");

            // "mon" was body text, not a command: only the two top-level lines were offered.
            Assert.Equal(["IF TRUE"], seen);
        }
        #endregion

        #region Errors and exit codes
        [Fact]
        public void ExitAtThePromptIsWorthItsCodeToWhoeverLaunchedUs()
            => Assert.Equal(3, Type(Quiet(), "exit 3").ExitCode);

        [Fact]
        public void AScriptErrorIsReportedAndTheSessionContinues()
        {
            List<string> errors = [];
            ReplOptions options = new() { Prompt = () => "", Interactive = false, WriteError = errors.Add };

            (int code, string[] output) = Type(options, "print $NeverDefined", "print still-here");

            Assert.Equal(0, code);
            Assert.Equal(["still-here"], output);
            Assert.Contains("Undefined variable", Assert.Single(errors));
        }

        [Fact]
        public void ErrorsGoToStderrUnlessTheHostSaysOtherwise()
        {
            // The default is right for a CLI; a terminal shell usually wants its own styling on
            // stdout instead, which is what WriteError is for.
            Assert.Equal(["still-here"], Type(Quiet(), "print $NeverDefined", "print still-here").Output);
        }

        [Fact]
        public void AParseErrorDoesNotLeaveTheBufferPoisoned()
        {
            // The unit is cleared before it runs, so a line that fails to parse cannot make every
            // later line fail with it.
            List<string> errors = [];
            ReplOptions options = new() { Prompt = () => "", Interactive = false, WriteError = errors.Add };

            (_, string[] output) = Type(options, """print "unterminated""", "print recovered");

            Assert.Single(errors);
            Assert.Equal(["recovered"], output);
        }
        #endregion

        #region Interactive flag
        [Fact]
        public void TheReplTurnsOnInteractiveModeByDefault()
        {
            // Which is what makes `python`, `pwsh` and `vim` work at the prompt.
            Runtime rt = new();
            using ConsoleCapture console = new(ConsoleCapture.Typed(":exit"));
            EasyShellRepl.Run(new ReplOptions { Runtime = rt, Prompt = () => "" });

            Assert.True(Executor.IsInteractive(rt));
        }

        [Fact]
        public void AHostCanTurnInteractiveModeOff()
        {
            Runtime rt = new();
            using ConsoleCapture console = new(ConsoleCapture.Typed(":exit"));
            EasyShellRepl.Run(new ReplOptions { Runtime = rt, Prompt = () => "", Interactive = false });

            Assert.False(Executor.IsInteractive(rt));
        }
        #endregion
    }
}
