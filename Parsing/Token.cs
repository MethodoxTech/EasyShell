namespace EasyShell.Parsing
{
    public enum TokKind { Word, Symbol, VarRef, LParen, RParen }

    public readonly record struct Token(TokKind Kind, string Text, bool WasQuoted);
}
