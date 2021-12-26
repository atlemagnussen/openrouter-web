import { exec } from "child_process"

class Fping {
    pingRange(subnet, netmask) {
        netmask = this.translate(netmask);
        const network = `${subnet}/${netmask}`;
        const args = ["-4aqg", network];
        return new Promise(resolve => {
            exec("fping " + args.join(" "), function(err, stdout, stderr) {
                if (err.code == 1) err = null;
                if (err) {
                    throw err;
                }
                const hosts = stdout
                    .toString()
                    .trim()
                    .split("\n");
                resolve(hosts);
            });
        });
    }
    translate(netmask) {
        switch (netmask) {
            case "255.255.255.0":
                return "24";
            default:
                return "24";
        }
    }
}

export default new Fping()
