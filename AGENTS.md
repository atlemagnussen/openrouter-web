# OpenRouter Web

A web interface for a self-built Linux router, in the spirit of a consumer router's admin pages.
It starts read-only (show clients, leases, DHCP and IPv6 config) and will grow editing,
authentication and background tasks.

- `web/` — the new app (.NET 10, ASP.NET Core Razor Pages). All new work goes here.
- `app/` — the old SvelteKit app built for ISC dhcpd. Legacy, kept for reference only;
  it will be deleted. Don't modify it or port its parsers (the data sources have changed).

**This repository is public.** Never commit real router data: config files, leases, MAC addresses,
hostnames, IPv6 prefixes or public IPs. Use the made-up data in `web/samples/`.

## The router

Debian 13 (trixie) box with three NICs: WAN, LAN and a guest LAN. Interface names are pinned
with systemd `.link` files and configured with ifupdown (`/etc/network/interfaces`).

| Concern | Software | Where the app reads it |
|---|---|---|
| IPv4 DHCP | Kea 2.6 (`kea-dhcp4-server`, runs as `_kea`) | Config `/etc/kea/kea-dhcp4.conf` (JSON); control socket `/run/kea/kea4-ctrl-socket`; leases via the `lease_cmds` hook, file `/var/lib/kea/kea-leases4.csv` as fallback |
| IPv6 addressing | radvd (SLAAC + RDNSS, no DHCPv6 server) | `/etc/radvd.conf` |
| WAN DHCP + IPv6 prefix delegation | dhcpcd (assigns a /64 from the ISP's delegated prefix to the LAN) | `ip -j addr`, dhcpcd state |
| Who is online | Kernel neighbour table | `ip -j neigh` (IPv4 + IPv6, no root needed) |
| DNS | bind9, local zones + forwarders | not managed yet |
| Firewall / NAT | nftables, `/etc/nftables.conf` | not managed yet |

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
- Plain CSS in `wwwroot/css/site.css` with light/dark colour tokens on `:root`. No Bootstrap,
  no jQuery, no CDN assets (the router may be offline).
- Nullable enabled, file-scoped namespaces, records for data models, `sealed` by default.
- Paths and sockets come from `RouterOptions` (`Router` section in appsettings). Development
  points at `web/samples/`; never hard-code system paths elsewhere.
- Keep parsing pure: parsers take text or JSON and return models, with no file or process
  access, so they're unit-tested in `OpenRouter.Web.Tests` against sample input.
  File, socket and process access lives in thin services around them.
- Avoid new NuGet packages unless they clearly earn their place. The framework already
  covers JSON, cookie auth, password hashing, hosted services and options.
- Run external commands with `ProcessStartInfo.ArgumentList`, never by concatenating a
  command string, and never pass user input to a shell.

## Security model (applies once editing exists)

- The app runs as a dedicated unprivileged user (planned: `openrouter`), never root.
  It reaches Kea through its control socket (group `_kea`), and gets other privileged
  actions only through narrow, explicit grants (e.g. sudoers for one exact
  `systemctl reload radvd`).
- Every config change is: back up the current file → write the new one → validate with the
  service's own checker → apply → on failure restore the backup. Never leave a service
  running on an unvalidated config.
- Authentication is planned as cookie auth for a single admin, with the password hash
  (ASP.NET Core `PasswordHasher`) stored outside the repo. All pages require login.
  Anti-forgery tokens stay on (Razor Pages default).
- LAN only: listen on the LAN interface, not the WAN or guest network.

## Commands

Run from `web/`:

```sh
dotnet build
dotnet test
dotnet run --project OpenRouter.Web      # http://localhost:4000, Development, uses samples/
```

Deployment (planned): `dotnet publish -c Release -r linux-x64 --self-contained` to
`/opt/openrouter-web`, run by a systemd unit as the `openrouter` user. The root
`openrouter.service` and `start-prod.sh` belong to the old app.
