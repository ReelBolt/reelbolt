'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { EditImageDocument, EditImageDocumentSummary } from '../types/image-document';

/**
 * The project's layer documents — one picture each, decomposed into ordered own-pixel layers.
 *
 * <p>Keyed on the same URL the API module builds, so `mutate('/api/v1/projects/{id}/image-documents')`
 * from anywhere refreshes every mounted reader. Neither hook polls: a document only changes because
 * the user just changed it, and each of those call sites refreshes explicitly.</p>
 */

/**
 * Every layer document in the project, for the editor's picker. The route answers SUMMARIES — no
 * layers — so this is what a picker needs and nothing more; the layers come from the hook below.
 */
export function useImageDocuments(projectId: string | null | undefined) {
  return useSWR<EditImageDocumentSummary[]>(
    projectId ? `/api/v1/projects/${projectId}/image-documents` : null,
    fetcher,
  );
}

/**
 * One document with its layers, for the panel that renders them. Null `documentId` fetches nothing,
 * so a project with no document yet costs no request beyond the list above.
 */
export function useImageDocument(
  projectId: string | null | undefined,
  documentId: string | null | undefined,
) {
  return useSWR<EditImageDocument>(
    projectId && documentId
      ? `/api/v1/projects/${projectId}/image-documents/${documentId}`
      : null,
    fetcher,
  );
}
