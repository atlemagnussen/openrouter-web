import getUuid from "uuid-by-string"
import type { Lease, Range } from "../types/interfaces"

export const parseSubnet = (line: string): string => {
    const words = trimAndSplitToWords(line)
    if (words.length > 1) {
        for (let i = 0; i < words.length; i++) {
            if (words[i] == "subnet" && words[i + 1]) {
                return words[i + 1]
            }
        }
    }
    throw new Error("no subnet to be found")
}

export const parseNetmask = (line: string): string => {
    const words = trimAndSplitToWords(line)
    if (words.length > 1) {
        for (let i = 0; i < words.length; i++) {
            if (words[i] == "netmask" && words[i + 1]) {
                return words[i + 1]
            }
        }
    }
    throw new Error("no netmask to be found")
}

export const parseRange = (line: string): Range => {
    const words = trimAndSplitToWords(line)
    if (words.length === 3 && words[0] === "range") {
        return {
            from: words[1],
            to: words[2],
        }
    }
    throw new Error("no range to be found")
}

export const parseRouters = (line: string): Array<string> => {
    const words = trimAndSplitToWords(line)
    if (words.length > 2 && words[0] === "option" && words[1] === "routers") {
        const arr = []
        for (let i = 2; i < words.length; i++) {
            arr.push(words[i])
        }
        return arr
    }
    throw new Error("no routers to be found")
}

export const parseDns = (line: string): Array<string> => {
    const words = trimAndSplitToWords(line)
    if (words.length > 2 && words[0] === "option" && words[1] === "domain-name-servers") {
        const arr = []
        for (let i = 2; i < words.length; i++) {
            arr.push(words[i])
        }
        return arr
    }
    throw new Error("no dns server to be found")
}

export const parseLeaseTime = (line: string): string => {
    const words = trimAndSplitToWords(line)
    if (words.length === 2 && (words[0] === "default-lease-time" || words[0] === "max-lease-time")) {
        return words[1]
    }
    throw new Error("no lease time to be found")
}

export const parseHost = (line: string): string => {
    const words = trimAndSplitToWords(line)
    if (words.length > 1 && words[0] === "host") {
        return words[1]
    }
    throw new Error("no host to be found")
}

export const parseMac = (line: string): string => {
    return line
        .replace("hardware", "")
        .replace("ethernet", "")
        .replace(";", "")
        .trim()
}

export const parseFixedIp = (line: string): string => {
    const words = trimAndSplitToWords(line)
    if (words.length > 1 && words[0] === "fixed-address") {
        return words[1]
    }
    throw new Error("no fixed address to be found")
}

export const parseDateTime = (line: string): Date => {
    const trim = line
        .replace("starts", "")
        .replace("ends", "")
        .trim()
    const year = subStringParseInt(trim, 2, 6)
    const month = subStringParseInt(trim, 7, 9)
    const date = subStringParseInt(trim,10, 12)
    const hour = subStringParseInt(trim, 13, 15)
    const min = subStringParseInt(trim, 16, 18)
    const sec = subStringParseInt(trim,19, 21)
    const d = new Date(Date.UTC(year, month - 1, date, hour, min, sec))
    return d
}

const subStringParseInt = (line: string, start: number, end: number) => {
    const numStr = line.substring(start, end)
    return parseInt(numStr)
}

export const parseLeaseIp = (line: string): string => {
    return line
        .replace("lease", "")
        .replace("{", "")
        .trim()
}

export const parseHostName = (line: string): string => {
    return line
        .replace("client-hostname", "")
        .replace(";", "")
        .replace(/"/gi, "")
        .trim()
}

export const parseUid = (line: string): string => {
    return line
        .replace("uid", "")
        .replace(";", "")
        .trim()
}

export const getUuidDet = (lease: Lease): string => {
    const leaseString = `${lease.ip}${lease.mac}${lease.name}${(lease.start as Date).toISOString()}${(lease.end as Date).toISOString()}`
    return getUuid(leaseString)
}

export const trimAndSplitToWords = (line: string): Array<string> => {
    if (!line) {
        throw new Error("nothing in line, critical!")
    }
    const trim = line.replace(";", "").trim()
    const words = trim.split(" ")
    return words
}
