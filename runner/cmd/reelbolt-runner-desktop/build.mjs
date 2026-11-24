#!/usr/bin/env node
// Build the window and put it where the Go embed can find it.
//
//   node runner/cmd/reelbolt-runner-desktop/build.mjs
//
// It runs the frontend's own build (Vite) and copies dist/ here, because
// go:embed cannot reach outside its own package directory. dist/ is NOT
// committed: only its .gitkeep, so a checkout that has never run this still
// compiles and says so in the window.
import { cpSync, existsSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const frontend = join(here, "..", "..", "frontend");

if (!existsSync(join(frontend, "package.json"))) {
  console.error("frontend not found at " + frontend);
  process.exit(1);
}

execFileSync("npm", ["run", "build"], { cwd: frontend, stdio: "inherit" });

const from = join(frontend, "dist");
const to = join(here, "dist");
rmSync(to, { recursive: true, force: true });
cpSync(from, to, { recursive: true });
console.log("copied " + from + " -> " + to);
