/**
 * Schema validation and edit-application utilities for workflow step configs.
 * Wraps ajv for validation with JSON-Pointer error paths, and provides a helper
 * for applying edits while preserving unknown fields.
 */

import Ajv from 'ajv';
import { STEP_CONFIG_SCHEMAS } from './workflow-step-configs';
import type { StepType } from '@/lib/types/workflow';

/** JSON Schema type (draft-07) for step configs. */
type JSONSchema7 = Record<string, unknown>;

/**
 * Validation result from validateStepConfig.
 */
export interface ValidationResult {
  valid: boolean;
  errors: Array<{
    path: string;
    message: string;
  }>;
}

/**
 * Singleton AJV instance for all step config validation.
 * Configured for draft-07 by default.
 */
const AJV = new Ajv({
  allErrors: true,
  coerceTypes: false,
});

/**
 * Validates a step config value against its StepType's schema.
 * Returns validation result with errors represented as JSON-Pointer paths and messages.
 *
 * @param stepType - The StepType (determines which schema to use)
 * @param value - The step config value to validate
 * @returns Validation result: { valid, errors }
 */
export function validateStepConfig(stepType: StepType, value: unknown): ValidationResult {
  const schema = STEP_CONFIG_SCHEMAS[stepType];
  const validate = AJV.compile(schema);
  const valid = validate(value);

  if (valid) {
    return { valid: true, errors: [] };
  }

  const errors = (validate.errors ?? []).map((err) => {
    // err.instancePath is the JSON-Pointer path (e.g., "/inputs/foo" or "")
    const path = err.instancePath || '/';
    const message = err.message ?? 'Validation error';
    return { path, message };
  });

  return { valid: false, errors };
}

/**
 * Applies a set of field edits to a raw object, respecting schema declarations.
 * Only overwrites fields that are declared in the schema; all other properties of raw are preserved untouched.
 * If raw is null or undefined, defaults to an empty object {}.
 *
 * @param raw - The existing config object (or null/undefined)
 * @param schema - The schema whose properties define which fields can be edited
 * @param edits - Object with the field values to apply
 * @returns New object with edits applied, unknown fields preserved
 */
export function applySchemaEdits(
  raw: Record<string, unknown> | null | undefined,
  schema: JSONSchema7,
  edits: Record<string, unknown>,
): Record<string, unknown> {
  // Default raw to {} if null or undefined
  const base = raw && typeof raw === 'object' && !Array.isArray(raw)
    ? { ...raw }
    : {};

  // Extract the schema's declared properties
  const schemaProperties = (schema.properties as Record<string, unknown>) || {};

  // Overwrite only schema-declared keys
  for (const key of Object.keys(schemaProperties)) {
    if (key in edits) {
      base[key] = edits[key];
    }
  }

  return base;
}

/**
 * Applies schema edits using the schema from STEP_CONFIG_SCHEMAS for a given StepType.
 * Convenience wrapper around applySchemaEdits that looks up the schema automatically.
 *
 * @param stepType - The StepType (determines which schema to use)
 * @param raw - The existing config object (or null/undefined)
 * @param edits - Object with the field values to apply
 * @returns New object with edits applied, unknown fields preserved
 */
export function applySchemaEditsForStepType(
  stepType: StepType,
  raw: Record<string, unknown> | null | undefined,
  edits: Record<string, unknown>,
): Record<string, unknown> {
  const schema = STEP_CONFIG_SCHEMAS[stepType];
  return applySchemaEdits(raw, schema, edits);
}
