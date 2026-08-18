using EasyShell.Exceptions;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EasyShell.Reflection
{
    public static class ReflectionInvoker
    {
        #region Configurations
        private const BindingFlags StaticLookup = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
        private const BindingFlags InstanceLookup = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        /// <summary>
        /// Score added for every parameter the caller left to its default value, so that an
        /// overload taking exactly the supplied arguments always wins over one padded with defaults.
        /// </summary>
        private const int OmittedArgumentPenalty = 40;
        /// <summary>
        /// Score added when arguments have to be packed into a `params` array. Keeps
        /// `Format(string, object)` ahead of `Format(string, params object[])` for two arguments.
        /// </summary>
        private const int ParamArrayPenalty = 60;
        #endregion

        #region Methods
        /// <summary>
        /// Would this dotted name even reach a .NET type? Command routing uses this to tell a
        /// qualified member from a program name that happens to contain dots (vim.tiny,
        /// greet.wasm, python3.12): when the type half resolves to nothing, the name is not a
        /// .NET call and belongs on the process path - where "command not found" and
        /// "permission denied" speak the user's language, instead of a reflection-policy
        /// refusal for a call that could never have existed. Resolution only; nothing invokes.
        /// </summary>
        public static bool CanResolveQualified(string fullyQualified)
        {
            int lastDot = fullyQualified.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == fullyQualified.Length - 1) return false;
            return ResolveType(fullyQualified[..lastDot]) is not null;
        }

        // Example: System.DateTime.Now                       (static property)
        // Example: System.String.Format "x={0}" 5             (static method)
        // Example: System.IO.File.WriteAllText "c:/x" "hi"    (static method)
        // Example: System.DateTime.AddDays $when 15           (instance method, $when is the target)
        // Example: System.DateTime.Year $when                 (instance property, $when is the target)
        public static Value InvokeFullyQualified(string fullyQualified, List<Value> args)
        {
            // Split into type + member by last dot.
            int lastDot = fullyQualified.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == fullyQualified.Length - 1)
                throw new EasyShellException($"Invalid member: {fullyQualified}");

            string typeName = fullyQualified[..lastDot];
            string memberName = fullyQualified[(lastDot + 1)..];

            Type type = ResolveType(typeName)
                       ?? throw new EasyShellException($"Type not found: {typeName}");

            // Static field?
            FieldInfo? field = FindField(type, memberName, StaticLookup);
            if (field is not null)
                return WrapResult(field.GetValue(null));

            // Static property?
            PropertyInfo? prop = FindProperty(type, memberName, StaticLookup);
            if (prop is not null && args.Count == 0)
                return WrapResult(prop.GetValue(null));

            // Static method?
            List<MethodInfo> statics = FindMethods(type, memberName, isStatic: true);
            if (statics.Count > 0 && TryBindBestMethod(statics, args, out MethodInfo? staticMethod, out object?[]? staticArgs))
                return InvokeChecked(staticMethod, null /*static - no target*/, staticArgs, fullyQualified);

            // Instance member, with the FIRST argument as the target:
            //   System.DateTime.AddDays $when 15   is   $when.AddDays(15)
            // This keeps the language's "function head first" shape for instance calls too, so a
            // handle no longer has to go through CALL just to reach an ordinary method.
            if (args.Count > 0 && TryInvokeInstanceMember(type, memberName, args, fullyQualified, out Value result))
                return result;

            throw new EasyShellException(
                statics.Count > 0 || args.Count > 0
                    ? $"No matching overload for {fullyQualified} ({DescribeArgs(args)})."
                    : $"Member not found: {fullyQualified}");
        }

        public static Value InvokeInstance(object target, string methodOrMember, List<Value> args)
        {
            Type type = target.GetType();
            string display = $"{type.FullName}.{methodOrMember}";

            if (TryInvokeOnTarget(type, target, methodOrMember, args, display, out Value result))
                return result;

            throw new EasyShellException($"Instance member not found: {display} ({DescribeArgs(args)}).");
        }
        #endregion

        #region Routines
        /// <summary>
        /// Invokes <paramref name="memberName"/> on the instance supplied as the first argument,
        /// e.g. `System.DateTime.AddDays $when 15`. Returns false (without throwing) when the first
        /// argument cannot serve as the target or no member matches, so the caller can report a
        /// single, complete error covering both the static and the instance attempt.
        /// </summary>
        private static bool TryInvokeInstanceMember(Type type, string memberName, List<Value> args, string display, out Value result)
        {
            result = Value.Null;

            if (!TryConvert(args[0], type, out object? target, out _) || target is null)
                return false;

            List<Value> callArgs = args.GetRange(1, args.Count - 1);
            return TryInvokeOnTarget(type, target, memberName, callArgs, display, out result);
        }

        private static bool TryInvokeOnTarget(Type type, object target, string memberName, List<Value> args, string display, out Value result)
        {
            result = Value.Null;

            // Property/field access if no args
            if (args.Count == 0)
            {
                FieldInfo? field = FindField(type, memberName, InstanceLookup);
                if (field is not null)
                {
                    result = WrapResult(field.GetValue(target));
                    return true;
                }

                PropertyInfo? prop = FindProperty(type, memberName, InstanceLookup);
                if (prop is not null)
                {
                    result = WrapResult(prop.GetValue(target));
                    return true;
                }
            }

            List<MethodInfo> methods = FindMethods(type, memberName, isStatic: false);
            if (methods.Count == 0 || !TryBindBestMethod(methods, args, out MethodInfo? method, out object?[]? converted))
                return false;

            result = InvokeChecked(method, target, converted, display);
            return true;
        }

        private static Value InvokeChecked(MethodInfo method, object? target, object?[] args, string display)
        {
            try
            {
                return WrapResult(method.Invoke(target, args));
            }
            catch (TargetInvocationException e)
            {
                // Surface what the called code complained about, not "Exception has been thrown by
                // the target of an invocation."
                throw new EasyShellException($"{display}: {e.InnerException?.Message ?? e.Message}");
            }
            catch (Exception e)
            {
                throw new EasyShellException($"Cannot invoke {display}: {e.Message}");
            }
        }

        /// <summary>
        /// Case-insensitive field lookup that survives shadowed (`new`) members instead of throwing
        /// AmbiguousMatchException out of the interpreter.
        /// </summary>
        private static FieldInfo? FindField(Type type, string name, BindingFlags flags)
        {
            try
            {
                return type.GetField(name, flags);
            }
            catch (AmbiguousMatchException)
            {
                // GetFields lists the most derived declarations first.
                return type.GetFields(flags).FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Case-insensitive property lookup. Indexers and write-only properties are skipped - there
        /// is no way to read them here, and asking would throw from deep inside reflection.
        /// </summary>
        private static PropertyInfo? FindProperty(Type type, string name, BindingFlags flags)
        {
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
                ? prop
                : null;
        }

        private static List<MethodInfo> FindMethods(Type type, string name, bool isStatic)
            => [.. type
                .GetMethods(BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance))
                .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))];

        private static bool TryBindBestMethod(
            List<MethodInfo> candidates,
            List<Value> args,
            [NotNullWhen(true)] out MethodInfo? method,
            [NotNullWhen(true)] out object?[]? convertedArgs)
        {
            // Simple binder: match parameters positionally, then pick the cheapest conversion score.
            List<(int Score, MethodInfo Method, object?[] Converted)> scored = [];

            foreach (MethodInfo m in candidates)
            {
                if (TryBind(m, args, out object?[]? converted, out int score))
                    scored.Add((score, m, converted));
            }

            if (scored.Count == 0)
            {
                method = null;
                convertedArgs = null;
                return false;
            }

            // Lower score is better
            (int Score, MethodInfo Method, object?[] Converted) best = scored.OrderBy(x => x.Score).First();
            method = best.Method;
            convertedArgs = best.Converted;
            return true;
        }

        private static bool TryBind(MethodInfo method, List<Value> args, [NotNullWhen(true)] out object?[]? converted, out int score)
        {
            ParameterInfo[] ps = method.GetParameters();

            // Straight positional match, optionally filling trailing optional parameters.
            if (args.Count <= ps.Length && TryBindFixed(ps, args, out converted, out score))
                return true;

            // params tail, e.g. String.Format(string, params object[]).
            // Deliberately array-only: `params ReadOnlySpan<T>` collections cannot be handed to
            // MethodInfo.Invoke at all, so they must not look bindable here.
            bool hasParamArray = ps.Length > 0
                              && ps[^1].ParameterType.IsArray
                              && ps[^1].IsDefined(typeof(ParamArrayAttribute), inherit: false);
            if (hasParamArray && TryBindParamArray(ps, args, out converted, out score))
                return true;

            converted = null;
            score = 0;
            return false;
        }

        private static bool TryBindFixed(ParameterInfo[] ps, List<Value> args, [NotNullWhen(true)] out object?[]? converted, out int score)
        {
            converted = null;
            score = 0;

            object?[] result = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < args.Count)
                {
                    if (!TryConvert(args[i], ps[i].ParameterType, out object? obj, out int s))
                        return false;

                    result[i] = obj;
                    score += s;
                    continue;
                }

                // Argument not supplied: only acceptable when the parameter has a default.
                if (!ps[i].HasDefaultValue)
                    return false;

                result[i] = ps[i].DefaultValue;
                score += OmittedArgumentPenalty;
            }

            converted = result;
            return true;
        }

        private static bool TryBindParamArray(ParameterInfo[] ps, List<Value> args, [NotNullWhen(true)] out object?[]? converted, out int score)
        {
            converted = null;
            score = ParamArrayPenalty;

            int fixedCount = ps.Length - 1;
            if (args.Count < fixedCount)
                return false;

            object?[] result = new object?[ps.Length];
            for (int i = 0; i < fixedCount; i++)
            {
                if (!TryConvert(args[i], ps[i].ParameterType, out object? obj, out int s))
                    return false;

                result[i] = obj;
                score += s;
            }

            Type elementType = ps[^1].ParameterType.GetElementType()
                               ?? typeof(object); // Unreachable for arrays; keeps the compiler happy
            Array rest = Array.CreateInstance(elementType, args.Count - fixedCount);
            for (int i = fixedCount; i < args.Count; i++)
            {
                if (!TryConvert(args[i], elementType, out object? obj, out int s))
                    return false;

                rest.SetValue(obj, i - fixedCount);
                score += s;
            }

            result[^1] = rest;
            converted = result;
            return true;
        }

        private static bool TryConvert(Value v, Type targetType, out object? obj, out int score)
        {
            score = 100;

            // null handling
            if (v.Kind == ValueKind.Null)
            {
                obj = null;
                score = 50;
                return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
            }

            // Nullable<T> takes whatever T takes (Convert.ChangeType cannot target Nullable<T>).
            Type? underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying is not null)
                return TryConvert(v, underlying, out obj, out score);

            // direct assignable for HANDLE
            if (v.Kind == ValueKind.Handle && v.Data is not null)
            {
                if (targetType.IsAssignableFrom(v.Data.GetType()))
                {
                    obj = v.Data;
                    score = 0;
                    return true;
                }
            }

            if (targetType == typeof(string))
            {
                obj = v.AsString();
                score = v.Kind == ValueKind.String ? 0 : 10;
                return true;
            }

            if (targetType == typeof(bool))
            {
                obj = v.AsBool();
                score = v.Kind == ValueKind.Bool ? 0 : 10;
                return true;
            }

            if (targetType == typeof(int))
            {
                obj = v.AsInt();
                score = v.Kind == ValueKind.Int ? 0 : 10;
                return true;
            }

            if (targetType == typeof(double))
            {
                obj = v.AsDouble();
                score = v.Kind == ValueKind.Double ? 0 : 10;
                return true;
            }

            // enums from string or int
            if (targetType.IsEnum)
            {
                string s = v.AsString();
                if (Enum.TryParse(targetType, s, ignoreCase: true, out object? e))
                {
                    obj = e;
                    score = 20;
                    return true;
                }
            }

            // object fallback
            if (targetType == typeof(object))
            {
                obj = v.Kind == ValueKind.Handle ? v.Data : v.AsString();
                score = 80;
                return true;
            }

            // Try Convert.ChangeType from string/double/int
            try
            {
                object? raw = v.Kind == ValueKind.Handle ? v.Data : v.AsString();
                obj = Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
                score = 90;
                return true;
            }
            catch
            {
                obj = null;
                return false;
            }
        }

        private static Value WrapResult(object? res)
        {
            if (res is null) return Value.Null;

            return res switch
            {
                string s => new Value(ValueKind.String, s),
                bool b => new Value(ValueKind.Bool, b),
                int i => new Value(ValueKind.Int, i),
                double d => new Value(ValueKind.Double, d),
                float f => new Value(ValueKind.Double, (double)f),
                // Counts and file sizes come back as long; keep them exact while they still fit.
                long l when l >= int.MinValue && l <= int.MaxValue => new Value(ValueKind.Int, (int)l),
                long l => new Value(ValueKind.Double, (double)l),
                decimal m => new Value(ValueKind.Double, (double)m),
                _ => new Value(ValueKind.Handle, res)
            };
        }

        private static string DescribeArgs(List<Value> args)
            => args.Count == 0
                ? "no arguments"
                : string.Join(", ", args.Select(a => a.Kind.ToString()));

        private static Type? ResolveType(string typeName)
        {
            // Try loaded assemblies first
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = asm.GetType(typeName, throwOnError: false, ignoreCase: true);
                if (t is not null) return t;
            }
            return null;
        }
        #endregion
    }
}
