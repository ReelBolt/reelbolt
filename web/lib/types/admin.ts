export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  isAdmin: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  /** Whether the current address is verified. Optional: older API builds omit it. */
  emailVerified?: boolean;
  /** An open admin-requested change awaiting the user's confirmation from the OLD mailbox. */
  pendingEmailChange?: PendingEmailChange | null;
}

export interface PendingEmailChange {
  newEmail: string;
  requestedAt: string;
  expiresAt: string;
}

export interface EmailChangeAccepted {
  pendingEmail: string;
  expiresAt: string;
}

export interface CreateUserRequest {
  email: string;
  displayName: string;
  isAdmin: boolean;
}

export interface CreateUserResponse {
  user: AdminUser;
  temporaryPassword: string;
}

export interface UpdateUserRequest {
  email?: string;
  displayName?: string;
  isAdmin?: boolean;
  resetPassword?: boolean;
}
