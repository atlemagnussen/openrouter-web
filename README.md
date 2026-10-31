# OpenRouter Web

A web interface for a self-built Debian router, in the spirit of a consumer router's admin pages:
clients, DHCP leases and reservations, IPv6, per-device traffic and automatic health checks.

It's an ASP.NET Core (.NET 10) Razor Pages app in [`web/`](web/), with a small TypeScript client
(Vite, uPlot) for the charts. See [AGENTS.md](AGENTS.md) for how the router is set up and how the code
is organised.

## Development

Needs the .NET 10 SDK and Node.js (for the chart client).

```sh
cd web
dotnet build          # also builds the client into OpenRouter.Web/wwwroot/dist the first time
dotnet test
dotnet run --project OpenRouter.Web      # http://localhost:4000
```

While working on the TypeScript, keep a rebuild running next to the app:

```sh
cd web/client
npm run dev           # vite build --watch → ../OpenRouter.Web/wwwroot/dist
```

In Development the app reads made-up router data from `web/samples/` and fills the traffic history
with made-up traffic. That needs `web/OpenRouter.Web/appsettings.Development.json` (not in git):

```json
{
  "Router": {
    "KeaConfigPath": "../samples/kea-dhcp4.conf",
    "KeaLeasesPath": "../samples/kea-leases4.csv",
    "KeaControlSocket": "../samples/kea4-ctrl-socket",
    "RadvdConfigPath": "../samples/radvd.conf",
    "NftablesConfigPath": "../samples/nftables.conf",
    "SampleDir": "../samples",
    "SampleNow": "2026-10-18T12:00:00Z",
    "TrafficDatabasePath": "../.data/traffic.db",
    "TrafficArchiveDir": "../.data/archive"
  }
}
```

## Requirements on the router

The app runs as an unprivileged user (planned: `openrouter`; while testing, your own user works).
Everything below is what that user needs. Replace `openrouter` with the user that runs the app,
and `eth1`/`eth2` with your LAN and guest interface names.

### The app's user

Create a system user without a login shell or password. It owns only the app's data directory, and
gets everything else through the grants below.

```sh
sudo adduser --system --group --home /var/lib/openrouter-web --shell /usr/sbin/nologin openrouter
sudo chmod 750 /var/lib/openrouter-web
```

This creates the `openrouter` user and group and the home directory `/var/lib/openrouter-web`, which
also holds the traffic database. After the steps below, check its groups with `id openrouter`. It
should be in `openrouter` and `_kea`.

To try something as that user, for example the Kea and sudo tests below:

```sh
sudo -u openrouter <command>
```

### Read access (Clients, Leases, DHCP, IPv6, Health)

Most of what the app reads is world-readable: `/etc/kea/kea-dhcp4.conf`, `/etc/radvd.conf`,
`/etc/nftables.conf`, `/proc`, `/sys`, `ip -j ...` and `systemctl show`.

**Kea's control socket** (leases and statistics). Connecting to a unix socket needs write permission on
it, and Kea creates it without group write. Put the app's user in Kea's group, and make Kea create the
socket group-writable:

```sh
sudo usermod -aG _kea openrouter
sudo systemctl edit kea-dhcp4-server
```

```ini
[Service]
UMask=0007
```

```sh
sudo systemctl restart kea-dhcp4-server
ls -l /run/kea/kea4-ctrl-socket      # srwxrwx--- _kea _kea
```

Group membership applies to processes started afterwards, so restart the app (or log in again).
For a quick test with your own user instead, an ACL lasts until Kea recreates the socket:

```sh
sudo setfacl -m u:$USER:x /run/kea
sudo setfacl -m u:$USER:rw /run/kea/kea4-ctrl-socket
```

**Ping** (Health page) uses unprivileged ICMP sockets. Debian allows them for everyone; check with
`sysctl net.ipv4.ping_group_range` (should be `0 2147483647`).

### Traffic accounting (Traffic page)

**1. nftables counts bytes per LAN address.** Add this table to `/etc/nftables.conf`, after the
`flush ruleset` line (or put it in its own file and `include` it). It only counts; it never drops or
changes traffic, so it doesn't interfere with the rest of the firewall.

```nft
# Per-address byte counters for OpenRouter Web's Traffic page.
# "up" = sent by a LAN device, "down" = received by it. Idle addresses expire after a day.
# CHANGE THESE to your LAN and guest interfaces (see `ip -br link`, or "interfaces" in kea-dhcp4.conf).
# If nftables.conf already defines them, reuse those instead: define acct_lans = { $LANLINK, $LANLINKGUEST }
define acct_lans = { "eth1", "eth2" }

table inet acct {
    set up4   { type ipv4_addr; size 65535; flags dynamic, timeout; timeout 1d; }
    set down4 { type ipv4_addr; size 65535; flags dynamic, timeout; timeout 1d; }
    set up6   { type ipv6_addr; size 65535; flags dynamic, timeout; timeout 1d; }
    set down6 { type ipv6_addr; size 65535; flags dynamic, timeout; timeout 1d; }

    chain forward {
        type filter hook forward priority -10; policy accept;
        iifname $acct_lans update @up4 { ip saddr counter }
        oifname $acct_lans update @down4 { ip daddr counter }
        iifname $acct_lans update @up6 { ip6 saddr counter }
        oifname $acct_lans update @down6 { ip6 daddr counter }
    }
}
```

Check and apply:

```sh
sudo nft -c -f /etc/nftables.conf            # syntax check only
sudo systemctl reload nftables
sudo nft -j list table inet acct | head -c 600   # elements should show "counter": {"packets": …, "bytes": …}
```

Notes:
- It sees traffic the router forwards: LAN ↔ internet and LAN ↔ guest. Traffic between two devices on
  the same network goes through the switch or access point and is never counted.
- The guest network sits behind the Nest Wifi's own NAT, so all of it shows up as one device (the Nest).
- If the ruleset ever uses a `flowtable` (flow offloading), offloaded packets skip the forward chain
  and won't be counted.
- Reloading nftables resets the counters; the app notices and carries on.

**2. sudoers: one read-only command.** Listing nftables needs root. Allow exactly this command, with
these exact arguments, and nothing else:

```sh
sudo visudo -f /etc/sudoers.d/openrouter-web
```

```
Defaults:openrouter !log_allowed, !pam_session
openrouter ALL=(root) NOPASSWD: /usr/sbin/nft -j list table inet acct
```

The app runs this every 5 seconds for the live chart. The `Defaults` line keeps that out of the logs:
by default sudo logs every allowed command and opens a PAM session for it ("session opened for user
root"), which would add about 17,000 lines a day to the journal. Denied commands are still logged.

Test it as that user: `sudo -n /usr/sbin/nft -j list table inet acct` must print JSON without asking
for a password. If the app runs under systemd, the unit must not set `NoNewPrivileges=true`, or sudo
can't work.

**3. A data directory** for the traffic history (SQLite). `adduser` above already created it as the
user's home. If you run the app as another user while testing, create it yourself:

```sh
sudo install -d -o "$USER" -g "$USER" -m 750 /var/lib/openrouter-web
```

(A systemd unit with `StateDirectory=openrouter-web` also creates it.)

The database grows by roughly one row per active device per minute. Settings (`Router` section in
`appsettings.json`):

| Setting | Default | |
|---|---|---|
| `TrafficDatabasePath` | `/var/lib/openrouter-web/traffic.db` | the live database |
| `TrafficArchiveDir` | `/var/lib/openrouter-web/archive` | monthly archives and snapshots |
| `TrafficArchiveAfterDays` | not set | move months older than this into `traffic-YYYY-MM.db` archive files; not set = keep everything live |
| `TrafficSampling` | `true` | turn sampling off |
| `TrafficLiveSeconds` | `5` | how often the live chart updates (one `sudo nft` call each time); `0` turns the live chart off and reads once a minute |
| `NftPath` | `/usr/sbin/nft` | must match the sudoers line |

Archive files have the same schema as the live database and open on their own, so they can be copied
to another machine. The Traffic page can also download a snapshot of the live database at any time
(SQLite `VACUUM INTO`, safe while the app is writing).

## Deployment

Run this on the router from a checkout of the repo. It builds there, so the router needs the .NET 10 SDK
and Node.js:

```sh
web/publish.sh
```

It builds the client and the app, replaces `/opt/openrouter-web` (owned by root, read-only for
everyone else), and restarts the `openrouter-web` service if it's installed.

### Running as a service

[`web/openrouter-web.service`](web/openrouter-web.service) runs the app as the `openrouter` user,
listening on `http://192.168.1.1:4000`, restarting it if it crashes and starting it at boot. Install it once:

```sh
sudo cp web/openrouter-web.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openrouter-web
```

To listen on another address, override it instead of editing the file, so a newer unit file can be
copied over later:

```sh
sudo systemctl edit openrouter-web
```

```ini
[Service]
Environment=ASPNETCORE_URLS=http://192.168.1.1:4000
```

Day to day:

```sh
web/publish.sh                            # new version: build, install, restart
systemctl status openrouter-web
journalctl -u openrouter-web -f           # logs
sudo systemctl restart openrouter-web
```

The unit keeps some systemd hardening (read-only system directories, no access to home directories,
private /tmp), but nothing that implies `NoNewPrivileges`: the app needs sudo for the one `nft` command.
Stop the service before running the app by hand with `web/run.sh`, or they'll compete for the port.
