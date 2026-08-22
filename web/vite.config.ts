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
    // The default 5000ms is too tight once the full suite runs its jsdom environments in
    // parallel: real userEvent.type() keystroke-by-keystroke simulation under MUI's component
    // weight reliably exceeds it under contention (reproduced again with NewHire, Workflows, and
    // AppTemplates: each passes in isolation and with bounded workers, but can cross 10 seconds in
    // the full run). Keep parallel coverage and give interaction-heavy tests a realistic cushion.
    testTimeout: 20000
  }
});
