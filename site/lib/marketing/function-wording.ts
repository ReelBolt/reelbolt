// The consent wording version for the Cloudflare Pages Functions. A plain JSON import (no import
// attribute): the Pages bundler (wrangler 3, esbuild) rejects `with { type: 'json' }` and loads JSON
// natively. Imported only from site/functions; Next and node --test use wording.ts instead.
import registry from './marketing-consent.wording.json';
import { currentWording, type WordingRegistry } from './wording-core';

export const FUNCTION_WORDING_VERSION: string = currentWording(registry as WordingRegistry).version;
