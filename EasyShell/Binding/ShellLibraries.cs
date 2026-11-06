using EasyShell.Binding;
using EasyShell.Reflection;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

// The curated libraries. Each line binds one type's public members through the source generator, into the library its Library names; ShellBindings is the generated class.

// Computation: working with values and nothing else - no file, process, console, environment, host path convention or host clock is reachable, so a sandbox can hand it to any script, and a virtual machine's scripts see nothing of the machine underneath it. The clock is left to the host (`getdate` reads ShellHost.Clock), and so is path joining (`joinpath` uses the host filesystem's separator).
[assembly: ShellBindType(typeof(object), Library = "Computation")]
[assembly: ShellBindType(typeof(Math), Library = "Computation")]
[assembly: ShellBindType(typeof(string), Library = "Computation")]
[assembly: ShellBindType(typeof(char), Library = "Computation")]
[assembly: ShellBindType(typeof(bool), Library = "Computation")]
[assembly: ShellBindType(typeof(int), Library = "Computation")]
[assembly: ShellBindType(typeof(long), Library = "Computation")]
[assembly: ShellBindType(typeof(double), Library = "Computation")]
[assembly: ShellBindType(typeof(DateTime), Library = "Computation", Exclude = ["Now", "UtcNow", "Today"])]
[assembly: ShellBindType(typeof(TimeSpan), Library = "Computation")]
[assembly: ShellBindType(typeof(Guid), Library = "Computation")]
[assembly: ShellBindType(typeof(Convert), Library = "Computation")]
[assembly: ShellBindType(typeof(Regex), Library = "Computation", StaticOnly = true)]
[assembly: ShellBindType(typeof(Match), Library = "Computation")]
[assembly: ShellBindType(typeof(Group), Library = "Computation")]
[assembly: ShellBindType(typeof(GroupCollection), Library = "Computation")]

// HostMachine: what a build script does to the real machine it runs on, beyond the host-routed built-ins. Never for a sandbox.
[assembly: ShellBindType(typeof(DateTime), Library = "HostMachine")]
[assembly: ShellBindType(typeof(System.IO.Path), Library = "HostMachine")]
[assembly: ShellBindType(typeof(System.IO.File), Library = "HostMachine")]
[assembly: ShellBindType(typeof(System.IO.Directory), Library = "HostMachine")]
[assembly: ShellBindType(typeof(Environment), Library = "HostMachine")]
[assembly: ShellBindType(typeof(OperatingSystem), Library = "HostMachine", StaticOnly = true)]
[assembly: ShellBindType(typeof(System.Runtime.InteropServices.RuntimeInformation), Library = "HostMachine")]
[assembly: ShellBindType(typeof(System.Diagnostics.FileVersionInfo), Library = "HostMachine")]
[assembly: ShellBindType(typeof(EasyShell.Commands.CommonUtilities), Library = "HostMachine")]
[assembly: ShellBindType(typeof(EasyShell.Commands.ZipUtil), Library = "HostMachine")]

namespace EasyShell.Binding
{
    /// <summary>
    /// The libraries EasyShell ships, and the one a host gets when it names none.
    /// </summary>
    public static class ShellLibraries
    {
        /// <summary>The runtime switch that turns the reflective library off; see <see cref="IsReflectionSupported"/>.</summary>
        public const string ReflectionSwitch = "EasyShell.Reflection.IsSupported";

        /// <summary>
        /// Whether scripts may reach the whole runtime by reflection: true under the JIT unless the app turns it off, and never under NativeAOT, where dynamic code is not supported.
        /// </summary>
        /// <remarks>
        /// A trimmed or NativeAOT app should also turn it off explicitly, so the trimmer and the AOT compiler fold it to false and remove the reflective library from the binary rather than merely never calling it:
        /// <code>&lt;RuntimeHostConfigurationOption Include="EasyShell.Reflection.IsSupported" Value="false" Trim="true" /&gt;</code>
        /// </remarks>
        [FeatureSwitchDefinition(ReflectionSwitch)]
        [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
        [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
        [UnconditionalSuppressMessage("Trimming", "IL4000", Justification = "No built-in check proves unreferenced code is safe; the switch is that check, and a trimmed app sets it to false, which the trimmer folds.")]
        public static bool IsReflectionSupported => RuntimeFeature.IsDynamicCodeSupported && (AppContext.TryGetSwitch(ReflectionSwitch, out bool enabled) ? enabled : true);

        /// <summary>Computation only: strings, numbers, dates, conversions, regular expressions - but not the time of day, which is the host's (<see cref="Hosting.ShellHost.Clock"/>).</summary>
        public static CuratedLibrary Computation => ShellBindings.Computation;
        /// <summary>The real machine: its clock (all of <see cref="DateTime"/>, Now included), paths, files, directories, environment, archives.</summary>
        public static CuratedLibrary HostMachine => ShellBindings.HostMachine;
        /// <summary>Everything EasyShell binds: what `easy` calls through when it cannot reflect.</summary>
        public static CuratedLibrary Standard => _standard ??= CuratedLibrary.Combine("Standard", Computation, HostMachine);
        private static CuratedLibrary? _standard;

        /// <summary>
        /// The library a host gets by default: the whole runtime where reflection works, <see cref="Standard"/> where it does not.
        /// </summary>
        public static IShellLibrary Default => IsReflectionSupported ? ReflectionLibrary.Instance : Standard;
    }
}
