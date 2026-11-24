/**
 * Opt-in browser notifications for background answers. Off until the person turns the switch on
 * (and the browser allows it); never asked for on load; never carries message text — a title and a
 * thread name only.
 */

export const NOTIFY_STORAGE_KEY = 'reelbolt.assistant.browserNotify';
/** Notifications close themselves after this long. */
export const NOTIFY_CLOSE_MS = 10000;

const listeners = new Set<() => void>();

export function isNotificationSupported(): boolean {
  return typeof window !== 'undefined' && 'Notification' in window;
}

export function notificationPermission(): NotificationPermission | 'unsupported' {
  return isNotificationSupported() ? Notification.permission : 'unsupported';
}

export function getNotifySwitch(): boolean {
  try {
    return window.localStorage.getItem(NOTIFY_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

export function setNotifySwitch(on: boolean): void {
  try {
    if (on) window.localStorage.setItem(NOTIFY_STORAGE_KEY, '1');
    else window.localStorage.removeItem(NOTIFY_STORAGE_KEY);
  } catch {
    // Storage blocked: the switch simply does not stick.
  }
  listeners.forEach((l) => l());
}

export function subscribeNotifySwitch(listener: () => void): () => void {
  listeners.add(listener);
  window.addEventListener('storage', listener);
  return () => {
    listeners.delete(listener);
    window.removeEventListener('storage', listener);
  };
}

/**
 * Turns notifications on. Must be called from the click that asks for it: the permission prompt is
 * only requested here, and only when the browser has not already answered. A refusal leaves the
 * switch off and is reported back so the caller can say why; it is never retried.
 */
export async function enableNotifications(): Promise<NotificationPermission | 'unsupported'> {
  if (!isNotificationSupported()) return 'unsupported';
  let permission = Notification.permission;
  if (permission === 'default') {
    try {
      permission = await Notification.requestPermission();
    } catch {
      permission = 'denied';
    }
  }
  setNotifySwitch(permission === 'granted');
  return permission;
}

/** Granted, switched on, and the person is not looking at the open drawer. */
export function shouldNotify(state: { drawerOpen: boolean }): boolean {
  if (!isNotificationSupported()) return false;
  if (Notification.permission !== 'granted' || !getNotifySwitch()) return false;
  return document.hidden || !state.drawerOpen;
}

export function showAssistantNotification(input: {
  title: string;
  body: string;
  threadId: string;
  onClick: () => void;
}): void {
  if (!isNotificationSupported()) return;
  try {
    // `tag` is the thread, so repeats for one conversation replace each other.
    const note = new Notification(input.title, { body: input.body, tag: input.threadId, requireInteraction: false });
    note.onclick = () => {
      window.focus();
      input.onClick();
      note.close();
    };
    setTimeout(() => note.close(), NOTIFY_CLOSE_MS);
  } catch {
    // Some webviews expose the API but refuse to construct: best effort only.
  }
}
