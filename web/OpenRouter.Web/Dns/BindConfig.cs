namespace OpenRouter.Web.Dns;

/// <summary>A statement in a bind config: words, then optionally a <c>{ ... }</c> block of statements.</summary>
public sealed record BindStatement(IReadOnlyList<string> Words, IReadOnlyList<BindStatement>? Block)
{
    public string Name => Words.Count > 0 ? Words[0] : "";

    public IEnumerable<BindStatement> Children(string name) =>
        (Block ?? []).Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The plain entries in the block, e.g. the addresses in <c>forwarders { 1.1.1.1; 8.8.8.8; };</c>.</summary>
    public IEnumerable<string> Entries => (Block ?? []).Where(s => s.Block is null).Select(s => string.Join(' ', s.Words));
}

/// <summary>The parts of bind's <c>options { }</c> the DNS page uses.</summary>
public sealed record BindOptions(
    IReadOnlyList<string> Forwarders,
    IReadOnlyList<string> ListenOn,
    IReadOnlyList<string> ListenOnV6,
    string? DnssecValidation,
    string? Recursion,
    IReadOnlyList<string> AllowRecursion,
    IReadOnlyList<string> StatisticsChannels)
{
    public static BindOptions Empty { get; } = new([], [], [], null, null, [], []);
}
