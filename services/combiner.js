import dataService from "./dataservice.js";

class Combiner {
    async getAll() {
        const clients = await dataService.getAllClients();
        const leases = await dataService.getAllLeases();
        const config = await dataService.getConfig();
        
        const ca = [];
        for (let i=0; i<clients.active.length; i++) {
            const a = clients.active[i];
            if (a.ip === "192.168.1.1") continue;
            const find1 = config.hosts.find(h => {
                return h.ip === a.ip;
            });
            if (find1) {
                ca.push(find1);
                continue;
            }
            const find2 = leases.active.find(l => {
                return l.ip === a.ip; 
            });
            if (find2) {
                ca.push(find2);
                continue;
            }
            ca.push({
                mac: "unknown mac",
                name: "unknown name",
                ip: a.ip
            });
        }
        clients.active = ca;

        return {
            clients,
            leases
        };
    }
}

export default new Combiner();
