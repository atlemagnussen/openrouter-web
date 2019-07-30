class Cors {
    addHeaders(req, res) {
        let origin = 'http://192.168.1.96';
        try {
            const orgHead = req.get("Origin");
            console.log(orgHead);
            if (orgHead.includes('matspart.no')) {
                origin = orgHead;
            }
        } catch (e) {
            console.log('Could not get origin header');
        }
        
        res.set('Access-Control-Allow-Origin', origin);
        res.set("Access-Control-Allow-Credentials", "true");
        res.set("Access-Control-Allow-Methods", "GET,HEAD,OPTIONS,POST,PUT");
        res.set("Access-Control-Allow-Headers", "Access-Control-Allow-Headers, Origin,Accept, X-Requested-With, Content-Type, Access-Control-Request-Method, Access-Control-Request-Headers");

    }
}

module.exports = new Cors();