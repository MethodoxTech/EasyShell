namespace EasyShell.Hosting
{
    /// <summary>
    /// Working directory and environment variables.
    ///
    /// On the host these are process-global (Directory.SetCurrentDirectory, Environment.*), which
    /// is fine for one `easy` process. In a virtual machine they must be PER SESSION - two
    /// terminals into the same image each keep their own cwd - which is exactly why they are
    /// behind an interface instead of being reached directly.
    /// </summary>
    public interface IShellEnvironment
    {
        string CurrentDirectory { get; set; }
        string? GetVariable(string name);
        void SetVariable(string name, string? value);
        /// <summary>Expand %NAME%-style (host convention) environment references in a string.</summary>
        string ExpandVariables(string text);
    }
}
