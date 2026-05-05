using EasyShell.Exceptions;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EasyShell.Reflection
{
    public static class ReflectionInvoker
    {
        // Example: System.DateTime.Now  (property)
        // Example: System.String.Format "x={0}" 5  (static method)
        // Example: System.IO.File.WriteAllText "c:/x" "hi" (static method)
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

            // Field?
            FieldInfo? field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (field is not null)
            {
                object? obj = field.GetValue(null);
                return WrapResult(obj);
            }

            // Property?
            PropertyInfo? prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (prop is not null && args.Count == 0)
            {
                object? obj = prop.GetValue(null);
                return WrapResult(obj);
            }

            // Method
            List<MethodInfo> methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (methods.Count == 0)
                throw new EasyShellException($"Static member not found: {fullyQualified}");

            (MethodInfo? best, object?[]? converted) = BindBestMethod(methods, args);
            try
            {
                object? res = best.Invoke(null, converted);
                return WrapResult(res);
            }
            catch (Exception e)
            {
                throw new EasyShellException(e.InnerException?.Message ?? e.Message);
            }
        }

        public static Value InvokeInstance(object target, string methodOrMember, List<Value> args)
        {
            Type type = target.GetType();

            // Property/field access if no args
            if (args.Count == 0)
            {
                FieldInfo? field = type.GetField(methodOrMember, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field is not null)
                    return WrapResult(field.GetValue(target));

                PropertyInfo? prop = type.GetProperty(methodOrMember, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop is not null)
                    return WrapResult(prop.GetValue(target));
            }

            List<MethodInfo> methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(methodOrMember, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (methods.Count == 0)
                throw new EasyShellException($"Instance member not found: {type.FullName}.{methodOrMember}");

            (MethodInfo? best, object?[]? converted) = BindBestMethod(methods, args);
            object? res = best.Invoke(target, converted);
            return WrapResult(res);
        }

        private static (MethodInfo method, object?[] convertedArgs) BindBestMethod(List<MethodInfo> candidates, List<Value> args)
        {
            // Simple binder: exact parameter count, then conversions score.
            List<(int score, MethodInfo m, object?[] converted)> scored = [];

            foreach (MethodInfo m in candidates)
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Count) 
                    continue;

                object?[] converted = new object?[ps.Length];
                int score = 0;
                bool ok = true;

                for (int i = 0; i < ps.Length; i++)
                {
                    Type pType = ps[i].ParameterType;
                    if (!TryConvert(args[i], pType, out object? obj, out int s))
                    {
                        ok = false; 
                        break;
                    }
                    converted[i] = obj;
                    score += s;
                }

                if (ok) 
                    scored.Add((score, m, converted));
            }

            if (scored.Count == 0)
                throw new EasyShellException($"No matching overload for ({string.Join(", ", args.Select(a => a.Kind))}).");

            // Lower score is better
            (int score, MethodInfo m, object?[] converted) best = scored.OrderBy(x => x.score).First();
            return (best.m, best.converted);
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
                long l => new Value(ValueKind.Double, (double)l),
                _ => new Value(ValueKind.Handle, res)
            };
        }

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
    }
}
