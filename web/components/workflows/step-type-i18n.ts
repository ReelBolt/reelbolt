import { useT } from '@/i18n';
import type { StepType } from '@/lib/types/workflow';

/** Resolves the customer-facing name/description of a step type through the catalogue (`workflows.stepType.<Type>.*`). The API value never changes. */
export function useStepTypeText() {
  const t = useT('workflows.stepType');
  return {
    label: (type: StepType | string): string => (t.has(`${type}.label`) ? t(`${type}.label`) : String(type)),
    description: (type: StepType | string): string => (t.has(`${type}.description`) ? t(`${type}.description`) : ''),
  };
}
