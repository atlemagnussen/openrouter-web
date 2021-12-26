import fping from "./fping"
import * as lib from "./dhcpLib"
import { readFile }from "./fileLib"
import type { DhcpConfig, IpAddress, LeasesOverView, Network } from "src/types/interfaces"
const dev = process.env.NODE_ENV !== "production"
const os = process.platform
const filePathOs = os == "linux" ? "/etc/dhcp/dhcpd.conf" : "/etc/dhcpd.conf"

const FILEPATH = dev ? "dhcpd.conf.example" : filePathOs
console.log(`FILEPATH=${FILEPATH}`)


export const getAllDhcp = async (): Promise<LeasesOverView<IpAddress>> => {
    const config = await readConfig()
    const active = await getActive(config)
    let id = 0
    const activeIps: IpAddress[] = active.map(ip => {
        id += 1
        return {
            id,
            ip,
        }
    })
    const inactiveHosts = config.hosts.filter(f => {
        return !activeIps.find(x => x.ip === f.ip)
    })
    const inactive: IpAddress[] = inactiveHosts.map(i => {
        id += 1
        i.id = id
        return i
    })
    return {
        activeIps,
        inactive,
    }
}
const getActive = async (conf: DhcpConfig) => {
    const net = conf.subnets[0]
    const active = await fping.pingRange(net.subnet, net.netmask)
    return active
}
const readConfig = async () => {
    const raw = await readFile(FILEPATH)
    if (!raw) {
        throw new Error(`No content in filepath "${FILEPATH}"`)
    }
    const lines = raw.split("\n")
    if (lines.length < 3) {
        throw new Error(`Too small content in lease file, lines.length = ${lines.length}`)
    }
    const json = parseConfigJson(lines)
    return json
}

const parseConfigJson = (lines: string[]) => {
    const retval: DhcpConfig = {
        subnets: [],
        hosts: [],
    }
    let currentSubnet: Network
    let currentHost
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i]
        if (line.startsWith("#")) continue
        if (!currentSubnet && line.includes("subnet") && line.includes("{")) {
            currentSubnet = {
                subnet: lib.parseSubnet(line),
                netmask: lib.parseNetmask(line),
                routers: [],
                dns: [],
                range: {
                    from: "",
                    to: ""
                },
                defaultLeaseTime: "",
                maxLeaseTime: ""
            }
        }
        if (!currentHost && line.includes("host") && line.includes("{")) {
            currentHost = {
                host: lib.parseHost(line),
            }
        }
        if (currentHost) {
            if (line.includes("hardware")) {
                currentHost.mac = lib.parseMac(line)
            }
            if (line.includes("fixed-address")) {
                currentHost.ip = lib.parseFixedIp(line)
            }
        }
        if (currentSubnet) {
            if (line.includes("range")) {
                currentSubnet.range = lib.parseRange(line)
            }
            if (line.includes("option routers")) {
                currentSubnet.routers.push(...lib.parseRouters(line))
            }
            if (line.includes("option domain-name-servers")) {
                currentSubnet.dns.push(...lib.parseDns(line))
            }
            if (line.includes("default-lease-time")) {
                currentSubnet.defaultLeaseTime = lib.parseLeaseTime(line)
            }
            if (line.includes("max-lease-time")) {
                currentSubnet.maxLeaseTime = lib.parseLeaseTime(line)
            }
        }
        if (line.includes("}")) {
            if (currentSubnet) {
                const clone = Object.assign({}, currentSubnet)
                retval.subnets.push(clone)
                currentSubnet = null
            }
            if (currentHost) {
                const clone = Object.assign({}, currentHost)
                retval.hosts.push(clone)
                currentHost = null
            }
        }
    }
    return retval
}

