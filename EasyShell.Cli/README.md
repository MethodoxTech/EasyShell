# EasyShell REPL

The `easy` executable: argument handling, the help text, and the prompt.

It is built two ways, chosen with `-p:EasyShellBuild=Full|Native`: the Full build reaches any .NET type by reflection, and the Native build is NativeAOT and calls .NET through the curated Standard library. `easy --build` says which one is running, and the REPL's banner says it too; `--version` stays the bare version number, because the build scripts name packages after it. See the repository README's "Two builds" section.

Options (`--help`, `--version`, `--build`, `--repl`) are read only before the script path; everything after it is the script's.

`easy --repl` (or `easy` with no arguments) is a real line-editing prompt. `HostConsole`
implements `IShellLineInput` over `System.Console`, so the editor sees individual keys and gets
cursor motion, history and Tab completion; `ShellCompletionSource` is what Tab offers, namely
built-in commands, the session's `$variables`, programs on PATH, and files and folders around
the working directory. Piped input (`echo ... | easy --repl`) keeps the whole-line path, because
keys cannot be read from a pipe.

Still not a login shell: no job control, no `&`, no Ctrl+Z.
