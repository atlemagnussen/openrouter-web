const getUuid = require('uuid-by-string')

var lease = {
    "ip":"192.168.1.156",
    "start": new Date("2019-07-30T07:11:23.000Z"),
    "end": new Date("2019-07-30T07:21:23.000Z"),
    "mac":"24:18:1d:35:52:90",
    "uid":"01:24:18:1d:35:52:90",
    "host":"Galaxy-S8"
}

var leaseString = `${lease.ip}${lease.mac}${lease.host}${lease.start.toISOString()}${lease.end.toISOString()}`

console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
console.log(getUuid(leaseString))
