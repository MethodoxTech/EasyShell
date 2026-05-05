using EasyShell.Exceptions;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EasyShell.Parsing
{
    public static class Tokenizer
    {
        public static List<Token> Tokenize(string line, int lineNo)
        {
            List<Token> tokens = [];
            StringBuilder sb = new();
            bool inQuote = false;
            bool wasQuoted = false;

            void FlushWord()
            {
                if (sb.Length == 0) 
                    return;
                string text = sb.ToString();
                sb.Clear();

                if (!inQuote && text.StartsWith('$') && text.Length > 1 && IsIdent(text[1..]))
                    tokens.Add(new Token(TokKind.VarRef, text, false));
                else
                    tokens.Add(new Token(TokKind.Word, text, wasQuoted));

                wasQuoted = false;
            }

            static bool TryTwoCharOp(char c1, char c2, out string op)
            {
                op = (c1, c2) switch
                {
                    ('=', '=') => "==",
                    ('!', '=') => "!=",
                    ('>', '=') => ">=",
                    ('<', '=') => "<=",
                    _ => ""
                };
                return op.Length != 0;
            }

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuote)
                {
                    if (c == '"')
                    {
                        inQuote = false;

                        // Emit quoted token even if empty (supports "")
                        tokens.Add(new Token(TokKind.Word, sb.ToString(), WasQuoted: true));
                        sb.Clear();

                        wasQuoted = false;  
                    }
                    else if (c == '\\' && i + 1 < line.Length)
                    {
                        // minimal escapes
                        char n = line[i + 1];
                        if (n == '"' || n == '\\')
                        {
                            sb.Append(n);
                            i++;
                        }
                        else 
                            sb.Append(c);
                    }
                    else
                        sb.Append(c);
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    FlushWord();
                    continue;
                }

                if (c == '"')
                {
                    FlushWord();
                    inQuote = true;
                    wasQuoted = true;
                    continue;
                }

                if (c == '(')
                {
                    FlushWord();
                    tokens.Add(new Token(TokKind.LParen, "(", false));
                    continue;
                }

                if (c == ')')
                {
                    FlushWord();
                    tokens.Add(new Token(TokKind.RParen, ")", false));
                    continue;
                }

                // IMPORTANT: match 2-char operators before 1-char '='
                if (i + 1 < line.Length && TryTwoCharOp(c, line[i + 1], out string? op2))
                {
                    FlushWord();
                    tokens.Add(new Token(TokKind.Symbol, op2, false));
                    i++; // consumed 2 chars
                    continue;
                }

                // Single-char operators you care about
                if (c == '=' || c == '!' || c == '<' || c == '>')
                {
                    FlushWord();
                    tokens.Add(new Token(TokKind.Symbol, c.ToString(), false));
                    continue;
                }

                sb.Append(c);
            }

            if (inQuote)
                throw new EasyShellException($"{lineNo}: Unterminated string literal.");

            FlushWord();
            return tokens;
        }

        private static bool IsIdent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
        }
    }
}
