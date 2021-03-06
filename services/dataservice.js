import fetch from "isomorphic-unfetch";
const baseUrl = "http://192.168.1.1:4000";
class DataService {
    async getAllClients() {
        const data = await fetch(`${baseUrl}/api/clients`)
            .then(res => res.json());
        return data;
    }
    async getAllLeases() {
        const data = await fetch(`${baseUrl}/api/leases`)
            .then(res => res.json());
        return data;
    }
    async getConfig() {
        const data = await fetch(`${baseUrl}/api/config`)
            .then(res => res.json());
        return data;
    }
    async getLeasesByMac(id) {
        const data = await fetch(`${baseUrl}/api/leases/${id}`)
            .then(res => res.json());
        return data;
    }
    async getActiveLeases() {
        const all = await this.getAllLeases();
        return all;
    }
}

export default new DataService();
