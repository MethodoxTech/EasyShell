using EasyShell.Binding;
using EasyShell.Types;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace EasyShell.Reflection
{
    /// <summary>
    /// The reflective library's calls, kept under their historical names for code that called them directly. The language itself now calls through its host's <see cref="Hosting.ShellHost.Library"/>.
    /// </summary>
    [RequiresUnreferencedCode("Scripts name types and members at run time; trimming removes what nothing references.")]
    [RequiresDynamicCode("Packing a params argument makes an array of a type known only at run time.")]
    public static class ReflectionInvoker
    {
        /// <summary>Would this dotted name reach a .NET type? See <see cref="ShellCallBinder.CanResolveQualified"/>.</summary>
        public static bool CanResolveQualified(string fullyQualified)
            => ShellCallBinder.CanResolveQualified(ReflectionLibrary.Instance, fullyQualified);

        /// <summary>LOADASSEMBLY. See <see cref="ReflectionLibrary.LoadAssembly"/>.</summary>
        public static string LoadAssembly(string nameOrPath)
            => ReflectionLibrary.Instance.LoadAssembly(nameOrPath);

        public static Value InvokeFullyQualified(string fullyQualified, List<Value> args)
            => ShellCallBinder.InvokeQualified(ReflectionLibrary.Instance, fullyQualified, args);

        public static Value InvokeInstance(object target, string methodOrMember, List<Value> args)
            => ShellCallBinder.InvokeInstance(ReflectionLibrary.Instance, target, methodOrMember, args);
    }
}
