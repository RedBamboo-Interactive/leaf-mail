import { fileURLToPath } from "node:url"
import fs from "node:fs"
import path from "node:path"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"
import { federation } from "@module-federation/vite"

const pluginJson = JSON.parse(
  fs.readFileSync(path.resolve(fileURLToPath(import.meta.url), "../../plugin.json"), "utf-8"),
)

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    federation({
      name: pluginJson.id,
      filename: "remoteEntry.js",
      exposes: {
        ".": "./src/index.ts",
      },
      shared: {
        react: { singleton: true },
        "react/jsx-runtime": { singleton: true },
        "react-dom": { singleton: true },
        "react-dom/client": { singleton: true },
        "react-router-dom": { singleton: true },
        "@redbamboo/ui": { singleton: true },
        "@redbamboo/utility": { singleton: true },
      },
      dts: false,
    }),
  ],
  define: {
    "process.env.NODE_ENV": JSON.stringify("production"),
  },
  build: {
    target: "es2023",
    emptyOutDir: true,
    sourcemap: false,
    lib: {
      entry: fileURLToPath(new URL("./src/index.ts", import.meta.url)),
      formats: ["es"],
      fileName: () => "plugin.js",
      cssFileName: "plugin",
    },
    rollupOptions: {
      output: {
        chunkFileNames: "chunks/[name]-[hash].js",
        assetFileNames: (info) => {
          const n = (info.names && info.names[0]) || info.name || ""
          return n.endsWith(".css") ? "plugin.css" : "assets/[name]-[hash][extname]"
        },
      },
    },
  },
})
