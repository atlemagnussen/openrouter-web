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
}
