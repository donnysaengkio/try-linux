import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Built into dist/, which the Tauri app loads (tauri.conf.json's frontendDist). The icons come from the
// repository's shared icons/ folder, outside this one, so the dev server is allowed to read it.
export default defineConfig({
  plugins: [react()],
  base: "./",
  clearScreen: false,
  server: { port: 1420, strictPort: true, fs: { allow: ["..", "../../icons"] } },
  build: { outDir: "dist", emptyOutDir: true, target: "es2022", chunkSizeWarningLimit: 1500 }, // loaded from disk, not the web
});
