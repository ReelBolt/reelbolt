import { describe, expect, it } from 'vitest';
import type { ProjectAvatar } from '../types/avatars';
import type { ProjectFile } from '../types/project';
import {
  avatarNamesByFile, avatarStatus, avatarUsesByFile, avatarsOfKind, defaultConsentLocale, defaultObjectRole,
  defaultStatementLocale, eligibleReferenceFiles, enabledKinds, isObjectAvatar, kindAvailability, kindOfMime,
  needsPictureConsent, objectFileLimits, objectStatements, rolesFor, selectionProblems, statementLocales, subjectKindOf,
} from './model';

const MIME = { mimeTypes: { image: ['image/jpeg', 'image/png'], video: ['video/mp4'], audio: ['audio/mpeg'] } };
const LIMITS = { maxImages: 1, maxVideos: 1, maxAudio: 1, maxImageBytes: 10, maxVideoBytes: 10, maxAudioBytes: 10 } as never;

const avatar = (over: Partial<ProjectAvatar>): ProjectAvatar => ({
  id: 'a', projectId: 'p', name: 'Anna', description: null, selectable: true, unselectableReason: null,
  projectVoiceId: null, projectVoiceName: null, projectVoiceSelectable: null,
  consent: { attestedByUserId: null, attestedAt: null, statement: '', statementVersion: 'v', locale: 'en', subjectName: 'A', basis: 'Self', subjectIsAdult: true, documentFileId: null, revokedAt: null, revokedByUserId: null },
  retentionUntil: '2027-01-01T00:00:00Z', disclosureLabel: 'Visible', primaryReferenceId: null, references: [],
  createdAt: '', createdByUserId: '', updatedAt: '', ...over,
});

describe('avatar model', () => {
  it('reads a MIME type the way the server does', () => {
    expect(kindOfMime('IMAGE/JPEG; charset=x', MIME)).toBe('Image');
    expect(kindOfMime('video/mp4', MIME)).toBe('Video');
    expect(kindOfMime('audio/mpeg', MIME)).toBe('Audio');
    expect(kindOfMime('text/plain', MIME)).toBeNull();
  });

  it('flags a missing picture, too many files and a file too large', () => {
    expect(selectionProblems([], LIMITS)).toEqual(['noImage']);
    expect(selectionProblems([
      { fileId: '1', kind: 'Image', role: 'Face', sizeBytes: 11 },
      { fileId: '2', kind: 'Image', role: 'Face', sizeBytes: 1 },
    ], LIMITS)).toEqual(['tooManyImages', 'tooLarge']);
  });

  it('maps the server reason to one status, and a ready avatar near its end to expiresSoon', () => {
    const now = new Date('2026-12-20T00:00:00Z');
    expect(avatarStatus(avatar({}), now)).toBe('expiresSoon');
    expect(avatarStatus(avatar({}), new Date('2026-01-01T00:00:00Z'))).toBe('ready');
    expect(avatarStatus(avatar({ selectable: false, unselectableReason: 'consent_revoked' }), now)).toBe('withdrawn');
    expect(avatarStatus(avatar({ selectable: false, unselectableReason: 'something_new' }), now)).toBe('unavailable');
  });

  it('lists the avatars using each file, the signed permission included', () => {
    const map = avatarNamesByFile([
      avatar({ name: 'Anna', references: [{ projectFileId: 'f1' } as never], consent: { ...avatar({}).consent, documentFileId: 'doc' } }),
      avatar({ id: 'b', name: 'Bea', references: [{ projectFileId: 'f1' } as never] }),
    ]);
    expect(map.get('f1')).toEqual(['Anna', 'Bea']);
    expect(map.get('doc')).toEqual(['Anna']);
  });

  it('opens the statement in the UI language when the server has it', () => {
    const req = { locales: ['en', 'it'], statements: { en: 'E', it: 'I' } };
    expect(defaultConsentLocale('it-IT', req)).toBe('it');
    expect(defaultConsentLocale('de', req)).toBe('en');
  });
});

describe('avatar model: people and objects', () => {
  const object = (over: Partial<ProjectAvatar> = {}) => avatar({ id: 'o', name: 'Shoe', subjectKind: 'Object', retentionUntil: null, ...over });
  const pf = (id: string, mimeType: string, sourceKind?: string | null) =>
    ({ id, originalFileName: `${id}.bin`, mimeType, sizeBytes: 1, sourceKind } as ProjectFile);

  it('reads a missing subject kind as a person (a server from before objects)', () => {
    expect(subjectKindOf(avatar({}))).toBe('Person');
    expect(subjectKindOf(avatar({ subjectKind: 'Person' }))).toBe('Person');
    expect(subjectKindOf(object())).toBe('Object');
    expect(subjectKindOf(avatar({ subjectKind: 'Robot' as never }))).toBe('Person');
    expect(subjectKindOf(null)).toBe('Person');
    expect(isObjectAvatar(object())).toBe(true);
    expect(isObjectAvatar(undefined)).toBe(false);
  });

  it('reads missing kinds as people on with the old entitlement, objects off', () => {
    expect(kindAvailability({ entitled: true })).toEqual({ Person: { enabled: true, entitled: true }, Object: { enabled: false, entitled: false } });
    expect(kindAvailability({ entitled: false })).toEqual({ Person: { enabled: true, entitled: false }, Object: { enabled: false, entitled: false } });
    expect(kindAvailability({} as never).Person).toEqual({ enabled: true, entitled: false });
    expect(enabledKinds({ entitled: true })).toEqual(['Person']);
    expect(enabledKinds(undefined)).toEqual(['Person']);
  });

  it('reads each kind\'s own switch and plan from kinds', () => {
    const req = { entitled: false, kinds: { person: { enabled: false, entitled: false }, object: { enabled: true, entitled: true } } };
    expect(kindAvailability(req)).toEqual({ Person: { enabled: false, entitled: false }, Object: { enabled: true, entitled: true } });
    expect(enabledKinds(req)).toEqual(['Object']);
    const both = { entitled: true, kinds: { person: { enabled: true, entitled: true }, object: { enabled: true, entitled: false } } };
    expect(enabledKinds(both)).toEqual(['Person', 'Object']);
    expect(enabledKinds({ entitled: true, kinds: { person: { enabled: false, entitled: true }, object: { enabled: false, entitled: true } } })).toEqual([]);
  });

  it('splits the avatars by kind', () => {
    const list = [avatar({ id: '1' }), object({ id: '2' }), avatar({ id: '3', subjectKind: 'Person' })];
    expect(avatarsOfKind(list, 'Person').map((a) => a.id)).toEqual(['1', '3']);
    expect(avatarsOfKind(list, 'Object').map((a) => a.id)).toEqual(['2']);
    expect(avatarsOfKind(undefined, 'Object')).toEqual([]);
  });

  it('gives an object its own roles: pictures front, angle or detail, a video moving, no recording', () => {
    expect(rolesFor('Person', 'Image')).toEqual(['Face', 'HalfBody', 'FullBody']);
    expect(rolesFor('Object', 'Image')).toEqual(['Front', 'Angle', 'Detail']);
    expect(rolesFor('Object', 'Video')).toEqual(['Motion']);
    expect(rolesFor('Object', 'Audio')).toEqual([]);
    // The server's list narrows them; a list that would leave nothing is ignored.
    expect(rolesFor('Object', 'Image', { objectRoles: ['Front', 'Motion'] })).toEqual(['Front']);
    expect(rolesFor('Object', 'Image', { objectRoles: ['Motion'] })).toEqual(['Front', 'Angle', 'Detail']);
    expect(rolesFor('Object', 'Image', { objectRoles: [] })).toEqual(['Front', 'Angle', 'Detail']);
  });

  it('starts the first picture as the front view and later ones as another angle', () => {
    expect(defaultObjectRole('Image', [])).toBe('Front');
    expect(defaultObjectRole('Image', [{ role: 'Front' }])).toBe('Angle');
    expect(defaultObjectRole('Image', [{ role: 'Detail' }])).toBe('Front');
    expect(defaultObjectRole('Video', [{ role: 'Front' }])).toBe('Motion');
    expect(defaultObjectRole('Image', [{ role: 'Front' }], { objectRoles: ['Front', 'Detail'] })).toBe('Front');
  });

  it('uses the object\'s own counts with the shared sizes, and no recordings', () => {
    const limits = { maxImages: 8, maxVideos: 3, maxAudio: 3, maxImageBytes: 10, maxVideoBytes: 20, maxAudioBytes: 30 } as never;
    expect(objectFileLimits({ limits, objectLimits: { maxImages: 5, maxVideos: 1 } })).toMatchObject({ maxImages: 5, maxVideos: 1, maxAudio: 0, maxImageBytes: 10, maxVideoBytes: 20 });
    expect(objectFileLimits({ limits })).toMatchObject({ maxImages: 8, maxVideos: 3, maxAudio: 0 });
    const objectLimits = objectFileLimits({ limits, objectLimits: { maxImages: 1, maxVideos: 0 } });
    expect(selectionProblems([{ fileId: 'v', kind: 'Video', role: 'Motion', sizeBytes: 1 }], objectLimits)).toEqual(['noImage', 'tooManyVideos']);
  });

  it('offers an object pictures and videos, a platform-made file included, never a synced file or a recording', () => {
    const files = [
      pf('img', 'image/png'), pf('vid', 'video/mp4'), pf('aud', 'audio/mpeg'), pf('txt', 'text/plain'),
      pf('made', 'image/png', 'output_copy'), pf('zip', 'image/jpeg', 'zip'), pf('ref', 'image/jpeg', 'avatar_reference'),
      pf('git', 'image/png', 'github'), pf('drive', 'image/png', 'google_drive'), pf('one', 'image/png', 'onedrive'),
      pf('box', 'image/png', 'dropbox'), pf('folder', 'video/mp4', 'local_folder'), pf('new', 'image/png', 'a_later_writer'),
    ];
    expect(eligibleReferenceFiles(files, MIME, 'Object').map((f) => f.id)).toEqual(['img', 'made', 'ref', 'vid', 'zip']);
    // A person keeps the stricter rule: only a file no other writer owns, recordings included.
    expect(eligibleReferenceFiles(files, MIME, 'Person').map((f) => f.id)).toEqual(['aud', 'img', 'ref', 'vid']);
    expect(eligibleReferenceFiles(files, MIME).map((f) => f.id)).toEqual(['aud', 'img', 'ref', 'vid']);
  });

  it('never expires an object: no keep-until date, whatever the clock says', () => {
    const late = new Date('2999-01-01T00:00:00Z');
    expect(avatarStatus(object(), late)).toBe('ready');
    // The stored far-future date of an object is not a date to warn about either.
    expect(avatarStatus(object({ retentionUntil: '9999-12-31T00:00:00Z' }), late)).toBe('ready');
    expect(avatarStatus(avatar({ retentionUntil: null }), late)).toBe('ready');
    expect(avatarStatus(object({ selectable: false, unselectableReason: 'consent_missing' }), late)).toBe('consentMissing');
    expect(avatarStatus(object({ selectable: false, unselectableReason: 'no_image_reference' }), late)).toBe('noPicture');
  });

  it('lists who uses each file with its kind, for the Files badge', () => {
    const uses = avatarUsesByFile([
      avatar({ name: 'Anna', references: [{ projectFileId: 'f1' } as never] }),
      object({ name: 'Shoe', references: [{ projectFileId: 'f1' } as never, { projectFileId: 'f2' } as never] }),
    ]);
    expect(uses.get('f1')).toEqual([{ name: 'Anna', kind: 'Person' }, { name: 'Shoe', kind: 'Object' }]);
    expect(uses.get('f2')).toEqual([{ name: 'Shoe', kind: 'Object' }]);
    expect(avatarNamesByFile([object({ name: 'Shoe', references: [{ projectFileId: 'f2' } as never] })]).get('f2')).toEqual(['Shoe']);
    expect(avatarUsesByFile(undefined).size).toBe(0);
  });

  it('opens a statement in the UI language when it is served in it, and lists only served languages', () => {
    const statements = { en: 'E', it: 'I' };
    expect(defaultStatementLocale('it-IT', ['en', 'it'], statements)).toBe('it');
    expect(defaultStatementLocale('it', ['en', 'it'], { en: 'E' })).toBe('en');
    expect(defaultStatementLocale('de', ['it'], { it: 'I' })).toBe('it');
    expect(statementLocales(['en', 'it'], statements)).toEqual(['en', 'it']);
    expect(statementLocales(['en', 'it'], { it: 'I' })).toEqual(['it']);
    expect(objectStatements({})).toEqual({});
    expect(objectStatements({ objectStatements: statements })).toBe(statements);
  });

  it('flags only a person the server says is not confirmed for pictures', () => {
    expect(needsPictureConsent(avatar({ coversPictures: false }))).toBe(true);
    expect(needsPictureConsent(avatar({ coversPictures: true }))).toBe(false);
    expect(needsPictureConsent(avatar({}))).toBe(false);
    expect(needsPictureConsent(object({ coversPictures: false }))).toBe(false);
  });
});
