#!/usr/bin/env node
import { main } from './lib/cli.mjs';

process.exitCode = await main(process.argv.slice(2), {
  env: process.env,
  stdout: (text) => process.stdout.write(text),
  stderr: (text) => process.stderr.write(text),
});
