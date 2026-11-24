/**
 * First run in one click: the pure decision. Given the chosen app, the project's files and what the
 * pre-run check and the estimate said, either one button runs the app ("Run it") or the person is
 * sent to the run page ("Review and run"), with the reason. Nothing here decides for the person:
 * a note, a consent, a choice between clips or a shortfall always means review.
 */
import type { WorkflowCostEstimate } from '@/lib/types/billing';
import type { AppFileRole, AppManifest, AppMediaEgress, LintFinding, RunBrief } from '@/lib/types/workflow';
import type { RunBlockedDetail } from '@/lib/telemetry/allowlist';
import { isPermissionInput } from './app-input-model';
import {
  appRoleOf,
  needsConfirm,
  DEFAULT_MAX_FILES,
  fileHasRole,
  pageBrief,
  type PageBriefFields,
} from './run-model';

export type ReviewReason = 'notes' | 'consent' | 'footage' | 'inputs' | 'shortfall' | 'check' | 'estimate' | 'cost';

/** The closed `alpha.run_blocked` token for a review reason (no text, nothing the person typed). */
export function reviewBlockDetail(reason: ReviewReason): RunBlockedDetail {
  switch (reason) {
    case 'notes': return 'notes_required';
    case 'shortfall': return 'credits';
    case 'cost': return 'estimate_gate';
    case 'estimate': return 'estimate_gate';
    default: return 'readiness';
  }
}

export type OneClickDecision =
  | { mode: 'oneClick'; brief: RunBrief }
  | { mode: 'review'; reason: ReviewReason };

interface FileLike {
  id: string;
  originalFileName: string;
  mimeType: string;
  category?: string;
  sourceKind?: string | null;
}

export interface OneClickInput {
  manifest: AppManifest;
  /** The workflow's own "needs a description" flag. */
  requiresUserInput: boolean;
  mediaEgress?: AppMediaEgress | null;
  files: readonly FileLike[];
  /** The pre-run check's findings; null while it has not answered. */
  findings: readonly Pick<LintFinding, 'ruleId' | 'severity'>[] | null;
  /** The estimate; null while it has not answered or when it failed. */
  estimate: Pick<WorkflowCostEstimate, 'billingEnabled' | 'affordable' | 'highCredits'> &
    Partial<Pick<WorkflowCostEstimate, 'fixedMaxCredits' | 'balanceCredits'>> | null;
  /** The keys of the style presets that exist; the app's suggested preset is sent only when it is one of them. */
  stylePresetKeys?: readonly string[] | undefined;
  /** The app's cost class; `No credits` never asks. */
  costClass?: string | null;
}

const FREE_CHOICE_ROLES: readonly AppFileRole[] = ['insertContent', 'overlay'];

function briefInput(manifest: AppManifest, field: string) {
  return manifest.inputs?.find((i) => i.kind === 'brief' && i.field === field);
}

/** The footage a slot would use, or why it cannot be used without the person choosing. */
function slotFiles(
  role: AppFileRole,
  max: number | null | undefined,
  required: boolean,
  files: readonly FileLike[],
): string[] | 'ambiguous' | 'missing' {
  const fitting = files.filter((f) => fileHasRole(f, role)).map((f) => f.id);
  if (fitting.length === 0) return required ? 'missing' : [];
  const cap = max ?? DEFAULT_MAX_FILES;
  // More files than the slot takes: which ones is the person's choice.
  return fitting.length > cap ? 'ambiguous' : fitting;
}

/**
 * The brief a one-click run would send (the footage slots filled, every other field at the app's own
 * default), or the reason the person has to look first. Needed before the pre-run check and the
 * estimate, which are asked for that very brief.
 */
export function oneClickBrief(
  input: Pick<OneClickInput, 'manifest' | 'requiresUserInput' | 'files' | 'stylePresetKeys'>,
): { brief: RunBrief } | { reason: ReviewReason } {
  const { manifest, files } = input;
  const inputs = manifest.inputs ?? [];
  const hasNotesBox = inputs.some((i) => i.kind === 'notes');
  if (input.requiresUserInput && hasNotesBox) return { reason: 'notes' };

  // The app's own fields (and an avatar, which needs a picked person) are the person's to fill.
  if (inputs.some((i) => (i.kind === 'input' && !isPermissionInput(i)) || (i.kind === 'brief' && i.field === 'avatar'))) {
    return { reason: 'inputs' };
  }

  const sourceFiles: { role: AppFileRole; fileIds: string[] }[] = [];
  let anySlot = false;
  for (const slot of inputs) {
    const role = slot.kind === 'files' ? appRoleOf(slot.role) : null;
    if (!role) continue;
    anySlot = true;
    // The picture slots start empty by design: never a guess.
    if (FREE_CHOICE_ROLES.includes(role)) continue;
    const picked = slotFiles(role, slot.max, slot.required !== false && role === 'video', files);
    if (picked === 'ambiguous' || picked === 'missing') return { reason: 'footage' };
    sourceFiles.push({ role, fileIds: picked });
  }
  if (anySlot && !sourceFiles.some((s) => s.fileIds.length > 0)) return { reason: 'footage' };

  const language = briefInput(manifest, 'language');
  const length = briefInput(manifest, 'lengthSec');
  const voice = briefInput(manifest, 'voice');
  const design = briefInput(manifest, 'designSystem');
  const fields: PageBriefFields = {
    language: String(language?.default ?? 'auto'),
    sourceLanguage: String(briefInput(manifest, 'sourceLanguage')?.default ?? 'auto'),
    lengthSec: length
      ? Number(length.default ?? (typeof length.choices?.[0] === 'number' ? length.choices[0] : 60))
      : null,
    narrated: voice ? voice.default !== false : null,
    voiceId: null,
    design: design ? (design.default === 'none' ? 'none' : 'project') : null,
    overlayCardAudio: String(briefInput(manifest, 'overlayCardAudio')?.default ?? 'agent'),
    sourceFiles,
  };
  const brief = pageBrief(fields);
  // Like the run page: the suggested style is a pre-selection, and only when it is a style that exists.
  const suggested = manifest.suggestedStylePreset;
  if (suggested && input.stylePresetKeys?.includes(suggested)) brief.stylePreset = suggested;
  return { brief };
}

export function decideOneClick(input: OneClickInput): OneClickDecision {
  const built = oneClickBrief(input);
  if ('reason' in built) return { mode: 'review', reason: built.reason };

  // The permission to send media is the person's alone; never switched on here. A required permission that is not
  // already allowed is review whatever the check says.
  const needsConsent = input.mediaEgress
    ? input.mediaEgress.alreadyAllowed !== true &&
      (input.mediaEgress.required || Boolean(input.findings?.some((f) => f.ruleId === 'WL015' && f.severity === 'error')))
    : false;
  if (needsConsent) return { mode: 'review', reason: 'consent' };

  if (!input.findings) return { mode: 'review', reason: 'check' };
  if (input.findings.some((f) => f.severity === 'error')) return { mode: 'review', reason: 'check' };

  if (!input.estimate) return { mode: 'review', reason: 'estimate' };
  if (input.estimate.billingEnabled && !input.estimate.affordable) return { mode: 'review', reason: 'shortfall' };
  // A billed run with no balance to show has no price line to click on.
  if (input.estimate.billingEnabled && (input.estimate.balanceCredits === null || input.estimate.balanceCredits === undefined)) {
    return { mode: 'review', reason: 'estimate' };
  }
  // Same rule as the run page's confirm dialog: a run that can cost this much is looked at first.
  if (needsConfirm(input.estimate, input.costClass)) return { mode: 'review', reason: 'cost' };

  return { mode: 'oneClick', brief: built.brief };
}
