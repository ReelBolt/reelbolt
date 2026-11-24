/**
 * A best-effort guess, used ONLY while the versions response is still loading.
 *
 * The API is the authority: `GET versions` carries `canEdit` and `canDelete` computed by
 * `IProjectAccessService`, the same rule the write routes enforce, so the tab switches to those
 * flags as soon as the response arrives. This fallback cannot know that the project's creator may
 * delete it even as a Member, which is exactly why it is not the answer once data exists.
 *
 * Delete is org Owner/Admin (`IProjectAccessService`); anyone but Billing may edit.
 */
export interface ProjectHistoryPermissions {
  canEdit: boolean;
  canDelete: boolean;
}

export function projectHistoryPermissions(
  orgRole: string | null | undefined,
  isPlatformAdmin = false,
): ProjectHistoryPermissions {
  const isBilling = orgRole === 'Billing';
  const isOrgAdmin = orgRole === 'Owner' || orgRole === 'Admin';
  return {
    canEdit: isPlatformAdmin || !isBilling,
    canDelete: isPlatformAdmin || isOrgAdmin,
  };
}
