using EasyShell.Exceptions;
using EasyShell.Tests.Infrastructure;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// `$Count -= 1`, and the head-first `-= $Count 1` that reads like every other operator here.
    ///
    /// <para>These exist because of what the language did without them. `-=` was not a token, so
    /// `-= $a 1` tokenized as the arithmetic command `-` applied to `=`, `$a` and `1`; that is a
    /// legal statement which computes a number, discards it, and leaves `$a` exactly as it was.
    /// A loop written that way ran forever and said nothing.</para>
    /// </summary>
    public class CompoundAssignmentTests
    {
        #region Both spellings
        [Fact]
        public void TheInfixFormUpdatesTheVariable()
        {
            ScriptResult result = ScriptHost.Run("""
                $a = 15
                $a -= 1
                print $a
                """);

            Assert.Equal(["14"], result.Lines);
        }

        [Fact]
        public void TheHeadFirstFormUpdatesTheVariableToo()
        {
            // The spelling that used to be a silent no-op.
            ScriptResult result = ScriptHost.Run("""
                $a = 15
                -= $a 1
                print $a
                """);

            Assert.Equal(["14"], result.Lines);
        }

        [Fact]
        public void ALoopThatCountsDownNowTerminates()
        {
            // The original report: this printed 15 forever.
            ScriptResult result = ScriptHost.Run("""
                $a = 15
                while (> $a 12)
                    print $a
                    -= $a 1
                end
                """);

            Assert.Equal(["15", "14", "13"], result.Lines);
        }
        #endregion

        #region Every operator
        [Theory]
        [InlineData("+=", "12")]
        [InlineData("-=", "8")]
        [InlineData("*=", "20")]
        [InlineData("/=", "5")]
        [InlineData("%=", "0")]
        [InlineData("^=", "100")]
        public void EachOperatorAbbreviatesItsOwnArithmetic(string compound, string expected)
        {
            ScriptResult result = ScriptHost.Run($"""
                $n = 10
                $n {compound} 2
                print $n
                """);

            Assert.Equal([expected], result.Lines);
        }

        [Fact]
        public void TheValueMayBeAnExpression()
        {
            ScriptResult result = ScriptHost.Run("""
                $a = 10
                $a += (* 2 3)
                print $a
                """);

            Assert.Equal(["16"], result.Lines);
        }

        [Fact]
        public void IntegerArithmeticStaysIntegerTheWayTheLongFormDoes()
        {
            // Desugaring rather than a statement kind of its own is what makes this automatic:
            // `$a += 1` IS `$a = (+ $a 1)`, so it inherits the int-safe rule unchanged.
            ScriptResult result = ScriptHost.Run("""
                $a = 1
                $a += 1
                print $a
                """);

            Assert.Equal(["2"], result.Lines);
        }

        [Fact]
        public void PlusEqualsConcatenatesWhenTheOperandIsNotANumber()
        {
            // Same rule as `+` itself, again inherited rather than restated.
            ScriptResult result = ScriptHost.Run("""
                $s = "Build"
                $s += "-42"
                print $s
                """);

            Assert.Equal(["Build-42"], result.Lines);
        }
        #endregion

        #region Diagnostics
        [Fact]
        public void CompoundAssignmentNeedsAVariableThatAlreadyExists()
        {
            // It reads the old value before it writes the new one, so there has to be one.
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("$nope += 1"));

            Assert.Contains("Undefined variable", e.Message);
        }

        [Fact]
        public void TheHeadFirstFormSaysSoWhenTheTargetIsNotAVariable()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("-= 5 1"));

            Assert.Contains("assigns to a variable", e.Message);
        }

        [Fact]
        public void AMissingValueIsReportedRatherThanRunAsAProgram()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                $a = 1
                $a -=
                """));

            Assert.Contains("'-='", e.Message);
            Assert.Contains("missing its value", e.Message);
        }
        #endregion
    }
}
