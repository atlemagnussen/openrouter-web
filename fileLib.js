const fs = require("fs");

class FileLib {
    readFile(filePath) {
        return new Promise(resolve => {
            fs.readFile(filePath, "utf8", (err, data) => {
                if (err) throw err;
                resolve(data);
            });
        });
    }
}

module.exports = new FileLib();
