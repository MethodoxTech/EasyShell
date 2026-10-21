using EasyShell.Binding;
using EasyShell.Exceptions;
using EasyShell.Hosting;
using EasyShell.Reflection;
using EasyShell.Tests.Infrastructure;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

[assembly: ShellBindType(typeof(EasyShell.Tests.BinderProbe), Library = "Probe")]

namespace EasyShell.Tests
{
    /// <summary>
    /// The curated library is what a NativeAOT `easy` and a sandbox call .NET through, so it is held to the reflective one: the same overload chosen, the same conversion made, the same result or the same complaint.
    /// </summary>
    public class CuratedLibraryTests
    {
        private static readonly CuratedLibrary Curated = CuratedLibrary.Combine("Test", ShellLibraries.Standard, ShellBindings.Probe);

        private static Value Invoke(IShellLibrary library, string member, params Value[] args)
            => ShellCallBinder.InvokeQualified(library, member, [.. args]);

        private static Value Text(string s) => new(ValueKind.String, s);
        private static Value Number(int i) => new(ValueKind.Int, i);

        #region The binder's rules, through generated bindings
        [Fact]
        public void AStaticPropertyAFieldAndAMethodAreAllReachable()
        {
            Assert.IsType<DateTime>(Invoke(Curated, "System.DateTime.Now").AsHandle());
            Assert.Equal(Math.PI, Invoke(Curated, "System.Math.PI").AsDouble());
            Assert.Equal("x=42", Invoke(Curated, "System.String.Format", Text("x={0}"), Number(42)).AsString());
        }

        [Fact]
        public void AnInstanceMemberTakesItsTargetAsTheFirstArgument()
        {
            Value later = Invoke(Curated, "System.DateTime.AddDays", new Value(ValueKind.Handle, new DateTime(2026, 1, 1)), Number(15));
            Assert.Equal(new DateTime(2026, 1, 16), Assert.IsType<DateTime>(later.AsHandle()));
            Assert.Equal(2026, Invoke(Curated, "System.DateTime.Year", later).AsInt());
            Assert.Equal("ABC", ShellCallBinder.InvokeInstance(Curated, "abc", "ToUpperInvariant", []).AsString());
        }

        [Fact]
        public void OverloadPreferencesHoldForGeneratedOverloads()
        {
            Assert.Equal("one:x", Invoke(Curated, "EasyShell.Tests.BinderProbe.Take", Text("x")).AsString());
            Assert.Equal("two:x:y", Invoke(Curated, "EasyShell.Tests.BinderProbe.Take", Text("x"), Text("y")).AsString());
            Assert.Equal("params:x:3", Invoke(Curated, "EasyShell.Tests.BinderProbe.Take", Text("x"), Text("a"), Text("b"), Text("c")).AsString());
            Assert.Equal("int:3", Invoke(Curated, "EasyShell.Tests.BinderProbe.Number", Number(3)).AsString());
            Assert.Equal("double:1.5", Invoke(Curated, "EasyShell.Tests.BinderProbe.Number", new Value(ValueKind.Double, 1.5)).AsString());
        }

        [Fact]
        public void AnOmittedArgumentTakesTheDefaultTheGeneratorWroteDown()
            => Assert.Equal("two:x:default", ShellCallBinder.InvokeQualified(ShellBindings.Probe, "EasyShell.Tests.BinderProbe.TakeDefault", [Text("x")]).AsString());

        [Fact]
        public void TheCalleesOwnComplaintIsWhatTheUserSees()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(
                () => Invoke(Curated, "System.IO.File.ReadAllText", Text("/es-no-such-file-anywhere.txt")));
            Assert.Contains("es-no-such-file-anywhere", e.Message);
            Assert.StartsWith("System.IO.File.ReadAllText: ", e.Message);
        }
        #endregion

        #region A fixed surface
        [Fact]
        public void GetTypeIsNeverBoundSoAHandleCannotReachTheTypeSystem()
        {
            EasyShellException e = Assert.Throws<EasyShellException>(() => ShellCallBinder.InvokeInstance(Curated, "abc", "GetType", []));
            Assert.Contains("Instance member not found", e.Message);
        }

        [Fact]
        public void ATypeNobodyBoundDoesNotExist()
        {
            Assert.False(ShellCallBinder.CanResolveQualified(Curated, "System.Reflection.Assembly.Load"));
            Assert.False(ShellCallBinder.CanResolveQualified(ShellLibraries.Pure, "System.IO.File.ReadAllText"));
            Assert.True(ShellCallBinder.CanResolveQualified(ShellLibraries.Pure, "System.Math.Sqrt"));
        }

        [Fact]
        public void LoadAssemblyIsRefusedBecauseTheSurfaceIsFixed()
        {
            Runtime rt = new() { Host = WithLibrary(ShellHost.Default, ShellLibraries.Pure) };
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("""LOADASSEMBLY "System.Text.Json" """, rt));
            Assert.Contains("fixed when it was built", e.Message);
        }

        [Fact]
        public void AScriptRunsTheSameOnAPureHost()
        {
            Runtime rt = new() { Host = WithLibrary(ShellHost.Default, ShellLibraries.Pure) };
            ScriptResult result = ScriptHost.Run("""
                print (sqrt 16)
                print (format "{0}-{1}" "a" 2)
                print (CALL "abc" ToUpperInvariant)
                print (System.Text.RegularExpressions.Regex.IsMatch "v12" "^v[0-9]")
                print (joinpath "a" "b")
                """, rt);

            Assert.Equal(["4", "a-2", "ABC", "TRUE", System.IO.Path.Combine("a", "b")], result.Lines);
        }

        [Fact]
        public void TheHostPolicyStillAppliesOnTopOfACuratedLibrary()
        {
            Runtime rt = new() { Host = WithLibrary(ShellHost.Default, ShellLibraries.Pure, _ => false) };
            EasyShellException e = Assert.Throws<EasyShellException>(() => ScriptHost.Run("print (sqrt 16)", rt));
            Assert.Contains("not permitted", e.Message);
        }

        [Fact]
        public void TheConsoleSwapOfAPipelineKeepsTheLibrary()
            => Assert.Same(ShellLibraries.Pure, WithLibrary(ShellHost.Default, ShellLibraries.Pure).WithConsole(ShellHost.Default.Console).Library);
        #endregion

        #region Parity with reflection
        /// <summary>
        /// Every member of the pure library, called by name through both libraries with arguments made for each of its overloads, must choose the same overload and come back with the same value or the same error.
        /// </summary>
        [Fact]
        public void EveryPureMemberBehavesAsItsReflectiveTwin()
        {
            List<string> mismatches = [];
            int calls = 0, answered = 0;
            foreach (CuratedType type in ShellLibraries.Pure.Types)
            {
                foreach ((string name, bool isStatic, List<Value> args) in CallsFor(type))
                {
                    if (Unrepeatable.Contains(name))
                        continue;
                    string qualified = $"{type.FullName}.{name}";
                    calls++;
                    string expected = Outcome(() => Invoke(ReflectionLibrary.Instance, qualified, [.. args]));
                    string actual = Outcome(() => Invoke(Curated, qualified, [.. args]));
                    if (!expected.StartsWith("error:", StringComparison.Ordinal))
                        answered++;
                    if (expected != actual)
                        mismatches.Add($"{qualified}({string.Join(", ", args.Select(a => a.Kind + ":" + a.AsString()))}): reflection {expected} / curated {actual}");
                }
            }

            // About 790 calls, of which about 690 return a value; the rest are the members' own refusals of sample arguments (an index out of range, a bad format), which must read the same too.
            Assert.True(calls > 700 && answered > 600, $"Only {calls} calls were made and {answered} answered; the matrix lost its members.");
            Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {calls} calls differ:\n" + string.Join("\n", mismatches.Distinct().Take(60)));
        }

        /// <summary>Members whose result differs between two calls by design.</summary>
        private static readonly HashSet<string> Unrepeatable = new(StringComparer.OrdinalIgnoreCase)
        {
            "Now", "UtcNow", "Today", "NewGuid", "CreateVersion7", "GetHashCode", "get_Now", "get_UtcNow", "get_Today",
        };

        private static string Outcome(Func<Value> call)
        {
            try
            {
                Value v = call();
                return $"{v.Kind}:{(v.Kind == ValueKind.Handle ? v.Data?.GetType().FullName + "=" : string.Empty)}{v.AsString()}";
            }
            catch (EasyShellException e)
            {
                return "error:" + e.Message;
            }
        }

        /// <summary>For each overload, one call whose arguments suit it - so every overload is aimed at, and the binder is free to choose another.</summary>
        private static IEnumerable<(string Name, bool IsStatic, List<Value> Args)> CallsFor(CuratedType type)
        {
            Value? receiver = SampleOf(type.Type);
            foreach (bool isStatic in new[] { true, false })
            {
                if (!isStatic && receiver is null)
                    continue;
                foreach (string name in type.MethodNames(isStatic))
                {
                    foreach (ShellMethod method in type.FindMethods(name, isStatic))
                    {
                        List<Value> args = [];
                        if (!isStatic)
                            args.Add(receiver!.Value);
                        bool ok = true;
                        foreach (ShellParameter p in method.Parameters)
                        {
                            Value? sample = SampleOf(p.Type);
                            if (sample is null) { ok = false; break; }
                            args.Add(sample.Value);
                        }
                        if (ok)
                            yield return (name, isStatic, args);
                    }
                }
            }
        }

        private static Value? SampleOf(Type type)
        {
            if (type == typeof(string)) return Text("Hello, World 12");
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return Number(2);
            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return new Value(ValueKind.Double, 2.5);
            if (type == typeof(bool)) return new Value(ValueKind.Bool, true);
            if (type == typeof(char)) return Text("e");
            if (type == typeof(object)) return Text("x");
            if (type.IsEnum) return Text(Enum.GetNames(type)[0]);
            if (type == typeof(DateTime)) return new Value(ValueKind.Handle, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            if (type == typeof(TimeSpan)) return new Value(ValueKind.Handle, new TimeSpan(1, 2, 3));
            if (type == typeof(Guid)) return new Value(ValueKind.Handle, new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"));
            if (type == typeof(System.Text.RegularExpressions.Match)) return new Value(ValueKind.Handle, System.Text.RegularExpressions.Regex.Match("v12", "([0-9])([0-9])"));
            if (type == typeof(System.Text.RegularExpressions.Group)) return new Value(ValueKind.Handle, System.Text.RegularExpressions.Regex.Match("v12", "([0-9])").Groups[1]);
            if (type == typeof(System.Text.RegularExpressions.GroupCollection)) return new Value(ValueKind.Handle, System.Text.RegularExpressions.Regex.Match("v12", "([0-9])").Groups);
            return null;
        }
        #endregion

        internal static ShellHost WithLibrary(ShellHost host, IShellLibrary library, Func<string, bool>? policy = null) => new()
        {
            Console = host.Console,
            FileSystem = host.FileSystem,
            Processes = host.Processes,
            Environment = host.Environment,
            CanInvokeQualified = policy ?? host.CanInvokeQualified,
            Library = library,
        };
    }
}
