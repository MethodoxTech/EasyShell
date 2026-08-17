using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EasyShell
{
    /// <summary>
    /// Everything the operating system knows about the controlling terminal, captured as an opaque
    /// blob and put back verbatim later.
    ///
    /// <para>The moment EasyShell learned to run a program in the foreground
    /// (<see cref="ProcessInvoker.RunForeground"/>) it took on this responsibility. A full-screen
    /// program puts the tty into raw mode and is expected to put it back on the way out; one that
    /// dies without unwinding - Ctrl+C during a redraw, a crash, a `kill` from another window -
    /// does not. What the user sees afterwards is a prompt that echoes nothing and reacts to
    /// nothing, and the shell looks broken even though the shell did nothing wrong.</para>
    ///
    /// <para>The rule that makes restoration reliable is to <b>enumerate nothing</b>: snapshot the
    /// whole termios struct and the whole Windows console mode words, write them back unchanged
    /// afterwards. Whatever anyone did in between is undone by construction, without this code
    /// needing to know a single flag name. The buffer is deliberately opaque and oversized -
    /// struct termios is 60 bytes on glibc and 72 on macOS with completely different field layouts,
    /// but a tcgetattr/tcsetattr roundtrip of the same bytes never needs to know the layout.</para>
    /// </summary>
    public sealed class TerminalState
    {
        private readonly byte[]? _termios;
        private readonly uint? _outMode;
        private readonly uint? _inMode;

        private TerminalState(byte[]? termios, uint? outMode, uint? inMode)
        {
            _termios = termios;
            _outMode = outMode;
            _inMode = inMode;
        }

        /// <summary>Capture the current state. Never throws; unavailable pieces are simply null.</summary>
        public static TerminalState Capture()
        {
            (uint? outMode, uint? inMode) = SaveWindowsModes();
            return new TerminalState(SaveTermios(), outMode, inMode);
        }

        /// <summary>Put back exactly what <see cref="Capture"/> saw. Idempotent, and never throws.</summary>
        public void Restore()
        {
            RestoreWindowsModes(_outMode, _inMode);
            RestoreTermios(_termios);
        }

        #region Unix
        private const string Libc = "easyshell_libc";
        private const int TermiosBufferSize = 256;   // >= any known struct termios
        private const int TCSANOW = 0;               // 0 on both Linux and Darwin

        static TerminalState()
        {
            // DllImport("libc") does not resolve on Debian/Ubuntu, where libc.so is a linker
            // script rather than a shared object, hence the explicit resolver.
            NativeLibrary.SetDllImportResolver(typeof(TerminalState).Assembly, ResolveLibc);
        }

        private static IntPtr ResolveLibc(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Libc) return IntPtr.Zero;

            string[] candidates = OperatingSystem.IsMacOS()
                ? new[] { "libSystem.dylib", "libSystem.B.dylib" }
                : new[] { "libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc.so" };

            foreach (string c in candidates)
                if (NativeLibrary.TryLoad(c, out IntPtr h)) return h;
            return IntPtr.Zero;
        }

        [DllImport(Libc)]
        private static extern unsafe int tcgetattr(int fd, byte* termios);

        [DllImport(Libc)]
        private static extern unsafe int tcsetattr(int fd, int optionalActions, byte* termios);

        private static unsafe byte[]? SaveTermios()
        {
            if (OperatingSystem.IsWindows()) return null;
            try
            {
                byte[] buffer = new byte[TermiosBufferSize];
                fixed (byte* p = buffer)
                    if (tcgetattr(0, p) != 0) return null;   // stdin is not a tty
                return buffer;
            }
            catch
            {
                return null;   // no libc match on an exotic platform: degrade gracefully
            }
        }

        private static unsafe void RestoreTermios(byte[]? saved)
        {
            if (saved is null) return;
            try
            {
                fixed (byte* p = saved)
                    tcsetattr(0, TCSANOW, p);
            }
            catch { }
        }
        #endregion

        #region Windows
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_INPUT_HANDLE = -10;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private static (uint? OutMode, uint? InMode) SaveWindowsModes()
        {
            if (!OperatingSystem.IsWindows()) return (null, null);
            uint? o = null, i = null;
            try
            {
                if (GetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE), out uint om)) o = om;
                if (GetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), out uint im)) i = im;
            }
            catch { }
            return (o, i);
        }

        private static void RestoreWindowsModes(uint? outMode, uint? inMode)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (outMode is { } o) SetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE), o);
                if (inMode is { } i) SetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), i);
            }
            catch { }
        }
        #endregion
    }
}
