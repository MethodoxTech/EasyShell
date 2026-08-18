# Easy Shell (`easy`)

Type: Shell Scripting Language  
Extensions: (Preferred) `.easy`, (Candidate) `.es`
Version: 0.1.0

> A simple shell that works cross-platform. It can't get easier.

`EasyShell` brings simplicity to process automation - with a handful of syntax, it enables automating common build tasks.

Easy Shell is a minimal shell scripting language implemented in C#. It combines:

* Shell-like “one command per line” scripting
* Strongly-typed **global variables**
* Control-flow keywords (`IF/ELSE/WHILE/FUNC/CALL`)
* Direct invocation of fully-qualified .NET members (methods, fields, properties)
* **Sub-command expressions** using parentheses for inline evaluation

## Design Notes

* It's minimal and for lazy people - no advanced bash like functions and mostly for scripting use. Also less risk of "forgetting some keyword/syntax" (like often with PowerShell).
* Serve as a successor to `MiniParcel` for shell purpose so `MiniParcel` can focus more on a "human-readable graph language".

* **Default value interchange format is string**, but variables enforce a declared type.
* Reflection invocation attempts to **match overloads by argument count + best conversion fit**.
* External executable calls return **stdout** (or stderr when stdout is empty) when used as an expression; at an interactive prompt a bare command instead takes the terminal, so `python`, `pwsh` and `vim` work.

## Features

* **Strongly typed variables** via `INT`, `BOOL`, `STRING`, `DOUBLE`, `HANDLE`
* **Case-insensitive variable names**
* **Global scope** for all variables (no function parameters needed)
* **Expressions** as parenthesized sub-commands: `(== $X 10)` or `(System.String.Format "x={0}" $X)`
* **Control flow**
  * `IF ... ELSEIF ... ELSE ... END`
  * `WHILE ... END`
  * `FUNC name ... END` and `CALL name`
* **Arithmetic** with `+`, `-`, `*`, `/`, `%`, `^` - head first, any number of operands
* **String concatenation** with `||` (or `CONCAT`/`APPEND`), any number of arguments
* **Reflection-based .NET invocation**
  * Static: `System.IO.File.WriteAllText "path" "content"`
  * Instance: `System.DateTime.AddDays $Now 15` (first argument is the target)
  * Instance on a handle: `CALL $handle MethodName [args...]`
* **External process execution** for non-keyword, non-qualified commands, captured in scripts
  and run in the foreground at an interactive prompt
* **Script arguments** via `HASARG` / `ARG` and `$EasyArgs`
* **File system commands** including `REMOVEALL` for deleting by wildcard
* **Comments** with `#`

## Repository Layout

| Project | |
|---|---|
| `EasyShell/` | The library: parser, runtime, executor, reflection, process invocation, terminal state, and the shared interactive prompt loop under `Interactive/`. |
| `EasyShell.Cli/` | The `easy` executable - argument handling and the help text, and nothing else. |
| `EasyShell.Tests/` | The unit tests. See [Testing](#testing). |

The library has no dependencies beyond the .NET runtime, and that is a constraint worth keeping:
`easy` is a build tool that has to clone and build on its own, and a host that embeds the engine
should not inherit that host's own dependency graph in return. Terminal handling is the case where
this was tempting to break - it is why `TerminalState` is 150 lines of self-contained P/Invoke here
rather than a reference to somebody's terminal library.

Anything that a host embedding EasyShell could want belongs in the library. The prompt loop is the
clearest case: HeadlessTerm's RetroShell wants an EasyShell prompt with its own banner, prompt
string and built-ins, and nothing else of its own - so block accumulation, `exit` codes, error
reporting and result printing live in `Interactive/EasyShellRepl.cs` and the differences are
`ReplOptions`.

## Usage Guide

### Example Script

```easy
# Get time as handle
HANDLEVAR NOW (System.DateTime.Now)

# Convert time to string via instance call
STRINGVAR DATE (CALL $NOW ToString)

# Prepare content via static call
STRINGVAR VALUE (System.String.Format "Current time: {0}" $DATE)

# Write file
STRINGVAR PATH "C:/Value"
System.IO.File.WriteAllText $PATH $VALUE
```

### Distinguish between argument and expression

```easy
es> $Date = (format "{0:yyyyMMdd}" (GetDate))
es> print $date
20251214
```

```easy
es> $Date = (format "{0:yyyyMMdd}" GetDate)
es> print $date
GetDate
```

## Quick Start

### Run

```bash
easy # REPL
easy path/to/script.easy
```

### Help / Version

```bash
easy --help
easy --version
```

## Language Overview

### Comments

Anything after `#` is ignored.

```easy
# This is a comment
STRINGVAR Name "Charles"  # trailing comment
```

### Variables

Declare variables with a type command:

```easy
INTVAR Count 10
BOOLVAR Enabled TRUE
DOUBLEVAR Pi 3.14159
STRINGVAR Title "Hello"
HANDLEVAR Now (System.DateTime.Now)
```

Rules:

* Variable names are **case-insensitive**
* Variables are **strongly typed**
* `HANDLE` stores an object instance (from .NET calls or other results)
* Empty strings are supported, e.g. `STRINGVAR VALUE ""`

### Variable Reference and Assignment

Reference: `$Name`

Assignment:

```easy
$Count = 11
$Title = "Updated"
```

Values can be literals or expressions:

```easy
$Title = (System.String.Format "Count={0}" $Count)
```

### Literals

* Strings: `"hello world"`
* Booleans: `TRUE`, `FALSE` (also accepts `YES`/`NO`)
* Integers: `123`
* Doubles: `3.14`

An unquoted word that looks like a number **is** a number, `0` and `1` included, so `$i = 0`
declares an INT and counts the way you would expect. They still read as conditions where one is
wanted - `IF 1`, and `(== $Flag 1)` against a boolean - because a non-zero number is true and a
comparison falls back to comparing both sides as booleans when they are not both numeric.

Quoting is what keeps a literal a string: `"1.0"` is a version, `1.0` is the number one.

### Commands

A line is generally a command invocation:

1. **Keywords** (language built-ins)
2. **External executables** (e.g., `git`, `ping`, `curl` depending on environment)
3. **Fully-qualified .NET members** (reflection invoked)

### Expressions (Sub-Commands)

Any argument may be an expression: a parenthesized command that is evaluated first and yields a value.

```easy
STRINGVAR X "10"
BOOLVAR IsTen (== $X 10)
```

### Built-in Comparison Commands

* `==`, `!=`, `>`, `<`, `>=`, `<=`

Examples:

```easy
BOOLVAR A (== 5 5)
BOOLVAR B (> 10 2)
BOOLVAR C (<= 3 3)
```

### Built-in Arithmetic Commands

* `+`, `-`, `*`, `/`, `%`, `^`

Every operator is head first and takes as many operands as you like:

```easy
$Sum = (+ 1 2 3)      # 6
$Neg = (- 5)          # -5, unary
$Pow = (^ 2 10)       # 1024
```

The result stays an `INT` when every operand is an `INT` and the operator cannot produce a
fraction (`+`, `-`, `*`, `%`); otherwise it is a `DOUBLE`.

### Built-in String Commands

* `||`, `CONCAT`, `APPEND` - the same command under three names

```easy
$Name = "EasyShell"
$Version = "0.1.0"

$Archive = (|| $Name "_v" $Version ".zip")     # EasyShell_v0.1.0.zip
$Archive = (CONCAT $Name "_v" $Version ".zip") # identical
```

Every argument is converted to its string form first, so numbers, booleans and handles
can be mixed in freely:

```easy
print (|| "Built " 3 " packages, ok=" TRUE)    # Built 3 packages, ok=TRUE
```

`+` doubles as concatenation when an operand is not a number, which keeps the common case short:

```easy
$Tag = (+ "build-" 42)     # build-42
$Total = (+ "10" 1)        # 11 - both operands are numeric, so this is still arithmetic
```

> Prefer `||` whenever you mean concatenation regardless of what the values look like -
> `+` only falls back once something is genuinely non-numeric.

### Script Arguments

Everything after the script path belongs to the script:

```bash
easy Publish.easy --incremental
```

```easy
IF (hasarg "--incremental")
  print "Skipping the clean step."
END

print (|| "Called with " $EasyArgCount " argument(s): " $EasyArgs)
print (arg 0)     # "--incremental"; an index that is not there reads as ""
```

`HASARG` ignores case. `ARG` returns an empty string rather than failing when the index is out of
range, so an optional argument needs no guard around it.

### File System Commands

* `MKDIR <path>`, `REMOVE`/`RM <path>`, `CP <source> <target>`, `MV <source> <target>`
* `EXISTS <path>` - true for a file or a directory
* `JOINPATH <part> <part> [...]`, `RESOLVE <path>`
* `REMOVEALL <folder> <pattern> [recursive]` - deletes every **file** matching a wildcard and
  answers with how many went, recursively unless told otherwise

```easy
# Strip XML documentation out of a publish folder, at any depth
$Removed = (removeall $PublishFolder "*.xml")
print (|| "Deleted " $Removed " XML file(s).")

# Only the top folder
removeall $PublishFolder "*.log" FALSE
```

Directories are never matched, so a pattern like `*` cannot quietly take a folder with it, and a
folder that does not exist deletes nothing rather than failing - it is already in the state the
caller asked for.

### Control Flow

#### IF / ELSEIF / ELSE / END

```easy
INTVAR X 10

IF (>= $X 10)
  STRINGVAR Msg "X is at least 10"
ELSEIF (== $X 9)
  STRINGVAR Msg "X is 9"
ELSE
  STRINGVAR Msg "X is something else"
END
```

#### WHILE / END

```easy
INTVAR I 0

WHILE (< $I 3)
  System.Console.WriteLine (System.String.Format "I={0}" $I)
  $I = (+ $I 1)
END
```

### Functions

Functions are named blocks with global variable access. They do not take arguments; “return values” are done by setting variables.

#### Define a function

```easy
FUNC WriteGreeting
  System.Console.WriteLine "Hello from a function"
END
```

#### Call a function

```easy
CALL WriteGreeting
```

### Calling .NET

#### Static members (fully qualified)

```easy
System.Console.WriteLine "Hello"
STRINGVAR S (System.String.Format "X={0}" 42)
```

Static property/field access (no arguments):

```easy
HANDLEVAR Now (System.DateTime.Now)
```

#### Instance members as command names

A fully-qualified name may also be an *instance* member. The first argument is the instance it
runs on, so the syntax stays head first:

```easy
HANDLEVAR Now (System.DateTime.Now)

# Same as $Now.AddDays(15)
HANDLEVAR Later (System.DateTime.AddDays $Now 15)

# Instance properties work the same way: $Now.Year
INTVAR ThisYear (System.DateTime.Year $Now)

# The target does not have to be a handle - anything convertible works
STRINGVAR Shout (System.String.ToUpper "hello")
```

Resolution order for `Type.Member <args...>`:

1. static field
2. static property (no arguments)
3. static method whose overload accepts all the arguments
4. instance member, using the **first** argument as the target

So a static member always wins when one fits; the instance form is what happens next rather
than something you have to ask for.

#### Instance calls via HANDLE

`CALL <handle> <method> [args...]` names the method separately, which is handy when the method
name itself comes from a variable, or when the type name is long:

```easy
HANDLEVAR Now (System.DateTime.Now)
STRINGVAR Stamp (CALL $Now ToString)
System.Console.WriteLine $Stamp
```

#### Overload matching

Overloads are matched by argument count and conversion fit. Optional parameters may be omitted,
and `params` arrays are filled from the remaining arguments:

```easy
print (System.String.Format "{0}-{1}-{2}-{3}" 1 2 3 4)   # params object[]
```

## Publish

### Prerequisites

* .NET SDK (recommended: .NET 8+)
* Powershell 7 (for first build)
* Alternatively, EasyShell binary
* Alternatively, build directly with `dotnet`

### Testing

```bash
dotnet test
```

`EasyShell.Tests` is an xUnit project covering the parser and tokenizer, value and variable
semantics, every built-in command and alias, control flow including `EXIT` and `RETURN`, the
reflection binder, external process execution (capture, foreground, exit codes and the wall-clock
timeout) and the interactive prompt loop. It has no dependencies of its own beyond the test runner,
and it runs in a few seconds.

Two things about it are worth knowing before adding to it:

* **Tests do not run in parallel**, deliberately. A shell is made of process-global state -
  `Console.In`/`Console.Out`, the working directory, `PATH`, the script-argument table - and the
  tests borrow all of it. See `AssemblyInfo.cs`.
* **External programs are written by the tests themselves** (`Infrastructure/ProgramProbe.cs`),
  as `sh` scripts on Unix and `.cmd` on Windows, in a temporary folder placed on `PATH`. Borrowing
  a system program instead would make the tests depend on what happens to be installed.

The publish scripts run the tests before publishing. Pass `--skip-tests` to skip that.

### Build

Use `pwsh` from the `BuildScripts` folder:

```powershell
pwsh ./BuildEasyShell.ps1
```

Or using `easy` itself:

```easy
easy ./BuildEasyShell.easy
```

Or using `dotnet`:

```easy
dotnet run -- ./BuildScripts/BuildEasyShell.easy
```

Expected folder structure:

```txt
<build-root>/
├─ External/
│  └─ EasyShell/
│     ├─ BuildScripts/
│     │  ├─ BuildEasyShell.easy
│     │  └─ BuildEasyShell.ps1
│     ├─ EasyShell/              # the library - engine, parser, runtime, process invocation
│     │  └─ EasyShell.csproj
│     ├─ EasyShell.Cli/          # the `easy` executable
│     │  └─ EasyShell.Cli.csproj
│     ├─ EasyShell.Tests/        # the unit tests
│     │  └─ EasyShell.Tests.csproj
│     ├─ Examples/
│     └─ EasyShell.sln
├─ Publish/
│  ├─ Utilities/
│  │  └─ EasyShell/
│  │     └─ Current/
│  └─ Packages/
```

The build script lives directly under `External/EasyShell/BuildScripts`.

It uses:

```powershell
$BuildRoot = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.Parent.FullName
```

So `BuildScripts` must be three levels below `<repo-root>`:

```txt
<build-root>/External/EasyShell/BuildScripts
```

The script publishes `External/EasyShell/EasyShell.Cli` into:

```txt
<build-root>/Publish/Utilities/EasyShell/Current
```

Then creates a package in:

```txt
<build-root>/Publish/Packages
```

## Changelog

* v0.1.0: Initial setup.
* v0.1.1 (Unreleased):
  * Script arguments: `HASARG`, `ARG`, `$EasyArgs`, `$EasyArgCount`
  * `REMOVEALL <folder> <pattern> [recursive]` for deleting files by wildcard
  * String concatenation: `||` / `CONCAT` / `APPEND`, and `+` when an operand is not a number
  * Instance members as command names: `System.DateTime.AddDays $Now 15`
  * Overload matching honors optional parameters and `params` arrays
  * Errors from .NET calls report the script line, and report what the callee complained about
  * `#` inside a quoted string is no longer treated as a comment

* v0.2.0 (Unreleased):
  * Split into a library (`EasyShell/`) and the `easy` CLI (`EasyShell.Cli/`), so other projects
    can join the solution and so hosts can reference the engine without pulling in an executable
  * `ProcessInvoker.RunForeground` and `$EasyInteractive`: at an interactive prompt, a
    statement-context external program inherits stdin/stdout/stderr instead of being captured
    through pipes. Without it `python` and `pwsh` see a non-tty, skip their REPL and exit
    immediately, and `vim` hangs on `/dev/tty`. Scripts are unchanged - a closed stdin is exactly
    what keeps a prompting tool from blocking CI - and expression context always captures
  * `RUN <program> [args...]` runs a program even when a built-in or alias shadows its name
  * `TerminalState` snapshots and restores the tty around every foreground child, so a program
    that dies without unwinding cannot leave the prompt in raw mode
  * Dotted names are resolved against PATH before being treated as .NET calls, so `vim.tiny`,
    `python3.12`, `node.exe` and every Windows `.cmd`/`.bat` are reachable at all
  * The REPL loop moved into the library as `EasyShell.Interactive.EasyShellRepl` and takes
    `ReplOptions`, so a host supplies only its banner, prompt and built-ins

* v0.3.0 (Unreleased):
  * `EasyShell.Hosting`: the virtualization seam. A `Runtime` now carries a `ShellHost` -
    console, filesystem, process runner, environment - defaulting to the real machine with
    behavior unchanged. A host that substitutes all four (a virtual machine whose filesystem is
    a portable image, whose processes are a virtual process table, whose console is a tty) gets
    the entire language and REPL inside its world: same parser, same semantics, zero drift
  * The machine-touching aliases (cd, cwd, resolve, exists, setenv, getenv, hasarg, arg, mkdir,
    remove/rm, removeall, cp, mv, print, rpl, regrpl) became host-routed built-ins; only pure or
    deliberately host-only targets (joinpath, format, sqrt, getdate, zip) remain reflection
    aliases. Behavior on the default host is unchanged and pinned by the alias tests
  * `ShellHost.CanInvokeQualified`: reflection policy. Direct .NET invocation is EasyShell's
    superpower on the host and its biggest escape hatch in a sandbox; a virtualizing host can
    refuse or allowlist fully-qualified names, and refusals read as ordinary script errors
  * Script arguments (`HASARG`/`ARG`) are per-Runtime, so embedded sessions cannot see each
    other's flags
  * **Both** reflection paths are gated. `CALL <receiver> <member>` reached
    `ReflectionInvoker.InvokeInstance` without consulting the policy, so `CALL "" GetType`
    walked String -> Type -> Assembly -> `GetType("System.IO.File")` -> `Invoke` and escaped a
    sandbox that correctly refused the same call written as `System.IO.File.WriteAllText ...`.
    Instance calls are now gated on the concrete receiver type plus member name
  * Separator normalization moved onto `IShellFileSystem.NormalizeSeparators`. The file
    built-ins folded '/' onto `Path.DirectorySeparatorChar`, which is right for the real machine
    and wrong for a virtual filesystem with its own convention: on Windows a pocket path
    `/home/x` became `\home\x` and silently matched nothing. The default host keeps the old
    behavior; a virtual host returns the path unchanged

## License

MIT

## Contributing

1. Fork / branch
2. Add tests for new language features
3. Keep scripts in `Examples/` small and focused
4. Open a PR with a short description of behavior changes

## See Also

* [Methodox Tutorials on EasyShell (YouTube)](https://www.youtube.com/playlist?list=PLZFRaSxvnUEdnlB1-spH2cDQ-Paq3P3gZ)
* [Visual Studio Code syntax highlight extension](https://marketplace.visualstudio.com/items?itemName=Methodox.easyshell)