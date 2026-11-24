#!/usr/bin/env node
// Validates docs/design/workflow-apps/eval-goals.json. Run from the repo root:
//   node docs/design/workflow-apps/check-eval-goals.mjs
// Exits non-zero on the first class of problem it finds (all problems are printed).
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..', '..');
const fixture = JSON.parse(readFileSync(join(here, 'eval-goals.json'), 'utf8'));
const catalog = readFileSync(join(root, 'inference/src/ReelBolt.Shared/Workflows/WorkflowTemplateCatalog.cs'), 'utf8');

const problems = [];

// Template keys: the literal `Key: "..."` entries plus the named constants used as keys.
const keys = new Set([...catalog.matchAll(/Key:\s*"([a-z0-9-]+)"/g)].map((m) => m[1]));
const consts = [...catalog.matchAll(/const string (\w+TemplateKey)\s*=\s*"([a-z0-9-]+)"/g)];
for (const [, , value] of consts) keys.add(value);

// Templates that do not exist yet but are decided (planned): key -> entry. A planned key must NOT yet be in the catalog
// (when it ships, delete it from plannedTemplates and from the goals' availability).
const planned = new Map((fixture.plannedTemplates ?? []).map((p) => [p.key, p]));
for (const key of planned.keys()) {
  if (keys.has(key)) problems.push(`plannedTemplates: ${key} now exists in the catalog; remove it from plannedTemplates and the goals' availability`);
}

const ROUTES = new Set(['template', 'template_with_patch', 'custom_graph', 'clarify_first', 'decline_with_alternative']);
const FORMATS = new Set(['Original', 'Vertical', 'Square', 'Landscape', 'Portrait']);
const RESOLUTIONS = new Set(['Auto', '720p', '1080p', '2160p']);
const PRESETS = new Set(['fast-social-reel', 'cinematic', 'corporate-explainer', 'documentary', 'beat-synced-music-video']);
const TONES = new Set(['Calm', 'Upbeat', 'Energetic', 'Serious', 'Playful', 'Inspirational']);
const VOICES = new Set(['none', 'narrated', 'project-voice']);
const DESIGN = new Set(['project', 'none']);
const ROLES = new Set(['code', 'video-clips', 'music-track', 'sfx', 'images', 'brief-doc']);
const BRIEF_FIELDS = new Set(['language', 'sourceLanguage', 'format', 'resolution', 'lengthSec', 'designSystem', 'stylePreset', 'voice', 'tone', 'notes', 'sourceFiles']);
const LINT = new Set(Array.from({ length: 19 }, (_, i) => `WL${String(i + 1).padStart(3, '0')}`));
const PROVIDERS = new Set(['chat', 'voice', 'videoGeneration', 'transcription', 'vision']);
const PLANS = new Set(['trial', 'creator', 'pro']);

const bad = (id, msg) => problems.push(`${id}: ${msg}`);

if (fixture.version !== 1) bad('fixture', 'version must be 1');
if (!Array.isArray(fixture.goals) || fixture.goals.length !== 50) bad('fixture', `expected 50 goals, found ${fixture.goals?.length}`);

const ids = new Set();
for (const g of fixture.goals ?? []) {
  const id = g.id ?? '?';
  if (!/^G\d{2}$/.test(id) || ids.has(id)) bad(id, 'id must be unique G01..G50');
  ids.add(id);
  if (!g.goal || g.goal.length < 15) bad(id, 'goal text missing or too short');
  if (!g.locale) bad(id, 'locale missing');
  const p = g.project ?? {};
  if (!PLANS.has(p.plan)) bad(id, `project.plan ${p.plan}`);
  for (const pr of p.providers ?? []) if (!PROVIDERS.has(pr)) bad(id, `unknown provider ${pr}`);
  const e = g.expected ?? {};
  if (!ROUTES.has(e.route)) bad(id, `route ${e.route}`);
  const needsTemplate = e.route === 'template' || e.route === 'template_with_patch';
  const isPlanned = needsTemplate && planned.has(e.templateKey);
  if (needsTemplate && !keys.has(e.templateKey) && !isPlanned) bad(id, `templateKey ${e.templateKey} is not in WorkflowTemplateCatalog or plannedTemplates`);
  if (isPlanned && e.availability !== `planned:${e.templateKey}`) bad(id, `planned template ${e.templateKey} needs expected.availability "planned:${e.templateKey}"`);
  if (!isPlanned && e.availability) bad(id, `availability is only for planned templates`);
  if (!needsTemplate && e.templateKey) bad(id, `route ${e.route} must not name a templateKey (${e.templateKey})`);
  for (const alt of e.alternates ?? []) {
    if (!keys.has(alt) && !planned.has(alt)) bad(id, `alternate ${alt} is not in WorkflowTemplateCatalog or plannedTemplates`);
    if (!keys.has(alt) && !planned.has(alt)) { /* reported above */ }
    if (alt === e.templateKey) bad(id, 'alternates must not repeat templateKey');
  }
  if (!needsTemplate && e.alternates?.length) bad(id, 'only template routes carry alternates');
  if (e.route === 'template_with_patch' && !(e.patch?.length > 0)) bad(id, 'template_with_patch needs a patch');
  if (e.route === 'clarify_first' && !(e.clarify?.length >= 1 && e.clarify.length <= 2)) bad(id, 'clarify_first needs 1-2 questions');
  if (e.route !== 'clarify_first' && e.clarify?.length) bad(id, 'only clarify_first carries questions');
  for (const r of e.lint ?? []) if (!LINT.has(r)) bad(id, `unknown lint rule ${r}`);
  const b = e.brief ?? {};
  for (const k of Object.keys(b)) if (!BRIEF_FIELDS.has(k)) bad(id, `unknown brief field ${k}`);
  if (b.format && !FORMATS.has(b.format)) bad(id, `format ${b.format}`);
  if (b.resolution && !RESOLUTIONS.has(b.resolution)) bad(id, `resolution ${b.resolution}`);
  if (b.stylePreset && !PRESETS.has(b.stylePreset)) bad(id, `stylePreset ${b.stylePreset}`);
  if (b.tone && !TONES.has(b.tone)) bad(id, `tone ${b.tone}`);
  if (b.voice && !VOICES.has(b.voice)) bad(id, `voice ${b.voice}`);
  if (b.designSystem && !DESIGN.has(b.designSystem)) bad(id, `designSystem ${b.designSystem}`);
  if (b.lengthSec !== undefined && !(Number.isInteger(b.lengthSec) && b.lengthSec >= 5 && b.lengthSec <= 600)) bad(id, `lengthSec ${b.lengthSec}`);
  if (b.language && !/^[a-z]{2,3}(-[A-Za-z0-9]+)?$/.test(b.language)) bad(id, `language ${b.language}`);
  if (b.notes && b.notes.length > 300) bad(id, 'notes over 300 characters');
  for (const r of b.sourceFiles ?? []) if (!ROLES.has(r)) bad(id, `sourceFiles role ${r}`);
  if (typeof e.paid !== 'boolean') bad(id, 'paid must be boolean');
  if (!e.why) bad(id, 'why missing');
}

const nonEnglish = (fixture.goals ?? []).filter((g) => g.locale !== 'en').length;
const withLint = (fixture.goals ?? []).filter((g) => g.expected?.lint?.length).length;
const counts = {};
for (const g of fixture.goals ?? []) counts[g.expected?.route] = (counts[g.expected?.route] ?? 0) + 1;
console.log(`goals=${fixture.goals?.length} nonEnglish=${nonEnglish} withLint=${withLint} routes=${JSON.stringify(counts)} catalogKeys=${keys.size}`);
if (nonEnglish < 12) bad('fixture', `need at least 12 non-English goals, found ${nonEnglish}`);
if (withLint < 8) bad('fixture', `need at least 8 goals with lint expectations, found ${withLint}`);

if (problems.length) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log('eval-goals.json: OK');
