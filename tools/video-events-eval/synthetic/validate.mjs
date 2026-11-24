// Checks every committed label file: frames in [0,frames), types in the closed vocabulary,
// unique ids, sidecar consistent with labels and the RenderEventManifest name rule.
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { VOCAB } from './src/model.js';

const dir = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'labelled', 'synthetic');
const errs = [];
let n = 0;
for (const f of readdirSync(dir).filter((x) => x.endsWith('.labels.json')).sort()) {
  const L = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  const S = JSON.parse(readFileSync(join(dir, f.replace('.labels.', '.events.')), 'utf8'));
  n++;
  const bad = (m) => errs.push(`${f}: ${m}`);
  if (!['animation', 'screen', 'silent', 'speech', 'music'].includes(L.videoType)) bad(`videoType ${L.videoType}`);
  if (L.events.length < 4 || L.events.length > (L.videoType === 'music' ? 80 : 14)) bad(`event count ${L.events.length} outside 4..14`);
  const ids = new Set();
  for (const e of L.events) {
    if (!VOCAB.includes(e.type)) bad(`${e.id}: type ${e.type}`);
    if (!Number.isInteger(e.frame) || e.frame < 0 || e.frame >= L.frames) bad(`${e.id}: frame ${e.frame} outside [0,${L.frames})`);
    if (ids.has(e.id)) bad(`duplicate id ${e.id}`); ids.add(e.id);
    if (e.region && (e.region.length !== 4 || e.region[0] < 0 || e.region[1] < 0 || e.region[0] + e.region[2] > L.width || e.region[1] + e.region[3] > L.height)) bad(`${e.id}: region ${e.region}`);
  }
  if (S.events.length !== L.events.length) bad('sidecar count differs');
  S.events.forEach((s, i) => {
    const e = L.events[i];
    if (!/^[a-z0-9_-]{1,40}$/.test(s.name)) bad(`sidecar name ${s.name}`);
    if (s.frame !== e.frame || s.name !== `${e.type}-${e.id}`) bad(`sidecar ${s.name} mismatch`);
  });
}
if (errs.length) { console.error(errs.join('\n')); process.exit(1); }
console.log(`ok: ${n} label files valid`);
