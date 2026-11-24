import { lintWorkflow } from '../api/workflows';
import type { CreateWorkflowStepRequest } from '../types/workflow';

/**
 * Which brief fields can some step of this draft read? The server owns that rule (`BriefFieldConsumers`,
 * enforced by `AppManifestValidator`), so it is asked rather than copied: the pre-run linter reports WL018
 * for every exposed field nothing reads. Each candidate is probed under its own name as the label, and the
 * ones the linter does not name are the readable ones. Probed in batches within the 8-input cap.
 */
export async function readableBriefFields(
  projectId: string,
  workflowId: string,
  steps: CreateWorkflowStepRequest[],
  candidates: string[],
): Promise<string[]> {
  const unreadable = new Set<string>();
  for (let i = 0; i < candidates.length; i += 8) {
    const batch = candidates.slice(i, i + 8);
    const res = await lintWorkflow(projectId, workflowId, {
      steps,
      appManifest: { version: 1, inputs: batch.map((field) => ({ kind: 'brief', field, label: field })) },
    });
    for (const finding of res.findings) {
      if (finding.ruleId !== 'WL018') continue;
      // The finding carries the input it is about as a token (T-109): index first, then the label. Never
      // match on `finding.message`, which is English and may be translated client-side.
      const index = Number(finding.args?.inputIndex);
      if (Number.isInteger(index) && index >= 0 && index < batch.length) {
        unreadable.add(batch[index]!);
        continue;
      }
      const label = finding.args?.input;
      if (label !== undefined && batch.includes(label)) unreadable.add(label);
    }
  }
  return candidates.filter((c) => !unreadable.has(c));
}
