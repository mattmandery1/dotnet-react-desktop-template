import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url))
const webRoot = path.resolve(scriptDirectory, '..')
const repoRoot = path.resolve(webRoot, '..', '..')
const productPropsPath = path.join(repoRoot, 'Directory.Product.props')

const productProps = fs.readFileSync(productPropsPath, 'utf8')

function readProductProperty(name) {
    const match = productProps.match(new RegExp(`<${name}>([^<]+)</${name}>`))

    if (!match?.[1]?.trim()) {
        throw new Error(`Directory.Product.props is missing ${name}.`)
    }

    return match[1].trim()
}

function updateJsonFile(filePath, update) {
    const original = fs.readFileSync(filePath, 'utf8')
    const data = JSON.parse(original)

    update(data)

    const next = `${JSON.stringify(data, null, 2)}\n`
    if (next !== original) {
        fs.writeFileSync(filePath, next)
    }
}

const webPackageName = readProductProperty('WebPackageName')

updateJsonFile(path.join(webRoot, 'package.json'), (data) => {
    data.name = webPackageName
})

const packageLockPath = path.join(webRoot, 'package-lock.json')
if (fs.existsSync(packageLockPath)) {
    updateJsonFile(packageLockPath, (data) => {
        data.name = webPackageName

        if (data.packages?.['']) {
            data.packages[''].name = webPackageName
        }
    })
}
