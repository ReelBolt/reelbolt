import type { CreateWorkflowStepRequest } from '@/lib/types/workflow';
import { camelizeKeys } from '@/lib/utils/config-case';
import type { StepData } from './WorkflowStepList';

/** One field the assistant's pending proposal would change on a step. */
export interface FieldChange {
  before: unknown;
  after: unknown;
}

/** path -> change. Config blob fields live under `config.<dotted.path>`, step columns under `col.<name>`. */
export type StepFieldDiff = Map<string, FieldChange>;

/** Request-side `*ConfigJson` key paired with the StepData property that carries the same config. */
const CONFIG_PAIRS: Array<{ json: keyof CreateWorkflowStepRequest; data: keyof StepData; raw?: boolean }> = [
  { json: 'extractConfigJson', data: 'extractConfig' },
  { json: 'videoAnalyzeConfigJson', data: 'videoAnalyzeConfig' },
  { json: 'videoCompileConfigJson', data: 'videoCompileConfig' },
  { json: 'videoGenerateConfigJson', data: 'videoGenerateConfig' },
  { json: 'voiceoverConfigJson', data: 'voiceoverConfig' },
  { json: 'thumbnailGenerateConfigJson', data: 'thumbnailGenerateConfig' },
  { json: 'timelineRenderConfigJson', data: 'timelineRenderConfig' },
  { json: 'designSystemConfigJson', data: 'designSystemConfig' },
  { json: 'extractFrameConfigJson', data: 'extractFrameConfig' },
  { json: 'audioGenerateConfigJson', data: 'audioGenerateConfig' },
  { json: 'imageGenerateConfigJson', data: 'imageGenerateConfig' },
  { json: 'audioCleanupConfigJson', data: 'audioCleanupConfig' },
  { json: 'videoEnhanceConfigJson', data: 'videoEnhanceConfig' },
  { json: 'conditionalConfigJson', data: 'conditionalConfig' },
  { json: 'reviewLoopConfigJson', data: 'reviewLoopConfig' },
  { json: 'editRoomConfigJson', data: 'editRoomConfigJson', raw: true },
  { json: 'graphicsRoomConfigJson', data: 'graphicsRoomConfigJson', raw: true },
  { json: 'colorGradeRoomConfigJson', data: 'colorGradeRoomConfigJson', raw: true },
  { json: 'dailiesRoomConfigJson', data: 'dailiesRoomConfigJson', raw: true },
];

const COLUMNS = [
  'label', 'agentDefinitionId', 'conditionExpression', 'loopSourceExpression', 'loopTargetStepOrder',
  'maxIterations', 'minScore', 'inputMappingJson', 'trueBranchStepOrder', 'falseBranchStepOrder', 'cacheMode',
] as const;

function parse(value: unknown, raw: boolean | undefined): unknown {
  if (value == null || value === '') return null;
  if (!raw && typeof value !== 'string') return value;
  if (typeof value !== 'string') return value;
  try {
    return camelizeKeys(JSON.parse(value));
  } catch {
    return null;
  }
}

/** Leaf paths of a JSON value; arrays and scalars are leaves so list edits show on the list's own field. */
export function flattenLeaves(value: unknown, prefix = '', out: Map<string, unknown> = new Map()): Map<string, unknown> {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      flattenLeaves(v, prefix ? `${prefix}.${k}` : k, out);
    }
  } else if (prefix) {
    out.set(prefix, value ?? null);
  }
  return out;
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

/**
 * Which fields of `current` the proposed `proposed` step would change. Pure, so the editors can
 * highlight "the assistant wants to change THIS field" without the assistant owning any UI here.
 */
export function computeStepFieldDiff(current: StepData, proposed: CreateWorkflowStepRequest | undefined): StepFieldDiff {
  const diff: StepFieldDiff = new Map();
  if (!proposed) return diff;

  for (const col of COLUMNS) {
    if (!(col in proposed)) continue;
    const before = (current as unknown as Record<string, unknown>)[col];
    const after = (proposed as unknown as Record<string, unknown>)[col];
    if (!same(before, after)) diff.set(`col.${col}`, { before, after });
  }
  if (proposed.parallelAgentIdsJson != null) {
    const after = parse(proposed.parallelAgentIdsJson, true);
    if (!same(current.parallelAgentIds, after)) diff.set('col.parallelAgentIds', { before: current.parallelAgentIds, after });
  }

  for (const pair of CONFIG_PAIRS) {
    if (proposed[pair.json] === undefined) continue;
    const afterObj = parse(proposed[pair.json], true);
    const beforeObj = parse((current as unknown as Record<string, unknown>)[pair.data as string], pair.raw);
    const a = flattenLeaves(afterObj);
    const b = flattenLeaves(beforeObj);
    for (const key of new Set([...a.keys(), ...b.keys()])) {
      if (key === 'version') continue;
      if (!same(b.get(key), a.get(key))) diff.set(`config.${key}`, { before: b.get(key) ?? null, after: a.get(key) ?? null });
    }
  }
  return diff;
}

/** Short human rendering of a value for the "was -> now" note. */
export function formatDiffValue(v: unknown): string {
  if (v === null || v === undefined || v === '') return 'empty';
  if (typeof v === 'string') return v.length > 40 ? `${v.slice(0, 37)}...` : v;
  const s = JSON.stringify(v);
  return s.length > 40 ? `${s.slice(0, 37)}...` : s;
}
