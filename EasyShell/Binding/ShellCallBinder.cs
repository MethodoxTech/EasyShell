using EasyShell.Exceptions;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EasyShell.Binding
{
    /// <summary>
    /// Calls a member of an <see cref="IShellLibrary"/>: member lookup in the language's order, overload choice by conversion cost, and argument conversion.
    /// </summary>
    /// <remarks>
    /// This was <c>ReflectionInvoker</c>'s algorithm, lifted off <c>System.Reflection</c> so the reflective library and a generated, curated one are bound by the same rules - which is what lets a script behave the same under the JIT and under NativeAOT.
    /// </remarks>
    public static class ShellCallBinder
    {
        #region Configurations
        /// <summary>
        /// Score added for every parameter the caller left to its default value, so that an overload taking exactly the supplied arguments always wins over one padded with defaults.
        /// </summary>
        private const int OmittedArgumentPenalty = 40;
        /// <summary>
        /// Score added when arguments have to be packed into a `params` array. Keeps `Format(string, object)` ahead of `Format(string, params object[])` for two arguments.
        /// </summary>
        private const int ParamArrayPenalty = 60;
        #endregion

        #region Methods
        /// <summary>
        /// Would this dotted name even reach a .NET type? Command routing uses this to tell a qualified member from a program name that happens to contain dots (vim.tiny, greet.wasm, python3.12): when the type half resolves to nothing, the name is not a .NET call and belongs on the process path - where "command not found" and "permission denied" speak the user's language, instead of a policy refusal for a call that could never have existed. Resolution only; nothing invokes.
        /// </summary>
        public static bool CanResolveQualified(IShellLibrary library, string fullyQualified)
        {
            int lastDot = fullyQualified.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == fullyQualified.Length - 1) return false;
            return library.Resolve(fullyQualified[..lastDot]) is not null;
        }

        // Example: System.DateTime.Now                       (static property)
        // Example: System.String.Format "x={0}" 5             (static method)
        // Example: System.IO.File.WriteAllText "c:/x" "hi"    (static method)
        // Example: System.DateTime.AddDays $when 15           (instance method, $when is the target)
        // Example: System.DateTime.Year $when                 (instance property, $when is the target)
        public static Value InvokeQualified(IShellLibrary library, string fullyQualified, List<Value> args)
        {
            // Split into type + member by last dot.
            int lastDot = fullyQualified.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == fullyQualified.Length - 1)
                throw new EasyShellException($"Invalid member: {fullyQualified}");

            string typeName = fullyQualified[..lastDot];
            string memberName = fullyQualified[(lastDot + 1)..];

            ShellType type = library.Resolve(typeName)
                           ?? throw new EasyShellException($"Type not found: {typeName}");

            // Static field?
            ShellValueMember? field = type.FindField(memberName, isStatic: true);
            if (field is not null)
                return WrapResult(field.Get(null));

            // Static property?
            ShellValueMember? prop = type.FindProperty(memberName, isStatic: true);
            if (prop is not null && args.Count == 0)
                return WrapResult(prop.Get(null));

            // Static method?
            IReadOnlyList<ShellMethod> statics = type.FindMethods(memberName, isStatic: true);
            if (statics.Count > 0 && TryBindBestMethod(statics, args, out ShellMethod? staticMethod, out object?[]? staticArgs))
                return InvokeChecked(staticMethod, null /*static - no target*/, staticArgs, fullyQualified);

            // Instance member, with the FIRST argument as the target:
            //   System.DateTime.AddDays $when 15   is   $when.AddDays(15)
            // This keeps the language's "function head first" shape for instance calls too, so a handle no longer has to go through CALL just to reach an ordinary method.
            if (args.Count > 0 && TryInvokeInstanceMember(type, memberName, args, fullyQualified, out Value result))
                return result;

            throw new EasyShellException(
                statics.Count > 0 || args.Count > 0
                    ? $"No matching overload for {fullyQualified} ({DescribeArgs(args)})."
                    : $"Member not found: {fullyQualified}");
        }

        public static Value InvokeInstance(IShellLibrary library, object target, string methodOrMember, List<Value> args)
        {
            string display = $"{target.GetType().FullName}.{methodOrMember}";

            ShellType? type = library.TypeOf(target);
            if (type is not null && TryInvokeOnTarget(type, target, methodOrMember, args, display, out Value result))
                return result;

            throw new EasyShellException($"Instance member not found: {display} ({DescribeArgs(args)}).");
        }

        /// <summary>
        /// Converts a script value to a parameter's type, scoring the conversion: 0 is exact, higher is costlier, and false means it cannot be done. Public so a host binding something by hand converts exactly as a bound member does.
        /// </summary>
        public static bool TryConvert(Value v, Type targetType, out object? obj, out int score)
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

        /// <summary>A .NET result as a script value: the language's own kinds where one fits, a handle otherwise.</summary>
        public static Value WrapResult(object? res)
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
        #endregion

        #region Routines
        /// <summary>
        /// Invokes <paramref name="memberName"/> on the instance supplied as the first argument, e.g. `System.DateTime.AddDays $when 15`. Returns false (without throwing) when the first argument cannot serve as the target or no member matches, so the caller can report a single, complete error covering both the static and the instance attempt.
        /// </summary>
        private static bool TryInvokeInstanceMember(ShellType type, string memberName, List<Value> args, string display, out Value result)
        {
            result = Value.Null;

            if (!TryConvert(args[0], type.Type, out object? target, out _) || target is null)
                return false;

            List<Value> callArgs = args.GetRange(1, args.Count - 1);
            return TryInvokeOnTarget(type, target, memberName, callArgs, display, out result);
        }

        private static bool TryInvokeOnTarget(ShellType type, object target, string memberName, List<Value> args, string display, out Value result)
        {
            result = Value.Null;

            // Property/field access if no args
            if (args.Count == 0)
            {
                ShellValueMember? field = type.FindField(memberName, isStatic: false);
                if (field is not null)
                {
                    result = WrapResult(field.Get(target));
                    return true;
                }

                ShellValueMember? prop = type.FindProperty(memberName, isStatic: false);
                if (prop is not null)
                {
                    result = WrapResult(prop.Get(target));
                    return true;
                }
            }

            IReadOnlyList<ShellMethod> methods = type.FindMethods(memberName, isStatic: false);
            if (methods.Count == 0 || !TryBindBestMethod(methods, args, out ShellMethod? method, out object?[]? converted))
                return false;

            result = InvokeChecked(method, target, converted, display);
            return true;
        }

        private static Value InvokeChecked(ShellMethod method, object? target, object?[] args, string display)
        {
            try
            {
                return WrapResult(method.Invoke(target, args));
            }
            catch (TargetInvocationException e)
            {
                // Surface what the called code complained about, not "Exception has been thrown by the target of an invocation."
                throw new EasyShellException($"{display}: {e.InnerException?.Message ?? e.Message}");
            }
            catch (InvocationRefusedException e)
            {
                // The call never reached the member: reflection refused it (an open generic, a by-ref parameter).
                throw new EasyShellException($"Cannot invoke {display}: {e.Message}");
            }
            catch (Exception e) when (e is not (ScriptExitException or ScriptReturnException))
            {
                // A generated binding calls the member directly, so the callee's own exception arrives as itself, and reads as the reflective path's unwrapped TargetInvocationException does.
                throw new EasyShellException($"{display}: {e.Message}");
            }
        }

        private static bool TryBindBestMethod(
            IReadOnlyList<ShellMethod> candidates,
            List<Value> args,
            [NotNullWhen(true)] out ShellMethod? method,
            [NotNullWhen(true)] out object?[]? convertedArgs)
        {
            // Simple binder: match parameters positionally, then pick the cheapest conversion score.
            List<(int Score, ShellMethod Method, object?[] Converted)> scored = [];

            foreach (ShellMethod m in candidates)
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
            (int Score, ShellMethod Method, object?[] Converted) best = scored.OrderBy(x => x.Score).First();
            method = best.Method;
            convertedArgs = best.Converted;
            return true;
        }

        private static bool TryBind(ShellMethod method, List<Value> args, [NotNullWhen(true)] out object?[]? converted, out int score)
        {
            ShellParameter[] ps = method.Parameters;

            // Straight positional match, optionally filling trailing optional parameters.
            if (args.Count <= ps.Length && TryBindFixed(ps, args, out converted, out score))
                return true;

            // params tail, e.g. String.Format(string, params object[]).
            // Deliberately array-only: `params ReadOnlySpan<T>` collections cannot be handed to a call through object[] at all, so they must not look bindable here.
            if (method.ParamsElementType is not null && method.MakeParamsArray is not null
                && TryBindParamArray(ps, method.ParamsElementType, method.MakeParamsArray, args, out converted, out score))
                return true;

            converted = null;
            score = 0;
            return false;
        }

        private static bool TryBindFixed(ShellParameter[] ps, List<Value> args, [NotNullWhen(true)] out object?[]? converted, out int score)
        {
            converted = null;
            score = 0;

            object?[] result = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < args.Count)
                {
                    if (!TryConvert(args[i], ps[i].Type, out object? obj, out int s))
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

        private static bool TryBindParamArray(ShellParameter[] ps, Type elementType, Func<int, Array> makeArray, List<Value> args, [NotNullWhen(true)] out object?[]? converted, out int score)
        {
            converted = null;
            score = ParamArrayPenalty;

            int fixedCount = ps.Length - 1;
            if (args.Count < fixedCount)
                return false;

            object?[] result = new object?[ps.Length];
            for (int i = 0; i < fixedCount; i++)
            {
                if (!TryConvert(args[i], ps[i].Type, out object? obj, out int s))
                    return false;

                result[i] = obj;
                score += s;
            }

            Array rest = makeArray(args.Count - fixedCount);
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

        private static string DescribeArgs(List<Value> args)
            => args.Count == 0
                ? "no arguments"
                : string.Join(", ", args.Select(a => a.Kind.ToString()));
        #endregion
    }
}
