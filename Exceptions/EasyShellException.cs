using System;

namespace EasyShell.Exceptions
{
    public sealed class EasyShellException : Exception
    {
        public EasyShellException(string message) : base(message) { }
    }
}
