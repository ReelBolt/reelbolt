'use client';

import {
  ActionIcon, Button, Checkbox, Group, MultiSelect, NumberInput, SegmentedControl, Select, Stack, Switch, Text, TextInput, Tooltip,
} from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { AppInputOption, AppManifest, AppManifestInput } from '@/lib/types/workflow';
import { MAX_APP_LABEL, parseDefault } from '@/lib/workflow-apps/app-inputs';
import {
  MAX_EXAMPLE, MAX_HELP, MAX_OPTION_LABEL, MAX_OPTIONS, NUMBER_UNITS, SHAPES, TEXT_CAPS, changeType, compatibleTypes, isTyped,
} from '@/lib/workflow-apps/app-input-builder';
import { isUnderMore } from './app-input-model';
import { AllowedModelsEditor } from './AllowedModelsEditor';

interface AppInputEditorProps {
  input: AppManifestInput;
  /** The whole draft, for "Show only when" (the other switches and choices). */
  manifest: AppManifest;
  onChange: (next: AppManifestInput) => void;
  /** A readable name for aria labels. */
  name: string;
  projectId: string;
  workflowId: string;
  /** Whether this input is in the stored manifest (the model list is asked of the server by its id). */
  saved: boolean;
}

const num = (v: string | number) => (typeof v === 'number' && Number.isFinite(v) ? v : null);

/**
 * One field of the app page, edited (docs/design/app-inputs.md 13.1, 24): the words people see, whether it must be
 * filled, where it sits, its starting value, and what its type needs (options, range, shapes, models). Plain controls,
 * only the ones the field's type uses.
 */
export function AppInputEditor({ input, manifest, onChange, name, projectId, workflowId, saved }: AppInputEditorProps) {
  const t = useT('apps.builder.edit');
  const set = (patch: Partial<AppManifestInput>) => onChange({ ...input, ...patch });
  const typed = isTyped(input) ? input : null;
  const text = typed?.type === 'shortText' || typed?.type === 'longText';

  // "Show only when": another switch or choice that is not itself shown only sometimes (one level).
  const controllers = (manifest.inputs ?? []).filter(
    (i) => isTyped(i) && i.id !== input.id && (i.type === 'toggle' || i.type === 'choice') && !i.showWhen,
  );
  const controller = controllers.find((c) => c.id === input.showWhen?.input) ?? null;
  // docs/design/app-inputs.md 29: a field's type changes in place only to one with the same kind of value (its id, and every
  // setting that uses it, stay); any other change is a new field.
  const types = typed ? compatibleTypes(typed.type) : [];
  const tt = useT('apps.builder');

  return (
    <Stack gap="xs">
      {typed && (types.length > 1 ? (
        <Select
          label={t('type')}
          description={t('typeHint')}
          size="sm"
          data={types.map((type) => ({ value: type, label: tt(`types.${type}`) }))}
          value={typed.type ?? null}
          onChange={(v) => { if (v) onChange(changeType(input, v as typeof types[number])); }}
          allowDeselect={false}
          comboboxProps={{ withinPortal: true }}
          data-testid="app-input-type"
        />
      ) : (
        <Text size="xs" c="dimmed" data-testid="app-input-type-fixed">{t('typeFixed', { type: tt(`types.${typed.type}`) })}</Text>
      ))}
      <TextInput
        label={t('label')}
        size="sm"
        maxLength={MAX_APP_LABEL}
        value={input.label ?? ''}
        onChange={(e) => set({ label: e.currentTarget.value })}
        aria-label={t('labelFor', { name })}
      />
      {typed && (
        <TextInput
          label={t('help')}
          description={t('helpHint')}
          size="sm"
          maxLength={MAX_HELP}
          value={input.help ?? ''}
          onChange={(e) => set({ help: e.currentTarget.value })}
        />
      )}
      {text && (
        <TextInput
          label={t('example')}
          description={t('exampleHint')}
          size="sm"
          maxLength={MAX_EXAMPLE}
          value={input.example ?? ''}
          onChange={(e) => set({ example: e.currentTarget.value })}
        />
      )}

      <Group gap="lg" wrap="wrap">
        {typed && typed.type !== 'toggle' && (
          <Switch
            label={t('required')}
            checked={input.required === true}
            onChange={(e) => set({ required: e.currentTarget.checked })}
            styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }}
          />
        )}
        <Switch
          label={t('more')}
          checked={isUnderMore(input)}
          onChange={(e) => set({ more: e.currentTarget.checked })}
          styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }}
        />
      </Group>

      {/* The starting value, in the field's own kind of control. */}
      {input.kind === 'brief' && input.field !== 'avatar' && (
        <TextInput
          label={t('startsAs')}
          size="sm"
          value={input.default == null ? '' : String(input.default)}
          onChange={(e) => set({ default: parseDefault(input.field, e.currentTarget.value) })}
        />
      )}
      {text && (
        <>
          <TextInput
            label={t('startsAs')}
            size="sm"
            maxLength={input.maxLength ?? TEXT_CAPS[typed!.type as 'shortText' | 'longText']}
            value={typeof input.default === 'string' ? input.default : ''}
            onChange={(e) => set({ default: e.currentTarget.value })}
          />
          <NumberInput
            label={t('maxLength')}
            size="sm"
            min={1}
            max={TEXT_CAPS[typed!.type as 'shortText' | 'longText']}
            value={input.maxLength ?? ''}
            onChange={(v) => set({ maxLength: num(v) })}
          />
        </>
      )}
      {(typed?.type === 'number' || typed?.type === 'duration') && (
        <>
          <Group grow align="flex-start">
            <NumberInput label={t('min')} size="sm" value={input.min ?? ''} onChange={(v) => set({ min: num(v) })} />
            <NumberInput label={t('max')} size="sm" value={input.max ?? ''} onChange={(v) => set({ max: num(v) })} />
          </Group>
          <NumberInput
            label={t('startsAs')}
            size="sm"
            value={typeof input.default === 'number' ? input.default : ''}
            onChange={(v) => set({ default: num(v) })}
          />
          {typed.type === 'number' ? (
            <Group grow align="flex-start">
              <NumberInput label={t('step')} size="sm" min={0} value={input.step ?? ''} onChange={(v) => set({ step: num(v) })} />
              <Select
                label={t('unit')}
                size="sm"
                value={input.unit ?? 'none'}
                onChange={(v) => set({ unit: v && v !== 'none' ? (v as AppManifestInput['unit']) : null })}
                data={[{ value: 'none', label: t('unitNone') }, ...NUMBER_UNITS.map((u) => ({ value: u, label: t(`units.${u}`) }))]}
                allowDeselect={false}
                comboboxProps={{ withinPortal: true }}
              />
            </Group>
          ) : (
            <MultiSelect
              label={t('lengths')}
              description={t('lengthsHint')}
              size="sm"
              data={[2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 30, 45, 60, 90, 120].map((s) => ({ value: String(s), label: t('seconds', { count: s }) }))}
              value={(input.choices ?? []).map(String)}
              onChange={(v) => set({ choices: v.map(Number).sort((a, b) => a - b) })}
              maxValues={MAX_OPTIONS}
              comboboxProps={{ withinPortal: true }}
            />
          )}
        </>
      )}
      {typed?.type === 'toggle' && (
        <>
          <Switch
            label={t('startsOn')}
            checked={input.default === true}
            onChange={(e) => set({ default: e.currentTarget.checked })}
            styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }}
          />
          <Group grow align="flex-start">
            <TextInput label={t('onLabel')} size="sm" maxLength={MAX_APP_LABEL} value={input.onLabel ?? ''} onChange={(e) => set({ onLabel: e.currentTarget.value })} />
            <TextInput label={t('offLabel')} size="sm" maxLength={MAX_APP_LABEL} value={input.offLabel ?? ''} onChange={(e) => set({ offLabel: e.currentTarget.value })} />
          </Group>
        </>
      )}
      {typed?.type === 'choice' && <OptionsEditor input={input} onChange={onChange} name={name} />}
      {typed?.type === 'shape' && (
        <>
          <Checkbox.Group label={t('shapes')} value={(input.choices ?? []).map(String)} onChange={(v) => set({ choices: SHAPES.filter((s) => v.includes(s)) })}>
            <Group gap="md" mt={4}>
              {SHAPES.map((s) => <Checkbox key={s} value={s} label={s} styles={{ body: { minHeight: 44, alignItems: 'center' } }} />)}
            </Group>
          </Checkbox.Group>
          <Select
            label={t('startsAs')}
            size="sm"
            data={((input.choices ?? []).length > 0 ? (input.choices ?? []).map(String) : [...SHAPES]).map((s) => ({ value: s, label: s }))}
            value={typeof input.default === 'string' ? input.default : null}
            onChange={(v) => set({ default: v })}
            placeholder={t('noDefault')}
            comboboxProps={{ withinPortal: true }}
          />
        </>
      )}
      {typed?.type === 'model' && (
        <AllowedModelsEditor input={input} onChange={onChange} projectId={projectId} workflowId={workflowId} saved={saved} />
      )}

      {typed && controllers.length > 0 && (
        <Group grow align="flex-start">
          <Select
            label={t('showWhen')}
            description={t('showWhenHint')}
            size="sm"
            data={[{ value: '', label: t('always') }, ...controllers.map((c) => ({ value: c.id!, label: c.label?.trim() || c.id! }))]}
            value={input.showWhen?.input ?? ''}
            onChange={(v) => {
              const next = controllers.find((c) => c.id === v);
              if (!next) return set({ showWhen: null });
              const equals = next.type === 'toggle' ? true : next.options?.[0]?.value ?? '';
              set({ showWhen: { input: next.id!, equals } });
            }}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
          />
          {controller && controller.type === 'toggle' && (
            <div>
              <Text size="sm" fw={500} mb={4}>{t('whenIs')}</Text>
              <SegmentedControl
                fullWidth
                value={input.showWhen?.equals === false ? 'off' : 'on'}
                onChange={(v) => set({ showWhen: { input: controller.id!, equals: v === 'on' } })}
                data={[{ value: 'on', label: t('on') }, { value: 'off', label: t('off') }]}
              />
            </div>
          )}
          {controller && controller.type === 'choice' && (
            <Select
              label={t('whenIs')}
              size="sm"
              data={(controller.options ?? []).map((o) => ({ value: String(o.value), label: o.label?.trim() || String(o.value) }))}
              value={input.showWhen ? String(input.showWhen.equals) : null}
              onChange={(v) => {
                const option = (controller.options ?? []).find((o) => String(o.value) === v);
                if (option) set({ showWhen: { input: controller.id!, equals: option.value } });
              }}
              allowDeselect={false}
              comboboxProps={{ withinPortal: true }}
            />
          )}
        </Group>
      )}
    </Stack>
  );
}

/** The options of a choice: what people see and the value the step gets (usually the same word). */
function OptionsEditor({ input, onChange, name }: { input: AppManifestInput; onChange: (next: AppManifestInput) => void; name: string }) {
  const t = useT('apps.builder.edit');
  const options = input.options ?? [];
  const setOptions = (next: AppInputOption[]) => onChange({ ...input, options: next });
  const setAt = (i: number, patch: Partial<AppInputOption>) => setOptions(options.map((o, k) => (k === i ? { ...o, ...patch } : o)));
  return (
    <Stack gap={6}>
      <Text size="sm" fw={500}>{t('options')}</Text>
      {options.map((o, i) => (
        <Group key={i} gap="xs" align="flex-end" wrap="nowrap">
          <TextInput
            label={i === 0 ? t('optionLabel') : undefined}
            aria-label={t('optionLabelFor', { n: i + 1, name })}
            size="sm"
            maxLength={MAX_OPTION_LABEL}
            value={o.label ?? ''}
            onChange={(e) => setAt(i, { label: e.currentTarget.value })}
            style={{ flex: '1 1 0', minWidth: 0 }}
          />
          <TextInput
            label={i === 0 ? t('optionValue') : undefined}
            aria-label={t('optionValueFor', { n: i + 1, name })}
            size="sm"
            value={String(o.value ?? '')}
            // A value that is a plain number is sent as one (a volume in dB), anything else as a word.
            onChange={(e) => {
              const raw = e.currentTarget.value;
              setAt(i, { value: /^-?\d+(\.\d+)?$/.test(raw.trim()) ? Number(raw.trim()) : raw });
            }}
            style={{ flex: '1 1 0', minWidth: 0 }}
          />
          <Tooltip label={t('removeOption')}>
            <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('removeOptionFor', { n: i + 1, name })} onClick={() => setOptions(options.filter((_, k) => k !== i))}>
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>
      ))}
      <Button
        size="sm"
        variant="default"
        leftSection={<IconPlus size={14} />}
        disabled={options.length >= MAX_OPTIONS}
        onClick={() => setOptions([...options, { value: `option${options.length + 1}`, label: t('optionN', { n: options.length + 1 }) }])}
        style={{ alignSelf: 'flex-start' }}
      >
        {t('addOption')}
      </Button>
      <Select
        label={t('startsAs')}
        size="sm"
        data={options.map((o) => ({ value: String(o.value), label: o.label?.trim() || String(o.value) }))}
        value={input.default === null || input.default === undefined ? null : String(input.default)}
        onChange={(v) => onChange({ ...input, default: options.find((o) => String(o.value) === v)?.value ?? null })}
        placeholder={t('noDefault')}
        comboboxProps={{ withinPortal: true }}
      />
    </Stack>
  );
}
