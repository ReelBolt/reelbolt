import { describe, expect, it } from 'vitest';
import {
  DAY_MS,
  RESTORE_REASON_CODES,
  causeMessage,
  errorReasonCode,
  expiryDescriptor,
  groupChangesByKind,
  reasonMessage,
  saveErrorMessage,
} from './labels';

describe('history labels', () => {
  it('maps every cause to its own plain label', () => {
    expect(causeMessage('named').$msg).toBe('projectHistory.cause.named');
    expect(causeMessage('beforeAssistant').$msg).toBe('projectHistory.cause.beforeAssistant');
    expect(causeMessage('idle').$msg).toBe('projectHistory.cause.idle');
    expect(causeMessage('mystery').$msg).toBe('projectHistory.cause.auto');
  });

  it('says kept for an unnamed expiry and counts whole days otherwise', () => {
    const now = Date.parse('2026-01-01T00:00:00Z');
    expect(expiryDescriptor(null, now)).toEqual({ kind: 'kept' });
    expect(expiryDescriptor(new Date(now + 2.2 * DAY_MS).toISOString(), now)).toEqual({ kind: 'days', days: 3 });
    expect(expiryDescriptor(new Date(now - 1).toISOString(), now)).toEqual({ kind: 'expired' });
  });

  it('has a sentence for every restore code and a generic fallback', () => {
    // The exact list of Services/History/ProjectHistoryQueries.cs (reasons + notices).
    expect([...RESTORE_REASON_CODES]).toEqual([
      'CONTENT_GONE',
      'ERASED',
      'QUARANTINED',
      'QUARANTINE_MARKER',
      'OUTPUT_HELD',
      'DESIGN_VERSION_GONE',
      'UNVERIFIED',
      'NOT_YET_SUPPORTED',
      'PATH_TAKEN',
      'STORAGE_QUOTA_EXCEEDED',
      'ITEM_NOT_FOUND',
      'ALREADY_CURRENT',
      'DESIGN_VERSION_INVALID',
      'WORKFLOW_INVALID',
      'WORKFLOW_RUN_ACTIVE',
      'TIMELINE_INVALID',
      'EGRESS_CONSENT_RESET',
      'NOTHING_TO_RESTORE',
      'RESTORE_FAILED',
      'PRONUNCIATION_ENTRY_SKIPPED',
      'FILE_IN_USE_BY_AVATAR',
    ]);
    for (const code of RESTORE_REASON_CODES) {
      expect(reasonMessage(code).$msg, code).not.toBe('projectHistory.reason.generic');
    }
    for (const code of ['PROJECT_TOO_LARGE_TO_VERSION', 'NAMED_VERSION_LIMIT', 'PROJECT_RUN_ACTIVE', 'RESTORE_IN_PROGRESS']) {
      expect(reasonMessage(code).$msg, code).not.toBe('projectHistory.reason.generic');
    }
    expect(reasonMessage('NOPE').$msg).toBe('projectHistory.reason.generic');
  });

  it('has a plain sentence for every request-validation code, including the name rule', () => {
    for (const code of ['INVALID_NAME', 'INVALID_SCOPE', 'INVALID_ITEMS', 'INVALID_ITEM_KIND', 'INVALID_TARGET']) {
      expect(reasonMessage(code).$msg, code).not.toBe('projectHistory.reason.generic');
    }
    expect(reasonMessage('INVALID_NAME').$msg).toBe('projectHistory.reason.invalidName');
    expect(reasonMessage('OUTPUT_HELD').$msg).toBe('projectHistory.reason.outputHeld');
  });

  it('uses a save-specific fallback, never "cannot be restored"', () => {
    expect(saveErrorMessage('INVALID_NAME').$msg).toBe('projectHistory.reason.invalidName');
    expect(saveErrorMessage('NAMED_VERSION_LIMIT').$msg).toBe('projectHistory.reason.namedVersionLimit');
    expect(saveErrorMessage(null).$msg).toBe('projectHistory.saveFailed');
    expect(saveErrorMessage('SOME_FUTURE_CODE').$msg).toBe('projectHistory.saveFailed');
  });

  it('derives a code from 413 and 402 statuses', () => {
    expect(errorReasonCode({ status: 413, code: null })).toBe('PROJECT_TOO_LARGE_TO_VERSION');
    expect(errorReasonCode({ status: 402, code: null })).toBe('STORAGE_QUOTA_EXCEEDED');
    expect(errorReasonCode({ status: 409, code: 'RESTORE_IN_PROGRESS' })).toBe('RESTORE_IN_PROGRESS');
  });

  it('groups changes in reading order', () => {
    const groups = groupChangesByKind([
      { kind: 'File', key: 'a', name: 'a', change: 'added' },
      { kind: 'Project', key: 'p', name: 'p', change: 'changed' },
    ]);
    expect(groups.map((g) => g.kind)).toEqual(['Project', 'File']);
  });
});
