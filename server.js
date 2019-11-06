const express = require("express");
const next = require("next");
//const path = require("path");
const expressRouter = express.Router; // make eslint shut up
const leases = require("./dhcpLeases");
const clients = require("./dhcpClients");
const cors = require("./cors");

const dev = process.env.NODE_ENV !== "production"; //true false
const nextApp = next({ dev });
const handle = nextApp.getRequestHandler(); //part of next config

const port = process.argv[2] || 4000;
console.log(`dev: ${dev}`);

const stdErrorHandling = (res, err) => {
    console.error(err);
    res.status(500).send("Something broke!");
};

nextApp.prepare().then(() => {
    const app = express();
    console.log(`app.get("env")=${app.get("env")}`);
    app.set("trust proxy", true);
    app.set("trust proxy", "loopback");

    app.use(express.json());

    const router = expressRouter();
    app.use("/api", router);

    router.get("/clients", async (req, res) => {
        cors.addHeaders(req, res);
        try {
            log("get leases");
            const allStd = await clients.getAll();
            res.json(allStd);
            log("got leases");
        } catch (err) {
            stdErrorHandling(res, err);
        }
    });
    router.get("/leases", async (req, res) => {
        cors.addHeaders(req, res);
        try {
            log("get leases");
            const allStd = await leases.getAll();
            res.json(allStd);
            log("got leases");
        } catch (err) {
            stdErrorHandling(res, err);
        }
    });
    router.get("/config", async (req, res) => {
        cors.addHeaders(req, res);
        try {
            log("get config");
            const conf = await clients.readConfig();
            res.json(conf);
            log("got conf");
        } catch (err) {
            stdErrorHandling(res, err);
        }
    });

    router.get("/leases/:mac", async (req, res) => {
        cors.addHeaders(req, res);
        try {
            log(`get leases for ${req.params.mac}`);
            const data = await leases.getLeasesByMac(req.params.mac);
            res.json(data);
            log(`got leases for ${req.params.mac}`);
        } catch (err) {
            stdErrorHandling(res, err);
        }
    });

    app.get("*", (req, res) => {
        return handle(req, res);
    });

    app.listen(port);
    console.log(`Listening on port: ${port}`);
});

const log = msg => {
    const d = new Date().toTimeString();
    console.log(`${d} - ${msg}`);
};
