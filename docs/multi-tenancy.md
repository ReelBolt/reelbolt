# Multi-tenancy (organizations, roles and the tenant boundary)

ReelBolt's cloud product is multi-tenant: every piece of data a user works on — a project, a file, a
custom agent, an assistant thread, an inference provider — belongs to exactly one **organization**,
and every request in ReelBolt is scoped to one of them. This document is the developer reference for
that tenancy model. It covers the three organization kinds (Personal, Team, Platform) and what each
means, the four member roles (Owner, Admin, Member, Billing) and what each may do, the `org` /
`orgRole` / `platformAdmin` JWT claims and the one-release `isAdmin` alias, how the active
organization is chosen at login and switched at will, the project-access rule (404 across
organizations, 403 inside one with a role that is too low, and none at all for the Billing role), the
EF Core tenant query filters that enforce this and the system/organization scopes a background job in
ReelBolt must open, the inference-provider resolution order and what "platform-managed" means against
an organization's own bring-your-own providers, and the self-hosted "Default" organization that
preserves the pre-SaaS single-workspace behaviour.

The tenancy model is fixed by the ADR `docs/design/tenancy.md` (work package A0) and by the product
the tenancy decisions recorded in the design notes. This page describes the code that implements
that record: the shared contracts in `inference/src/ReelBolt.Shared/Tenancy/`, the Go organizations
API in `api/handlers/orgs/` and `api/services/`, the claim extraction and membership re-check in
`inference/src/ReelBolt.Inference.Api/Services/Auth/`, and the tenant filters in
`inference/src/ReelBolt.Inference.Api/Data/InferenceApiDbContext.cs`. Where this page and the ADR
disagree, the ADR wins until it is amended first — but where the **shipped code** is narrower or
looser than the ADR's prose, this page follows the code and says so. Three such places are called out
in their own sections: renaming a Personal workspace is allowed (section "Roles and the permission
matrix"), the `Default` organization row is created by the migration on every deployment, cloud
included (section "The self-hosted Default organization"), and only eight tables carry the tenant
query filter (section "Tenant query filters and the scopes background jobs open").

## Organization kinds

ReelBolt has exactly three kinds of organization, modelled by the `OrganizationKind` enum in
`inference/src/ReelBolt.Shared/Tenancy/OrganizationKind.cs`. Kinds are persisted as strings
(`jsonb`-free: EF's `HasConversion<string>()` on `organizations.kind` stores the member name), so
adding a kind later needs no migration, but renaming one breaks stored rows — that is why the members
are append-only.

| Kind | What it is | Membership | Rules |
|---|---|---|---|
| `Personal` | A single user's own workspace, **auto-created with the user** (decision D1). This is where a user's own projects, custom agents and assistant threads live. | Exactly one member: the creator, as `Owner`. | No invites can be created, a member cannot leave, and it cannot be deleted. There is one `Personal` workspace per user, enforced by a partial unique index on `organizations.created_by_user_id` filtered to `kind = 'Personal'` (see the "Tenant query filters and the scopes background jobs open" section for where that index is declared). |
| `Team` | A shared workspace that a user creates for collaborators. This is the kind of organization that holds members with distinct roles. | Any number of members, each with one of the four roles (see the "Roles and the permission matrix" section). | At least one `Owner` always remains (the last-Owner rule, see that section). A `Team` is the kind that can be invited into, left and deleted through the organizations API. |
| `Platform` | **The platform itself, not a workspace.** It owns the platform-managed inference providers (the "ReelBolt Inference" rows, see the "Inference provider resolution" section) and the platform administrators. | Platform admins, as `Owner`. | Nobody *works in* it: it is never offered as an active organization, never owns a project, and cannot be made anyone's active org (see "The active organization: selection and switching"). |

Two organizations exist in **every** ReelBolt deployment, with fixed identifiers held in
`inference/src/ReelBolt.Shared/Tenancy/WellKnownOrganizations.cs`. They are C# constants rather than
generated values so that the A1 backfill migration, the Go API, the seeders and the tests all agree
on them without a lookup, and the class comment is explicit that a value must never change because
rows reference them by id:

| Constant | Value | Slug |
|---|---|---|
| `WellKnownOrganizations.PlatformId` | `5c1f0000-0000-4000-8000-000000000001` | `platform` |
| `WellKnownOrganizations.DefaultId` | `5c1f0000-0000-4000-8000-000000000002` | `default` |

`PlatformId` is the `Platform` kind; `DefaultId` is a `Team` kind — the bootstrap team org of a
self-hosted ReelBolt install (covered in the "The self-hosted Default organization" section). The
helper `WellKnownOrganizations.IsWellKnown(id)` reports whether an id is one of the two. Both rows are
inserted by the raw SQL of the A1 migration (`Migrations/OrganizationBackfill.cs`), which runs on
every deployment; the Go API mirrors the same two ids as `models.PlatformOrgID` and
`models.DefaultOrgID` in `api/models/organization.go` rather than looking them up.

## Roles and the permission matrix

Roles in ReelBolt are **per organization** and modelled by the `OrganizationRole` enum in
`inference/src/ReelBolt.Shared/Tenancy/OrganizationRole.cs` (also persisted as a string, append-only).
There are four, in descending order of authority:

- **Owner** — everything, including deleting the organization, transferring ownership and managing
  billing. Every organization keeps at least one.
- **Admin** — members, invites, the organization's own (bring-your-own) providers, and project
  deletion; but no ownership transfer and no org deletion.
- **Member** — creates and works inside projects; cannot manage people or providers.
- **Billing** — the billing pages only. No access to projects, files, agents or providers at all.

The matrix below is the one in `docs/design/tenancy.md`, reproduced here because it is the contract
every code path in ReelBolt is written against. Two of its rows are enforced by named code rather than
by convention: the Go organizations API (`api/handlers/orgs/orgs.go`) enforces the membership rows,
and `ProjectAccessService` (see the "Project access" section) enforces the project rows.

| Capability | Owner | Admin | Member | Billing |
|---|---|---|---|---|
| View and edit projects, files, workflows, run executions, use the assistant | yes | yes | yes | no |
| Create a project | yes | yes | yes | no |
| Delete a project | yes | yes | creator only | no |
| Custom agents: create, edit, delete | yes | yes | yes | no |
| Assign skills to org custom agents | yes | yes | no | no |
| Members: list, change role, remove | yes | yes | list only | no |
| Invites: create, list, revoke | yes | yes | no | no |
| Organization providers (BYO): create, edit, delete, test | yes | yes | no | no |
| Select a provider in a step editor (read-only selectable list) | yes | yes | yes | no |
| Billing pages: plan, invoices, top-ups | yes | no | no | yes |
| Rename the organization | yes | yes | no | no |
| Delete the organization / transfer ownership | yes | no | no | no |

Several rules cut across the matrix and are enforced in the Go organizations API
(`api/handlers/orgs/orgs.go`, the pure role helpers at the top of the file, directly unit-tested in
`orgs_test.go`):

- **Admins can never create or touch Owners.** The `CanAssign(actor, current, role)` helper returns
  true for an `Owner` actor unconditionally, but for any other actor it requires that *neither* the
  target role nor the member's current role is `Owner`. This is what makes "an Admin cannot remove or
  demote an Owner" true.
- **The last Owner cannot be removed or demoted by anyone.** The handler checks this up front
  (`guardLastOwner`, a `409 Conflict`), and the store re-checks it atomically inside its
  transaction: `GormOrgStore.SetMemberRole` and `RemoveMember` count Owners while holding row locks
  (`FOR UPDATE`, in `lockedOwnerCount` in `api/services/org_store.go`) and return `ErrLastOwner`
  when the count would drop to zero, so two concurrent demotions serialize and the loser sees the true
  count.
- **Anyone may leave the organization they are in.** `removeMember` lets a caller remove *themselves*
  without `CanManageMembers`, so a Member or a Billing user can leave a Team even though neither may
  manage members. The last-Owner rule still applies, and a Personal workspace has its own refusal.
- **Billing is denied everything except billing.** The project-access code in ReelBolt models this
  with a default-deny fallthrough (see the "Project access" section): `Billing`, and any role added
  in the future, is denied until the matrix explicitly says otherwise.
- **A Personal workspace is one Owner and nothing else.** It cannot be invited into, left, or deleted;
  the organizations API answers `400` with a message naming the reason ("a personal workspace cannot
  be deleted", "...cannot be left or have members removed", "...has a single owner", "...cannot have
  invitations"). The one operation the Personal guard does **not** cover is rename: `PATCH
  /api/v1/orgs/{id}` checks only that the caller may manage members, and the Owner of a Personal
  workspace is such a caller, so its name can be changed through the API.
- **Platform authority is not a role.** Being an organization's `Owner` never grants
  `platformAdmin`; that is a separate claim backed by `application_users.is_admin` and Platform-org
  membership (see the "JWT claims and the one-release isAdmin alias" section). Platform-managed
  inference providers are managed only by a platform admin.

Two more invariants are worth stating, because they are the reason a role is not trusted from the
token:

- **The role a request carries is re-read from the database, not trusted from the JWT.** The
  Inference API's `MembershipValidator` (`Services/Auth/MembershipValidator.cs`, registered as the
  JwtBearer `OnTokenValidated` hook) looks up the caller's membership in the token's `org`, cached
  for 30 seconds, and *replaces* the token's `orgRole` / `platformAdmin` / `isAdmin` claims on the
  principal with the database truth. A caller whose membership is gone is rejected outright
  (`"User is not a member of the token's organization."`); a member demoted after their token was
  issued is downgraded within the cache TTL, and a `platformAdmin` claim is honoured only while
  `is_admin` still agrees. The Go API side runs the same lookup behind the same 30-second cache
  (`MembershipCacheTTL` in `api/middleware/auth.go`) and answers `401 org_membership_revoked` when
  the membership has gone; both sides evict the cached entry the moment a role changes or a member
  leaves, so those take effect immediately.
- **The Go organizations API judges authority in the target org, not the active org.** A handler
  resolves the caller's membership in the organization named by the `{id}` path segment
  (`(*Handler).scope` → `orgScope`), so a user can manage any organization they belong to, not only
  the one their token is currently scoped to.

## JWT claims and the one-release isAdmin alias

The Go API remains the sole issuer of ReelBolt's JWT (HS256, 24-hour expiry). With tenancy it carries
a set of claims defined by `TokenClaims` in `api/services/jwt_service.go`:

| Claim | Type | Meaning |
|---|---|---|
| `sub`, `email` | string | The user, as before. |
| `org` | uuid (string) | The **active** organization. Emitted only when the token was issued for a membership (`omitempty`); a nil membership yields a legacy-shaped token with no org claims. |
| `orgRole` | string | The caller's role in `org` **at issue time**. Consumers are expected to re-read the database; this is a hint, not authority (see the "Roles and the permission matrix" section). |
| `platformAdmin` | bool | Platform-wide authority, backed by `application_users.is_admin`. |
| `isAdmin` | bool | **An alias of `platformAdmin`**, emitted for one release for consumers that have not moved over yet (decision D6). The two are always set to the same value in `GenerateToken`. |
| `mustChangePassword`, `emailVerified` | bool | As before. |

Two consumers read the alias, and each reads it in one place:

- The .NET services read these claims only through `ReelBolt.Shared.Auth.AuthClaims`.
  `AuthClaims.ReadPlatformAdmin` reads `platformAdmin` **and falls back to the `isAdmin` alias** —
  that is where the "one-release alias" is implemented, in one spot, so the alias rule is not
  scattered across services. `ReadOrganizationId` and `ReadOrgRole` parse the `org` and `orgRole`
  claims (an unparseable or undefined role simply reads as absent).
- The Go auth middleware ORs the two into `UserContext.PlatformAdmin`
  (`claims.PlatformAdmin || claims.IsAdmin`), for the same legacy-token reason.
- The **MCP server** is no longer a consumer. It does not verify or parse the token at all — the
  verifier that did (`mcp/src/auth/verifyReelBoltJwt.ts`) has been deleted. It forwards the caller's
  bearer token to the Inference API unchanged, and the API's own `AddJwtBearer` +
  `MembershipValidator` path (above) is the only thing that reads a claim or a role from it. So the
  legacy alias has exactly the two readers listed above, and the MCP layer has no say in authority.

Two consequences of the claim design are load-bearing:

- **The claims are only a hint.** `org` / `orgRole` / `platformAdmin` are re-validated against the
  database by the `MembershipValidator` (Inference API) and the membership cache (Go API) before the
  request is served (see the "Roles and the permission matrix" section). A caller with a stale or
  forged role claim is rejected or downgraded, so the security boundary is the database membership,
  not the token.
- **Engine on-behalf-of tokens never carry `platformAdmin`.** ReelBolt's WorkflowEngine mints
  short-lived HS256 tokens (five minutes by default) on behalf of an execution's initiating user for
  its calls to the Inference API, through `OnBehalfOfTokenIssuer` in
  `inference/src/ReelBolt.Shared/Auth/`. Those tokens carry `org` and `orgRole` for the initiator,
  plus `act=workflow-engine` and `executionId`, and deliberately no `platformAdmin` (and no
  `isAdmin` alias) at all. The issuer's comments state the contract: an engine call acts with at most
  the initiator's organization authority, never platform authority.

## The active organization: selection and switching

A user belongs to several ReelBolt organizations (their own `Personal` workspace plus any `Team`s
they have been added to), but a JWT is scoped to **one active organization at a time**. This section
covers how that active org is chosen at login and how it is switched.

**Choosing at login.** When a user logs in, the Go API's `ResolveActiveMembership`
(`api/services/org_service.go`) picks the organization the session should land in, in this order:

1. The user's **last active organization**, *if they are still a member of it* and it is not the
   `Platform` org. The last-active id is stored on the user row
   (`application_users.last_active_organization_id`) and is written by every switch (below).
2. Otherwise their **`Personal` workspace** — the query orders `Personal` first, then oldest
   membership, so the choice is stable when more than one candidate remains.
3. Otherwise **any other** non-`Platform` workspace they belong to, chosen as the oldest membership.

The `Platform` org is never a workspace and is never returned here. If the user belongs to no
organization at all, login fails with `403` ("no workspace available for this account"). The chosen
membership's `org` and `orgRole` are written into the token and the session cookie.

**Switching at will.** `POST /api/v1/auth/switch-org` takes `{ "organizationId": "<uuid>" }` and
re-issues the whole session for that organization. It looks up the caller's membership in the named
org; a non-member, an unknown id, **and the `Platform` org all return `404`** — never `403` — so an
attacker cannot use the endpoint to learn that an organization exists (decision D5: existence never
leaks). On success it writes the new id to `last_active_organization_id` and re-issues the token and
cookies through the same `writeSession` path login uses.

**Accepting an invite switches too.** When a signed-in user accepts an invitation for a `Team`
organization, the accept handler (`api/handlers/auth/invitations.go`) creates the membership and then
immediately re-issues the session for that organization (setting the last-active id and the cookies),
so the client needs no separate `switch-org` call. A plain `POST /api/v1/orgs/invites/accept` in the
organizations API does the same join but does *not* switch: its handler comment says the client calls
`switch-org` next if it wants to land in the new org.

**What the UI reads.** The session is also carried in a readable, non-`httpOnly` `reelbolt_user`
cookie holding `{ email, isAdmin, isPlatformAdmin, mustChangePassword, emailVerified, org, orgName,
orgKind, orgRole }`. That cookie is UI state only — it is never trusted for authority, which always
comes from the validated token re-checked against the database.

## Project access: 404 across organizations, 403 for an under-privileged role, none for Billing

Project access is the concrete enforcement point of the tenancy model in ReelBolt, implemented by
`ProjectAccessService`
(`inference/src/ReelBolt.Inference.Api/Services/Auth/ProjectAccessService.cs`). The rule it applies is
the one decision D5 fixed:

- **A project in another organization — or one that does not exist — returns `404`.** Existence
  never leaks: a caller gets the same answer for "not mine" and "not there".
- **A project in the caller's own organization, where the caller's role is too low, returns `403`.**
- **The `Billing` role has no project access at all**, and neither does any role added in the
  future: the pure `Allowed` matrix falls through to `false` for them until it explicitly says
  otherwise.
- **A caller with no active organization at all gets `404`, not `403`**: both entry points return
  `NotFound` when `ICurrentUser.OrganizationId` or `OrgRole` is absent.

The service expresses the role rule as a small pure function,
`ProjectAccessService.Allowed(role, permission, isCreator)`, which is unit-tested directly in
`ProjectAccessServiceTests`. `ProjectPermission` has exactly three members — `View`, `Edit` and
`Delete`; there is no separate "create" permission, and creating a project is judged through
`AuthorizeOrganization(Edit)` on the organization as a whole:

| Caller's role | `View` and `Edit` | `Delete` |
|---|---|---|
| `Owner`, `Admin` | allowed | allowed |
| `Member` | allowed | allowed **only if they are the creator** (`project.OwnerId == caller`) |
| `Billing` (and any future role) | denied | denied |

Two enforcement layers work together, and both are in the code:

- **The tenant query filter** (see the next section) already scopes the project lookup to the active
  organization, so a foreign project simply does not appear in the query result.
- **An explicit second check** in `Evaluate` compares `project.OrganizationId` to the caller's
  `OrganizationId` as an independent defence: if the two do not match, the result is `NotFound`
  regardless of what the filter returned. The in-code comment calls this out deliberately — the query
  filter is the first enforcement and the explicit comparison is the second, independent one.

The same `404`-for-absent rule is why the platform assistant treats a project outside the caller's
active organization as **absent rather than rejected**: it never turns a foreign project into a `403`
it could use to probe existence. `IProjectAccessService` also states that platform authority is
deliberately not consulted here: a platform admin sees only their active organization's projects.

## Tenant query filters and the scopes background jobs open

The mechanism that makes project access true for the ReelBolt Inference API's tables is a **named EF
Core query filter** applied in `InferenceApiDbContext`
(`inference/src/ReelBolt.Inference.Api/Data/InferenceApiDbContext.cs`). It is named `"Tenant"`
(`TenantFilterName`) so that it coexists with the context's soft-delete filters, and it is driven by
the `ITenantContext` that DI always supplies — so every request and background path is filtered, and
it **fails closed** when there is neither a request org nor a system scope.

The filter reads two members of the context instance, which EF re-evaluates **per query**, so a query
follows whichever scope is current when it runs:

- `TenantUnfiltered` — true when the context has no tenant accessor at all (a hand-built context in
  design-time tooling or a non-tenancy unit test, where there is nothing to scope by) **or** when the
  current scope is a **system** scope.
- `TenantOrgId` — the active organization id from the current scope, or `null`.

The per-table predicates are:

| Table | Predicate | Effect |
|---|---|---|
| `projects` | `TenantUnfiltered \|\| (TenantOrgId != null && e.OrganizationId == TenantOrgId)` | A project is visible only to the org that owns it. |
| `project_files` | `… e.Project.OrganizationId == TenantOrgId` | A file is visible exactly when its project is; written out (not via the Project filter) so the predicate stays a single translatable expression. |
| `agent_definitions` | `… e.IsBuiltIn \|\| (TenantOrgId != null && e.OrganizationId == TenantOrgId)` | **Built-ins are shared** (no org); every custom agent belongs to one org and is visible only to it. A check constraint (`ck_agent_definitions_builtin_or_org`) keeps the two halves apart. |
| `inference_providers`, `inference_models`, `model_capabilities` | `… e.OrganizationId == null \|\| (TenantOrgId != null && e.OrganizationId == TenantOrgId)` | **Platform-managed rows (null org) are visible to every tenant**; a BYO row is visible only to its own org. A model and a capability carry their provider's org, kept equal by the admin service. `model_capability_prices` and `pricing_settings` carry no org and no filter: prices are reached only through platform-admin endpoints. |
| `assistant_threads` | `… e.OrganizationId == TenantOrgId` | Threads are org-scoped (the assistant's "absent, never rejected" rule for foreign projects is a separate, handler-level decision). |
| `storage_retention_marks` | `… e.OrganizationId == TenantOrgId` | A retention mark is org-scoped (work package F10 added the table with the same named filter). |

**Not every owned table carries the filter, and that is by design rather than by omission.** The eight
above are the ones a caller can reach by an id of their own choosing. `assistant_messages`,
`project_voices`, `editing_styles`, `edit_timelines` and `media_derivatives` instead hang off a
thread, a project or an owning user, and the handler authorizes the parent first (through
`IProjectAccessService` or an owner-scoped query) and then reads the child. `editing_styles` is
owner-scoped rather than org-scoped, which is why it carries no tenant filter; `edit_timelines` and
`media_derivatives` carry only the soft-delete filter. A new table that a caller can address directly
should carry the tenant filter; one reached only through an authorized parent is guarded at the
parent.

The catalog carries the indexes that make BYO safe at the schema level: on `inference_providers` a
composite unique index on `(organization_id, name)` with `AreNullsDistinct(false)`, so provider names
are unique per owning org and two platform rows (both null org) still cannot share a name; the same
on `inference_models (organization_id, display_name)`; and on `model_capabilities` a composite unique
index on `(organization_id, capability)` filtered to `is_default`, so at most one default exists
**per owning org and per capability**. There is also a partial unique index on
`organizations.created_by_user_id` filtered to `kind = 'Personal'`, which is what enforces one
Personal workspace per creator, and a unique index on `organizations.slug`.

**Background jobs have no HTTP request, so they must open a scope explicitly.** `ITenantContext`
(`inference/src/ReelBolt.Shared/Tenancy/ITenantContext.cs`) exposes two:

- `BeginSystemScope()` — marks the current async flow as **platform-internal** (migrations, seeding,
  cleanup, the daily storage sampler). Inside it `TenantUnfiltered` is true, so the work deliberately
  spans every organization.
- `BeginOrganizationScope(organizationId)` — runs the current async flow **as a specific
  organization**, for a background job that belongs to one org's data.

The Inference API's `TenantContext` (`Services/Auth/TenantContext.cs`) implements this with an
`AsyncLocal` scope that **always wins over the request token** — an explicit scope overrides what the
token says, and disposing it restores the previous scope. The nullable convenience extensions
`TenantScopes.System()` and `.Organization(orgId)` (`Services/Tenancy/TenantScopes.cs`) make it safe
to call these from a background path that might (in a bare test container) have no `ITenantContext`
registered at all: they return a no-op disposable rather than throwing. Production always registers a
`TenantContext` (`services.AddSingleton<ITenantContext, TenantContext>()` in
`InferenceApiServiceCollectionExtensions`), so a background job in ReelBolt that opens neither scope
is filtered to **nothing** — the fail-closed behaviour the interface documents. `FileSummarizationService`,
`StartupCleanupService`, `ProjectVoiceRetentionService`, the storage sampler and the seeder are the
in-product examples of a background path opening `.System()`.

## Inference provider resolution: platform-managed vs bring-your-own

ReelBolt's inference providers come in two ownership classes, distinguished by the
`inference_providers.organization_id` column:

- **Platform-managed** — `organization_id = NULL`. These are the "ReelBolt Inference" rows the
  platform operator configures; they are visible to every organization (see the tenant filter above)
  and are the default path for every capability. The A1 backfill made every provider that existed
  before tenancy platform-managed (decision D4). They are managed only by a platform admin, through
  `/api/v1/inference-providers`.
- **Bring-your-own (BYO)** — `organization_id` set to an organization. The org registered the
  provider with its own key. BYO rows are visible only to their own org, and an organization may use
  them only when its plan allows it (the `ByoAllowed` entitlement; ReelBolt's Trial plan sets it
  `false`, so a Trial org's own rows stay in the database but are dropped from resolution and the
  organization falls back to the platform-managed providers). BYO rows are managed by the org's Owners
  and Admins through
  `/api/v1/org/inference-providers` (note the singular `org`: nginx routes the plural
  `/api/v1/orgs/…` to the Go API and everything else under `/api/v1/` to the Inference API, so the
  two never collide). A row in another organization, or a platform-managed row, is `404` there and
  never `403`; a Member or Billing caller in the right org gets `403`; creating, editing or testing
  one without the BYO entitlement is `402 byo_not_in_plan`. Listing, reading and deleting stay open
  so a downgraded org can still clean up its rows.

Two rules apply to an org row but not to a platform row, both enforced by
`Services/Inference/InferenceProviderAdminService.cs` at the boundary where the row is written
(decision D3): an org row may **not** store the literal `env:` ambient-credential sentinel (a tenant
could otherwise aim the endpoint at its own server and capture the deployment's credential), and it may
**not** set `PersonalOAuthMode` ("Dev/OAuth (Personal use only)"). An org row also loses the
private-address exemption, so `OpenJev`, `FishAudio` and `OpenAICompatible` rows may sit at a
loopback or RFC1918 address only for the platform, where the operator and the target are the same
party.

The resolution itself lives in `InferenceProviderResolver`
(`inference/src/ReelBolt.Shared/Inference/InferenceProviderResolver.cs`), a TTL-cached (default 60 s,
`Inference:ProviderCacheSeconds`) resolver that loads, per organization, a **snapshot** holding that
organization's visible rows — the platform rows (null org) plus its own. An organization's snapshot can
therefore never observe another organization's providers, which is the core isolation guarantee.

**Chat resolution** (`ResolveAsync`) walks this order, first match wins:

1. **The agent's per-definition override** — the `inferenceProviderId` stored on the specific
   `AgentDefinition` being run (a solo `Agent` step passes its definition id).
2. **The organization's override for a built-in agent type** — `organization_agent_provider_overrides`.
   Built-in agent definitions are shared rows that an org cannot write its choice onto, so an org
   keeps its per-type choice in this table instead. This beats the platform-wide built-in override.
3. **The platform-wide built-in override** for the running agent type — the override stored on the
   shared built-in definition. This is what makes "set `VideoStoryEditor` to model X" mean the same
   thing everywhere, including a room step whose seats carry no per-seat definition id.
4. **The default `Chat` row** — the resolver's `DefaultFor(Chat)`, which **prefers the
   organization's own default over the platform's** (decision D3: org default → platform default).
5. **The legacy `AzureOpenAI:*` configuration fallback** — only when none of the above resolved, for
   existing config-only deployments.

Every override branch applies the same discipline: the store only returns **enabled** providers
(`IInferenceProviderStore.LoadEnabledAsync`), so a reference to a disabled or deleted provider simply
misses and falls through; and the resolver re-checks `Capability == Chat` as defence in depth, so a
`Transcription` row that somehow got assigned as a chat override is not handed to a chat call.

**Explicit-id resolution** (the `Guid? explicitProviderId` variants — transcription, vision,
decision, video generation, embedding, speech synthesis, and the chat pin) share one rule, enforced by
`TryGetExplicit`: an explicit id resolves **only inside the caller's own snapshot** (platform rows
plus the org's own). An id that is not in the snapshot — another organization's row, a disabled or
deleted one, or a BYO row after a downgrade — is logged, counted on the
`InferenceTenancyDiagnostics.ForeignProviderRef` counter, and treated as **missing**, so the call
falls through to that capability's default. This is how "a step's explicit provider id must belong to
the org or to the platform" (decision D3) is enforced without any caller-side trust. The capability
filter is applied here too, for the same defence-in-depth reason.

A few provider-specific refinements sit on top of this: speech synthesis is health-aware (a failed
last connection test on the default row lets a healthy row narrate instead, with a warning logged),
and embedding keeps a legacy `AzureOpenAI:*` configuration fallback for config-only deployments
(see `docs/embeddings.md`). But the ownership split — platform-managed visible to all, BYO visible to
one, org-default beating platform-default — is the part tenancy adds, and it is the same on every
capability.

The list a step editor is allowed to offer is its own read: `GET
/api/v1/inference-providers/selectable` returns the enabled platform rows plus, while the plan still
allows BYO, the org's own — never an endpoint, key or health detail.

Provider keys never leave the server: they are encrypted at rest with ASP.NET Core Data Protection
(`ISecretProtector`, purpose string `ReelBolt.InferenceProvider.ApiKey`) and decrypted only inside
the resolver, where the plaintext is held in memory for the duration of the call. Rows are returned to
the UI with `hasApiKey` and `apiKeyLastFour` only.

## The self-hosted Default organization

Tenancy must not change the behaviour of a self-hosted ReelBolt install, which has historically been
a single shared workspace of admin-invited users. That behaviour is preserved by the **Default**
organization, the bootstrap `Team` org with the fixed id `WellKnownOrganizations.DefaultId`
(`5c1f0000-0000-4000-8000-000000000002`, slug `default`).

The deployment shape is selected by `Tenancy:Mode` (`TenancyMode`, in
`inference/src/ReelBolt.Shared/Tenancy/TenancyMode.cs`; the Go API reads the same value as
`TENANCY_MODE`), with **`SelfHosted` as the default**:

| | `SelfHosted` | `Cloud` |
|---|---|---|
| How users arrive | admin invite | self-serve signup (behind `SIGNUP_ENABLED`, default off) |
| Memberships created with the user | `Personal` (as `Owner`) **plus `Default`** (as `Member`, or `Owner` for admins) | `Personal` (as `Owner`) only |
| Entitlements | all unlimited (no billing) | plan-driven (see `docs/billing.md`) |

The membership rule lives in `ProvisionUserTenancy` (`api/services/org_provisioning.go`), which runs
inside the transaction that inserts a user: every user gets a `Personal` org with themselves as
`Owner`, a non-Cloud user also joins `Default`, and an `is_admin` user additionally becomes an
`Owner` of the `Platform` org in either mode (and loses that membership if the flag is later
cleared, through `SyncPlatformMembership`).

So on a self-hosted ReelBolt install every admin-invited user joins the `Default` team org, and
everything they create lands there or in their own `Personal` workspace — the shared-visibility
behaviour the install already had. On a `Cloud` deployment nobody is added to `Default` by
provisioning (decision D2), so a cloud user starts with only their own `Personal` workspace until they
create or join a `Team`. **The `Default` row itself, however, is created on every deployment**:
`Migrations/OrganizationBackfill.cs` inserts both well-known organizations unconditionally, so a
cloud install carries an empty `Default` org rather than none. Decision D2 and the ADR both say "in
Cloud mode it is not created"; the migration is what actually runs, and this page follows the
migration.

The A1 backfill (run as raw SQL by the migration, per the ADR) is what populates these on an existing
install:

1. It inserts the `Platform` and `Default` orgs with their fixed ids.
2. It gives every user a `Personal` org (as `Owner`) and **every** user a `Default` membership —
   `Owner` for an admin, `Member` for everyone else. This step is raw SQL with no mode branch, so
   on an install migrated from the pre-tenancy schema the `Default` membership is written regardless
   of `TENANCY_MODE`; only users created afterwards are provisioned mode-aware.
3. `is_admin` users become `Owner`s of `Platform`.
4. Each existing project moves to its owner's `Personal` org, each custom agent to its owner's
   `Personal` org (an agent with no owner goes to `Default`), and each assistant thread to its
   user's `Personal` org.
5. Every existing inference provider keeps `organization_id = NULL`: it becomes platform-managed.
6. `last_active_organization_id` is set to each user's `Personal` org.

The `is_admin` column is left in place and documented as "platform admin"; it is not renamed.
