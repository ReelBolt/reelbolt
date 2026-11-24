import {
  IconActivity,
  IconCloudDownload,
  IconEye,
  IconFocus2,
  IconHeadphones,
  IconLoader2,
  IconMovie,
  IconPalette,
  IconPencil,
  IconPhoto,
  IconPhotoEdit,
  IconPlayerPlay,
  IconRobot,
  IconWand,
  IconUpload,
  IconUserPlus,
  IconUserCircle,
  IconVolume,
  IconWaveSine,
  IconPhotoVideo,
} from '@tabler/icons-react';
import { apiFetch } from '@/lib/api/client';
import { cancelSubjectClip } from '@/lib/editing/subjects/subject-clip-api';
import { registerJobType } from './registry';
import type { ActivityJob } from './types';

const execPage = (projectId: string, workflowId: string | null, executionId: string | null): string | null =>
  workflowId && executionId ? `/projects/${projectId}/workflows/${workflowId}/executions/${executionId}` : null;

const projectPage = (j: ActivityJob): string | null => (j.projectId ? `/projects/${j.projectId}` : null);

const runPage = (j: ActivityJob): string | null =>
  execPage(j.projectId, j.workflowId, j.subject.sourceExecutionId ?? j.executionId) ?? projectPage(j);

const timelinePage = (j: ActivityJob): string | null =>
  j.subject.timelineId ? `/editor/${j.projectId}/timeline/${j.subject.timelineId}` : j.projectId ? `/editor?project=${j.projectId}` : null;

const editorHub = (j: ActivityJob): string | null => (j.projectId ? `/editor?project=${j.projectId}` : null);

/**
 * Stops a clip with avatars from its server row (the hidden run has no workflow page): `POST …/subject-clips/{e}/cancel`.
 * A clip that ended first answers 409, which is just as good.
 */
export async function stopSubjectClip(job: ActivityJob): Promise<void> {
  if (!job.projectId || !job.executionId) return;
  await cancelSubjectClip(job.projectId, job.executionId);
}

/** Stops a run-backed job: `POST …/executions/{e}/stop`. */
export async function stopRun(job: ActivityJob): Promise<void> {
  if (!job.projectId || !job.workflowId || !job.executionId) return;
  await apiFetch(`/api/v1/projects/${job.projectId}/workflows/${job.workflowId}/executions/${job.executionId}/stop`, { method: 'POST' });
}

registerJobType({ type: 'workflowRun', icon: IconPlayerPlay, labelKey: 'types.workflowRun', href: runPage, cancel: stopRun });
registerJobType({ type: 'designSystemBuild', icon: IconPalette, labelKey: 'types.designSystemBuild', href: runPage, cancel: stopRun });
registerJobType({ type: 'revision', icon: IconWand, labelKey: 'types.revision', href: runPage, cancel: stopRun });
registerJobType({ type: 'changeRequest', icon: IconPencil, labelKey: 'types.changeRequest', href: runPage, cancel: stopRun });
registerJobType({
  type: 'graphic',
  icon: IconPhoto,
  labelKey: 'types.graphic',
  href: (j) => (j.subject.documentId ? `/editor/thumbnails/${j.subject.documentId}` : projectPage(j)),
  cancel: stopRun,
});
registerJobType({ type: 'render', icon: IconMovie, labelKey: 'types.render', href: timelinePage, cancel: stopRun });
registerJobType({ type: 'preview', icon: IconEye, labelKey: 'types.preview', href: timelinePage, cancel: stopRun });
registerJobType({ type: 'loudness', icon: IconVolume, labelKey: 'types.loudness', href: timelinePage, cancel: stopRun });
registerJobType({ type: 'audition', icon: IconHeadphones, labelKey: 'types.audition', href: timelinePage, cancel: stopRun });
registerJobType({ type: 'objectAnalysis', icon: IconFocus2, labelKey: 'types.objectAnalysis', href: editorHub, cancel: stopRun });
registerJobType({ type: 'audioCleanup', icon: IconWaveSine, labelKey: 'types.audioCleanup', href: editorHub });
// A clip that shows the project's avatars or objects, started from the editor's media pool (the finished clip is in the pool).
// The server lists its hidden run under this type too; its row is stopped through the subject-clips route.
registerJobType({ type: 'subjectClip', icon: IconUserCircle, labelKey: 'types.subjectClip', href: editorHub, cancel: stopSubjectClip });
// Successes of the two media types are too noisy to announce; their failures still are (notifier).
registerJobType({ type: 'mediaAnalysis', icon: IconWaveSine, labelKey: 'types.mediaAnalysis', href: editorHub });
registerJobType({ type: 'mediaPrep', icon: IconPhotoVideo, labelKey: 'types.mediaPrep', href: editorHub });
registerJobType({
  type: 'cloudSync',
  icon: IconCloudDownload,
  labelKey: 'types.cloudSync',
  href: (j) => (j.projectId ? `/projects/${j.projectId}/files` : null),
});
registerJobType({
  type: 'hiddenRun',
  icon: IconLoader2,
  labelKey: 'types.hiddenRun',
  href: runPage,
  cancel: stopRun,
});
registerJobType({ type: 'upload', icon: IconUpload, labelKey: 'types.upload' });
registerJobType({ type: 'pictureOperation', icon: IconPhotoEdit, labelKey: 'types.pictureOperation' });
registerJobType({ type: 'proposal', icon: IconWand, labelKey: 'types.proposal' });
registerJobType({ type: 'assistantWatch', icon: IconRobot, labelKey: 'types.assistantWatch' });
// An avatar being made in the background (a variant by the picture model, an object pulled out of a video): the
// Avatars tab tracks it (components/avatars/variants/useAvatarBuildTray.ts) and gives it its own cancel.
registerJobType({
  type: 'avatarBuild',
  icon: IconUserPlus,
  labelKey: 'types.avatarBuild',
  href: (j) => (j.projectId ? `/projects/${j.projectId}?tab=avatars` : null),
});
registerJobType({ type: 'generic', icon: IconActivity, labelKey: 'types.generic' });
