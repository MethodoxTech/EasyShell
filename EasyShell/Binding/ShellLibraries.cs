using EasyShell.Binding;
using EasyShell.Reflection;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

// The curated libraries. Each line binds one type's public members through the source generator, into the library its Library names; ShellBindings is the generated class.

// Pure: computation and nothing else - no file, process, console or environment is reachable, so a sandbox can hand it to any script.
[assembly: ShellBindType(typeof(object), Library = "Pure")]
[assembly: ShellBindType(typeof(Math), Library = "Pure")]
[assembly: ShellBindType(typeof(string), Library = "Pure")]
[assembly: ShellBindType(typeof(char), Library = "Pure")]
[assembly: ShellBindType(typeof(bool), Library = "Pure")]
[assembly: ShellBindType(typeof(int), Library = "Pure")]
[assembly: ShellBindType(typeof(long), Library = "Pure")]
[assembly: ShellBindType(typeof(double), Library = "Pure")]
[assembly: ShellBindType(typeof(DateTime), Library = "Pure")]
[assembly: ShellBindType(typeof(TimeSpan), Library = "Pure")]
[assembly: ShellBindType(typeof(Guid), Library = "Pure")]
[assembly: ShellBindType(typeof(Convert), Library = "Pure")]
[assembly: ShellBindType(typeof(Regex), Library = "Pure", StaticOnly = true)]
[assembly: ShellBindType(typeof(Match), Library = "Pure")]
[assembly: ShellBindType(typeof(Group), Library = "Pure")]
[assembly: ShellBindType(typeof(GroupCollection), Library = "Pure")]
[assembly: ShellBindType(typeof(EasyShell.Commands.CommonUtilities), Library = "Pure", Include = ["JoinPath", "NormalizeSeparators"])]

// Machine: what a build script does to the real machine it runs on, beyond the host-routed built-ins. Never for a sandbox.
[assembly: ShellBindType(typeof(System.IO.Path), Library = "Machine")]
[assembly: ShellBindType(typeof(System.IO.File), Library = "Machine")]
[assembly: ShellBindType(typeof(System.IO.Directory), Library = "Machine")]
[assembly: ShellBindType(typeof(Environment), Library = "Machine")]
[assembly: ShellBindType(typeof(OperatingSystem), Library = "Machine", StaticOnly = true)]
[assembly: ShellBindType(typeof(System.Runtime.InteropServices.RuntimeInformation), Library = "Machine")]
[assembly: ShellBindType(typeof(System.Diagnostics.FileVersionInfo), Library = "Machine")]
[assembly: ShellBindType(typeof(EasyShell.Commands.CommonUtilities), Library = "Machine")]
[assembly: ShellBindType(typeof(EasyShell.Commands.ZipUtil), Library = "Machine")]

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

        /// <summary>Computation only: strings, numbers, dates, conversions, regular expressions.</summary>
        public static CuratedLibrary Pure => ShellBindings.Pure;
        /// <summary>The real machine: paths, files, directories, environment, archives.</summary>
        public static CuratedLibrary Machine => ShellBindings.Machine;
        /// <summary>Everything EasyShell binds: what `easy` calls through when it cannot reflect.</summary>
        public static CuratedLibrary Standard => _standard ??= CuratedLibrary.Combine("Standard", Pure, Machine);
        private static CuratedLibrary? _standard;

        /// <summary>
        /// The library a host gets by default: the whole runtime where reflection works, <see cref="Standard"/> where it does not.
        /// </summary>
        public static IShellLibrary Default => IsReflectionSupported ? ReflectionLibrary.Instance : Standard;
    }
}
