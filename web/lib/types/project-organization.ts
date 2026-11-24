/** Wire shapes for project tags, folders and per-tag activity (contract: project-organization). */

export const TAG_COLORS = ['gray', 'red', 'orange', 'yellow', 'green', 'teal', 'blue', 'indigo', 'violet', 'pink'] as const;
export type TagColor = (typeof TAG_COLORS)[number];
export const DEFAULT_TAG_COLOR: TagColor = 'gray';

export const MAX_TAGS_PER_PROJECT = 20;
export const MAX_BULK_PROJECTS = 100;

export interface ProjectTag {
  id: string;
  name: string;
  color: string;
  projectCount: number;
  createdAt: string;
}

export interface ProjectTagBody {
  name: string;
  color: TagColor;
}

export interface ProjectFolder {
  id: string;
  name: string;
  projectCount: number;
  createdAt: string;
}

export interface ProjectFolderBody {
  name: string;
}

export interface ProjectOrganizationBody {
  tagIds: string[];
  folderId: string | null;
}

export interface OrganizeProjectsBody {
  projectIds: string[];
  addTagIds: string[];
  removeTagIds: string[];
  folderId?: string | null;
  clearFolder?: boolean;
}

export interface TagRef {
  id: string;
  name: string;
  color: string;
}

export interface ActivityTotals {
  projects: number;
  runs: number;
  /** null when billing is off. */
  credits: number | null;
}

export interface TagActivityRow {
  tagId: string;
  name: string;
  color: string;
  projects: number;
  runs: number;
  passed: number;
  failed: number;
  credits: number | null;
}

export interface TagActivityResponse {
  windowStart: string;
  windowEnd: string;
  billingEnabled: boolean;
  workspace: ActivityTotals;
  tags: TagActivityRow[];
  untagged: ActivityTotals;
}

export interface TagActivityProject {
  projectId: string;
  name: string;
  runs: number;
  credits: number | null;
}

export interface TagActivityRun {
  executionId: string;
  projectId: string;
  workflowId: string;
  projectName: string;
  workflowName: string;
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  credits: number | null;
}

export interface TagDetailActivityResponse {
  tag: TagRef;
  windowStart: string;
  windowEnd: string;
  billingEnabled: boolean;
  totals: { projects: number; runs: number; passed: number; failed: number; credits: number | null };
  projects: TagActivityProject[];
  recentRuns: TagActivityRun[];
}

export type ActivityDays = 7 | 30 | 90;
