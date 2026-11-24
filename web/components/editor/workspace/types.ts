import { EditorStore } from '@/lib/editing/store';
import { SyncClient } from '@/lib/editing/sync';

export interface EditorWorkspaceProps {
  store: EditorStore;
  sync: SyncClient;
  /** Reload the document from the server (after a conflict). */
  onReload: () => void;
  /** The step result the timeline was seeded from; the remove-silence dialog reads its analysis. */
  seedStepResultId?: string | null;
}

/** What the controller is given: the workspace's props with their defaults applied. */
export type EditorWorkspaceInput = Omit<EditorWorkspaceProps, 'seedStepResultId'> & { seedStepResultId: string | null };
