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
* External executable calls return **stdout** (or stderr when stdout is empty).

## Features

* **Strongly typed variables** via `INT`, `BOOL`, `STRING`, `DOUBLE`, `HANDLE`
* **Case-insensitive variable names**
* **Global scope** for all variables (no function parameters needed)
* **Expressions** as parenthesized sub-commands: `(== $X 10)` or `(System.String.Format "x={0}" $X)`
* **Control flow**
  * `IF ... ELSEIF ... ELSE ... END`
  * `WHILE ... END`
  * `FUNC name ... END` and `CALL name`
* **Reflection-based .NET invocation**
  * Static: `System.IO.File.WriteAllText "path" "content"`
  * Instance: `CALL $handle MethodName [args...]`
* **External process execution** for non-keyword, non-qualified commands
* **Comments** with `#`

## Usage Guide

### Example Script

```easy
# Get time as handle
HANDLEVAR NOW System.DateTime.Now

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
STRING Name "Charles"  # trailing comment
```

### Variables

Declare variables with a type command:

```easy
INTVAR Count 10
BOOLVAR Enabled TRUE
DOUBLEVAR Pi 3.14159
STRINGVAR Title "Hello"
HANDLEVAR Now System.DateTime.Now
```

Rules:

* Variable names are **case-insensitive**
* Variables are **strongly typed**
* `HANDLE` stores an object instance (from .NET calls or other results)
* Empty strings are supported, e.g. `STRING VALUE ""`

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
* Booleans: `TRUE`, `FALSE` (also accepts common equivalents)
* Integers: `123`
* Doubles: `3.14`

### Commands

A line is generally a command invocation:

1. **Keywords** (language built-ins)
2. **External executables** (e.g., `git`, `ping`, `curl` depending on environment)
3. **Fully-qualified .NET members** (reflection invoked)

### Expressions (Sub-Commands)

Any argument may be an expression: a parenthesized command that is evaluated first and yields a value.

```easy
STRING X "10"
BOOL IsTen (== $X 10)
```

### Built-in Comparison Commands

* `==`, `!=`, `>`, `<`, `>=`, `<=`

Examples:

```easy
BOOL A (== 5 5)
BOOL B (> 10 2)
BOOL C (<= 3 3)
```

### Control Flow

#### IF / ELSEIF / ELSE / END

```easy
INT X 10

IF (>= $X 10)
  STRING Msg "X is at least 10"
ELSEIF (== $X 9)
  STRING Msg "X is 9"
ELSE
  STRING Msg "X is something else"
END
```

#### WHILE / END

```easy
INT I 0

WHILE (< $I 3)
  System.Console.WriteLine (System.String.Format "I={0}" $I)
  $I = (+ $I 1)  # if you add a + command later; otherwise assign directly
END
```

> Note: If arithmetic commands are not implemented yet, you can update values using .NET calls you provide, or extend the engine with `+`, `-`, etc.

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
STRING S (System.String.Format "X={0}" 42)
```

Static property/field access (no arguments):

```easy
HANDLEVAR Now System.DateTime.Now
```

#### Instance calls via HANDLE

Use `CALL <handle> <method> [args...]`:

```easy
HANDLEVAR Now System.DateTime.Now
STRINGVAR Stamp (CALL $Now ToString)
System.Console.WriteLine $Stamp
```

## Publish

### Prerequisites

* .NET SDK (recommended: .NET 8+)
* Powershell 7 (for first build)
* Alternatively, EasyShell binary
* Alternatively, build directly with `dotnet`

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
│     ├─ EasyShell.csproj
│     └─ ...
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

The script publishes `External/EasyShell` into:

```txt
<build-root>/Publish/Utilities/EasyShell/Current
```

Then creates a package in:

```txt
<build-root>/Publish/Packages
```

## Changelog

* v0.1.0: Initial setup.

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