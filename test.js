const dhcp = require("./dhcpClients.js");

const test = async () => {
    // const d = new Date(Date.UTC(2019, 6, 29, 19, 0));
    // const res = await parse.getActiveLeases(d);
    const res = await dhcp.readConfig();
    console.log(res);
};

test()
    .then(console.log)
    .catch(console.error);
