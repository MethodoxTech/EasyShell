# EasyShell under NativeAOT and WebAssembly

EasyShell can be NativeAOT-compatible without losing its language, and this branch makes it so: `dotnet publish EasyShell.Cli -r win-x64 -p:PublishAot=true` builds a single 5.4 MB `easy.exe` with no trim or AOT warning, and the same core compiled by ManagedWasm runs a script in Node. What it costs is the open half of "call any C#": under NativeAOT a script calls the types somebody bound at compile time, not any type it can name.

This note answers why EasyShell needed reflection, how Parcel NExT's NRI solved the same problem for the Divooka Compute runtime, what the branch changes, and what remains.

## Why EasyShell used reflection

Reflection was the language's standard library. Every name with a dot in it - `System.Math.Sqrt 2`, `System.DateTime.AddDays $When 15`, `CALL $s ToUpper`, and the aliases `sqrt`, `format`, `getdate`, `joinpath` and `zip` - was resolved by name at run time, its overloads were listed and scored, and the winner was called through `MethodInfo.Invoke`. All of it lived in one file, `EasyShell/Reflection/ReflectionInvoker.cs`; nothing else in the engine reflects, and nothing anywhere uses `dynamic`, `System.Linq.Expressions`, `Reflection.Emit`, Roslyn scripting, `Activator` or a reflection-based serializer.

The inventory, with line numbers in `ReflectionInvoker.cs` as it was on `main` (dd8074c):

| # | Site | What it does | Class |
|---|---|---|---|
| 1 | `ResolveType`, lines 482-498 | `AppDomain.GetAssemblies()` then `Assembly.GetType(name, ignoreCase)`: a type by the name the script wrote | (a) inherent |
| 2 | `ResolveFromProbableAssembly` / `ProbeAssembly`, lines 505-540 | `Assembly.Load` of each namespace prefix, so `System.Text.RegularExpressions.Regex` loads its assembly on demand | (a) inherent |
| 3 | `LoadAssembly`, lines 56-81 | `Assembly.LoadFrom` / `Assembly.Load` for `LOADASSEMBLY` | (a) inherent |
| 4 | `FindField`, lines 211-222 | `Type.GetField` / `GetFields`, case-insensitive | (a) inherent |
| 5 | `FindProperty`, lines 228-243 | `Type.GetProperty` / `GetProperties`, skipping indexers and write-only properties | (a) inherent |
| 6 | `FindMethods`, lines 245-248 | `Type.GetMethods`, every overload of a name | (a) inherent |
| 7 | `InvokeChecked` and the value reads, lines 104, 109, 169, 176, 193 | `FieldInfo.GetValue`, `PropertyInfo.GetValue`, `MethodInfo.Invoke` | (a) inherent |
| 8 | `TryBind*`, lines 279-365 | `GetParameters`, `HasDefaultValue`/`DefaultValue`, `IsDefined(ParamArrayAttribute)`: the binder's view of a signature | (a) inherent |
| 9 | `TryBindParamArray`, line 352 | `Array.CreateInstance(elementType, n)` to pack a params tail | (b) replaceable: generated code writes `new T[n]` |
| 10 | `TryConvert`, lines 367-456 | `IsAssignableFrom`, `Nullable.GetUnderlyingType`, `IsEnum`, `Enum.TryParse(Type, ...)`, `Convert.ChangeType` | (b) uses `System.Type` but is AOT-safe as it stands; kept and shared |
| 11 | `Executor.cs` line 747 | `handle.GetType().FullName` to name a `CALL` for the host's policy | (b) AOT-safe |
| 12 | `TerminalState.cs` line 61 | `NativeLibrary.SetDllImportResolver(typeof(TerminalState).Assembly, ...)` for libc | (b) AOT-safe; matters only to WebAssembly (below) |
| 13 | `EasyShell.Tests`: `AssemblyLoadingTests.cs` lines 40 and 96, `ProcessInvokerTests.cs` line 223, `ReflectionInvokerTests.cs` | `Assembly.Location`, `GetType().Name`, and the invoker under test | (c) tests only |

So eight sites are the language feature itself, four are incidental, and one group is tests; the CLI has none. Reflection was never the interpreter's machinery - the parser, values, control flow, pipes, globbing, processes and the REPL are all ordinary code - only the door from a script into .NET.

## What NativeAOT made of it

`main` already publishes with `-p:PublishAot=true`. It builds a 3.9 MB `easy.exe` with warnings at nine sites, all in `ReflectionInvoker.cs`: IL2070 at lines 215, 220, 233, 237 and 246 (member lookup on a `Type` nobody annotated), IL2026 at lines 67, 487 and 510 (`Assembly.LoadFrom`, `Assembly.GetType`), and IL3050 at line 352 (`Array.CreateInstance`) - seventeen lines of build output once the analyzer's and the AOT compiler's copies are counted. The binary then runs every script that does not touch .NET, and fails every one that does, because the trimmer kept nothing a script names only at run time:

| Script line | `main` under NativeAOT | This branch under NativeAOT |
|---|---|---|
| `print (sqrt 16)` | `Type not found: System.Math` | `4` |
| `print (format "x={0}" 5)` | `No matching overload for System.String.Format (String, Int).` | `x=5` |
| `print (CALL "abc" ToUpperInvariant)` | `Instance member not found: System.String.ToUpperInvariant` | `ABC` |
| `print (System.Text.RegularExpressions.Regex.IsMatch "v12" "^v[0-9]")` | `No matching overload ...` | `TRUE` |
| `$when = (getdate)` | `Member not found: System.DateTime.Now` | a `DateTime` handle |
| `print (System.Math.Max 3 9)` | treated as a program: `The system cannot find the file specified.` | `9` |
| `print (joinpath "a" "b")` | `Type not found: EasyShell.Commands.CommonUtilities` | `a\b` |
| `LOADASSEMBLY "System.Text.Json"` | `No metadata found for this assembly.` | refused: the library is fixed |

That is the reason to do this properly rather than annotate the warnings away: the warnings were few, but they marked the whole standard library.

## How NRI solved the same problem

The Divooka Compute runtime calls package members a document names, which is the same problem one level up. Parcel NExT's No-Reflection Interpreter (`Parcel.NExT/CoreEngines/Divooka.NRI.*`) solved it with five pieces, and they transfer:

1. **The surface is declared as data a generator can read.** A `.NRI` project names the plugin's `Toolboxes.cs` (`NriToolboxDeclarations`) or says `[assembly: NriBindType(typeof(T))]`; nothing is discovered at run time (`Divooka.NRI.Generator/README.md`).
2. **A Roslyn source generator writes direct calls.** `Divooka.NRI.Generator` walks the declared types as symbols, the way the reflective `TypeIndexer` walks them, and emits an `NriBindings` class whose symbols each carry an invoker that calls the member directly. Members it cannot bind - generic beyond the generated instantiations, by-ref-like, requiring unreferenced or dynamic code - are still registered with the reason.
3. **The host composes a registry explicitly.** `NriRegistry.Build(...NriBindings.Package)`: "Nothing is scanned, loaded or activated" (`Divooka.NRI.Abstractions/README.md`).
4. **One conversion rule, reproduced exactly.** `NriConversions` is the reflective `TypeHelper.TryConvert` "branch for branch and in its order", and parity tests (`InvocationParityTests`, `Divooka.NRI.ParityTests`) call every symbol both ways and require the same outputs or the same exception.
5. **A NativeAOT smoke program is the gate.** `TestPrograms/Divooka.NRI.AotSmoke` publishes with trim and AOT warnings as errors and calls the packages natively; `Automation/NRI/VerifyLocally.ps1` runs it.

What does not transfer is the generator itself. NRI's is about 2,400 lines of Divooka specifics - toolbox identifiers, pins, documentation ids, procedural base types - and EasyShell must keep referencing nothing of Parcel NExT. EasyShell's problem is also smaller: its binder needs a member's name, parameter types, defaults and a way to call it, nothing more. So this branch writes its own generator of about 400 lines and borrows the pattern, not the code.

## What this branch does

**A library seam.** `ShellHost.Library` (an `IShellLibrary`) is the .NET surface scripts call, beside the console, filesystem, processes and environment the host already supplies. `Executor` routes every qualified call, every `CALL`, the dotted-name test and `LOADASSEMBLY` through it.

**One binder for both libraries.** `ShellCallBinder` is `ReflectionInvoker`'s algorithm moved off `System.Reflection`: the same lookup order (static field, static property, static method, then an instance member with the first argument as the target), the same scoring, the same `TryConvert`, the same messages. It works over `ShellType` and `ShellMethod`, so the reflective and the generated library differ only in where the candidates come from and how the chosen one is called.

**The reflective library, unchanged in behaviour.** `ReflectionLibrary` is the old resolution and lookup, marked `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. `ReflectionInvoker` stays as a thin facade under its old name.

**Curated libraries, generated.** `[assembly: ShellBindType(typeof(T), Library = "...")]` names a type, and `EasyShell.Generators` writes a `ShellBindings.<Library>` property building a `CuratedLibrary` of direct calls - a lambda per overload, `new T[n]` for a params tail, each default value written as a literal. The walk is reflection's (a type's own statics; its instance members with everything inherited), so the generated candidates are the reflective ones in the same order. It never binds `GetType`, so a handle cannot become a way into the type system, and it skips what cannot be called without reflection, listing each skip in the generated file. EasyShell declares two libraries in `EasyShell/Binding/ShellLibraries.cs`:

- **Pure**: `Object`, `Math`, `String`, `Char`, `Boolean`, `Int32`, `Int64`, `Double`, `DateTime`, `TimeSpan`, `Guid`, `Convert`, `Regex` (static members), `Match`, `Group`, `GroupCollection`, and `CommonUtilities.JoinPath`/`NormalizeSeparators`. Computation only.
- **Machine**: `Path`, `File`, `Directory`, `Environment`, `OperatingSystem`, `RuntimeInformation`, `FileVersionInfo`, and all of `CommonUtilities` and `ZipUtil`. What a build script does to the real machine.

Together they are **Standard**: 25 types, 1,332 method overloads, 115 properties and 59 fields; 204 members are left out (164 for a by-ref or by-ref-like parameter, 18 generic, 15 with a result that cannot be boxed, 2 such fields, 5 obsolete). Every .NET member called by the nine `.easy` scripts in the Parcel NExT tree - its automation scripts, this repository's build script and `Examples/` - is in it, and each was run under NativeAOT.

**A feature switch picks the default.** `ShellLibraries.Default` is the reflective library when `ShellLibraries.IsReflectionSupported` holds and Standard otherwise. The switch is `EasyShell.Reflection.IsSupported`, declared with `[FeatureSwitchDefinition]` and `[FeatureGuard]`, and it is false whenever dynamic code is unsupported, which is always the case under NativeAOT. `EasyShell.Cli` sets it to false when publishing with `PublishAot` or `PublishTrimmed`, so the compiler folds it and drops the reflective library from the binary. The library is now `IsAotCompatible`, so the analyzers hold every other line of it to the rule.

**Held to the reflective library.** `CuratedLibraryTests.EveryPureMemberBehavesAsItsReflectiveTwin` aims a call at every overload of every Pure member - 789 calls, 691 of which return a value and the rest a member's own refusal of a sample argument - and requires the same result or the same error message from both libraries; there are no differences. The binder's overload preferences (an exact overload over one padded with defaults, a fixed one over a params array, the conversion-free one) are tested through a generated library as well as the reflective one.

### Results

| | `main` | this branch |
|---|---|---|
| Trim/AOT warnings in a clean NativeAOT publish | 9 sites (IL2070 x5, IL2026 x3, IL3050 x1) | 0 |
| `easy.exe` (win-x64, NativeAOT) | 3.9 MB, every .NET call fails | 5.4 MB, the table above passes |
| `Examples/*.easy` under NativeAOT | fail at the first .NET call | both pass |
| EasyShell tests (JIT) | 463 | 475 (12 new), all pass |
| PocketOS tests against this EasyShell | | 235, all pass |

Running a short script that loops, calls a function and makes ten .NET calls took a median of 42 ms (35-60 ms) under NativeAOT and 151 ms (132-379 ms) under the JIT with reflection, over 15 alternating runs each. The machine was 68-97% busy with other work while they ran, so these are provisional, and they measure process start more than the language.

## What changes for scripts

Under the JIT nothing changes: the default library is still the whole runtime, and every existing test passes unchanged. Under NativeAOT, or for a host that chooses a curated library:

- A type nobody bound does not exist. A dotted name whose type half is unknown is not a .NET call, so it goes to the process path, as `vim.tiny` and `python3.12` always have.
- `LOADASSEMBLY` is refused: the surface was fixed when the program was built.
- `GetType` is not callable, so there is no `Type` or `Assembly` to walk from.
- Generic methods, `out`/`ref` parameters and span parameters are not callable. They mostly were not callable through reflection either - the binder could not supply them - and the parity test holds the two together where they overlap.
- Operators and conversion operators by name (`System.DateTime.op_Subtraction`) and obsolete members are not bound yet. Nothing in the repositories calls them.

No syntax changes.

## What it means for PocketOS

PocketOS sandboxes EasyShell with `PocketShellHost.IsPurelyComputational`, a predicate over member names: `GetType` and the `System.Reflection`, `RuntimeType`, `System.Type`, `Activator` and `AppDomain` prefixes are denied, then `System.Math`, `System.String`, `System.DateTime`, `System.Convert`, `System.Guid` and `CommonUtilities.JoinPath` are allowed. That is a policy inside a process that can still reach everything, and Round 3 named it as the one place a mistake would let a script out.

The curated library is the allowlist made structural. Each allowed prefix is a `ShellBindType` line in Pure, and the denied names are simply never bound, so there is nothing for a chain of calls to reach. PocketOS would set `Library = ShellLibraries.Pure` in `PocketShellHost.Create`, and can keep the predicate as defence in depth or drop it. Pure also binds `Char`, `Boolean`, `Int32`, `Int64`, `Double`, `TimeSpan` and the regular-expression types, which the predicate currently refuses; PocketOS can bind its own set instead with `[assembly: ShellBindType]` in `PocketOS.Shell`, since the generator runs in any assembly that references it (`EasyShell.Tests` binds its `BinderProbe` that way). Two things change visibly: a refused name such as `System.IO.File.ReadAllText` becomes "command not found" rather than "not permitted", which `TerminalEndToEndTests` asserts today, and `joinpath` should become a host-routed built-in, because `CommonUtilities.JoinPath` joins with the host's separator and gives `\home\x` on a Windows host.

Everything else the game needs - files, directories, processes, console input and output, the environment - already goes through the host's built-ins and needs no .NET binding at all.

## WebAssembly

ManagedWasm's `mwm` compiles an ordinary .NET program on NativeAOT's CoreLib and applies the same feature switches a NativeAOT publish does, so the curated core is what it needs. Tried with the `mwm` build of ManagedWasm at 4dc0416 (M1.5; its `origin/main` is later):

1. `mwm compile easy.dll EasyShell.dll` succeeds in about 47 s: a 12.6 MB module on the debug framework, with two `MWM0001` warnings from the runtime about threads.
2. **It stops at instantiation.** The module imports P/Invokes that the loader does not provide: `easyshell_libc.tcgetattr`/`tcsetattr` from `TerminalState`, and 37 functions of `libSystem.Native`, `libSystem.Globalization.Native` and `libSystem.IO.Compression.Native` that the Machine library's file, process and zip code pull in. Node refuses a module with an import nobody supplies.
3. With every foreign import stubbed (none was ever called), `easy` starts, prints its banner and the REPL prompt, and then fails at `Console.TreatControlCAsInput` with `PlatformNotSupportedException`: console input is ManagedWasm's M3, not done yet.
4. A small host program that runs a script through `EasyShellEngine` on the default host runs the whole probe script - a loop, a function, arithmetic, `sqrt`, `format`, `CALL`, `String.Join`, regular expressions, dates, `Convert`, `joinpath` - and prints exactly what the JIT prints, except that `joinpath` joins with `/`. (That host and the stubbing loader were spike scaffolding and are not committed.)

So the language and the curated library already run as WebAssembly; what is missing is a host. For the game that is the right shape anyway: a wasm EasyShell with the Pure library and `IShellConsole`/`IShellFileSystem`/`IShellProcessRunner` implemented over imports from the pocket kernel would be a real isolation boundary instead of a policy. ManagedWasm emits `mwm.*` browser imports rather than WASI and uses WebAssembly exception handling, so PocketOS's engines need both before it can run there.

## Plan

| Phase | Work | Estimate |
|---|---|---|
| 0, this branch | The library seam, the shared binder, the generator, Pure/Machine/Standard, the feature switch, an AOT-clean `easy`, parity tests | done |
| 1, harden | A local `BuildScripts/VerifyAot.ps1` that publishes with warnings as errors and runs `Examples/` natively, as NRI's AotSmoke does; the parity matrix extended to instance members and to Machine in a temporary directory; operators; the skip list surfaced as a build diagnostic | 1-2 days |
| 2, PocketOS | After this merges: `PocketShellHost` sets `Library = ShellLibraries.Pure` or a PocketOS set; `joinpath` host-routed; the end-to-end test's expectation updated | half a day, a Parcel NExT PR |
| 3, distribution | Decide whether Divooka Compute ships `easy` as NativeAOT (one 5 MB file, fast start, the Standard library) or keeps the JIT build for scripts that need any .NET type; `BuildDivooka.ps1` publishes it either way | 1 day |
| 4, WebAssembly | EasyShell side: keep `TerminalState`'s P/Invokes unreachable on browser and WASI, and give a wasm build only Pure. ManagedWasm side: stubs or a WASI target for unused imports, and console input (its M3). Then a PocketOS host over the kernel's imports | EasyShell half a day; the rest is ManagedWasm's roadmap |

## Recommendation

Merge the seam and the generator: under the JIT they change nothing, and they make `easy` a correct NativeAOT program instead of one that builds and then fails its first .NET call. Keep the JIT `easy` on the reflective library for build automation, where reaching any type is the point, and move PocketOS onto a curated library, where reaching any type is the hazard. Treat WebAssembly as reachable but not yet worth chasing: the engine is ready, and the remaining work belongs to ManagedWasm's console and WASI milestones.
