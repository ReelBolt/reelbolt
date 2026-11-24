#!/usr/bin/env node
// Validates docs/design/workflow-apps/app-input-eval-goals.json (docs/design/app-inputs.md 29). Run from the repo root:
//   node docs/design/workflow-apps/check-app-input-eval-goals.mjs
// The scorer itself is AppInputEvalScoring (inference/tests/ReelBolt.WorkflowEngine.Tests/AssistantEval); its tests run with
//   dotnet test inference/tests/ReelBolt.WorkflowEngine.Tests --filter AppInputEvalScoringTests
// Neither calls a model. Exits non-zero when anything is wrong (every problem is printed).
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..', '..');
const read = (p) => readFileSync(join(root, p), 'utf8');
const fixture = JSON.parse(readFileSync(join(here, 'app-input-eval-goals.json'), 'utf8'));
const recipeGoals = JSON.parse(readFileSync(join(here, 'eval-goals.json'), 'utf8'));
const catalog = read('inference/src/ReelBolt.Shared/Workflows/WorkflowTemplateCatalog.cs');
const recipeInputs = read('inference/src/ReelBolt.Shared/Workflows/Apps/WorkflowTemplateAppInputs.cs');
const enums = read('inference/src/ReelBolt.Shared/Data/Models/Enums.cs');
const toolsDir = join(root, 'inference/src/ReelBolt.Inference.Api/Agents/Tools');
const toolSource = readdirSync(toolsDir).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(join(toolsDir, f), 'utf8')).join('\n');

const problems = [];
const bad = (id, msg) => problems.push(`${id}: ${msg}`);

const keys = new Set([...catalog.matchAll(/Key:\s*"([a-z0-9-]+)"/g)].map((m) => m[1]));
for (const [, , value] of catalog.matchAll(/const string (\w+TemplateKey)\s*=\s*"([a-z0-9-]+)"/g)) keys.add(value);
const capabilityBlock = enums.slice(enums.indexOf('public enum InferenceProviderCapability'));
const capabilities = new Set([...capabilityBlock.slice(0, capabilityBlock.indexOf('}')).matchAll(/^\s*([A-Z]\w+)\s*(?:=\s*\d+)?\s*,?\s*$/gm)].map((m) => m[1]));

const OPS = new Set(['setAppInput', 'removeAppInput', 'bindAppInput', 'setAppPage']);
const TYPES = new Set(['shortText', 'longText', 'number', 'duration', 'choice', 'toggle', 'shape', 'model']);
const MODES = new Set(['placeholder', 'value']);
const PROPOSALS = new Set(['patch', 'patchOrSave', 'execute']);
const ALTERNATES = new Set(['clarify_first']);
const MUST_NOT = new Set(['egress', 'ids', 'prices']);

if (fixture.version !== 2) bad('fixture', 'version must be 2');
if (!Array.isArray(fixture.goals) || fixture.goals.length !== 5) bad('fixture', `expected 5 goals, found ${fixture.goals?.length}`);
// The pinned recipe-first gate is never widened by these goals.
if (recipeGoals.goals?.length !== 50) bad('eval-goals.json', `the recipe-first set must stay at 50 goals, found ${recipeGoals.goals?.length}`);

const ids = new Set();
for (const g of fixture.goals ?? []) {
  const id = g.id ?? '?';
  if (!/^AI\d{2}$/.test(id) || ids.has(id)) bad(id, 'id must be unique AI01..AI99');
  ids.add(id);
  if (!g.locale) bad(id, 'locale missing');
  if (!g.goal || g.goal.length < 15) bad(id, 'goal text missing or too short');
  const e = g.expected ?? {};
  for (const tool of e.tools ?? []) if (!toolSource.includes(`"${tool}"`)) bad(id, `tool ${tool} is not an assistant tool`);
  if (e.templateKey && !keys.has(e.templateKey)) bad(id, `templateKey ${e.templateKey} is not in WorkflowTemplateCatalog`);
  const s = e.score;
  if (!s) { bad(id, 'expected.score missing'); continue; }
  if (!PROPOSALS.has(s.proposal)) bad(id, `score.proposal ${s.proposal}`);
  if (s.apply !== undefined && typeof s.apply !== 'boolean') bad(id, 'score.apply must be a boolean');
  const aliases = new Set();
  for (const [i, op] of (s.ops ?? []).entries()) {
    const at = `score.ops[${i}]`;
    if (!OPS.has(op.op)) { bad(id, `${at}: unknown op ${op.op}`); continue; }
    if (op.op === 'setAppInput') {
      if (!TYPES.has(op.type)) bad(id, `${at}: unknown type ${op.type}`);
      if (op.capability && !capabilities.has(op.capability)) bad(id, `${at}: unknown capability ${op.capability}`);
      if (op.capability && op.type !== 'model') bad(id, `${at}: only a model field names a capability`);
      if (op.recommended && op.recommended !== 'cheapest') bad(id, `${at}: recommended must be "cheapest"`);
      if (op.as) aliases.add(op.as);
    }
    if (op.op === 'bindAppInput') {
      if (!MODES.has(op.mode)) bad(id, `${at}: mode ${op.mode}`);
      if (!op.field) bad(id, `${at}: field missing`);
      if (op.input && !aliases.has(op.input)) bad(id, `${at}: input "${op.input}" is not the alias of an earlier setAppInput`);
    }
    if (op.op === 'removeAppInput' && !op.id) bad(id, `${at}: id missing`);
  }
  if (s.conditional && !aliases.has(s.conditional.reads)) bad(id, `score.conditional.reads "${s.conditional.reads}" is not an alias`);
  for (const alt of s.alternates ?? []) if (!ALTERNATES.has(alt)) bad(id, `unknown alternate ${alt}`);
  for (const rule of s.mustNot ?? []) if (!MUST_NOT.has(rule)) bad(id, `unknown mustNot ${rule}`);
  const removed = new Set((s.ops ?? []).filter((o) => o.op === 'removeAppInput').map((o) => o.id));
  for (const only of s.onlyRemoves ?? []) if (!removed.has(only)) bad(id, `onlyRemoves ${only} is not removed by an op`);
  if (s.proposal === 'execute') {
    if (!e.templateKey) bad(id, 'an execute goal names its recipe (templateKey)');
    if (!s.inputs || Object.keys(s.inputs).length === 0) bad(id, 'an execute goal lists brief.inputs');
    for (const input of Object.keys(s.inputs ?? {})) if (!recipeInputs.includes(`"${input}"`)) bad(id, `input ${input} is not a recipe field`);
    if (JSON.stringify(s.inputs ?? {}) !== JSON.stringify(e.brief?.inputs ?? {})) bad(id, 'score.inputs must equal expected.brief.inputs');
  } else if ((s.ops ?? []).length === 0) bad(id, 'a patch or save goal lists its ops');
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log(`app-input-eval-goals.json: ${fixture.goals.length} goals ok (the recipe-first set stays at ${recipeGoals.goals.length})`);
