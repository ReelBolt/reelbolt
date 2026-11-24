import type { ProjectAvatar } from '@/lib/types/avatars';

/** A usable person avatar for tests; `over` changes what a test is about. */
export function testAvatar(over: Partial<ProjectAvatar> = {}): ProjectAvatar {
  return {
    id: 'anna',
    projectId: 'p1',
    subjectKind: 'Person',
    coversPictures: true,
    name: 'Anna',
    description: null,
    selectable: true,
    unselectableReason: null,
    projectVoiceId: null,
    projectVoiceName: null,
    projectVoiceSelectable: null,
    consent: {
      attestedByUserId: 'u1',
      attestedAt: '2026-11-01T00:00:00Z',
      statement: 's',
      statementVersion: 'avatar-consent-2026-12',
      locale: 'en',
      subjectName: 'Anna',
      basis: 'Self',
      subjectIsAdult: true,
      documentFileId: null,
      revokedAt: null,
      revokedByUserId: null,
    },
    retentionUntil: '2028-01-01T00:00:00Z',
    disclosureLabel: 'Visible',
    primaryReferenceId: null,
    references: [],
    createdAt: '2026-11-01T00:00:00Z',
    createdByUserId: 'u1',
    updatedAt: '2026-11-01T00:00:00Z',
    ...over,
  };
}

/** A usable object avatar for tests. */
export function testObject(over: Partial<ProjectAvatar> = {}): ProjectAvatar {
  return testAvatar({ id: 'bottle', name: 'Bottle', subjectKind: 'Object', retentionUntil: null, disclosureLabel: 'MetadataOnly', ...over });
}
