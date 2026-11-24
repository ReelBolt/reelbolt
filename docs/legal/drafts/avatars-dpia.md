# DRAFT — NEEDS COUNSEL: data protection impact assessment for avatars

> **Applied on branch `legal/avatars-terms-bump-HELD`** (approved by the controller, Tommaso Celano,
> 2026-11-05). Measure 2 (the Terms + Privacy bump, version `2026-11-05`) is applied there; this DPIA itself is still unsigned. The branch is held: it merges only in the release that turns the `avatars`
> plan feature on. The status text below is kept as written for the record.

Status: **draft DPIA (GDPR Art. 35) for legal review, not approved, not signed.** Written 2026-11-05 for lane
A6 of the avatar foundation ([`../../design/avatars.md`](../../design/avatars.md); owner decisions AV-1..AV-13
at their recommended options). It is a working draft for counsel and the controller to complete; nothing in
it is a legal conclusion. Proposed customer-facing text is in
[`avatars-privacy-terms.md`](avatars-privacy-terms.md).

| Item | Value |
|---|---|
| Controller | Tommaso Celano, P.IVA IT02862070030 (operator of ReelBolt) |
| Contact | privacy@reelbolt.ai (no DPO appointed; see the Privacy Policy) |
| Supervisory authority | Garante per la Protezione dei Dati Personali |
| Processing assessed | Avatars, phases 1 to 3 (phase 1 assessed in full; phases 2 and 3 as far as known) |
| Must be completed | Before phase 1 is shown to customers; reviewed again before phase 2 and phase 3 |
| Review owner | Controller, with counsel |

## 1. Why a DPIA

The design treats avatar media as special-category data "to be safe" and asks for a DPIA before launch.
Indicators from Art. 35(3) and the EDPB criteria (WP248 rev.01) that look present, for counsel to confirm:

- possibly **biometric** data (face and voice) of an identifiable person (Art. 4(14), Art. 9(1)), whether or
  not anyone is identified, because a vendor's model may build a face or voice representation;
- **innovative technology** (generative video conditioned on a real person);
- data about **people who are not our users** (the depicted person), who cannot easily exercise rights
  against us and may not know we hold their data;
- **risk of serious harm** if misused (impersonation, non-consensual deepfakes, now a crime in Italy under
  art. 612-quater c.p., L. 132/2025);
- transfer to AI providers outside the EU (phase 2 and 3).

Whether the processing appears on the Garante's list of processing requiring a DPIA, and whether a DPIA is
therefore mandatory rather than advisable, is question D1.

## 2. Description of the processing

**Purpose.** Let an account holder make a reusable, consented likeness of a real adult (themselves, a
founder, a presenter) and, from phase 2, use it to show that person in AI-generated video in one project.

**Data subjects.** (a) The **depicted person** (subject), who may or may not be a user. (b) The **account
holder** who attests consent.

**Data, by phase.**

| Data | Subject | Phase | Where kept |
|---|---|---|---|
| Reference pictures (≤ 8), reference videos (≤ 3, 2..15 s), speech samples (≤ 3, 2..15 s) | depicted person | 1 | Our object storage, as ordinary project files of the one project |
| Subject's name as typed, basis (`Self` / `WrittenPermission`), adult confirmation | depicted person | 1 | Database (`project_avatars`) |
| Optional signed release (PDF or image) | depicted person, possibly signatories | 1 | Project file; never sent to any vendor |
| Consent record: attesting user id, time, verbatim statement, version, language; revoke time and user | account holder, depicted person | 1 | Database |
| Audit log of created / changed / revoked / deleted / used events (counts and outcome words only; no media, links, storage keys or the subject's name) | account holder | 1 | Database (`project_avatar_events`), survives the avatar |
| Link to a cloned voice of the same project (voice has its own consent) | depicted person | 1 | Database |
| Moderation and CSAM scan results on the reference files | depicted person | 1 | Existing file columns and hash-only audit |
| Reference media sent with each generation request; vendor file ids (MiniMax keeps uploads 7 days) | depicted person | 2 | Vendor; ids in `project_avatar_vendor_assets` |
| Generated clips showing the person, marked AI-generated | depicted person | 2 | Customer's project outputs |
| A generated still of the person and the narration audio sent to animate it | depicted person | 3 | Our storage under the avatar; vendor (fal or a self-hosted model) |

**Recipients.** Our own hosting; the content moderation provider (frames and transcript of uploads, as for
every upload, per the live Privacy Policy); from phase 2 MiniMax; from phase 3 a fal-hosted model (or a
self-hosted open model, which keeps the data on our hosts). No recipient verifies consent: the design notes
that nobody upstream checks it (MiniMax and fal put the burden on the uploader).

**Lifecycle.** Created only in the app UI by a person ticking the statement (never by the assistant or the
MCP tools); kept until a retention date of 1..730 days (default 365); revoke makes it unusable at once and
the sweep deletes it 30 days later; delete removes reference files, the release, vendor assets (best
effort), phase 3 stills and the row, and reports each outcome; the audit log is kept 3 years (AV-7, to
confirm).

**Not done (by design).** No face detection, face matching, age estimation or public-figure recognition;
no identification of anyone; no sharing of an avatar across projects or organisations; no use for training
any model by us.

## 3. Necessity and proportionality

| Principle | How the design meets it | Open point |
|---|---|---|
| Lawfulness (Art. 6, 9) | Explicit consent of the subject (Art. 9(2)(a)), collected by the account holder, attested verbatim in the subject-facing wording, versioned, in the language shown | Evidence of the subject's own consent (D3); controller or processor role (D4) |
| Purpose limitation | Consent and use limited to "videos in this project"; one project per avatar (AV-1) | Org-wide library later would need a new consent wording |
| Data minimisation | Caps on number, length and size of references; audit log holds no media or name; release never leaves our systems | Whether speech samples are needed before phase 2 (they are collected in phase 1) |
| Accuracy | A changed reference file makes the avatar unusable until re-attested (digest check) | |
| Storage limitation | Retention date chosen by the user, max 2 years; revoked grace 30 days; sweep every 6 h | Audit 3 years (D6) |
| Integrity and confidentiality | Project access rules (404 across organisations); verified email to create; files screened before use; quarantine and output holds respected | Vendor-side security (D5) |
| Rights of the depicted person | Revoke and delete by the account holder; privacy@ channel drafted for the subject | Platform-side revoke not yet designed (D7) |
| Transparency | Privacy Policy Part A/B drafts; visible AI label by default and machine-readable marking (phase 2) | Information to the subject is the account holder's job; do we need to supply a notice template (D3) |

## 4. Risks to the people concerned

Likelihood and severity are a first estimate for counsel to challenge (L/M/H).

| # | Risk | Likelihood | Severity | Mitigation in the design | Residual |
|---|---|---|---|---|---|
| R1 | Avatar of someone who did not consent (false attestation) | M | H | Verbatim attestation, verified email, audit trail, Terms clause and indemnity, abuse reporting, revoke and delete, visible label | **High**: nothing detects a false attestation |
| R2 | Avatar of a minor | L | H | Adult confirmation required, minors refused outright (AV-12), `sexual/minors` moderation always blocks | **Medium/High**: no age check exists |
| R3 | Public figure impersonation | M | H | Banned unless the subject is the account holder (AV-8); Terms; reports | Medium: no recognition of public figures |
| R4 | Sexual or degrading content of the subject | L | H | Moderation of references; NCII and sexual content banned regardless of consent; generation-prompt moderation (existing) | Medium (moderation thresholds uncalibrated) |
| R5 | Vendor keeps the subject's media after our delete, or trains on it | M | M | Delete removes vendor assets where the vendor allows; Privacy text says copies follow the vendor's terms; fal `X-Fal-Store-IO: 0` planned for phase 3 | **Medium**: vendor terms not read in full (MiniMax platform terms unread) |
| R6 | Transfer to a provider outside the EU without adequate safeguards | M | M | SCCs per the live Privacy Policy | To confirm per vendor (D5) |
| R7 | Use continues after consent is withdrawn | L | M | Revoke is immediate; each take re-checks before submit; cache, resume, retry and reuse re-check consent | Low; a take already submitted finishes |
| R8 | Subject cannot reach us (not a user) | M | M | privacy@ and support@ in the drafted Privacy/AUP text | Medium until a platform-side revoke exists (D7) |
| R9 | Generated video misleads viewers (deepfake) | M | M | Machine-readable marking and visible label by default; label removal forbidden by Terms | Medium: a customer can turn the visible label off with a self-declaration |
| R10 | Breach of our storage exposes faces and voices | L | H | Existing access controls and storage; no new public link type | Low/Medium |
| R11 | Reference files reused as ordinary footage outside the consent | L | M | Files tagged as avatar references; assistant told not to use them as footage | Low |
| R12 | Audit log itself becomes personal data kept too long | L | L | No media, links or name; 3 years then purged | Low |

## 5. Measures still to decide or build before launch

1. Counsel-approved consent wording in both languages (version `avatar-consent-2026-11`, or a new id).
2. Terms + Privacy bump at phase 1 launch (see the companion draft, section 2).
3. A platform-side revoke for a subject's request, with a short runbook (who decides, what we tell each
   side, time limits).
4. Vendor terms read in full for each provider that will receive avatar media, before its phase ships;
   transfer mechanism recorded; deletion route per vendor recorded in `docs/provider-data-handling.md`.
5. Phase 2 marking (metadata now, C2PA in phase 2, AV-9) live before any avatar clip can be generated.
6. Decide whether a release template is offered to customers (en and it).
7. Re-assess this DPIA before phase 2 and before phase 3, and whenever an avatar can be shared beyond one
   project.

## 6. Questions for counsel and the controller

- **D1** Is a DPIA mandatory here (Garante list, Art. 35(3)) or advisable? Does any residual risk call for
  prior consultation under Art. 36?
- **D2** Is conditioning a generative model on face pictures and voice processing of biometric data under
  Art. 4(14) and Art. 9(1), given we do not identify anyone? (No EDPB or Garante position was found.)
- **D3** Is the account holder's attestation plus an optional release adequate evidence of the subject's
  explicit consent (Art. 7(1))? Must we supply a subject information notice and release template?
- **D4** Controller or processor for the subject's data? DPA annex needed?
- **D5** Transfer mechanism and retention for MiniMax and fal; are their terms compatible with our purpose
  (impersonation bans, training rights)?
- **D6** Audit log retention: 3 years right? Lawful basis?
- **D7** May we revoke on the subject's request without the account holder's agreement? Time limits?
- **D8** Is residual risk R1 (false attestation) acceptable without any identity or face-match check,
  given that such a check would itself be biometric processing?
- **D9** AI Act Art. 50: provider or deployer role, and whether the planned marking meets 50(2).

## 7. Sign-off

| Role | Name | Date | Decision |
|---|---|---|---|
| Counsel | | | |
| Controller | Tommaso Celano | | |
