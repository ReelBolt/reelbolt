'use client';

import { useEffect, useMemo } from 'react';
import { useAssistant } from './AssistantShellProvider';
import type { AssistantContext, AssistantPageRegistration } from './AssistantShellProvider';

interface AssistantPageContextProps {
  projectId?: string;
  workflowId?: string;
  /**
   * The picture editor's open document. Only the picture editor passes it, and it is the one place
   * the assistant's picture tools learn which picture to act on: the route cannot be trusted to
   * derive it, because the project that goes with it travels in the page's `?project=` query and
   * the app router writes the URL only after the render that already reports the new pathname (see
   * `AssistantShellProvider`'s location search). A page that loaded the document with those two
   * ids states them rather than asking the drawer to re-derive them from the URL.
   */
  imageDocumentId?: string;
}

/**
 * Tells the platform's one assistant which project and workflow the page on screen is about.
 *
 * The assistant is mounted once, above the router (`app/(app)/layout.tsx`); a page contributes
 * context, it never owns an assistant. That is why this renders nothing — a page drops
 * `<AssistantPageContext projectId={id} />` anywhere in its tree and forgets about it.
 *
 * The context is cleared on unmount so the next page never inherits this one's project.
 */
export function AssistantPageContext({
  projectId,
  workflowId,
  imageDocumentId,
}: AssistantPageContextProps) {
  const { setContext } = useAssistant();

  // A stable object, because the effect below depends on it: a freshly built `{ projectId }` on
  // every render would re-set the shell's context on every render of the page. Absent fields are
  // omitted rather than set to undefined, so the shell sees exactly the keys the page has.
  const context = useMemo<AssistantContext>(() => {
    const next: AssistantContext = {};
    if (projectId) next.projectId = projectId;
    if (workflowId) next.workflowId = workflowId;
    if (imageDocumentId) next.imageDocumentId = imageDocumentId;
    return next;
  }, [projectId, workflowId, imageDocumentId]);

  useEffect(() => {
    setContext(context);
    return () => setContext({});
  }, [setContext, context]);

  return null;
}

/**
 * Hands the assistant what only this page can give it — the handler proposals are applied through,
 * the steps a diff is drawn against, and the page's own review tray — for as long as it is mounted.
 *
 * The dependency array holds the three registration FIELDS rather than the registration object, so
 * a page may pass an object literal inline. What it must not do is hand over a value that changes
 * identity on every render: an inline callback, or a `review` element built without `useMemo`, makes
 * the effect re-run on every render, and every registration re-renders the shell — an infinite
 * register → setState → re-render loop.
 */
export function useAssistantPageContext(registration: AssistantPageRegistration): void {
  const { registerPage } = useAssistant();
  const { onWorkflowDiff, currentSteps, review } = registration;

  useEffect(
    () => registerPage({ onWorkflowDiff, currentSteps, review }),
    [registerPage, onWorkflowDiff, currentSteps, review],
  );
}
