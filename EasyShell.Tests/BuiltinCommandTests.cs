using EasyShell.Exceptions;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// The commands the executor answers itself, before any alias, .NET member or program on PATH
    /// gets a chance: arithmetic, comparison, logic, concatenation and ASSERT.
    /// </summary>
    public class BuiltinCommandTests
    {
        #region Arithmetic
        [Theory]
        [InlineData("+ 2 3", "5")]
        [InlineData("+ 2 3 4", "9")]        // head-first, any number of operands
        [InlineData("- 10 3", "7")]
        [InlineData("- 5", "-5")]           // unary minus
        [InlineData("* 2 3 4", "24")]
        [InlineData("/ 7 2", "3.5")]
        [InlineData("% 7 3", "1")]
        [InlineData("^ 2 10", "1024")]
        public void ArithmeticIsHeadFirst(string command, string expected)
            => Assert.Equal(expected, ScriptHost.EvaluateText(command));

        [Fact]
        public void IntegerOperandsStayIntegers()
        {
            // A file count or a loop counter formatted as "3" rather than "3" only by luck is a
            // bug waiting to reach a file name, so the integer-safe operators keep the kind.
            Assert.Equal(ValueKind.Int, ScriptHost.Evaluate("+ 2 3").Kind);
            Assert.Equal(ValueKind.Double, ScriptHost.Evaluate("/ 4 2").Kind);   // division never is
        }

        [Fact]
        public void PlusConcatenatesAsSoonAsAnOperandIsNotANumber()
        {
            // Adding non-numbers used to produce 0 in silence.
            Assert.Equal("build-42", ScriptHost.EvaluateText("""+ "build-" 42"""));
            Assert.Equal("11", ScriptHost.EvaluateText("""+ "10" 1"""));   // numeric strings still add up
        }
        #endregion

        #region Concatenation
        [Theory]
        [InlineData("|| a b c")]
        [InlineData("CONCAT a b c")]
        [InlineData("APPEND a b c")]
        public void ConcatenationHasThreeSpellingsAndTakesAnyNumberOfArguments(string command)
            => Assert.Equal("abc", ScriptHost.EvaluateText(command));

        [Fact]
        public void ConcatenationStringifiesEveryKind()
            => Assert.Equal("Built 3 packages, ok=TRUE",
                ScriptHost.EvaluateText("""CONCAT "Built " 3 " packages, ok=" TRUE"""));
        #endregion

        #region Comparison
        [Theory]
        [InlineData("== 2 2", true)]
        [InlineData("== 2 3", false)]
        [InlineData("!= 2 3", true)]
        [InlineData("> 10 9", true)]
        [InlineData("< 10 9", false)]
        [InlineData(">= 10 10", true)]
        [InlineData("<= 9 10", true)]
        public void NumbersCompareAsNumbers(string command, bool expected)
            => Assert.Equal(expected, ScriptHost.Evaluate(command).AsBool());

        [Fact]
        public void NumericStringsCompareAsNumbers()
        {
            // Otherwise "10" < "9" ordinally, and every version or count check read from a program's
            // output would be quietly wrong.
            Assert.True(ScriptHost.Evaluate("""> "10" "9" """).AsBool());
            Assert.True(ScriptHost.Evaluate("""== "2" 2""").AsBool());
        }

        [Fact]
        public void TextComparesIgnoringCase()
        {
            // Shell-friendly on purpose: paths and flags are compared far more often than anything
            // that would care about case here.
            Assert.True(ScriptHost.Evaluate("== abc ABC").AsBool());
            Assert.True(ScriptHost.Evaluate("""< "apple" "banana" """).AsBool());
        }

        [Fact]
        public void BooleansCompareAsBooleans()
        {
            Assert.True(ScriptHost.Evaluate("== TRUE true").AsBool());
            Assert.True(ScriptHost.Evaluate("!= TRUE FALSE").AsBool());
        }

        [Fact]
        public void ComparisonWantsExactlyTwoOperands()
            => Assert.Contains("expects exactly 2", Assert.Throws<EasyShellException>(() => ScriptHost.Run("== 1")).Message);
        #endregion

        #region Logic
        [Theory]
        [InlineData("NOT TRUE", false)]
        [InlineData("NOT FALSE", true)]
        [InlineData("AND TRUE TRUE", true)]
        [InlineData("AND TRUE FALSE", false)]
        [InlineData("OR FALSE TRUE", true)]
        [InlineData("OR FALSE FALSE", false)]
        [InlineData("XOR TRUE TRUE", false)]
        [InlineData("XOR TRUE FALSE", true)]
        public void LogicOperators(string command, bool expected)
            => Assert.Equal(expected, ScriptHost.Evaluate(command).AsBool());

        [Theory]
        [InlineData("AND FALSE $NeverDefined")]
        [InlineData("OR TRUE $NeverDefined")]
        [InlineData("""?? "present" $NeverDefined""")]
        [InlineData("""?: TRUE "taken" $NeverDefined""")]
        [InlineData("""?: FALSE $NeverDefined "taken" """)]
        public void ShortCircuitingMeansTheOtherSideIsNeverEvaluated(string command)
        {
            // Reading an undefined variable throws, which makes it the cleanest possible probe:
            // if the unused branch were evaluated, this would not come back at all.
            ScriptHost.Evaluate(command);
        }

        [Fact]
        public void NullCoalescingTreatsEmptyTextAsMissing()
        {
            // `$Tag = (?? (getenv "TAG") "dev")` is the shape this exists for, and an unset
            // environment variable comes back as an empty string, not as null.
            Assert.Equal("dev", ScriptHost.EvaluateText("""?? "" "dev" """));
            Assert.Equal("set", ScriptHost.EvaluateText("""?? "set" "dev" """));
        }

        [Fact]
        public void ConditionalPicksTheBranch()
        {
            Assert.Equal("yes", ScriptHost.EvaluateText("""?: TRUE "yes" "no" """));
            Assert.Equal("no", ScriptHost.EvaluateText("""?: FALSE "yes" "no" """));
        }

        [Theory]
        [InlineData("NOT", "NOT expects exactly 1")]
        [InlineData("AND TRUE", "AND expects exactly 2")]
        [InlineData("OR TRUE", "OR expects exactly 2")]
        [InlineData("XOR TRUE", "XOR expects exactly 2")]
        [InlineData("?? TRUE", "?? expects exactly 2")]
        [InlineData("?: TRUE", "?: expects exactly 3")]
        public void LogicOperatorsCheckTheirArity(string command, string expected)
            => Assert.Contains(expected, Assert.Throws<EasyShellException>(() => ScriptHost.Run(command)).Message);
        #endregion

        #region Assert
        [Fact]
        public void AssertPassesQuietly()
            => Assert.Equal("", ScriptHost.Run("assert (== 1 1)").Output);

        [Fact]
        public void AFailedAssertNamesTheLineAndTheMessage()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                print starting
                assert (== 1 2) "the counts should match"
                """));

            Assert.Contains("2:", e.Message);
            Assert.Contains("the counts should match", e.Message);
        }

        [Fact]
        public void AFailedAssertWithoutAMessageStillSaysSomething()
            => Assert.Contains("Assertion failed", Assert.Throws<EasyShellException>(() => ScriptHost.Run("assert FALSE")).Message);
        #endregion

        #region Typed declarations
        [Theory]
        [InlineData("INTVAR X 42", ValueKind.Int, "42")]
        [InlineData("BOOLVAR X TRUE", ValueKind.Bool, "TRUE")]
        [InlineData("""STRINGVAR X "1.0" """, ValueKind.String, "1.0")]
        [InlineData("DOUBLEVAR X 2", ValueKind.Double, "2")]
        public void TypedDeclarationsCoerceTheirValue(string declaration, ValueKind kind, string text)
        {
            Runtime rt = new();
            ScriptHost.Run(declaration, rt);

            Assert.Equal(kind, rt.GetVar("X").Value.Kind);
            Assert.Equal(text, rt.GetVar("X").Value.AsString());
        }

        [Fact]
        public void AHandleDeclarationHoldsTheObjectItself()
        {
            Runtime rt = new();
            ScriptHost.Run("HANDLEVAR Now (System.DateTime.Now)", rt);

            Assert.IsType<System.DateTime>(rt.GetVar("Now").Value.AsHandle());
        }

        [Fact]
        public void ADeclarationWithoutAValueSaysHowToWriteOne()
            => Assert.Contains("INTVAR <NAME> <VALUE>",
                Assert.Throws<EasyShellException>(() => ScriptHost.Run("INTVAR Count")).Message);
        #endregion

        #region Instance calls
        [Fact]
        public void CallReachesAMethodOnAHandle()
        {
            string year = ScriptHost.EvaluateText("""
                HANDLEVAR Now (System.DateTime.Now)
                CALL $Now ToString "yyyy"
                """);

            Assert.Equal(System.DateTime.Now.Year.ToString(), year);
        }

        [Fact]
        public void CallAlsoWorksOnAnOrdinaryValue()
        {
            // Any non-null value can take an instance call - `CALL $someString ToUpper` is a
            // perfectly reasonable thing to type, and requiring a HANDLE for it would be noise.
            Assert.Equal("ABC", ScriptHost.EvaluateText("""
                $Name = "abc"
                CALL $Name ToUpper
                """));
        }

        [Fact]
        public void CallOnNothingIsAScriptError()
            => Assert.Contains("CALL target is null", Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                HANDLEVAR Nothing (System.Environment.GetEnvironmentVariable "EASYSHELL_UNSET_VARIABLE")
                CALL $Nothing ToUpper
                """)).Message);
        #endregion
    }
}
