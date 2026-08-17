using EasyShell.Exceptions;
using EasyShell.Parsing;
using EasyShell.Tests.Infrastructure;
using Xunit;

namespace EasyShell.Tests
{
    public class ParserTests
    {
        private static Block Parse(string text, string? origin = null) => new Parser(origin).Parse(text);

        #region Comments
        [Fact]
        public void TrailingCommentsAreStripped()
            => Assert.Equal("print x ", Parser.StripComment("print x # and the rest"));

        [Fact]
        public void AHashInsideAStringIsNotAComment()
        {
            // Naive truncation turned `print "Issue #42"` into an unterminated string literal.
            Assert.Equal("""print "Issue #42" """, Parser.StripComment("""print "Issue #42" # note"""));
            Assert.Equal("Issue #42", ScriptHost.Run("""print "Issue #42" # note""").FirstLine);
        }

        [Fact]
        public void AnEscapedQuoteDoesNotEndTheStringForCommentPurposes()
            => Assert.Equal("""print "a \" # b" """, Parser.StripComment("""print "a \" # b" # real comment"""));

        [Fact]
        public void CommentOnlyAndBlankLinesProduceNoStatements()
            => Assert.Empty(Parse("""
                # just a comment

                    # indented comment
                """).Statements);
        #endregion

        #region Statements
        [Fact]
        public void AssignmentIsRecognized()
        {
            Statement stmt = Assert.Single(Parse("$Name = 5").Statements);
            AssignStatement assign = Assert.IsType<AssignStatement>(stmt);

            Assert.Equal("Name", assign.VarName);
            Assert.Equal(1, assign.Line);
        }

        [Fact]
        public void ACommandBecomesACommandStatement()
        {
            CommandStatement cmd = Assert.IsType<CommandStatement>(Assert.Single(Parse("git status").Statements));
            Assert.Equal(2, cmd.Args.Count);
        }

        [Fact]
        public void ParenthesesFoldIntoASubExpression()
        {
            CommandStatement cmd = Assert.IsType<CommandStatement>(Assert.Single(Parse("print (+ 1 2)").Statements));

            Assert.Equal(2, cmd.Args.Count);
            ExprArg expr = Assert.IsType<ExprArg>(cmd.Args[1]);
            Assert.Equal(3, expr.InnerCommandArgs.Count);
        }

        [Fact]
        public void NestedParenthesesNestTheirExpressions()
        {
            CommandStatement cmd = Assert.IsType<CommandStatement>(
                Assert.Single(Parse("""print (System.String.Format "{0}" (+ 1 2))""").Statements));

            ExprArg outer = Assert.IsType<ExprArg>(cmd.Args[1]);
            Assert.IsType<ExprArg>(outer.InnerCommandArgs[2]);
        }

        [Fact]
        public void UnmatchedParenthesisIsAnError()
            => Assert.Contains("Unmatched", Assert.Throws<EasyShellException>(() => Parse("print (+ 1 2")).Message);

        [Fact]
        public void CallOfAFunctionIsItsOwnStatement()
        {
            CallFuncStatement call = Assert.IsType<CallFuncStatement>(Assert.Single(Parse("CALL Build").Statements));
            Assert.Equal("Build", call.Name);
        }

        [Fact]
        public void CallOnAHandleStaysACommand()
        {
            // `CALL $handle Method args` has to reach the executor as an ordinary command; only the
            // two-token form is a function call.
            Assert.IsType<CommandStatement>(Assert.Single(Parse("CALL $Now ToString").Statements));
        }
        #endregion

        #region Blocks
        [Fact]
        public void IfElseIfElseCollectsEveryBranch()
        {
            IfStatement iff = Assert.IsType<IfStatement>(Assert.Single(Parse("""
                IF (== $X 1)
                    print one
                ELSEIF (== $X 2)
                    print two
                ELSE
                    print other
                END
                """).Statements));

            Assert.Equal(2, iff.Branches.Count);
            Assert.NotNull(iff.ElseBody);
        }

        [Fact]
        public void BlocksNest()
        {
            WhileStatement loop = Assert.IsType<WhileStatement>(Assert.Single(Parse("""
                WHILE TRUE
                    IF TRUE
                        print inner
                    END
                END
                """).Statements));

            Assert.IsType<IfStatement>(Assert.Single(loop.Body.Statements));
        }

        [Fact]
        public void FunctionDefinitionCapturesItsBody()
        {
            FuncDefinitionStatement func = Assert.IsType<FuncDefinitionStatement>(Assert.Single(Parse("""
                FUNC Build
                    print building
                    print done
                END
                """).Statements));

            Assert.Equal("Build", func.Name);
            Assert.Equal(2, func.Body.Statements.Count);
        }

        [Theory]
        [InlineData("IF TRUE\n    print x", "IF block missing END")]
        [InlineData("WHILE TRUE\n    print x", "WHILE block missing END")]
        [InlineData("FUNC F\n    print x", "FUNC block missing END")]
        public void AnUnclosedBlockIsAnError(string script, string expected)
            => Assert.Contains(expected, Assert.Throws<EasyShellException>(() => Parse(script)).Message);

        [Theory]
        [InlineData("IF", "IF missing condition")]
        [InlineData("WHILE", "WHILE missing condition")]
        [InlineData("FUNC", "FUNC syntax")]
        public void AHeaderWithoutItsOperandIsAnError(string script, string expected)
            => Assert.Contains(expected, Assert.Throws<EasyShellException>(() => Parse(script)).Message);
        #endregion

        #region Diagnostics
        [Fact]
        public void ErrorsCarryTheirOriginAndLine()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => Parse("""
                print ok
                print ok

                IF TRUE
                """, origin: "Build.easy"));

            Assert.Contains("Build.easy:4:", e.Message);
        }

        [Theory]
        [InlineData("$X = 1 2")]
        [InlineData("IF == $X 1\n    print x\nEND")]
        [InlineData("WHILE == $X 1\n    print x\nEND")]
        [InlineData("IF FALSE\n    print x\nELSEIF == $X 1\n    print y\nEND")]
        public void SeveralValuesWhereOneIsExpectedIsAScriptError(string script)
        {
            // Forgetting the parentheses around a condition - `IF == $X 1` - is the easiest mistake
            // to make in this language. It used to surface as LINQ's "Sequence contains more than
            // one element", an unhandled crash with no line number attached; it has to read as an
            // ordinary script error instead.
            EasyShellException e = Assert.Throws<EasyShellException>(() => Parse(script, origin: "Build.easy"));
            Assert.Contains("Build.easy:", e.Message);
        }
        #endregion
    }
}
