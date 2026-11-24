/**
 * Mirrors `ProjectVoiceDtos.cs` (Inference API) — see docs/voiceover.md "Voices and consent".
 * A cloned voice is a person's biometric data: `selectable` is the ONE read-path rule (consent
 * attested, not revoked, within retention), and the UI never offers a voice that is false here.
 */
export type ProjectVoiceSource = 'Cloned' | 'Library';

export type UnselectableReason = 'consent_missing' | 'consent_revoked' | 'retention_expired' | string;

export interface ProjectVoice {
  id: string;
  projectId: string;
  inferenceProviderId: string | null;
  inferenceProviderName: string | null;
  name: string;
  description: string | null;
  source: ProjectVoiceSource;
  remoteVoiceId: string;
  referenceProjectFileIds: string[];
  selectable: boolean;
  unselectableReason: UnselectableReason | null;
  consentAttestedByUserId: string | null;
  consentAttestedAt: string | null;
  consentStatement: string | null;
  consentSubjectName: string | null;
  consentRevokedAt: string | null;
  retentionUntil: string | null;
  createdAt: string;
}

export interface CreateLibraryVoiceRequest {
  name: string;
  remoteVoiceId: string;
  inferenceProviderId?: string | null;
  description?: string | null;
}

export interface CloneVoiceRequest {
  name: string;
  referenceProjectFileIds: string[];
  /** Must equal `REQUIRED_CONSENT_STATEMENT` verbatim — the API rejects anything else. */
  consentStatement: string;
  consentSubjectName: string;
  inferenceProviderId?: string | null;
  description?: string | null;
  retentionDays?: number;
}

export interface DeleteVoiceResponse {
  id: string;
  rowDeleted: boolean;
  remoteModelDeleted: boolean;
  remoteDeletionNote: string | null;
  referenceFilesDeleted: number;
}

/** Mirrors `ProjectVoicesController.RequiredConsentStatement` byte for byte. */
export const REQUIRED_CONSENT_STATEMENT =
  'I confirm that the person whose voice is in these recordings has given informed consent to ' +
  'having their voice cloned and used to narrate videos in this project, that I will keep a ' +
  'record of that consent, and that I will revoke or delete this voice if they withdraw it.';

export const DEFAULT_RETENTION_DAYS = 365;
export const MAX_RETENTION_DAYS = 730;

/** One voice of the speech provider's public library (`GET /voices/library`). */
export interface VoiceLibraryItem {
  voiceId: string;
  name: string;
  languages: string[];
  tags: string[];
  previewAvailable: boolean;
}

export interface VoiceLibraryPage {
  items: VoiceLibraryItem[];
  total: number;
  page: number;
  pageSize: number;
}

/** Refusal code when the project's speech provider has no browsable library (404). */
export const VOICE_LIBRARY_NOT_SUPPORTED = 'voice_library_not_supported';
