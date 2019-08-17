const fs = require("fs");
const getUuid = require("uuid-by-string");
const dev = process.env.NODE_ENV !== "production"; //true false
const FILEPATH = dev ? "dhcpd.leases" : "/var/db/dhcpd.leases";
console.log(`FILEPATH=${FILEPATH}`);
class DhcpdLeases {
    async getLeasesByMac(mac) {
        const arr = await this.ReadAllLeases();
        const filter = arr.filter((f) => {
            return f.mac === mac;
        });
        return filter;
    }
    async getLeasesByHost(host) {
        const arr = await this.ReadAllLeases();
        const filter = arr.filter((f) => {
            return f.host === host;
        });
        return filter;
    }
    async ReadAllLeases() {
        const raw = await this.readFile(FILEPATH);
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
    async getActiveLeases(date) {
        const all = await this.ReadAllLeases();
        console.log(`Fetch newer leases than ${date.toISOString()}`);
        const active = all.filter((f) => {
            return f.end > date;
        });
        const sorted = active.sort(function(a,b){
            return new Date(b.end) - new Date(a.end);
        });
        return sorted;
    }
    async getActiveClients(date) {
        if (!date) {
            date = new Date();
        }
        const activeLeases = await this.getActiveLeases(date);
        const macsUnique = [...new Set(activeLeases.map(x => x.mac))];
        return activeLeases.filter((f) => {
            if (macsUnique.includes(f.mac)) {
                const index = macsUnique.indexOf(f.mac);
                if (index !== -1) macsUnique.splice(index, 1);
                return true;
            }
        });
    }
    readFile(filePath) {
        return new Promise(resolve => {
            fs.readFile(filePath, "utf8", (err, data) => {
                if (err) throw err;
                resolve(data);
            });
        });
    }
    parseJson(lines) {
        const array = [];
        let current;
        for(let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (!current && line.includes("lease") && line.includes("{")) {
                const ip = this.parseIp(line);
                current = {
                    ip
                };
            }
            if (current) {
                if (line.includes("starts")) {
                    const start = this.parseDateTime(line);
                    current.start = start;
                }
                if (line.includes("ends")) {
                    const end = this.parseDateTime(line);
                    current.end = end;
                }
                if (line.includes("hardware ethernet")) {
                    const mac = this.parseMac(line);
                    current.mac = mac;
                }
                if (line.includes("uid")) {
                    const uid = this.parseUid(line);
                    current.uid = uid;
                }
                if(line.includes("client-hostname")) {
                    const host = this.parseHostName(line);
                    current.host = host;
                }
                if (line.includes("}")) {
                    const add = {
                        id: this.getUuidDet(current)
                    };
                    array.push(Object.assign(add, current));
                    current = null;
                }
            }
        }
        return array;
    }
    parseHostName(line) {
        return line.replace("client-hostname", "")
            .replace(";", "")
            .replace(/"/gi, "")
            .trim();
    }
    parseUid(line) {
        return line.replace("uid", "")
            .replace(";", "")
            .trim();
    }
    parseMac(line) {
        return line.replace("hardware", "")
            .replace("ethernet", "")
            .replace(";", "")
            .trim();
    }
    parseIp(line) {
        return line.replace("lease", "").replace("{", "").trim();
    }
    parseDateTime(line) {
        const trim = line.replace("starts", "").replace("ends", "").trim();
        const year = trim.substring(2,6);
        const month = trim.substring(7,9);
        const monthInt = parseInt(month, 10);
        const date = trim.substring(10,12);
        const hour = trim.substring(13,15);
        const min = trim.substring(16,18);
        const sec = trim.substring(19,21);
        const d = new Date(Date.UTC(year, monthInt - 1, date, hour, min, sec));
        return d;
    }
    getUuidDet(lease) {
        const leaseString = `${lease.ip}${lease.mac}${lease.host}${lease.start.toISOString()}${lease.end.toISOString()}`;
        return getUuid(leaseString);
    }
}

module.exports = new DhcpdLeases();
