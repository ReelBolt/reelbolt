/**
 * Step options carried on the step itself rather than in a type-specific config: "continue from an earlier run's
 * composition" (`continueCompositionJson`) and "save the output as a project file" (`registerOutputAsProjectFile`).
 * Mirrors `ContinueCompositionConfig` (ReelBolt.Shared/Workflows): exactly one of `executionId` / `versionId`.
 */
export interface ContinueComposition {
  executionId?: string | null;
  versionId?: string | null;
}

/** Parses the stored `continueCompositionJson`; null for empty or unusable input. Keys are matched case-insensitively. */
export function parseContinueComposition(raw: string | null | undefined): ContinueComposition | null {
  if (!raw) return null;
  try {
    const value = JSON.parse(raw) as Record<string, unknown> | null;
    if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
    const pick = (name: string): string | null => {
      const key = Object.keys(value).find((k) => k.toLowerCase() === name.toLowerCase());
      const v = key ? value[key] : null;
      return typeof v === 'string' && v ? v : null;
    };
    return { executionId: pick('executionId'), versionId: pick('versionId') };
  } catch {
    return null;
  }
}

/**
 * The `continueCompositionJson` to send, or null. A choice that names nothing yet (the switch is on but no run is
 * picked) is not sent: the server needs exactly one id, and a half-made choice must not fail a save.
 */
export function continueCompositionToJson(value: ContinueComposition | null | undefined): string | null {
  if (!value) return null;
  if (value.executionId) return JSON.stringify({ executionId: value.executionId });
  if (value.versionId) return JSON.stringify({ versionId: value.versionId });
  return null;
}

/**
 * Step-option fields of a create request, left out entirely when unset so an untouched step sends what it always did.
 * Also carries `pinInsertConfigJson` (T-206): the PinInsert config rides this shared spread, which both builder pages
 * already send, so a PinInsert step saves without a page-level mapping of its own.
 */
export function stepOptionsToRequest(step: {
  continueComposition?: ContinueComposition | null;
  registerOutputAsProjectFile?: boolean;
  stepType?: string;
  pinInsertConfig?: unknown;
}): { continueCompositionJson?: string; registerOutputAsProjectFile?: true; pinInsertConfigJson?: string } {
  const json = continueCompositionToJson(step.continueComposition);
  return {
    ...(json ? { continueCompositionJson: json } : {}),
    ...(step.registerOutputAsProjectFile ? { registerOutputAsProjectFile: true as const } : {}),
    ...(step.stepType === 'PinInsert' && step.pinInsertConfig ? { pinInsertConfigJson: JSON.stringify(step.pinInsertConfig) } : {}),
  };
}
