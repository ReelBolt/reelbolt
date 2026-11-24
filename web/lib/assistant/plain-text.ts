/**
 * Text for people. Everything the assistant puts on screen is written in the names the product uses;
 * these are the nets under that rule, for the rare id that slips through anyway.
 */

const GUID_PATTERN = '\\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\b';
const HAS_GUID = new RegExp(GUID_PATTERN, 'i');

/**
 * Removes any id from `value` and tidies the sentence around the gap. Text with no id in it is
 * returned exactly as it came — this is a net for strays, not an editor.
 */
export function plainText(value: string): string {
  if (!HAS_GUID.test(value)) return value;
  return value
    .replace(new RegExp(GUID_PATTERN, 'gi'), '')
    .replace(/[“‘][ \t]*[”’]/g, '')
    .replace(/"[ \t]*"/g, '')
    .replace(/\([ \t]*\)/g, '')
    .replace(/[ \t]{2,}/g, ' ')
    .replace(/[ \t]+([.,;:!?])/g, '$1')
    .replace(/[ \t]+\n/g, '\n')
    .trim();
}

/**
 * The message the attach control sends names the file's id in a parenthesis, because the id is the
 * only handle the assistant's own tools accept. This is the same message as a person should read it.
 */
export function withoutAttachId(value: string): string {
  return value.replace(new RegExp(`\\s*\\(project file id ${GUID_PATTERN}\\)`, 'gi'), '');
}
