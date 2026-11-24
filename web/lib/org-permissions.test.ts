import { describe, expect, it } from 'vitest';
import {
  assignableRoles,
  canAssignRole,
  canManageOrg,
  canViewMembers,
  isLastOwner,
  ownerCount,
} from './org-permissions';

/**
 * A16-UI: the workspace authority rules, mirrored from api/handlers/orgs/orgs.go so the UI offers
 * exactly what the server accepts. These are the rules the members page and the members table
 * branch on, so they are asserted directly rather than only through the components.
 */
describe('org-permissions', () => {
  it('lets only Owner and Admin manage a workspace', () => {
    expect(canManageOrg('Owner')).toBe(true);
    expect(canManageOrg('Admin')).toBe(true);
    expect(canManageOrg('Member')).toBe(false);
    expect(canManageOrg('Billing')).toBe(false);
    expect(canManageOrg(null)).toBe(false);
    expect(canManageOrg(undefined)).toBe(false);
  });

  it('lets a Member list members but not Billing', () => {
    expect(canViewMembers('Owner')).toBe(true);
    expect(canViewMembers('Admin')).toBe(true);
    expect(canViewMembers('Member')).toBe(true);
    expect(canViewMembers('Billing')).toBe(false);
    expect(canViewMembers(null)).toBe(false);
  });

  it('mirrors CanAssign: only an Owner can grant or touch an Owner', () => {
    expect(canAssignRole('Owner', 'Member', 'Owner')).toBe(true);
    expect(canAssignRole('Owner', 'Owner', 'Member')).toBe(true);
    expect(canAssignRole('Admin', 'Member', 'Admin')).toBe(true);
    expect(canAssignRole('Admin', 'Member', 'Owner')).toBe(false);
    expect(canAssignRole('Admin', 'Owner', 'Admin')).toBe(false);
    expect(canAssignRole('Member', 'Member', 'Member')).toBe(false);
    expect(canAssignRole('Billing', null, 'Member')).toBe(false);
  });

  it('offers an Owner every role and an Admin every role but Owner', () => {
    expect(assignableRoles('Owner')).toEqual(['Owner', 'Admin', 'Member', 'Billing']);
    expect(assignableRoles('Admin')).toEqual(['Admin', 'Member', 'Billing']);
    expect(assignableRoles('Member')).toEqual([]);
    expect(assignableRoles(null)).toEqual([]);
  });

  it('flags the last Owner, and only the last one', () => {
    const soleOwner = [{ userId: 'u1', role: 'Owner' }];
    expect(ownerCount(soleOwner)).toBe(1);
    expect(isLastOwner(soleOwner[0], soleOwner)).toBe(true);

    const twoOwners = [{ userId: 'u1', role: 'Owner' }, { userId: 'u2', role: 'Owner' }];
    expect(ownerCount(twoOwners)).toBe(2);
    expect(isLastOwner(twoOwners[0], twoOwners)).toBe(false);

    const admin = [{ userId: 'u1', role: 'Owner' }, { userId: 'u2', role: 'Admin' }];
    expect(isLastOwner(admin[1], admin)).toBe(false);
  });
});
