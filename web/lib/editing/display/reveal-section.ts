/**
 * Scrolls a section of a panel into view and puts the keyboard focus on it. The panel may not be mounted yet (the
 * workspace or the Compose entry was just switched), so this looks for the element for about a second.
 */
export function revealElement(id: string, tries = 20): void {
  if (typeof document === 'undefined') return;
  const el = document.getElementById(id);
  if (!el) {
    if (tries > 0) window.setTimeout(() => revealElement(id, tries - 1), 50);
    return;
  }
  el.scrollIntoView?.({ block: 'center' });
  if (!el.matches('input, button, select, textarea, [tabindex]')) el.setAttribute('tabindex', '-1');
  el.focus({ preventScroll: true });
}
