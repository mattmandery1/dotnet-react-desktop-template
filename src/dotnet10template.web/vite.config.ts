import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const configDirectory = path.dirname(fileURLToPath(import.meta.url))

function readProductProperty(name: string, fallback: string) {
    const productPropsPath = findProductPropsPath(configDirectory)
    const productProps = fs.readFileSync(productPropsPath, 'utf8')
    const match = productProps.match(new RegExp(`<${name}>([^<]+)</${name}>`))

    return match?.[1]?.trim() || fallback
}

function findProductPropsPath(startDirectory: string) {
    let currentDirectory = startDirectory

    while (true) {
        const candidate = path.join(currentDirectory, 'Directory.Product.props')
        if (fs.existsSync(candidate)) {
            return candidate
        }

        const parentDirectory = path.dirname(currentDirectory)
        if (parentDirectory === currentDirectory) {
            throw new Error(`Directory.Product.props was not found from ${startDirectory}.`)
        }

        currentDirectory = parentDirectory
    }
}

export default defineConfig(({ mode }) => {
    const env = loadEnv(mode, process.cwd(), '')
    const productDisplayName = env.VITE_PRODUCT_DISPLAY_NAME ||
        readProductProperty('ProductDisplayName', 'Dotnet10Template Desktop')
    const productShortName = env.VITE_PRODUCT_SHORT_NAME ||
        readProductProperty('ProductShortName', 'Dotnet10Template')

    return {
        plugins: [react()],
        define: {
            'import.meta.env.VITE_PRODUCT_DISPLAY_NAME': JSON.stringify(productDisplayName),
            'import.meta.env.VITE_PRODUCT_SHORT_NAME': JSON.stringify(productShortName),
        },
        server: {
            proxy: {
                '/api': {
                    target: env.VITE_API_PROXY_TARGET,
                    changeOrigin: true,
                },
            },
        },
    }
})
