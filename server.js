const next = require('next')
const path = require("path");
const express = require("express");
const app = express();
const expressRouter = express.Router; // make eslint shut up
const router = expressRouter();
const leases = require("./dhcpdLeases.js");

const port = process.argv[2] || 4000;

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

const clientPath = path.join(__dirname, "public");
app.use((req, res, next) => {
    res.header("Access-Control-Allow-Origin", "*");
    res.header("Access-Control-Allow-Headers", "Origin, X-Requested-With, Content-Type, Accept");
    next();
});
app.use(express.static(clientPath, staticOpts));
router.get("/leases", async (req, res) => {
    try {
        const allStd = await leases.ReadAllLeases();
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


app.set("trust proxy", true);
app.set("trust proxy", "loopback");

app.use(express.json());
app.use("/api", router);
app.listen(port);
console.log(`Listening on port: ${port}`);
