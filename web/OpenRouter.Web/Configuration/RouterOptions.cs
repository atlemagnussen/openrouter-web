using System.ComponentModel.DataAnnotations;

namespace OpenRouter.Web.Configuration;

/// <summary>
/// Where the router's services keep their config, state and control sockets.
/// Defaults match Debian 13 packages; Development points at ../samples instead.
/// </summary>
public sealed class RouterOptions
{
    public const string Section = "Router";

    [Required]
    public string KeaConfigPath { get; set; } = "/etc/kea/kea-dhcp4.conf";

    [Required]
    public string KeaLeasesPath { get; set; } = "/var/lib/kea/kea-leases4.csv";

    [Required]
    public string KeaControlSocket { get; set; } = "/run/kea/kea4-ctrl-socket";

    [Required]
    public string RadvdConfigPath { get; set; } = "/etc/radvd.conf";

    [Required]
    public string NftablesConfigPath { get; set; } = "/etc/nftables.conf";

    /// <summary>systemd units the Health page checks. Units that aren't installed are reported as such.</summary>
    public string[] Services { get; set; } = [];

    /// <summary>Health page: a name the router's DNS servers are asked to resolve.</summary>
    [Required]
    public string ProbeDnsName { get; set; } = "debian.org";

    /// <summary>Health page: internet hosts to ping over IPv4 and IPv6.</summary>
    [Required]
    public string ProbeIpv4Host { get; set; } = "1.1.1.1";

    [Required]
    public string ProbeIpv6Host { get; set; } = "2606:4700:4700::1111";

    /// <summary>nft binary, run through sudo to read the traffic accounting table. Must match the sudoers line.</summary>
    [Required]
    public string NftPath { get; set; } = "/usr/sbin/nft";

    /// <summary>Sample per-client traffic every minute. Needs the acct table and sudoers line from the README.</summary>
    public bool TrafficSampling { get; set; } = true;

    /// <summary>SQLite database holding the traffic history.</summary>
    [Required]
    public string TrafficDatabasePath { get; set; } = "/var/lib/openrouter-web/traffic.db";

    /// <summary>Where monthly archive files and downloadable snapshots of the traffic database are written.</summary>
    [Required]
    public string TrafficArchiveDir { get; set; } = "/var/lib/openrouter-web/archive";

    /// <summary>
    /// Move traffic older than this many days out of the live database into monthly archive files.
    /// Null keeps everything in the live database.
    /// </summary>
    [Range(1, 3650)]
    public int? TrafficArchiveAfterDays { get; set; }

    /// <summary>
    /// Development only: read command output (<c>ip -j ...</c>, <c>systemctl show</c>, Kea statistics) from files
    /// in this folder, and <c>/proc</c> and <c>/sys</c> from its <c>proc</c> and <c>sys</c> subfolders.
    /// Live network tests (ping, DNS) are skipped, and the traffic history is filled with made-up data instead of sampled.
    /// </summary>
    public string? SampleDir { get; set; }

    /// <summary>Development only: stop the app's clock at this time, matching the sample lease file.</summary>
    public DateTimeOffset? SampleNow { get; set; }

    public string ProcRoot => SampleDir is { } dir ? Path.Combine(dir, "proc") : "/proc";

    public string SysRoot => SampleDir is { } dir ? Path.Combine(dir, "sys") : "/sys";

    /// <summary>Makes relative paths (used by the Development samples) relative to the content root.</summary>
    public void ResolvePaths(string contentRoot)
    {
        KeaConfigPath = Path.GetFullPath(KeaConfigPath, contentRoot);
        KeaLeasesPath = Path.GetFullPath(KeaLeasesPath, contentRoot);
        KeaControlSocket = Path.GetFullPath(KeaControlSocket, contentRoot);
        RadvdConfigPath = Path.GetFullPath(RadvdConfigPath, contentRoot);
        NftablesConfigPath = Path.GetFullPath(NftablesConfigPath, contentRoot);
        TrafficDatabasePath = Path.GetFullPath(TrafficDatabasePath, contentRoot);
        TrafficArchiveDir = Path.GetFullPath(TrafficArchiveDir, contentRoot);
        if (SampleDir is not null)
            SampleDir = Path.GetFullPath(SampleDir, contentRoot);
    }
}
