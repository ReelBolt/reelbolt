import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import {
  describeGitHubLinkError,
  describeGitHubPullError,
  describeGitHubUnlinkError,
  describeLastSyncError,
  getProjectGitHubLink,
  githubErrorCode,
  linkProjectRepository,
  pullProjectRepository,
  unlinkProjectRepository,
  updateProjectRepository,
} from './github';
import type { ProjectGitHubLink } from '../types/github';

function mockFetchOnce(status: number, body: unknown, statusText = 'Bad Request') {
  const json = typeof body === 'string'
    ? () => Promise.reject(new SyntaxError('not json'))
    : () => Promise.resolve(body);
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: status >= 200 && status < 300, status, statusText, json }));
}

async function errorOf(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (err) {
    return err as ApiError;
  }
  throw new Error('expected rejection');
}

/** A refusal exactly as the controller writes it: a single-key body, and a message that IS the code. */
function refusal(status: number, code: string): ApiError {
  return new ApiError(code, status, [], { error: code });
}

afterEach(() => vi.unstubAllGlobals());

const link: ProjectGitHubLink = {
  projectId: 'p1',
  repository: 'acme/promo-videos',
  repositoryId: 991,
  installationId: 42,
  branch: 'main',
  pathPrefix: null,
  lastPulledCommit: 'a1b2c3d4e5f6',
  lastPulledAt: '2026-10-09T10:00:00Z',
  lastPulledFileCount: 12,
  lastError: null,
};

describe('githubErrorCode', () => {
  it('reads the single-key error body the controller writes', () => {
    expect(githubErrorCode(refusal(409, 'project_already_linked'))).toBe('project_already_linked');
  });

  it('is null for anything else', () => {
    expect(githubErrorCode(new Error('boom'))).toBeNull();
    expect(githubErrorCode(new ApiError('boom', 500))).toBeNull();
    expect(githubErrorCode(new ApiError('boom', 500, [], { error: 7 }))).toBeNull();
    expect(githubErrorCode(new ApiError('boom', 500, [], 'text'))).toBeNull();
  });
});

describe('requests', () => {
  it('reads the link', async () => {
    mockFetchOnce(200, link);
    await expect(getProjectGitHubLink('p1')).resolves.toEqual(link);
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/projects/p1/github');
  });

  it('reads 404 not_linked as no link, not as an error', async () => {
    mockFetchOnce(404, { error: 'not_linked' }, 'Not Found');
    await expect(getProjectGitHubLink('p1')).resolves.toBeNull();
  });

  it('still throws every other failure, so a broken page is not shown as an empty one', async () => {
    mockFetchOnce(500, { error: 'boom' }, 'Server Error');
    expect((await errorOf(getProjectGitHubLink('p1'))).status).toBe(500);
  });

  it('links with the documented body, the chosen files included', async () => {
    mockFetchOnce(200, link);
    await linkProjectRepository('p1', { repository: 'acme/promo-videos', branch: 'release', includePaths: ['video', 'README.md'] });

    const [url, init] = vi.mocked(fetch).mock.calls[0];
    expect(url).toBe('/api/v1/projects/p1/github');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual({
      repository: 'acme/promo-videos', branch: 'release', includePaths: ['video', 'README.md'],
    });
  });

  it('changes the choice with PATCH', async () => {
    mockFetchOnce(200, link);
    await updateProjectRepository('p1', { branch: null, includePaths: [] });

    const [url, init] = vi.mocked(fetch).mock.calls[0];
    expect(url).toBe('/api/v1/projects/p1/github');
    expect(init?.method).toBe('PATCH');
    expect(JSON.parse(String(init?.body))).toEqual({ branch: null, includePaths: [] });
  });

  it('unlinks with DELETE and resolves on the 204 the server answers', async () => {
    mockFetchOnce(204, '', 'No Content');
    await expect(unlinkProjectRepository('p1')).resolves.toBeUndefined();

    const [url, init] = vi.mocked(fetch).mock.calls[0];
    expect(url).toBe('/api/v1/projects/p1/github');
    expect(init?.method).toBe('DELETE');
  });

  it('queues "Sync now" through the pull endpoint and reads the 202 it answers', async () => {
    mockFetchOnce(202, { status: 'queued' }, 'Accepted');
    const result = await pullProjectRepository('p1');
    expect(result.status).toBe('queued');
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/projects/p1/github/pull');
    expect(vi.mocked(fetch).mock.calls[0][1]?.method).toBe('POST');
  });

  it('reads "already syncing" as an answer, not an error', async () => {
    mockFetchOnce(202, { status: 'already_syncing' }, 'Accepted');
    await expect(pullProjectRepository('p1')).resolves.toEqual({ status: 'already_syncing' });
  });
});
// The whole point of this module: the server answers with CODES, and every one of them has to become
// a sentence that says what to do. Every code the docs document is asserted here, so a code added to
// the controller without a sentence fails this test rather than reaching a user as a bare token.
const DOCUMENTED_LINK_CODES: [number, string][] = [
  [400, 'invalid_repository'],
  [400, 'invalid_branch'],
  [400, 'invalid_path_prefix'],
  [400, 'invalid_include_paths'],
  [404, 'repository_not_found'],
  [404, 'github_app_not_installed'],
  [404, 'branch_not_found'],
  [409, 'project_already_linked'],
  [409, 'repository_already_linked'],
  [409, 'github_account_not_connected'],
  [429, 'sync_too_soon'],
  [424, 'github_error'],
  [503, 'github_app_not_configured'],
];

/** Words that describe how the server is set up. None of them belongs in a sentence a user reads. */
const SETUP_JARGON = /GitHub App|installation|install the app|webhook|credential|private key|client secret|pathPrefix|GitHub:/i;

describe('describeGitHubLinkError', () => {
  it('gives every documented code its own sentence, in plain words, and never the code itself', () => {
    const seen = new Map<string, string>();

    for (const [status, code] of DOCUMENTED_LINK_CODES) {
      const sentence = describeGitHubLinkError(refusal(status, code));
      expect(sentence, code).not.toBe('The repository could not be linked. Try again.');
      expect(sentence, code).not.toContain(code);
      expect(sentence, code).not.toMatch(SETUP_JARGON);
      expect(sentence.length, code).toBeGreaterThan(30);
      seen.set(code, sentence);
    }

    // Distinct codes, distinct fixes — except the two the server deliberately merges (below).
    expect(new Set(seen.values()).size).toBe(DOCUMENTED_LINK_CODES.length - 1);
  });

  it('answers every "cannot use that repository" 404 with one sentence, as the server does', () => {
    // Not given access and claimed by another workspace are ONE answer on the wire (D5 - never an
    // existence oracle), so they must be one answer on screen.
    const installed = describeGitHubLinkError(refusal(404, 'github_app_not_installed'));
    expect(describeGitHubLinkError(refusal(404, 'repository_not_found'))).toBe(installed);
    expect(describeGitHubLinkError(new ApiError('Not Found', 404))).toBe(installed);
    expect(describeGitHubLinkError(refusal(404, 'something_new'))).toBe(installed);
    expect(installed).toMatch(/Give ReelBolt access/);
  });

  it('tells a person with no connected account to connect one', () => {
    expect(describeGitHubLinkError(refusal(409, 'github_account_not_connected'))).toMatch(/Connect your GitHub account first/);
  });

  it('names a limit refusal with its numbers', () => {
    const err = new ApiError('x', 422, [], { error: 'too_many_files', actual: 3995, limit: 2000 });
    expect(describeGitHubLinkError(err)).toBe(
      'Too many files are chosen: 3,995, and one project can sync at most 2,000. Choose fewer files or folders.');
  });

  it('says how long to wait after a sync that ran moments ago', () => {
    const err = new ApiError('x', 429, [], { error: 'sync_too_soon', retryAfterSeconds: 7 });
    expect(describeGitHubLinkError(err)).toMatch(/Wait 7 seconds/);
  });

  it('never shows a bare code as if it were prose', async () => {
    mockFetchOnce(500, { error: 'something_new' }, 'Server Error');
    const err = await errorOf(linkProjectRepository('p1', { repository: 'a/b' }));
    expect(err.message).toBe('something_new');
    expect(describeGitHubLinkError(err)).toBe('The repository could not be linked. Try again.');
  });

  it('falls back to a real sentence for a non-ApiError', () => {
    expect(describeGitHubLinkError(new Error('network down'))).toBe('network down');
    expect(describeGitHubLinkError('nonsense')).toBe('The repository could not be linked. Try again.');
  });
});

describe('describeGitHubPullError', () => {
  it('passes the plain reason the server wrote through for a GitHub failure', () => {
    const reason = 'GitHub had a problem answering (error 503). This is usually temporary: sync again in a few minutes.';
    const err = new ApiError(reason, 424, [], { error: 'github_error', message: reason });
    expect(describeGitHubPullError(err)).toBe(reason);
  });

  it('names every limit refusal with its numbers rather than "try again"', () => {
    expect(describeGitHubPullError(new ApiError('x', 422, [], { error: 'too_many_files', actual: 3995, limit: 2000 })))
      .toMatch(/3,995.*2,000/);
    expect(describeGitHubPullError(new ApiError('x', 422, [], { error: 'selection_too_large', actual: 900 * 1024 * 1024, limit: 512 * 1024 * 1024 })))
      .toMatch(/900 MB.*512 MB/);
    expect(describeGitHubPullError(new ApiError('x', 422, [], { error: 'repository_too_large' })))
      .toMatch(/too large for GitHub to list/);
  });

  it('still says something useful for a GitHub failure without a written reason', () => {
    expect(describeGitHubPullError(refusal(424, 'github_error'))).toMatch(/GitHub did not answer as expected/);
  });

  it('treats a vanished link as reload, and a missing account as connect', () => {
    expect(describeGitHubPullError(refusal(404, 'not_linked'))).toMatch(/Reload/);
    expect(describeGitHubPullError(refusal(409, 'github_account_not_connected'))).toMatch(/Connect your GitHub account first/);
  });

  it('names the server that is not set up, in plain words', () => {
    const sentence = describeGitHubPullError(refusal(503, 'github_app_not_configured'));
    expect(sentence).toMatch(/not set up on this server/);
    expect(sentence).not.toMatch(SETUP_JARGON);
  });
});

describe('describeLastSyncError', () => {
  it('re-words the operator message an older server stored, with the numbers', () => {
    const stored = 'vecchiotom/ReelBolt@463690a holds 3995 files, over the configured limit of 2000. Link a subdirectory (pathPrefix) or raise GitHub:MaxFilesPerPull.';
    const sentence = describeLastSyncError(stored)!;
    expect(sentence).toMatch(/3,995.*2,000/);
    expect(sentence).not.toMatch(SETUP_JARGON);
  });

  it('never shows a REST path', () => {
    expect(describeLastSyncError('GitHub answered 500 for /repos/a/b/git/trees/c?recursive=1.')).not.toMatch(/\/repos\//);
    expect(describeLastSyncError('GitHub answered 403 for /repos/a/b/git/blobs/x.')).toMatch(/refused ReelBolt access/);
  });

  it('passes a plain sentence through unchanged, and nothing through as nothing', () => {
    expect(describeLastSyncError('Too many files are chosen to sync: 3, and the limit is 2.')).toBe('Too many files are chosen to sync: 3, and the limit is 2.');
    expect(describeLastSyncError(null)).toBeNull();
  });
});

describe('describeGitHubUnlinkError', () => {
  it('reads an already-gone link as already done', () => {
    const sentence = describeGitHubUnlinkError(refusal(404, 'not_linked'));
    expect(sentence).toMatch(/not linked to a repository any more/);
    expect(describeGitHubUnlinkError(new ApiError('Not Found', 404))).toBe(sentence);
  });

  it('falls back for anything else', () => {
    expect(describeGitHubUnlinkError(new ApiError('boom', 500))).toBe('boom');
  });
});
