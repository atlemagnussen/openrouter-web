// combine all
import type { DhcpConfig, Host, Lease, OverView } from "src/types/interfaces"
import {readDhcpConfig} from "./dhcpConfig"
import {getLeasesOverview} from "./dhcpLeases"
import {getPingableIps} from "./pingNetwork"

export const getActiveOverview = async (): Promise<OverView<Host>> => {
    const config = await readDhcpConfig()
    const leases = await getLeasesOverview(new Date())
    const pingedIps = await getPingableIps(config)
    
    const ret: OverView<Host> = {
        active: [],
        inactive: []
    }

    for(let i = 0; i < pingedIps.length; i++) {
        const ip = pingedIps[i]
        let ipIsRouter = false
        for (let i = 0; i < config.subnets.length; i++) {
            const subnet = config.subnets[i]
            for (let y = 0; y < subnet.routers.length; y++) {
                const router = subnet.routers[y]
                if (router == ip)
                    ipIsRouter = true
            }
        }
        if (ipIsRouter)
            continue
        
        let h = findHost(config, leases, ip)
        if (!h) {
            h = {
                ip,
                name: "unknown",
                mac: "unknown"
            }
        }
        const host: Host = {
            ip,
            name: h.name,
            mac: h.mac
        }
        ret.active.push(host)
    }

    return ret
}

const findHost = (config: DhcpConfig, leases: OverView<Lease>, ip: string) => {
    let host = config.hosts.find(h => h.ip == ip)
    if (!host)
            host = leases.active.find(a => a.ip == ip)
    if (!host)
        host = leases.inactive.find(i => i.ip == ip)
    return host    
}