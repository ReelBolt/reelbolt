# Recipe presets (internal)

Built-in recipes and how well their app inputs use the dynamic-input features (typed inputs, `showWhen`, brief fields, mapped bindings). Non-video-editing recipes only; the footage (derush) family, testimonial reel, region effect, green-screen insert and the subtitles family are audited elsewhere. Egress consent stays off in every recipe and no input can turn it on.

## Audit

**Quick-win promo family (starter promo, `-ds`, `-plates`, `-plates-ds`, `lean-context-promo`).** Turns the project's code (or approved brand and product profile) into a short promo. Inputs were already minimal and typed: language, length, shape, style, design (brand variants), notes, call to action, and the two model pickers under "More options". Verdict: good, but there was no way to ask for a tone although the script writer reads it. Change: added the brief `tone` field to every recipe of the family (also the plates variants, which keep their `backgroundLook` text). No step changed, so no version bump.

**Cinematic feature spotlight.** A cinematic video about one feature. Brief fields derive from what the steps read (language, length, tone, shape, style) plus "Who it is for", bound into the request. Verdict: good as it is; nothing added, to stay inside the eight visible inputs.

**Story-arc launch trailer.** A launch trailer with a story arc. Same shape as the spotlight: derived brief fields plus "Who it is for". Verdict: good; unchanged.

**Narrated product story.** Analyses uploaded clips, writes a story first, then cuts to it, narrates and captions. Inputs: derived brief fields (language, length, voice, tone and so on), clips slot, audience, a Captions switch bound into the compile, and an Ending fade switch under "More options". Verdict: good, a fair use of mapped bindings; unchanged. It is a footage recipe by steps, so its step content is untouched here.

**Product profile from code.** Builds the product profile for a design system from the code. It is provisioned by the design-system build route with its own estimate and confirmation, not by a run form, so it has no per-run settings worth exposing. Verdict: leave as is; deliberately no inputs.

**Avatar clip.** One recipe for both avatar templates. A "Lite mode" switch reveals either "What they say" (cheap still animated to speak) or "What they do", length and video model (full), with shape always shown and the model pickers under the branch they belong to. Verdict: the best showcase of `showWhen` already; unchanged. The permission to send media stays off.
