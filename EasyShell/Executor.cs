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
        #region Configurations
        /// <summary>
        /// Script-settable variable. When TRUE, a non-zero exit code from an external program is
        /// recorded in $LAST_EXIT_CODE but does not abort the script.
        /// </summary>
        public const string ContinueOnErrorVariable = "EasyContinueOnError";
        /// <summary>
        /// Script-settable variable. Wall-clock limit in seconds for a single external program;
        /// 0 or unset means no limit. On expiry the whole process tree is killed.
        /// </summary>
        public const string ProcessTimeoutVariable = "EasyProcessTimeoutSeconds";
        /// <summary>Set after every external program invocation.</summary>
        public const string LastExitCodeVariable = "LAST_EXIT_CODE";
        /// <summary>
        /// Script-settable variable. When TRUE, an external program used as a STATEMENT runs in the
        /// foreground on the host's own terminal instead of being captured through pipes - which is
        /// the difference between `python`, `pwsh` and `vim` working and them exiting immediately or
        /// hanging. Expression context is unaffected: `$sha = (git rev-parse HEAD)` must still
        /// capture, whatever this is set to.
        ///
        /// Default FALSE, because the captured path is the right one for a build script: closing
        /// the child's stdin is what stops a tool that decides to prompt from blocking CI forever.
        /// An interactive host turns it on - <see cref="Interactive.EasyShellRepl"/> does.
        /// </summary>
        public const string InteractiveVariable = "EasyInteractive";
        #endregion

        #region Command Mapping
        private static readonly HashSet<string> OperatorCmds =
            new(StringComparer.OrdinalIgnoreCase) { "+", "-", "*", "/", "%", "^" };
        private static readonly HashSet<string> ComparisonCmds =
            new(StringComparer.OrdinalIgnoreCase) { "==", "!=", ">", "<", ">=", "<=" };
        private static readonly HashSet<string> ConcatCmds =
            new(StringComparer.OrdinalIgnoreCase) { "||", "CONCAT", "APPEND" };
        private static readonly HashSet<string> DeclarationCmds =
            new(StringComparer.OrdinalIgnoreCase) { "INTVAR", "BOOLVAR", "STRINGVAR", "DOUBLEVAR", "HANDLEVAR" };
        /// <summary>Recognized inline by <see cref="ExecuteCommand"/>, so listed here only for the completion view.</summary>
        private static readonly HashSet<string> KeywordCmds =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "NOT", "AND", "OR", "XOR", "??", "?:",
                "RUN", "CALL", "ASSERT", "RETURN", "EXIT",
                "IF", "ELSEIF", "ELSE", "WHILE", "FUNC", "END",
            };

        /// <summary>
        /// Every name the language itself answers to, for a host that needs to offer them - Tab
        /// completion is the reason this exists. Assembled from the very tables the dispatcher
        /// consults, so a command added to one of them appears here without a second edit, and the
        /// block keywords are included because they are things you type at a prompt too.
        ///
        /// <para>Built on demand rather than in a field initializer: the tables it reads live in
        /// several regions of this class, and static initializers run in declaration order.</para>
        /// </summary>
        public static IReadOnlyList<string> BuiltinCommandNames => _builtinCommandNames ??= BuildBuiltinCommandNames();
        private static IReadOnlyList<string>? _builtinCommandNames;
        private static IReadOnlyList<string> BuildBuiltinCommandNames()
        {
            SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            names.UnionWith(OperatorCmds);
            names.UnionWith(ComparisonCmds);
            names.UnionWith(ConcatCmds);
            names.UnionWith(DeclarationCmds);
            names.UnionWith(KeywordCmds);
            names.UnionWith(HostBuiltins);
            names.UnionWith(Aliases.Keys);
            return [.. names];
        }
        /// <summary>
        /// Reflection-backed aliases. Only names whose targets are PURE (no machine state) or
        /// deliberately host-only (zip) remain here; everything that touches filesystem, console,
        /// environment or working directory is a host-routed built-in now - see
        /// <see cref="ExecuteHostBuiltin"/> - so that a virtualized <see cref="Hosting.ShellHost"/>
        /// carries the whole command set with it.
        /// </summary>
        private static readonly Dictionary<string, string> Aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Path arithmetic (pure)
                // Remark: Deliberately NOT System.IO.Path.Join - see CommonUtilities.JoinPath for why.
                ["joinpath"] = "EasyShell.Commands.CommonUtilities.JoinPath",
                // String
                ["format"] = "System.String.Format",
                // Zip (host-only by design: operates on real archives; a sandboxing host blocks it
                // via CanInvokeQualified)
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
        public static void ExecuteStatement(Runtime rt, Statement stmt)
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
                    // Statement context: an external program streams its output live, so there is
                    // nothing left to echo afterwards (doing both printed everything twice).
                    Value output = ExecuteCommand(rt, cmd.Args, cmd.Line, statementContext: true);
                    if (output.Kind == ValueKind.String && output.Data is string text && !string.IsNullOrEmpty(text))
                        rt.Host.Console.WriteLine(text);
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

        public static Value ExecuteCommand(Runtime rt, List<Arg> args, int line, bool statementContext = false)
        {
            if (args.Count == 0)
                return Value.Null;

            // Pipes and redirection are recognized here, in the one funnel both statements and
            // parenthesized expressions pass through, so `ls | wc -l` reads the same as a
            // statement and as `$n = (ls | wc -l)`. See Pipelines for the head-position rule
            // that keeps `(> $a $b)` a comparison.
            if (Pipelines.Uses(args))
                return Pipelines.Execute(rt, args, line, statementContext);

            // Command name must be an AtomArg or VarRefArg resolving to string.
            Value cmdNameVal = EvaluateArg(rt, args[0]);
            string cmdName = cmdNameVal.AsString();

            if (string.IsNullOrWhiteSpace(cmdName))
                return Value.Null;

            // Built-in: RUN <program> [args...]
            // The escape hatch past our own table: `print`, `rm`, `cp`, `mv` and `zip` are aliases
            // here and real programs on PATH, so without RUN an alias shadows its program forever.
            if (cmdName.Equals("RUN", StringComparison.OrdinalIgnoreCase) && args.Count >= 2)
            {
                List<string> runArgs = [.. EvalArgs(rt, args.Skip(1)).Select(v => v.AsString())];
                return ExecuteExternal(rt, runArgs[0], runArgs.GetRange(1, runArgs.Count - 1), line, statementContext);
            }

            // Host-routed built-ins: filesystem, console, environment, working directory.
            if (IsHostBuiltin(cmdName))
                return ExecuteHostBuiltin(rt, cmdName, EvalArgs(rt, args.Skip(1)), line);

            // Command alias
            if (Aliases.TryGetValue(cmdName, out string? target))
            {
                // Replace command name with fully-qualified target and invoke as usual
                // Equivalent to: target <args...>
                return InvokeQualified(rt, target, EvalArgs(rt, args.Skip(1)), line);
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
                    // A handle is the usual target, but any non-null value can take an instance
                    // call - CALL $someString ToUpper is perfectly reasonable.
                    Value receiver = EvaluateArg(rt, args[1]);
                    object handle = receiver.AsHandle() ?? receiver.Data
                                 ?? throw new EasyShellException($"{line}: CALL target is null.");

                    string method = EvaluateArg(rt, args[2]).AsString();
                    List<Value> callArgs = EvalArgs(rt, args.Skip(3));

                    return InvokeOnHandle(rt, handle, method, callArgs, line);
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

            // Built-in string concatenation: || <a> <b> [...]  (also CONCAT / APPEND)
            if (IsConcat(cmdName))
            {
                if (args.Count < 2)
                    throw new EasyShellException($"{line}: {cmdName} expects at least 1 argument.");

                return Concat(EvalArgs(rt, args.Skip(1)));
            }

            // Built-in arithmetic shorthands: + - * / % ^
            if (OperatorCmds.Contains(cmdName))
            {
                if (args.Count < 3 && cmdName != "-")
                    throw new EasyShellException($"{line}: {cmdName} expects at least 2 arguments.");

                List<Value> vs = EvalArgs(rt, args.Skip(1));

                // '+' doubles as string concatenation the moment an operand is not a number, so
                // (+ "Build-" $Tag) reads the way people expect. Adding non-numbers used to
                // silently produce 0. Use || when concatenation is what you always mean.
                if (cmdName == "+" && !vs.All(IsNumeric))
                    return Concat(vs);

                // Every remaining operator is arithmetic and nothing else, so an operand that is
                // not a number is a mistake and gets said out loud. Coercing it to 0 instead is
                // how `-= $a 1` used to look like it worked before `-=` was a token: the stray
                // '=' became 0 and the subtraction went ahead. A shell that quietly invents an
                // operand computes the wrong answer without ever admitting to it.
                foreach (Value operand in vs)
                    if (!IsNumeric(operand))
                        throw new EasyShellException(
                            $"{line}: {cmdName} expects numbers; {Describe(operand)} is not one.");

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

            // C# fully qualified member invocation or access - static, or instance with the first
            // argument as the target: System.DateTime.AddDays $when 15
            //
            // A dotted name is ambiguous - `vim.tiny`, `python3.12` and `node.exe` are programs -
            // so it is a .NET call only when nothing runnable answers to it AND the name's type
            // half actually resolves. Without the resolution check, a typo'd or not-yet-
            // executable program (greet.wasm before chmod +x) fell into reflection and died as
            // a POLICY refusal - "'greet.wasm' is not permitted" - when the honest errors,
            // "command not found" or "permission denied", live on the process path.
            if (!rt.Host.FileSystem.FileExists(cmdName) && cmdName.Contains('.', StringComparison.Ordinal)
                && rt.Host.Processes.Resolve(cmdName) is null
                && ReflectionInvoker.CanResolveQualified(cmdName))
                return InvokeQualified(rt, cmdName, EvalArgs(rt, args.Skip(1)), line);

            // External executable
            return ExecuteExternal(rt, cmdName,
                                   [.. EvalArgs(rt, args.Skip(1)).Select(v => v.AsString())],
                                   line, statementContext);
        }

        /// <summary>
        /// Run an external program, choosing between the foreground and the captured invocation.
        ///
        /// The choice is not a preference, it is a semantic requirement in both directions.
        /// Expression context - `$sha = (git rev-parse HEAD)` - must capture, because the text is
        /// the value. Statement context has no value to produce, so the only question is whether
        /// the child should be given the terminal; an interactive host says yes, and that is the
        /// difference between being able to type `vim` at a prompt and watching it hang.
        /// </summary>
        public static Value ExecuteExternal(Runtime rt, string program, List<string> arguments, int line, bool statementContext)
        {
            if (statementContext && IsInteractive(rt))
            {
                int foregroundExit;
                try
                {
                    foregroundExit = rt.Host.Processes.RunForeground(program, arguments, GetProcessTimeout(rt));
                }
                catch (Exception e)
                {
                    throw new EasyShellException($"{line}: Failed to execute '{program}': {e.Message}");
                }

                CheckExitCode(rt, program, foregroundExit, line);
                return Value.Null;   // the child wrote to the terminal itself; there is nothing to echo
            }

            ProcessInvoker.ProcessResult result;
            try
            {
                // In statement context stream live so long builds show progress; in expression
                // context stay quiet, because the caller wants the text as a value.
                Action<string>? sink = statementContext ? rt.Host.Console.WriteLine : null;
                result = rt.Host.Processes.RunCaptured(program, arguments, sink, GetProcessTimeout(rt));
            }
            catch (Exception e)
            {
                throw new EasyShellException($"{line}: Failed to execute '{program}': {e.Message}");
            }

            CheckExitCode(rt, program, result.ExitCode, line);

            // Nothing to hand back in statement context - it has already been printed.
            return statementContext
                ? Value.Null
                : new Value(ValueKind.String, result.BestText);
        }

        /// <summary>Record the exit code so scripts can inspect it, then abort unless told not to.</summary>
        internal static void CheckExitCode(Runtime rt, string program, int exitCode, int line)
        {
            SetLastExitCode(rt, exitCode);

            if (exitCode != 0 && !IsContinueOnError(rt))
                throw new EasyShellException(
                    $"{line}: '{program}' exited with code {exitCode}. " +
                    $"Set ${ContinueOnErrorVariable} to TRUE to ignore non-zero exit codes.");
        }
        #endregion

        #region Host built-ins
        private static readonly HashSet<string> HostBuiltins = new(StringComparer.OrdinalIgnoreCase)
        {
            "cd", "cwd", "resolve", "exists", "setenv", "getenv", "hasarg", "arg",
            "mkdir", "remove", "rm", "removeall", "cp", "mv", "print", "rpl", "regrpl",
        };

        private static bool IsHostBuiltin(string cmdName) => HostBuiltins.Contains(cmdName);

        /// <summary>
        /// The commands that touch the machine, routed through <see cref="Runtime.Host"/>. These
        /// were reflection aliases onto System.IO/System.Console statics; as built-ins they behave
        /// identically on the default host and follow the host into any virtual world.
        /// </summary>
        private static Value ExecuteHostBuiltin(Runtime rt, string cmdName, List<Value> args, int line)
        {
            Hosting.ShellHost host = rt.Host;

            string Arg(int i, string what)
            {
                if (i >= args.Count)
                    throw new EasyShellException($"{line}: {cmdName.ToUpperInvariant()} expects {what}.");
                return args[i].AsString();
            }

            switch (cmdName.ToLowerInvariant())
            {
                case "cd":
                {
                    string target = host.Environment.ExpandVariables(Arg(0, "a directory path"));
                    string resolved = host.FileSystem.GetFullPath(target);
                    if (!host.FileSystem.DirectoryExists(resolved))
                        throw new EasyShellException($"{line}: Directory not found: {resolved}");
                    host.Environment.CurrentDirectory = resolved;
                    return Value.Null;
                }
                case "cwd":
                    return new Value(ValueKind.String, host.Environment.CurrentDirectory);
                case "resolve":
                    return new Value(ValueKind.String, host.FileSystem.GetFullPath(
                        host.Environment.ExpandVariables(Arg(0, "a path"))));
                case "exists":
                    return new Value(ValueKind.Bool, Hosting.ShellBuiltins.Exists(host, Arg(0, "a path")));
                case "setenv":
                    host.Environment.SetVariable(Arg(0, "a name and a value"), Arg(1, "a value"));
                    return Value.Null;
                case "getenv":
                {
                    string? value = host.Environment.GetVariable(Arg(0, "a name"));
                    return value is null ? Value.Null : new Value(ValueKind.String, value);
                }
                case "hasarg":
                {
                    string flag = Arg(0, "a flag name");
                    bool has = rt.ScriptArguments.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
                    return new Value(ValueKind.Bool, has);
                }
                case "arg":
                {
                    int index = args.Count > 0 ? args[0].AsInt() : throw new EasyShellException($"{line}: ARG expects an index.");
                    string value = index >= 0 && index < rt.ScriptArguments.Length ? rt.ScriptArguments[index] : string.Empty;
                    return new Value(ValueKind.String, value);
                }
                case "mkdir":
                    host.FileSystem.CreateDirectory(host.FileSystem.GetFullPath(
                        host.Environment.ExpandVariables(Arg(0, "a directory path"))));
                    return Value.Null;
                case "remove" or "rm":
                    return new Value(ValueKind.Bool, Hosting.ShellBuiltins.Remove(host, Arg(0, "a path")));
                case "removeall":
                {
                    bool recursive = args.Count < 3 || args[2].AsBool();
                    int removed = Hosting.ShellBuiltins.RemoveAll(host, Arg(0, "a folder and a pattern"), Arg(1, "a pattern"), recursive);
                    return new Value(ValueKind.Int, removed);
                }
                case "cp":
                    return new Value(ValueKind.Bool, Hosting.ShellBuiltins.Copy(host, Arg(0, "a source and a target"), Arg(1, "a target")));
                case "mv":
                    return new Value(ValueKind.Bool, Hosting.ShellBuiltins.Move(host, Arg(0, "a source and a target"), Arg(1, "a target")));
                case "print":
                {
                    // Mirrors Console.WriteLine overload behavior: one argument prints verbatim,
                    // several treat the first as a format string.
                    if (args.Count == 0) { host.Console.WriteLine(string.Empty); return Value.Null; }
                    string text = args.Count == 1
                        ? args[0].AsString()
                        : string.Format(args[0].AsString(), args.Skip(1).Select(v => (object)v.AsString()).ToArray());
                    host.Console.WriteLine(text);
                    return Value.Null;
                }
                case "rpl":
                    Hosting.ShellBuiltins.Replace(host, Arg(0, "a file, a search text and a replacement"), Arg(1, "a search text"), Arg(2, "a replacement"));
                    return Value.Null;
                case "regrpl":
                    Hosting.ShellBuiltins.RegexReplace(host, Arg(0, "a file, a pattern and a replacement"), Arg(1, "a pattern"), Arg(2, "a replacement"));
                    return Value.Null;
                default:
                    throw new EasyShellException($"{line}: Unknown built-in '{cmdName}'.");
            }
        }
        #endregion

        #region Routines
        /// <summary>
        /// Invokes a fully-qualified .NET member, tagging failures with the script line - reflection
        /// itself has no idea which line asked for it.
        /// </summary>
        private static Value InvokeQualified(Runtime rt, string fullyQualified, List<Value> args, int line)
        {
            // The reflection policy is the sandbox line: a virtualizing host that cannot allow
            // arbitrary .NET access refuses here, and the refusal reads as an ordinary script
            // error rather than a crash.
            if (rt.Host.CanInvokeQualified is { } permits && !permits(fullyQualified))
                throw new EasyShellException($"{line}: '{fullyQualified}' is not permitted in this environment.");

            try
            {
                return ReflectionInvoker.InvokeFullyQualified(fullyQualified, args);
            }
            catch (Exception e) when (IsReportableCallFailure(e))
            {
                throw new EasyShellException($"{line}: {e.Message}");
            }
        }
        /// <summary>
        /// Same, for `CALL &lt;handle&gt; &lt;method&gt; [args]`.
        /// </summary>
        private static Value InvokeOnHandle(Runtime rt, object handle, string method, List<Value> args, int line)
        {
            // Instance reflection is the same escape hatch as qualified reflection and MUST go
            // through the same policy - otherwise `CALL "" GetType` walks String -> Type ->
            // Assembly -> GetType("System.IO.File") -> Invoke and reaches arbitrary host members
            // that `System.IO.File.WriteAllText ...` (which routes through InvokeQualified) would
            // have refused. The gate name is the CONCRETE receiver type plus the member, so a
            // policy that allows only pure namespaces stops the pivot at its first hop: the
            // returned Type is a System.RuntimeType, and no further CALL on it is permitted.
            if (rt.Host.CanInvokeQualified is { } permits)
            {
                string qualified = $"{handle.GetType().FullName}.{method}";
                if (!permits(qualified))
                    throw new EasyShellException($"{line}: '{qualified}' is not permitted in this environment.");
            }

            try
            {
                return ReflectionInvoker.InvokeInstance(handle, method, args);
            }
            catch (Exception e) when (IsReportableCallFailure(e))
            {
                throw new EasyShellException($"{line}: {e.Message}");
            }
        }
        /// <summary>
        /// A failed .NET call should read as a script error with a line number, not as an unhandled
        /// crash - except for the exceptions that ARE the script's control flow.
        /// </summary>
        private static bool IsReportableCallFailure(Exception e)
            => e is not (ScriptExitException or ScriptReturnException);
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
        public static void SetLastExitCode(Runtime rt, int code)
        {
            Value v = new(ValueKind.Int, code);
            if (rt.TryGetVar(LastExitCodeVariable, out _))
                rt.Assign(LastExitCodeVariable, v);
            else
                rt.Declare("INT", LastExitCodeVariable, v);
        }
        private static bool IsContinueOnError(Runtime rt)
            => rt.TryGetVar(ContinueOnErrorVariable, out Variable? v) && v.Value.AsBool();
        /// <summary>Whether this runtime is attached to a person at a terminal. See <see cref="InteractiveVariable"/>.</summary>
        public static bool IsInteractive(Runtime rt)
            => rt.TryGetVar(InteractiveVariable, out Variable? v) && v.Value.AsBool();
        internal static TimeSpan? GetProcessTimeout(Runtime rt)
        {
            if (!rt.TryGetVar(ProcessTimeoutVariable, out Variable? v))
                return null;

            double seconds = v.Value.AsDouble();
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
        private static bool IsPresent(Value v)
        {
            if (v.Kind == ValueKind.Null) return false;
            if (v.Kind == ValueKind.Handle) return v.AsHandle() is not null;

            // For shell ergonomics: empty string counts as "missing"
            return !string.IsNullOrEmpty(v.AsString());
        }
        private static bool IsDeclarateTypedVariableCommand(string s) => DeclarationCmds.Contains(s);
        private static bool IsComparison(string s) => ComparisonCmds.Contains(s);
        private static bool IsConcat(string s) => ConcatCmds.Contains(s);
        private static Value Concat(List<Value> values)
            => new(ValueKind.String, string.Concat(values.Select(v => v.AsString())));
        /// <summary>
        /// Whether a value can take part in arithmetic. Strings count when they parse as a number,
        /// which is how "10" has always behaved in (+ "10" 1).
        /// </summary>
        private static bool IsNumeric(Value v) =>
            v.Kind is ValueKind.Int or ValueKind.Double or ValueKind.Bool ||
            (v.Kind == ValueKind.String &&
             double.TryParse(v.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        /// <summary>An operand as a diagnostic should show it: quoted, or named when it has no text at all.</summary>
        private static string Describe(Value v)
        {
            string text = v.Kind == ValueKind.Null ? string.Empty : v.AsString();
            return string.IsNullOrEmpty(text) ? "an empty value" : $"'{text}'";
        }
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
