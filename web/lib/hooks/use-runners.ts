'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { pairingRequestUrl } from '../api/runners';
import type { RunnerDevice, RunnerLlmResponse, RunnerPairing } from '../types/runner';

/**
 * The active workspace's paired machines (E7-UI). Readable by every member: `GET /api/v1/runners`
 * gates on having an active workspace, not on a role, and answers 403 only when the session has
 * none. The list is 'what can run work right now' — revoked devices are excluded server-side.
 *
 * The online dot is derived from `lastSeenAt`, so it only moves when the query refetches; the
 * page refreshes on focus/reconnect like every other SWR query here, which is the honest cadence
 * for a heartbeat that is itself written at most once a minute.
 */
export function useRunners(enabled: boolean = true) {
  return useSWR<RunnerDevice[]>(enabled ? '/api/v1/runners' : null, fetcher);
}

/**
 * One pending pairing request, by its normalized user code.
 *
 * Never retried and never revalidated: a 404 is a TERMINAL answer for this code (unknown, expired,
 * used or denied — the server answers all four the same way), and refetching after an approval
 * would replace the screen the user just acted on with a 'not found'.
 */
export function usePairingRequest(userCode: string | null) {
  return useSWR<RunnerPairing>(userCode ? pairingRequestUrl(userCode) : null, fetcher, {
    shouldRetryOnError: false,
    revalidateOnFocus: false,
    revalidateOnReconnect: false,
  });
}

/** The workspace's paired computers that share a local model (org Owner/Admin only). */
export function useOrgRunnerLlm(enabled: boolean = true) {
  return useSWR<RunnerLlmResponse>(enabled ? '/api/v1/org/runner-llm' : null, fetcher);
}
