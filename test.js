const dhcp = require("./dhcpClients.js");
const parse = require("./dhcpLeases.js");

const test = async () => {
    const d = new Date(Date.UTC(2019, 6, 29, 19, 0));
    const allLeases = await parse.readAllLeases();
    const res = await parse.getActiveLeases(allLeases, d);
    //const res = await dhcp.readConfig();
    console.log(res);
};

test()
    .then(console.log)
    .catch(console.error);
