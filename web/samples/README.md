# Sample data

Made-up router files used when running in Development (see `appsettings.Development.json`).
They mirror the real formats on the router but contain no real addresses:
MACs are locally administered (`02:…`) and IPv6 uses the documentation prefix `2001:db8::/32` and a made-up ULA.

This repository is public — never copy real config, leases, MACs or prefixes from the router into it.

The lease expiry times in `kea-leases4.csv` are Unix timestamps; regenerate them if you need "active" leases.
