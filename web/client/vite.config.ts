import { defineConfig } from "vite";

// Builds straight into the ASP.NET app's wwwroot/dist (gitignored). File names are stable:
// the app adds its own version hash (asp-append-version).
export default defineConfig(({ mode }) => ({
    build: {
        outDir: "../OpenRouter.Web/wwwroot/dist",
        emptyOutDir: true,
        sourcemap: mode === "development",
        minify: mode !== "development",
        rolldownOptions: {
            input: {
                traffic: "src/traffic.ts",
            },
            output: {
                entryFileNames: "[name].js",
                chunkFileNames: "[name].js",
                assetFileNames: "[name][extname]",
            },
        },
    },
}));
