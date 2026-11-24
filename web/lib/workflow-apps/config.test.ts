import { describe, expect, it } from 'vitest';
import { workflowAppsEnabled } from './config';

describe('workflowAppsEnabled', () => {
  it('defaults to on', () => {
    expect(workflowAppsEnabled({})).toBe(true);
    expect(workflowAppsEnabled({ WORKFLOW_APPS_ENABLED: 'true' })).toBe(true);
    expect(workflowAppsEnabled({ WORKFLOW_APPS_ENABLED: '' })).toBe(true);
  });
  it('is off only for the exact string false', () => {
    expect(workflowAppsEnabled({ WORKFLOW_APPS_ENABLED: 'false' })).toBe(false);
  });
});
