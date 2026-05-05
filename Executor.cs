using EasyShell.Exceptions;
using EasyShell.Parsing;
using EasyShell.Reflection;
using EasyShell.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EasyShell
{
    public static class Executor
    {
        #region Command Mapping
        private static readonly HashSet<string> OperatorCmds =
            new(StringComparer.OrdinalIgnoreCase) { "+", "-", "*", "/", "%", "^" };
        private static readonly Dictionary<string, string> Aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Working directory
                ["cd"] = "System.IO.Directory.SetCurrentDirectory",
                ["cwd"] = "System.IO.Directory.GetCurrentDirectory",
                // Path
                ["joinpath"] = "System.IO.Path.Join",
                ["resolve"] = "System.IO.Path.GetFullPath",
                ["exists"] = "EasyShell.Commands.CommonUtilities.Exists",
                // Env
                ["setenv"] = "System.Environment.SetEnvironmentVariable",
                ["getenv"] = "System.Environment.GetEnvironmentVariable",
                // File Sys
                ["mkdir"] = "System.IO.Directory.CreateDirectory",
                ["remove"] = "EasyShell.Commands.CommonUtilities.Remove",
                ["rm"] = "EasyShell.Commands.CommonUtilities.Remove", // Shorthand
                ["cp"] = "EasyShell.Commands.CommonUtilities.Copy",
                ["mv"] = "EasyShell.Commands.CommonUtilities.Move",
                // STDIO
                ["print"] = "System.Console.WriteLine",
                // String
                ["format"] = "System.String.Format",
                // File
                ["rpl"] = "EasyShell.Commands.CommonUtilities.Replace",
                ["regrpl"] = "EasyShell.Commands.CommonUtilities.RegexReplace",
                // Zip
                ["zip"] = "EasyShell.Commands.ZipUtil.CompressArchive",
                // Math
                ["sqrt"] = "System.Math.Sqrt",
                // Date
                ["getdate"] = "System.DateTime.Now",
            };
        #endregion

        #region Methods
        public static void ExecuteBlock(Runtime rt, Block block)
        {
            foreach (Statement s in block.Statements)
                ExecuteStatement(rt, s);
        }
        private static void ExecuteStatement(Runtime rt, Statement stmt)
        {
            switch (stmt)
            {
                case FuncDefinitionStatement f:
                    rt.Functions[f.Name] = f.Body;
                    return;

                case CallFuncStatement c:
                    InvokeFunction(rt, c.Name, c.Line);
                    return;

                case AssignStatement a:
                    {
                        Value v = EvaluateArg(rt, a.ValueArg);
                        rt.AssignOrDeclare(a.VarName, v);
                        return;
                    }

                case IfStatement iff:
                    {
                        foreach ((Arg? cond, Block? body) in iff.Branches)
                        {
                            if (EvaluateCondition(rt, cond))
                            {
                                ExecuteBlock(rt, body);
                                return;
                            }
                        }
                        if (iff.ElseBody is not null)
                            ExecuteBlock(rt, iff.ElseBody);
                        return;
                    }

                case WhileStatement w:
                    {
                        while (EvaluateCondition(rt, w.Condition))
                            ExecuteBlock(rt, w.Body);
                        return;
                    }

                case CommandStatement cmd:
                    Value output = ExecuteCommand(rt, cmd.Args, cmd.Line);
                    if (output.Kind == ValueKind.String && !string.IsNullOrEmpty(output.Data as string))
                        Console.WriteLine(output.Data);
                    return;

                default:
                    throw new EasyShellException($"Unsupported statement at line {stmt.Line}.");
            }
        }

        private static bool EvaluateCondition(Runtime rt, Arg cond)
        {
            Value v = EvaluateArg(rt, cond);
            return v.AsBool();
        }

        public static Value EvaluateArg(Runtime rt, Arg arg)
        {
            return arg switch
            {
                VarRefArg vr => rt.GetVar(vr.Name).Value,
                AtomArg a => Value.FromLiteralToken(a.Text, a.WasQuoted),
                ExprArg e => ExecuteCommand(rt, e.InnerCommandArgs, e.Line),
                _ => Value.Null
            };
        }

        private static List<Value> EvalArgs(Runtime rt, IEnumerable<Arg> args)
            => args.Select(a => EvaluateArg(rt, a)).ToList();

        public static Value ExecuteCommand(Runtime rt, List<Arg> args, int line)
        {
            if (args.Count == 0)
                return Value.Null;

            // Command name must be an AtomArg or VarRefArg resolving to string.
            Value cmdNameVal = EvaluateArg(rt, args[0]);
            string cmdName = cmdNameVal.AsString();

            if (string.IsNullOrWhiteSpace(cmdName))
                return Value.Null;

            // Command alias
            if (Aliases.TryGetValue(cmdName, out string? target))
            {
                // Replace command name with fully-qualified target and invoke as usual
                // Equivalent to: target <args...>
                return ReflectionInvoker.InvokeFullyQualified(
                    target,
                    args.Skip(1).Select(a => EvaluateArg(rt, a)).ToList()
                );
            }

            // Built-in: variable declarations
            if (IsDeclarateTypedVariableCommand(cmdName))
            {
                if (args.Count < 3)
                    throw new EasyShellException($"{line}: {cmdName} syntax: {cmdName} <NAME> <VALUE>");

                string name = EvaluateArg(rt, args[1]).AsString();
                Value value = EvaluateArg(rt, args[2]);
                rt.Declare(cmdName[..^3] /*Remove "VAR"*/, name, value);
                return Value.Null;
            }

            // Built-in: CALL method on handle...
            if (cmdName.Equals("CALL", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count == 2)
                {
                    // CALL <funcName> handled earlier in parser; still allow here.
                    string fn = EvaluateArg(rt, args[1]).AsString();
                    InvokeFunction(rt, fn, line);
                    return Value.Null;
                }

                if (args.Count >= 3)
                {
                    object handle = EvaluateArg(rt, args[1]).AsHandle()
                                 ?? throw new EasyShellException($"{line}: CALL handle is null.");

                    string method = EvaluateArg(rt, args[2]).AsString();
                    List<Value> callArgs = EvalArgs(rt, args.Skip(3));

                    Value result = ReflectionInvoker.InvokeInstance(handle, method, callArgs);
                    return result;
                }

                throw new EasyShellException($"{line}: CALL syntax: CALL <funcName> OR CALL <handle> <method> [args]");
            }

            // Built-in comparisons: == != > < >= <=
            if (IsComparison(cmdName))
            {
                if (args.Count != 3)
                    throw new EasyShellException($"{line}: {cmdName} expects exactly 2 arguments.");

                Value a = EvaluateArg(rt, args[1]);
                Value b = EvaluateArg(rt, args[2]);
                bool ok = Compare(cmdName, a, b);
                return new Value(ValueKind.Bool, ok);
            }

            // Built-in arithmetic shorthands: + - * / % ^
            if (OperatorCmds.Contains(cmdName))
            {
                if (args.Count < 3 && cmdName != "-")
                    throw new EasyShellException($"{line}: {cmdName} expects at least 2 arguments.");

                List<Value> vs = args.Skip(1).Select(a => EvaluateArg(rt, a)).ToList();

                // unary minus: (- 5)
                if (cmdName == "-" && vs.Count == 1)
                    return new Value(ValueKind.Double, -vs[0].AsDouble());

                double acc = vs[0].AsDouble();
                for (int i = 1; i < vs.Count; i++)
                {
                    double b = vs[i].AsDouble();
                    acc = cmdName switch
                    {
                        "+" => acc + b,
                        "-" => acc - b,
                        "*" => acc * b,
                        "/" => acc / b,
                        "%" => acc % b,
                        "^" => Math.Pow(acc, b),
                        _ => acc
                    };
                }

                // Return INT if all inputs were ints and operator is integer-safe
                bool allInt = vs.All(v => v.Kind == ValueKind.Int);
                bool intSafe = cmdName is "+" or "-" or "*" or "%";
                if (allInt && intSafe)
                    return new Value(ValueKind.Int, (int)acc);

                return new Value(ValueKind.Double, acc);
            }

            // Built-in: NOT <value>
            if (cmdName.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 2)
                    throw new EasyShellException($"{line}: NOT expects exactly 1 argument.");

                Value v = EvaluateArg(rt, args[1]);
                return new Value(ValueKind.Bool, !v.AsBool());
            }
            // Built-in: AND <cond1> <cond2>
            if (cmdName.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 3)
                    throw new EasyShellException($"{line}: AND expects exactly 2 arguments.");

                // Short-circuit
                if (!EvaluateArg(rt, args[1]).AsBool())
                    return new Value(ValueKind.Bool, false);

                bool rhs = EvaluateArg(rt, args[2]).AsBool();
                return new Value(ValueKind.Bool, rhs);
            }
            // Built-in: OR <cond1> <cond2>
            if (cmdName.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 3)
                    throw new EasyShellException($"{line}: OR expects exactly 2 arguments.");

                // Short-circuit
                if (EvaluateArg(rt, args[1]).AsBool())
                    return new Value(ValueKind.Bool, true);

                bool rhs = EvaluateArg(rt, args[2]).AsBool();
                return new Value(ValueKind.Bool, rhs);
            }
            // Built-in: XOR <cond1> <cond2>
            if (cmdName.Equals("XOR", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 3)
                    throw new EasyShellException($"{line}: XOR expects exactly 2 arguments.");

                bool a = EvaluateArg(rt, args[1]).AsBool();
                bool b = EvaluateArg(rt, args[2]).AsBool();
                return new Value(ValueKind.Bool, a ^ b);
            }
            // Built-in: ?? <lhs> <rhs>
            if (cmdName == "??")
            {
                if (args.Count != 3)
                    throw new EasyShellException($"{line}: ?? expects exactly 2 arguments.");

                Value lhs = EvaluateArg(rt, args[1]);
                if (IsPresent(lhs))
                    return lhs;

                // Short-circuit: rhs only evaluated if needed
                return EvaluateArg(rt, args[2]);
            }
            // Built-in: ?: <condition> <trueValue> <falseValue>
            if (cmdName == "?:")
            {
                if (args.Count != 4)
                    throw new EasyShellException($"{line}: ?: expects exactly 3 arguments.");

                bool cond = EvaluateArg(rt, args[1]).AsBool();

                // Short-circuit: only evaluate selected branch
                return cond
                    ? EvaluateArg(rt, args[2])
                    : EvaluateArg(rt, args[3]);
            }

            // Built-in: ASSERT <condition> [message...]
            if (cmdName.Equals("ASSERT", StringComparison.OrdinalIgnoreCase))
            {
                // Remark: This is better implemented as built-in command than as C# call because this way we have more control over execution flow if we need
                if (args.Count < 2)
                    throw new EasyShellException($"{line}: ASSERT expects at least 1 argument.");

                bool ok = EvaluateArg(rt, args[1]).AsBool();
                if (ok)
                    return Value.Null;

                string msg = args.Count >= 3
                    ? string.Join(" ", args.Skip(2).Select(a => EvaluateArg(rt, a).AsString()))
                    : "Assertion failed.";

                throw new EasyShellException($"{line}: ASSERT failed: {msg}");
            }

            // Built-in: return (function-only)
            if (cmdName.Equals("RETURN", StringComparison.OrdinalIgnoreCase))
            {
                throw new ScriptReturnException();
            }
            // Built-in: exit [code]
            if (cmdName.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                int code = 0;

                if (args.Count >= 2)
                    code = EvaluateArg(rt, args[1]).AsInt();

                throw new ScriptExitException(code);
            }

            // C# fully qualified member invocation or access
            if (!File.Exists(cmdName) && cmdName.Contains('.', StringComparison.Ordinal))
            {
                List<Value> callArgs = EvalArgs(rt, args.Skip(1));
                return ReflectionInvoker.InvokeFullyQualified(cmdName, callArgs);
            }

            // External executable
            {
                try
                {
                    List<string> callArgs = [.. EvalArgs(rt, args.Skip(1)).Select(v => v.AsString())];
                    string output = ProcessInvoker.RunStreaming(cmdName, callArgs, Console.WriteLine);
                    return new Value(ValueKind.String, output);
                }
                catch (Exception e)
                {
                    throw new EasyShellException($"Failed to execute: {e.Message}");
                }
            }
        }
        #endregion

        #region Routines
        private static void InvokeFunction(Runtime rt, string funcName, int line)
        {
            if (!rt.Functions.TryGetValue(funcName, out Block? body))
                throw new EasyShellException($"{line}: Unknown function: {funcName}");

            try
            {
                ExecuteBlock(rt, body);
            }
            catch (ScriptReturnException)
            {
                // swallow: function-only exit
            }
        }
        #endregion

        #region Helpers
        private static bool IsPresent(Value v)
        {
            if (v.Kind == ValueKind.Null) return false;
            if (v.Kind == ValueKind.Handle) return v.AsHandle() is not null;

            // For shell ergonomics: empty string counts as "missing"
            return !string.IsNullOrEmpty(v.AsString());
        }
        private static bool IsDeclarateTypedVariableCommand(string s) =>
            s.Equals("INTVAR", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("BOOLVAR", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("STRINGVAR", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("DOUBLEVAR", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("HANDLEVAR", StringComparison.OrdinalIgnoreCase);
        private static bool IsComparison(string s) =>
            s is "==" or "!=" or ">" or "<" or ">=" or "<=";
        private static bool Compare(string op, Value a, Value b)
        {
            // Prefer numeric if both look numeric-ish
            bool aNum = a.Kind is ValueKind.Int or ValueKind.Double || (a.Kind == ValueKind.String && double.TryParse(a.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            bool bNum = b.Kind is ValueKind.Int or ValueKind.Double || (b.Kind == ValueKind.String && double.TryParse(b.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));

            if (aNum && bNum)
            {
                double da = a.AsDouble();
                double db = b.AsDouble();
                return op switch
                {
                    "==" => da == db,
                    "!=" => da != db,
                    ">" => da > db,
                    "<" => da < db,
                    ">=" => da >= db,
                    "<=" => da <= db,
                    _ => false
                };
            }

            // Bool if both parseable
            if (Value.TryParseBool(a.AsString(), out bool ba) && Value.TryParseBool(b.AsString(), out bool bb))
            {
                return op switch
                {
                    "==" => ba == bb,
                    "!=" => ba != bb,
                    ">" => (ba ? 1 : 0) > (bb ? 1 : 0),
                    "<" => (ba ? 1 : 0) < (bb ? 1 : 0),
                    ">=" => (ba ? 1 : 0) >= (bb ? 1 : 0),
                    "<=" => (ba ? 1 : 0) <= (bb ? 1 : 0),
                    _ => false
                };
            }

            // String ordinal ignore-case comparisons are usually shell-friendly
            string sa = a.AsString();
            string sb = b.AsString();
            int cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);

            return op switch
            {
                "==" => cmp == 0,
                "!=" => cmp != 0,
                ">" => cmp > 0,
                "<" => cmp < 0,
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                _ => false
            };
        }
        #endregion
    }
}
