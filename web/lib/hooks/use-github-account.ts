'use client';

import useSWR from 'swr';
import { getGitHubAccount } from '../api/github';
import type { GitHubAccount } from '../types/github';

/**
 * The workspace's connection to a person's GitHub account (docs/github-app.md).
 *
 * The key is the endpoint itself, so connecting or disconnecting — which happens in another tab, on
 * github.com — is picked up by `mutate()` after the callback returns rather than by polling.
 */
export function useGitHubAccount() {
  return useSWR<GitHubAccount>('/api/v1/github/account', getGitHubAccount);
}
