using EasyShell.Exceptions;
using System;
using System.IO;
using System.Linq;

namespace EasyShell
{
    public static class Program
    {
        #region Configurations
        private const string Version = "0.1.0";

        private static readonly string HelpText = """
            Easy Shell (easyshell) - a tiny scripting language.

            Usage:
              easyshell <script.es>
              easyshell --help
              easyshell --version
              easyshell --repl

            REPL:
              - Type commands line by line.
              - Multi-line blocks (IF/WHILE/FUNC) are entered until END.
              - REPL commands:
                  :help  :vars  :funcs  :exit   # Exits the REPL

            Language:
              - Lines are commands, external executables, or fully-qualified C# member invocations.
              - Comments start with '#'.
              - Variables are global and case-insensitive.

            Variable declarations (strongly typed):
              INTVAR    Name Value
              BOOLVAR   Name Value
              STRINGVAR Name Value
              DOUBLEVAR Name Value
              HANDLEVAR Name <any>   # stores an object (e.g., System.DateTime.Now)

            Variable reference:
              $Name

            Variable assignment:
              $Name = ValueOrExpression

            Expressions (sub-commands):
              Parenthesized command that evaluates to a value:
                (== $X 10)
                (CALL $h ToString)
                (System.String.Format "Time: {0}" (CALL $now ToString))

            Control flow:
              IF <condition>
                ...
              ELSEIF <condition>
                ...
              ELSE
                ...
              END

              WHILE <condition>
                ...
              END

              FUNC <Name>
                ...
                # RETURN exits the function early
              END

              CALL <FuncName>
              CALL <HandleExpr> <MethodName> [args...]

            Execution control:
              EXIT [code]
                Immediately aborts script execution.
                An optional integer code becomes the process exit code.

              RETURN
                Exits the current function only.
                Has no effect outside of FUNC blocks.

            Conditions:
              - boolean literal TRUE/FALSE
              - boolean-equivalent string ("true"/"false")
              - expression in parentheses, e.g.: (== $X 10)

            Built-in comparison commands:
              ==  !=  >  <  >=  <=

            Built-in arithmetic commands:
              +  -  *  /  %  ^
                Head-first, any number of operands: (+ 1 2 3)
                '+' concatenates instead when an operand is not a number.

            Built-in string commands:
              ||  CONCAT  APPEND
                Concatenates every argument: (|| $Name "-" $Version)

            Built-in logic commands:
              NOT AND OR XOR ?? ?:

            Calling .NET:
              System.String.Format "x={0}" 42     # static method
              System.DateTime.Now                 # static property
              System.DateTime.AddDays $When 15    # instance method - $When is the target
              System.DateTime.Year $When          # instance property - $When is the target
              CALL $When ToString "yyyyMMdd"      # instance call on a handle

            External programs:
              A command that is not built-in is run as an external program. Its output is
              streamed live when used as a statement, or captured as a string when used as
              an expression: $text = (git rev-parse HEAD)

              A non-zero exit code aborts the script by default.

            Script arguments:
              easy Publish.easy --incremental
                HASARG "--incremental"      TRUE when that flag was passed (case-insensitive)
                ARG 0                       The argument at that position, or "" when absent

            Global variables:
              $EasyScriptRoot $IsWindows $IsLinux $IsMacOS
              $EasyArgs $EasyArgCount     Arguments passed to the script, joined and counted.

              $LAST_EXIT_CODE             Exit code of the last external program.
              $EasyContinueOnError        Set TRUE to let non-zero exit codes through
                                          instead of aborting. Default FALSE.
              $EasyProcessTimeoutSeconds  Wall-clock limit per external program; the
                                          process tree is killed on expiry. 0/unset = no limit.
            """;
        #endregion

        #region Entry
        public static int Main(string[] args)
        {
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(HelpText);
                return 0;
            }

            if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(Version);
                return 0;
            }

            // Repl
            if (args.Length == 0 || args.Contains("--repl", StringComparer.OrdinalIgnoreCase))
            {
                Runtime rt = GetPresetRuntime(Directory.GetCurrentDirectory(), []);
                return EasyShellRepl.Run(HelpText, Version, rt);
            }

            // Process shell script
            string scriptPath = args[0];
            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Script not found: {scriptPath}");
                return 2;
            }
            try
            {
                string scriptRoot = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? "";
                // Everything after the script path belongs to the script, not to easy itself
                Runtime rt = GetPresetRuntime(scriptRoot, [.. args.Skip(1)]);
                EasyShellEngine engine = new(rt);

                string text = File.ReadAllText(scriptPath);
                int code = engine.Run(text, scriptPath);
                return code;
            }
            catch (EasyShellException ex)
            {
                Console.Error.WriteLine($"(Error) {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unhandled error: {ex}");
                return 3;
            }
        }
        #endregion

        #region Helpers
        private static Runtime GetPresetRuntime(string scriptRoot, string[] scriptArguments)
        {
            Runtime rt = new();
            rt.InjectString("$EasyScriptRoot", scriptRoot);
            rt.InjectBool("$IsWindows", System.OperatingSystem.IsWindows());
            rt.InjectBool("$IsLinux", System.OperatingSystem.IsLinux());
            rt.InjectBool("$IsMacOS", System.OperatingSystem.IsMacOS());

            // Injected as a string for the common "did they pass anything at all" check; HASARG and ARG read the list itself
            Commands.CommonUtilities.SetScriptArguments(scriptArguments);
            rt.InjectString("$EasyArgs", string.Join(" ", scriptArguments));
            rt.InjectInt("$EasyArgCount", scriptArguments.Length);
            return rt;
        }
        #endregion
    }
}