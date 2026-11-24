# ADR: Tenancy, organizations and entitlements

- **Status:** Accepted, pending founder confirmation of D4 (see "Founder confirmation needed")
- **Date:** 2026-10-05
- **Work package:** A0 of the SaaS launch plan
- **Decisions covered:** the tenancy model, organization scoping, roles and the data ownership rules below

Every later tenancy work package (A1-A18, and the B, C and D work that reads org data) codes against
this page. Where this page and an area plan disagree, this page wins until it is amended here first.

## Context

ReelBolt today has users and an `is_admin` flag. Projects, custom agents and assistant threads are owned by a
user; inference providers are global and admin-managed. The cloud product needs workspaces, teams, bring-your-own
providers and plan-driven limits, while a self-hosted install must keep working with no configuration.

## Decision

### Organizations

Three kinds, stored as a string (`OrganizationKind`, append-only):

| Kind | Purpose | Members | Rules |
|---|---|---|---|
| `Personal` | A user's own workspace, auto-created with the user | Exactly one (Owner) | No invites, cannot be deleted or left. Unique per creator (partial unique index). |
| `Team` | Shared workspace | Owner, Admin, Member, Billing | At least one Owner always remains. |
| `Platform` | Owns platform-managed providers and platform admins | Platform admins (Owner) | Not a workspace: never offered as an active org, never owns projects. |

Two organizations exist in every deployment, with fixed ids in `WellKnownOrganizations`
(`ReelBolt.Shared.Tenancy`): **Platform** (`5c1f0000-0000-4000-8000-000000000001`, slug `platform`) and
**Default** (`5c1f0000-0000-4000-8000-000000000002`, slug `default`). Default is the bootstrap Team org of a
self-hosted install that admin-invited users join. In `Cloud` mode it is not created and nobody is added to it.

`Tenancy:Mode` = `SelfHosted | Cloud` (Go API: `TENANCY_MODE`), default `SelfHosted`:

| | SelfHosted | Cloud |
|---|---|---|
| Users arrive by | admin invite | self-serve signup (`SIGNUP_ENABLED`) |
| Memberships on creation | Personal (Owner) + Default (Member, or Owner for admins) | Personal (Owner) |
| Entitlements | all unlimited | plan data (B8a) |

### Roles and the permission matrix

Roles are per organization (`OrganizationRole`: Owner, Admin, Member, Billing). Platform authority is **not** a role;
it is the separate `platformAdmin` claim, backed by `application_users.is_admin` and Platform org membership.

| Capability | Owner | Admin | Member | Billing |
|---|---|---|---|---|
| View and edit projects, files, workflows, run executions, use the assistant | yes | yes | yes | no |
| Create a project | yes | yes | yes | no |
| Delete a project | yes | yes | creator only | no |
| Custom agents: create, edit, delete | yes | yes | yes | no |
| Assign skills to org custom agents | yes | yes | no | no |
| Members: list, change role, remove | yes | yes | no (list only) | no |
| Invites: create, list, revoke | yes | yes | no | no |
| Org providers (BYO): create, edit, delete, test | yes | yes | no | no |
| Select a provider in a step editor (read-only selectable list) | yes | yes | yes | no |
| Billing pages: plan, invoices, top-ups | yes | no | no | yes |
| Rename the org | yes | yes | no | no |
| Delete the org / transfer ownership | yes | no | no | no |

Notes:

- Admin cannot remove or demote an Owner; the last Owner cannot be removed or demoted by anyone.
- Billing sees billing pages only. Every project, file, agent, provider and execution route denies it.
- Personal orgs: one Owner, no invites, no leave, no delete. Billing pages are the Owner's.
- Platform-managed providers (organization id null) and platform-wide views require `platformAdmin`; being an org Owner never grants it.
- Role is read from the database membership row on each request (A3a, A6), not trusted from the token.

### Error rule (D5)

- A resource in **another** organization (or one that does not exist) returns **404**. Existence never leaks.
- A resource in the **caller's** organization where the role is too low returns **403**.
- The assistant keeps its "absent, never rejected" behaviour: a project outside the active org is treated as absent.

### JWT contract (D6)

The Go API stays the sole issuer (HS256). Claims:

| Claim | Type | Meaning |
|---|---|---|
| `sub`, `email` | as today | |
| `org` | uuid | Active organization. |
| `orgRole` | string | Role in `org` at issue time; consumers re-read the database. |
| `platformAdmin` | bool | Platform authority. |
| `isAdmin` | bool | **Alias** of `platformAdmin`, emitted for one release (MCP verifier and web cookie read it). Then removed. |

Switching org re-issues the token (`POST /api/v1/auth/switch-org`). Engine on-behalf-of tokens carry `org` and
`orgRole` for the execution's initiating user, **never** `platformAdmin`; if the initiator is no longer a member,
no token is minted (fail closed).

### Provider ownership (D3)

`inference_providers.organization_id = null` means platform-managed ("ReelBolt Inference"). Org rows are BYO.
Resolution order: step explicit id (must belong to the org or the platform) -> agent override -> org default per
capability -> platform default -> legacy config. Org rows may not use the `env:` sentinel or PersonalOAuthMode,
and are dropped from resolution when the org is not entitled to BYO. Provider keys never leave the server.

### Entitlements (R3)

There is exactly **one** entitlement contract: `IOrganizationEntitlements` in `ReelBolt.Shared.Tenancy`. Tenancy
(A9a, A12), billing (B8a) and compute (D6) all read it; B8a's `IEntitlementService` and D6's
`IComputeEntitlements` are not created. It returns an immutable `OrganizationEntitlementSet`:

| Field | Type | Trial (indicative) | Used by |
|---|---|---|---|
| `Tier` | string (catalog plan id; `selfhost` when billing off) | `trial` | everywhere |
| `ByoAllowed` | bool | false | A9a, A9b |
| `MaxConcurrentExecutions` | int | small | A12 fair-share |
| `MaxOutputHeight` | int | 720 | B8a, the compile encode and the Remotion render |
| `Watermark` | bool | true | B8a, the compile encode and the Remotion render |
| `RoomsAllowed` | bool | false | B8a, workflow save and run |
| `VideoGenAllowed` | bool | false | B8a |
| `Priority` | int | 0 | B8a, admission |
| `StorageGb` | int | small | B8a |
| `RunnerAllowed` | bool | per plan | D6 |

"No limit" is `OrganizationEntitlementSet.UnlimitedInt` / `UnlimitedCredits`; Video spend has no plan cap (only `VideoGenAllowed` decides who may generate). Convenience
extensions cover the A0 names (`AllowsByoProvidersAsync`, `MaxConcurrentExecutionsAsync`,
`TierAsync`).

Implementations: A0 ships `ConfigOrganizationEntitlements`, which returns `Tenancy:DefaultEntitlements` for every
org (all-allowed by default, which equals self-host). B8a replaces it with plan data. With `Billing:Enabled=false`
the result is always `Unlimited`. Callers decide per call and do not cache beyond a request or step, so a downgrade
applies on the next read.

### Compute preference (R4)

`organizations.compute_preference`, added by A1: `ComputePreference` = `Auto`, `CloudOnly`, `PreferRunner`,
`RunnerOnly`; stored as a string, append-only, default `Auto`. `Auto` behaves as `CloudOnly` until a runner is
paired. Whether runners may be used at all is the entitlement `RunnerAllowed`; the preference is the org's choice
within it. D6's selector reads both through `IOrganizationEntitlements` and the org row; E8-UI needs no migration.

### Backfill (D4, default)

Performed by the A1 migration in raw SQL:

1. Insert Platform and Default orgs with the fixed ids.
2. Every user gets a Personal org (Owner). In SelfHosted mode every user also joins Default (admins Owner, others Member).
3. Users with `is_admin` become Owners of Platform.
4. Each existing project moves to its owner's Personal org; each custom agent to its owner's Personal org (an agent
   with no owner goes to Default); each assistant thread to its user's Personal org.
5. Existing inference providers keep `organization_id = null`: they become platform-managed.
6. `last_active_organization_id` is set to the user's Personal org.

The `is_admin` column stays and is documented as "platform admin"; it is not renamed.

## Founder confirmation needed

**D4 (the gate for this ADR).** The ADR is written assuming D4 as stated above. Please confirm or choose another:

| Option | Effect | Consequence |
|---|---|---|
| **D4 as stated (assumed):** projects and custom agents to the owner's Personal org; providers platform-managed; admins become Platform Owners | Nobody loses access; each user sees exactly their own data | On a self-host install that previously shared everything through one admin, non-admin users no longer see each other's projects unless moved into Default. Admins' projects sit in their Personal orgs, not Default. |
| Everything to Default (self-host-friendly) | Projects, agents and providers go to the Default team org | Preserves shared visibility on self-host; but cloud users would start with team data and no clean Personal split, and a user's old projects become visible to everyone in Default. Wrong for any deployment that already has more than one real user's data. |
| Providers to Default org instead of platform-managed | Providers become BYO of Default | Providers disappear from Personal/Team orgs and from the platform default; cloud would have no platform inference until re-created. Only sensible if the existing providers are someone's private keys. |

Other choices this WP had to make, each overridable:

1. **Platform org membership = `is_admin` users, role Owner.** Alternative: a distinct "PlatformAdmin" role. Consequence: a new role later means a migration of the Platform memberships and a change to the matrix above; chosen the simple route since platform authority is already the `platformAdmin` claim.
2. **Billing role is denied all project access** (billing pages only), as the plan specifies. Alternative: read-only access to projects; this would need a new row in the matrix and a read/write split in `ProjectPermission`.
3. **Member may delete only projects they created; Admin+ may delete any.** Alternative: Admin+ only, which is safer for teams but frustrates solo members of small teams.
4. **Default org exists only in SelfHosted mode.** Alternative: always create it. Consequence: a cloud deployment would carry a dead org that backfills and tooling must special-case.
5. **`ComputePreference.Auto` is a distinct member** (behaves as CloudOnly until a runner pairs) rather than defaulting to `CloudOnly`, so pairing a runner can start using it without editing the setting.
6. **Entitlement "no limit" is a sentinel (`int.MaxValue`/`decimal.MaxValue`), not null**, keeping comparisons branch-free. Alternative: nullable limits; every consumer then handles null.
7. **Entitlements are async (`ValueTask`)** because the plan-backed B8a implementation reads the database. Alternative: a synchronous snapshot; faster for hot paths but stale after a downgrade.

## Consequences

- A1 (migration) and A3a (Go claims) can start from this contract; the enums and ids live in `ReelBolt.Shared.Tenancy`.
- `ICurrentUser.IsAdmin` becomes obsolete in A6; it stays one release as `IsPlatformAdmin`.
- The Go API needs the same two GUIDs and enum strings; A3a mirrors them in Go constants.
- Adding enum members later needs no migration (stored as strings), but renaming one breaks stored rows.

## Code introduced by A0

`inference/src/ReelBolt.Shared/Tenancy/`: `WellKnownOrganizations`, `OrganizationKind`, `OrganizationRole`,
`ComputePreference`, `TenancyMode` + `TenancyOptions`, `IOrganizationEntitlements` + extensions,
`OrganizationEntitlementSet`, `ConfigOrganizationEntitlements`. Nothing is registered in DI or referenced by a
service yet.
