using System.Net;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;

namespace OpenRouter.Web.Pages;

public sealed class Ipv6Model(RadvdService radvd, IpCommand ip, ILogger<Ipv6Model> logger) : RouterPageModel(logger)
{
    public sealed record InterfaceView(
        RadvdInterface Radvd,
        NetInterface? Interface,
        IReadOnlyList<NetAddress> Addresses,
        IReadOnlyList<PrefixView> Prefixes);

    /// <param name="Covering">Interface addresses whose prefix covers the advertised one, i.e. why the router can route it.</param>
    /// <param name="IsAutomatic"><c>::/64</c>: radvd advertises every /64 on the interface.</param>
    public sealed record PrefixView(RadvdSection Section, string Prefix, IReadOnlyList<NetAddress> Covering, bool IsAutomatic);

    public IReadOnlyList<InterfaceView> Interfaces { get; private set; } = [];

    /// <summary>False when <c>ip addr</c> failed, so missing addresses aren't reported as problems.</summary>
    public bool HasAddresses { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var config = await TryLoadAsync("radvd.conf", () => radvd.GetConfigAsync(cancellationToken));
        var nics = await TryLoadAsync("interface addresses", () => ip.GetInterfacesAsync(cancellationToken));
        HasAddresses = nics is not null;
        if (config is null)
            return;

        Interfaces = config.Interfaces.Select(i =>
        {
            var nic = nics?.FirstOrDefault(n => n.Name == i.Name);
            var addresses = nic?.Addresses.Where(a => a.IsIpv6 && a.Scope == "global").ToList() ?? [];
            var prefixes = i.Prefixes.Select(section =>
            {
                var text = section.Arguments.FirstOrDefault() ?? "";
                if (!IpPrefix.TryParse(text, out var prefix))
                    return new PrefixView(section, text, [], IsAutomatic: false);

                var isAutomatic = prefix.Value.Network.Equals(IPAddress.IPv6Any);
                var covering = isAutomatic
                    ? addresses.Where(a => a.PrefixLength == prefix.Value.Length).ToList()
                    : addresses.Where(a => a.Covers(prefix.Value)).ToList();
                return new PrefixView(section, text, covering, isAutomatic);
            }).ToList();
            return new InterfaceView(i, nic, addresses, prefixes);
        }).ToList();
    }

    public bool IsRouterAddress(string address) =>
        Interfaces.Any(i => i.Addresses.Any(a => a.Address == address));
}
