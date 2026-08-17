using EasyShell.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EasyShell.Parsing
{
    public sealed class Parser
    {
        #region Construction
        private readonly string? _origin;
        public Parser(string? origin)
            => _origin = origin;
        #endregion

        #region Methods
        public Block Parse(string text)
        {
            List<(int lineNo, string line)> lines = SplitLines(text);
            int idx = 0;
            return ParseBlock(lines, ref idx, untilEndKeywords: true, out _);
        }
        #endregion

        #region Routines
        private Block ParseBlock(List<(int lineNo, string line)> lines, ref int idx, bool untilEndKeywords, out string? stoppedBy)
        {
            stoppedBy = null;
            List<Statement> stmts = [];

            while (idx < lines.Count)
            {
                (int lineNo, string? raw) = lines[idx];
                idx++;

                string trimmed = StripComment(raw).Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                List<Token> tokens = Tokenizer.Tokenize(trimmed, lineNo);
                if (tokens.Count == 0)
                    continue;

                string head = tokens[0].Text;

                if (untilEndKeywords && head.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    stoppedBy = "END";
                    break;
                }

                if (untilEndKeywords && head.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
                {
                    stoppedBy = "ELSE";
                    idx--; // Let caller consume
                    break;
                }

                if (untilEndKeywords && head.Equals("ELSEIF", StringComparison.OrdinalIgnoreCase))
                {
                    stoppedBy = "ELSEIF";
                    idx--; // Let caller consume
                    break;
                }

                // Assignment: $NAME = <value...>
                if (tokens[0].Kind == TokKind.VarRef && tokens.Count >= 3 && tokens[1].Text == "=")
                {
                    string varName = tokens[0].Text[1..];
                    Arg valueArg = ParseSingleArg(lineNo, tokens.Skip(2).ToList(), "Assignment");
                    stmts.Add(new AssignStatement(lineNo, varName, valueArg));
                    continue;
                }

                // IF
                if (head.Equals("IF", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Count < 2) throw Err(lineNo, "IF missing condition.");
                    Arg condArg = ParseSingleArg(lineNo, tokens.Skip(1).ToList(), "IF condition");

                    List<(Arg, Block)> branches = [];
                    Block? elseBody = (Block?)null;

                    // Parse IF body
                    Block ifBody = ParseBlock(lines, ref idx, untilEndKeywords: true, out string? stop1);
                    branches.Add((condArg, ifBody));

                    while (stop1 is not null)
                    {
                        if (stop1 == "END")
                            break;

                        // Peek line for ELSE / ELSEIF
                        (int ln2, string? raw2) = lines[idx];
                        List<Token> t2 = Tokenizer.Tokenize(StripComment(raw2).Trim(), ln2);
                        if (t2.Count == 0) { idx++; continue; }

                        if (t2[0].Text.Equals("ELSEIF", StringComparison.OrdinalIgnoreCase))
                        {
                            idx++; // consume ELSEIF line
                            if (t2.Count < 2) throw Err(ln2, "ELSEIF missing condition.");

                            Arg cond2 = ParseSingleArg(ln2, t2.Skip(1).ToList(), "ELSEIF condition");

                            Block body2 = ParseBlock(lines, ref idx, untilEndKeywords: true, out string? stop2);
                            branches.Add((cond2, body2));
                            stop1 = stop2;
                            continue;
                        }

                        if (t2[0].Text.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
                        {
                            // consume ELSE line
                            idx++;
                            Block bodyElse = ParseBlock(lines, ref idx, untilEndKeywords: true, out string? stopE);
                            elseBody = bodyElse;
                            stop1 = stopE;
                            continue;
                        }

                        break;
                    }

                    if (stop1 != "END")
                        throw Err(lineNo, "IF block missing END.");

                    stmts.Add(new IfStatement(lineNo, branches, elseBody));
                    continue;
                }

                // WHILE
                if (head.Equals("WHILE", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Count < 2) throw Err(lineNo, "WHILE missing condition.");
                    Arg condArg = ParseSingleArg(lineNo, tokens.Skip(1).ToList(), "WHILE condition");

                    Block body = ParseBlock(lines, ref idx, untilEndKeywords: true, out string? stop);
                    if (stop != "END")
                        throw Err(lineNo, "WHILE block missing END.");

                    stmts.Add(new WhileStatement(lineNo, condArg, body));
                    continue;
                }

                // FUNC
                if (head.Equals("FUNC", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Count != 2) throw Err(lineNo, "FUNC syntax: FUNC <NAME>");
                    string name = tokens[1].Text;

                    Block body = ParseBlock(lines, ref idx, untilEndKeywords: true, out string? stop);
                    if (stop != "END")
                        throw Err(lineNo, "FUNC block missing END.");

                    stmts.Add(new FuncDefinitionStatement(lineNo, name, body));
                    continue;
                }

                // CALL <func>
                if (head.Equals("CALL", StringComparison.OrdinalIgnoreCase) && tokens.Count == 2 && tokens[1].Kind == TokKind.Word)
                {
                    stmts.Add(new CallFuncStatement(lineNo, tokens[1].Text));
                    continue;
                }

                // Otherwise, parse as command line
                List<Arg> args = ParseArgTokens(lineNo, tokens);
                stmts.Add(new CommandStatement(lineNo, args));
            }

            return new Block(stmts);
        }
        #endregion

        #region Helpers
        private static List<(int lineNo, string line)> SplitLines(string s)
        {
            List<(int, string)> list = [];
            using StringReader sr = new(s);
            string? line;
            int ln = 0;
            while ((line = sr.ReadLine()) is not null)
            {
                ln++;
                list.Add((ln, line));
            }
            return list;
        }

        /// <summary>
        /// Drops a trailing '#' comment. A '#' inside a double-quoted string is kept, so
        /// `print "Issue #42"` survives - naive truncation turned that into an unterminated string.
        /// </summary>
        public static string StripComment(string line)
        {
            bool inQuote = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                // Same minimal escapes the tokenizer honors
                if (inQuote && c == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    inQuote = !inQuote;
                else if (c == '#' && !inQuote)
                    return line[..i];
            }

            return line;
        }

        /// <summary>
        /// Reads exactly one value where the grammar allows exactly one - the value of an
        /// assignment, or a condition.
        ///
        /// <para>Forgetting the parentheses (`IF == $X 1` rather than `IF (== $X 1)`) is the easiest
        /// mistake to make in this language, and it used to surface as LINQ's "Sequence contains
        /// more than one element" from SingleOrDefault: an unhandled crash, with no line number and
        /// nothing pointing at the parentheses. It is a script error like any other.</para>
        /// </summary>
        private Arg ParseSingleArg(int line, List<Token> tokens, string what)
        {
            List<Arg> args = ParseArgTokens(line, tokens);

            return args.Count switch
            {
                1 => args[0],
                0 => throw Err(line, $"{what} is missing its value."),
                _ => throw Err(line, $"{what} takes a single value; wrap the expression in parentheses, e.g. ({string.Join(" ", tokens.Select(t => t.Text))}).")
            };
        }

        private EasyShellException Err(int line, string msg)
        {
            string prefix = _origin is null ? "" : $"{_origin}:";
            return new EasyShellException($"{prefix}{line}: {msg}");
        }

        private static List<Arg> ParseArgTokens(int line, List<Token> tokens)
        {
            // Convert tokens into Args, but also fold parenthesized sub-commands into ExprArg.
            List<Arg> args = [];
            for (int i = 0; i < tokens.Count; i++)
            {
                Token t = tokens[i];

                if (t.Kind == TokKind.LParen)
                {
                    // read until matching RParen
                    int depth = 1;
                    List<Token> inner = [];
                    i++;
                    for (; i < tokens.Count; i++)
                    {
                        if (tokens[i].Kind == TokKind.LParen) depth++;
                        else if (tokens[i].Kind == TokKind.RParen)
                        {
                            depth--;
                            if (depth == 0) break;
                        }
                        inner.Add(tokens[i]);
                    }
                    if (depth != 0)
                        throw new EasyShellException($"{line}: Unmatched '(' in expression.");

                    List<Arg> innerArgs = ParseArgTokens(line, inner);
                    args.Add(new ExprArg(line, innerArgs));
                    continue;
                }

                if (t.Kind == TokKind.VarRef)
                {
                    args.Add(new VarRefArg(line, t.Text[1..]));
                    continue;
                }

                if (t.Kind == TokKind.Word || t.Kind == TokKind.Symbol)
                {
                    args.Add(new AtomArg(line, t.Text, t.WasQuoted));
                    continue;
                }

                // ignore standalone parens handled above
            }
            return args;
        }
        #endregion
    }
}
