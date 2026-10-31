import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: "../src/CipherVault.Server/wwwroot",
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 900,
  },
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": {
        target: "http://127.0.0.1:43127",
        changeOrigin: true,
      },
      "/mcp": {
        target: "http://127.0.0.1:43127",
        changeOrigin: true,
      },
    },
  },
});
