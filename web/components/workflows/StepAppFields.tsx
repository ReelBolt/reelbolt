'use client';

import { useRef, useState } from 'react';
import { Badge, Button, Fieldset, Group, Menu, NativeSelect, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { useT } from '@/i18n';
import { useAppBuilder } from '@/components/workflow-apps/AppBuilderContext';
import type { AppBindableField, AppManifestInput } from '@/lib/types/workflow';
import { briefFieldLabel, inputName } from '@/lib/workflow-apps/app-inputs';
import {
  appInputsOf, bindValue, canMap, compatibleInputs, concretePaths, configOf, entryValues, getPath, inputForSetting, insertAt,
  isModelPath, isTyped, itemIndexOf, mappableKeys, modelCapabilityFor, newInputId, placeholder, placeholderIds, removeInput,
  setPath, settingValue, settleVersion, unbindValue, withConfig,
} from '@/lib/workflow-apps/app-input-builder';
import type { StepData } from './WorkflowStepList';

/**
 * A setting's name in the reader's language: the two app-only settings ("Words to speak", "Picture model") have catalogue
 * keys; every other setting keeps the step card's own label.
 */
function settingLabel(t: (key: string) => string, field: AppBindableField): string {
  if (field.path === '@spokenText') return t('settingLabel.spokenText');
  if (field.path === '@stillModel') return t('settingLabel.stillModel');
  if (field.path === '@visionModel') return t('settingLabel.visionModel');
  return field.label;
}

interface StepAppFieldsProps {
  step: StepData;
  onChange: (updates: Partial<StepData>) => void;
}

/**
 * "App fields" in a step's settings (docs/design/app-inputs.md 13.1, 24): each setting of this step that the app page may
 * fill, with "Use a field for this setting" (an existing field or a new one made from the setting), what fills it now,
 * and "Stop using the field". A text setting can mix fixed words and fields ("Insert a field"). Only while app inputs are
 * on (the page provides the draft); otherwise it renders nothing and the drawer is as before.
 */
export function StepAppFields({ step, onChange }: StepAppFieldsProps) {
  const t = useT('apps.builder.step');
  const root = useT();
  const builder = useAppBuilder();
  const [notice, setNotice] = useState<string | null>(null);
  if (!builder) return null;
  const { manifest, setManifest, bindableFields } = builder;
  const fields = bindableFields[step.stepType] ?? [];
  const agentRow = step.stepType === 'Agent' && Boolean(step.agentDefinitionId);
  if (fields.length === 0 && !agentRow) return null;

  const config = configOf(step);
  const inputs = manifest.inputs ?? [];
  const byId = (id: string) => inputs.find((i) => i.kind === 'input' && i.id === id) ?? null;
  const shownBrief = new Set(inputs.filter((i) => i.kind === 'brief' && i.field).map((i) => i.field!));

  const addInput = (input: AppManifestInput) => {
    setManifest(settleVersion({ ...manifest, inputs: [...inputs, input] }));
    setNotice(t('added', { name: input.label ?? input.id ?? '' }));
  };

  // A setting inside a list (regionEffects[].effect, trackObjects[].label) is one row per item the step has.
  const rows = fields.flatMap((field) => concretePaths(config, field.path).map((concrete) => ({ field, concrete })));

  return (
    <Fieldset legend={t('legend')} data-testid="step-app-fields">
      <Stack gap="sm">
        <Text size="xs" c="dimmed">{t('intro')}</Text>
        {notice && <Text size="sm" role="status">{notice}</Text>}
        {rows.map(({ field, concrete }) => {
          const n = itemIndexOf(concrete);
          const own = settingLabel(t, field);
          const label = n === null ? own : t('itemLabel', { label: own, n: n + 1 });
          if (field.governedByBriefField && shownBrief.has(field.governedByBriefField)) {
            return (
              <SettingRow key={concrete} label={label}>
                <Text size="sm" c="dimmed">{t('governed', { field: briefFieldLabel(root, field.governedByBriefField) })}</Text>
              </SettingRow>
            );
          }
          return field.mode === 'value' ? (
            <ValueRow
              key={concrete}
              label={label}
              field={field}
              bound={byId(appInputsOf(config)[concrete] ?? '')}
              values={entryValues(config, concrete)}
              choices={compatibleInputs(manifest, field, isModelPath(field) ? modelCapabilityFor(step, field) : null)}
              onUse={(input) => onChange(withConfig(step, bindValue(config, concrete, input.id!, firstValues(field, input, config, concrete))))}
              onNew={() => {
                const input = inputForSetting(step, field, concrete, manifest, label);
                addInput(input);
                onChange(withConfig(step, bindValue(config, concrete, input.id!)));
              }}
              onValues={(input, values) => onChange(withConfig(step, bindValue(config, concrete, input.id!, values)))}
              onStop={() => onChange(withConfig(step, unbindValue(config, concrete)))}
            />
          ) : (
            <TextRow
              key={concrete}
              label={label}
              text={typeof getPath(config, concrete) === 'string' ? (getPath(config, concrete) as string) : ''}
              fields={inputs.filter((i) => isTyped(i) && field.types.includes(i.type))}
              byId={byId}
              onText={(text) => onChange(withConfig(step, setPath(config, concrete, text)))}
              onNew={() => {
                const input = inputForSetting(step, field, concrete, manifest, label);
                addInput(input);
                onChange(withConfig(step, setPath(config, concrete, placeholder(input.id!))));
              }}
            />
          );
        })}
        {agentRow && (
          <AgentModelRow
            input={inputs.find((i) => i.kind === 'input' && i.type === 'model' && i.agent === step.agentDefinitionId) ?? null}
            onNew={() => {
              const label = t('agentModelLabel');
              addInput({ kind: 'input', id: newInputId(label, manifest), type: 'model', label, capability: 'Chat', agent: step.agentDefinitionId });
            }}
            onStop={(id) => {
              const result = removeInput(manifest, builder.steps, id);
              if (result.ok) setManifest(result.manifest);
            }}
          />
        )}
      </Stack>
    </Fieldset>
  );

}

/**
 * A text setting: unbound, "Use a field for this setting" replaces it with one field (the old words become the field's
 * example); once it holds a field, the text is edited here with "Insert a field" at the cursor.
 */
function TextRow(props: {
  label: string;
  text: string;
  fields: AppManifestInput[];
  byId: (id: string) => AppManifestInput | null;
  onText: (text: string) => void;
  onNew: () => void;
}) {
  const t = useT('apps.builder.step');
  const root = useT();
  const ref = useRef<HTMLTextAreaElement>(null);
  const ids = placeholderIds(props.text);
  const insert = (id: string) => {
    const { text, caret } = insertAt(props.text, ref.current?.selectionStart, placeholder(id));
    props.onText(text);
    requestAnimationFrame(() => {
      ref.current?.focus();
      ref.current?.setSelectionRange(caret, caret);
    });
  };
  const stop = () => {
    let text = props.text;
    for (const id of ids) {
      const input = props.byId(id);
      const fill = typeof input?.default === 'string' ? input.default : input?.example ?? '';
      text = text.split(placeholder(id)).join(fill);
    }
    props.onText(text);
  };
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500}>{props.label}</Text>
      {ids.length === 0 ? (
        <Menu position="bottom-start" withinPortal>
          <Menu.Target>
            <Button size="sm" variant="default" style={{ alignSelf: 'flex-start' }}>{t('use')}</Button>
          </Menu.Target>
          <Menu.Dropdown>
            {props.fields.map((i) => (
              <Menu.Item key={i.id!} onClick={() => props.onText(placeholder(i.id!))}>{t('useExisting', { name: inputName(i, root) })}</Menu.Item>
            ))}
            <Menu.Item onClick={props.onNew}>{t('useNew')}</Menu.Item>
          </Menu.Dropdown>
        </Menu>
      ) : (
        <>
          <Group gap={6} wrap="wrap">
            {ids.map((id) => (
              <Badge key={id} variant="light" size="lg" style={{ textTransform: 'none', maxWidth: '100%' }}>
                {t('fromApp', { name: props.byId(id) ? inputName(props.byId(id)!, root) : id })}
              </Badge>
            ))}
          </Group>
          <Textarea
            ref={ref}
            aria-label={t('textWithFields', { label: props.label })}
            description={t('textWithFieldsHint')}
            size="sm"
            autosize
            minRows={2}
            maxRows={8}
            value={props.text}
            onChange={(e) => props.onText(e.currentTarget.value)}
          />
          <Group gap="xs" wrap="wrap">
            {props.fields.length > 0 && (
              <Menu position="bottom-start" withinPortal>
                <Menu.Target>
                  <Button size="sm" variant="subtle">{t('insert')}</Button>
                </Menu.Target>
                <Menu.Dropdown>
                  {props.fields.map((i) => (
                    <Menu.Item key={i.id!} onClick={() => insert(i.id!)}>{inputName(i, root)}</Menu.Item>
                  ))}
                </Menu.Dropdown>
              </Menu>
            )}
            <Button size="sm" variant="subtle" color="gray" onClick={stop}>{t('stopText')}</Button>
          </Group>
        </>
      )}
    </Stack>
  );
}

function SettingRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500}>{label}</Text>
      {children}
    </Stack>
  );
}

function UseMenu({ choices, onUse, onNew }: { choices: AppManifestInput[]; onUse: (i: AppManifestInput) => void; onNew: () => void }) {
  const t = useT('apps.builder.step');
  const root = useT();
  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <Button size="sm" variant="default" style={{ alignSelf: 'flex-start' }}>{t('use')}</Button>
      </Menu.Target>
      <Menu.Dropdown>
        {choices.map((i) => (
          <Menu.Item key={i.id!} onClick={() => onUse(i)}>{t('useExisting', { name: inputName(i, root) })}</Menu.Item>
        ))}
        <Menu.Item onClick={onNew}>{t('useNew')}</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

/**
 * The values a switch or a choice starts with when it is chosen for a setting of another type: each answer writes the step's
 * current value (so the run is unchanged until the author edits them), the switch's "off" writes nothing (the step's own
 * value stays). Null when the input's own value fits the setting.
 */
function firstValues(field: AppBindableField, input: AppManifestInput, config: Record<string, unknown> | null, concrete: string) {
  if (!canMap(field) || field.types.includes(input.type!)) return null;
  const current = getPath(config, concrete);
  if (typeof current !== 'string' && typeof current !== 'number' && typeof current !== 'boolean') return null;
  const values: Record<string, string | number | boolean> = {};
  for (const { key } of mappableKeys(input)) if (key !== 'false') values[key] = current;
  return values;
}

function ValueRow(props: {
  label: string;
  field: AppBindableField;
  bound: AppManifestInput | null;
  values: Record<string, string | number | boolean> | null;
  choices: AppManifestInput[];
  onUse: (i: AppManifestInput) => void;
  onNew: () => void;
  onValues: (i: AppManifestInput, values: Record<string, string | number | boolean> | null) => void;
  onStop: () => void;
}) {
  const t = useT('apps.builder.step');
  const root = useT();
  const mappable = props.bound && canMap(props.field) && (props.bound.type === 'toggle' || props.bound.type === 'choice');
  return (
    <SettingRow label={props.label}>
      {props.bound ? (
        <Stack gap={6}>
          <Group gap="xs" wrap="wrap">
            <Badge variant="light" size="lg" style={{ textTransform: 'none', maxWidth: '100%' }}>
              {t('fromApp', { name: inputName(props.bound, root) })}
            </Badge>
            <Button size="sm" variant="subtle" color="gray" onClick={props.onStop}>{t('stop')}</Button>
          </Group>
          {mappable && (
            <AnswerValues
              field={props.field}
              input={props.bound!}
              values={props.values}
              onValues={(values) => props.onValues(props.bound!, values)}
            />
          )}
        </Stack>
      ) : (
        <UseMenu choices={props.choices} onUse={props.onUse} onNew={props.onNew} />
      )}
    </SettingRow>
  );
}

/**
 * "What each answer sets" (docs/design/app-inputs.md 27): for a switch, its on and off; for a choice, each option. Each
 * answer writes the value chosen here, or leaves the step's own value ("Keep the step's own"). With nothing listed the
 * field's own value is written, which only fits a setting of the same type.
 */
function AnswerValues(props: {
  field: AppBindableField;
  input: AppManifestInput;
  values: Record<string, string | number | boolean> | null;
  onValues: (values: Record<string, string | number | boolean> | null) => void;
}) {
  const t = useT('apps.builder.step');
  const root = useT();
  const kind = props.field.valueKind ?? 'text';
  const set = (key: string, text: string) => {
    const next = { ...(props.values ?? {}) };
    const value = settingValue(props.field, text);
    if (value === null) delete next[key];
    else next[key] = value;
    props.onValues(Object.keys(next).length > 0 ? next : null);
  };
  const answerName = (key: string, value: unknown) => {
    if (props.input.type === 'toggle') return key === 'true' ? t('answerOn') : t('answerOff');
    const option = (props.input.options ?? []).find((o) => String(o.value) === key);
    return option?.label ?? String(value);
  };
  return (
    <Stack gap={4} data-testid="answer-values">
      <Text size="xs" c="dimmed">{t('answersIntro')}</Text>
      {mappableKeys(props.input).map(({ key, value }) => {
        const label = t('answerSets', { answer: answerName(key, value), name: inputName(props.input, root) });
        const current = props.values?.[key];
        const text = current === undefined ? '' : String(current);
        return kind === 'number' ? (
          <TextInput
            key={key}
            size="sm"
            type="number"
            label={label}
            placeholder={t('keepOwn')}
            value={text}
            min={props.field.min ?? undefined}
            max={props.field.max ?? undefined}
            onChange={(e) => set(key, e.currentTarget.value)}
          />
        ) : (
          <NativeSelect
            key={key}
            size="sm"
            label={label}
            value={text}
            onChange={(e) => set(key, e.currentTarget.value)}
            data={[
              { value: '', label: t('keepOwn') },
              ...(kind === 'boolean'
                ? [{ value: 'true', label: t('valueOn') }, { value: 'false', label: t('valueOff') }]
                : (props.field.choices ?? []).map((c) => ({ value: c, label: c }))),
            ]}
          />
        );
      })}
    </Stack>
  );
}

function AgentModelRow({ input, onNew, onStop }: { input: AppManifestInput | null; onNew: () => void; onStop: (id: string) => void }) {
  const t = useT('apps.builder.step');
  const root = useT();
  return (
    <SettingRow label={t('agentModel')}>
      {input ? (
        <Group gap="xs" wrap="wrap">
          <Badge variant="light" size="lg" style={{ textTransform: 'none', maxWidth: '100%' }}>
            {t('fromApp', { name: inputName(input, root) })}
          </Badge>
          <Button size="sm" variant="subtle" color="gray" onClick={() => onStop(input.id!)}>{t('stopAgent')}</Button>
        </Group>
      ) : (
        <Button size="sm" variant="default" onClick={onNew} style={{ alignSelf: 'flex-start' }}>{t('letPick')}</Button>
      )}
    </SettingRow>
  );
}
