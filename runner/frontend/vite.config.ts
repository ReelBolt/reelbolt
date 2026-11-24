/// <reference types="vitest/config" />
// Vitest 4 no longer augments vite's UserConfig with `test` on its own; this
// reference brings that augmentation in so `test` below type-checks.
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The window is served by the Wails host's INTERNAL asset server, not by a
// listening port: the host mounts the built files on the webview's own request
// path and forwards /api/* to the Go shell. So the build emits relative asset
// URLs, and nothing here configures a server for production.
export default defineConfig({
  plugins: [react()],
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    // The host embeds dist/ into the binary (see cmd/reelbolt-runner-desktop),
    // so a single chunk per entry keeps that small and predictable.
    sourcemap: false,
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
