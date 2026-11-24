import type { UserInfo } from '@/lib/types/auth';
import type { ActivityItem } from '../types';

export class FakeEventSource {
  static instances: FakeEventSource[] = [];
  url: string;
  closed = false;
  listeners = new Map<string, ((event: Event) => void)[]>();
  onerror: ((event: Event) => void) | null = null;
  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, handler: (event: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), handler]);
  }
  removeEventListener() {}
  close() {
    this.closed = true;
  }
  emit(type: string, payload: unknown) {
    const data = typeof payload === 'string' ? payload : JSON.stringify(payload);
    for (const h of this.listeners.get(type) ?? []) h(new MessageEvent(type, { data }));
  }
}

export function signInAs(org = 'org-personal') {
  const full: UserInfo = {
    email: 'tommaso@example.com',
    isPlatformAdmin: false,
    mustChangePassword: false,
    org,
    orgName: 'Personal',
    orgKind: 'Personal',
    orgRole: 'Owner',
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

export function item(over: Partial<ActivityItem> = {}): ActivityItem {
  return {
    id: 'run:1',
    type: 'render',
    state: 'running',
    projectId: 'p1',
    projectName: 'Project',
    title: 'My film',
    executionId: 'e1',
    workflowId: 'w1',
    subject: { timelineId: 't1' },
    step: null,
    progress: null,
    credits: { heldCredits: null, chargedCredits: null },
    failure: null,
    startedByYou: true,
    cancellable: true,
    createdAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    endedAt: null,
    ...over,
  };
}
