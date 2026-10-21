using System;
using System.Collections.Generic;

namespace EasyShell.Binding
{
    /// <summary>
    /// The .NET surface a script can call: `System.Math.Sqrt 2`, `CALL $When ToString "yyyy"`, and the aliases built on them.
    /// </summary>
    /// <remarks>
    /// <para>There are two kinds. <see cref="Reflection.ReflectionLibrary"/> is the whole runtime, found by name at run time - the historical behaviour, and what a build tool running with its user's authority wants. A <see cref="CuratedLibrary"/> is a fixed set of types whose members were bound at compile time by EasyShell's source generator, so it needs no reflection, survives trimming, and runs under NativeAOT; it is also a sandbox by construction, because a member nobody bound does not exist.</para>
    ///
    /// <para>Both are driven by the one binder, <see cref="ShellCallBinder"/>, which owns overload choice and argument conversion, so a call bound both ways selects the same overload and converts its arguments the same way.</para>
    /// </remarks>
    public interface IShellLibrary
    {
        /// <summary>The type a qualified name's type half names, case-insensitively, or null when this library has no such type.</summary>
        ShellType? Resolve(string typeName);

        /// <summary>The type whose instance members apply to <paramref name="target"/> - its own, or the nearest base this library knows - or null.</summary>
        ShellType? TypeOf(object target);

        /// <summary>LOADASSEMBLY: brings more types into reach, answering the loaded assembly's simple name; a library whose surface is fixed refuses.</summary>
        string LoadAssembly(string nameOrPath);
    }

    /// <summary>
    /// One type's callable members, as the binder sees them: fields and readable properties by name, and every overload of a method name in declaration order.
    /// </summary>
    public abstract class ShellType
    {
        /// <summary>The type's full name, which is what error messages and the host's policy see.</summary>
        public abstract string FullName { get; }
        /// <summary>The runtime type, which argument conversion targets when an instance is passed as the first argument.</summary>
        public abstract Type Type { get; }

        public abstract ShellValueMember? FindField(string name, bool isStatic);
        public abstract ShellValueMember? FindProperty(string name, bool isStatic);
        public abstract IReadOnlyList<ShellMethod> FindMethods(string name, bool isStatic);
    }

    /// <summary>A field or a readable, non-indexed property: something with a value and no arguments.</summary>
    public sealed class ShellValueMember(string name, Func<object?, object?> get)
    {
        public string Name { get; } = name;
        /// <summary>Reads the value from the target, which is null for a static member.</summary>
        public Func<object?, object?> Get { get; } = get;
    }

    /// <summary>A parameter as the binder needs it: what to convert an argument to, and what to pass when the argument is left out.</summary>
    public sealed class ShellParameter(Type type, bool hasDefaultValue = false, object? defaultValue = null)
    {
        public Type Type { get; } = type;
        public bool HasDefaultValue { get; } = hasDefaultValue;
        public object? DefaultValue { get; } = defaultValue;
    }

    /// <summary>
    /// One overload of a method. <see cref="Invoke"/> is handed the target (null when static) and arguments already converted to the parameters' types, a params tail already packed into its array.
    /// </summary>
    public sealed class ShellMethod(string name, ShellParameter[] parameters, Func<object?, object?[], object?> invoke, Type? paramsElementType = null, Func<int, Array>? makeParamsArray = null)
    {
        public string Name { get; } = name;
        public ShellParameter[] Parameters { get; } = parameters;
        /// <summary>The element type of a trailing <c>params T[]</c>, or null.</summary>
        public Type? ParamsElementType { get; } = paramsElementType;
        /// <summary>Makes the params array: <c>new T[n]</c> in generated code, so no array type has to be made at run time.</summary>
        public Func<int, Array>? MakeParamsArray { get; } = makeParamsArray;
        public Func<object?, object?[], object?> Invoke { get; } = invoke;
    }

    /// <summary>
    /// Thrown by a library when a call could not be made at all, as opposed to the member itself failing: the binder reports it as "Cannot invoke", where a member's own exception is reported as what the member said.
    /// </summary>
    public sealed class InvocationRefusedException(string message, Exception? inner = null) : Exception(message, inner);
}
