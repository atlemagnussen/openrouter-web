# open router web

Try to build a simple web interface for using on a DIY router where you have ie access to the `dhcpd.conf` and `dhcpd.leases` files  
Should work on both Linux and BSD

![example1](https://storage.googleapis.com/atle-static-north/pictures/openrouterweb-example1.jpg "openweb example")

## Requirements

-   Running [ISC DHCP server](https://www.isc.org/dhcp/)
-   [Fping tool](https://fping.org/) installed

## Run in production

```sh
npm run build
./start-prod.sh
```
