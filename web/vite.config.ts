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
    // weight reliably exceeds it under contention (reproduced: Register's multi-field form test
    // times out in the full run, passes in isolation). Bump rather than serialize workers --
    // serializing was already tried and correctly reverted in an earlier task as unjustified
    // speculative engineering; this is the actual, evidenced fix.
    testTimeout: 10000
  }
});
