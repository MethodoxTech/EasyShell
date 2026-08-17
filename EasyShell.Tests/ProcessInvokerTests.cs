using EasyShell.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace EasyShell.Tests
{
    public class ProcessInvokerTests
    {
        #region Capture
        [Fact]
        public void StdoutComesBackWithTheExitCode()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-run-ok", "hello");

            ProcessInvoker.ProcessResult result = ProcessInvoker.RunOnce("es-run-ok", []);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("hello", result.StdOut);
            Assert.Equal("hello", result.BestText);
        }

        [Fact]
        public void ArgumentsArePassedThroughUntouched()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-run-args", "banner");

            // No shell in between, so an argument with a space stays one argument.
            ProcessInvoker.ProcessResult result = ProcessInvoker.RunOnce("es-run-args", ["one two", "three"]);

            Assert.Contains("one two", result.StdOut);
            Assert.Contains("three", result.StdOut);
        }

        [Fact]
        public void StderrIsCapturedSeparatelyAndUsedWhenStdoutIsEmpty()
        {
            using ProgramProbe probe = new();
            probe.Stderr("es-run-err", "complaint");

            ProcessInvoker.ProcessResult result = ProcessInvoker.RunOnce("es-run-err", []);

            Assert.Equal("", result.StdOut.Trim());
            Assert.Contains("complaint", result.StdErr);
            // Shell-ish: a program that only complains still has something to say.
            Assert.Equal("complaint", result.BestText);
        }

        [Fact]
        public void ANonZeroExitCodeIsReportedRatherThanThrown()
        {
            using ProgramProbe probe = new();
            probe.Fail("es-run-fail", 7);

            Assert.Equal(7, ProcessInvoker.RunOnce("es-run-fail", []).ExitCode);
        }

        [Fact]
        public void OutputIsStreamedLineByLineAsItArrives()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-run-stream", "banner");

            List<string> lines = [];
            ProcessInvoker.RunStreaming("es-run-stream", ["a", "b"], lines.Add);

            Assert.Equal(["banner", "a", "b"], lines);
        }

        [Fact]
        public async Task TheAsyncPathCapturesTheSameThings()
        {
            using ProgramProbe probe = new();
            probe.Echo("es-run-async", "hello");

            ProcessInvoker.ProcessResult result = await ProcessInvoker.RunAsync("es-run-async", []);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("hello", result.StdOut);
        }
        #endregion

        #region Not hanging
        [Fact]
        public void AChildThatReadsStdinSeesEofInsteadOfBlocking()
        {
            // The reason stdin is redirected and closed straight away: a tool that decides to prompt
            // - for credentials, for a "press any key" - would otherwise block a build forever.
            using ProgramProbe probe = new();
            probe.ReadStdin("es-run-stdin");

            ProcessInvoker.ProcessResult result =
                ProcessInvoker.RunStreaming("es-run-stdin", [], onLine: null, timeout: TimeSpan.FromSeconds(30));

            Assert.Contains("drained", result.StdOut);
        }

        [Fact]
        public void TheTimeoutKillsTheProcessAndSaysSo()
        {
            using ProgramProbe probe = new();
            probe.Sleep("es-run-slow", 60);

            Stopwatch clock = Stopwatch.StartNew();
            TimeoutException e = Assert.Throws<TimeoutException>(
                () => ProcessInvoker.RunStreaming("es-run-slow", [], onLine: null, timeout: TimeSpan.FromSeconds(2)));
            clock.Stop();

            Assert.Contains("es-run-slow", e.Message);
            // The point of the timeout is that it returns; 30s of headroom, and still a fraction
            // of the 60s the child wanted.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        }
        #endregion

        #region Foreground
        [Fact]
        public void ForegroundExecutionReturnsTheExitCode()
        {
            // Nothing is captured here by definition - the child writes to whatever terminal we
            // are - so the exit code is the entire result.
            using ProgramProbe probe = new();
            probe.Fail("es-fg-fail", 5);

            Assert.Equal(5, ProcessInvoker.RunForeground("es-fg-fail", []));
            Assert.Equal(0, ProcessInvoker.RunForeground(probe.Echo("es-fg-ok", "hi"), []));
        }

        [Fact]
        public void ForegroundExecutionAlsoHonoursTheTimeout()
        {
            using ProgramProbe probe = new();
            probe.Sleep("es-fg-slow", 60);

            Assert.Throws<TimeoutException>(() => ProcessInvoker.RunForeground("es-fg-slow", [], TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public void TheTerminalSurvivesAForegroundChild()
        {
            // TerminalState is what keeps a program that dies in raw mode from leaving the prompt
            // unable to echo. Under a test runner stdin is not a tty at all, so the real assertion
            // is that the whole path degrades quietly rather than throwing.
            using ProgramProbe probe = new();
            probe.Echo("es-fg-terminal", "hi");

            ProcessInvoker.RunForeground("es-fg-terminal", []);
        }
        #endregion

        #region Program selection
        [Fact]
        public void AProgramIsFoundThroughPathTheSameWayTheResolverFindsIt()
        {
            // The resolved path is what starts, not the typed name: on Windows the OS launcher
            // would refuse both a dotted name and a .cmd, which are exactly the two cases
            // ProgramResolver was added to recognize.
            using ProgramProbe probe = new();
            probe.Echo("es-run.dotted", "resolved");

            Assert.Contains("resolved", ProcessInvoker.Run("es-run.dotted", []));
        }

        [Fact]
        public void AnAbsolutePathRunsWithoutAnyLookup()
        {
            using ProgramProbe probe = new(onPath: false);
            string created = probe.Echo("es-run-absolute", "direct");

            Assert.Contains("direct", ProcessInvoker.Run(created, []));
        }

        [Fact]
        public void AProgramThatIsNotThereFailsLoudly()
        {
            Exception e = Assert.ThrowsAny<Exception>(() => ProcessInvoker.RunOnce("es-no-such-program-at-all", []));
            Assert.True(e is System.ComponentModel.Win32Exception or InvalidOperationException, e.GetType().Name);
        }
        #endregion

        #region Drain grace
        [Fact]
        public void TheDrainGraceIsConfigurable()
        {
            // A grandchild holding an inherited pipe open is what makes an unbounded drain hang a
            // build; the bound has to stay adjustable by the host.
            TimeSpan original = ProcessInvoker.StreamDrainGrace;
            try
            {
                ProcessInvoker.StreamDrainGrace = TimeSpan.FromSeconds(1);
                Assert.Equal(TimeSpan.FromSeconds(1), ProcessInvoker.StreamDrainGrace);
            }
            finally
            {
                ProcessInvoker.StreamDrainGrace = original;
            }
        }
        #endregion
    }
}
