import { defineConfig, type PlaywrightTestConfig } from "@playwright/test";

// End to end against a running API (compose: http://localhost:8080) and the built web app served by vite preview.
// Set WEB_BASE to test an already running web server (or the deployed Static Web App) instead.
const apiBase = process.env.API_BASE ?? "http://localhost:8080";
const webServer: PlaywrightTestConfig["webServer"] = {
  // Vite bakes VITE_* variables in at build time, so the build must run with the API base set.
  command: "npx vite build && npx vite preview --port 5173 --strictPort",
  url: "http://localhost:5173",
  reuseExistingServer: true,
  timeout: 180_000,
  env: { VITE_API_BASE: apiBase },
};

export default defineConfig({
  testDir: "./e2e",
  timeout: 120_000,
  expect: { timeout: 30_000 },
  retries: 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: process.env.WEB_BASE ?? "http://localhost:5173",
    trace: "retain-on-failure",
  },
  ...(process.env.WEB_BASE ? {} : { webServer }),
});
