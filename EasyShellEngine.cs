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
            catch (ScriptExitException ex)
            {
                // Record it
                if (_rt.TryGetVar("LAST_EXIT_CODE", out _))
                    _rt.Assign("LAST_EXIT_CODE", new Value(ValueKind.Int, ex.ExitCode));
                else
                    _rt.Declare("INT", "LAST_EXIT_CODE", new Value(ValueKind.Int, ex.ExitCode));

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
            foreach (Statement stmt in block.Statements)
            {
                if (stmt is CommandStatement c)
                    last = Executor.ExecuteCommand(_rt, c.Args, c.Line);
                else
                    ExecutorExecuteStmtViaPublicWrapper(stmt); // see below
            }
            return last;

            void ExecutorExecuteStmtViaPublicWrapper(Statement s)
            {
                // Alternatively: make Executor.ExecuteStmt internal/public.
                // (Minimal) Execute as a one-statement block.
                Executor.ExecuteBlock(_rt, new Block(new List<Statement> { s }));
            }
        }
        public IEnumerable<(string Name, string Kind, string Value)> DumpVariables()
            => _rt.DumpVariables();
        public IEnumerable<string> DumpFunctions()
            => _rt.Functions.Keys.OrderBy(x => x);
        #endregion
    }
}
