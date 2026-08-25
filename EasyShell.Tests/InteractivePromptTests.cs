using EasyShell.Exceptions;
using EasyShell.Hosting;
using System;
using System.Collections.Generic;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// Asking the person running the script a question.
    ///
    /// <para>These go through <see cref="IShellConsole"/> like every other host-routed built-in, so
    /// a script that prompts stays runnable inside a virtualized host - and so a test can type at
    /// it without borrowing the real console.</para>
    ///
    /// <para>The behaviour worth pinning is what happens when nobody is there to answer. PROMPT
    /// takes its default, because an optional question should not stop an unattended run; CHOOSE
    /// throws, because the question it exists for - which store is this build for - has no safe
    /// guess.</para>
    /// </summary>
    public class InteractivePromptTests
    {
        #region Fake console
        /// <summary>
        /// Records writes as well as lines, unlike the fake in HostingTests: half of what a prompt
        /// does is the text it puts on the screen before reading, and that arrives through Write.
        /// </summary>
        private sealed class TypingConsole : IShellConsole
        {
            public readonly List<string> Written = [];
            public readonly List<string> Errors = [];
            public readonly Queue<string> Typed = new();

            public TypingConsole(params string[] lines)
            {
                foreach (string line in lines)
                    Typed.Enqueue(line);
            }

            public void Write(string text) => Written.Add(text);
            public void WriteLine(string text) => Written.Add(text);
            public void WriteErrorLine(string text) => Errors.Add(text);
            public string? ReadLine() => Typed.Count > 0 ? Typed.Dequeue() : null;

            /// <summary>Everything shown, as one string - what the operator would have read.</summary>
            public string Screen => string.Join("\n", Written);
        }

        private static Runtime World(TypingConsole console, params string[] scriptArguments) => new()
        {
            Host = ShellHost.Default.WithConsole(console),
            ScriptArguments = scriptArguments,
        };

        private static void Run(Runtime rt, string script) => new EasyShellEngine(rt).Run(script, "<test>");
        #endregion

        #region PROMPT
        [Fact]
        public void PromptReturnsWhatWasTyped()
        {
            TypingConsole console = new("Charles");
            Runtime rt = World(console);

            Run(rt, """
                $name = (prompt "Your name?")
                print $name
                """);

            Assert.Contains("Charles", console.Screen);
            Assert.Contains("Your name? ", console.Written);
        }

        [Fact]
        public void PromptTakesTheDefaultOnEmptyInput()
        {
            TypingConsole console = new("   ");
            Runtime rt = World(console);

            Run(rt, """
                $answer = (prompt "Channel?" "Steam")
                print $answer
                """);

            Assert.Contains("Steam", console.Screen);
        }

        /// <summary>
        /// A piped or redirected session reads end-of-input immediately. An optional question must
        /// not turn that into a failure, or every prompting script becomes unrunnable in CI.
        /// </summary>
        [Fact]
        public void PromptTakesTheDefaultOnEndOfInput()
        {
            TypingConsole console = new();
            Runtime rt = World(console);

            Run(rt, """
                $answer = (prompt "Channel?" "Itch.io")
                print $answer
                """);

            Assert.Contains("Itch.io", console.Screen);
        }
        #endregion

        #region CHOOSE
        [Fact]
        public void ChooseAcceptsAnIndex()
        {
            TypingConsole console = new("2");
            Runtime rt = World(console);

            Run(rt, """
                $channel = (choose "Publish to?" "Steam" "Itch.io" "Mac App Store")
                print $channel
                """);

            Assert.Contains("Itch.io", console.Screen);
            Assert.Contains("  1) Steam", console.Written);
            Assert.Contains("  3) Mac App Store", console.Written);
        }

        [Fact]
        public void ChooseAcceptsAName()
        {
            TypingConsole console = new("mac app store");
            Runtime rt = World(console);

            Run(rt, """
                $channel = (choose "Publish to?" "Steam" "Itch.io" "Mac App Store")
                print $channel
                """);

            Assert.Contains("Mac App Store", console.Screen);
        }

        [Fact]
        public void ChooseAcceptsAnUnambiguousPrefix()
        {
            TypingConsole console = new("ste");
            Runtime rt = World(console);

            Run(rt, """
                $channel = (choose "Publish to?" "Steam" "Itch.io")
                print $channel
                """);

            Assert.Contains("Steam", console.Screen);
        }

        /// <summary>
        /// "S" fits both stores below. Resolving it to whichever was listed first would publish to
        /// the wrong one, so an ambiguous prefix is refused and the question repeats.
        /// </summary>
        [Fact]
        public void ChooseRefusesAnAmbiguousPrefixAndAsksAgain()
        {
            TypingConsole console = new("s", "Steam Demo");
            Runtime rt = World(console);

            Run(rt, """
                $channel = (choose "Publish to?" "Steam" "Steam Demo")
                print $channel
                """);

            Assert.Contains("Steam Demo", console.Screen);
            Assert.Contains(console.Errors, e => e.Contains("not one of the options"));
        }

        [Fact]
        public void ChooseReAsksAfterUnusableInput()
        {
            TypingConsole console = new("", "17", "banana", "1");
            Runtime rt = World(console);

            Run(rt, """
                $channel = (choose "Publish to?" "Steam" "Itch.io")
                print $channel
                """);

            Assert.Contains("Steam", console.Screen);
        }

        /// <summary>
        /// The load-bearing one. With no answer available, CHOOSE must stop the run rather than
        /// take the first option - otherwise an unattended publish silently ships a Steam build to
        /// the wrong storefront. The message has to name the options so the caller knows what to
        /// pass instead.
        /// </summary>
        [Fact]
        public void ChooseFailsLoudlyWhenNobodyCanAnswer()
        {
            TypingConsole console = new();
            Runtime rt = World(console);

            EasyShellException error = Assert.Throws<EasyShellException>(() => Run(rt, """
                $channel = (choose "Publish to?" "Steam" "Itch.io")
                """));

            Assert.Contains("end-of-input", error.Message);
            Assert.Contains("Steam, Itch.io", error.Message);
        }
        #endregion

        #region ARGVALUE
        [Fact]
        public void ArgValueReadsTheValueAfterAFlag()
        {
            TypingConsole console = new();
            Runtime rt = World(console, "--channel", "Itch.io", "--verbose");

            Run(rt, """
                print (argvalue "--channel")
                """);

            Assert.Contains("Itch.io", console.Screen);
        }

        [Fact]
        public void ArgValueFallsBackWhenTheFlagIsAbsentOrTrailing()
        {
            TypingConsole console = new();
            Runtime rt = World(console, "--verbose", "--channel");

            Run(rt, """
                print (argvalue "--missing" "fallback")
                print (argvalue "--channel" "fallback")
                """);

            // A flag with nothing after it is the same as not supplying it - there is no value to read.
            Assert.Equal(["fallback", "fallback"], console.Written);
        }

        /// <summary>
        /// The pairing this was added for: ask only when the answer was not supplied on the
        /// command line, so one script covers both the interactive and the unattended run.
        /// </summary>
        [Fact]
        public void ArgValueCombinesWithChooseToSkipThePrompt()
        {
            TypingConsole console = new();
            Runtime rt = World(console, "--channel", "Steam");

            Run(rt, """
                $channel = (?? (argvalue "--channel") (choose "Publish to?" "Steam" "Itch.io"))
                print $channel
                """);

            Assert.Contains("Steam", console.Screen);
            // The prompt never ran, so nothing was asked.
            Assert.DoesNotContain("Publish to?", console.Screen);
        }
        #endregion
    }
}
