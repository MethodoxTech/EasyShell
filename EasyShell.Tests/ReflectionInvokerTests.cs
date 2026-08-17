using EasyShell.Exceptions;
using EasyShell.Reflection;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using System;
using System.IO;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// Calling .NET is what gives this language a standard library, so the binder is effectively
    /// the whole of it: overload choice, conversions, and what a failure reads like.
    /// </summary>
    public class ReflectionInvokerTests
    {
        private static Value Invoke(string member, params Value[] args)
            => ReflectionInvoker.InvokeFullyQualified(member, [.. args]);

        private static Value Text(string s) => new(ValueKind.String, s);
        private static Value Number(int i) => new(ValueKind.Int, i);

        #region Members
        [Fact]
        public void AStaticPropertyNeedsNoArguments()
            => Assert.IsType<DateTime>(Invoke("System.DateTime.Now").AsHandle());

        [Fact]
        public void AStaticFieldIsReadable()
            => Assert.Equal(Path.DirectorySeparatorChar.ToString(), Invoke("System.IO.Path.DirectorySeparatorChar").AsString());

        [Fact]
        public void AStaticMethodTakesItsArgumentsPositionally()
            => Assert.Equal("x=42", Invoke("System.String.Format", Text("x={0}"), Number(42)).AsString());

        [Fact]
        public void AnInstanceMethodTakesItsTargetAsTheFirstArgument()
        {
            // `System.DateTime.AddDays $When 15` is `$When.AddDays(15)`, so an ordinary method is
            // reachable without going through CALL.
            DateTime when = new(2026, 1, 1);
            Value result = Invoke("System.DateTime.AddDays", new Value(ValueKind.Handle, when), Number(15));

            Assert.Equal(new DateTime(2026, 1, 16), Assert.IsType<DateTime>(result.AsHandle()));
        }

        [Fact]
        public void AnInstancePropertyWorksTheSameWay()
            => Assert.Equal(2026, Invoke("System.DateTime.Year", new Value(ValueKind.Handle, new DateTime(2026, 1, 1))).AsInt());

        [Fact]
        public void MemberNamesAreCaseInsensitive()
            => Assert.Equal("X=1", Invoke("system.string.format", Text("X={0}"), Number(1)).AsString());

        [Fact]
        public void InvokeInstanceReachesAMethodOnAnObject()
            => Assert.Equal("ABC", ReflectionInvoker.InvokeInstance("abc", "ToUpperInvariant", []).AsString());
        #endregion

        #region Overloads
        [Fact]
        public void AnExactOverloadBeatsOnePaddedWithDefaults()
        {
            // Otherwise a call quietly lands on an overload with extra parameters that happen to
            // have defaults - and does something other than what was written.
            Assert.Equal("one:x", Invoke("EasyShell.Tests.BinderProbe.Take", Text("x")).AsString());
        }

        [Fact]
        public void AFixedOverloadBeatsAParamsArray()
        {
            // Format(string, object) has to win over Format(string, params object[]), or every
            // two-argument call pays for an array it did not need.
            Assert.Equal("two:x:y", Invoke("EasyShell.Tests.BinderProbe.Take", Text("x"), Text("y")).AsString());
        }

        [Fact]
        public void AParamsArrayCatchesWhatNothingFixedCan()
        {
            Assert.Equal("params:x:3", Invoke("EasyShell.Tests.BinderProbe.Take", Text("x"), Text("a"), Text("b"), Text("c")).AsString());
            Assert.Equal("1 2 3", Invoke("System.String.Format", Text("{0} {1} {2}"), Number(1), Number(2), Number(3)).AsString());
        }

        [Fact]
        public void TheOverloadThatNeedsNoConversionWins()
        {
            Assert.Equal("int:3", Invoke("EasyShell.Tests.BinderProbe.Number", Number(3)).AsString());
            Assert.Equal("double:1.5", Invoke("EasyShell.Tests.BinderProbe.Number", new Value(ValueKind.Double, 1.5)).AsString());
        }

        [Fact]
        public void AnExactMatchOnStringsIsStillPreferred()
            => Assert.Equal("a-b", Invoke("System.String.Concat", Text("a-"), Text("b")).AsString());
        #endregion

        #region Conversions
        [Fact]
        public void StringsIntsBoolsAndDoublesConvertToWhateverTheParameterWants()
        {
            Assert.Equal(4.0, Invoke("System.Math.Sqrt", Number(16)).AsDouble());
            Assert.Equal("True", Invoke("System.Boolean.ToString", new Value(ValueKind.Bool, true)).AsString());
            Assert.Equal(7, Invoke("System.Math.Max", Number(3), Number(7)).AsInt());
        }

        [Fact]
        public void EnumParametersAcceptTheirNameAsText()
            => Assert.True(Invoke("System.String.Equals", Text("a"), Text("A"), Text("OrdinalIgnoreCase")).AsBool());

        [Fact]
        public void ALongComesBackAsAnIntWhileItFits()
        {
            // Counts and file sizes arrive as long; keeping them exact is what lets a script
            // compare one without losing precision to a double.
            Value small = Invoke("System.Math.BigMul", Number(2), Number(3));
            Assert.Equal(ValueKind.Int, small.Kind);
            Assert.Equal(6, small.AsInt());

            Value huge = Invoke("System.Math.BigMul", Number(int.MaxValue), Number(int.MaxValue));
            Assert.Equal(ValueKind.Double, huge.Kind);
        }

        [Fact]
        public void AVoidMethodProducesNothingRatherThanAnError()
            => Assert.Equal(ValueKind.Null, Invoke("System.GC.KeepAlive", Value.Null).Kind);
        #endregion

        #region Failures
        [Fact]
        public void AnUnknownTypeSaysSo()
            => Assert.Contains("Type not found", Assert.Throws<EasyShellException>(() => Invoke("System.NoSuchType.Member")).Message);

        [Fact]
        public void AnUnknownMemberSaysSo()
            => Assert.Contains("Member not found", Assert.Throws<EasyShellException>(() => Invoke("System.DateTime.NoSuchMember")).Message);

        [Fact]
        public void ArgumentsThatFitNoOverloadAreDescribed()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => Invoke("System.Math.Sqrt", Text("one"), Text("two"), Text("three")));

            Assert.Contains("No matching overload", e.Message);
            Assert.Contains("String", e.Message);
        }

        [Fact]
        public void SomethingThatIsNotAQualifiedNameIsRejected()
            => Assert.Contains("Invalid member", Assert.Throws<EasyShellException>(() => Invoke("Nonsense")).Message);

        [Fact]
        public void TheCalleesOwnComplaintIsWhatTheUserSees()
        {
            // Not "Exception has been thrown by the target of an invocation", which says nothing
            // about what went wrong.
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => Invoke("System.IO.File.ReadAllText", Text("/es-no-such-file-anywhere.txt")));

            Assert.DoesNotContain("target of an invocation", e.Message);
            Assert.Contains("es-no-such-file-anywhere", e.Message);
        }
        #endregion

        #region Through the language
        [Fact]
        public void AFailedCallIsTaggedWithTheScriptLine()
        {
            // Reflection has no idea which line asked for it; the executor is what knows.
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""
                print one
                print two
                System.IO.File.ReadAllText "/es-no-such-file-anywhere.txt"
                """));

            Assert.StartsWith("3:", e.Message);
        }

        [Fact]
        public void ExitInsideAnExpressionIsControlFlowNotAFailure()
        {
            // ScriptExitException and ScriptReturnException are the script's own control flow and
            // must not be rewritten into "the call failed" on their way out.
            Assert.Equal(2, ScriptHost.Run("""print (exit 2)""").ExitCode);
        }
        #endregion
    }

    /// <summary>
    /// Overloads shaped so the binder's preferences are observable. The BCL is a poor test subject
    /// here - it barely uses optional parameters - and a call into it can only be checked by its
    /// result, never by which overload actually ran.
    /// </summary>
    public static class BinderProbe
    {
        public static string Take(string a) => $"one:{a}";
        public static string Take(string a, string b = "default") => $"two:{a}:{b}";
        public static string Take(string a, params string[] rest) => $"params:{a}:{rest.Length}";

        public static string Number(int value) => $"int:{value}";
        public static string Number(double value) => $"double:{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
