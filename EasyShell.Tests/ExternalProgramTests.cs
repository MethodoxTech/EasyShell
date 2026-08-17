using EasyShell.Exceptions;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// How a program on PATH becomes a command, and the two contexts it can appear in. The
    /// distinction is a semantic requirement, not a preference: an expression must capture, because
    /// the text is the value, and a statement at a prompt must not, because the terminal belongs to
    /// the child.
    /// </summary>
    public class ExternalProgramTests
    {
        #region Statement and expression
        [Fact]
        public void AProgramAsAStatementStreamsItsOutputExactlyOnce()
        {
            // Printing the captured text as well as streaming it showed everything twice.
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-echo", "from-program");

            Assert.Equal(["from-program"], ScriptHost.Run("es-cmd-echo").Lines);
        }

        [Fact]
        public void AProgramAsAnExpressionBecomesItsText()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-capture", "captured");

            ScriptResult result = ScriptHost.Run("""
                $Text = (es-cmd-capture)
                print (|| "[" $Text "]")
                """);

            Assert.Equal(["[captured]"], result.Lines);
        }

        [Fact]
        public void ArgumentsReachTheProgram()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-args", "banner");

            Assert.Equal(["banner", "--flag", "a value"],
                ScriptHost.Run("""es-cmd-args --flag "a value" """).Lines);
        }
        #endregion

        #region Exit codes
        [Fact]
        public void ANonZeroExitCodeAbortsTheScriptAndSaysHowToNotAbort()
        {
            using ProgramProbe probe = new();
            probe.Fail("es-cmd-fail", 3);

            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                print before
                es-cmd-fail
                print after
                """));

            Assert.Contains("2:", e.Message);
            Assert.Contains("exited with code 3", e.Message);
            Assert.Contains(Executor.ContinueOnErrorVariable, e.Message);
        }

        [Fact]
        public void ContinueOnErrorTurnsAFailureIntoAVariable()
        {
            using ProgramProbe probe = new();
            probe.Fail("es-cmd-soft-fail", 4);

            Runtime rt = new();
            ScriptResult result = ScriptHost.Run($"""
                ${Executor.ContinueOnErrorVariable} = TRUE
                es-cmd-soft-fail
                print (|| "code=" ${Executor.LastExitCodeVariable})
                """, rt);

            Assert.Contains("code=4", result.Output);
            Assert.Equal(4, rt.GetVar(Executor.LastExitCodeVariable).Value.AsInt());
        }

        [Fact]
        public void ASuccessfulProgramAlsoRecordsItsExitCode()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-fine", "ok");

            Runtime rt = new();
            ScriptHost.Run("es-cmd-fine", rt);

            Assert.Equal(0, rt.GetVar(Executor.LastExitCodeVariable).Value.AsInt());
        }
        #endregion

        #region Timeout
        [Fact]
        public void TheProcessTimeoutBoundsASingleProgram()
        {
            using ProgramProbe probe = new();
            probe.Sleep("es-cmd-slow", 60);

            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run($"""
                ${Executor.ProcessTimeoutVariable} = 2
                es-cmd-slow
                """));

            Assert.Contains("Failed to execute", e.Message);
            Assert.Contains("es-cmd-slow", e.Message);
        }
        #endregion

        #region Resolution
        [Fact]
        public void ADottedNameOnPathRunsAsAProgramRatherThanReflection()
        {
            // `vim.tiny`, `python3.12`, `node.exe`: a dotted command used to be a .NET call unless
            // it happened to be a file in the working directory, so none of them could be typed.
            using ProgramProbe probe = new();
            probe.Echo("es-cmd.dotted", "i-am-a-program");

            Assert.Equal(["i-am-a-program"], ScriptHost.Run("es-cmd.dotted").Lines);
        }

        [Fact]
        public void ADottedNameThatIsNotOnPathIsStillADotNetCall()
        {
            Assert.Equal(System.DateTime.Now.Year,
                ScriptHost.Evaluate("System.DateTime.Year (System.DateTime.Now)").AsInt());
        }

        [Fact]
        public void RunReachesAProgramThatAnAliasWouldOtherwiseShadow()
        {
            // `print`, `rm`, `cp`, `mv` and `zip` are all aliases here and real programs out there.
            // RUN is the only way past the table.
            using ProgramProbe probe = new();
            probe.Echo("print", "the real program");

            Assert.Equal(["hello"], ScriptHost.Run("""print "hello" """).Lines);
            Assert.Equal(["the real program", "hello"], ScriptHost.Run("""RUN print "hello" """).Lines);
        }

        [Fact]
        public void RunAlsoWorksAsAnExpression()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-run-expr", "value");

            Assert.Equal("value", ScriptHost.EvaluateText("RUN es-cmd-run-expr"));
        }

        [Fact]
        public void AProgramThatDoesNotExistIsAScriptErrorWithItsLine()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                print one
                es-definitely-no-such-program
                """));

            Assert.Contains("2:", e.Message);
            Assert.Contains("Failed to execute", e.Message);
        }

        [Fact]
        public void ACommandNameCanComeFromAVariable()
        {
            // A consequence of evaluating the head like any other argument. Worth pinning: it is
            // either a feature or a trap, and either way a change here should be deliberate.
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-indirect", "indirect");

            Assert.Equal(["indirect"], ScriptHost.Run("""
                $Program = "es-cmd-indirect"
                $Program
                """).Lines);
        }
        #endregion

        #region Interactive mode
        [Fact]
        public void AtAPromptAStatementProgramTakesTheTerminalInsteadOfBeingCaptured()
        {
            // This is the difference between being able to type `vim` and watching it hang. In
            // interactive mode a top-level command is a statement: it runs in the foreground and
            // produces no value, so there is nothing for the REPL to echo.
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-foreground", "written to the terminal");

            Runtime interactive = new();
            interactive.InjectBool(Executor.InteractiveVariable, true);

            Assert.Equal(ValueKind.Null, ScriptHost.Evaluate("es-cmd-foreground", interactive).Kind);
        }

        [Fact]
        public void WithoutInteractiveModeTheSameUnitIsAnExpression()
        {
            // Which is what a host embedding the engine to compute something expects.
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-evaluated", "as-a-value");

            Assert.Equal("as-a-value", ScriptHost.EvaluateText("es-cmd-evaluated"));
        }

        [Fact]
        public void ASubExpressionCapturesEvenInInteractiveMode()
        {
            // `$sha = (git rev-parse HEAD)` has to keep working at a prompt: there the text really
            // is the value, whatever the runtime's mode.
            using ProgramProbe probe = new();
            probe.Echo("es-cmd-subexpr", "captured-anyway");

            Runtime interactive = new();
            interactive.InjectBool(Executor.InteractiveVariable, true);

            ScriptHost.Evaluate("""$Text = (es-cmd-subexpr)""", interactive);
            Assert.Equal("captured-anyway", interactive.GetVar("Text").Value.AsString());
        }
        #endregion
    }
}
