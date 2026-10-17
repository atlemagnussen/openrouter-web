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

    /// <summary>
    /// Development only: read <c>ip -j</c> output from <c>ip-neigh.json</c> and <c>ip-addr.json</c>
    /// in this folder instead of running <c>ip</c>.
    /// </summary>
    public string? IpSampleDir { get; set; }

    /// <summary>Development only: stop the app's clock at this time, matching the sample lease file.</summary>
    public DateTimeOffset? SampleNow { get; set; }

    /// <summary>Makes relative paths (used by the Development samples) relative to the content root.</summary>
    public void ResolvePaths(string contentRoot)
    {
        KeaConfigPath = Path.GetFullPath(KeaConfigPath, contentRoot);
        KeaLeasesPath = Path.GetFullPath(KeaLeasesPath, contentRoot);
        KeaControlSocket = Path.GetFullPath(KeaControlSocket, contentRoot);
        RadvdConfigPath = Path.GetFullPath(RadvdConfigPath, contentRoot);
        if (IpSampleDir is not null)
            IpSampleDir = Path.GetFullPath(IpSampleDir, contentRoot);
    }
}
