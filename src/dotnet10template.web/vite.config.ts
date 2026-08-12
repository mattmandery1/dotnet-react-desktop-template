import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const configDirectory = path.dirname(fileURLToPath(import.meta.url))

function readProductProperty(name: string, fallback: string) {
    const productPropsPath = path.resolve(configDirectory, '..', '..', 'Directory.Product.props')
    const productProps = fs.readFileSync(productPropsPath, 'utf8')
    const match = productProps.match(new RegExp(`<${name}>([^<]+)</${name}>`))

    return match?.[1]?.trim() || fallback
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
