# OpenRouter Web

A web interface for a self-built Linux router, in the spirit of a consumer router's admin pages.
It starts read-only (show clients, leases, DHCP and IPv6 config) and will grow editing,
authentication and background tasks.

- `router/`: the router app (.NET 10, ASP.NET Core Razor Pages), its tests, the TypeScript chart client
  (`router/client/`), made-up sample data, and the deploy scripts (`publish.sh`, `run.sh`, `openrouter-web.service`).
- `client/`: `netcheck`, .NET 10 tools for a Linux laptop that measure the connection from the device side
  (ping, DNS, HTTPS timing, Wi-Fi via `iw`, scans of the networks around) into their own SQLite file, to compare
  with the router's history. `OpenRouter.Client.Core` (probes, storage, report), `OpenRouter.Client` (the `netcheck`
  command line and its `daemon` collector, a systemd user service) and `OpenRouter.Client.App` (an Avalonia desktop
  app with ScottPlot charts that only reads the database). Separate solution (`client/OpenRouter.Client.slnx`),
  installed with `client/install.sh`; see `client/README.md`. It shares `DnsMessage.cs` with the web app by linking
  the file, not by a project reference. Avalonia and ScottPlot are the client's only extra packages besides SQLite.

**This repository is public.** Never commit real router data: config files, leases, MAC addresses,
hostnames, IPv6 prefixes or public IPs. Use the made-up data in `router/samples/`.

## The router

Debian 13 (trixie) box with three NICs: WAN, LAN and a guest LAN. Interface names are pinned
with systemd `.link` files and configured with ifupdown (`/etc/network/interfaces`).

| Concern | Software | Where the app reads it |
|---|---|---|
| IPv4 DHCP | Kea 2.6 (`kea-dhcp4-server`, runs as `_kea`) | Config `/etc/kea/kea-dhcp4.conf` (JSON); control socket `/run/kea/kea4-ctrl-socket`; leases via the `lease_cmds` hook, file `/var/lib/kea/kea-leases4.csv` as fallback |
| IPv6 addressing | radvd (SLAAC + RDNSS, no DHCPv6 server) | `/etc/radvd.conf` |
| WAN DHCP + IPv6 prefix delegation | dhcpcd (assigns a /64 from the ISP's delegated prefix to the LAN) | `ip -j addr`, dhcpcd state |
| Who is online | Kernel neighbour table | `ip -j neigh` (IPv4 + IPv6, no root needed) |
| DNS | bind9, local zones + forwarders | `/etc/bind/named.conf.options` (forwarders, listen addresses); statistics channel `http://127.0.0.1:8053/json/v1`; probes are plain DNS lookups |
| Firewall / NAT | nftables, `/etc/nftables.conf` | text check on the Health page; table `inet acct` counts bytes per LAN address for the Traffic page (README) |

Facts that shape the code:
- **Kea 2.6 has no `host_cmds` hook**, so reservations can't be changed through the API.
  To edit DHCP settings: write the JSON config, run `config-test` over the control socket,
  then `config-reload`. Kea accepts comments in its config, but the app's rewrites drop them,
  so per-host notes belong in `user-context`.
- **Hostnames in leases are whatever the client sent** and may end in `.`. Prefer the
  reservation's `hostname`, and normalise the rest.
- **IPv6 clients have no leases.** They come only from the neighbour table, and one device
  often has several addresses (privacy extensions). The Clients page groups everything by MAC.
- **The public IPv6 prefix can change** (ISP prefix delegation). Never store it; read it from the
  LAN interface. The ULA prefix is stable and is what DNS uses.
- `radvd.conf` uses radvd's own brace format. Parse and write only what's needed
  (`interface`, `prefix`, `RDNSS`, `DNSSL` blocks, `key value;` options, `#` comments).
  Validate with `radvd --configtest` before `systemctl reload radvd`.

## Stack and conventions

- .NET 10, ASP.NET Core **Razor Pages** (not Blazor, not MVC controllers, no SPA).
  Server-rendered HTML; forms post back to page handlers. If something needs to update live,
  use htmx partials rather than adding a JS framework.
- Charts and other browser code are TypeScript in `router/client/` (Vite, uPlot), built into
  `OpenRouter.Web/wwwroot/dist/` (gitignored) with stable file names; pages include them with
  `asp-append-version`. Scripts enhance server-rendered pages and fetch JSON from page handlers
  (`?handler=...`); they don't take over the page. Chart colours come from `--series-*` tokens in `site.css`.
- Plain CSS in `wwwroot/css/site.css` with light/dark colour tokens on `:root`. No Bootstrap,
  no jQuery, no CDN assets (the router may be offline).
- Nullable enabled, file-scoped namespaces, records for data models, `sealed` by default.
- Paths and sockets come from `RouterOptions` (`Router` section in appsettings). Development
  points at `router/samples/`; never hard-code system paths elsewhere.
- Keep parsing pure: parsers take text or JSON and return models, with no file or process
  access, so they're unit-tested in `OpenRouter.Web.Tests` against sample input.
  File, socket and process access lives in thin services around them.
- Avoid new NuGet packages unless they clearly earn their place. The framework already
  covers JSON, cookie auth, password hashing, hosted services and options.
  `Microsoft.Data.Sqlite` (traffic history) is the one exception so far.
- Run external commands with `ProcessStartInfo.ArgumentList`, never by concatenating a
  command string, and never pass user input to a shell.

## Code layout (`router/OpenRouter.Web`)

- `Kea/`, `Radvd/`, `Network/`: one folder per data source, each with models, a pure parser
  and a thin service (`KeaService`, `RadvdService`, `IpCommand`) that does the I/O.
- `Kernel/` (`/proc`, `/sys`), `Systemd/` (`systemctl show`), `Firewall/` (nftables.conf text) and
  `Probes/` (ping, a minimal DNS query) follow the same pattern. `Processes/CommandRunner` runs
  external commands.
- `Health/`: the Health page's checks. Each area (`DhcpHealth`, `Ipv6Health`, `LinkHealth`, ...) is a
  pure function from loaded data to `HealthCheck`s with a status, summary and advice; `HealthReport`
  assembles them. New automatic checks go here, with tests in `HealthCheckTests`.
- `Traffic/`: per-client traffic history. `TrafficSampler` (a hosted service) reads `nft -j list table
  inet acct` (`NftService`, via sudo) and interface byte counters every `TrafficLiveSeconds` (5 s),
  publishes live rates and adds the increases up into whole minutes; `TrafficEvents` pushes `live`,
  `minute` and `dns` updates to open pages over Server-Sent Events (`/events`, a minimal endpoint in
  `Program.cs`), and the page script reloads charts and the devices table (a partial) on `minute`; `TrafficAttribution`
  turns counter increases into per-device bytes (by MAC, through `ClientDirectory`); `TrafficStore`
  keeps them in SQLite (one row per device per minute, monthly archive files, `VACUUM INTO` snapshots);
  `TrafficSeriesBuilder` makes chart series. In Development, `SampleTrafficSeeder` fills an empty
  database with made-up traffic instead of sampling.
- `Dns/`: bind. `BindConfigParser` (named.conf syntax), `BindStatsParser` (statistics channel JSON) and
  `DnsMetrics` (the counters kept, with restart-aware deltas) are pure; `BindService` does the I/O.
  `DnsSampler` stores bind's counters and probe lookups (through bind, and straight at each forwarder)
  every minute in `DnsStore` (tables in the traffic SQLite file) and sends a `dns` event. The DNS
  checks shared by the DNS and Health pages are in `Health/DnsHealth`.
- `Clients/`: `ClientDirectory` merges the neighbour table with Kea reservations and leases by MAC.
- `Pages/`: Razor Pages. Page models derive from `RouterPageModel` and load data through
  `TryLoadAsync`, so an unreadable source shows as a message on the page instead of an error page.
- `Configuration/`: `RouterOptions`. In Development, `SampleDir` makes services read command output
  from `samples/` (`ip-*.json`, `kea-statistics.json`, `systemctl-show.txt`) and `/proc`, `/sys` from
  `samples/proc`, `samples/sys`, and skips live ping/DNS tests; `SampleNow` stops the clock (`TimeProvider`)
  at the time the sample leases were made for. Always get the time from the injected `TimeProvider`.

## Security model (applies once editing exists)

- The app runs as a dedicated unprivileged user (planned: `openrouter`), never root.
  It reaches Kea through its control socket (group `_kea`). The Health page needs nothing more:
  `/proc`, `/sys`, `ip` and `systemctl show` are unprivileged, and ping uses unprivileged ICMP
  sockets (`net.ipv4.ping_group_range`, open to all users on Debian). It gets other privileged
  actions only through narrow, explicit grants: sudoers for one exact command each, run with
  `sudo -n` (so far `/usr/sbin/nft -j list table inet acct`; planned `systemctl reload radvd`).
  The README lists every grant the router needs; keep it up to date.
- Every config change is: back up the current file → write the new one → validate with the
  service's own checker → apply → on failure restore the backup. Never leave a service
  running on an unvalidated config.
- Authentication is planned as cookie auth for a single admin, with the password hash
  (ASP.NET Core `PasswordHasher`) stored outside the repo. All pages require login.
  Anti-forgery tokens stay on (Razor Pages default).
- LAN only: listen on the LAN interface, not the WAN or guest network.

## Commands

Run from `router/`:

```sh
dotnet build
dotnet test
dotnet run --project OpenRouter.Web      # http://localhost:4000, Development, uses samples/
cd client && npm run dev                 # rebuild the TypeScript client on change
```

`dotnet build` builds the client when `wwwroot/dist` is missing; Release builds always rebuild it.

Deployment: `router/publish.sh` builds the client and a self-contained Release build, installs it to
`/opt/openrouter-web` and restarts the `openrouter-web` systemd service
(`router/openrouter-web.service`, runs as the `openrouter` user). Keep that unit free of options that imply
`NoNewPrivileges`, or sudo stops working.
