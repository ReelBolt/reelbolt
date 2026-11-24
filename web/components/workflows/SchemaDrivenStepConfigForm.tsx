'use client';

import React, { useMemo, useState, useCallback, useEffect } from 'react';
import { useT } from '@/i18n';
import {
  Stack, TextInput, NumberInput, Switch, Select, Fieldset, TagsInput, Collapse, Button, Group, Text, Box, Loader, Center,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { STEP_CONFIG_SCHEMAS } from '@/lib/schemas/workflow-step-configs';
import { validateStepConfig, applySchemaEditsForStepType } from '@/lib/schemas/schema-form';
import type { StepType } from '@/lib/types/workflow';
import { useFd, type WrapProps } from './config-kit';

/** Everything a custom field renderer needs; the form still owns state, validation and Apply. */
export interface FieldRenderContext {
  value: unknown;
  onChange: (next: unknown) => void;
  error: string | null | undefined;
  label: string;
  description?: string;
  wrapperProps?: WrapProps;
}

interface SchemaDrivenStepConfigFormProps {
  stepType: StepType;
  value: Record<string, unknown> | null;
  onChange: (next: Record<string, unknown>) => void;
  fields?: string[];
  /** Per-field replacement widgets (provider/step pickers) keyed by schema property name. */
  renderers?: Record<string, (ctx: FieldRenderContext) => React.ReactNode>;
  /** Friendlier labels keyed by schema property name (default: humanised key). */
  labels?: Record<string, string>;
  /** Extra hint lines keyed by schema property name, shown instead of the schema description. */
  hints?: Record<string, string>;
  /** Placeholder text (usually the effective default) for empty inputs. */
  placeholders?: Record<string, string>;
  /** Maps a schema key to its field-diff path; default `config.<key>`. */
  pathFor?: (key: string) => string;
}

interface FieldInfo {
  key: string;
  schema: Record<string, unknown>;
  isRequired: boolean;
  isAdvanced: boolean;
}

interface ErrorMap {
  [fieldPath: string]: string | null;
}

/**
 * Renders a dynamic form based on STEP_CONFIG_SCHEMAS[stepType].
 * - String/number/boolean/enum inputs by type
 * - Validates on blur
 * - Errors render adjacent to fields
 * - Preserves unknown properties via applySchemaEditsForStepType
 */
export function SchemaDrivenStepConfigForm({
  stepType,
  value,
  onChange,
  fields: allowedFields,
  renderers,
  labels,
  hints,
  placeholders,
  pathFor = (k) => `config.${k}`,
}: SchemaDrivenStepConfigFormProps): React.ReactElement {
  const t = useT('wfSteps.schemaForm');
  const schema = STEP_CONFIG_SCHEMAS[stepType];
  const [errors, setErrors] = useState<ErrorMap>({});
  const [fieldErrors, setFieldErrors] = useState<Set<string>>(new Set());
  const [advancedOpened, setAdvancedOpened] = useState(false);
  const fd = useFd();

  // Parse and order fields
  const fieldInfos = useMemo(() => {
    const schemaProps = (schema.properties as Record<string, unknown>) || {};
    const requiredFields = (schema.required as string[]) || [];

    const fields: FieldInfo[] = Object.entries(schemaProps).map(([key, fieldSchema]) => {
      const isRequired = requiredFields.includes(key);
      const fieldSchemaObj = fieldSchema as Record<string, unknown>;
      const isAdvanced = (fieldSchemaObj['x-advanced'] as boolean) || false;

      return {
        key,
        schema: fieldSchemaObj,
        isRequired,
        isAdvanced,
      };
    }).sort((a, b) => {
      // Sort: required (non-advanced) first, then optional (non-advanced), then advanced
      if (a.isAdvanced !== b.isAdvanced) {
        return a.isAdvanced ? 1 : -1;
      }
      if (a.isRequired !== b.isRequired) {
        return a.isRequired ? -1 : 1;
      }
      return a.key.localeCompare(b.key);
    });

    // Filter by allowedFields if provided
    // An explicit allowlist is also the display order: authors list fields in the order a person reads them.
    const filtered = allowedFields
      ? fields.filter((f) => allowedFields.includes(f.key)).sort((a, b) => allowedFields.indexOf(a.key) - allowedFields.indexOf(b.key))
      : fields;
    return filtered;
  }, [schema, allowedFields]);

  // Initialize form values
  const initialValues = useMemo(() => {
    const base: Record<string, unknown> = value && typeof value === 'object' && !Array.isArray(value)
      ? { ...value }
      : {};

    // Only add missing schema fields if they are required and nullable
    const schemaProps = (schema.properties as Record<string, unknown>) || {};
    const requiredFields = (schema.required as string[]) || [];
    Object.keys(schemaProps).forEach((key) => {
      if (!(key in base) && requiredFields.includes(key)) {
        const propSchema = schemaProps[key] as Record<string, unknown>;
        const types = propSchema.type;

        // Check if the schema type includes 'null'
        const isNullable = types === 'null' || (Array.isArray(types) && types.includes('null'));

        // Only default to null if the schema explicitly allows it and field is required
        // Otherwise leave the key absent (undefined) for form rendering
        if (isNullable) {
          base[key] = null;
        }
      }
    });

    return base;
  }, [value, schema]);

  const form = useForm({
    initialValues,
  });

  // Sync form values when initialValues changes
  useEffect(() => {
    form.setValues(initialValues);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialValues]);

  // Validate a single field and return error message if any
  const validateField = useCallback((fieldKey: string, fieldValue: unknown): string | null => {
    // Create a test object with only this field
    const testObj = { ...initialValues, [fieldKey]: fieldValue };
    const result = validateStepConfig(stepType, testObj);

    // Find error for this field (check JSON-Pointer path matching)
    const fieldError = result.errors.find((err) => {
      // Match paths like "/" (root), "/fieldKey", or nested paths starting with "/fieldKey/"
      const path = err.path === '/' ? '' : err.path;
      return path === `/${fieldKey}` || path.startsWith(`/${fieldKey}/`);
    });

    return fieldError?.message || null;
  }, [initialValues, stepType]);

  // Handle blur: validate field and update errors
  const handleBlur = useCallback((fieldKey: string) => {
    const fieldValue = form.values[fieldKey];
    const error = validateField(fieldKey, fieldValue);

    setErrors((prev) => ({
      ...prev,
      [fieldKey]: error,
    }));

    if (error) {
      setFieldErrors((prev) => new Set(prev).add(fieldKey));
    }
  }, [form.values, validateField]);

  // Handle change: if field was in error state, re-validate on change to clear error early
  const handleFieldChange = useCallback((fieldKey: string, fieldValue: unknown) => {
    form.setFieldValue(fieldKey, fieldValue);

    // If this field had an error, check if change would clear it
    if (fieldErrors.has(fieldKey)) {
      const error = validateField(fieldKey, fieldValue);
      setErrors((prev) => ({
        ...prev,
        [fieldKey]: error,
      }));

      if (!error) {
        setFieldErrors((prev) => {
          const next = new Set(prev);
          next.delete(fieldKey);
          return next;
        });
      }
    }
  }, [form, fieldErrors, validateField]);

  // Handle form submission: apply edits via applySchemaEditsForStepType
  const handleSubmit = useCallback(() => {
    // Only include form values that are actually set (not undefined)
    // This prevents undefined values from the form from overwriting existing value properties
    const formValuesPruned: Record<string, unknown> = {};
    Object.entries(form.values).forEach(([key, val]) => {
      if (val !== undefined) {
        formValuesPruned[key] = val;
      }
    });

    // Merge form.values with original value to preserve fields that can't be edited (e.g., fields with $ref)
    const mergedValues = { ...value, ...formValuesPruned };

    // Validate all fields first
    const result = validateStepConfig(stepType, mergedValues);

    if (!result.valid) {
      // Build error map
      const newErrors: ErrorMap = {};
      result.errors.forEach((err) => {
        // Extract field key from JSON-Pointer path
        const parts = err.path === '/' ? [] : err.path.split('/').filter(Boolean);
        if (parts.length > 0) {
          const fieldKey = parts[0];
          newErrors[fieldKey] = err.message;
        }
      });
      setErrors(newErrors);
      setFieldErrors(new Set(Object.keys(newErrors)));
      return;
    }

    // Apply edits, preserving unknown fields
    const nextValue = applySchemaEditsForStepType(stepType, value, formValuesPruned);
    onChange(nextValue);
  }, [stepType, form.values, value, onChange]);

  // Render a single field based on its schema
  const renderField = (fieldInfo: FieldInfo): React.ReactElement => {
    const { key, schema: fieldSchema } = fieldInfo;
    const fieldValue = form.values[key];
    const fieldError = errors[key];
    const fieldType = fieldSchema.type as string | string[];
    const fieldEnum = fieldSchema.enum as unknown[] | undefined;
    const fieldRef = fieldSchema.$ref as string | undefined;

    // Handle $ref (nested object)
    if (fieldRef) {
      return (
        <Fieldset key={key} legend={key}>
          <Text size="sm" c="gray">
            {t('nestedRef', { ref: fieldRef })}
          </Text>
          {fieldError && <Text size="sm" c="red">{fieldError}</Text>}
        </Fieldset>
      );
    }

    const humanised = key.replace(/([A-Z])/g, ' $1').trim();
    const label = labels?.[key] ?? humanised.charAt(0).toUpperCase() + humanised.slice(1).toLowerCase();
    const isRequired = fieldInfo.isRequired ? ' *' : '';
    const description = hints?.[key] ?? (fieldSchema.description as string | undefined);
    const wrap = fd(pathFor(key));
    const custom = renderers?.[key];
    if (custom) {
      return (
        <React.Fragment key={key}>
          {custom({
            value: fieldValue,
            onChange: (next) => handleFieldChange(key, next),
            error: fieldError,
            label: label + isRequired,
            description,
            wrapperProps: wrap.wrapperProps,
          })}
        </React.Fragment>
      );
    }

    // Handle enum (enum fields use Select)
    if (fieldEnum) {
      const options = fieldEnum.map((val) => ({
        value: String(val),
        label: String(val),
      }));

      return (
        <Select
          key={key}
          label={label + isRequired}
          description={description}
          {...wrap}
          placeholder={t('select')}
          data={options}
          value={fieldValue ? String(fieldValue) : null}
          onChange={(val) => handleFieldChange(key, val ? (fieldEnum[options.findIndex((o) => o.value === val)] ?? null) : null)}
          onBlur={() => handleBlur(key)}
          error={fieldError}
        />
      );
    }

    // Handle type-based fields
    const baseType = Array.isArray(fieldType) ? fieldType[0] : fieldType;

    switch (baseType) {
      case 'string':
        return (
          <TextInput
            key={key}
            label={label + isRequired}
            description={description}
            {...wrap}
            placeholder=""
            value={fieldValue ? String(fieldValue) : ''}
            onChange={(e) => handleFieldChange(key, e.currentTarget.value)}
            onBlur={() => handleBlur(key)}
            error={fieldError}
          />
        );

      case 'integer':
      case 'number':
        return (
          <NumberInput
            key={key}
            label={label + isRequired}
            description={description}
            {...wrap}
            placeholder={placeholders?.[key] ?? (fieldSchema.default !== undefined && fieldSchema.default !== null ? t('defaultValue', { value: String(fieldSchema.default) }) : '')}
            value={fieldValue !== null && fieldValue !== undefined ? Number(fieldValue) : undefined}
            onChange={(val) => handleFieldChange(key, val ?? null)}
            onBlur={() => handleBlur(key)}
            error={fieldError}
          />
        );

      case 'boolean':
        return (
          <Switch
            key={key}
            label={label + isRequired}
            description={description}
            {...wrap}
            checked={fieldValue === true}
            onChange={(e) => handleFieldChange(key, e.currentTarget.checked)}
            onBlur={() => handleBlur(key)}
            error={fieldError}
          />
        );

      case 'array':
        // Handle array of primitives as TagsInput
        const itemType = (fieldSchema.items as Record<string, unknown>)?.type;
        if (itemType === 'string' || !itemType) {
          return (
            <TagsInput
              key={key}
              label={label + isRequired}
              description={description}
              {...wrap}
              placeholder={t('addItems')}
              value={Array.isArray(fieldValue) ? fieldValue.map(String) : []}
              onChange={(val) => handleFieldChange(key, val.length > 0 ? val : null)}
              onBlur={() => handleBlur(key)}
              error={fieldError}
            />
          );
        }
        // Fallback for non-string arrays
        return (
          <TextInput
            key={key}
            label={label + isRequired}
            placeholder={t('arrayUnsupported')}
            disabled
            error={fieldError}
          />
        );

      case 'object':
        // Nested objects render as Fieldset
        return (
          <Fieldset key={key} legend={label + isRequired}>
            <Text size="sm" c="gray">
              {t('nestedObject')}
            </Text>
            {fieldError && <Text size="sm" c="red">{fieldError}</Text>}
          </Fieldset>
        );

      default:
        return (
          <TextInput
            key={key}
            label={label + isRequired}
            placeholder={t('unknownType', { type: baseType })}
            disabled
            error={fieldError}
          />
        );
    }
  };

  const dirty = useMemo(
    () => JSON.stringify(Object.fromEntries(Object.entries(form.values).filter(([, v]) => v !== undefined)))
      !== JSON.stringify(Object.fromEntries(Object.entries(initialValues).filter(([, v]) => v !== undefined))),
    [form.values, initialValues],
  );

  // Separate basic and advanced fields
  const basicFields = fieldInfos.filter((f) => !f.isAdvanced);
  const advancedFields = fieldInfos.filter((f) => f.isAdvanced);

  return (
    <Stack gap="md">
      {/* Basic fields */}
      {basicFields.map((fieldInfo) => renderField(fieldInfo))}

      {/* Advanced fields in a collapsed section */}
      {advancedFields.length > 0 && (
        <>
          <Button
            variant="subtle"
            onClick={() => setAdvancedOpened((o) => !o)}
          >
            {t('advancedOptions')}
          </Button>
          <Collapse expanded={advancedOpened}>
            <Stack gap="md">
              {advancedFields.map((fieldInfo) => renderField(fieldInfo))}
            </Stack>
          </Collapse>
        </>
      )}

      {/* Submit button */}
      <Group justify="flex-end" mt="md" align="center">
        {dirty && (
          <Text size="xs" c="orange" fw={600}>
            {t('unapplied')}
          </Text>
        )}
        <Button onClick={handleSubmit} variant="filled">
          {t('apply')}
        </Button>
      </Group>
    </Stack>
  );
}
