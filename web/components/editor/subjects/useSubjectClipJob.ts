'use client';

import { useCallback, useEffect, useRef, useSyncExternalStore } from 'react';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { useActivity } from '@/lib/activity/ActivityProvider';
import type { SubjectClipRequest } from '@/lib/editing/subjects/subject-clip-api';
import {
  cancelSubjectClipJob,
  defaultSubjectClipApi,
  dismissSubjectClipJob,
  startSubjectClipJob,
  subjectClipJobs,
  subjectClipsVersion,
  subscribeSubjectClips,
  type SubjectClipApi,
  type SubjectClipJob,
} from './subject-clip-store';

export { defaultSubjectClipApi };
export type { SubjectClipApi, SubjectClipJob };

/** The id of a clip's row in the header's activity tray. */
export const subjectClipActivityId = (executionId: string): string => `subjectClip:${executionId}`;

/**
 * True where the activity tray stays quiet about a finished clip (`isOnJobScreen` in lib/activity/notifier.ts): a
 * timeline of this project, or the editor's hub. There the "added" note is said here instead; anywhere else the
 * tray says it, so the person is told exactly once.
 */
function inEditorOf(projectId: string): boolean {
  if (typeof window === 'undefined') return false;
  const { pathname } = window.location;
  return pathname.includes(`/editor/${projectId}`) || /\/editor\/?$/.test(pathname);
}

/**
 * The project's clip jobs, and how to start and stop one. The jobs and their polling live in a store outside
 * React (`subject-clip-store`), so they outlive the dialog and the pool's tab; each started clip is also shown in
 * the header's activity tray (`trackJob`, never a stream of its own).
 */
export function useSubjectClipJobs(projectId: string, api: SubjectClipApi = defaultSubjectClipApi) {
  const t = useT('editor-media.subjects');
  const { trackJob, allJobs, cancel: cancelTracked } = useActivity();
  useSyncExternalStore(subscribeSubjectClips, subjectClipsVersion, subjectClipsVersion);
  const jobs = subjectClipJobs(projectId);
  // Read at call time: a cancel from the panel must find the tray's row as it is then.
  const tracked = useRef(allJobs);
  useEffect(() => {
    tracked.current = allJobs;
  }, [allJobs]);

  const start = useCallback(
    (request: SubjectClipRequest, names: readonly string[]): Promise<SubjectClipJob> =>
      startSubjectClipJob(projectId, request, {
        names,
        api,
        onStarted: (job, finished) => {
          void trackJob(
            {
              type: 'subjectClip',
              title: names.join(', '),
              projectId,
              id: subjectClipActivityId(job.executionId),
              executionId: job.executionId,
              href: `/editor?project=${projectId}`,
              creditsEstimate: job.credits,
              cancel: () => cancelSubjectClipJob(job.executionId),
            },
            finished,
          ).catch(() => undefined);
        },
        onReady: () => {
          // In this project's editor the tray stays quiet about a success, so say it here; elsewhere the tray does.
          if (inEditorOf(projectId)) notifications.show({ color: 'green', message: t('added') });
        },
      }),
    [projectId, api, trackJob, t],
  );

  const cancel = useCallback(
    (job: SubjectClipJob): Promise<void> => {
      const row = tracked.current.find((j) => j.origin === 'client' && j.id === subjectClipActivityId(job.executionId));
      // Through the tray when it lists the clip, so its row reads "cancelled" and not "did not finish".
      return row ? cancelTracked(row) : cancelSubjectClipJob(job.executionId);
    },
    [cancelTracked],
  );

  const dismiss = useCallback((job: SubjectClipJob) => dismissSubjectClipJob(job.executionId), []);

  return { jobs, start, cancel, dismiss };
}
