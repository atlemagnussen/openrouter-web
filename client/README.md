# netcheck

Measures a laptop's network connection and tells you where problems come from: the Wi-Fi, the router, DNS
or the internet connection. It records every measurement in SQLite, so the results can be compared with the
router's own history from OpenRouter Web.

Two ways to use it:
- **A run of a few hours** from the terminal (`netcheck run`), ending with a report.
- **Continuously:** a collector runs in the background as a systemd user service (`netcheck daemon`), and a
  desktop app shows it as charts, with the Wi-Fi networks around, and a tray icon that turns amber or red
  when the connection gets bad.

It's .NET 10: a command line, and a desktop app made with Avalonia. Wi-Fi readings and scans use Linux's `iw`
and NetworkManager; on other systems the rest still works.

## What it measures

| What | How often | Why |
|---|---|---|
| **Ping the router** (IPv4, and IPv6 link-local) | every second | Loss or slow replies here mean the problem is the local link: Wi-Fi, access point, power saving. |
| **Ping the internet** (1.1.1.1, 8.8.8.8, and 2606:4700:4700::1111 if you have IPv6) | every second | Loss only here, with the router fine, points at the WAN or the ISP. |
| **DNS lookups** straight to the router's DNS and to 1.1.1.1 | every 10 s | Slow or failing lookups feel like "every new page hangs". Comparing the two separates the router's DNS from the internet. The names rotate, so some answers come from cache and some don't. |
| **HTTPS requests** on a fresh connection | every 30 s | What opening a site costs: name lookup, TCP connect, TLS handshake and first byte, each timed separately. |
| **Wi-Fi link** (`iw`) | every 2 s | Signal (dBm), which access point (BSSID), band and channel, link rate, and failed transmissions. |
| **Network state** | every 5 s | IP address, gateway and IPv6 address changes. |
| **Networks around** (scan) | every 2 min | Every network the laptop hears: name (or hidden), BSSID, band, channel, signal, and who made it. Shows what shares your channel, your mesh's own hidden networks, and devices that put up their own Wi-Fi (Chromecast setup networks, printers). |

It also records **events**:
- **roam:** switched to another access point or mesh node;
- **band:** moved between 2.4 and 5 GHz;
- **wifi-disconnected** / **wifi-connected**;
- **outage-start** / **outage-end:** three or more pings in a row lost;
- **address** / **gateway** changes, and **network-down** / **network-up**;
- **sleep:** the laptop was suspended. That time is a gap, not an outage, and measuring waits 30 s for Wi-Fi to
  reconnect afterwards.

## Continuously: the collector and the app

```sh
cd client
./install.sh
```

It builds both programs and installs them for your user:
- **the programs:** in `~/.local/lib/netcheck`, with `netcheck` and `netcheck-app` linked into `~/.local/bin`;
- **the collector:** started as the systemd user service `netcheck`, now and at every login;
- **the app:** added to your launcher as "netcheck".

Run `./install.sh` again after pulling changes.

The **collector** (`netcheck daemon`) measures all the time while you're logged in:
- **One run per network:** it starts a new run when the laptop joins another network, or has had none for a minute.
- **Retention:** it deletes measurements older than 30 days (`--keep`). The database stays around a gigabyte.

```sh
systemctl --user status netcheck
journalctl --user -u netcheck -f          # a status line every 5 minutes, and every event
systemctl --user stop netcheck            # pause it
```

The **app** (`netcheck-app`) only reads the database. Closing its window leaves it in the tray; quit from the
tray menu or with Ctrl+Q. It shows:
- **Status bar:** the Wi-Fi signal, access point and channel right now, and the round trip and loss to the router
  and the internet over the last 2 minutes.
- **Connection:** charts for signal (with roams marked), link rate, round trip, and lost pings, over 15 minutes up
  to 7 days. Point at a chart for that moment's numbers. Gaps are times nothing was measured (asleep, collector off).
- **Networks:** every network heard in the range's scans, as dots by channel and signal, and as a table with the
  maker and when it was first heard. Three colours: the network you're on, your other networks, everyone else's.
  Tick **Mine** on a row to mark a network (all access points with that name) as yours, for example a guest
  network from another box. A hidden network on the same device as one of yours counts as yours too. The
  table also notes which networks share your channel, and setup networks like a Chromecast's.
- **Events:** roams, outages, disconnects and sleep, newest first. Click one to see the 15 minutes around it.
- **Tray icon:** green when fine; amber when 1% or more of the pings to the router are lost (or 3% to the
  internet); red at 10% or when Wi-Fi is down; grey when nothing is being recorded.
- **Collector off:** if the collector isn't recording, the app says so and offers to start it.

To have the app in the tray from login, start it hidden. In niri's config:

```kdl
spawn-at-startup "netcheck-app" "--hidden"
```

It runs under XWayland (Avalonia draws through X11; niri provides it via xwayland-satellite).

## A run from the terminal

Needs the .NET 10 SDK. On Linux, Wi-Fi readings need the `iw` package. It's usually installed; if not, use
`sudo dnf install iw` or `sudo apt install iw`. No root is needed: ping uses the unprivileged ICMP sockets
that Fedora and Debian allow by default.

```sh
cd client
dotnet run --project OpenRouter.Client -- run --duration 4h --note "living room, evening"
```

Or build once and use the binary:

```sh
dotnet publish OpenRouter.Client -c Release -o ~/.local/share/netcheck-bin
~/.local/share/netcheck-bin/netcheck run -d 4h
```

While it runs it prints a status line every 15 seconds, and a line for each event as it happens:

```
20:18:49 | gateway 5.8 ms | gateway-v6 6.1 ms | 1.1.1.1 13 ms | 8.8.8.8 19 ms LOST 1/15 | dns 10 ms | https 108 ms | wifi -50 dBm 2.4 GHz ch4 02:94:…
20:31:07 ** roam: 02:94:…:45 (2.4 GHz, -71 dBm) → 02:94:…:47 (5 GHz channel 36, -58 dBm)
```

When the time is up, or when you press Ctrl+C, it writes what's left to the database and prints a report.

**Keep the laptop awake.** A sleeping laptop measures nothing, and the time it slept shows up as a gap.
For a long run, prevent sleep while it runs:

```sh
systemd-inhibit --what=sleep:idle --why="netcheck" dotnet run --project OpenRouter.Client -- run -d 8h
```

### Commands and options

```
netcheck run [options]       measure, then print a report (Ctrl+C stops early)
netcheck report [N|last]     report on run N (default: the last one)
netcheck runs                list recorded runs
netcheck scan                list the Wi-Fi networks around now: band, channel, signal, maker
netcheck mine [add|remove N] networks that are yours besides the one you're on (by name, or BSSID if hidden)
netcheck daemon [options]    measure continuously (what the systemd service runs)

-d, --duration 4h            how long (s, m, h, d; 0 = until Ctrl+C). Default 2h
--ping 1.1.1.1,8.8.8.8       ping targets besides the gateway (replaces the defaults)
--dns 1.1.1.1                DNS servers besides the gateway (replaces the defaults)
--no-gateway-dns             don't query the gateway as DNS server
--url https://...            HTTPS URLs to time (replaces the defaults)
-i, --interface wlan0        interface to watch (default: the one with the default route)
--ping-interval 1s  --dns-interval 10s  --http-interval 30s
--scan-interval 2m           scan for the networks around (0 = never)
--keep 30d                   daemon: delete measurements older than this
--note "living room"         stored with the run
--db path.db                 database (default ~/.local/share/openrouter-client/netcheck.db)
```

Run it in different places (next to an access point, in the far room) with a `--note`, then compare
the reports.

## Reading the report

```
Ping                                      sent    lost    loss     avg     p50     p95     max  jitter  outages
  gateway 192.168.1.1                      14400      31    0.2%  5.3 ms  4.0 ms   12 ms  480 ms  2.6 ms  2, longest 6 s at 21:03:12
  internet 1.1.1.1                         14400      40    0.3%   12 ms   11 ms   17 ms  510 ms  3.1 ms  2, longest 6 s at 21:03:12
…
Wi-Fi
  Signal       avg -61 dBm, weakest -78 dBm, strongest -47 dBm (good)
  Band         2.4 GHz 64%, 5 GHz 36% of the time
  Access point …:45 2.4 GHz channel 4 (HomeNet): 64% of the time, avg -63 dBm
  Roams        5, disconnects 0

What it suggests
  - 2 of 2 outages started within 15 s of switching access point or losing Wi-Fi: roaming between the mesh nodes is a likely cause.
```

- **p50 / p95:** half the replies were faster than p50, and 95% faster than p95. A good Wi-Fi link to the router
  stays under about 10 ms at p95. Spikes show in p95 and max, not in the average.
- **jitter:** the average change between consecutive round trips. A high value with a steady router means a busy line (bufferbloat).
- **outages:** three or more lost pings in a row. When the router and the internet targets have outages at the same
  times, the local link dropped. When only the internet ones do, the WAN dropped.
- **Signal:** around −50 dBm is excellent, −65 good, −72 fair, below −75 weak.
- **What it suggests:** rules of thumb that combine the numbers. For example:
  - loss to the router itself means a local problem;
  - outages right after a roam point at the mesh;
  - mostly 2.4 GHz means a slower, more crowded band;
  - the router's DNS failing while 1.1.1.1 answers points at bind;
  - the link rate falling to the lowest rates while the signal is good points at interference or a busy channel;
  - other loud networks on your channel (from the scans) take airtime from yours.

The report also lists the networks heard on the channel the laptop used most.

### Scanning without root

A full `iw scan` needs root. Instead, netcheck asks NetworkManager to scan, which the logged-in user may do, then
reads the results with `iw dev wlan0 scan dump`. A scan makes the radio leave its channel for a moment, so a ping
around it can be a little slower; every 2 minutes is a compromise.

Makers come from the IEEE list in `/usr/share/hwdata/oui.txt` (package hwdata). Mesh systems make up extra
addresses for their other networks (a hidden backhaul, the 2.4 GHz side), which aren't in the list. Those get the
maker of a network whose address differs only in the first and last byte.

Your networks are kept in `~/.config/netcheck/settings.json`, shared by the app and the reports, apart from the
measurements:

```json
{ "myNetworks": ["GuestNet"], "myBssids": [] }
```

## The database

The default is `~/.local/share/openrouter-client/netcheck.db`, one file for all runs. Times are Unix
**milliseconds** (UTC) in `time_ms`.

| Table | One row per |
|---|---|
| `runs` | run: start, end, host, interface, **MAC**, IPv4, gateway, note |
| `pings` | ping: target, label (`gateway`, `gateway-v6`, `internet`, `internet-v6`), `ms` (null = lost) |
| `dns` | lookup: server, name, `ms` (null = no answer), `rcode`, error |
| `http` | request: url, `dns_ms`, `connect_ms`, `tls_ms`, `first_byte_ms`, status, error |
| `wifi` | reading: SSID, BSSID, `freq_mhz`, `signal_dbm`, `signal_avg_dbm`, bitrates, `tx_packets`, `tx_failed`, `tx_retries`, `beacon_loss` (counters restart at each association; null when the driver doesn't report them) |
| `events` | event: kind, detail |
| `scans` | network heard in a scan: bssid, ssid (null = hidden), `freq_mhz`, `signal_dbm`, `associated`; one scan = rows with the same `time_ms` |

The database stays on the laptop. It holds your own addresses and access point names, so don't commit it
to this public repository (`.gitignore` excludes `*.db`).

### Comparing with the router

The router's history (OpenRouter Web) stores whole minutes, keyed by Unix **seconds** at the start of the
minute. Each device is identified by its MAC address. Download a snapshot from the Traffic page ("Download a snapshot"),
then attach it next to the laptop's database:

```sql
-- sqlite3 ~/.local/share/openrouter-client/netcheck.db
ATTACH 'traffic-snapshot-20261103-220000.db' AS router;

-- Per minute: this laptop's loss to the router, what the router saw it download, and the router's DNS health.
SELECT datetime(p.minute, 'unixepoch', 'localtime') AS minute,
       p.sent, p.lost, round(p.avg_ms, 1) AS avg_ms,
       c.down_bytes, c.up_bytes,
       d.servfail, d.upstream_timeouts
FROM (SELECT time_ms / 60000 * 60 AS minute, count(*) AS sent, sum(ms IS NULL) AS lost, avg(ms) AS avg_ms
      FROM pings WHERE run_id = 1 AND label = 'gateway' GROUP BY 1) p
LEFT JOIN router.clients rc ON rc.key = (SELECT mac FROM runs WHERE id = 1)
LEFT JOIN router.client_minutes c ON c.minute = p.minute AND c.client_id = rc.id
LEFT JOIN router.dns_minutes d ON d.minute = p.minute
ORDER BY p.minute;
```

`runs.mac` is the MAC address the laptop actually used on that network. With "private" or randomised Wi-Fi addresses,
that's the one the router sees too. The router's Traffic page shows it, and so does `netcheck runs`.

## Code

- `OpenRouter.Client.Core`: everything both programs use.
  - `Probes/`: one probe per measurement (`PingProbe`, `DnsProbe`, `HttpProbe`, `WifiProbe` with a pure `IwParser`,
    `WifiScanner` with a pure `ScanParser`, `NetworkState`), and `Vendors` (makers from the OUI list). The DNS wire
    format is shared with the router app (`router/.../Probes/DnsMessage.cs`).
  - `NetMonitor`: runs the probes side by side, notes events, handles sleep, prints status, and batches samples to the store.
  - `Storage/`: `ResultStore` (the schema above, writing) and `HistoryReader` (the app's queries by time, in buckets).
  - `Reporting/`: statistics (`Stats`, `Outage`), the report and its hints (`RunReport`), and `Neighbourhood` (networks heard).
- `OpenRouter.Client`: the `netcheck` command line (`Program.cs`).
- `OpenRouter.Client.Core/Settings/ClientSettings`: which networks are yours.
- `OpenRouter.Client.App`: the Avalonia app (`--screenshot file.png` renders the window to a file and quits, for checking layout). `MainWindow` (status bar, charts with ScottPlot, networks, events),
  `App` (tray icon), `ChartStyle` (the router pages' chart colours, light and dark).
- `OpenRouter.Client.Tests`: tests for the parsers, statistics, hints, store and history queries (`dotnet test` in `client/`).
- `install.sh`, `netcheck.service`: the install for one user.

Avalonia's build collects anonymous usage data unless `AVALONIA_TELEMETRY_OPTOUT=1` is set; `install.sh` sets it.
Set it in your shell too if you build by hand.
