import { apiFetch } from './client';
import type {
  AdminUser,
  CreateUserRequest,
  CreateUserResponse,
  EmailChangeAccepted,
  UpdateUserRequest,
} from '../types/admin';

export function getUsers(): Promise<AdminUser[]> {
  return apiFetch<AdminUser[]>('/api/v1/admin/users');
}

export function getUser(id: string): Promise<AdminUser> {
  return apiFetch<AdminUser>(`/api/v1/admin/users/${id}`);
}

export function createUser(data: CreateUserRequest): Promise<CreateUserResponse> {
  return apiFetch<CreateUserResponse>('/api/v1/admin/users', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

export function updateUser(id: string, data: UpdateUserRequest): Promise<AdminUser> {
  return apiFetch<AdminUser>(`/api/v1/admin/users/${id}`, {
    method: 'PUT',
    body: JSON.stringify(data),
  });
}

export function deleteUser(id: string): Promise<void> {
  return apiFetch<void>(`/api/v1/admin/users/${id}`, { method: 'DELETE' });
}

/**
 * Asks the user to confirm a new address. Nothing changes until the CURRENT (verified) mailbox
 * confirms; the API answers 202 with the pending address.
 */
export function requestEmailChange(id: string, newEmail: string): Promise<EmailChangeAccepted> {
  return apiFetch<EmailChangeAccepted>(`/api/v1/admin/users/${id}/email-change`, {
    method: 'POST',
    body: JSON.stringify({ newEmail }),
  });
}

export function cancelEmailChange(id: string): Promise<void> {
  return apiFetch<void>(`/api/v1/admin/users/${id}/email-change`, { method: 'DELETE' });
}
