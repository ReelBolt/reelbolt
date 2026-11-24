'use client';

import React from 'react';
import { Badge } from '@mantine/core';
import { useReviewLinks } from '@/lib/hooks/use-review-links';
import { unreadLinkCount } from '@/lib/editing/review/guest-links';
import { useT } from '@/i18n';

/** The hub card's "new guest notes" chip: shows only when a share link has activity the owner has not opened. */
export function GuestActivityBadge({ projectId, timelineId }: { projectId: string; timelineId: string }) {
  const t = useT('editor.review.guestActivityBadge');
  const { data } = useReviewLinks(projectId, timelineId);
  const unread = unreadLinkCount(data);
  if (unread === 0) return null;
  return (
    <Badge fz="xs" size="sm" color="red" variant="filled" style={{ textTransform: 'none', flexShrink: 0 }} data-testid={`guest-activity-${timelineId}`}>
      {t('newGuestActivity')}
    </Badge>
  );
}
