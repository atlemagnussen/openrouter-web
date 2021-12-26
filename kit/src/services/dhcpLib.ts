import * as getUuid from "uuid-by-string"

export const parseSubnet = (line: string) => {
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

export const parseNetmask = (line: string) => {
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

export const parseRange = (line: string) => {
    const words = trimAndSplitToWords(line)
    if (words.length === 3 && words[0] === "range") {
        return {
            from: words[1],
            to: words[2],
        }
    }
    throw new Error("no range to be found")
}

export const parseRouters = (line: string) => {
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

export const parseDns = (line) => {
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

export const parseLeaseTime = (line) => {
    const words = trimAndSplitToWords(line)
    if (words.length === 2 && (words[0] === "default-lease-time" || words[0] === "max-lease-time")) {
        return words[1]
    }
    throw new Error("no lease time to be found")
}

export const parseHost = (line) => {
    const words = trimAndSplitToWords(line)
    if (words.length > 1 && words[0] === "host") {
        return words[1]
    }
    throw new Error("no host to be found")
}

export const parseMac = (line) =>  {
    return line
        .replace("hardware", "")
        .replace("ethernet", "")
        .replace(";", "")
        .trim()
}

export const parseFixedIp = (line) => {
    const words = trimAndSplitToWords(line)
    if (words.length > 1 && words[0] === "fixed-address") {
        return words[1]
    }
    throw new Error("no fixed address to be found")
}

export const parseDateTime = (line) => {
    const trim = line
        .replace("starts", "")
        .replace("ends", "")
        .trim()
    const year = trim.substring(2, 6)
    const month = trim.substring(7, 9)
    const monthInt = parseInt(month, 10)
    const date = trim.substring(10, 12)
    const hour = trim.substring(13, 15)
    const min = trim.substring(16, 18)
    const sec = trim.substring(19, 21)
    const d = new Date(Date.UTC(year, monthInt - 1, date, hour, min, sec))
    return d
}

export const parseLeaseIp = (line) => {
    return line
        .replace("lease", "")
        .replace("{", "")
        .trim()
}

export const parseHostName = (line) => {
    return line
        .replace("client-hostname", "")
        .replace(";", "")
        .replace(/"/gi, "")
        .trim()
}

export const parseUid = (line) => {
    return line
        .replace("uid", "")
        .replace(";", "")
        .trim()
}

export const getUuidDet = (lease) => {
    const leaseString = `${lease.ip}${lease.mac}${lease.host}${lease.start.toISOString()}${lease.end.toISOString()}`
    return getUuid(leaseString)
}

export const trimAndSplitToWords = (line) => {
    if (!line) {
        throw new Error("nothing in line, critical!")
    }
    const trim = line.replace(";", "").trim()
    const words = trim.split(" ")
    return words
}
