import { flattenLeaves } from '@/components/workflows/field-diff';
import { parseConfigJson } from '@/lib/utils/config-case';

/** The save-time validator's line format: `Step <order> (<Type>): <message>`. */
const STEP_ERROR = /^Step\s+(\d+)\s*(?:\(([^)]*)\))?\s*:\s*([\s\S]+)$/;

/** Groups the server's per-step validation lines by 1-based step order. Unparseable lines go to `general`. */
export function parseStepErrors(errors: readonly string[]): { byStep: Map<number, string[]>; general: string[] } {
  const byStep = new Map<number, string[]>();
  const general: string[] = [];
  for (const line of errors) {
    const m = STEP_ERROR.exec(line.trim());
    if (!m) {
      general.push(line);
      continue;
    }
    const order = Number(m[1]);
    byStep.set(order, [...(byStep.get(order) ?? []), m[3].trim()]);
  }
  return { byStep, general };
}

/** Step columns a message can name. */
const COLUMN_KEYS = ['minScore', 'maxIterations', 'loopTargetStepOrder', 'conditionExpression', 'trueBranchStepOrder', 'falseBranchStepOrder'];

/** VideoGenerate's avatar fields (docs/avatars.md "Generating a clip with an avatar"). */
const AVATAR_KEYS = ['avatarId', 'avatarReferenceRoles', 'avatarAudio', 'avatarAudioProjectFileId'];

/**
 * Best-effort mapping of a server message to the field it is about: a field whose property name
 * appears in the message (case-insensitively, whole word) is the one to mark. Longest name wins so
 * `pollIntervalSeconds` is not mistaken for a shorter key inside it.
 */
export function fieldPathsForMessage(message: string, configJson: string | null | undefined): string[] {
  const lower = message.toLowerCase();
  const candidates: Array<{ key: string; path: string }> = COLUMN_KEYS.map((k) => ({ key: k, path: `col.${k}` }));
  const cfg = parseConfigJson(configJson);
  if (cfg) {
    for (const leaf of flattenLeaves(cfg).keys()) {
      candidates.push({ key: leaf.split('.').pop() as string, path: `config.${leaf}` });
    }
  }
  // The avatar fields are named even when the config does not carry them yet (or carries an object, whose
  // leaves have other names), so a refused avatar choice lands next to the avatar picker.
  for (const key of AVATAR_KEYS) candidates.push({ key, path: `config.${key}` });
  const hits = candidates
    .filter(({ key }) => key.length >= 4 && new RegExp(`(^|[^a-z0-9])${key.toLowerCase()}([^a-z0-9]|$)`).test(lower))
    .sort((a, b) => b.key.length - a.key.length);
  const paths = [...new Set(hits.map((h) => h.path))];
  // The save check's own lines ("avatar <id> was not found in this project.", "avatar <id> cannot be used: ...")
  // name no property: they are about the chosen avatar.
  if (!paths.some((p) => p.startsWith('config.avatar')) && /(^|[^a-z0-9])avatar([^a-z0-9]|$)/.test(lower)) paths.unshift('config.avatarId');
  return paths.slice(0, 3);
}
