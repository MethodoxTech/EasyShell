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
- [ ] Draft build script
- [ ] Publish to Itch.io
- [ ] Create Visual Shell script for publishing whole Steam Divooka explore distribution
- [ ] Reference/Refactor `EasyShell` directly for Visual Shell use

Operator/Command:

- [x] `+` or `Append/Concat` or `||` for string concat (with many potential arguments), notice the syntax is function head first: `|| $a $b $c`
      `||`, `CONCAT` and `APPEND` are the same command and take any number of arguments;
      `+` falls back to concatenation as soon as an operand is not numeric.
- [x] Support instance method as command name: e.g. `System.DateTime.AddDays $myTime 15`
      The first argument is the target. Static members still win when one fits; instance
      members (methods, properties, fields) are tried next. `CALL` still works as before.

Enhance syntax:

- [ ] Array/List (may utilize built-in C# construct?)
- [ ] Foreach
- [ ] SWITCH... CASE ... END (On the other hand, we should try to reduce surface and eliminate keywords)

Manual testing:

- [ ] Test call method of handles
- [ ] Run `Examples/StringsAndInstanceMembers.easy` - covers `||`/`CONCAT`/`APPEND`, `+` as
      concatenation, instance methods/properties as command names, and `CALL`. It self-checks
      with `assert`, so a non-zero exit code means something regressed.

Enhancement:

- [x] (Phase 2) Add error code or at least know when a program exited with error - external
      programs now set `$LAST_EXIT_CODE` and abort the script on non-zero unless
      `$EasyContinueOnError` is TRUE; `$EasyProcessTimeoutSeconds` bounds a single program.
- (Phase 2) Try.. Exception.. End.
- (Phase 2) Support PIPE, WRITE/READ/APPEND for piping and redirection
- (Phase 3) FOREACH $Array $Item...END
- (Phase 2) Common built-ins for "6) File system conveniences"
- GETENV, SETENV
- [x] [Logging] During error, print line number so we can know which line is causing issue, e.g. Let's change `BuildEasyShell.easy` last line to `zip $PublishFolder\* $ArchivePath` which will raise an exception.
      Reflection/alias failures are now tagged with the script line, and the message is the
      callee's own complaint rather than "Exception has been thrown by the target of an invocation".

Issues:

- [ ] (Runtime) Currently `$a` is interpreted and the program will attempt to invoke the interpreted result as command. Maybe in this case we should avoid the command being interpretable from variables/expressions? Although it could be a feature that command themselves can be variables.

Unit Test

- [ ] Aliases
- [ ] Script `exit` and function `return`

REPL:

- [ ] In REPL, if a command has return value, we automatically print it, e.g. we can implement cwd this way as a function that returns a string - map directly to C# function.

Documentation:

- [ ] Tutorials on YouTube
- [ ] Ask ChatGPT to generate a comprehensive user guide based on latest (full source code + incomplete README)
- [ ] Update Methodox Wiki for Easy Shell