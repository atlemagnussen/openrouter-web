import fs from "fs"

export const readFile = (filePath: string): Promise<string> => {
    return new Promise(resolve => {
        fs.readFile(filePath, "utf8", (err, data) => {
            if (err) throw err;
            resolve(data);
        })
    })
}

