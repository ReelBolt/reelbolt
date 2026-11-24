/**
 * R03.7: the characters a title or caption keeps. Mirrors the server's allowlist
 * (`ReelBolt.Shared.Editing.OverlayTextSanitizer`): letters, digits, combining marks, spaces and
 * `. ! ? " ( ) - — / & # @ +`. Anything else is dropped when the clip is rendered, so the editor says so
 * while the person types instead of letting the render differ from the field.
 */

const DISALLOWED = /[^\p{L}\p{N}\p{M}\s.!?"()\-—/&#@+]/gu;

/** The sentence under the field when characters were (or will be) removed. */
export const REMOVED_CHARACTERS_NOTE = "Some characters aren't allowed in titles and were removed";

/** Whether `text` holds a character titles and captions do not keep. */
export function hasRemovedCharacters(text: string): boolean {
  return new RegExp(DISALLOWED.source, 'u').test(text.normalize('NFC'));
}

/** `text` without the characters titles and captions do not keep. */
export function sanitizeTitleText(text: string): string {
  return text.normalize('NFC').replace(DISALLOWED, '');
}
