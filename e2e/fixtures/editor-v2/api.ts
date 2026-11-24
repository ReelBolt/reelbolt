import { expect, type APIRequestContext } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/* Minimal shapes of the editor document (web/lib/editing/types.ts); only what the suite reads. */
export type AssetRef = { kind: 'projectFile' | 'stepResult'; projectFileId?: string | null; stepResultId?: string | null };
export type Item = {
  type: string;
  id: string;
  name: string;
  recordIn: number;
  recordOut: number;
  assetRef?: AssetRef;
  sourceIn?: number;
  fadeInFrames?: number;
  fadeOutFrames?: number;
  transform?: Record<string, unknown> | null;
  [k: string]: unknown;
};
export type Track = { id: string; kind: 'video' | 'audio'; name: string; items: Item[]; gainDb?: unknown; [k: string]: unknown };
export type TimelineDocument = {
  schemaVersion: number;
  id: string;
  projectId: string;
  name: string;
  settings: { width: number; height: number; rate: { num: number; den: number }; [k: string]: unknown };
  tracks: Track[];
  markers?: Array<{ id: string; name: string; frame: number; [k: string]: unknown }> | null;
  sequences?: unknown[] | null;
  [k: string]: unknown;
};
export type Timeline = { id: string; projectId: string; name: string; revision: number; document: TimelineDocument; seedStepResultId?: string | null };

/** Thin REST client over the session cookie (nginx turns the cookie into the Bearer token). */
export class EditorApi {
  constructor(readonly request: APIRequestContext) {}

  async json<T>(method: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE', url: string, data?: unknown, expected?: number[]): Promise<T> {
    const res = await this.request.fetch(url, { method, data, failOnStatusCode: false });
    const text = await res.text();
    const ok = expected ? expected.includes(res.status()) : res.ok();
    expect(ok, `${method} ${url} -> ${res.status()} ${text.slice(0, 400)}`).toBeTruthy();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  /* ---------------------------------------------------------------- projects and files */

  createProject(name: string): Promise<{ id: string; name: string }> {
    return this.json('POST', '/api/v1/projects', { name, description: 'editor-v2 regression suite (T-001), safe to delete' });
  }

  async findProjectByName(name: string): Promise<{ id: string; name: string } | undefined> {
    const list = await this.json<Array<{ id: string; name: string }>>('GET', '/api/v1/projects');
    return list.find((p) => p.name === name);
  }

  async upload(projectId: string, file: string, mimeType: string, name = path.basename(file)): Promise<string> {
    const res = await this.request.post(`/api/v1/projects/${projectId}/files`, {
      multipart: { file: { name, mimeType, buffer: fs.readFileSync(file) } },
      failOnStatusCode: false,
    });
    expect(res.ok(), `upload ${name} -> ${res.status()} ${(await res.text()).slice(0, 300)}`).toBeTruthy();
    return ((await res.json()) as { id: string }).id;
  }

  async listFiles(projectId: string): Promise<Array<{ id: string; originalFileName: string }>> {
    const body = await this.json<unknown>('GET', `/api/v1/projects/${projectId}/files`);
    return (Array.isArray(body) ? body : ((body as { items?: unknown[] }).items ?? [])) as Array<{ id: string; originalFileName: string }>;
  }

  /* ---------------------------------------------------------------- timelines */

  timelinesUrl(projectId: string, id?: string): string {
    return `/api/v1/projects/${projectId}/edit-timelines${id ? `/${id}` : ''}`;
  }

  createTimeline(projectId: string, name: string, seedStepResultId?: string): Promise<Timeline> {
    return this.json('POST', this.timelinesUrl(projectId), { name, seedStepResultId: seedStepResultId ?? null });
  }

  listTimelines(projectId: string): Promise<Array<{ id: string; name: string; revision: number }>> {
    return this.json('GET', this.timelinesUrl(projectId));
  }

  getTimeline(projectId: string, id: string): Promise<Timeline> {
    return this.json('GET', this.timelinesUrl(projectId, id));
  }

  duplicateTimeline(projectId: string, id: string, name: string): Promise<Timeline> {
    return this.json('POST', `${this.timelinesUrl(projectId, id)}/duplicate`, { name }, [200, 201]);
  }

  /** Whole-document PATCH (how the audit seeded its timelines): `ops: []` stores the document as given. */
  async putDocument(projectId: string, id: string, mutate: (doc: TimelineDocument) => void): Promise<Timeline> {
    const current = await this.getTimeline(projectId, id);
    const doc = structuredClone(current.document);
    mutate(doc);
    return this.json('PATCH', this.timelinesUrl(projectId, id), { baseRevision: current.revision, document: discriminatorFirst(doc), ops: [] });
  }

  /* ---------------------------------------------------------------- workflows */

  async agentId(agentType: string): Promise<string> {
    const agents = await this.json<Array<{ id: string; agentType: string }>>('GET', '/api/v1/agents');
    const agent = agents.find((a) => a.agentType === agentType);
    expect(agent, `agent ${agentType}`).toBeTruthy();
    return agent!.id;
  }

  async runWorkflow(projectId: string, workflowId: string, timeoutMs = 300_000): Promise<{ id: string; status: string; stepResults: Array<{ id: string; status: string; outputStorageKey?: string | null; artifactStorageKey?: string | null; workflowStepId: string }> }> {
    const started = await this.json<{ id: string }>('POST', `/api/v1/projects/${projectId}/workflows/${workflowId}/execute`, {}, [200, 201, 202]);
    const deadline = Date.now() + timeoutMs;
    let last: { id: string; status: string; stepResults: never[] } | undefined;
    while (Date.now() < deadline) {
      last = await this.json('GET', `/api/v1/projects/${projectId}/executions/${started.id}`);
      if (['Passed', 'Failed', 'Cancelled'].includes(last!.status)) return last!;
      await new Promise((r) => setTimeout(r, 2000));
    }
    throw new Error(`execution ${started.id} did not finish: ${JSON.stringify(last)?.slice(0, 600)}`);
  }
}

/** The server reads items polymorphically by `type` and wants it first (web `stringifyDiscriminatorFirst`). */
export function discriminatorFirst<T>(value: T): T {
  return JSON.parse(JSON.stringify(value, (_k, v: unknown) => {
    if (v && typeof v === 'object' && !Array.isArray(v) && typeof (v as { type?: unknown }).type === 'string') {
      const { type, ...rest } = v as { type: string } & Record<string, unknown>;
      return { type, ...rest };
    }
    return v;
  })) as T;
}

export function mediaClip(id: string, name: string, projectFileId: string, recordIn: number, recordOut: number, extra: Partial<Item> = {}): Item {
  return {
    type: 'mediaClip',
    id,
    name,
    recordIn,
    recordOut,
    assetRef: { kind: 'projectFile', projectFileId, stepResultId: null },
    sourceIn: 0,
    playbackRate: 1,
    enabled: true,
    ...extra,
  };
}
