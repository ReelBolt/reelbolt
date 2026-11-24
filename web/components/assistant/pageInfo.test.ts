import { describe, expect, it } from 'vitest';
import { resolvePageInfo, welcomeFor } from './pageInfo';

describe('resolvePageInfo', () => {
  it('maps the execution route to its entity ids', () => {
    const info = resolvePageInfo('/projects/p1/workflows/w1/executions/e1');
    expect(info.kind).toBe('execution');
    expect(info.entityIds).toEqual({ projectId: 'p1', workflowId: 'w1', executionId: 'e1' });
  });
  it('strips the /app basePath and trailing slash', () => {
    expect(resolvePageInfo('/app/agents/').kind).toBe('agents');
  });
  it('knows the files tab from the route', () => {
    expect(resolvePageInfo('/projects/p1/files')).toMatchObject({ kind: 'project-files', tab: 'files' });
  });
  it('recognises admin inference pages', () => {
    expect(resolvePageInfo('/admin/inference-providers').kind).toBe('admin-providers');
    expect(resolvePageInfo('/admin/inference-providers/x').entityIds.providerId).toBe('x');
  });
  it('falls back for unknown routes', () => {
    expect(resolvePageInfo('/nope').kind).toBe('other');
    expect(welcomeFor('other').suggestions.length).toBeGreaterThanOrEqual(3);
  });
  it('every kind with a welcome has 2-4 chips', () => {
    for (const kind of ['dashboard', 'projects', 'project', 'workflow', 'execution', 'agents', 'admin-providers', 'editor', 'editor-hub', 'pictures', 'picture-editor'] as const) {
      const n = welcomeFor(kind).suggestions.length;
      expect(n).toBeGreaterThanOrEqual(2);
      expect(n).toBeLessThanOrEqual(4);
    }
  });
});

describe('picture editor route', () => {
  it('does not mistake /editor/thumbnails/{doc} for a video editor project id', () => {
    const info = resolvePageInfo('/app/editor/thumbnails/04d17aa1-84fe-4694-9b86-db1bf2d3997d', '?project=de696df1-0fb1-488a-9ab0-e90dc313a0c8');
    expect(info.title).toBe('Picture editor');
    expect(info.entityIds.projectId).toBe('de696df1-0fb1-488a-9ab0-e90dc313a0c8');
  });

  it('is its own page kind and carries the document id from the path', () => {
    const info = resolvePageInfo('/app/editor/thumbnails/04d17aa1-84fe-4694-9b86-db1bf2d3997d', '?project=de696df1-0fb1-488a-9ab0-e90dc313a0c8');
    expect(info.kind).toBe('picture-editor');
    expect(info.entityIds).toEqual({
      documentId: '04d17aa1-84fe-4694-9b86-db1bf2d3997d',
      projectId: 'de696df1-0fb1-488a-9ab0-e90dc313a0c8',
    });
  });

  it('never sends a document id that is not a GUID', () => {
    const info = resolvePageInfo('/editor/thumbnails/not-an-id');
    expect(info.kind).toBe('picture-editor');
    expect(info.entityIds.documentId).toBeUndefined();
    expect(resolvePageInfo('/editor/thumbnails/%E0%A4%A').entityIds.documentId).toBeUndefined();
  });

  it('keeps the timeline editor on its own kind, with its project and timeline', () => {
    expect(resolvePageInfo('/editor/p1/timeline/t1')).toMatchObject({
      kind: 'editor',
      title: 'Video editor',
      entityIds: { projectId: 'p1', timelineId: 't1' },
    });
    expect(resolvePageInfo('/editor/p1/review/t1')).toMatchObject({ kind: 'editor', entityIds: { projectId: 'p1', timelineId: 't1' } });
  });

  it("carries the hub's project from the query", () => {
    const project = 'de696df1-0fb1-488a-9ab0-e90dc313a0c8';
    expect(resolvePageInfo('/editor', `?project=${project}&view=media`)).toMatchObject({ kind: 'editor-hub', entityIds: { projectId: project } });
  });

  it('tells the two editors apart on every route that reaches one of them', () => {
    // The Editor page is a list, and its two views are different lists.
    expect(resolvePageInfo('/editor')).toMatchObject({ kind: 'editor-hub', title: 'Editor' });
    expect(resolvePageInfo('/editor', '?view=videos')).toMatchObject({ kind: 'editor-hub', title: 'Editor' });
    expect(resolvePageInfo('/editor', '?view=pictures')).toMatchObject({ kind: 'pictures', title: 'Pictures' });

    // The video editor is the timeline, wherever it is opened from.
    expect(resolvePageInfo('/projects/p1/editor/s1')).toMatchObject({
      kind: 'editor',
      title: 'Video editor',
      entityIds: { projectId: 'p1', stepResultId: 's1' },
    });
    expect(resolvePageInfo('/editor/p1/s1')).toMatchObject({ kind: 'editor', title: 'Video editor' });

    // The picture editor is neither of those, and the pictures LIST is not the picture editor.
    expect(
      resolvePageInfo('/editor/thumbnails/04d17aa1-84fe-4694-9b86-db1bf2d3997d', '?project=p1'),
    ).toMatchObject({ kind: 'picture-editor', title: 'Picture editor' });
    expect(resolvePageInfo('/editor', '?view=pictures').kind).not.toBe('picture-editor');
  });

  it('greets each editor with its own copy', () => {
    expect(welcomeFor('editor').heading).toBe('In the video editor');
    expect(welcomeFor('editor-hub').heading).toBe('On the Editor page');
    expect(welcomeFor('pictures').heading).toBe('Your pictures');
    expect(welcomeFor('picture-editor').heading).toBe('In the picture editor');
  });

  it('greets the picture editor with picture chips', () => {
    const welcome = welcomeFor('picture-editor');
    expect(welcome.heading).toBe('In the picture editor');
    expect(welcome.suggestions.map((s) => s.title)).toContain('Design a sale badge');
  });

});
