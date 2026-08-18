# EasyShell REPL

The `easy` executable: argument handling, the help text, and the prompt.

`easy --repl` (or `easy` with no arguments) is a real line-editing prompt. `HostConsole`
implements `IShellLineInput` over `System.Console`, so the editor sees individual keys and gets
cursor motion, history and Tab completion; `ShellCompletionSource` is what Tab offers, namely
built-in commands, the session's `$variables`, programs on PATH, and files and folders around
the working directory. Piped input (`echo ... | easy --repl`) keeps the whole-line path, because
keys cannot be read from a pipe.

Still not a login shell: no job control, no `&`, no Ctrl+Z.
