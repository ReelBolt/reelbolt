import type { T } from '@/i18n/translator';
import type { RunAppInput } from '@/lib/types/workflow';
import { authored } from './AppInputField';

/** One app field of a past run, ready to show: its label and its value in words (docs/design/app-inputs.md 10.4). */
export interface RunInputWords {
  id: string;
  label: string;
  value: string;
}

const SHAPE_WORD: Record<string, string> = { '9:16': 'vertical', '1:1': 'square', '16:9': 'landscape', '4:5': 'portrait' };

/**
 * A past run's app field in words, in the reader's language where the page allows it: a curated label or choice from
 * the catalogue (else the page's own words), a switch by its words, a shape by its name, a model by name (never an id).
 * `t` is the root translator (`useT()`); curated keys of the page are under `apps.`.
 */
export function runInputWords(t: T, input: RunAppInput): RunInputWords {
  const label = authored(t, appsKey(input.labelKey), input.label) ?? input.id;
  return { id: input.id, label, value: valueWords(t, input) };
}

/** A curated key of the page (relative to the `apps` namespace) as a root key. */
function appsKey(key: string | null | undefined): string | null {
  return key ? `apps.${key}` : null;
}

const ALLOWED_BY = new Set(['app', 'api_key', 'mcp', 'assistant_card']);

function valueWords(t: T, input: RunAppInput): string {
  const v = input.value;
  // The permission to send media reads by who allowed it ("Allowed in the app"); an API key keeps its name from the server.
  if (input.id === 'allowMediaEgress' && v === true) {
    if (input.allowedBy === 'api_key' && input.valueText.trim()) return input.valueText;
    return t(`apps.permission.mediaEgress.allowedBy.${input.allowedBy && ALLOWED_BY.has(input.allowedBy) ? input.allowedBy : 'other'}`);
  }
  switch (input.type) {
    case 'model':
      return input.modelName?.trim() || t('apps.run.history.unknownModel');
    case 'choice':
      return authored(t, appsKey(input.valueLabelKey), input.valueText) ?? input.valueText;
    case 'toggle':
      return v === true
        ? input.onLabel?.trim() || t('apps.run.history.on')
        : input.offLabel?.trim() || t('apps.run.history.off');
    case 'shape':
      return typeof v === 'string' && SHAPE_WORD[v] ? `${t(`apps.run.inputs.shape.${SHAPE_WORD[v]}`)} (${v})` : input.valueText;
    case 'duration':
      return typeof v === 'number' ? t('apps.run.page.seconds', { count: v }) : input.valueText;
    case 'number':
      if (typeof v === 'number' && (input.unit === 'takes' || input.unit === 'percent')) {
        return t(`apps.run.inputs.unit.${input.unit}`, { count: v });
      }
      return input.valueText;
    default:
      return input.valueText;
  }
}

/** The history row's short line: the first two fields' values, as the plan's "Lite mode · “Welcome!”" (10.4). */
export function runInputsSummary(t: T, inputs: readonly RunAppInput[] | null | undefined): string | null {
  const words = (inputs ?? []).slice(0, 2).map((i) => {
    const w = runInputWords(t, i);
    // A switch reads by its label when on ("Lite mode"); every other field by its value.
    if (i.type === 'toggle') return i.value === true ? w.label : `${w.label}: ${w.value}`;
    if (i.type === 'shortText' || i.type === 'longText') return `“${shorten(w.value, 60)}”`;
    return w.value;
  });
  return words.length > 0 ? words.join(' · ') : null;
}

function shorten(text: string, max: number): string {
  const line = text.replace(/\s+/g, ' ').trim();
  return line.length > max ? `${line.slice(0, max - 1)}…` : line;
}
