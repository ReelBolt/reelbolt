import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import {
  describeGitHubAccountError,
  describeGitHubConnectOutcome,
  describeGitHubFilesError,
  describeGitHubRepositoryListError,
  listGitHubBranches,
  listGitHubFiles,
  listGitHubRepositories,
  startGitHubConnect,
} from './github';

/**
 * The account half of the GitHub client (docs/github-app.md): the sentences a refused connection
 * produces, the sentence the sign-in's redirect token produces, and the queries the file selector
 * builds.
 */

let lastUrl: string | null = null;
let lastInit: RequestInit | undefined;

function mockFetchOnce(status: number, body: unknown) {
  const json = () => Promise.resolve(body);
  vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    lastUrl = url;
    lastInit = init;
    return Promise.resolve({ ok: status >= 200 && status < 300, status, statusText: 'Bad Request', json });
  }));
}

function refusal(status: number, code: string): ApiError {
  return new ApiError(code, status, [], { error: code });
}

afterEach(() => {
  vi.unstubAllGlobals();
  lastUrl = null;
  lastInit = undefined;
});

describe('describeGitHubConnectOutcome', () => {
  it('says nothing when the page was opened normally', () => {
    expect(describeGitHubConnectOutcome(null)).toBeNull();
    expect(describeGitHubConnectOutcome(undefined)).toBeNull();
    expect(describeGitHubConnectOutcome('')).toBeNull();
  });

  it('confirms a connection and sends the reader on to choosing', () => {
    expect(describeGitHubConnectOutcome('connected')).toMatch(/choose a repository/);
  });

  it('does not call a cancellation or a timeout a failure, because neither is one', () => {
    expect(describeGitHubConnectOutcome('denied')).toMatch(/did not approve/);
    expect(describeGitHubConnectOutcome('expired')).toMatch(/expired/);
    expect(describeGitHubConnectOutcome('expired')).not.toMatch(/wrong|error|failed/i);
  });

  it('explains an unverifiable callback without printing its token', () => {
    const sentence = describeGitHubConnectOutcome('state_mismatch');
    expect(sentence).toMatch(/could not be verified/);
    expect(sentence).not.toContain('state_mismatch');
  });

  it('falls back to a sentence for a token it does not know, never the token itself', () => {
    const sentence = describeGitHubConnectOutcome('something_new_from_github');
    expect(sentence).toBeTruthy();
    expect(sentence).not.toContain('something_new_from_github');
  });
});

describe('describeGitHubAccountError', () => {
  it('turns every "your connection cannot be used" code into the one action that fixes it', () => {
    for (const code of ['not_connected', 'github_account_not_connected', 'github_account_revoked', 'github_account_expired', 'github_account_unreadable']) {
      expect(describeGitHubAccountError(refusal(409, code))).toMatch(/Connect your GitHub account/);
    }
  });

  it('treats any 409 as the same condition, since connecting is the fix for all of them', () => {
    expect(describeGitHubAccountError(new ApiError('conflict', 409))).toMatch(/Connect your GitHub account first/);
  });

  it('explains a server that cannot link repositories at all, in plain words, never the code', () => {
    const sentence = describeGitHubAccountError(refusal(503, 'github_oauth_not_configured'));
    expect(sentence).toMatch(/not set up on this server yet/);
    expect(sentence).not.toContain('github_oauth_not_configured');
    expect(sentence).not.toMatch(/GitHub App|client|callback|credential/i);
  });

  it('passes a real message through, but never a bare server code', () => {
    // A sentence the server wrote is more useful than a generic apology...
    expect(describeGitHubAccountError(new ApiError('the workspace was archived', 500)))
      .toBe('the workspace was archived');

    // ...while a snake_case token is not a sentence, and printing it would show the reader a code.
    expect(describeGitHubAccountError(new ApiError('some_unmapped_code', 418)))
      .toBe('Your GitHub account could not be read. Try again.');
  });
});

describe('describeGitHubRepositoryListError', () => {
  it('sends a revoked or missing connection down the same path as reading the account', () => {
    expect(describeGitHubRepositoryListError(refusal(409, 'github_account_revoked')))
      .toMatch(/Connect your GitHub account/);
  });

  it('names GitHub when it is GitHub that could not answer', () => {
    expect(describeGitHubRepositoryListError(refusal(424, 'github_error'))).toMatch(/GitHub could not be reached/);
  });
});

describe('describeGitHubFilesError', () => {
  it('distinguishes a branch that moved from a repository that is no longer usable', () => {
    expect(describeGitHubFilesError(refusal(404, 'branch_not_found'))).toMatch(/renamed or deleted/);
    expect(describeGitHubFilesError(refusal(404, 'repository_not_found'))).toMatch(/cannot open this repository any more/);
    expect(describeGitHubFilesError(refusal(503, 'github_app_not_configured'))).toMatch(/not set up on this server/);
    expect(describeGitHubFilesError(refusal(409, 'github_account_not_connected'))).toMatch(/Connect your GitHub account first/);
  });
});

describe('the file and branch queries', () => {
  it('names the picked repository and the branch, encoded', async () => {
    mockFetchOnce(200, { entries: [] });
    await listGitHubFiles('p1', 991, 'release/1 & 2');
    expect(lastUrl).toBe('/api/v1/projects/p1/github/files?repositoryId=991&branch=release%2F1+%26+2');
  });

  it('asks for the linked repository when no repository is named, and never sends a tree SHA', async () => {
    mockFetchOnce(200, { entries: [] });
    await listGitHubFiles('p1', null, null);
    expect(lastUrl).toBe('/api/v1/projects/p1/github/files');
    expect(lastUrl).not.toMatch(/sha/i);

    mockFetchOnce(200, { branches: [] });
    await listGitHubBranches('p1', null);
    expect(lastUrl).toBe('/api/v1/projects/p1/github/branches');
    await listGitHubBranches('p1', 991);
    expect(lastUrl).toBe('/api/v1/projects/p1/github/branches?repositoryId=991');
  });
});

describe('account routes', () => {
  it('reads the repository picker from the project, so the project access check guards it', async () => {
    mockFetchOnce(200, { repositories: [], truncated: false, installUrl: null, accountLogin: null });
    await listGitHubRepositories('p1');
    expect(lastUrl).toBe('/api/v1/projects/p1/github/repositories');
  });

  it('starts the connect flow with a POST and returns the URL rather than following it', async () => {
    mockFetchOnce(200, { authorizeUrl: 'https://github.com/login/oauth/authorize?client_id=Iv1.abc' });
    const url = await startGitHubConnect('p1');

    expect(url).toBe('https://github.com/login/oauth/authorize?client_id=Iv1.abc');
    expect(lastInit?.method).toBe('POST');
    // The project travels so the callback can send the browser back to it; the person does not, because
    // the server reads that from the session and from its own single-use state row.
    expect(lastInit?.body).toBe(JSON.stringify({ projectId: 'p1' }));
  });

  it('sends a null project rather than omitting it when the flow starts outside a project', async () => {
    mockFetchOnce(200, { authorizeUrl: 'https://github.com/login/oauth/authorize' });
    await startGitHubConnect(null);
    expect(lastInit?.body).toBe(JSON.stringify({ projectId: null }));
  });
});
