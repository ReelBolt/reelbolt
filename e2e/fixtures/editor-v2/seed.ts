import { expect } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as path from 'path';
import { EditorApi, mediaClip } from './api';
import { MEDIA_DIR, READONLY_PROJECT } from './env';

export type SeedFiles = { clipA: string; clipB: string; vertical: string; still: string; tone: string };

export type Seed = {
  projectId: string;
  projectName: string;
  files: SeedFiles;
  blankTimelineId: string;
  seededTimelineId: string;
  derushTimelineId: string;
  api: EditorApi;
  /** True when the seed is the kept audit project on a deployed box (no writes allowed). */
  readonly: boolean;
};

/** The seeded timeline's layout (audit setup.md §4, rev 6): every source lands as 150 frames. */
export const SEEDED_LAYOUT = {
  clipA: [0, 150],
  clipB: [150, 300],
  vertical: [300, 450],
  still: [450, 600],
  tone: [0, 150],
} as const;

const MEDIA: Array<[keyof SeedFiles, string, string]> = [
  ['clipA', 'clipA.mp4', 'video/mp4'],
  ['clipB', 'clipB.mp4', 'video/mp4'],
  ['vertical', 'vertical.mp4', 'video/mp4'],
  ['still', 'still.png', 'image/png'],
  ['tone', 'tone.wav', 'audio/wav'],
];

/** Generates the five media files (ffmpeg, deterministic generators) into MEDIA_DIR once. */
let mediaChecked = false;
export function ensureMedia(): string {
  // The script locks, decode-checks and writes atomically, so it is safe to call from every worker; it is cheap when
  // everything is already valid. Once per process is enough.
  if (!mediaChecked) {
    execFileSync('bash', [path.join(__dirname, 'media', 'make-media.sh'), MEDIA_DIR], { stdio: 'pipe' });
    mediaChecked = true;
  }
  return MEDIA_DIR;
}

/**
 * Creates the worker's project through the REST API only: five uploads, proxies requested and awaited
 * (the audit's seed had them, so viewer guards test the viewer, not the upload pipeline that TOP10-05
 * pins), a blank timeline, the seeded timeline and a derush timeline made WITHOUT any LLM:
 * VideoAnalyze (clipA, transcription/vision Off) -> VideoCompile (keepWholeSources), executed, then
 * "New timeline" from that VideoCompile result.
 */
export async function createSeed(api: EditorApi, projectName: string): Promise<Seed> {
  const dir = ensureMedia();
  const project = await api.createProject(projectName);
  const files = {} as SeedFiles;
  for (const [key, file, mime] of MEDIA) files[key] = await api.upload(project.id, path.join(dir, file), mime);

  await requestProxies(api, project.id, [files.clipA, files.clipB, files.vertical]);

  const blank = await api.createTimeline(project.id, 'remediation-blank');

  const seeded = await api.createTimeline(project.id, 'remediation-seeded');
  await api.putDocument(project.id, seeded.id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    v.items = (['clipA', 'clipB', 'vertical', 'still'] as const).map((k) =>
      mediaClip(crypto.randomUUID(), MEDIA.find(([m]) => m === k)![1], files[k], SEEDED_LAYOUT[k][0], SEEDED_LAYOUT[k][1]),
    );
    a.items = [mediaClip(crypto.randomUUID(), 'tone.wav', files.tone, SEEDED_LAYOUT.tone[0], SEEDED_LAYOUT.tone[1])];
  });

  const derushTimelineId = await createDerushTimeline(api, project.id, files.clipA);
  await waitForProxies(api, project.id, [files.clipA, files.clipB, files.vertical]);

  return {
    projectId: project.id,
    projectName,
    files,
    blankTimelineId: blank.id,
    seededTimelineId: seeded.id,
    derushTimelineId,
    api,
    readonly: false,
  };
}

async function createDerushTimeline(api: EditorApi, projectId: string, clipA: string): Promise<string> {
  const agent = await api.agentId('VideoTransform');
  const wf = await api.json<{ id: string }>('POST', `/api/v1/projects/${projectId}/workflows`, {
    name: 'remediation derush (no LLM)',
    requiresUserInput: false,
    steps: [
      {
        agentDefinitionId: agent, stepOrder: 1, edgeConditionJson: null, label: 'Analyze', stepType: 'VideoAnalyze',
        videoAnalyzeConfigJson: JSON.stringify({
          version: 1, source: { kind: 'ProjectFile', projectFileId: clipA }, transcription: 'Off', vision: 'Off', derushPrePass: 'Off',
        }),
      },
      {
        agentDefinitionId: agent, stepOrder: 2, edgeConditionJson: null, label: 'Compile', stepType: 'VideoCompile',
        videoCompileConfigJson: JSON.stringify({
          version: 1, decision: { from: 'Previous' }, analysisStepOrder: 1, keepWholeSources: true, mode: 'Reencode', outputFormat: 'Landscape',
        }),
      },
    ],
  }, [200, 201]);
  const run = await api.runWorkflow(projectId, wf.id);
  expect(
    run.status,
    `derush run ${JSON.stringify(run.stepResults.map((s) => ({ status: s.status, code: s.failureCode, class: s.failureClass, error: s.errorDetails?.slice(0, 400) })))}`,
  ).toBe('Passed');
  const compile = run.stepResults.find((s) => /\.mp4$/i.test(s.outputStorageKey ?? ''));
  expect(compile, 'VideoCompile result with a media output').toBeTruthy();
  const tl = await api.createTimeline(projectId, 'remediation-derush', compile!.id);
  return tl.id;
}

async function requestProxies(api: EditorApi, projectId: string, fileIds: string[]): Promise<void> {
  for (const id of fileIds) {
    await api.json('POST', `/api/v1/projects/${projectId}/media/derivatives/request`, { mediaRef: `file:${id}`, kinds: ['proxy', 'waveformPeaks'] }, [200, 202]);
  }
}

async function waitForProxies(api: EditorApi, projectId: string, fileIds: string[], timeoutMs = 180_000): Promise<void> {
  for (const id of fileIds) {
    // One budget per file: the proxies are encoded one after the other by an engine that shares four vCPUs with two workers
    // and their browsers on a hosted runner, so a budget shared by every file ran out on the last one under load.
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const body = await api.json<unknown>('GET', `/api/v1/projects/${projectId}/media/file:${id}/derivatives`);
      const rows = (Array.isArray(body) ? body : ((body as { derivatives?: unknown[]; items?: unknown[] }).derivatives ?? (body as { items?: unknown[] }).items ?? [])) as Array<{ kind: string }>;
      if (rows.some((r) => r.kind === 'proxy')) break;
      if (Date.now() > deadline) throw new Error(`proxy for file ${id} not ready after ${timeoutMs} ms: ${JSON.stringify(body).slice(0, 300)}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
  }
}

/** Read-only: resolve the kept audit seed project on a deployed box by name. Writes nothing. */
export async function findReadonlySeed(api: EditorApi): Promise<Seed> {
  const project = await api.findProjectByName(READONLY_PROJECT);
  expect(project, `project ${READONLY_PROJECT}`).toBeTruthy();
  const timelines = await api.listTimelines(project!.id);
  const byName = (n: string) => timelines.find((t) => t.name === n)?.id ?? '';
  const files = await api.listFiles(project!.id).catch(() => [] as Array<{ id: string; originalFileName: string }>);
  const file = (n: string) => files.find((f) => f.originalFileName === n)?.id ?? '';
  return {
    projectId: project!.id,
    projectName: project!.name,
    files: { clipA: file('clipA.mp4'), clipB: file('clipB.mp4'), vertical: file('vertical.mp4'), still: file('still.png'), tone: file('tone.wav') },
    blankTimelineId: '',
    seededTimelineId: byName('audit-seed-timeline'),
    derushTimelineId: byName('audit-derush-timeline'),
    api,
    readonly: true,
  };
}
