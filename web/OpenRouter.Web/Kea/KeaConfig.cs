namespace OpenRouter.Web.Kea;

/// <summary>The parts of a Kea DHCPv4 config the app shows. Lifetimes are in seconds.</summary>
public sealed record KeaConfig(
    IReadOnlyList<string> Interfaces,
    int? ValidLifetime,
    int? MaxValidLifetime,
    IReadOnlyList<KeaSubnet> Subnets,
    IReadOnlyList<KeaReservation> GlobalReservations)
{
    public static KeaConfig Empty { get; } = new([], null, null, [], []);

    public IEnumerable<KeaReservation> AllReservations =>
        Subnets.SelectMany(s => s.Reservations).Concat(GlobalReservations);

    /// <summary>Interface names Kea serves; entries may be "eth1/192.168.1.1" or the wildcard "*".</summary>
    public IEnumerable<string> InterfaceNames =>
        Interfaces.Select(i => i.Split('/')[0]).Where(i => i != "*");

    public KeaSubnet? SubnetById(int id) => Subnets.FirstOrDefault(s => s.Id == id);
}

public sealed record KeaSubnet(
    int Id,
    string Subnet,
    string? Interface,
    string? Name,
    IReadOnlyList<string> Pools,
    IReadOnlyList<KeaOption> Options,
    int? ValidLifetime,
    int? MaxValidLifetime,
    IReadOnlyList<KeaReservation> Reservations)
{
    public string? Option(string name) => Options.FirstOrDefault(o => o.Name == name)?.Data;
}

public sealed record KeaOption(string Name, string Data);

/// <summary>A host reservation. Only hw-address reservations carry a MAC; others are identified by client-id etc.</summary>
public sealed record KeaReservation(string? Hostname, string? HwAddress, string? IpAddress);
