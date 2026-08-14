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
        /// </summary>
        public Value RunUnit(string unitText, string? origin = null)
        {
            Parser parser = new(origin);
            Block block = parser.Parse(unitText);

            Value last = Value.Null;
            try
            {
                foreach (Statement stmt in block.Statements)
                {
                    if (stmt is CommandStatement c)
                        last = Executor.ExecuteCommand(_rt, c.Args, c.Line);
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
