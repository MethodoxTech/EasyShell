using EasyShell.Hosting;
using System;
using System.Collections.Generic;

namespace EasyShell
{
    /// <summary>
    /// Pathname expansion: <c>cat pets/*</c> hands <c>cat</c> every file in <c>pets</c>, the way every shell does.
    ///
    /// <para>The rule for WHEN it happens is the same shape as the pipe rule in <see cref="Pipelines"/>, with one more clause: <b>a word is expanded only when it is an argument to a program, in non-head position, and unquoted.</b> EasyShell's arithmetic is an ordinary command named <c>*</c>, so <c>(* 2 3)</c> must stay a multiplication - and it does twice over, because <c>*</c> is the head there and because an operator is not a program. Language commands (arithmetic, comparisons, declarations, keywords, host built-ins such as <c>rm</c> and <c>removeall</c>, functions and .NET calls) receive their words verbatim: their arguments are values, not file lists, and <c>removeall $dir *.xml</c> has always meant the pattern, not whatever the working directory happens to hold. Quoting is the escape everywhere: <c>echo "*"</c> prints a star.</para>
    ///
    /// <para>The rule for WHAT it does is POSIX's. <c>*</c>, <c>?</c> and <c>[abc]</c>/<c>[a-z]</c>/<c>[!a-z]</c> (or <c>[^a-z]</c>) work in any segment of a path, so <c>docs/*/notes*.txt</c> walks every folder under <c>docs</c>. A name that starts with '.' is matched only by a segment that starts with '.' itself. Matches come back sorted, ordinally, so the order is the same on every host. A word that matches nothing is passed on unchanged, so <c>cat x*</c> reports a missing file in <c>cat</c>'s own words rather than running <c>cat</c> with no arguments at all.</para>
    ///
    /// <para>Every lookup goes through <see cref="IShellFileSystem"/>, so a virtual machine expands against its own disk and <c>easy</c> against the real one. Literal segments keep the text the user typed - separators included - and only the segments that were patterns are replaced by the names found, so <c>pets/*</c> becomes <c>pets/cat.txt</c> on every host rather than <c>pets\cat.txt</c> on Windows.</para>
    /// </summary>
    public static class Globbing
    {
        /// <summary>Whether <paramref name="word"/> contains anything that would make it a pattern: '*', '?', or a '[' that is closed by a ']'.</summary>
        public static bool HasPattern(string word)
        {
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (c is '*' or '?') return true;
                if (c == '[' && TryMatchClass(word, i, '\0', out _, out _)) return true;
            }
            return false;
        }

        /// <summary>
        /// Every path <paramref name="word"/> matches, sorted ordinally; empty when it matches nothing or is not a pattern at all. The caller decides what an empty answer means - a shell keeps the word as typed.
        /// </summary>
        public static List<string> Expand(IShellFileSystem fileSystem, string word)
        {
            List<string> matches = [];
            if (string.IsNullOrEmpty(word) || !HasPattern(word))
                return matches;

            List<(string Text, string Separator)> segments = Split(word, BackslashSeparates(fileSystem));

            // Each candidate is the text the user would have typed to name it: literal segments verbatim, pattern segments replaced by what was found.
            List<string> candidates = [""];
            bool lastWasPattern = false;
            for (int i = 0; i < segments.Count; i++)
            {
                (string text, string separator) = segments[i];
                bool isLast = i == segments.Count - 1;
                lastWasPattern = HasPattern(text);

                List<string> next = [];
                if (!lastWasPattern)
                {
                    foreach (string prefix in candidates)
                        next.Add(prefix + text + separator);
                }
                else
                {
                    // Only a directory can have something after it, and a trailing separator (`pets/*/`) asks for directories by name.
                    bool directoriesOnly = !isLast || separator.Length > 0;
                    foreach (string prefix in candidates)
                        foreach (string name in ListNames(fileSystem, prefix.Length == 0 ? "." : prefix, directoriesOnly))
                            if (Matches(text, name))
                                next.Add(prefix + name + separator);
                }

                candidates = next;
                if (candidates.Count == 0)
                    return matches;
            }

            // A literal tail (`*/notes.txt`) was never looked up; keep only the paths that exist.
            foreach (string candidate in candidates)
                if (lastWasPattern || fileSystem.FileExists(candidate) || fileSystem.DirectoryExists(candidate))
                    matches.Add(candidate);

            matches.Sort(StringComparer.Ordinal);
            return matches;
        }

        /// <summary>
        /// Whether one name matches one pattern segment. A name starting with '.' needs a pattern starting with '.', which is what keeps <c>*</c> from sweeping up hidden files.
        /// </summary>
        public static bool Matches(string pattern, string name)
        {
            if (name.Length > 0 && name[0] == '.' && (pattern.Length == 0 || pattern[0] != '.'))
                return false;

            // Iterative match with one backtrack point: the most recent '*' absorbs one more character each time the rest fails, which is linear-ish rather than exponential.
            int p = 0, n = 0, starP = -1, starN = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length)
                {
                    char c = pattern[p];
                    if (c == '*')
                    {
                        starP = p++;
                        starN = n;
                        continue;
                    }
                    if (c == '?')
                    {
                        p++;
                        n++;
                        continue;
                    }
                    if (c == '[' && TryMatchClass(pattern, p, name[n], out int end, out bool hit))
                    {
                        if (hit)
                        {
                            p = end;
                            n++;
                            continue;
                        }
                    }
                    else if (c == name[n])   // includes a '[' that never closes, which is literal
                    {
                        p++;
                        n++;
                        continue;
                    }
                }

                if (starP < 0)
                    return false;
                p = starP + 1;
                n = ++starN;
            }

            while (p < pattern.Length && pattern[p] == '*')
                p++;
            return p == pattern.Length;
        }

        /// <summary>
        /// A bracket expression starting at <paramref name="start"/>: false when it never closes (the '[' is then an ordinary character); otherwise <paramref name="end"/> is the index after its ']' and <paramref name="hit"/> says whether <paramref name="c"/> is in it. A ']' straight after the '[' (or after the negation) is a member, not the end.
        /// </summary>
        private static bool TryMatchClass(string pattern, int start, char c, out int end, out bool hit)
        {
            end = start;
            hit = false;

            int i = start + 1;
            bool negate = i < pattern.Length && pattern[i] is '!' or '^';
            if (negate) i++;

            bool matched = false;
            bool first = true;
            while (i < pattern.Length)
            {
                char lo = pattern[i];
                if (lo == ']' && !first)
                {
                    end = i + 1;
                    hit = matched != negate;
                    return true;
                }

                if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
                {
                    char hi = pattern[i + 2];
                    if (lo <= c && c <= hi) matched = true;
                    i += 3;
                }
                else
                {
                    if (lo == c) matched = true;
                    i++;
                }
                first = false;
            }
            return false;
        }

        /// <summary>
        /// The word cut at its separators, each segment carrying the separator run that followed it. A leading separator becomes an empty first segment, so an absolute path stays absolute; a trailing one stays on the last segment.
        /// </summary>
        private static List<(string Text, string Separator)> Split(string word, bool backslashSeparates)
        {
            bool IsSeparator(char c) => c == '/' || (backslashSeparates && c == '\\');

            List<(string, string)> segments = [];
            int i = 0;
            while (i < word.Length)
            {
                int textStart = i;
                while (i < word.Length && !IsSeparator(word[i])) i++;
                int separatorStart = i;
                while (i < word.Length && IsSeparator(word[i])) i++;
                segments.Add((word[textStart..separatorStart], word[separatorStart..i]));
            }
            return segments;
        }

        /// <summary>
        /// '/' separates on every filesystem; '\' only on one that says so. Asking the filesystem rather than the platform is the point: a virtual disk on a Windows host is still '/'-only, and a '\' in one of its names is just a character.
        /// </summary>
        private static bool BackslashSeparates(IShellFileSystem fileSystem)
        {
            try { return fileSystem.GetFileName("a\\b") == "b"; }
            catch (Exception) { return false; }
        }

        /// <summary>The names in one folder. A folder that cannot be read contributes nothing, as in every shell - a pattern is a question, not a demand.</summary>
        private static List<string> ListNames(IShellFileSystem fileSystem, string directory, bool directoriesOnly)
        {
            List<string> names = [];
            try
            {
                if (!fileSystem.DirectoryExists(directory))
                    return names;
                foreach (string path in fileSystem.EnumerateDirectories(directory))
                    names.Add(fileSystem.GetFileName(path));
                if (!directoriesOnly)
                    foreach (string path in fileSystem.EnumerateFiles(directory, "*", recursive: false))
                        names.Add(fileSystem.GetFileName(path));
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                // Unreadable: no matches from here.
            }
            return names;
        }
    }
}
