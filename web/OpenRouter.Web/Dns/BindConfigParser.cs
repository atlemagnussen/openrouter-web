namespace OpenRouter.Web.Dns;

public sealed class BindParseException(string message, int line)
    : FormatException($"bind config line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// Parses bind's config format (<c>named.conf</c>): statements end with <c>;</c>, blocks are <c>{ ... };</c>,
/// comments are <c>//</c>, <c>#</c> and <c>/* */</c>, and strings are quoted. Includes are not followed.
/// </summary>
public static class BindConfigParser
{
    public static IReadOnlyList<BindStatement> Parse(string text)
    {
        var tokens = new Queue<Token>(Tokenize(text));
        var statements = ParseBlock(tokens, closing: false);
        return statements;
    }

    /// <summary>Reads the <c>options { }</c> statement, wherever it is in the file.</summary>
    public static BindOptions ParseOptions(string text)
    {
        var options = Parse(text).FirstOrDefault(s => s.Name == "options");
        if (options is null)
            return BindOptions.Empty;

        IReadOnlyList<string> Entries(string name) => options.Children(name).SelectMany(s => s.Entries).ToList();
        string? Value(string name) => options.Children(name).LastOrDefault() is { Words.Count: > 1 } s ? s.Words[1] : null;

        return new BindOptions(
            Forwarders: Entries("forwarders"),
            ListenOn: Entries("listen-on"),
            ListenOnV6: Entries("listen-on-v6"),
            DnssecValidation: Value("dnssec-validation"),
            Recursion: Value("recursion"),
            AllowRecursion: Entries("allow-recursion"),
            // statistics-channels is a top-level statement: { inet 127.0.0.1 port 8053 allow { 127.0.0.1; }; };
            StatisticsChannels: Parse(text).Where(s => s.Name == "statistics-channels")
                .SelectMany(s => s.Block ?? []).Select(s => string.Join(' ', s.Words)).ToList());
    }

    private static List<BindStatement> ParseBlock(Queue<Token> tokens, bool closing)
    {
        var statements = new List<BindStatement>();
        while (true)
        {
            if (!tokens.TryPeek(out var next))
            {
                if (closing)
                    throw new BindParseException("expected \"}\", found end of file", 0);
                return statements;
            }
            if (next.Text == "}" && !next.Quoted)
            {
                if (!closing)
                    throw new BindParseException("unexpected \"}\"", next.Line);
                tokens.Dequeue();
                return statements;
            }

            var words = new List<string>();
            while (tokens.TryPeek(out var token) && (token.Quoted || token.Text is not (";" or "{" or "}")))
                words.Add(tokens.Dequeue().Text);

            if (!tokens.TryDequeue(out var end))
                throw new BindParseException("expected \";\", found end of file", next.Line);
            if (end.Text == ";")
            {
                if (words.Count > 0)
                    statements.Add(new BindStatement(words, null));
            }
            else if (end.Text == "{")
            {
                var block = ParseBlock(tokens, closing: true);
                // The ';' after a block is required by bind, but tolerate a missing one.
                if (tokens.TryPeek(out var semicolon) && semicolon.Text == ";" && !semicolon.Quoted)
                    tokens.Dequeue();
                statements.Add(new BindStatement(words, block));
            }
            else
            {
                throw new BindParseException($"unexpected \"{end.Text}\"", end.Line);
            }
        }
    }

    private readonly record struct Token(string Text, int Line, bool Quoted = false);

    private static IEnumerable<Token> Tokenize(string text)
    {
        var line = 1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                line += text.AsSpan(i, stop - i).Count('\n');
                i = stop;
            }
            else if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                    throw new BindParseException("unterminated string", line);
                yield return new Token(text[(i + 1)..end], line, Quoted: true);
                line += text.AsSpan(i, end - i).Count('\n');
                i = end + 1;
            }
            else if (c is '{' or '}' or ';')
            {
                yield return new Token(c.ToString(), line);
                i++;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or ';' or '"' or '#'))
                {
                    if (text[i] == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
                        break;
                    i++;
                }
                yield return new Token(text[start..i], line);
            }
        }
    }
}
