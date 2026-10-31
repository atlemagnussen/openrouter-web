namespace OpenRouter.Web.Traffic;

/// <summary>
/// Development only: fills an empty traffic database with a week of made-up traffic for the sample clients,
/// so the Traffic page has something to show. Deterministic, so screenshots stay comparable.
/// </summary>
public static class SampleTrafficSeeder
{
    private sealed record Device(string Mac, string? Name, string? Network, string Interface, double DownMbit, double UpMbit, Func<int, double> Activity);

    // Activity by local hour (0-23): 0 = idle, 1 = typical peak.
    private static double Evening(int hour) => hour is >= 17 and <= 23 ? 1 : hour is >= 7 and <= 16 ? 0.25 : 0.03;
    private static double Daytime(int hour) => hour is >= 8 and <= 17 ? 1 : hour is >= 18 and <= 22 ? 0.3 : 0.02;
    private static double Night(int hour) => hour is >= 1 and <= 5 ? 1 : 0.02;
    private static double Steady(int hour) => 1;

    private static readonly Device[] Devices =
    [
        new("02:00:00:00:01:10", "desktop", "LAN", "eth1", 18, 1.5, Evening),
        new("02:00:00:00:01:03", "nas", "LAN", "eth1", 1, 25, Night),
        new("02:00:00:00:01:11", "laptop-wifi", "LAN", "eth1", 6, 1.2, Daytime),
        new("02:00:00:00:02:04", "speaker", "LAN", "eth1", 0.4, 0.02, Evening),
        new("02:00:00:00:02:37", "phone", "LAN", "eth1", 3, 0.6, Evening),
        new("02:00:00:00:01:30", "k8s-master-01", "LAN", "eth1", 0.2, 0.15, Steady),
        new("02:00:00:00:01:20", "printer", "LAN", "eth1", 0.01, 0.005, Daytime),
        new("02:00:00:00:03:01", "guest-ap", "LAN-GUEST", "eth2", 4, 0.8, Evening),
        new("02:00:00:00:02:51", "tv", "LAN", "eth1", 9, 0.3, Evening),
        new(TrafficAttribution.UnknownKey, null, null, "eth1", 0.05, 0.02, Steady),
    ];

    public static async Task SeedIfEmptyAsync(TrafficStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await store.IsEmptyAsync(cancellationToken))
            return;

        await store.SetLinkRolesAsync([("eth0", "WAN"), ("eth1", "LAN"), ("eth2", "LAN-GUEST")], cancellationToken);

        var random = new Random(42);
        var end = now.ToUnixTimeSeconds() / 60 * 60;
        var start = end - (long)TimeSpan.FromDays(8).TotalSeconds;
        var samples = new List<MinuteSample>();
        for (var minute = start; minute < end; minute += 60)
        {
            var hour = DateTimeOffset.FromUnixTimeSeconds(minute).ToOffset(TimeSpan.FromHours(1)).Hour;
            // A few bursts: big downloads, the odd evening video call.
            var burst = random.NextDouble() < 0.004 ? 6 + random.NextDouble() * 10 : 1;

            var clients = new List<ClientTraffic>();
            foreach (var device in Devices)
            {
                var activity = device.Activity(hour) * (0.4 + random.NextDouble() * 1.2);
                if (random.NextDouble() > 0.85 && activity < 0.5)
                    continue;
                var factor = device.Name == "desktop" ? burst : 1;
                clients.Add(new ClientTraffic(device.Mac, device.Name, device.Network,
                    DownBytes: Bytes(device.DownMbit * activity * factor),
                    UpBytes: Bytes(device.UpMbit * activity)));
            }

            long Sum(string iface, Func<ClientTraffic, long> bytes) =>
                clients.Where(c => Devices.First(d => d.Mac == c.Key).Interface == iface).Sum(bytes);
            var down = clients.Sum(c => c.DownBytes);
            var up = clients.Sum(c => c.UpBytes);
            samples.Add(new MinuteSample(minute, clients,
            [
                new LinkTraffic("eth0", RxBytes: (long)(down * 1.03), TxBytes: (long)(up * 1.04)),
                new LinkTraffic("eth1", RxBytes: Sum("eth1", c => c.UpBytes), TxBytes: Sum("eth1", c => c.DownBytes)),
                new LinkTraffic("eth2", RxBytes: Sum("eth2", c => c.UpBytes), TxBytes: Sum("eth2", c => c.DownBytes)),
            ]));
        }
        await store.WriteAsync(samples, cancellationToken);
    }

    private static long Bytes(double megabitsPerSecond) => (long)(megabitsPerSecond * 1_000_000 / 8 * 60);
}
