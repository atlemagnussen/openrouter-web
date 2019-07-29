const fs = require("fs");
const FILEPATH = "dhcpd.leases";

class DhcpdLeases {
    async getLeasesByMac(mac) {
        const arr = await this.ReadAllLeases();
        const filter = arr.filter((f) => {
            return f.mac == mac;
        });
        return filter;
    }
    async getLeasesByHost(host) {
        const arr = await this.ReadAllLeases();
        const filter = arr.filter((f) => {
            return f.host == host;
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
                    array.push(Object.assign({}, current));
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
        const date = trim.substring(10,12);
        const hour = trim.substring(13,15);
        const min = trim.substring(16,18);
        const sec = trim.substring(19,21);
        const d = new Date(Date.UTC(year, month, date, hour, min, sec));
        return d;
    }
}

const test = async () => {
    const parse = new DhcpdLeases();
    // const res = await parse.ReadAsJson(FILEPATH);
    const res = await parse.getLeasesByHost("Chromecast");
    console.log(res);
    return "done";
};
  
// test()
//     .then(console.log)
//     .catch(console.error);

module.exports = new DhcpdLeases();