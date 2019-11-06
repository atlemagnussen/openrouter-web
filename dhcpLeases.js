const lib = require("./dhcpLib");
const fileLib = require("./fileLib");
const dev = process.env.NODE_ENV !== "production"; //true false
const FILEPATH = dev ? "dhcpd.leases.example" : "/var/db/dhcpd.leases";
console.log(`FILEPATH=${FILEPATH}`);
class DhcpdLeases {
    async getAll(date) {
        if (!date) {
            date = new Date();
        }
        const allLeases = await this.readAllLeases();
        const activeLeasesDistinct = await this.getActive(allLeases, date);

        const inactiveLeases = allLeases.filter(f => {
            return !activeLeasesDistinct.find(x => x.mac === f.mac);
        });
        const inactiveLeasesDistinct = this.getDistinct(inactiveLeases);
        return {
            active: activeLeasesDistinct,
            inactive: inactiveLeasesDistinct,
        };
    }
    async getLeasesByMac(mac) {
        const arr = await this.readAllLeases();
        const filter = arr.filter(f => {
            return f.mac === mac;
        });
        return filter;
    }
    async getLeasesByHost(host) {
        const arr = await this.readAllLeases();
        const filter = arr.filter(f => {
            return f.host === host;
        });
        return filter;
    }
    async readAllLeases() {
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
    getActiveLeases(allLeases, date) {
        console.log(`Fetch newer leases than ${date.toISOString()}`);
        const active = allLeases.filter(f => {
            return f.end > date;
        });
        const sorted = active.sort(function(a, b) {
            return new Date(b.end) - new Date(a.end);
        });
        return sorted;
    }
    async getActive(allLeases, date) {
        if (!date) {
            date = new Date();
        }
        const activeLeases = await this.getActiveLeases(allLeases, date);
        return this.getDistinct(activeLeases);
    }
    getDistinct(leases) {
        const macsUnique = [...new Set(leases.map(x => x.mac))];
        return leases.filter(f => {
            if (macsUnique.includes(f.mac)) {
                const index = macsUnique.indexOf(f.mac);
                if (index !== -1) macsUnique.splice(index, 1);
                return true;
            }
        });
    }

    parseJson(lines) {
        const array = [];
        let current;
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (!current && line.includes("lease") && line.includes("{")) {
                const ip = lib.parseLeaseIp(line);
                current = {
                    ip,
                };
            }
            if (current) {
                if (line.includes("starts")) {
                    const start = lib.parseDateTime(line);
                    current.start = start;
                }
                if (line.includes("ends")) {
                    const end = lib.parseDateTime(line);
                    current.end = end;
                }
                if (line.includes("hardware ethernet")) {
                    const mac = lib.parseMac(line);
                    current.mac = mac;
                }
                if (line.includes("uid")) {
                    const uid = lib.parseUid(line);
                    current.uid = uid;
                }
                if (line.includes("client-hostname")) {
                    const host = lib.parseHostName(line);
                    current.host = host;
                }
                if (line.includes("}")) {
                    const add = {
                        id: lib.getUuidDet(current),
                    };
                    array.push(Object.assign(add, current));
                    current = null;
                }
            }
        }
        return array;
    }
}

module.exports = new DhcpdLeases();
