const express = require("express");
const next = require('next')
const path = require("path");
const expressRouter = express.Router; // make eslint shut up
const leases = require("./dhcpdLeases.js");

const dev = process.env.NODE_ENV !== "production"; //true false
const nextApp = next({ dev });
const handle = nextApp.getRequestHandler() //part of next config

const port = process.argv[2] || 4000;
console.log(`dev: ${dev}`);
// static /
const staticOpts = {
    "dotfiles": "ignore",
    "etag": false,
    "extensions": ["js", "html"],
    "index": "index.html",
    "maxAge": "1d",
    "redirect": false,
    "setHeaders": (res) => {
        res.set("x-timestamp", Date.now());
    }
};

const stdErrorHandling = (res, err) => {
    console.error(err);
    res.status(500).send("Something broke!");
};


nextApp.prepare()
.then(() => {
    const app = express();
    console.log(`app.get('env')=${app.get('env')}`);
    app.set("trust proxy", true);
    app.set("trust proxy", "loopback");
    
    app.use(express.json());

    const router = expressRouter();
    app.use("/api", router);

    router.get("/leases", async (req, res) => {
        try {
            const allStd = await leases.getActiveLeases();
            res.json(allStd);
        } catch (err) {
            stdErrorHandling(res, err);
        }
    });
    
    router.get("/leases/:mac", async (req, res) => {
        try {
            const data = await leases.getLeasesByMac(req.params.mac);
            res.json(data);
        } catch (err) {
            stdErrorHandling(res, err);
        }
    });
    
    app.get('*', (req, res) => {
        return handle(req, res);
    });

    
    app.listen(port);
    console.log(`Listening on port: ${port}`);
});

