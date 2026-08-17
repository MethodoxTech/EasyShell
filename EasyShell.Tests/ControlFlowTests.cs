using EasyShell.Exceptions;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using Xunit;

namespace EasyShell.Tests
{
    public class ControlFlowTests
    {
        #region Branching
        [Theory]
        [InlineData(1, "one")]
        [InlineData(2, "two")]
        [InlineData(9, "other")]
        public void TheFirstMatchingBranchWins(int input, string expected)
        {
            ScriptResult result = ScriptHost.Run($"""
                INTVAR X {input}
                IF (== $X 1)
                    print one
                ELSEIF (== $X 2)
                    print two
                ELSE
                    print other
                END
                """);

            Assert.Equal(expected, result.FirstLine);
            Assert.Single(result.Lines);
        }

        [Fact]
        public void AConditionCanBeABareBooleanOrAVariable()
        {
            // `taken` rather than `yes` on purpose: YES and NO are boolean words in this language,
            // so `print yes` prints "True" - see ValueTests.
            Assert.Equal("taken", ScriptHost.Run("IF TRUE\n    print taken\nEND").FirstLine);
            Assert.Equal("", ScriptHost.Run("IF FALSE\n    print taken\nEND").Output);
            Assert.Equal("taken", ScriptHost.Run("BOOLVAR Flag TRUE\nIF $Flag\n    print taken\nEND").FirstLine);
        }
        #endregion

        #region Loops
        [Fact]
        public void WhileRunsUntilItsConditionGoesFalse()
        {
            ScriptResult result = ScriptHost.Run("""
                INTVAR i 0
                WHILE (< $i 3)
                    print (|| "i=" $i)
                    $i = (+ $i 1)
                END
                """);

            Assert.Equal(["i=0", "i=1", "i=2"], result.Lines);
        }

        [Fact]
        public void ACounterDeclaredByAssignmentDoesNotWorkYet()
        {
            // Known issue, pinned here so a fix is noticed rather than a regression: `$i = 0`
            // declares a BOOL, because the literal "0" is parsed as a boolean before the integer
            // parser ever sees it (see ValueTests.ZeroAndOneAreReadAsBooleansToday). The loop
            // below therefore never runs at all. INTVAR is the working spelling today.
            Assert.Equal("", ScriptHost.Run("""
                $i = 0
                WHILE (< $i 3)
                    print (|| "i=" $i)
                    $i = (+ $i 1)
                END
                """).Output);
        }

        [Fact]
        public void NestedBlocksExecuteInOrder()
        {
            ScriptResult result = ScriptHost.Run("""
                INTVAR i 0
                WHILE (< $i 2)
                    IF (== $i 0)
                        print first
                    ELSE
                        print second
                    END
                    $i = (+ $i 1)
                END
                """);

            Assert.Equal(["first", "second"], result.Lines);
        }
        #endregion

        #region Functions
        [Fact]
        public void AFunctionIsDefinedThenCalled()
        {
            ScriptResult result = ScriptHost.Run("""
                FUNC Build
                    print building
                END
                print before
                CALL Build
                print after
                """);

            Assert.Equal(["before", "building", "after"], result.Lines);
        }

        [Fact]
        public void ReturnLeavesOnlyTheFunction()
        {
            ScriptResult result = ScriptHost.Run("""
                FUNC Build
                    print building
                    RETURN
                    print unreachable
                END
                CALL Build
                print after
                """);

            Assert.Equal(["building", "after"], result.Lines);
        }

        [Fact]
        public void FunctionsShareTheScriptsVariables()
        {
            // Variables are global by design; a function is a labelled block, not a scope.
            ScriptResult result = ScriptHost.Run("""
                STRINGVAR Tag "v1"
                FUNC Show
                    print $Tag
                    $Tag = "v2"
                END
                CALL Show
                print $Tag
                """);

            Assert.Equal(["v1", "v2"], result.Lines);
        }

        [Fact]
        public void CallingAnUnknownFunctionNamesTheLine()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                print one
                CALL NoSuchFunction
                """));

            Assert.Contains("2:", e.Message);
            Assert.Contains("Unknown function", e.Message);
        }
        #endregion

        #region Exit and return
        [Fact]
        public void ExitStopsTheScriptAndBecomesTheExitCode()
        {
            ScriptResult result = ScriptHost.Run("""
                print one
                exit 3
                print two
                """);

            Assert.Equal(3, result.ExitCode);
            Assert.Equal(["one"], result.Lines);
        }

        [Fact]
        public void ExitWithoutACodeSucceeds()
            => Assert.Equal(0, ScriptHost.Run("exit").ExitCode);

        [Fact]
        public void ExitRecordsItselfInLastExitCode()
        {
            Runtime rt = new();
            ScriptHost.Run("exit 4", rt);

            Assert.Equal(4, rt.GetVar(Executor.LastExitCodeVariable).Value.AsInt());
        }

        [Fact]
        public void ExitUnwindsOutOfBlocksAndFunctions()
        {
            ScriptResult result = ScriptHost.Run("""
                FUNC Fail
                    print inside
                    exit 9
                END
                WHILE TRUE
                    CALL Fail
                    print unreachable
                END
                print also-unreachable
                """);

            Assert.Equal(9, result.ExitCode);
            Assert.Equal(["inside"], result.Lines);
        }

        [Fact]
        public void ReturnOutsideAFunctionEndsTheScriptQuietly()
        {
            // Documented behaviour, not a crash: RETURN at the top level is how a script says
            // "nothing more to do", and it is a success.
            ScriptResult result = ScriptHost.Run("""
                print one
                RETURN
                print two
                """);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(["one"], result.Lines);
        }

        [Fact]
        public void ReturnTypedAtAPromptEndsOnlyThatUnit()
        {
            // RunUnit swallows it: the session has to stay alive.
            EasyShellEngine engine = new(new Runtime());
            Assert.Equal(ValueKind.Null, engine.RunUnit("RETURN").Kind);
        }

        [Fact]
        public void ExitTypedAtAPromptReachesTheHost()
        {
            // The REPL catches this to return the code to whoever launched it, so RunUnit must
            // NOT swallow it the way it swallows RETURN.
            EasyShellEngine engine = new(new Runtime());
            ScriptExitException e = Assert.Throws<ScriptExitException>(() => engine.RunUnit("exit 5"));

            Assert.Equal(5, e.ExitCode);
        }
        #endregion
    }
}
