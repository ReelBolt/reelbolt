// The ReelBolt trademark symbol. The owner filed an Italian application for the word mark REELBOLT
// (2026-10-27); it is PENDING, not registered, so the unregistered-mark symbol is the only one
// allowed. Never switch this to the registered-mark symbol until a registration has been granted.
//
// A leaf module with no imports on purpose: client components (the header wordmark) need the symbol
// and must not pull `legal-placeholders` (the legal identity and address) into the browser bundle.
// The brand name stays plain wherever it is data (schema.org names, metadata, titles); the symbol is
// added in markup only.
export const TRADEMARK_SYMBOL = '™';
