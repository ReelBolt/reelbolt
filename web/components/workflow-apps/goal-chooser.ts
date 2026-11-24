import { msg, type Msg } from '@/i18n/msg';

export type GoalId =
  | 'cut'
  | 'subtitles'
  | 'voice'
  | 'translate'
  | 'promo'
  | 'blur'
  | 'greenScreen'
  | 'thumbnail';

/**
 * `template`: sets up that recipe and opens it. `soon`: no app makes this yet, so the tile says so
 * instead of doing nothing.
 */
export type GoalTarget = { kind: 'template'; key: string } | { kind: 'soon' };

export interface GoalTile {
  id: GoalId;
  label: Msg;
  hint: Msg;
  target: GoalTarget;
  /** Kept in the table but not offered: no app serves it yet. Delete the flag to bring the tile back. */
  hidden?: boolean;
}

/** The one table from a plain goal to the recipe key (WorkflowTemplateCatalog) that serves it. */
export const ALL_GOAL_TILES: readonly GoalTile[] = [
  { id: 'cut', label: msg('apps.goals.cut.label'), hint: msg('apps.goals.cut.hint'), target: { kind: 'template', key: 'video-derush-edit' } },
  { id: 'subtitles', label: msg('apps.goals.subtitles.label'), hint: msg('apps.goals.subtitles.hint'), target: { kind: 'template', key: 'video-subtitles' } },
  { id: 'voice', label: msg('apps.goals.voice.label'), hint: msg('apps.goals.voice.hint'), target: { kind: 'template', key: 'video-derush-edit-voiceover' } },
  { id: 'translate', label: msg('apps.goals.translate.label'), hint: msg('apps.goals.translate.hint'), target: { kind: 'template', key: 'video-derush-edit-dub' } },
  { id: 'promo', label: msg('apps.goals.promo.label'), hint: msg('apps.goals.promo.hint'), target: { kind: 'template', key: 'quick-win-promo' } },
  { id: 'blur', label: msg('apps.goals.blur.label'), hint: msg('apps.goals.blur.hint'), target: { kind: 'template', key: 'video-region-effect' } },
  { id: 'greenScreen', label: msg('apps.goals.greenScreen.label'), hint: msg('apps.goals.greenScreen.hint'), target: { kind: 'template', key: 'green-screen-insert' } },
  { id: 'thumbnail', label: msg('apps.goals.thumbnail.label'), hint: msg('apps.goals.thumbnail.hint'), target: { kind: 'soon' }, hidden: true },
];

/** What the chooser shows. */
export const GOAL_TILES: readonly GoalTile[] = ALL_GOAL_TILES.filter((g) => !g.hidden);
