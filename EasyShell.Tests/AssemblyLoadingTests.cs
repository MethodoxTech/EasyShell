using EasyShell.Exceptions;
using EasyShell.Hosting;
using EasyShell.Reflection;
using EasyShell.Tests.Infrastructure;
using System;
using System.IO;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// Reaching a type whose assembly nothing has loaded yet.
    ///
    /// <para>.NET loads assemblies lazily, so a scan of <c>AppDomain.CurrentDomain.GetAssemblies()</c>
    /// describes what the process happens to have touched, not what is available to it. That made
    /// large parts of the framework unreachable for no reason a script author could see: Regex and
    /// FileVersionInfo both reported "type not found" in a fresh process, and - because an
    /// unresolvable dotted name is treated as a program - surfaced as a baffling "cannot find the
    /// file specified" from the process path instead.</para>
    /// </summary>
    public class AssemblyLoadingTests
    {
        /// <summary>Windows paths carry backslashes; scripts here are written with forward ones.</summary>
        private static string Escape(string path) => path.Replace("\\", "/");

        #region Implicit loading
        // The two types from the bug report. Both live in assemblies a plain `easy` process has no
        // reason to have loaded, and both are exactly what a build script reaches for.
        [Theory]
        [InlineData("""System.Text.RegularExpressions.Regex.IsMatch "v0.8.1" "^v[0-9]" """, "TRUE")]
        [InlineData("""System.Text.RegularExpressions.Regex.Replace "a1b2" "[0-9]" "" """, "ab")]
        public void ATypeIsReachableEvenWhenItsAssemblyWasNeverLoaded(string command, string expected)
            => Assert.Equal(expected, ScriptHost.EvaluateText(command));

        [Fact]
        public void FileVersionInfoIsReachable()
        {
            // The version of a freshly published executable is the thing a packaging script wants
            // most, and it was unreachable.
            string file = Escape(typeof(AssemblyLoadingTests).Assembly.Location);

            Assert.IsType<System.Diagnostics.FileVersionInfo>(
                ScriptHost.Evaluate($"""System.Diagnostics.FileVersionInfo.GetVersionInfo "{file}" """).AsHandle());
        }

        [Fact]
        public void ProbingWalksTheNamespaceBackToFindTheAssembly()
        {
            // ZipFile does not live in an assembly called "System.IO.Compression.ZipFile"; finding
            // it means falling back a segment, to "System.IO.Compression".
            using TempDirectory temp = new();
            temp.WriteFile("a.txt");
            string source = temp.CreateDirectory("source");
            File.WriteAllText(Path.Combine(source, "a.txt"), "x");
            string zip = temp.PathTo("a.zip");

            ScriptHost.Run($"""
                System.IO.Compression.ZipFile.CreateFromDirectory "{Escape(source)}" "{Escape(zip)}"
                """);

            Assert.True(File.Exists(zip));
        }

        [Fact]
        public void AnUnknownTypeStillReportsCleanly()
        {
            // Probing must not turn "no such type" into something worse. The name has to keep
            // falling through to the program path, which is what produces the honest error.
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => ScriptHost.Run("Contoso.NoSuchNamespace.NoSuchType.Member"));

            Assert.Contains("Contoso.NoSuchNamespace.NoSuchType.Member", e.Message);
        }

        [Fact]
        public void ADottedProgramNameIsNotMistakenForAType()
        {
            // Regression: probing is asked about every dotted command, so a program name with a
            // dot in it must not be captured by reflection on its way to the process path.
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => ScriptHost.Run("no-such-program.tiny --version"));

            Assert.DoesNotContain("Type not found", e.Message, StringComparison.OrdinalIgnoreCase);
        }
        #endregion

        #region LOADASSEMBLY
        [Fact]
        public void LoadAssemblyAnswersWithTheSimpleName()
            => Assert.Equal("System.Text.Json", ScriptHost.EvaluateText("""LOADASSEMBLY "System.Text.Json" """));

        [Fact]
        public void LoadAssemblyAcceptsAPathToADll()
        {
            // The case probing cannot serve: a DLL that is not on the probing path at all.
            string path = Escape(typeof(AssemblyLoadingTests).Assembly.Location);
            Assert.Equal("EasyShell.Tests", ScriptHost.EvaluateText($"""LOADASSEMBLY "{path}" """));
        }

        [Fact]
        public void LoadAssemblyRejectsSomethingThatIsNeither()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => ScriptHost.Run("""LOADASSEMBLY "Contoso.NotAnAssembly" """));

            // The message has to say what to do, because the two accepted spellings are the whole
            // of the fix.
            Assert.Contains("Cannot load assembly", e.Message);
            Assert.Contains("assembly name", e.Message);
        }

        [Fact]
        public void LoadAssemblyNeedsAnArgument()
            => Assert.Contains(
                "expects an assembly name",
                Assert.Throws<EasyShellException>(() => ScriptHost.Run("LOADASSEMBLY")).Message);

        [Fact]
        public void LoadAssemblyObeysTheReflectionPolicy()
        {
            // A sandboxing host that refuses reflection must not be handed an escape hatch that
            // pulls arbitrary code into the process.
            Runtime rt = new()
            {
                Host = new ShellHost
                {
                    Console = ShellHost.Default.Console,
                    FileSystem = ShellHost.Default.FileSystem,
                    Processes = ShellHost.Default.Processes,
                    Environment = ShellHost.Default.Environment,
                    CanInvokeQualified = _ => false,
                }
            };

            EasyShellException e = Assert.Throws<EasyShellException>(
                () => ScriptHost.Run("""LOADASSEMBLY "System.Text.Json" """, rt));

            Assert.Contains("not permitted", e.Message);
        }

        [Fact]
        public void LoadAssemblyIsOfferedForCompletion()
            => Assert.Contains("LOADASSEMBLY", Executor.BuiltinCommandNames);
        #endregion

        #region Direct binder surface
        [Fact]
        public void TheBinderResolvesAProbedTypeDirectly()
            => Assert.True(ReflectionInvoker.CanResolveQualified("System.Text.RegularExpressions.Regex.IsMatch"));

        [Fact]
        public void TheBinderStillRefusesANameThatIsNoType()
            => Assert.False(ReflectionInvoker.CanResolveQualified("definitely.not.a.type.Member"));
        #endregion
    }
}
