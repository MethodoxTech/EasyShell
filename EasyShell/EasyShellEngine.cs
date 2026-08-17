using EasyShell.Exceptions;
using EasyShell.Parsing;
using EasyShell.Types;
using System.Collections.Generic;
using System.Linq;

namespace EasyShell
{
    public sealed class EasyShellEngine
    {
        #region Construction
        private readonly Runtime _rt;
        public EasyShellEngine(Runtime? runtime = null)
            => _rt = runtime ?? new Runtime();
        #endregion

        #region Methods
        public int Run(string scriptText, string? origin = null)
        {
            Parser parser = new(origin);
            Block block = parser.Parse(scriptText);

            try
            {
                Executor.ExecuteBlock(_rt, block);
                return 0;
            }
            catch (ScriptReturnException)
            {
                // RETURN outside a function ends the script, as documented - not a crash.
                return 0;
            }
            catch (ScriptExitException ex)
            {
                // Record it
                Executor.SetLastExitCode(_rt, ex.ExitCode);
                return ex.ExitCode;
            }
        }
        /// <summary>
        /// REPL use.
        /// Execute a unit and return the value of the last statement if it was a CommandStmt; else Null.
        ///
        /// <para>The context that top-level command is executed in follows the runtime's interactive
        /// mode, and the distinction matters more than it looks. At a prompt, `python` is a command
        /// the person wants to <i>run</i> - a statement - so it takes the terminal and produces no
        /// value. Executed as an expression instead, its output would be captured through pipes,
        /// which is what made every interactive program exit immediately or hang. When the runtime
        /// is not interactive, RunUnit stays an evaluator: the text comes back as the value, which
        /// is what a host embedding EasyShell to compute something expects.</para>
        ///
        /// <para>Sub-expressions are unaffected either way: `$sha = (git rev-parse HEAD)` captures,
        /// because there the text really is the value.</para>
        /// </summary>
        public Value RunUnit(string unitText, string? origin = null)
        {
            bool statementContext = Executor.IsInteractive(_rt);
            Parser parser = new(origin);
            Block block = parser.Parse(unitText);

            Value last = Value.Null;
            try
            {
                foreach (Statement stmt in block.Statements)
                {
                    if (stmt is CommandStatement c)
                        last = Executor.ExecuteCommand(_rt, c.Args, c.Line, statementContext);
                    else
                        Executor.ExecuteStatement(_rt, stmt);
                }
            }
            catch (ScriptReturnException)
            {
                // RETURN typed at the prompt just ends this unit; the session stays alive.
            }
            return last;
        }
        public IEnumerable<(string Name, string Kind, string Value)> DumpVariables()
            => _rt.DumpVariables();
        public IEnumerable<string> DumpFunctions()
            => _rt.Functions.Keys.OrderBy(x => x);
        #endregion
    }
}
