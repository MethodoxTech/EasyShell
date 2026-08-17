using EasyShell.Hosting;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// The virtualization seam: a Runtime given a fake ShellHost must execute the whole language
    /// against that fake world - filesystem, console, environment, processes - and never touch
    /// the real machine. This is the contract a virtual machine embeds EasyShell through, so
    /// these tests are effectively the VM's compatibility suite.
    /// </summary>
    public class HostingTests
    {
        #region Fake world
        /// <summary>A '/'-separated in-memory filesystem - deliberately NOT host conventions.</summary>
        private sealed class FakeFileSystem : IShellFileSystem
        {
            public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
            public readonly HashSet<string> Directories = new(StringComparer.Ordinal) { "/" };
            public Func<string> GetCwd = () => "/";

            private string Resolve(string path)
            {
                if (!path.StartsWith('/')) path = GetCwd().TrimEnd('/') + "/" + path;
                List<string> parts = new();
                foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part == ".") continue;
                    if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                    parts.Add(part);
                }
                return "/" + string.Join('/', parts);
            }

            public string GetFullPath(string path) => Resolve(path);
            public string Combine(string a, string b) => a.TrimEnd('/') + "/" + b.TrimStart('/');
            public string? GetDirectoryName(string path)
            {
                int i = path.TrimEnd('/').LastIndexOf('/');
                return i <= 0 ? (path.StartsWith('/') ? "/" : null) : path[..i];
            }
            public string GetFileName(string path) => path.TrimEnd('/').Split('/')[^1];
            public string GetRelativePath(string relativeTo, string path) =>
                path.StartsWith(relativeTo, StringComparison.Ordinal)
                    ? path[relativeTo.Length..].TrimStart('/')
                    : path;

            public bool FileExists(string path) => Files.ContainsKey(Resolve(path));
            public bool DirectoryExists(string path) => Directories.Contains(Resolve(path));

            public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive)
            {
                string root = Resolve(directory).TrimEnd('/');
                string suffix = pattern.StartsWith('*') ? pattern[1..] : "";
                foreach (string file in Files.Keys.ToArray())
                {
                    if (!file.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                    if (!recursive && file[(root.Length + 1)..].Contains('/')) continue;
                    if (pattern == "*" || file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        yield return file;
                }
            }

            public IEnumerable<string> EnumerateDirectories(string directory)
            {
                string root = Resolve(directory).TrimEnd('/');
                return Directories
                    .Where(d => d.StartsWith(root + "/", StringComparison.Ordinal) && !d[(root.Length + 1)..].Contains('/'))
                    .ToArray();
            }

            public string ReadAllText(string path) => Files[Resolve(path)];
            public void WriteAllText(string path, string content) => Files[Resolve(path)] = content;

            public void CreateDirectory(string path)
            {
                string resolved = Resolve(path);
                string current = "";
                foreach (string part in resolved.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    current += "/" + part;
                    Directories.Add(current);
                }
            }

            public void DeleteFile(string path) => Files.Remove(Resolve(path));
            public void DeleteDirectory(string path)
            {
                string root = Resolve(path);
                Directories.RemoveWhere(d => d == root || d.StartsWith(root + "/", StringComparison.Ordinal));
                foreach (string file in Files.Keys.Where(f => f.StartsWith(root + "/", StringComparison.Ordinal)).ToArray())
                    Files.Remove(file);
            }
            public void CopyFile(string source, string target) => Files[Resolve(target)] = Files[Resolve(source)];
            public void MoveFile(string source, string target) { CopyFile(source, target); DeleteFile(source); }
            public void MoveDirectory(string source, string target)
            {
                string from = Resolve(source), to = Resolve(target);
                foreach (string file in Files.Keys.Where(f => f.StartsWith(from + "/", StringComparison.Ordinal)).ToArray())
                {
                    Files[to + file[from.Length..]] = Files[file];
                    Files.Remove(file);
                }
                Directories.Remove(from);
                Directories.Add(to);
            }
        }

        private sealed class FakeConsole : IShellConsole
        {
            public readonly List<string> Output = new();
            public readonly List<string> Errors = new();
            public readonly Queue<string> Input = new();
            public void Write(string text) { }
            public void WriteLine(string text) => Output.Add(text);
            public void WriteErrorLine(string text) => Errors.Add(text);
            public string? ReadLine() => Input.Count > 0 ? Input.Dequeue() : null;
        }

        private sealed class FakeEnvironment : IShellEnvironment
        {
            public readonly Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase);
            public string CurrentDirectory { get; set; } = "/";
            public string? GetVariable(string name) => Variables.TryGetValue(name, out string? v) ? v : null;
            public void SetVariable(string name, string? value)
            {
                if (value is null) Variables.Remove(name);
                else Variables[name] = value;
            }
            public string ExpandVariables(string text) => text;
        }

        private sealed class FakeProcessRunner : IShellProcessRunner
        {
            public readonly List<string> Invocations = new();
            public readonly HashSet<string> KnownPrograms = new(StringComparer.Ordinal);
            public string? Resolve(string command) => KnownPrograms.Contains(command) ? "/bin/" + command : null;
            public int RunForeground(string program, List<string> arguments, TimeSpan? timeout)
            {
                Invocations.Add($"fg:{program} {string.Join(' ', arguments)}".TrimEnd());
                return 0;
            }
            public ProcessInvoker.ProcessResult RunCaptured(string program, List<string> arguments, Action<string>? onLine, TimeSpan? timeout)
            {
                Invocations.Add($"cap:{program} {string.Join(' ', arguments)}".TrimEnd());
                onLine?.Invoke("captured-output");
                return new ProcessInvoker.ProcessResult(0, "captured-output\n", "");
            }
        }

        private static (Runtime Runtime, FakeFileSystem Fs, FakeConsole Console, FakeEnvironment Env, FakeProcessRunner Procs)
            CreateWorld(Func<string, bool>? reflectionPolicy = null)
        {
            FakeFileSystem fs = new();
            FakeConsole console = new();
            FakeEnvironment env = new();
            FakeProcessRunner procs = new();
            fs.GetCwd = () => env.CurrentDirectory;
            Runtime rt = new()
            {
                Host = new ShellHost
                {
                    Console = console,
                    FileSystem = fs,
                    Processes = procs,
                    Environment = env,
                    CanInvokeQualified = reflectionPolicy,
                },
            };
            return (rt, fs, console, env, procs);
        }

        private static void Run(Runtime rt, string script) => new EasyShellEngine(rt).Run(script, "<hosted>");
        #endregion

        [Fact]
        public void FileCommandsOperateOnTheVirtualWorldOnly()
        {
            var (rt, fs, console, _, _) = CreateWorld();

            Run(rt, """
                mkdir "/home/user"
                cd "/home/user"
                print (cwd)
                """);

            Assert.Contains("/home/user", fs.Directories);
            Assert.Equal("/home/user", console.Output[^1]);
        }

        [Fact]
        public void WriteCopyMoveDeleteRoundTripInTheFakeFs()
        {
            var (rt, fs, _, _, _) = CreateWorld();
            fs.CreateDirectory("/data");
            fs.WriteAllText("/data/one.txt", "content");

            Run(rt, """
                cp "/data/one.txt" "/data/two.txt"
                mv "/data/two.txt" "/data/three.txt"
                rm "/data/one.txt"
                """);

            Assert.False(fs.Files.ContainsKey("/data/one.txt"));
            Assert.False(fs.Files.ContainsKey("/data/two.txt"));
            Assert.Equal("content", fs.Files["/data/three.txt"]);
        }

        [Fact]
        public void RplRewritesAVirtualFile()
        {
            var (rt, fs, _, _, _) = CreateWorld();
            fs.CreateDirectory("/etc");
            fs.WriteAllText("/etc/version", "version = 0.1.0");

            Run(rt, """rpl "/etc/version" "0.1.0" "0.2.0" """);

            Assert.Equal("version = 0.2.0", fs.Files["/etc/version"]);
        }

        [Fact]
        public void TwoRuntimesKeepIndependentWorkingDirectories()
        {
            // The exact bug per-host environments exist to prevent: two sessions into the same
            // image must not share a process-global cwd.
            var a = CreateWorld();
            var b = CreateWorld();
            a.Fs.CreateDirectory("/home/alice");
            b.Fs.CreateDirectory("/home/bob");

            Run(a.Runtime, """cd "/home/alice" """);
            Run(b.Runtime, """cd "/home/bob" """);

            Assert.Equal("/home/alice", a.Env.CurrentDirectory);
            Assert.Equal("/home/bob", b.Env.CurrentDirectory);
        }

        [Fact]
        public void PrintAndErrorsGoToTheHostConsole()
        {
            var (rt, _, console, _, _) = CreateWorld();

            Run(rt, """print "into the fake" """);

            Assert.Equal("into the fake", Assert.Single(console.Output));
        }

        [Fact]
        public void EnvironmentVariablesLiveInTheHost()
        {
            var (rt, _, console, env, _) = CreateWorld();

            Run(rt, """
                setenv "GREETING" "hello"
                print (getenv "GREETING")
                """);

            Assert.Equal("hello", env.Variables["GREETING"]);
            Assert.Equal("hello", console.Output[^1]);
        }

        [Fact]
        public void ExternalProgramsGoThroughTheHostProcessTable()
        {
            var (rt, _, console, _, procs) = CreateWorld();
            procs.KnownPrograms.Add("uname");

            Run(rt, "uname -a");

            Assert.Equal("cap:uname -a", Assert.Single(procs.Invocations));
            Assert.Contains("captured-output", console.Output);
        }

        [Fact]
        public void InteractiveModeRunsProgramsInTheForeground()
        {
            var (rt, _, _, _, procs) = CreateWorld();
            procs.KnownPrograms.Add("vim");
            rt.AssignOrDeclare(Executor.InteractiveVariable, new Value(ValueKind.Bool, true));

            Run(rt, "vim notes.txt");

            Assert.Equal("fg:vim notes.txt", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void DottedProgramNamesResolveThroughTheHostNotThePath()
        {
            var (rt, _, _, _, procs) = CreateWorld();
            procs.KnownPrograms.Add("vim.tiny");

            Run(rt, "vim.tiny notes.txt");

            Assert.Equal("cap:vim.tiny notes.txt", Assert.Single(procs.Invocations));
        }

        [Fact]
        public void ReflectionPolicyBlocksQualifiedInvocation()
        {
            var (rt, _, _, _, _) = CreateWorld(reflectionPolicy: _ => false);

            var ex = Assert.Throws<Exceptions.EasyShellException>(
                () => Run(rt, """System.IO.File.WriteAllText "/tmp/escape.txt" "boo" """));
            Assert.Contains("not permitted", ex.Message);
        }

        [Fact]
        public void ReflectionPolicyCanAllowlistPureMembers()
        {
            var (rt, _, console, _, _) = CreateWorld(
                reflectionPolicy: name => name.StartsWith("System.Math.", StringComparison.Ordinal));

            Run(rt, "print (sqrt 16)");
            Assert.Equal("4", console.Output[^1]);

            Assert.Throws<Exceptions.EasyShellException>(
                () => Run(rt, """System.IO.File.ReadAllText "/etc/passwd" """));
        }

        [Fact]
        public void ScriptArgumentsArePerRuntime()
        {
            var a = CreateWorld();
            var b = CreateWorld();
            a.Runtime.ScriptArguments = ["--fast"];
            b.Runtime.ScriptArguments = ["--slow"];

            Run(a.Runtime, """print (?: (hasarg "--fast") "yes" "no")""");
            Run(b.Runtime, """print (?: (hasarg "--fast") "yes" "no")""");

            Assert.Equal("yes", a.Console.Output[^1]);
            Assert.Equal("no", b.Console.Output[^1]);
        }

        [Fact]
        public void ReplRunsAgainstTheHostConsole()
        {
            var (rt, fs, console, _, _) = CreateWorld();
            fs.CreateDirectory("/work");
            console.Input.Enqueue("INTVAR X 2");
            console.Input.Enqueue("$X = (+ $X 40)");
            console.Input.Enqueue("print (|| \"answer: \" $X)");
            // EOF ends the session.

            int code = Interactive.EasyShellRepl.Run(new Interactive.ReplOptions
            {
                Runtime = rt,
                Prompt = () => "", // prompts go through Write, which the fake discards
            });

            Assert.Equal(0, code);
            Assert.Contains("answer: 42", console.Output);
        }

        [Fact]
        public void DefaultHostIsTheRealMachine()
        {
            // The seam must be invisible when unused: a plain Runtime gets the historical world.
            Runtime rt = new();
            Assert.Same(ShellHost.Default, rt.Host);
            Assert.Equal(System.IO.Directory.GetCurrentDirectory(), rt.Host.Environment.CurrentDirectory);
        }
    }
}
