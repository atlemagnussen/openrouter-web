# Sample data

Made-up router files used when running in Development (see `appsettings.Development.json`).
They mirror the real formats on the router but contain no real addresses:
MACs are locally administered (`02:…`) and IPv6 uses the documentation prefix `2001:db8::/32` and a made-up ULA.

This repository is public — never copy real config, leases, MACs or prefixes from the router into it.

Lease expiry times in `kea-leases4.csv` are Unix timestamps around 2026-10-18 12:00 UTC.
Development stops the app's clock at that moment (`Router:SampleNow`), so the same leases always show as active.

`proc/`, `sys/`, `ip-*.json`, `kea-statistics.json`, `systemctl-show.txt` and `nftables.conf` feed the Health page
(`Router:SampleDir`). They include a few deliberate faults (a 100 Mbit guest port that flaps, a named that restarted,
radvd's short intervals) so every kind of check shows up.
