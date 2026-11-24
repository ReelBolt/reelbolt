import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.hoisted(() => vi.fn());
vi.mock('./client', () => ({ apiFetch }));

import { executeWorkflow, getWorkflowApp, getWorkflowApps, lintWorkflow, workflowAppPath, workflowAppsPath } from './workflows';

beforeEach(() => {
  apiFetch.mockReset();
  apiFetch.mockResolvedValue({});
});

describe('workflow apps client', () => {
  it('reads the gallery and one app manifest from the documented paths', async () => {
    await getWorkflowApps('p1');
    expect(apiFetch).toHaveBeenLastCalledWith('/api/v1/projects/p1/workflow-apps');
    await getWorkflowApp('p1', 'w1');
    expect(apiFetch).toHaveBeenLastCalledWith('/api/v1/projects/p1/workflows/w1/app');
    expect(workflowAppsPath('p1')).toBe('/api/v1/projects/p1/workflow-apps');
    expect(workflowAppPath('p1', 'w1')).toBe('/api/v1/projects/p1/workflows/w1/app');
  });

  it('posts the lint body as given, and an empty one by default', async () => {
    await lintWorkflow('p1', 'w1');
    expect(apiFetch).toHaveBeenLastCalledWith('/api/v1/projects/p1/workflows/w1/lint', { method: 'POST', body: '{}' });
    await lintWorkflow('p1', 'w1', { brief: { lengthSec: 60, voice: { mode: 'Narrated' } } });
    const [, init] = apiFetch.mock.calls[1];
    expect(JSON.parse(init.body)).toEqual({ brief: { lengthSec: 60, voice: { mode: 'Narrated' } } });
  });

  it('sends the typed brief on execute only when given, so a plain run keeps its body', async () => {
    await executeWorkflow('p1', 'w1', 'notes');
    expect(JSON.parse(apiFetch.mock.calls[0][1].body)).toEqual({ userRequest: 'notes', regenerateVideoClips: false });
    await executeWorkflow('p1', 'w1', null, false, {}, undefined, undefined, {
      language: 'it', sourceFiles: [{ role: 'Video', fileIds: ['f1'] }],
    });
    expect(JSON.parse(apiFetch.mock.calls[1][1].body).brief).toEqual({
      language: 'it', sourceFiles: [{ role: 'Video', fileIds: ['f1'] }],
    });
  });

  it('sends the brief alone: no request text and no legacy choices beside it', async () => {
    await executeWorkflow('p1', 'w1', 'legacy notes', false, { stylePresetKey: 'tiktok', outputFormat: 'Vertical' }, undefined, undefined, {
      stylePreset: 'tiktok', notes: 'n',
    });
    expect(JSON.parse(apiFetch.mock.calls[0][1].body)).toEqual({
      regenerateVideoClips: false, brief: { stylePreset: 'tiktok', notes: 'n' },
    });
  });
});
