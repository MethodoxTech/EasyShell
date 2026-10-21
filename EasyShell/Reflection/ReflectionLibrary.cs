using EasyShell.Binding;
using EasyShell.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;

namespace EasyShell.Reflection
{
    /// <summary>
    /// The whole .NET runtime as a script's library: any public type by name, any public member, any assembly the name implies or LOADASSEMBLY names.
    /// </summary>
    /// <remarks>
    /// This is what makes `easy` a build tool that can reach anything, and it is exactly what NativeAOT and trimming cannot offer - a member reached only by name from a script is a member the compiler never saw used, so it is not in the binary. A build without dynamic code uses a <see cref="CuratedLibrary"/> instead; see <see cref="ShellLibraries.Default"/>.
    /// </remarks>
    [RequiresUnreferencedCode("Scripts name types and members at run time; trimming removes what nothing references.")]
    [RequiresDynamicCode("Packing a params argument makes an array of a type known only at run time.")]
    public sealed class ReflectionLibrary : IShellLibrary
    {
        #region Configurations
        private const BindingFlags StaticLookup = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
        private const BindingFlags InstanceLookup = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        #endregion

        public static ReflectionLibrary Instance { get; } = new();

        #region Methods
        public ShellType? Resolve(string typeName)
            => ResolveType(typeName) is { } type ? new ReflectedType(type) : null;

        public ShellType? TypeOf(object target)
            => new ReflectedType(target.GetType());

        /// <summary>
        /// LOADASSEMBLY &lt;name-or-path&gt; -> the simple name of what was loaded.
        ///
        /// <para>Namespace probing covers the framework and well-behaved packages, but it can only find assemblies whose name the type name predicts. A plugin DLL beside the script, or a library whose assembly name shares nothing with its namespaces, needs to be named outright - and a script that says so up front fails with "no such assembly" at the point of the load, instead of a puzzling "type not found" much later.</para>
        /// </summary>
        public string LoadAssembly(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                throw new EasyShellException("LOADASSEMBLY expects an assembly name or a path to a .dll.");

            Assembly loaded;
            try
            {
                // A real file wins over a simple name: "./Plugins/Foo.dll" is unambiguous, and LoadFrom is the only way to reach an assembly outside the probing path.
                loaded = File.Exists(nameOrPath)
                    ? Assembly.LoadFrom(Path.GetFullPath(nameOrPath))
                    : Assembly.Load(new AssemblyName(nameOrPath));
            }
            catch (Exception e)
            {
                throw new EasyShellException(
                    $"Cannot load assembly '{nameOrPath}': {e.Message} " +
                    "Give either an assembly name (System.Text.RegularExpressions) or a path to a .dll that exists.");
            }

            // A name that failed to probe before may well succeed now that something is loaded under it, so nothing stale is allowed to outlive an explicit load.
            AssemblyProbeCache.Clear();
            return loaded.GetName().Name ?? nameOrPath;
        }
        #endregion

        #region Type resolution
        private static Type? ResolveType(string typeName)
        {
            // Try loaded assemblies first
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = asm.GetType(typeName, throwOnError: false, ignoreCase: true);
                if (t is not null) return t;
            }

            // Then the assembly the name itself implies. Assembly loading is lazy, so half the framework is simply not present in a fresh process: System.Text.RegularExpressions and System.Diagnostics.FileVersionInfo both live in assemblies nothing has touched yet, and a scan of what happens to be loaded therefore reported "type not found" for types that are perfectly available. The name is the map to its own assembly - walk the namespace back one segment at a time and ask for each candidate.
            return ResolveFromProbableAssembly(typeName);
        }

        /// <summary>
        /// For "System.Text.RegularExpressions.Regex", probes the assemblies "System.Text.RegularExpressions", then "System.Text", then "System" - the convention the framework itself follows, and the one NuGet packages overwhelmingly follow too.
        /// </summary>
        private static Type? ResolveFromProbableAssembly(string typeName)
        {
            for (int cut = typeName.LastIndexOf('.'); cut > 0; cut = typeName.LastIndexOf('.', cut - 1))
            {
                Type? found = ProbeAssembly(typeName[..cut])?
                    .GetType(typeName, throwOnError: false, ignoreCase: true);
                if (found is not null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// Loads an assembly by simple name, remembering the answer - including "no such assembly".
        ///
        /// <para>The cache is what makes probing affordable. A miss costs a thrown FileNotFoundException, and command routing asks about every dotted name it sees - `python3.12` and `vim.tiny` included - so without remembering the failures, every mention of a program with a dot in its name would pay for the same exception again.</para>
        /// </summary>
        private static Assembly? ProbeAssembly(string simpleName)
            => AssemblyProbeCache.GetOrAdd(simpleName, static name =>
            {
                try
                {
                    return Assembly.Load(new AssemblyName(name));
                }
                catch
                {
                    // Not an assembly - the overwhelmingly common case, since this is also asked about ordinary program names. Absence is an answer, not an error.
                    return null;
                }
            });
        private static readonly ConcurrentDictionary<string, Assembly?> AssemblyProbeCache =
            new(StringComparer.OrdinalIgnoreCase);
        #endregion

        #region Members
        /// <summary>A type's members as reflection finds them, case-insensitively.</summary>
        [RequiresUnreferencedCode("Scripts name members at run time; trimming removes what nothing references.")]
        [RequiresDynamicCode("Packing a params argument makes an array of a type known only at run time.")]
        private sealed class ReflectedType(Type type) : ShellType
        {
            public override string FullName => type.FullName ?? type.Name;
            public override Type Type => type;

            /// <summary>
            /// Case-insensitive field lookup that survives shadowed (`new`) members instead of throwing AmbiguousMatchException out of the interpreter.
            /// </summary>
            public override ShellValueMember? FindField(string name, bool isStatic)
            {
                BindingFlags flags = isStatic ? StaticLookup : InstanceLookup;
                FieldInfo? field;
                try
                {
                    field = type.GetField(name, flags);
                }
                catch (AmbiguousMatchException)
                {
                    // GetFields lists the most derived declarations first.
                    field = type.GetFields(flags).FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                }
                return field is null ? null : new ShellValueMember(field.Name, field.GetValue);
            }

            /// <summary>
            /// Case-insensitive property lookup. Indexers and write-only properties are skipped - there is no way to read them here, and asking would throw from deep inside reflection.
            /// </summary>
            public override ShellValueMember? FindProperty(string name, bool isStatic)
            {
                BindingFlags flags = isStatic ? StaticLookup : InstanceLookup;
                PropertyInfo? prop;
                try
                {
                    prop = type.GetProperty(name, flags);
                }
                catch (AmbiguousMatchException)
                {
                    prop = type.GetProperties(flags).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                }

                return prop is not null && prop.CanRead && prop.GetIndexParameters().Length == 0
                    ? new ShellValueMember(prop.Name, prop.GetValue)
                    : null;
            }

            public override IReadOnlyList<ShellMethod> FindMethods(string name, bool isStatic)
                => [.. type
                    .GetMethods(BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance))
                    .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(ToShellMethod)];

            private static ShellMethod ToShellMethod(MethodInfo method)
            {
                ParameterInfo[] ps = method.GetParameters();
                ShellParameter[] parameters = [.. ps.Select(ToShellParameter)];

                // Deliberately array-only: `params ReadOnlySpan<T>` collections cannot be handed to MethodInfo.Invoke at all, so they must not look bindable here.
                bool hasParamArray = ps.Length > 0
                                  && ps[^1].ParameterType.IsArray
                                  && ps[^1].IsDefined(typeof(ParamArrayAttribute), inherit: false);
                Type? elementType = hasParamArray ? ps[^1].ParameterType.GetElementType() : null;

                return new ShellMethod(
                    method.Name,
                    parameters,
                    (target, args) =>
                    {
                        try
                        {
                            return method.Invoke(target, args);
                        }
                        catch (TargetInvocationException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            throw new InvocationRefusedException(e.Message, e);
                        }
                    },
                    elementType,
                    elementType is null ? null : n => Array.CreateInstance(elementType, n));
            }

            private static ShellParameter ToShellParameter(ParameterInfo p)
            {
                // The default is read only when there is one: DefaultValue throws for a few metadata shapes nobody can supply anyway.
                if (!p.HasDefaultValue)
                    return new ShellParameter(p.ParameterType);
                try
                {
                    return new ShellParameter(p.ParameterType, hasDefaultValue: true, p.DefaultValue);
                }
                catch (FormatException)
                {
                    return new ShellParameter(p.ParameterType);
                }
            }
        }
        #endregion
    }
}
