using EasyShell.Exceptions;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// The surface a host actually programs against: run a script, evaluate a unit, look at what
    /// the session holds.
    /// </summary>
    public class EngineTests
    {
        [Fact]
        public void ASuccessfulScriptIsZero()
            => Assert.Equal(0, ScriptHost.Run("print ok").ExitCode);

        [Fact]
        public void AnEmptyScriptIsAllowed()
        {
            Assert.Equal(0, ScriptHost.Run("").ExitCode);
            Assert.Equal(0, ScriptHost.Run("   \n\n# nothing here\n").ExitCode);
        }

        [Fact]
        public void ScriptErrorsReachTheHostRatherThanBeingSwallowed()
        {
            // The CLI turns this into "(Error) ..." and exit code 1; the engine must not decide
            // that on its behalf.
            Assert.Throws<EasyShellException>(() => ScriptHost.Run("print $NeverDefined"));
        }

        [Fact]
        public void TheOriginIsPartOfAParseError()
        {
            EasyShellEngine engine = new(new Runtime());
            EasyShellException e = Assert.Throws<EasyShellException>(() => engine.Run("IF TRUE", "Publish.easy"));

            Assert.Contains("Publish.easy:1:", e.Message);
        }

        [Fact]
        public void RunUnitReturnsTheLastCommandsValue()
        {
            // Only a command produces a value; an assignment or a block leaves the previous one
            // alone, which is why a REPL does not echo anything after `$X = 1`.
            Assert.Equal("5", ScriptHost.EvaluateText("+ 2 3"));
            Assert.Equal(ValueKind.Null, ScriptHost.Evaluate("""$X = "quiet" """).Kind);
        }

        [Fact]
        public void RunUnitKeepsTheSessionBetweenCalls()
        {
            // One prompt line at a time still has to add up to one session.
            EasyShellEngine engine = new(new Runtime());

            engine.RunUnit("""STRINGVAR Tag "v1" """);
            engine.RunUnit("""$Tag = (|| $Tag "-patched")""");

            Assert.Equal("v1-patched", engine.RunUnit("|| $Tag").AsString());
        }

        [Fact]
        public void DumpVariablesAndFunctionsAreWhatTheReplShows()
        {
            Runtime rt = new();
            EasyShellEngine engine = new(rt);
            engine.Run("""
                STRINGVAR Tag "v1"
                INTVAR Count 2
                FUNC Build
                    print x
                END
                FUNC Clean
                    print y
                END
                """);

            List<(string Name, string Kind, string Value)> variables = [.. engine.DumpVariables()];
            Assert.Contains(("Count", "INT", "2"), variables);
            Assert.Contains(("Tag", "STRING", "v1"), variables);

            Assert.Equal(["Build", "Clean"], engine.DumpFunctions().ToArray());
        }

        [Fact]
        public void AHostCanInjectItsOwnVariablesBeforeRunning()
        {
            // Every host does this: $EasyScriptRoot, $IsWindows, $EasyArgs and friends are nothing
            // more than injected variables.
            Runtime rt = new();
            rt.InjectString("$EasyScriptRoot", "/scripts");
            rt.InjectBool("$IsWindows", false);

            Assert.Equal(["/scripts", "FALSE"], ScriptHost.Run("""
                print $EasyScriptRoot
                print (|| $IsWindows)
                """, rt).Lines);
        }
    }
}
