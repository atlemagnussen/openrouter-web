using OpenRouter.Web.Kea;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Probes;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Systemd;

namespace OpenRouter.Web.Health;

/// <summary>Everything the checks look at. A null source couldn't be read and its checks are left out.</summary>
public sealed record HealthInput(
    KeaConfig? KeaConfig,
    IReadOnlyList<KeaLease>? Leases,
    KeaStatistics? KeaStatistics,
    RadvdConfig? Radvd,
    IReadOnlyList<Neighbour>? Neighbours,
    IReadOnlyList<NetInterface>? Interfaces,
    IReadOnlyList<LinkStats>? Links,
    IReadOnlyList<NicInfo>? Nics,
    IReadOnlyList<DefaultRoute>? Routes4,
    IReadOnlyList<DefaultRoute>? Routes6,
    KernelSnapshot? Kernel,
    IReadOnlyList<UnitStatus>? Units,
    string? Firewall,
    ProbeResults? Probes,
    DateTimeOffset Now);

public static class HealthReport
{
    public static IReadOnlyList<HealthArea> Build(HealthInput input)
    {
        var ports = Ports(input.Routes4, input.KeaConfig, input.Radvd);
        var wan = input.Routes4?.FirstOrDefault()?.Device;
        var wanMtu = input.Links?.FirstOrDefault(l => l.Name == wan)?.Mtu;

        HealthArea[] areas =
        [
            new("Internet", "Tested from the router: routes, ping and the DNS servers clients are given.",
                ConnectivityHealth.Check(input.Routes4, input.Routes6, input.Probes).ToList()),
            new("Network links", "The router's own ports. Counters are since boot.",
                LinkHealth.Check(ports, input.Links, input.Nics).ToList()),
            new("DHCP (IPv4)", "Kea's address pools, conflicts and counters.",
                DhcpHealth.Check(input.KeaConfig, input.Leases, input.KeaStatistics, input.Neighbours, input.Now).ToList()),
            new("IPv6", "radvd's router advertisements, checked against what phones accept.",
                Ipv6Health.Check(input.Radvd, input.Interfaces, input.Routes6).ToList()),
            new("Router", "Kernel tables, CPU and memory.",
                SystemHealth.Check(input.Kernel, input.Neighbours).ToList()),
            new("Services", "The router's daemons, from systemd.",
                ServiceHealth.Check(input.Units, input.Now).ToList()),
            new("Firewall", "A quick text check of nftables.conf for rules whose absence causes stalls.",
                FirewallHealth.Check(input.Firewall, wanMtu).ToList()),
        ];
        return areas.Where(a => a.Checks.Count > 0).ToList();
    }

    /// <summary>The ports worth checking: WAN (the default route's interface) and the networks Kea and radvd serve.</summary>
    public static IReadOnlyList<LinkHealth.Port> Ports(
        IReadOnlyList<DefaultRoute>? routes4, KeaConfig? kea, RadvdConfig? radvd)
    {
        var ports = new List<LinkHealth.Port>();
        void Add(string? name, string role)
        {
            if (!string.IsNullOrEmpty(name) && ports.All(p => p.Name != name))
                ports.Add(new(name, role));
        }

        foreach (var route in routes4 ?? [])
            Add(route.Device, "WAN");
        foreach (var subnet in kea?.Subnets ?? [])
            Add(subnet.Interface, subnet.Name ?? "LAN");
        foreach (var name in kea?.InterfaceNames ?? [])
            Add(name, "LAN");
        foreach (var config in radvd?.Interfaces ?? [])
            Add(config.Name, "LAN");
        return ports;
    }

    /// <summary>DNS servers clients are told to use: Kea's domain-name-servers and radvd's RDNSS.</summary>
    public static IReadOnlyList<string> DnsServers(KeaConfig? kea, RadvdConfig? radvd) =>
        (kea?.Subnets ?? [])
            .SelectMany(s => (s.Option("domain-name-servers") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat((radvd?.Interfaces ?? []).SelectMany(i => i.Rdnss).SelectMany(r => r.Arguments))
            .Distinct()
            .ToList();
}
