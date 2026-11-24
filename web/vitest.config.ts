import path from "node:path";
import { defineConfig } from "vitest/config";

// Mirrors tsconfig.json's "@/*" -> "./*" path mapping for Vitest's own module
// resolution. Without this, any test that transitively imports a module using
// the "@/..." alias fails at transform time with "Failed to resolve import"
// (source files use "@/..." extensively; only tests importing purely
// relative-path modules were unaffected).
export default defineConfig({
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "."),
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./vitest.setup.ts"],
  },
});
