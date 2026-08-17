using EasyShell.Exceptions;
using EasyShell.Parsing;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EasyShell.Tests
{
    public class TokenizerTests
    {
        private static List<Token> Tokenize(string line) => Tokenizer.Tokenize(line, lineNo: 1);
        private static string[] Texts(string line) => [.. Tokenize(line).Select(t => t.Text)];

        [Fact]
        public void WordsSplitOnWhitespace()
            => Assert.Equal(["git", "rev-parse", "HEAD"], Texts("  git   rev-parse HEAD  "));

        [Fact]
        public void QuotedTextIsOneTokenAndStaysAString()
        {
            List<Token> tokens = Tokenize("""print "hello world" """);

            Assert.Equal(2, tokens.Count);
            Assert.Equal("hello world", tokens[1].Text);
            // WasQuoted is what stops "42" from being read back as the number 42.
            Assert.True(tokens[1].WasQuoted);
            Assert.False(tokens[0].WasQuoted);
        }

        [Fact]
        public void EmptyQuotesAreStillAToken()
        {
            // `assert (== (arg 99) "")` depends on this: an empty string has to survive as an
            // argument rather than vanishing with the whitespace.
            List<Token> tokens = Tokenize("""== $X "" """);

            Assert.Equal(3, tokens.Count);
            Assert.Equal("", tokens[2].Text);
            Assert.True(tokens[2].WasQuoted);
        }

        [Fact]
        public void BackslashEscapesQuoteAndBackslashOnly()
        {
            Assert.Equal([@"a""b"], Texts(@"""a\""b"""));
            Assert.Equal([@"C:\temp"], Texts(@"""C:\\temp"""));
            // Any other backslash is left alone, so a Windows path written the way people actually
            // write it is not silently mangled into "C:emp".
            Assert.Equal([@"C:\temp"], Texts(@"""C:\temp"""));
        }

        [Fact]
        public void DollarNameBecomesAVariableReference()
        {
            List<Token> tokens = Tokenize("print $Version");

            Assert.Equal(TokKind.Word, tokens[0].Kind);
            Assert.Equal(TokKind.VarRef, tokens[1].Kind);
            Assert.Equal("$Version", tokens[1].Text);
        }

        [Theory]
        [InlineData("$my_var")]
        [InlineData("$my-var")]
        [InlineData("$LAST_EXIT_CODE")]
        [InlineData("$x1")]
        public void VariableNamesAllowLettersDigitsUnderscoreAndDash(string reference)
            => Assert.Equal(TokKind.VarRef, Tokenize(reference)[0].Kind);

        [Theory]
        [InlineData("$")]
        [InlineData("$$")]
        [InlineData("$a.b")]
        public void ADollarThatIsNotAnIdentifierIsAnOrdinaryWord(string text)
            => Assert.Equal(TokKind.Word, Tokenize(text)[0].Kind);

        [Fact]
        public void AQuotedDollarIsNotAVariableReference()
        {
            List<Token> tokens = Tokenize("""print "$Version" """);

            Assert.Equal(TokKind.Word, tokens[1].Kind);
            Assert.Equal("$Version", tokens[1].Text);
        }

        [Theory]
        [InlineData("==")]
        [InlineData("!=")]
        [InlineData(">=")]
        [InlineData("<=")]
        public void TwoCharacterOperatorsWinOverOneCharacterOnes(string op)
        {
            // Matching '=' first would turn `>=` into `>` followed by `=`, and the comparison
            // would silently become the wrong one.
            List<Token> tokens = Tokenize($"{op} $A 1");

            Assert.Equal(op, tokens[0].Text);
            Assert.Equal(TokKind.Symbol, tokens[0].Kind);
            Assert.Equal(3, tokens.Count);
        }

        [Fact]
        public void OperatorsDoNotNeedSurroundingSpace()
            => Assert.Equal(["$X", "=", "5"], Texts("$X=5"));

        [Fact]
        public void ParenthesesAreTheirOwnTokens()
        {
            List<Token> tokens = Tokenize("print (+ 1 2)");

            Assert.Equal(TokKind.LParen, tokens[1].Kind);
            Assert.Equal(TokKind.RParen, tokens[^1].Kind);
        }

        [Fact]
        public void PathsAndDottedNamesSurviveIntact()
        {
            // The tokenizer must not get opinions about '.', '/' or '\': `System.DateTime.Now`,
            // `./build.sh` and `C:\out` are all single words.
            Assert.Equal(["System.DateTime.Now"], Texts("System.DateTime.Now"));
            Assert.Equal(["./build.sh", "--flag"], Texts("./build.sh --flag"));
            Assert.Equal([@"C:\out\bin"], Texts(@"C:\out\bin"));
        }

        [Fact]
        public void UnterminatedStringIsReportedWithItsLine()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => Tokenizer.Tokenize("""print "never closed""", lineNo: 42));

            Assert.Contains("42", e.Message);
            Assert.Contains("Unterminated", e.Message);
        }
    }
}
