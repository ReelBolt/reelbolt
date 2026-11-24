/**
 * The open phone sheets, as a stack tied to the browser history (VF8): opening a sheet pushes a history
 * entry, so Android back and the iOS swipe-back close the TOP sheet instead of leaving the editor, and
 * Escape closes it too. Closing a sheet by its own controls takes its history entry off again.
 */
interface Entry {
  id: string;
  close: () => void;
}

const stack: Entry[] = [];
let ignorePops = 0;
let attached = false;

function onPop(): void {
  if (ignorePops > 0) {
    ignorePops -= 1;
    detachIfIdle();
    return;
  }
  const top = stack.pop();
  top?.close();
  detachIfIdle();
}

function onKey(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || event.defaultPrevented) return;
  const top = stack[stack.length - 1];
  if (!top) return;
  event.preventDefault();
  top.close();
}

function attach(): void {
  if (attached || typeof window === 'undefined') return;
  attached = true;
  window.addEventListener('popstate', onPop);
  window.addEventListener('keydown', onKey);
}

function detachIfIdle(): void {
  if (!attached || stack.length > 0 || ignorePops > 0 || typeof window === 'undefined') return;
  attached = false;
  window.removeEventListener('popstate', onPop);
  window.removeEventListener('keydown', onKey);
}

/** Unregistrations waiting one tick: a sheet that re-registers at once (React strict-mode re-run of the effect) keeps its entry. */
const pending = new Map<string, ReturnType<typeof setTimeout>>();

/** Registers an open sheet. Returns the function that unregisters it (and drops its history entry). */
export function registerSheet(id: string, close: () => void): () => void {
  if (typeof window === 'undefined') return () => undefined;
  attach();
  const entry: Entry = { id, close };
  stack.push(entry);
  const waiting = pending.get(id);
  if (waiting !== undefined) {
    clearTimeout(waiting);
    pending.delete(id);
  } else {
    try {
      window.history.pushState({ ...(window.history.state as object | null), rbSheet: id }, '');
    } catch {
      // A sandboxed frame may refuse; Escape and the close button still work.
    }
  }
  return () => {
    const index = stack.indexOf(entry);
    if (index === -1) return; // already taken off by a back gesture
    stack.splice(index, 1);
    pending.set(
      id,
      setTimeout(() => {
        pending.delete(id);
        ignorePops += 1;
        try {
          window.history.back();
        } catch {
          ignorePops -= 1;
        }
        detachIfIdle();
      }, 0),
    );
  };
}

export function openSheetCount(): number {
  return stack.length;
}

/** Test seam. */
export function resetSheetHistory(): void {
  stack.length = 0;
  pending.forEach((t) => clearTimeout(t));
  pending.clear();
  ignorePops = 0;
  detachIfIdle();
}
