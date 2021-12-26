import * as lib  from "./dhcpLib"
import { readFile } from "./fileLib"
import type { Lease, LeasesOverView } from "../types/interfaces"
const dev = process.env.NODE_ENV !== "production"

const os = process.platform
const osFilePath = os == "linux" ? "/var/lib/dhcp/dhcpd.leases" : "/var/db/dhcpd.leases"

const FILEPATH = dev ? "./examples/dhcpd.leases.example" : osFilePath
console.log(`FILEPATH=${FILEPATH}`)

export const getLeasesOverview = async (date: Date): Promise<LeasesOverView<Lease>> => {
    console.log(`getLeasesOverview: date=${date.toISOString()}`)
    if (!date) {
        date = new Date()
    }
    const allLeases = await readAllLeases()
    console.log(`allLeases count=${allLeases.length}`)

    const activeLeasesDistinct = await getActive(allLeases, date)

    const inactiveLeases = allLeases.filter(f => {
        return !activeLeasesDistinct.find(x => x.mac === f.mac)
    })
    const inactiveLeasesDistinct = getDistinct(inactiveLeases)
    return {
        active: activeLeasesDistinct,
        inactive: inactiveLeasesDistinct,
    }
}
export const getLeasesByMac = async (mac: string): Promise<Lease[]> => {
    const arr = await readAllLeases()
    const filter = arr.filter(f => {
        return f.mac === mac
    })
    return filter
}

export const getLeasesByHost = async (host: string): Promise<Lease[]> => {
    const arr = await readAllLeases()
    const filter = arr.filter(f => {
        return f.host === host
    })
    return filter
}

const getActiveLeases = async (allLeases: Lease[], date: Date) => {
    console.log(`Fetch newer leases than ${date.toISOString()}`)
    const active = allLeases.filter(f => {
        return f.end > date
    })
    const sorted = active.sort(function(a, b) {
        return (b.end as Date).getDate() - (a.end as Date).getDate()
    })
    return sorted
}

const getActive = async (allLeases: Lease[], date?: Date) => {
    if (!date) {
        date = new Date()
    }
    const activeLeases = await getActiveLeases(allLeases, date)
    return getDistinct(activeLeases)
}

const getDistinct = (leases: Lease[]) => {
    const macsUnique = [...new Set(leases.map(x => x.mac))]
    return leases.filter(f => {
        if (macsUnique.includes(f.mac)) {
            const index = macsUnique.indexOf(f.mac)
            if (index !== -1) macsUnique.splice(index, 1)
            return true
        }
    })
}

const parseJson = (lines: string[]): Lease[] => {
    console.log(`start parsing lines, length=${lines.length}`)
    const array = []
    let current: Lease
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i]
        if (!current && line.includes("lease") && line.includes("{")) {
            const ip = lib.parseLeaseIp(line)
            current = {
                uid: "",
                ip,
                mac: "",
                start: new Date(-8640000000000000),
                end:new Date(-8640000000000000),
                host: ""
            }
        }
        if (current) {
            if (line.includes("starts")) {
                const start = lib.parseDateTime(line)
                current.start = start
            }
            if (line.includes("ends")) {
                const end = lib.parseDateTime(line)
                current.end = end
            }
            if (line.includes("hardware ethernet")) {
                const mac = lib.parseMac(line)
                current.mac = mac
            }
            if (line.includes("uid")) {
                const uid = lib.parseUid(line)
                current.uid = uid
            }
            if (line.includes("client-hostname")) {
                const host = lib.parseHostName(line)
                current.host = host
            }
            if (line.includes("}")) {
                const add = {
                    id: lib.getUuidDet(current),
                }
                array.push(Object.assign(add, current))
                current = null
            }
        }
    }
    return array
}

const readAllLeases = async () => {
    const raw = await readFile(FILEPATH)
    if (!raw) {
        throw new Error(`No content in filepath "${FILEPATH}"`)
    }
    const lines = raw.split("\n")
    if (lines.length < 3) {
        throw new Error(`Too small content in lease file, lines.length = ${lines.length}`)
    }
    console.log(`total number of lines in leases file=${lines.length}`)
    const json = parseJson(lines)
    return json
}