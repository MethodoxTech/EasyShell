using EasyShell.Exceptions;
using EasyShell.Types;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EasyShell.Tests
{
    public class RuntimeTests
    {
        [Fact]
        public void VariableNamesAreCaseInsensitive()
        {
            Runtime rt = new();
            rt.InjectString("Name", "EasyShell");

            Assert.Equal("EasyShell", rt.GetVar("NAME").Value.AsString());
            Assert.Equal("EasyShell", rt.GetVar("name").Value.AsString());
        }

        [Fact]
        public void InjectionAcceptsBothDollarAndBareNames()
        {
            // Hosts write `rt.InjectString("$EasyScriptRoot", ...)` because that is how the script
            // will spell it; both have to land on the same variable.
            Runtime rt = new();
            rt.InjectString("$Root", "/tmp");
            rt.InjectInt("Count", 3);

            Assert.Equal("/tmp", rt.GetVar("Root").Value.AsString());
            Assert.Equal(3, rt.GetVar("Count").Value.AsInt());
        }

        [Fact]
        public void EveryInjectedKindKeepsItsKind()
        {
            Runtime rt = new();
            object handle = new();
            rt.InjectString("S", "x");
            rt.InjectInt("I", 1);
            rt.InjectBool("B", true);
            rt.InjectDouble("D", 1.5);
            rt.InjectHandle("H", handle);

            Assert.Equal(ValueKind.String, rt.GetVar("S").Value.Kind);
            Assert.Equal(ValueKind.Int, rt.GetVar("I").Value.Kind);
            Assert.Equal(ValueKind.Bool, rt.GetVar("B").Value.Kind);
            Assert.Equal(ValueKind.Double, rt.GetVar("D").Value.Kind);
            Assert.Same(handle, rt.GetVar("H").Value.AsHandle());
        }

        [Fact]
        public void ReadingAnUndefinedVariableIsAScriptError()
            => Assert.Contains("Undefined variable", Assert.Throws<EasyShellException>(() => new Runtime().GetVar("Nope")).Message);

        [Fact]
        public void AssigningAnUndeclaredVariableDeclaresIt()
        {
            Runtime rt = new();
            rt.AssignOrDeclare("Tag", new Value(ValueKind.String, "v1"));

            Assert.Equal("v1", rt.GetVar("Tag").Value.AsString());
            Assert.Equal(ValueKind.String, rt.GetVar("Tag").DeclaredKind);
        }

        [Fact]
        public void AnInferredNullBecomesAString()
        {
            // A command that produced nothing still has to leave a usable variable behind, and an
            // empty string is the only thing the rest of the language can do anything with.
            Runtime rt = new();
            rt.AssignOrDeclare("Out", Value.Null);

            Assert.Equal(ValueKind.String, rt.GetVar("Out").DeclaredKind);
            Assert.Equal("", rt.GetVar("Out").Value.AsString());
        }

        [Fact]
        public void AssigningADeclaredVariableKeepsItsDeclaredKind()
        {
            Runtime rt = new();
            rt.Declare("INT", "Count", new Value(ValueKind.Int, 1));
            rt.Assign("Count", new Value(ValueKind.String, "12"));

            Assert.Equal(ValueKind.Int, rt.GetVar("Count").Value.Kind);
            Assert.Equal(12, rt.GetVar("Count").Value.AsInt());
        }

        [Fact]
        public void AssigningSomethingNeverDeclaredIsAnError()
            => Assert.Throws<EasyShellException>(() => new Runtime().Assign("Nope", Value.Null));

        [Fact]
        public void AnUnknownTypeCommandIsAnError()
            => Assert.Contains("Unknown type command", Assert.Throws<EasyShellException>(
                () => new Runtime().Declare("FLOATVAR", "X", Value.Null)).Message);

        [Fact]
        public void DeclaringTheSameNameTwiceRedeclaresIt()
        {
            // `STRINGVAR X 1` after `INTVAR X 1` replaces the variable rather than fighting the
            // old declaration's coercion; a REPL session depends on being able to change its mind.
            Runtime rt = new();
            rt.Declare("INT", "X", new Value(ValueKind.Int, 1));
            rt.Declare("STRING", "X", new Value(ValueKind.String, "one"));

            Assert.Equal(ValueKind.String, rt.GetVar("X").DeclaredKind);
            Assert.Equal("one", rt.GetVar("X").Value.AsString());
        }

        [Fact]
        public void DumpVariablesIsSortedAndCarriesTheDeclaredKind()
        {
            Runtime rt = new();
            rt.InjectString("Zebra", "z");
            rt.InjectInt("Apple", 1);

            List<(string Name, string Kind, string Value)> dumped = [.. rt.DumpVariables()];

            Assert.Equal(["Apple", "Zebra"], dumped.Select(d => d.Name));
            Assert.Equal("INT", dumped[0].Kind);
            Assert.Equal("1", dumped[0].Value);
        }

        [Fact]
        public void FunctionsAreCaseInsensitiveToo()
        {
            Runtime rt = new();
            rt.Functions["Build"] = new Parsing.Block([]);

            Assert.True(rt.Functions.ContainsKey("BUILD"));
        }
    }
}
