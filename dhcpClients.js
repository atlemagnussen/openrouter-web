const lib = require("./dhcpLib");
const fileLib = require("./fileLib");
const fping = require("./fping");
const dev = process.env.NODE_ENV !== "production"; //true false
const FILEPATH = dev ? "dhcpd.conf.example" : "/etc/dhcpd.conf";
console.log(`FILEPATH=${FILEPATH}`);

class DhcpClients {
    async getAll() {
        const config = await this.readConfig();
        const activeIps = await this.getActive(config);
        let id = 0;
        const active = activeIps.map(ip => {
            id += 1;
            return {
                id,
                ip,
            };
        });
        const inactiveHosts = config.hosts.filter(f => {
            return !active.find(x => x === f.ip);
        });
        const inactive = inactiveHosts.map(i => {
            id += 1;
            i.id = id;
            return i;
        });
        return {
            active,
            inactive,
        };
    }
    async getActive(conf) {
        const net = conf.subnets[0];
        const active = await fping.pingRange(net.subnet, net.netmask);
        return active;
    }
    async readConfig() {
        const raw = await fileLib.readFile(FILEPATH);
        if (!raw) {
            throw new Error(`No content in filepath "${FILEPATH}"`);
        }
        const lines = raw.split("\n");
        if (lines.length < 3) {
            throw new Error(`Too small content in lease file, lines.length = ${lines.length}`);
        }
        const json = this.parseJson(lines);
        return json;
    }

    parseJson(lines) {
        let retval = {
            subnets: [],
            hosts: [],
        };
        let currentSubnet, currentHost;
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (line.startsWith("#")) continue;
            if (!currentSubnet && line.includes("subnet") && line.includes("{")) {
                currentSubnet = {
                    subnet: lib.parseSubnet(line),
                    netmask: lib.parseNetmask(line),
                    routers: [],
                    dns: [],
                };
            }
            if (!currentHost && line.includes("host") && line.includes("{")) {
                currentHost = {
                    name: lib.parseHost(line),
                };
            }
            if (currentHost) {
                if (line.includes("hardware")) {
                    currentHost.mac = lib.parseMac(line);
                }
                if (line.includes("fixed-address")) {
                    currentHost.ip = lib.parseFixedIp(line);
                }
            }
            if (currentSubnet) {
                if (line.includes("range")) {
                    currentSubnet.range = lib.parseRange(line);
                }
                if (line.includes("option routers")) {
                    currentSubnet.routers.push(...lib.parseRouters(line));
                }
                if (line.includes("option domain-name-servers")) {
                    currentSubnet.dns.push(...lib.parseDns(line));
                }
                if (line.includes("default-lease-time")) {
                    currentSubnet.defaultLeaseTime = lib.parseLeaseTime(line);
                }
                if (line.includes("max-lease-time")) {
                    currentSubnet.maxLeaseTime = lib.parseLeaseTime(line);
                }
            }
            if (line.includes("}")) {
                if (currentSubnet) {
                    const clone = Object.assign({}, currentSubnet);
                    retval.subnets.push(clone);
                    currentSubnet = null;
                }
                if (currentHost) {
                    const clone = Object.assign({}, currentHost);
                    retval.hosts.push(clone);
                    currentHost = null;
                }
            }
        }
        return retval;
    }
}

module.exports = new DhcpClients();
