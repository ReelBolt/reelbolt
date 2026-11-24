import type { OrgRole } from './types/auth';

// Workspace authority rules, mirrored from the Go API (api/handlers/orgs/orgs.go) so the UI
// offers exactly the controls the server will accept rather than rendering a button that 403s.
// The server stays authoritative: every call re-reads the caller's membership from the database,
// so these helpers only decide what to show.
//
//   - CanManageMembers: owner, admin  → rename, members, roles, invites
//   - CanViewMembers:   owner, admin, member (Billing sees none)
//   - CanAssign:        only an Owner can create or touch an Owner

/** Whether `role` may manage the workspace: rename it, change roles, remove members, invite. */
export function canManageOrg(role?: string | null): boolean {
  return role === 'Owner' || role === 'Admin';
}

/** Whether `role` may list the workspace's members (Billing may not). */
export function canViewMembers(role?: string | null): boolean {
  return canManageOrg(role) || role === 'Member';
}

/**
 * Whether `actor` may grant `role`, or change a member who currently holds `current`
 * (pass `null`/`''` for a new invitation). Mirrors the Go `CanAssign`: an Admin can never
 * create or touch an Owner, only an Owner can.
 */
export function canAssignRole(
  actor?: string | null,
  current?: string | null,
  role?: string | null,
): boolean {
  if (!canManageOrg(actor)) return false;
  if (actor === 'Owner') return true;
  return role !== 'Owner' && current !== 'Owner';
}

/** The roles `actor` may hand out, in display order. Empty for a non-manager. */
export function assignableRoles(actor?: string | null): OrgRole[] {
  if (actor === 'Owner') return ['Owner', 'Admin', 'Member', 'Billing'];
  if (actor === 'Admin') return ['Admin', 'Member', 'Billing'];
  return [];
}

/** How many members currently hold the Owner role. */
export function ownerCount(members: { role: string }[]): number {
  return members.filter((m) => m.role === 'Owner').length;
}

/**
 * Whether demoting or removing `member` would leave the Team workspace without an Owner.
 * The API answers 409 for the same case; this only lets the table say so before the click.
 */
export function isLastOwner(
  member: { userId: string; role: string },
  members: { userId: string; role: string }[],
): boolean {
  return member.role === 'Owner' && ownerCount(members) <= 1;
}
