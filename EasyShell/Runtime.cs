using EasyShell.Exceptions;
using EasyShell.Parsing;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyShell
{
    public sealed class Runtime
    {
        private readonly Dictionary<string, Variable> _vars =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The world this runtime executes in - console, filesystem, processes, environment.
        /// Defaults to the real machine; a virtual machine supplies its own. See
        /// <see cref="Hosting.ShellHost"/>.
        ///
        /// <para>Settable because a pipeline stage temporarily swaps in a capturing console
        /// (<see cref="Hosting.ShellHost.WithConsole"/>) and restores the original afterwards.
        /// Execution is single-threaded per runtime, so the swap is never observable from
        /// outside the stage that made it.</para>
        /// </summary>
        public Hosting.ShellHost Host { get; set; } = Hosting.ShellHost.Default;

        /// <summary>
        /// Arguments passed to the running script, consumed by HASARG/ARG. Per-runtime rather
        /// than static so multiple embedded sessions cannot see each other's arguments.
        /// </summary>
        public string[] ScriptArguments { get; set; } = Array.Empty<string>();

        public readonly Dictionary<string, Block> Functions =
            new(StringComparer.OrdinalIgnoreCase);

        #region Variables
        public void Inject(string name, ValueKind kind, object? value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new EasyShellException("Injected variable name is empty.");

            // Allow injection using "$Name" or "Name"
            if (name.StartsWith("$", StringComparison.Ordinal))
                name = name[1..];

            Value v = kind switch
            {
                ValueKind.String => new Value(ValueKind.String, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
                ValueKind.Int => new Value(ValueKind.Int, Convert.ToInt32(value, CultureInfo.InvariantCulture)),
                ValueKind.Bool => new Value(ValueKind.Bool, Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
                ValueKind.Double => new Value(ValueKind.Double, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                ValueKind.Handle => new Value(ValueKind.Handle, value),
                ValueKind.Null => Value.Null,
                _ => throw new EasyShellException($"Unsupported injected kind: {kind}")
            };

            _vars[name] = new Variable(name, kind, v);
        }
        public void InjectString(string name, string value) 
            => Inject(name, ValueKind.String, value);
        public void InjectHandle(string name, object? value) 
            => Inject(name, ValueKind.Handle, value);
        public void InjectBool(string name, bool value) 
            => Inject(name, ValueKind.Bool, value);
        public void InjectInt(string name, int value) 
            => Inject(name, ValueKind.Int, value);
        public void InjectDouble(string name, double value) 
            => Inject(name, ValueKind.Double, value);
        public bool TryGetVar(string name, out Variable v) => _vars.TryGetValue(name, out v!);

        public Variable GetVar(string name)
        {
            if (!_vars.TryGetValue(name, out Variable? v))
                throw new EasyShellException($"Undefined variable: {name}");
            return v;
        }
        public void Declare(string variableType, string name, Value value)
        {
            ValueKind kind = variableType.ToUpperInvariant() switch
            {
                "STRING" => ValueKind.String,
                "INT" => ValueKind.Int,
                "BOOL" => ValueKind.Bool,
                "DOUBLE" => ValueKind.Double,
                "HANDLE" => ValueKind.Handle,
                _ => throw new EasyShellException($"Unknown type command: {variableType}")
            };

            _vars[name] = new Variable(name, kind, kind == ValueKind.Handle ? EnsureHandle(value) : value);
        }
        public void Assign(string name, Value value)
        {
            Variable v = GetVar(name);
            v.Set(value);
        }
        public void AssignOrDeclare(string name, Value value)
        {
            if (_vars.TryGetValue(name, out Variable? v))
            {
                v.Set(value);
                return;
            }

            ValueKind inferred = InferKind(value);
            _vars[name] = new Variable(name, inferred, inferred == ValueKind.Handle ? new Value(ValueKind.Handle, value.Data) : value);
        }
        public Value ResolveVarRef(string varRefToken)
        {
            // varRefToken is like "$NAME"
            string name = varRefToken[1..];
            return GetVar(name).Value;
        }
        #endregion

        #region Utilities
        /// <summary>
        /// The names currently bound, without their values. Tab completion wants exactly this and
        /// nothing more, and asking <see cref="DumpVariables"/> instead would render every value -
        /// including handles, whose ToString can be arbitrarily expensive.
        /// </summary>
        public IEnumerable<string> VariableNames => _vars.Values.Select(v => v.Name);

        public IEnumerable<(string Name, string Kind, string Value)> DumpVariables()
        {
            foreach (Variable? v in _vars.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                yield return (v.Name, v.DeclaredKind.ToString().ToUpperInvariant(), v.Value.AsString());
        }
        #endregion

        #region Helpers
        private static Value EnsureHandle(Value v)
        {
            if (v.Kind == ValueKind.Handle) return v;
            // For HANDLE declarations, commonly you want to store a real object.
            // If the value is a string/int/etc, we still store its underlying Data.
            return new Value(ValueKind.Handle, v.Data);
        }
        private static ValueKind InferKind(Value v)
        {
            return v.Kind switch
            {
                ValueKind.Int => ValueKind.Int,
                ValueKind.Bool => ValueKind.Bool,
                ValueKind.Double => ValueKind.Double,
                ValueKind.Handle => ValueKind.Handle,
                ValueKind.Null => ValueKind.String,   // treat null/empty as string
                _ => ValueKind.String
            };
        }
        #endregion
    }
}
