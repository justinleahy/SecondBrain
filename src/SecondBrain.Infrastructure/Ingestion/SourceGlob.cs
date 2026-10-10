namespace SecondBrain.Infrastructure.Ingestion;

/// <summary>
/// Source and <c>.brainignore</c> globs matched by a linear-time automaton, never a backtracking regex:
/// <c>**/</c> is an optional directory prefix, <c>**</c> any text, <c>*</c> and <c>?</c> stay within one segment.
/// </summary>
public sealed class SourceGlob
{
    public const int MaximumPatternLength = 1024;
    private enum Kind : byte { Literal, One, Star, Any, Directories }
    private readonly Kind[] kinds;
    private readonly char[] literals;

    private SourceGlob(List<(Kind Kind, char Literal)> tokens)
    {
        kinds = tokens.Select(token => token.Kind).ToArray();
        literals = tokens.Select(token => token.Literal).ToArray();
    }

    public static SourceGlob Compile(string pattern)
    {
        if (pattern.Length > MaximumPatternLength) throw new InvalidDataException("source_pattern");
        // Mirrors the former translation order: every "**/" first, then "**", then "*" and "?".
        var tokens = new List<(Kind, char)>(pattern.Length);
        for (var start = 0; ;)
        {
            var directories = pattern.IndexOf("**/", start, StringComparison.Ordinal);
            var end = directories < 0 ? pattern.Length : directories;
            for (var i = start; i < end; i++)
            {
                if (pattern[i] == '*' && i + 1 < end && pattern[i + 1] == '*') { Add(tokens, Kind.Any); i++; }
                else if (pattern[i] == '*') Add(tokens, Kind.Star);
                else tokens.Add(pattern[i] == '?' ? (Kind.One, '\0') : (Kind.Literal, pattern[i]));
            }
            if (directories < 0) break;
            tokens.Add((Kind.Directories, '\0')); start = directories + 3;
        }
        return new SourceGlob(tokens);
    }

    // Adjacent "*" and "**" runs match exactly what one of them matches; collapsing them bounds hostile repetition.
    private static void Add(List<(Kind Kind, char Literal)> tokens, Kind kind)
    {
        if (tokens.Count > 0 && tokens[^1].Kind is Kind.Star or Kind.Any)
        {
            if (kind == Kind.Any) tokens[^1] = (Kind.Any, '\0');
            return;
        }
        tokens.Add((kind, '\0'));
    }

    public static bool IsMatch(string path, string pattern) => Compile(pattern).IsMatch(path);

    /// <summary>Simulates every pattern position at once: O(path × pattern) time, with no timeout to escape.</summary>
    public bool IsMatch(string path)
    {
        var count = kinds.Length;
        var current = new bool[count + 1]; var next = new bool[count + 1];
        // A "**/" position that has consumed text but not yet its closing '/'.
        var inside = new bool[count]; var nextInside = new bool[count];
        current[0] = true; Close(current);
        foreach (var character in path)
        {
            Array.Clear(next); Array.Clear(nextInside);
            var alive = false;
            for (var i = 0; i < count; i++)
            {
                if (inside[i]) { nextInside[i] = alive = true; if (character == '/') next[i + 1] = true; }
                if (!current[i]) continue;
                switch (kinds[i])
                {
                    case Kind.Literal: if (character == literals[i]) next[i + 1] = alive = true; break;
                    case Kind.One: if (character != '/') next[i + 1] = alive = true; break;
                    case Kind.Star: if (character != '/') next[i] = alive = true; break;
                    case Kind.Any: next[i] = alive = true; break;
                    case Kind.Directories: nextInside[i] = alive = true; if (character == '/') next[i + 1] = true; break;
                }
            }
            if (!alive) return false;
            Close(next);
            (current, next) = (next, current); (inside, nextInside) = (nextInside, inside);
        }
        return current[count];
    }

    // Wildcards also match empty text; their epsilon edges only point forward, so one pass closes the set.
    private void Close(bool[] states)
    {
        for (var i = 0; i < kinds.Length; i++)
            if (states[i] && kinds[i] is Kind.Star or Kind.Any or Kind.Directories) states[i + 1] = true;
    }
}
