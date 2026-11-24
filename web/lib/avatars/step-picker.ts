/**
 * Pure helpers of the avatar pickers: the "Avatar" section of a VideoGenerate step and the "Avatar" input of an
 * app's run page (docs/avatars.md "Choosing an avatar in the dashboard"). No user-visible text lives here: every
 * function answers a value or a key suffix the components look up in the catalogue.
 */
import type { AvatarReferenceRole, AvatarSubjectKind, ProjectAvatar } from '../types/avatars';
import { avatarsOfKind } from './model';
import { displayName, groupByOriginal } from './variants';
import type { VideoGenerateStepConfig } from '../types/workflow';
import type { ProjectFile } from '../types/project';

/** The reference roles in the order the server lists them (`AvatarReferenceRole`; an object's roles were appended). */
export const ROLE_ORDER: readonly AvatarReferenceRole[] = ['Face', 'HalfBody', 'FullBody', 'Motion', 'Speech', 'Front', 'Angle', 'Detail'];

/** The picture roles, a person's then an object's: a step must keep at least one (`VideoGenerateStepConfig.Validate`). */
export const PICTURE_ROLES: readonly AvatarReferenceRole[] = ['Face', 'HalfBody', 'FullBody', 'Front', 'Angle', 'Detail'];

/** The roles the avatar's usable reference files carry, in the server's order. */
export function rolesPresent(avatar: ProjectAvatar | null | undefined): AvatarReferenceRole[] {
  if (!avatar) return [];
  const present = new Set(avatar.references.filter((r) => r.state === 'ok').map((r) => r.role));
  return ROLE_ORDER.filter((r) => present.has(r));
}

/**
 * The roles the step sends: the stored subset (an empty one included, which the save refuses), else every role the
 * avatar has (null on the wire = every role).
 * A stored role the avatar no longer carries stays listed so the person can see and drop it.
 */
export function effectiveRoles(stored: readonly AvatarReferenceRole[] | null | undefined, present: readonly AvatarReferenceRole[]): AvatarReferenceRole[] {
  return stored ? ROLE_ORDER.filter((r) => stored.includes(r)) : [...present];
}

/**
 * The value to store for a ticked set of roles: null when it is every role the avatar has (the server then sends
 * everything, also a file added later), else the subset in the server's order.
 */
export function rolesToStore(ticked: readonly string[], present: readonly AvatarReferenceRole[]): AvatarReferenceRole[] | null {
  const picked = ROLE_ORDER.filter((r) => ticked.includes(r));
  if (picked.length === present.length && present.every((r) => picked.includes(r))) return null;
  return picked;
}

/** True when the ticked roles keep a picture, as the save validation requires. */
export function keepsPicture(roles: readonly AvatarReferenceRole[]): boolean {
  return roles.some((r) => PICTURE_ROLES.includes(r));
}

/**
 * Why the step cannot show an avatar, as a key suffix under `wfSteps.videoGenerate.avatar.blocked`, or null:
 * an avatar works on an inline prompt only and never with a first/last frame (the server refuses both).
 */
export type AvatarBlock = 'plan' | 'frames';

export function avatarBlock(config: Pick<VideoGenerateStepConfig, 'planSource' | 'firstFrame' | 'lastFrame' | 'firstFrameProjectFileId' | 'lastFrameProjectFileId'>): AvatarBlock | null {
  if (config.planSource !== 'Inline') return 'plan';
  if (config.firstFrame || config.lastFrame || config.firstFrameProjectFileId || config.lastFrameProjectFileId) return 'frames';
  return null;
}

/**
 * The fal.ai talking-head routes (`FalVideoCatalog`, job `TalkingHead`): models that declare that a speech track
 * drives what the person says. Kept in step with docs/fal-ai-models.md section 14.
 */
export const TALKING_HEAD_MODELS: readonly string[] = [
  'minimax/h3-max/lip-sync/image-to-video',
  'fal-ai/kling-video/ai-avatar/v2/standard',
];

/**
 * Whether the step's model makes the person say a speech track: `yes` for a talking-head route, `no` for the
 * MiniMax H3 family and the self-hosted graphs (their reference audio is a voice sample, never words to say), and
 * `unknown` for any other model id (the server decides when the step runs).
 */
export type SpeechDrive = 'yes' | 'no' | 'unknown';

export function speechDrive(model: string | null | undefined): SpeechDrive {
  const id = (model ?? '').trim();
  if (TALKING_HEAD_MODELS.includes(id)) return 'yes';
  if (/^MiniMax-H3/i.test(id) || id.startsWith('wan2.2-')) return 'no';
  return 'unknown';
}

/** Where the driving speech comes from: nothing, an earlier Voiceover step, or a project audio file. */
export type SpeechSource = 'none' | 'step' | 'file';

export function speechSource(config: Pick<VideoGenerateStepConfig, 'avatarAudio' | 'avatarAudioProjectFileId'>): SpeechSource {
  if (config.avatarAudioProjectFileId) return 'file';
  if (config.avatarAudio) return 'step';
  return 'none';
}

/** The earlier Voiceover steps of the workflow, as 1-based step orders. */
export function voiceoverStepOrders(steps: readonly { stepType: string }[], currentStepIndex: number): number[] {
  return steps
    .slice(0, Math.max(0, currentStepIndex))
    .flatMap((s, i) => (s.stepType === 'Voiceover' ? [i + 1] : []));
}

/** The project's audio files a speech track can come from, by name. */
export function audioFiles(files: readonly ProjectFile[] | null | undefined): ProjectFile[] {
  return (files ?? [])
    .filter((f) => (f.mimeType ?? '').toLowerCase().startsWith('audio/'))
    .sort((a, b) => a.originalFileName.localeCompare(b.originalFileName));
}

/**
 * The avatars a picker offers: usable ones first (by name), then the rest (by name), so the choice is at the top.
 * A variant is listed right after its original (by label), whatever its own state; one whose original is not
 * among the avatars is placed like any other avatar.
 */
export function pickerOrder(avatars: readonly ProjectAvatar[] | null | undefined): ProjectAvatar[] {
  return groupByOriginal(avatars)
    .sort((a, b) => Number(b.avatar.selectable) - Number(a.avatar.selectable) || a.avatar.name.localeCompare(b.avatar.name))
    .flatMap((g) => [g.avatar, ...g.variants]);
}

/** The name a picker shows: `Car · Red` for a variant, the avatar's own name otherwise. */
export function pickerName(avatar: ProjectAvatar, avatars: readonly ProjectAvatar[] | null | undefined): string {
  return displayName(avatar, avatars);
}

/** One kind's avatars in a picker, in `pickerOrder`. */
export interface PickerGroup {
  kind: AvatarSubjectKind;
  avatars: ProjectAvatar[];
}

/**
 * The picker's avatars by kind, people first, a kind with none left out. A picker shows group headings only
 * when this answers more than one group, so a project with people only looks as it always did.
 */
export function pickerGroups(avatars: readonly ProjectAvatar[] | null | undefined): PickerGroup[] {
  return (['Person', 'Object'] as const)
    .map((kind) => ({ kind, avatars: pickerOrder(avatarsOfKind(avatars, kind)) }))
    .filter((g) => g.avatars.length > 0);
}
