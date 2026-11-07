namespace OpenRouter.Web.Radvd;

public sealed class RadvdParseException(string message, int line)
    : FormatException($"radvd.conf line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// Parses radvd.conf: <c>interface NAME { option; section { option; }; };</c> with <c>#</c> comments.
/// Sections don't nest in radvd, so one level below the interface is enough.
/// </summary>
public static class RadvdParser
{
    public static RadvdConfig Parse(string text)
    {
        var tokens = new TokenStream(text);
        var interfaces = new List<RadvdInterface>();

        while (!tokens.AtEnd)
        {
            tokens.Expect("interface");
            var name = tokens.Next("an interface name");
            if (!name.IsWord)
                throw new RadvdParseException($"expected an interface name, found \"{name.Text}\"", name.Line);

            tokens.Expect("{");
            var options = new List<RadvdOption>();
            var sections = new List<RadvdSection>();

            while (!tokens.Peek("\"}\" closing the interface").Is("}"))
            {
                var words = tokens.Words();
                if (tokens.Next("\";\" or \"{\"").Is("{"))
                {
                    sections.Add(new RadvdSection(words[0], words[1..], ParseSectionBody(tokens)));
                    tokens.Expect("}");
                    tokens.SkipSemicolon();
                }
                else
                {
                    options.Add(ToOption(words));
                }
            }
            tokens.Expect("}");
            tokens.SkipSemicolon();

            interfaces.Add(new RadvdInterface(name.Text, options, sections));
        }
        return new RadvdConfig(interfaces);
    }

    private static List<RadvdOption> ParseSectionBody(TokenStream tokens)
    {
        var options = new List<RadvdOption>();
        while (!tokens.Peek("\"}\" closing the section").Is("}"))
        {
            var words = tokens.Words();
            tokens.Expect(";");
            options.Add(ToOption(words));
        }
        return options;
    }

    private static RadvdOption ToOption(List<string> words) => new(words[0], string.Join(' ', words.Skip(1)));

    private readonly record struct Token(string Text, int Line)
    {
        public bool Is(string text) => Text.Equals(text, StringComparison.OrdinalIgnoreCase);
        public bool IsWord => Text is not ("{" or "}" or ";");
    }

    private sealed class TokenStream(string text)
    {
        private readonly Queue<Token> _tokens = new(Tokenize(text));
        private readonly int _lastLine = text.Count(c => c == '\n') + 1;

        public bool AtEnd => _tokens.Count == 0;

        public Token Next(string expected) =>
            _tokens.TryDequeue(out var token) ? token : throw EndOfFile(expected);

        public Token Peek(string expected) =>
            _tokens.TryPeek(out var token) ? token : throw EndOfFile(expected);

        public void Expect(string text)
        {
            var token = Next($"\"{text}\"");
            if (!token.Is(text))
                throw new RadvdParseException($"expected \"{text}\", found \"{token.Text}\"", token.Line);
        }

        public void SkipSemicolon()
        {
            if (_tokens.TryPeek(out var token) && token.Is(";"))
                _tokens.Dequeue();
        }

        /// <summary>Reads one or more words up to (not including) the next <c>;</c>, <c>{</c> or <c>}</c>.</summary>
        public List<string> Words()
        {
            var words = new List<string>();
            while (_tokens.TryPeek(out var token) && token.IsWord)
                words.Add(_tokens.Dequeue().Text);

            if (words.Count == 0)
            {
                var found = Peek("an option or section");
                throw new RadvdParseException($"expected an option or section, found \"{found.Text}\"", found.Line);
            }
            return words;
        }

        private RadvdParseException EndOfFile(string expected) =>
            new($"expected {expected}, found end of file", _lastLine);

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
                else if (c == '#')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;
                }
                else if (c is '{' or '}' or ';')
                {
                    yield return new Token(c.ToString(), line);
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or ';' or '#'))
                        i++;
                    yield return new Token(text[start..i], line);
                }
            }
        }
    }
}
