## Extending Easy Shell

Potential additions:

* Arithmetic commands: `+`, `-`, `*`, `/`
* Boolean ops: `AND`, `OR`, `NOT`
* `RETURN` keyword for function short-circuiting
* Better process invocation on Windows (e.g., `cmd /c` fallback)
* Module import (`IMPORT path.es`)

## TODO

Current:

- [ ] Test with more real-world use cases
- [x] ~~Draft build script~~ `BuildScripts/BuildEasyShell.easy` builds EasyShell itself, and Parcel NExT's `Pure2/BuildScripts/PublishPure.easy` publishes Pure and Pure Notebook - the first real-world script written against this shell, and the source of the two additions below.
- [ ] Publish to Itch.io
- [ ] Create Visual Shell script for publishing whole Steam Divooka explore distribution
- [ ] Reference/Refactor `EasyShell` directly for Visual Shell use

Operator/Command:

- [x] `+` or `Append/Concat` or `||` for string concat (with many potential arguments), notice the syntax is function head first: `|| $a $b $c`. 
    * `||`, `CONCAT` and `APPEND` are the same command and take any number of arguments; `+` falls back to concatenation as soon as an operand is not numeric.
- [x] Support instance method as command name: e.g. `System.DateTime.AddDays $myTime 15`.
    * The first argument is the target. Static members still win when one fits; instance members (methods, properties, fields) are tried next. `CALL` still works as before.

Commands:

- [x] `REMOVEALL <folder> <pattern> [recursive]` - deleting by wildcard. A publish script always ends with "and now strip the XML docs out of the output", which `REMOVE` cannot express without knowing every file name in advance.
- [x] Script arguments: `HASARG <flag>`, `ARG <index>`, `$EasyArgs`, `$EasyArgCount`. A build script needs its flags - `easy Publish.easy --incremental` - and everything after the script path now belongs to the script.

Enhance syntax:

- [ ] Array/List (may utilize built-in C# construct?)
- [ ] Foreach
- [ ] SWITCH... CASE ... END (On the other hand, we should try to reduce surface and eliminate keywords)

Manual testing:

- [ ] Test call method of handles
- [ ] Run `Examples/ArgumentsAndCleanup.easy`, with and without `--incremental` - covers `HASARG`/`ARG`/`$EasyArgs` and `REMOVEALL` against a scratch folder. It self-checks with `assert`.
- [ ] Run `Examples/StringsAndInstanceMembers.easy` - covers `||`/`CONCAT`/`APPEND`, `+` as concatenation, instance methods/properties as command names, and `CALL`. It self-checks with `assert`, so a non-zero exit code means something regressed.

Enhancement:

- [x] (Phase 2) Add error code or at least know when a program exited with error - external
      programs now set `$LAST_EXIT_CODE` and abort the script on non-zero unless
      `$EasyContinueOnError` is TRUE; `$EasyProcessTimeoutSeconds` bounds a single program.
- (Phase 2) Try.. Exception.. End.
- (Phase 2) Support PIPE, WRITE/READ/APPEND for piping and redirection
- (Phase 3) FOREACH $Array $Item...END
- (Phase 2) Common built-ins for "6) File system conveniences"
- GETENV, SETENV
- [x] (Logging) During error, print line number so we can know which line is causing issue, e.g. Let's change `BuildEasyShell.easy` last line to `zip $PublishFolder\* $ArchivePath` which will raise an exception.
    * Reflection/alias failures are now tagged with the script line, and the message is the callee's own complaint rather than "Exception has been thrown by the target of an invocation".

Issues:

- [x] (Language) **`0` and `1` used to be boolean literals**, so `$i = 0` declared a BOOL, not an
      INT: a counter written the obvious way never advanced, and the `WHILE (< $i 3)` around it
      never ran even once, in silence. `Value.FromLiteralToken` asked `TryParseBool` - which
      accepts "1"/"0" alongside TRUE/YES/FALSE/NO - before it asked the number parsers. The
      numeric parses go first now. Nothing was lost: `IF 1` is still true because a non-zero INT
      is truthy, and `(== $Flag 1)` still holds because comparison falls back to comparing both
      sides as booleans when they are not both numeric.
- [ ] (Language) A numeric-looking literal loses its formatting when stored as text:
      `STRINGVAR Version 1.0` holds `"1"`, because the token became the double 1.0 before the
      declared kind coerced it back to a string. Quoting it - `STRINGVAR Version "1.0"` - is the
      answer today. Fixing it properly means a Value remembering the text it was parsed from.
- [ ] (Runtime) Currently `$a` is interpreted and the program will attempt to invoke the interpreted result as command. Maybe in this case we should avoid the command being interpretable from variables/expressions? Although it could be a feature that command themselves can be variables.

Unit Test

- [x] Aliases - `EasyShell.Tests/AliasTests.cs` walks the whole table: `print`, `cwd`/`cd`,
      `joinpath`/`resolve`/`exists`, `setenv`/`getenv`, `mkdir`/`rm`/`removeall`/`cp`/`mv`,
      `format`, `rpl`/`regrpl`, `zip`, `sqrt`, `getdate`, `hasarg`/`arg`.
- [x] Script `exit` and function `return` - `ControlFlowTests.cs`, including `exit` unwinding out
      of blocks and functions, `RETURN` at the top level, and the difference between the two at a
      prompt (`RETURN` ends the unit, `exit` reaches the host).
- [ ] Something in CI, so the suite runs on every push rather than only when someone remembers.
      The repository has no workflows at all today.

Structure:

- [x] Split the CLI executable into its own project (`EasyShell.Cli`) so the engine is a plain
      library and other projects can join the solution.
- [x] A unit-test project in the same solution: `EasyShell.Tests` (xUnit). `dotnet test` runs it,
      and both publish scripts gate on it unless `--skip-tests` is passed.

REPL:

- [ ] In REPL, if a command has return value, we automatically print it, e.g. we can implement cwd this way as a function that returns a string - map directly to C# function.
- [x] Interactive programs at the prompt. `python`, `pwsh` and `vim` used to exit immediately or
      hang, because a statement-context program was captured through pipes and handed a closed
      stdin. `$EasyInteractive` (set by the REPL) now runs them in the foreground on the terminal;
      scripts are unchanged, and expression context still captures.
- [x] The prompt loop is reusable: `EasyShell.Interactive.EasyShellRepl.Run(ReplOptions)`. A host
      supplies a banner, prompt and built-ins; block accumulation, `exit` codes and error handling
      are shared. HeadlessTerm's RetroShell is the second consumer.
- [x] `RUN <program>` to reach a program past a built-in or alias of the same name (`print`, `rm`,
      `cp`, `mv` and `zip` are all aliases here and real programs on PATH).

Issues:

- [x] A dotted command name was always treated as a .NET call unless it was a file in the working
      directory, which made `vim.tiny`, `python3.12`, `node.exe` and every Windows `.cmd`/`.bat`
      unreachable. `ProgramResolver` asks PATH instead.
- [x] Recognizing those names was only half of it: the OS launcher does not do the same lookup we
      do. `ProcessInvoker` now starts the *resolved* path (CreateProcess appends ".exe" only to a
      name with no extension at all, so `python3.12` would be found and then fail to start), and
      runs a `.bat`/`.cmd` through `cmd.exe /c`, which CreateProcess cannot execute directly.
      **Verified on Linux only** - the Windows half wants one run on a Windows machine.
- [x] `IF == $X 1` - the missing parentheses everyone writes at least once - came out of LINQ's
      `SingleOrDefault` as "Sequence contains more than one element" with no line number. It is a
      script error naming the line and suggesting the parentheses.

Documentation:

- [ ] Tutorials on YouTube
- [ ] Ask ChatGPT to generate a comprehensive user guide based on latest (full source code + incomplete README)
- [ ] Update Methodox Wiki for Easy Shell