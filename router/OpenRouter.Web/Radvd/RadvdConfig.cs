namespace OpenRouter.Web.Radvd;

public sealed record RadvdConfig(IReadOnlyList<RadvdInterface> Interfaces);

public sealed record RadvdInterface(
    string Name,
    IReadOnlyList<RadvdOption> Options,
    IReadOnlyList<RadvdSection> Sections)
{
    public IEnumerable<RadvdSection> Prefixes => Of("prefix");
    public IEnumerable<RadvdSection> Rdnss => Of("RDNSS");
    public IEnumerable<RadvdSection> Dnssl => Of("DNSSL");
    public IEnumerable<RadvdSection> Routes => Of("route");

    public string? Option(string name) => RadvdOption.Find(Options, name);

    private IEnumerable<RadvdSection> Of(string kind) =>
        Sections.Where(s => s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A block inside an interface, e.g. <c>prefix 2001:db8::/64 { ... };</c> or <c>RDNSS addr addr { ... };</c>.</summary>
public sealed record RadvdSection(
    string Kind,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<RadvdOption> Options)
{
    public string? Option(string name) => RadvdOption.Find(Options, name);
}

/// <summary><c>Name Value;</c>. Value is empty for bare entries such as addresses in a <c>clients</c> block.</summary>
public sealed record RadvdOption(string Name, string Value)
{
    internal static string? Find(IEnumerable<RadvdOption> options, string name) =>
        options.LastOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
