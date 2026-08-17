using EasyShell.Types;
using Xunit;

namespace EasyShell.Tests
{
    public class ValueTests
    {
        #region Literals
        [Theory]
        [InlineData("TRUE", true)]
        [InlineData("true", true)]
        [InlineData("Yes", true)]
        [InlineData("FALSE", false)]
        [InlineData("no", false)]
        public void BooleanWordsBecomeBooleans(string token, bool expected)
        {
            Value v = Value.FromLiteralToken(token, wasQuoted: false);

            Assert.Equal(ValueKind.Bool, v.Kind);
            Assert.Equal(expected, v.AsBool());
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("1", 1)]
        public void ZeroAndOneAreNumbers(string token, int expected)
        {
            // They used to be booleans - TryParseBool accepts "1"/"0" and was asked first - which
            // made `$i = 0` declare a BOOL and the obvious counter silently never advance.
            Value v = Value.FromLiteralToken(token, wasQuoted: false);

            Assert.Equal(ValueKind.Int, v.Kind);
            Assert.Equal(expected, v.AsInt());
        }

        [Fact]
        public void ANumberIsStillReadableAsACondition()
        {
            // Which is what makes preferring the number free: a flag compared against 1, or used
            // as a condition outright, behaves exactly as it did.
            Assert.True(new Value(ValueKind.Int, 1).AsBool());
            Assert.False(new Value(ValueKind.Int, 0).AsBool());

            Assert.True(TryBool("1"));
            Assert.False(TryBool("0"));

            static bool TryBool(string s)
            {
                Assert.True(Value.TryParseBool(s, out bool b));
                return b;
            }
        }

        [Theory]
        [InlineData("42", ValueKind.Int)]
        [InlineData("-7", ValueKind.Int)]
        [InlineData("4.5", ValueKind.Double)]
        [InlineData("hello", ValueKind.String)]
        [InlineData("2026-01-01", ValueKind.String)]
        public void OtherLiteralsTakeTheNarrowestKindThatFits(string token, ValueKind expected)
            => Assert.Equal(expected, Value.FromLiteralToken(token, wasQuoted: false).Kind);

        [Theory]
        [InlineData("42")]
        [InlineData("TRUE")]
        [InlineData("4.5")]
        public void QuotingKeepsALiteralAString(string token)
        {
            // The whole point of the quote: `print "42"` must not turn into a number and lose its
            // formatting, and a version string like "1.0" must not become a double.
            Value v = Value.FromLiteralToken(token, wasQuoted: true);

            Assert.Equal(ValueKind.String, v.Kind);
            Assert.Equal(token, v.AsString());
        }
        #endregion

        #region Conversions
        [Fact]
        public void BooleansPrintAsTrueAndFalseInTheLanguagesOwnSpelling()
        {
            Assert.Equal("TRUE", new Value(ValueKind.Bool, true).AsString());
            Assert.Equal("FALSE", new Value(ValueKind.Bool, false).AsString());
        }

        [Fact]
        public void NumbersFormatWithTheInvariantCulture()
        {
            // A script that writes a version number into a file must produce "1.5" on a machine
            // whose locale would otherwise write "1,5".
            Assert.Equal("1.5", new Value(ValueKind.Double, 1.5).AsString());
            Assert.Equal("42", new Value(ValueKind.Int, 42).AsString());
        }

        [Fact]
        public void NullIsEmptyAndFalse()
        {
            Assert.Equal("", Value.Null.AsString());
            Assert.False(Value.Null.AsBool());
            Assert.Equal(0, Value.Null.AsInt());
        }

        [Theory]
        [InlineData("TRUE", true)]
        [InlineData("no", false)]
        [InlineData("anything else", true)]     // non-empty text is truthy
        [InlineData("", false)]
        public void StringsFallBackToEmptinessWhenTheyAreNotBooleanWords(string text, bool expected)
            => Assert.Equal(expected, new Value(ValueKind.String, text).AsBool());

        [Fact]
        public void NumbersConvertBothWays()
        {
            Assert.Equal(3, new Value(ValueKind.Double, 3.9).AsInt());          // truncates
            Assert.Equal(1.0, new Value(ValueKind.Bool, true).AsDouble());
            Assert.Equal(12, new Value(ValueKind.String, "12").AsInt());
            Assert.Equal(0, new Value(ValueKind.String, "not a number").AsInt());
        }

        [Fact]
        public void AHandleIsOnlyAHandle()
        {
            object payload = new();

            Assert.Same(payload, new Value(ValueKind.Handle, payload).AsHandle());
            Assert.Null(new Value(ValueKind.String, "x").AsHandle());
            Assert.True(new Value(ValueKind.Handle, payload).AsBool());
            Assert.False(new Value(ValueKind.Handle, null).AsBool());
        }
        #endregion

        #region Variables
        [Fact]
        public void ADeclaredVariableCoercesWhateverItIsGiven()
        {
            // INTVAR means INT: the declared kind wins over the kind of the value assigned to it,
            // both at declaration and on every later assignment.
            Assert.Equal("42", Variable.Coerce(ValueKind.String, new Value(ValueKind.Int, 42)).AsString());
            Assert.Equal(ValueKind.Int, Variable.Coerce(ValueKind.Int, new Value(ValueKind.String, "7")).Kind);
            Assert.Equal(7, Variable.Coerce(ValueKind.Int, new Value(ValueKind.String, "7")).AsInt());
            Assert.Equal(0, Variable.Coerce(ValueKind.Int, new Value(ValueKind.String, "nonsense")).AsInt());
        }

        [Fact]
        public void AssignmentKeepsTheDeclaredKind()
        {
            Variable v = new("Count", ValueKind.Int, new Value(ValueKind.String, "1"));
            Assert.Equal(ValueKind.Int, v.Value.Kind);

            v.Set(new Value(ValueKind.Double, 9.7));
            Assert.Equal(ValueKind.Int, v.Value.Kind);
            Assert.Equal(9, v.Value.AsInt());
        }
        #endregion
    }
}
