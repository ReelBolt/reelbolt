// Plain, framework-free validation shared by the contact form's client-side
// component (for inline/on-blur feedback) and the `/api/contact` route (the
// authoritative, server-side check). No React/Next imports — must run
// unmodified in both a browser bundle and a Node/Edge route handler.

export interface ValidationError {
  field: string;
  message: string;
}

export const CONTACT_TOPICS = ['general', 'enterprise', 'billing', 'runner'] as const;
export type ContactTopic = (typeof CONTACT_TOPICS)[number];

export const CONTACT_TOPIC_LABELS: Record<ContactTopic, string> = {
  general: 'General question',
  enterprise: 'Enterprise',
  billing: 'Billing',
  runner: 'Rendering on my own computer',
};

export function isContactTopic(value: unknown): value is ContactTopic {
  return typeof value === 'string' && (CONTACT_TOPICS as readonly string[]).includes(value);
}

/** Maps a query-string value to a topic, falling back to `general` for anything unknown. */
export function topicFromParam(value: string | null | undefined): ContactTopic {
  return isContactTopic(value) ? value : 'general';
}

export interface ContactInput {
  name: string;
  email: string;
  company?: string;
  topic?: string;
  message?: string;
  website?: string;
  /** Optional, separate newsletter opt-in. Absent means no; the enquiry never depends on it. */
  marketingOptIn?: boolean;
  /** Optional newsletter first name; read only when marketingOptIn is true, never validated as a field. */
  firstName?: string;
}

// Reasonably strict without trying to be a full RFC 5322 implementation.
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** The trimmed text of a field. Absent (undefined/null) reads as empty; any other non-string
 * (number, object, array, boolean) is `null`, so a client's bad type is a 400, never a thrown `.trim()`. */
function textField(value: unknown): string | null {
  if (value === undefined || value === null) return '';
  return typeof value === 'string' ? value.trim() : null;
}

export function validateContact(input: ContactInput): ValidationError[] {
  const errors: ValidationError[] = [];

  const name = textField(input.name);
  if (name === null) {
    errors.push({ field: 'name', message: 'Invalid submission.' });
  } else if (!name) {
    errors.push({ field: 'name', message: 'Please enter your name.' });
  } else if (name.length < 2 || name.length > 100) {
    errors.push({ field: 'name', message: 'Name must be between 2 and 100 characters.' });
  }

  const email = textField(input.email);
  if (email === null) {
    errors.push({ field: 'email', message: 'Invalid submission.' });
  } else if (!email) {
    errors.push({ field: 'email', message: 'Please enter your email address.' });
  } else if (email.length > 254 || !EMAIL_RE.test(email)) {
    errors.push({ field: 'email', message: 'Please enter a valid email address.' });
  }

  const company = textField(input.company);
  if (company === null) {
    errors.push({ field: 'company', message: 'Invalid submission.' });
  } else if (company.length > 100) {
    errors.push({ field: 'company', message: 'Company name must be 100 characters or fewer.' });
  }

  // Absent means "general"; anything present must be one of the known topics.
  if (input.topic !== undefined && input.topic !== null && !isContactTopic(input.topic)) {
    errors.push({ field: 'topic', message: 'Please choose a topic from the list.' });
  }

  const message = textField(input.message);
  if (message === null) {
    errors.push({ field: 'message', message: 'Invalid submission.' });
  } else if (!message) {
    errors.push({ field: 'message', message: 'Please enter a message.' });
  } else if (message.length < 20 || message.length > 5000) {
    errors.push({ field: 'message', message: 'Message must be between 20 and 5000 characters.' });
  }

  if (input.marketingOptIn !== undefined && typeof input.marketingOptIn !== 'boolean') {
    errors.push({ field: 'marketingOptIn', message: 'Invalid submission.' });
  }

  // Honeypot: a real visitor never fills this in. The API route already
  // short-circuits (silently, before validation runs) when it's filled, but
  // flag it here too so this function is a complete, honest description of
  // "what a valid submission looks like" for any other caller.
  const website = textField(input.website);
  if (website === null || website.length > 0) {
    errors.push({ field: 'website', message: 'Invalid submission.' });
  }

  return errors;
}
