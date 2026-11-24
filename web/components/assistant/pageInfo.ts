import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/**
 * What the assistant is told about the page the user is on. Derived from the router's pathname
 * (basePath `/app` already stripped by `usePathname`), so no page has to cooperate. The server
 * treats every field as an untrusted HINT — it bounds and sanitises them, never uses them for
 * authority — see `AssistantPageContextDto` on the Inference API.
 */
export type AssistantPageKind =
  | 'dashboard'
  | 'projects'
  | 'project'
  | 'project-files'
  | 'project-workflows'
  | 'workflow-new'
  | 'workflow'
  | 'execution'
  | 'editor'
  | 'editor-hub'
  | 'pictures'
  | 'picture-editor'
  | 'agents'
  | 'agent'
  | 'admin'
  | 'admin-users'
  | 'admin-user'
  | 'admin-providers'
  | 'admin-provider'
  | 'admin-skills'
  | 'admin-skill'
  | 'admin-calibration'
  | 'admin-workflow-service'
  | 'settings'
  | 'other';

export interface AssistantEntityIds {
  projectId?: string;
  workflowId?: string;
  executionId?: string;
  agentId?: string;
  timelineId?: string;
  stepResultId?: string;
  userId?: string;
  providerId?: string;
  skillName?: string;
  /** The picture editor's document (only ever a GUID). */
  documentId?: string;
}

export interface AssistantPageInfo {
  /** Route path without the `/app` basePath, query string excluded. */
  path: string;
  kind: AssistantPageKind;
  /** Human title of the page kind; the live document title and active tab are added at send time. */
  title: string;
  entityIds: AssistantEntityIds;
  /** The visible tab, when the page has tabs. Read from the DOM at send time. */
  tab?: string;
}

interface Route {
  pattern: RegExp;
  kind: AssistantPageKind;
  title: string;
  ids?: readonly (keyof AssistantEntityIds)[];
  tab?: string;
  /**
   * The kind and title for a route that is one URL with two pages behind it. The Editor page is the
   * only one: `?view=` picks its Videos or Pictures list, and the assistant has to know which list
   * the user is looking at (they are different things, opened in different editors), so the query
   * decides the kind rather than defaulting every visit to the videos view.
   */
  byQuery?: (query: URLSearchParams) => { kind: AssistantPageKind; title: string };
}

// Order matters: first match wins, most specific first.
const ROUTES: readonly Route[] = [
  { pattern: /^\/dashboard$/, kind: 'dashboard', title: 'Dashboard' },
  { pattern: /^\/projects$/, kind: 'projects', title: 'Projects' },
  {
    pattern: /^\/projects\/([^/]+)\/workflows\/new$/,
    kind: 'workflow-new',
    title: 'New workflow',
    ids: ['projectId'],
  },
  {
    pattern: /^\/projects\/([^/]+)\/workflows\/([^/]+)\/executions\/([^/]+)$/,
    kind: 'execution',
    title: 'Workflow execution',
    ids: ['projectId', 'workflowId', 'executionId'],
  },
  {
    pattern: /^\/projects\/([^/]+)\/workflows\/([^/]+)$/,
    kind: 'workflow',
    title: 'Workflow editor',
    ids: ['projectId', 'workflowId'],
  },
  {
    pattern: /^\/projects\/([^/]+)\/workflows$/,
    kind: 'project-workflows',
    title: 'Project workflows',
    ids: ['projectId'],
    tab: 'workflows',
  },
  {
    pattern: /^\/projects\/([^/]+)\/files$/,
    kind: 'project-files',
    title: 'Project files',
    ids: ['projectId'],
    tab: 'files',
  },
  {
    pattern: /^\/projects\/([^/]+)\/editor\/([^/]+)$/,
    kind: 'editor',
    title: 'Video editor',
    ids: ['projectId', 'stepResultId'],
  },
  { pattern: /^\/projects\/([^/]+)$/, kind: 'project', title: 'Project', ids: ['projectId'] },
  // Before the video editor's `/editor/{projectId}/{stepResultId}`: that pattern also matches
  // `/editor/thumbnails/{documentId}` and used to send "thumbnails" as the project id, which the
  // API refuses (400), so the assistant could not start a thread on the picture editor. The
  // picture editor's project is in its `?project=` query (see resolvePageInfo's `search`).
  { pattern: /^\/editor\/thumbnails\/([^/]+)$/, kind: 'picture-editor', title: 'Picture editor', ids: ['documentId'] },
  // The video editor: one editable timeline, addressed by project and timeline.
  {
    pattern: /^\/editor\/([^/]+)\/timeline\/([^/]+)$/,
    kind: 'editor',
    title: 'Video editor',
    ids: ['projectId', 'timelineId'],
  },
  // The phone review page of a timeline: watching and commenting, still the video editor's subject.
  {
    pattern: /^\/editor\/([^/]+)\/review\/([^/]+)$/,
    kind: 'editor',
    title: 'Video review',
    ids: ['projectId', 'timelineId'],
  },
  // A workflow render opened by its step result: it becomes a timeline in the same editor.
  {
    pattern: /^\/editor\/([^/]+)\/([^/]+)$/,
    kind: 'editor',
    title: 'Video editor',
    ids: ['projectId', 'stepResultId'],
  },
  // The Editor hub: timelines, media and pictures of one project (`?project=&view=`). Its Pictures
  // view is a different list from the other two, opened in a different editor, so the query
  // decides the kind rather than defaulting every visit to the hub.
  {
    pattern: /^\/editor$/,
    kind: 'editor-hub',
    title: 'Editor',
    byQuery: (query) =>
      query.get('view') === 'pictures'
        ? { kind: 'pictures', title: 'Pictures' }
        : { kind: 'editor-hub', title: 'Editor' },
  },
  { pattern: /^\/editor(\/.*)?$/, kind: 'editor-hub', title: 'Editor' },
  { pattern: /^\/agents$/, kind: 'agents', title: 'Agents' },
  { pattern: /^\/agents\/([^/]+)$/, kind: 'agent', title: 'Agent', ids: ['agentId'] },
  { pattern: /^\/admin$/, kind: 'admin', title: 'Admin overview' },
  { pattern: /^\/admin\/users$/, kind: 'admin-users', title: 'Admin: users' },
  { pattern: /^\/admin\/users\/([^/]+)$/, kind: 'admin-user', title: 'Admin: user', ids: ['userId'] },
  {
    pattern: /^\/admin\/inference-providers$/,
    kind: 'admin-providers',
    title: 'Admin: inference providers',
  },
  {
    pattern: /^\/admin\/inference-providers\/([^/]+)$/,
    kind: 'admin-provider',
    title: 'Admin: inference provider',
    ids: ['providerId'],
  },
  { pattern: /^\/admin\/skills$/, kind: 'admin-skills', title: 'Admin: skills' },
  {
    pattern: /^\/admin\/skills\/([^/]+)$/,
    kind: 'admin-skill',
    title: 'Admin: skill',
    ids: ['skillName'],
  },
  { pattern: /^\/admin\/calibration$/, kind: 'admin-calibration', title: 'Admin: decision calibration' },
  {
    pattern: /^\/admin\/workflow-service$/,
    kind: 'admin-workflow-service',
    title: 'Admin: workflow service',
  },
  { pattern: /^\/settings(\/.*)?$/, kind: 'settings', title: 'Settings' },
];

/** Resolves a router pathname to what the assistant should know about it. Never throws. */
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** True when a value has the shape of an id the API accepts; anything else is never sent as one. */
export function isGuid(value: string | null | undefined): value is string {
  return !!value && GUID.test(value);
}

function safeDecode(raw: string): string {
  try {
    return decodeURIComponent(raw);
  } catch {
    return raw;
  }
}

export function resolvePageInfo(pathname: string | null | undefined, search?: string | null): AssistantPageInfo {
  const path = (pathname ?? '/').replace(/^\/app(?=\/|$)/, '').replace(/\/+$/, '') || '/';
  const query = new URLSearchParams(search ?? '');
  const queryProject = query.get('project');

  for (const route of ROUTES) {
    const match = route.pattern.exec(path);
    if (!match) continue;
    const entityIds: AssistantEntityIds = {};
    route.ids?.forEach((key, index) => {
      const raw = match[index + 1];
      if (!raw) return;
      const value = safeDecode(raw);
      // A document id names a server row the assistant may act on, so only a real id is ever sent.
      if (key === 'documentId' && !isGuid(value)) return;
      entityIds[key] = value;
    });
    // A page addressed by query (`?project=`) rather than path, e.g. the picture editor.
    if (!entityIds.projectId && isGuid(queryProject)) entityIds.projectId = queryProject;
    // A page whose kind the query decides (the Editor page's Videos/Pictures views) — see
    // `Route.byQuery`.
    const derived = route.byQuery?.(query);
    const info: AssistantPageInfo = {
      path,
      kind: derived?.kind ?? route.kind,
      title: derived?.title ?? route.title,
      entityIds,
    };
    if (route.tab) info.tab = route.tab;
    return info;
  }

  return { path, kind: 'other', title: 'ReelBolt', entityIds: {} };
}

/** The visible tab's label on the page right now, counts stripped. Browser-only; undefined otherwise. */
export function readActiveTab(): string | undefined {
  if (typeof document === 'undefined') return undefined;
  const tab = document.querySelector<HTMLElement>('main [role="tab"][aria-selected="true"]');
  const label = tab?.textContent?.replace(/\s*\(\d+\)\s*$/, '').trim();
  return label ? label.slice(0, 60) : undefined;
}

/** The page's live document title, or undefined. */
export function readDocumentTitle(): string | undefined {
  if (typeof document === 'undefined') return undefined;
  const title = document.title?.trim();
  return title ? title.slice(0, 120) : undefined;
}

export interface WelcomeContent {
  heading: string;
  copy: string;
  suggestions: readonly { title: string; prompt: string }[];
}

const WELCOME_SHAPE: Partial<Record<AssistantPageKind, readonly [name: string, suggestions: number]>> = {
  dashboard: ['dashboard', 4],
  projects: ['projects', 3],
  project: ['project', 4],
  'project-files': ['projectFiles', 3],
  'project-workflows': ['projectWorkflows', 3],
  'workflow-new': ['workflowNew', 3],
  workflow: ['workflow', 4],
  execution: ['execution', 4],
  editor: ['editor', 3],
  'editor-hub': ['editorHub', 3],
  pictures: ['pictures', 3],
  'picture-editor': ['pictureEditor', 4],
  agents: ['agents', 3],
  agent: ['agent', 3],
  settings: ['settings', 2],
};

/** Admin pages are English only: their greetings are not in the catalogues. */
const ADMIN_WELCOME: Partial<Record<AssistantPageKind, WelcomeContent>> = {
  admin: {
    heading: 'Admin overview',
    copy: 'I can explain platform configuration and point you to the right admin page.',
    suggestions: [
      { title: 'What can I configure?', prompt: 'What can I configure from the admin pages?' },
      { title: 'Check health', prompt: 'Is the platform healthy? Check what you can.' },
      { title: 'Set up providers', prompt: 'How do I set up an inference provider?' },
    ],
  },
  'admin-users': {
    heading: 'Managing users',
    copy: 'I can list users and explain how accounts work.',
    suggestions: [
      { title: 'List users', prompt: 'List the platform\'s users and who is an admin.' },
      { title: 'How do invites work?', prompt: 'How does creating a user and their first login work?' },
      { title: 'Reset a password', prompt: 'How do I reset a user\'s password?' },
    ],
  },
  'admin-providers': {
    heading: 'Inference providers',
    copy: 'I can explain capabilities, defaults and which provider kind fits.',
    suggestions: [
      { title: 'List providers', prompt: 'List the configured inference providers and their capabilities.' },
      { title: 'Capabilities explained', prompt: 'What are Chat, Vision, Transcription and Decision capabilities used for?' },
      { title: 'Add a provider', prompt: 'How do I add an OpenAI-compatible provider?' },
      { title: 'Why is a test failing?', prompt: 'A provider test failed. What are the likely causes?' },
    ],
  },
  'admin-provider': {
    heading: 'This inference provider',
    copy: 'Ask about its settings, defaults and why a test may fail.',
    suggestions: [
      { title: 'Explain its settings', prompt: 'Explain this provider\'s settings.' },
      { title: 'Is it the default?', prompt: 'Is this provider the default for its capability?' },
      { title: 'Troubleshoot', prompt: 'Why might this provider\'s connection test fail?' },
    ],
  },
  'admin-skills': {
    heading: 'Skills',
    copy: 'Ask what skills are and how agents use them.',
    suggestions: [
      { title: 'What are skills?', prompt: 'What are skills and how do agents use them?' },
      { title: 'Write a skill', prompt: 'Help me write a new skill.' },
    ],
  },
  'admin-calibration': {
    heading: 'Decision calibration',
    copy: 'I can read your calibration data, explain it and recommend a mode or thresholds. I never change settings.',
    suggestions: [
      { title: 'Explain this page', prompt: 'Explain what this calibration page shows.' },
      { title: 'Should I enable Gate?', prompt: 'Use GetDecisionCalibration and tell me, per agent, whether it is safe to move from Shadow to Gate.' },
      { title: 'Pick a threshold', prompt: 'Using my calibration data, what AcceptAt and MinMargin would you choose, and why?' },
    ],
  },
  'admin-workflow-service': {
    heading: 'Workflow service',
    copy: 'Ask about the engine, queues and running executions.',
    suggestions: [
      { title: 'Engine status', prompt: 'What is the workflow engine\'s status right now?' },
      { title: 'Stuck executions', prompt: 'How do I find and stop a stuck execution?' },
    ],
  },
};

ADMIN_WELCOME['admin-skill'] = ADMIN_WELCOME['admin-skills'];
ADMIN_WELCOME['admin-user'] = ADMIN_WELCOME['admin-users'];

function welcomeContent(t: T, name: string, suggestions: number): WelcomeContent {
  return {
    heading: t(`assistant.welcome.${name}.heading`),
    copy: t(`assistant.welcome.${name}.copy`),
    suggestions: Array.from({ length: suggestions }, (_, i) => ({
      title: t(`assistant.welcome.${name}.s${i + 1}Title`),
      prompt: t(`assistant.welcome.${name}.s${i + 1}Prompt`),
    })),
  };
}

/** Greeting + suggestion chips for the page the user is on, with a generic fallback. */
export function welcomeFor(kind: AssistantPageKind | undefined, t: T = enT): WelcomeContent {
  const shape = kind ? WELCOME_SHAPE[kind] : undefined;
  if (shape) return welcomeContent(t, shape[0], shape[1]);
  return (kind && ADMIN_WELCOME[kind]) || welcomeContent(t, 'generic', 4);
}
