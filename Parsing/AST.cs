using System.Collections.Generic;

namespace EasyShell.Parsing
{
    public abstract record Statement(int Line);

    public sealed record Block(List<Statement> Statements);

    public sealed record CommandStatement(int Line, List<Arg> Args) : Statement(Line);

    public sealed record AssignStatement(int Line, string VarName, Arg ValueArg) : Statement(Line);

    public sealed record IfStatement(int Line, List<(Arg Condition, Block Body)> Branches, Block? ElseBody) : Statement(Line);

    public sealed record WhileStatement(int Line, Arg Condition, Block Body) : Statement(Line);

    public sealed record FuncDefinitionStatement(int Line, string Name, Block Body) : Statement(Line);

    public sealed record CallFuncStatement(int Line, string Name) : Statement(Line);

    public abstract record Arg(int Line);
    public sealed record AtomArg(int Line, string Text, bool WasQuoted) : Arg(Line);
    public sealed record VarRefArg(int Line, string Name) : Arg(Line);
    public sealed record ExprArg(int Line, List<Arg> InnerCommandArgs) : Arg(Line);
}
