using Xunit;

// A shell is made of process-global state, and so is a test for one: Console.In/Console.Out (the
// REPL, and `print`), the current working directory (`cd`), PATH (external program resolution) and
// the static script-argument table behind HASARG/ARG. Every test here puts back what it borrowed,
// which is only sound while no two of them are in flight at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
