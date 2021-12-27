# open router web

Try to build a simple web interface for using on a DIY router where you have ie access to the `dhcpd.conf` and `dhcpd.leases` files  
Should work on both Linux and BSD

## Requirements

- Running [ISC DHCP server](https://www.isc.org/dhcp/)
- [Fping tool](https://fping.org/) installed

## Run in production

```sh
npm run build
```

then use systemd using `openrouter.service` file  

or use bash script:

```sh
./start-prod.sh
```
