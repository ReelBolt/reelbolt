'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { getTagActivityUrl, getTagDetailActivityUrl } from '../api/project-organization';
import type { TagActivityResponse, TagDetailActivityResponse } from '../types/project-organization';

export function useTagActivity(days: number) {
  return useSWR<TagActivityResponse>(getTagActivityUrl(days), fetcher);
}

export function useTagDetailActivity(tagId: string | null, days: number) {
  return useSWR<TagDetailActivityResponse>(tagId ? getTagDetailActivityUrl(tagId, days) : null, fetcher);
}
