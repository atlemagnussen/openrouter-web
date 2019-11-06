const getUuid = require("uuid-by-string");

class DhcpLib {
    parseSubnet(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length > 1) {
            for (let i = 0; i < words.length; i++) {
                if (words[i] == "subnet" && words[i + 1]) {
                    return words[i + 1];
                }
            }
        }
        throw new Error("no subnet to be found");
    }
    parseNetmask(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length > 1) {
            for (let i = 0; i < words.length; i++) {
                if (words[i] == "netmask" && words[i + 1]) {
                    return words[i + 1];
                }
            }
        }
        throw new Error("no netmask to be found");
    }
    parseRange(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length === 3 && words[0] === "range") {
            return {
                from: words[1],
                to: words[2],
            };
        }
        throw new Error("no range to be found");
    }
    parseRouters(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length > 2 && words[0] === "option" && words[1] === "routers") {
            const arr = [];
            for (let i = 2; i < words.length; i++) {
                arr.push(words[i]);
            }
            return arr;
        }
        throw new Error("no routers to be found");
    }
    parseDns(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length > 2 && words[0] === "option" && words[1] === "domain-name-servers") {
            const arr = [];
            for (let i = 2; i < words.length; i++) {
                arr.push(words[i]);
            }
            return arr;
        }
        throw new Error("no dns server to be found");
    }
    parseLeaseTime(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length === 2 && (words[0] === "default-lease-time" || words[0] === "max-lease-time")) {
            return words[1];
        }
        throw new Error("no lease time to be found");
    }
    parseHost(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length > 1 && words[0] === "host") {
            return words[1];
        }
        throw new Error("no host to be found");
    }
    parseMac(line) {
        return line
            .replace("hardware", "")
            .replace("ethernet", "")
            .replace(";", "")
            .trim();
    }
    parseFixedIp(line) {
        const words = this.trimAndSplitToWords(line);
        if (words.length > 1 && words[0] === "fixed-address") {
            return words[1];
        }
        throw new Error("no fixed address to be found");
    }
    parseDateTime(line) {
        const trim = line
            .replace("starts", "")
            .replace("ends", "")
            .trim();
        const year = trim.substring(2, 6);
        const month = trim.substring(7, 9);
        const monthInt = parseInt(month, 10);
        const date = trim.substring(10, 12);
        const hour = trim.substring(13, 15);
        const min = trim.substring(16, 18);
        const sec = trim.substring(19, 21);
        const d = new Date(Date.UTC(year, monthInt - 1, date, hour, min, sec));
        return d;
    }
    parseLeaseIp(line) {
        return line
            .replace("lease", "")
            .replace("{", "")
            .trim();
    }
    parseHostName(line) {
        return line
            .replace("client-hostname", "")
            .replace(";", "")
            .replace(/"/gi, "")
            .trim();
    }

    parseUid(line) {
        return line
            .replace("uid", "")
            .replace(";", "")
            .trim();
    }

    getUuidDet(lease) {
        const leaseString = `${lease.ip}${lease.mac}${lease.host}${lease.start.toISOString()}${lease.end.toISOString()}`;
        return getUuid(leaseString);
    }

    trimAndSplitToWords(line) {
        if (!line) {
            throw new Error("nothing in line, critical!");
        }
        const trim = line.replace(";", "").trim();
        const words = trim.split(" ");
        return words;
    }
}
module.exports = new DhcpLib();
