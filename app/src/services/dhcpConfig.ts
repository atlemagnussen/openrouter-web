
import * as lib from "./dhcpLib"
import { readFile }from "./fileLib"
import type { DhcpConfig, Host, Network } from "src/types/interfaces"
const dev = process.env.NODE_ENV !== "production"
const os = process.platform
const filePathOs = os == "linux" ? "/etc/dhcp/dhcpd.conf" : "/etc/dhcpd.conf"

const FILEPATH = dev ? "./examples/dhcpd.conf.example" : filePathOs
console.log(`FILEPATH=${FILEPATH}`)


export const readDhcpConfig = async (): Promise<DhcpConfig> => {
    const raw = await readFile(FILEPATH)
    if (!raw) {
        throw new Error(`No content in filepath "${FILEPATH}"`)
    }
    const lines = raw.split("\n")
    if (lines.length < 3) {
        throw new Error(`Too small content in lease file, lines.length = ${lines.length}`)
    }
    const config = parseConfigJson(lines)
    config.configFilePath = FILEPATH
    return config
}

const parseConfigJson = (lines: string[]) => {
    const retval: DhcpConfig = {
        subnets: [],
        hosts: [],
    }
    let currentSubnet: Network
    let currentHost: Host
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
                name: lib.parseHost(line),
                ip: "",
                mac: ""
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

