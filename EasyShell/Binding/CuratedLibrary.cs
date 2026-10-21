using EasyShell.Exceptions;
using System;
using System.Collections.Generic;

namespace EasyShell.Binding
{
    /// <summary>
    /// A fixed set of types bound ahead of time: what NativeAOT, a trimmed build, and a sandbox all call .NET through.
    /// </summary>
    /// <remarks>
    /// <para>Its types come from <c>[assembly: ShellBindType(typeof(T))]</c>, which EasyShell's source generator turns into direct calls, so nothing is looked up, loaded or invoked by reflection. A member nobody bound is simply absent - "Type not found" or "Member not found", as if it did not exist - which makes the set an allowlist that cannot be walked around: there is no <c>GetType</c> to start from, and no <c>Assembly</c> to reach.</para>
    ///
    /// <para>Libraries combine, so a host composes the surface it wants: <see cref="ShellLibraries.Pure"/> for computation, plus <see cref="ShellLibraries.Machine"/> where scripts may touch the real machine, plus its own generated set.</para>
    /// </remarks>
    public sealed class CuratedLibrary : IShellLibrary
    {
        private readonly Dictionary<string, CuratedType> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Type, CuratedType> _byType = [];

        public CuratedLibrary(string name) => Name = name;

        /// <summary>A name for diagnostics.</summary>
        public string Name { get; }
        public IEnumerable<CuratedType> Types => _byName.Values;

        /// <summary>Adds a bound type; a later binding of the same type replaces an earlier one.</summary>
        public CuratedLibrary Add(CuratedType type)
        {
            _byName[type.FullName] = type;
            _byType[type.Type] = type;
            return this;
        }

        /// <summary>A library holding every type of these, later ones winning where two bind the same type.</summary>
        public static CuratedLibrary Combine(string name, params CuratedLibrary[] libraries)
        {
            CuratedLibrary combined = new(name);
            foreach (CuratedLibrary library in libraries)
                foreach (CuratedType type in library.Types)
                    combined.Add(type);
            return combined;
        }

        public ShellType? Resolve(string typeName)
            => _byName.TryGetValue(typeName, out CuratedType? type) ? type : null;

        public ShellType? TypeOf(object target)
        {
            // The runtime type, or the nearest base this library binds: a RuntimeType answers as System.Object would, if Object is bound at all.
            for (Type? t = target.GetType(); t is not null; t = t.BaseType)
                if (_byType.TryGetValue(t, out CuratedType? type))
                    return type;
            return null;
        }

        public string LoadAssembly(string nameOrPath)
            => throw new EasyShellException(
                $"Cannot load assembly '{nameOrPath}': this shell's .NET library ({Name}) was fixed when it was built, so nothing more can be loaded into it.");
    }

    /// <summary>
    /// One bound type: its members by name, case-insensitively, each method name with its overloads in the order the type declares them.
    /// </summary>
    /// <remarks>Filled by generated code; a host may also add a member by hand.</remarks>
    public sealed class CuratedType(string fullName, Type type) : ShellType
    {
        private readonly Dictionary<string, ShellValueMember> _staticFields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ShellValueMember> _instanceFields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ShellValueMember> _staticProperties = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ShellValueMember> _instanceProperties = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ShellMethod>> _staticMethods = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ShellMethod>> _instanceMethods = new(StringComparer.OrdinalIgnoreCase);

        public override string FullName { get; } = fullName;
        public override Type Type { get; } = type;

        public CuratedType Field(bool isStatic, string name, Func<object?, object?> get)
        {
            // First wins, as reflection's lookup finds the most derived declaration first.
            (isStatic ? _staticFields : _instanceFields).TryAdd(name, new ShellValueMember(name, get));
            return this;
        }

        public CuratedType Property(bool isStatic, string name, Func<object?, object?> get)
        {
            (isStatic ? _staticProperties : _instanceProperties).TryAdd(name, new ShellValueMember(name, get));
            return this;
        }

        public CuratedType Method(bool isStatic, ShellMethod method)
        {
            Dictionary<string, List<ShellMethod>> methods = isStatic ? _staticMethods : _instanceMethods;
            if (!methods.TryGetValue(method.Name, out List<ShellMethod>? overloads))
                methods[method.Name] = overloads = [];
            overloads.Add(method);
            return this;
        }

        /// <summary>Every bound method name, for a host listing what scripts can call.</summary>
        public IEnumerable<string> MethodNames(bool isStatic) => (isStatic ? _staticMethods : _instanceMethods).Keys;

        public override ShellValueMember? FindField(string name, bool isStatic)
            => (isStatic ? _staticFields : _instanceFields).GetValueOrDefault(name);

        public override ShellValueMember? FindProperty(string name, bool isStatic)
            => (isStatic ? _staticProperties : _instanceProperties).GetValueOrDefault(name);

        public override IReadOnlyList<ShellMethod> FindMethods(string name, bool isStatic)
            => (isStatic ? _staticMethods : _instanceMethods).TryGetValue(name, out List<ShellMethod>? overloads) ? overloads : [];
    }

    /// <summary>
    /// Binds a type's public members into a curated library: <c>[assembly: ShellBindType(typeof(System.Math), Library = "Pure")]</c>.
    /// </summary>
    /// <remarks>
    /// EasyShell's source generator reads this and writes the bindings into the assembly's <c>ShellBindings</c> class, one static property per library name; nothing reads it at run time. Static members are bound as the type declares them, instance members with those it inherits, as reflection's public lookup sees them. A member that cannot be called without reflection - a generic method, a by-ref or by-ref-like parameter, a pointer - or that is marked as needing unreferenced or dynamic code is left out, and <c>GetType</c> is always left out, so a handle can never become a way into the type system.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class ShellBindTypeAttribute(Type type) : Attribute
    {
        public Type Type { get; } = type;
        /// <summary>The library the type belongs to: the name of the generated property it is registered under.</summary>
        public string Library { get; set; } = "Standard";
        /// <summary>Only these members, by name.</summary>
        public string[]? Include { get; set; }
        /// <summary>Not these members, by name.</summary>
        public string[]? Exclude { get; set; }
        /// <summary>Static members only: for a type whose instances a script should not be able to drive.</summary>
        public bool StaticOnly { get; set; }
    }
}
