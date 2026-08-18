using System;

namespace EasyShell.Hosting
{
    /// <summary>
    /// Where the shell's own text goes and where interactive input comes from.
    ///
    /// The default implementation is System.Console. A host that embeds EasyShell somewhere that
    /// is not the process console - a virtual machine's tty, a GUI pane, a test harness - supplies
    /// its own, and every prompt, echo, result and diagnostic follows it with no code change.
    /// </summary>
    public interface IShellConsole
    {
        void Write(string text);
        void WriteLine(string text);
        /// <summary>A diagnostic line. The default writes to stderr; a terminal host may style it instead.</summary>
        void WriteErrorLine(string text);
        /// <summary>One line of input, or null on end-of-input.</summary>
        string? ReadLine();
    }
}
