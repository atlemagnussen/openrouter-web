namespace OpenRouter.Web.Systemd;

/// <summary>
/// A unit's state from <c>systemctl show</c>. <see cref="Restarts"/> counts automatic restarts by systemd
/// (<c>Restart=</c>) since the unit was last started by hand.
/// </summary>
public sealed record UnitStatus(
    string Id,
    string LoadState,
    string ActiveState,
    string SubState,
    string Result,
    int Restarts,
    DateTimeOffset? ActiveSince)
{
    public bool IsInstalled => LoadState != "not-found";
}
