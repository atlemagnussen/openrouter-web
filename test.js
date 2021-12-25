const dhcp = require("./dhcpClients.js")
const parse = require("./dhcpLeases.js")

const test = async () => {
    const d = new Date(Date.UTC(2021, 11, 24, 19, 0))
    const allLeases = await parse.readAllLeases()
    //console.log(allLeases)
    const active = await parse.getActiveLeases(allLeases, d)
    //console.log(active)
    const res = await dhcp.readConfig()
    console.log(res)
}

test()
    .then(console.log)
    .catch(console.error)
