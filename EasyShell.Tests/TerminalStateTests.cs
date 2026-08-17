using Xunit;

namespace EasyShell.Tests
{
    /// <summary>
    /// The tty snapshot taken around every foreground child. What it does when it works can only be
    /// seen on a real terminal; what it must never do - throw, or refuse to load - is exactly what a
    /// test runner is well placed to check, because a runner's stdin is not a tty and the native
    /// lookup has to degrade quietly rather than take the shell down with it.
    /// </summary>
    public class TerminalStateTests
    {
        [Fact]
        public void CaptureWorksWhereThereIsNoTerminal()
        {
            // Under a test runner, and in CI, stdin is a pipe. tcgetattr fails, and that is fine.
            TerminalState state = TerminalState.Capture();
            Assert.NotNull(state);
        }

        [Fact]
        public void RestoreIsIdempotentAndNeverThrows()
        {
            TerminalState state = TerminalState.Capture();

            state.Restore();
            state.Restore();
        }

        [Fact]
        public void TheLibcResolverSurvivesDistributionsWhereLibcIsALinkerScript()
        {
            // DllImport("libc") does not resolve on Debian/Ubuntu - libc.so is a linker script, not
            // a shared object - which is why there is an explicit resolver. If it were wrong, the
            // static constructor or the first P/Invoke would throw rather than return null, and
            // every foreground command would fail on those systems.
            for (int i = 0; i < 3; i++)
                TerminalState.Capture().Restore();
        }
    }
}
