import fs from "fs"
import path from "path"

const rootDir = process.cwd()

console.log("rootDir", rootDir)

export const readFile = (filePath: string): Promise<string> => {
    return new Promise(resolve => {
        const fullpath = path.resolve(rootDir, filePath)
        console.log("fullPath", fullpath)
        fs.readFile(fullpath, "utf8", (err, data) => {
            if (err) throw err;
            resolve(data);
        })
    })
}

