'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import {
  BILLING_SUMMARY_PATH,
  getBillingUsageUrl,
  postWorkflowCostEstimate,
  workflowEstimatePath,
  type BillingUsageParams,
  type WorkflowEstimateBody,
} from '../api/billing';
import { merchantLabel } from '../../components/billing/subscription';
import type { BillingSummary, BillingUsage, WorkflowCostEstimate } from '../types/billing';
import type { RunOutputResolution } from '../types/styles';

/**
 * The organization's credit balance and plan. 404s (caught by `isBillingUnavailable`) when
 * billing is not enabled on the deployment.
 */
export function useBillingSummary(enabled = true) {
  // A null key is SWR's "do not fetch": for a caller that is mounted but not showing anything yet.
  return useSWR<BillingSummary>(enabled ? BILLING_SUMMARY_PATH : null, fetcher);
}

/** The provider's display name for copy (or a neutral phrase while the summary is unknown). */
export function useMerchantLabel(enabled = true): string {
  const { data } = useBillingSummary(enabled);
  return merchantLabel(data);
}

/**
 * The largest output short side the organization's plan renders, in pixels, or null when there is
 * no cap to show: an unlimited plan, billing off on this deployment (the summary 404s), an older
 * API without `entitlements`, or the summary not read yet. Null enables every choice; the server
 * still refuses anything above the plan, so this only decides what the UI offers. `enabled: false`
 * reads nothing (a closed dialog) and answers null.
 */
export function useOutputHeightCap(enabled = true): number | null {
  const { data } = useBillingSummary(enabled);
  return data?.entitlements?.maxOutputHeight ?? null;
}

/**
 * The organization's metered usage. The SWR key is the fully-built URL, so changing the axis,
 * the window or the execution filter produces a different key and re-queries rather than
 * serving the previous query's cached roll-up.
 */
export function useBillingUsage(params: BillingUsageParams | null = {}) {
  // A null key is SWR's "do not fetch": used when the caller has no id to filter on yet.
  return useSWR<BillingUsage>(params === null ? null : getBillingUsageUrl(params), fetcher);
}

/**
 * The credits a single run was charged, read from the same ledger the usage page reads.
 *
 * Returns `undefined` while the figure is unknown AND whenever billing is unavailable on this
 * deployment — the caller must hide the number rather than render a zero it never verified.
 */
export function useExecutionCredits(executionId: string | undefined): number | undefined {
  const { data, error } = useBillingUsage(executionId ? { executionId } : null);
  return error ? undefined : data?.totals.credits;
}

/**
 * What one workflow's next run is expected to cost (B7), read before Execute is pressed.
 *
 * Keyed on the endpoint URL, so the confirm dialog, the Run button and anything else asking about
 * the same workflow share one request and one cached answer. A null key (no id yet) is SWR's "do
 * not fetch".
 *
 * This NEVER throws at the caller: a deployment without billing, an estimate that could not be
 * computed and a plain network failure all have to leave the screen unchanged, so the consumers
 * read `data?.billingEnabled` and render nothing when it is not there. The returned `mutate` is
 * what a 402 calls to pick up the balance the refusal was based on.
 */
export function useWorkflowCostEstimate(
  projectId: string | undefined,
  workflowId: string | undefined,
  outputResolution?: RunOutputResolution,
  videoFileIds?: readonly string[],
) {
  return useSWR<WorkflowCostEstimate>(
    projectId && workflowId ? workflowEstimatePath(projectId, workflowId, outputResolution, videoFileIds) : null,
    fetcher,
  );
}

/**
 * The estimate of a run with its brief (`POST .../estimate`, docs/design/app-inputs.md 11.4), for an app whose inputs
 * move the price. A null body fetches nothing. The caller debounces the body; the previous answer stays while the next
 * one loads, so the price line never blinks.
 */
export function useWorkflowCostEstimateWithBrief(
  projectId: string | undefined,
  workflowId: string | undefined,
  body: WorkflowEstimateBody | null,
) {
  const key = projectId && workflowId && body ? ['workflow-estimate-brief', projectId, workflowId, JSON.stringify(body)] : null;
  return useSWR<WorkflowCostEstimate>(
    key,
    () => postWorkflowCostEstimate(projectId!, workflowId!, body!),
    { keepPreviousData: true, revalidateOnFocus: false, shouldRetryOnError: false },
  );
}
