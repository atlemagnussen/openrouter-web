const parse = require("./dhcpdLeases.js");

const test = async () => {
    const d = new Date(Date.UTC(2019, 6, 29, 19, 0));
    // const res = await parse.getActiveLeases(d);
    const res = await parse.getActiveClients(d);
    console.log(res);
};

test()
    .then(console.log)
    .catch(console.error);
