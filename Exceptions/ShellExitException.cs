using System;

namespace EasyShell.Exceptions
{
    public sealed class ScriptExitException : Exception
    {
        public int ExitCode { get; }

        public ScriptExitException(int exitCode = 0)
        {
            ExitCode = exitCode;
        }
    }
}
