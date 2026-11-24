/**
 * Wire shapes of the runner API (WP E4b), mirrored from the Go DTOs in
 * api/handlers/runners/dto.go. Go's json tags are camelCase, so the field names below are exactly
 * what the API returns, and every optional field is one the server OMITS rather than sends as
 * null/empty (`omitempty`).
 *
 * There is no type for the device's side of the flow (pair/start and pair/poll): the browser never
 * calls those. They are the runner daemon's endpoints, documented in docs/runner-protocol.md.
 */

/**
 * One paired desktop machine. Mirrors `dto.DeviceResponse`.
 *
 * `GET /api/v1/runners` only ever returns ACTIVE devices — the store filters
 * `revoked_at IS NULL` — so `revokedAt` is always absent here. Revoking is terminal for the row:
 * the list cannot show a revoked device, and re-pairing the same machine creates a new one.
 */
export interface RunnerDevice {
  id: string;
  name: string;
  /** `windows` or `linux`. Decision D26's set; macOS is not a runner target. */
  os?: string;
  /** `amd64` or `arm64`. */
  arch?: string;
  runnerVersion?: string;
  createdAt: string;
  /**
   * When a gateway last heard from this device, refreshed at most once a minute while it is
   * connected. Absent when the device has never opened a connection. The column is NOT cleared
   * when the device disconnects, so a stale value is the normal state for an offline runner —
   * see `runnerPresence` for the rule that turns it into an online dot.
   */
  lastSeenAt?: string;
  /** Present in the DTO; never present on this endpoint's rows. */
  revokedAt?: string;
  /**
   * The account that APPROVED the pairing, as a uuid. The API returns no email or display name
   * here, so the list resolves it through the workspace's members when the caller may read them
   * (`canViewMembers`) and otherwise shows the id's short form.
   */
  pairedByUserId?: string;
  /**
   * The runner's own `hello.capabilities` blob, relayed verbatim by the API — which validates only
   * that it is JSON, and drops it when it is not (`capabilitiesOf` in api/handlers/runners/runners.go).
   *
   * Typed `unknown` deliberately. It is UNTRUSTED, self-reported data written by a device we have
   * never verified, so every reader must narrow it rather than assume a shape: see
   * `parseRunnerCapabilities` in lib/utils/runners.ts.
   */
  capabilities?: unknown;
}

/**
 * The pairing request the approval screen shows. Mirrors `dto.PairApprovalResponse`.
 *
 * It deliberately carries nothing about which workspace the device would land in: the approver
 * chooses that, and the choice is checked against their own memberships at approval time.
 */
export interface RunnerPairing {
  /** The code as the user reads it off the device's screen: XXXX-XXXX. */
  userCode: string;
  /** The device's self-reported machine name. */
  name: string;
  os?: string;
  arch?: string;
  version?: string;
  /**
   * The address the API saw the pairing request come from. There is NO geolocation behind this:
   * coarse geolocation was not implemented (it would need a GeoIP database or a vendor lookup that
   * this deployment does not ship), so the UI shows the address and claims no city or country.
   */
  requestedFromIp?: string;
  requestedAt: string;
  expiresAt: string;
}

/** The workspace the device was approved into. Mirrors `dto.ApprovePairResponse`. */
export interface ApprovePairingResponse {
  deviceId: string;
  organizationId: string;
}

/**
 * What `DELETE /api/v1/runners/{id}` answers. Mirrors `dto.RevokeResponse`.
 * Revoking twice is not an error; `alreadyRevoked` says whether this call was the one that did it.
 */
export interface RevokeRunnerResponse {
  id: string;
  revokedAt: string;
  alreadyRevoked: boolean;
}

/** One paired desktop app that shares a local model (`GET /api/v1/org/runner-llm`). */
export interface RunnerLlmDevice {
  deviceId: string;
  name: string;
  online: boolean;
  enabled: boolean;
  serverKind: string;
  /** Model names the computer reports. */
  models: string[];
}

export interface RunnerLlmResponse {
  devices: RunnerLlmDevice[];
}
