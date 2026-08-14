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
* **Arithmetic** with `+`, `-`, `*`, `/`, `%`, `^` - head first, any number of operands
* **String concatenation** with `||` (or `CONCAT`/`APPEND`), any number of arguments
* **Reflection-based .NET invocation**
  * Static: `System.IO.File.WriteAllText "path" "content"`
  * Instance: `System.DateTime.AddDays $Now 15` (first argument is the target)
  * Instance on a handle: `CALL $handle MethodName [args...]`
* **External process execution** for non-keyword, non-qualified commands
* **Comments** with `#`

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
* v0.1.1 (Unreleased):
  * String concatenation: `||` / `CONCAT` / `APPEND`, and `+` when an operand is not a number
  * Instance members as command names: `System.DateTime.AddDays $Now 15`
  * Overload matching honors optional parameters and `params` arrays
  * Errors from .NET calls report the script line, and report what the callee complained about
  * `#` inside a quoted string is no longer treated as a comment

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