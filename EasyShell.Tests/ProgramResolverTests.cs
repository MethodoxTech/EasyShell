using EasyShell.Tests.Infrastructure;
using System;
using System.IO;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// One question, asked before every dotted command: is this a program, or a .NET member?
    /// Getting it wrong in either direction is a whole class of command that cannot be typed.
    /// </summary>
    public class ProgramResolverTests
    {
        [Fact]
        public void ABareNameIsFoundOnPath()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-probe-tool", "ok");

            string? resolved = ProgramResolver.Resolve("es-probe-tool");

            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
            Assert.True(ProgramResolver.Exists("es-probe-tool"));
        }

        [Fact]
        public void ADottedNameOnPathIsAProgram()
        {
            // The bug this whole type exists for: `vim.tiny`, `python3.12` and `node.exe` are
            // programs that look exactly like a .NET member invocation. Asking PATH is what tells
            // them apart, and it answers correctly in both directions.
            using ProgramProbe probe = new();
            probe.Echo("es-probe.tool", "ok");

            Assert.True(ProgramResolver.Exists("es-probe.tool"));
        }

        [Fact]
        public void ADotNetMemberIsNotAProgram()
        {
            Assert.False(ProgramResolver.Exists("System.DateTime.Now"));
            Assert.False(ProgramResolver.Exists("System.IO.File.WriteAllText"));
        }

        [Fact]
        public void SomethingThatIsNotThereResolvesToNothing()
            => Assert.Null(ProgramResolver.Resolve("es-no-such-program-anywhere"));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyInputIsNotAProgram(string command)
            => Assert.Null(ProgramResolver.Resolve(command));

        [Fact]
        public void APathIsResolvedDirectlyRatherThanThroughPath()
        {
            using ProgramProbe probe = new(onPath: false);
            string created = probe.Echo("es-probe-direct", "ok");

            // Spelled with a separator, so it must be found even though its folder is not on PATH.
            string spelled = Path.Combine(probe.Directory, Path.GetFileNameWithoutExtension(created));
            Assert.NotNull(ProgramResolver.Resolve(spelled));
            Assert.Null(ProgramResolver.Resolve(Path.GetFileNameWithoutExtension(created)));
        }

        [Fact]
        public void AMalformedPathIsAnAnswerOfNoRatherThanAnException()
        {
            // PATH entries are user data and are routinely broken; the resolver has to survive
            // whatever is in there.
            Assert.Null(ProgramResolver.Resolve("\0/nonsense"));
        }

        [Fact]
        public void NothingIsCached()
        {
            // A script that builds an executable and then runs it is an ordinary thing to write,
            // and a remembered "no" would be a genuinely baffling failure.
            using ProgramProbe probe = new();
            Assert.False(ProgramResolver.Exists("es-probe-built-later"));

            probe.Echo("es-probe-built-later", "ok");
            Assert.True(ProgramResolver.Exists("es-probe-built-later"));
        }

        [Fact]
        public void OnUnixTheExecuteBitIsWhatMakesAFileAProgram()
        {
            if (OperatingSystem.IsWindows())
                return;   // Windows has no execute bit; PATHEXT plays that role and is covered below.

            using ProgramProbe probe = new();
            File.WriteAllText(Path.Combine(probe.Directory, "es-probe-data"), "not a program");

            Assert.False(ProgramResolver.Exists("es-probe-data"));
        }

        [Fact]
        public void OnWindowsPathextSuppliesTheExtension()
        {
            if (!OperatingSystem.IsWindows())
                return;   // PATHEXT is a Windows concept.

            using ProgramProbe probe = new();
            probe.Echo("es-probe-batch", "ok");   // written as es-probe-batch.cmd

            string? resolved = ProgramResolver.Resolve("es-probe-batch");
            Assert.NotNull(resolved);
            Assert.EndsWith(".cmd", resolved, StringComparison.OrdinalIgnoreCase);
        }
    }
}
