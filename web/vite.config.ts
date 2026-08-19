import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// The API base is read at runtime from VITE_API_BASE; in dev we proxy /api to the backend.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5080", changeOrigin: true },
      "/health": { target: "http://localhost:5080", changeOrigin: true }
    }
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/setupTests.ts"],
    fileParallelism: false,
    maxWorkers: 1
  }
});
