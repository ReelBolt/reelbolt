import { apiFetch, ApiError } from './client';
import type {
  ConsolidationReport,
  CreateInferenceModelRequest,
  CreateInferenceProviderRequest,
  CreateModelCapabilityRequest,
  InferenceModel,
  InferenceProvider,
  InferenceProviderCapability,
  InferenceProviderStatus,
  ModelCapability,
  ModelCapabilityPrice,
  SelectableProvider,
  TestInferenceProviderRequest,
  TestInferenceProviderResponse,
  UpdateInferenceModelRequest,
  UpdateInferenceProviderRequest,
  UpdateModelCapabilityRequest,
} from '../types/inference-provider';
import type { CreatePriceRequest } from '../types/pricing';

/**
 * WHICH set of providers an operation acts on.
 *
 * `platform` is the platform-managed set (null organization), reachable only by a platform admin
 * through `/api/v1/inference-providers`. `org` is the active workspace's own bring-your-own
 * providers, managed by its Owner/Admin through the SINGULAR `/api/v1/org/…` (nginx sends the plural
 * `/api/v1/orgs/…` to the Go API, so the two never collide).
 *
 * Both answer the same shapes, except that the org twin always answers `prices: null` and has no
 * price routes and no consolidation report — those calls below take no scope at all.
 */
export type ProviderScope = 'platform' | 'org';

const BASE: Record<ProviderScope, string> = {
  platform: '/api/v1/inference-providers',
  org: '/api/v1/org/inference-providers',
};

/** The list URL for a scope, so the SWR key and the request can never drift apart. */
export function providersUrl(scope: ProviderScope = 'platform'): string {
  return BASE[scope];
}

/** One provider's URL for a scope. */
export function providerUrl(id: string, scope: ProviderScope = 'platform'): string {
  return `${BASE[scope]}/${encodeURIComponent(id)}`;
}

const modelUrl = (providerId: string, modelId: string, scope: ProviderScope) =>
  `${providerUrl(providerId, scope)}/models/${encodeURIComponent(modelId)}`;

const capabilityUrl = (providerId: string, modelId: string, capId: string, scope: ProviderScope) =>
  `${modelUrl(providerId, modelId, scope)}/capabilities/${encodeURIComponent(capId)}`;

const json = (method: string, body?: unknown): RequestInit =>
  body === undefined ? { method } : { method, body: JSON.stringify(body) };

/** `GET /api/v1/inference-providers/selectable` — the member-facing list, not scope-parameterized. */
export const SELECTABLE_PROVIDERS_PATH = '/api/v1/inference-providers/selectable';

/** `GET /api/v1/inference-providers/consolidation-report` — platform only. */
export const CONSOLIDATION_REPORT_PATH = '/api/v1/inference-providers/consolidation-report';

// ---------------------------------------------------------------- provider accounts

export function getProviders(scope: ProviderScope = 'platform'): Promise<InferenceProvider[]> {
  return apiFetch<InferenceProvider[]>(providersUrl(scope));
}

export function getProvider(id: string, scope: ProviderScope = 'platform'): Promise<InferenceProvider> {
  return apiFetch<InferenceProvider>(providerUrl(id, scope));
}

export function createProvider(
  data: CreateInferenceProviderRequest,
  scope: ProviderScope = 'platform',
): Promise<InferenceProvider> {
  return apiFetch<InferenceProvider>(providersUrl(scope), json('POST', data));
}

/** Omitting `apiKey` keeps the stored key, a value replaces it, and `""` clears it. */
export function updateProvider(
  id: string,
  data: UpdateInferenceProviderRequest,
  scope: ProviderScope = 'platform',
): Promise<InferenceProvider> {
  return apiFetch<InferenceProvider>(providerUrl(id, scope), json('PUT', data));
}

/** Refused with 409 while any of its capabilities is a default, and always for the system provider. */
export function deleteProvider(id: string, scope: ProviderScope = 'platform'): Promise<void> {
  return apiFetch<void>(providerUrl(id, scope), json('DELETE'));
}

/**
 * fal.ai only: adds, under this same provider (no key copy), a model for every picture-editor tool
 * that has none yet. Answers the models it created (empty when there was nothing to add).
 */
export function createImageToolModels(id: string, scope: ProviderScope = 'platform'): Promise<InferenceModel[]> {
  return apiFetch<InferenceModel[]>(`${providerUrl(id, scope)}/image-tools`, json('POST'));
}

/** Tests an unsaved (or in-progress-edit) configuration; `providerId` reuses that provider's saved key. */
export function testProviderConfig(
  data: TestInferenceProviderRequest,
  scope: ProviderScope = 'platform',
): Promise<TestInferenceProviderResponse> {
  return apiFetch<TestInferenceProviderResponse>(`${providersUrl(scope)}/test`, json('POST', data));
}

// ---------------------------------------------------------------- models

export function createModel(
  providerId: string,
  data: CreateInferenceModelRequest,
  scope: ProviderScope = 'platform',
): Promise<InferenceModel> {
  return apiFetch<InferenceModel>(`${providerUrl(providerId, scope)}/models`, json('POST', data));
}

export function updateModel(
  providerId: string,
  modelId: string,
  data: UpdateInferenceModelRequest,
  scope: ProviderScope = 'platform',
): Promise<InferenceModel> {
  return apiFetch<InferenceModel>(modelUrl(providerId, modelId, scope), json('PUT', data));
}

export function deleteModel(providerId: string, modelId: string, scope: ProviderScope = 'platform'): Promise<void> {
  return apiFetch<void>(modelUrl(providerId, modelId, scope), json('DELETE'));
}

// ---------------------------------------------------------------- capabilities

export function createCapability(
  providerId: string,
  modelId: string,
  data: CreateModelCapabilityRequest,
  scope: ProviderScope = 'platform',
): Promise<ModelCapability> {
  return apiFetch<ModelCapability>(
    `${modelUrl(providerId, modelId, scope)}/capabilities`,
    json('POST', data),
  );
}

/** `isDefault: true` clears the other default of the same capability in the same scope. */
export function updateCapability(
  providerId: string,
  modelId: string,
  capId: string,
  data: UpdateModelCapabilityRequest,
  scope: ProviderScope = 'platform',
): Promise<ModelCapability> {
  return apiFetch<ModelCapability>(capabilityUrl(providerId, modelId, capId, scope), json('PUT', data));
}

export function deleteCapability(
  providerId: string,
  modelId: string,
  capId: string,
  scope: ProviderScope = 'platform',
): Promise<void> {
  return apiFetch<void>(capabilityUrl(providerId, modelId, capId, scope), json('DELETE'));
}

/** Tests a saved capability; the server records the outcome as its last test. */
export function testCapability(
  providerId: string,
  modelId: string,
  capId: string,
  scope: ProviderScope = 'platform',
): Promise<TestInferenceProviderResponse> {
  return apiFetch<TestInferenceProviderResponse>(
    `${capabilityUrl(providerId, modelId, capId, scope)}/test`,
    json('POST'),
  );
}

// ---------------------------------------------------------------- prices (platform only)

/** Adds a price row; a new row for the same (unit, variant) closes the open one. Platform only. */
export function createPrice(
  providerId: string,
  modelId: string,
  capId: string,
  data: CreatePriceRequest,
): Promise<ModelCapabilityPrice> {
  return apiFetch<ModelCapabilityPrice>(
    `${capabilityUrl(providerId, modelId, capId, 'platform')}/prices`,
    json('POST', data),
  );
}

/** Ends a price at `at` (ISO), leaving that unit unpriced from then on. Platform only. */
export function retirePrice(
  providerId: string,
  modelId: string,
  capId: string,
  priceId: string,
  at: string,
): Promise<ModelCapabilityPrice> {
  return apiFetch<ModelCapabilityPrice>(
    `${capabilityUrl(providerId, modelId, capId, 'platform')}/prices/${encodeURIComponent(priceId)}/retire?at=${encodeURIComponent(at)}`,
    json('POST'),
  );
}

/** Which providers the catalog upgrade merged, and into which survivor. Platform only. */
export function getConsolidationReport(): Promise<ConsolidationReport> {
  return apiFetch<ConsolidationReport>(CONSOLIDATION_REPORT_PATH);
}

// ---------------------------------------------------------------- member-facing reads

/** The selectable list's URL, so the SWR key and the request can never drift apart. */
export function getSelectableProvidersUrl(capability?: InferenceProviderCapability | null): string {
  return capability
    ? `${SELECTABLE_PROVIDERS_PATH}?capability=${encodeURIComponent(capability)}`
    : SELECTABLE_PROVIDERS_PATH;
}

/**
 * The model capabilities the caller's active workspace may choose in a picker: every enabled
 * platform one plus the workspace's own, while its plan allows bring-your-own. Open to every member
 * except Billing, and deliberately minimal — never an endpoint, key or health detail.
 */
export function getSelectableProviders(
  capability?: InferenceProviderCapability | null,
): Promise<SelectableProvider[]> {
  return apiFetch<SelectableProvider[]>(getSelectableProvidersUrl(capability));
}

/**
 * Which provider a run would use for `capability` right now, and whether it is healthy. NOT
 * admin-only — the voiceover step settings show it to every customer — so it carries no endpoint,
 * model or key.
 */
export function getProviderStatus(capability: InferenceProviderCapability): Promise<InferenceProviderStatus> {
  return apiFetch<InferenceProviderStatus>(
    `/api/v1/inference-providers/status?capability=${encodeURIComponent(capability)}`,
  );
}

/** The error code the API answers with a 402 when the workspace's plan does not include BYO providers. */
export const BYO_NOT_IN_PLAN_CODE = 'byo_not_in_plan';

/**
 * True when a write to an ORG provider was refused with `402 byo_not_in_plan`: the workspace's plan
 * does not include bringing its own inference provider. A PLAN LIMIT, not a failure — the caller
 * presents it with the upgrade path rather than as a red error.
 */
export function isByoNotInPlan(error: unknown): boolean {
  return error instanceof ApiError && error.status === 402;
}

/**
 * True when the API refused a provider read with 403: the caller's role may not do this. The page
 * says so instead of rendering a failure the caller cannot fix.
 */
export function isProviderAccessDenied(error: unknown): boolean {
  return error instanceof ApiError && error.status === 403;
}
