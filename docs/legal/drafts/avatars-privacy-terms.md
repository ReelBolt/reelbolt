# DRAFT — NEEDS COUNSEL: avatars in the Terms, the Privacy Policy and the Acceptable Use Policy

> **Applied on branch `legal/avatars-terms-bump-HELD`** (approved by the controller, Tommaso Celano,
> 2026-11-05). Phase 1 text only: Terms 9.1, 11 and 18, Acceptable Use Policy sections 1, 2 and 5, Privacy Part A (sections 3, 4, 10, 11 and 12), and the AI Act Art. 50 labelling duties, as Terms and Privacy version `2026-11-05` (migration `AvatarsLegalVersions`). Privacy Part B (phases 2 and 3) is NOT applied; it comes later as a Privacy-only bump. The branch is held: it merges only in the release that turns the `avatars`
> plan feature on. The status text below is kept as written for the record.

Status: **draft for legal review, not published, not in force.** Written 2026-11-05 for lane A6 of the
avatar foundation ([`../../design/avatars.md`](../../design/avatars.md); owner decisions AV-1..AV-13 taken at
their recommended options). Nothing here changes a live page or a `legal_documents` version. The live
versions stay **Terms 2026-10-26** and **Privacy 2026-10-31** until counsel approves this text and the owner
decides the bump below.

This folder (`docs/legal/drafts/`) is internal. It is not on the website, not in the user guide, and not in
the assistant's documentation search (the search reads only top-level `docs/*.md`, `user-guide/**` and
`assistant-kb/**`). Nothing in it is a legal conclusion; every paragraph meant for customers is a proposal
that counsel may rewrite.

Companion: [`avatars-dpia.md`](avatars-dpia.md) (the data protection impact assessment draft).

## 1. What changes and why the current text does not fit

An **avatar** is a person's likeness (reference pictures, short reference videos, short speech samples and
an optional link to a cloned voice of the same project) held under a recorded consent, so that a video model
can show that person in a generated clip. Phase 1 only stores the avatar and its consent record; no avatar
media leaves our systems for any AI provider until phase 2 (a MiniMax video model) and phase 3 (a cheaper
still-then-animate path, candidate models on fal).

**The live Terms forbid the feature as written.** Terms section 9.1 (and the Acceptable Use Policy section 1,
which repeats it) says the user must not create "any image, video or audio that impersonates, depicts or
purports to be a real person" or "a deepfake, or any other synthetic media that depicts an identifiable
person". The very next paragraph already permits a consented voice clone, so 9.1 has an internal tension
today; avatars make it a direct conflict. 9.1 has to be rewritten into a **consent-based** rule before the
Avatars tab is shown to any customer.

**The live Privacy Policy does not describe the processing.** It covers "uploaded content" under contract
(Art. 6(1)(b)) and says nothing about likeness, voice, consent records of a third person, or special-category
treatment. The design treats avatar media as special-category data to be safe (explicit consent, DPIA,
bounded retention).

## 2. Bump recommendation

**Recommendation: one combined Terms + Privacy bump (Terms re-acceptance) at the phase 1 customer launch,
then a Privacy-only acknowledgement at phase 2.**

1. **Phase 1 launch: Terms bump (re-acceptance) together with a Privacy bump.**
   - The Terms change is material: section 9.1 moves from "never depict an identifiable person" to "only
     with recorded consent, under these conditions", and it adds new obligations the user did not accept
     (keep a record of the subject's consent, revoke on withdrawal, no minors, no public figures unless the
     user is that person, keep the AI label, respond to our requests about a complaint, a specific
     indemnity). Section 19 of the live Terms promises notice before a material change takes effect; under
     the repository's rule a Terms bump is the re-acceptance mechanism (`/accept-terms`, as on 2026-10-26),
     and it also stops anyone who has not accepted the new rules from creating an avatar.
   - The Privacy bump rides along (new data category, new purpose, new lawful basis, new retention rule,
     the depicted person's own channel). Shown together on `/accept-terms`, as in 2026-10-26.
   - Put the **Art. 50 deployer duties** (keep the label, disclose deepfakes) in this Terms version already,
     even though marking is enforced only in phase 2, so phase 2 does not need a second re-acceptance.
   - Do not bump before the feature is visible: the text describes a feature, and a bump for an invisible
     feature costs every user a re-acceptance for nothing. Ship the new versions in the same release that
     turns the plan feature `avatars` on.
2. **Phase 2 launch: Privacy-only bump (acknowledgement).** It adds the provider rows (MiniMax receives
   avatar media), the transfer statement for that provider, and the marking paragraph. These are
   information about processing the user already agreed to in substance in the phase 1 Terms, so an
   acknowledgement is enough **if** counsel agrees the phase 1 Terms already covered sending avatar media to
   providers (the drafted 9.1 and 11 text below says so). If counsel wants the provider list in the
   contract itself, phase 2 becomes a Terms bump too. **Question for counsel (Q1).**
3. **Phase 3 launch:** another Privacy-only acknowledgement for the fal row(s), on the same reasoning.

Engineering note for whoever ships it (not legal text): bump with a migration in the style of
`LaunchAlphaLegalVersions` / `PrivacySafetyScreeningVersion` (move each `legal_documents` row only while it
still holds the previous shipped default; never touch an operator-published version), update
`LegalDocumentSeeder`, `TERMS_LAST_UPDATED` / `LAST_UPDATED` in `site/lib/legal-placeholders.ts`, and the
version list in `docs/marketing-consent.md` "The privacy-policy acknowledgement". The version ids are the
publication dates, chosen at release time.

## 3. The consent statement (one version, two languages)

Version id: **`avatar-consent-2026-11`** (stored in `project_avatars.consent_statement_version`; the
language shown is stored in `consent_locale`). Lane A2 copies these two strings verbatim into the server
constants; the server compares ordinally, so any edit after counsel review is a **new version id**, never an
in-place change.

**English (`en`), verbatim from the design document:**

> I confirm that I am the person shown and heard in these files, or that I have that person's written
> permission; that the person is 18 or older; that they have given informed consent to their face, body and
> voice being used to generate AI videos in this project, and to these files being sent for that purpose to
> the AI providers this project uses; that I will keep a record of that consent; and that I will revoke or
> delete this avatar if they withdraw it.

As one line (for the constant): `I confirm that I am the person shown and heard in these files, or that I have that person's written permission; that the person is 18 or older; that they have given informed consent to their face, body and voice being used to generate AI videos in this project, and to these files being sent for that purpose to the AI providers this project uses; that I will keep a record of that consent; and that I will revoke or delete this avatar if they withdraw it.`

**Italian (`it`). The design document has no Italian text; this translation is drafted here and is
NEEDS COUNSEL (Q2):**

> Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto
> di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del
> suo volto, del suo corpo e della sua voce per generare video con l'intelligenza artificiale in questo
> progetto, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da questo
> progetto; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la
> persona ritira il consenso.

As one line (for the constant): `Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del suo volto, del suo corpo e della sua voce per generare video con l'intelligenza artificiale in questo progetto, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la persona ritira il consenso.`

Translation notes: "provider" stays "provider" per `docs/i18n-glossary.md`; "consent" is "consenso";
"written permission" is "permesso scritto" (counsel may prefer "autorizzazione scritta", the usual term
for a liberatoria under art. 96 L. 633/1941, which would be a new version id if changed after A2 ships).

Drafting points for counsel on the statement itself (not changes; questions): the statement is an
attestation by the account holder, not the subject's own consent, so the subject's consent is collected
outside the product (Q3); "these files" covers the reference files attached at that moment, and changing
the references re-asks the whole statement (`PUT .../references`).

## 3a. Further statements (avatar objects initiative, drafts for counsel)

> **Published 2026-11-17** (owner's decision, option B; Terms and Privacy `2026-11-17`, migration
> `AvatarLibraryLegalVersions`, every user re-accepts the Terms): Terms 9.1 (pictures, the workspace avatar library,
> avatars of objects, variants, withdrawal) and 11, Acceptable Use 2, and in the Privacy Policy the fal.ai row
> (reference-to-video, scene pictures, picture editor, lip-sync, segmentation and the model makers), the Vision check
> and the avatar paragraphs. The statements themselves stay as quoted below. No lawyer reviewed the published text.

Added 2026-11-16 by the avatar objects initiative ([`../../design/avatar-objects.md`](../../design/avatar-objects.md),
decisions AO-1, AO-2 and AO-5 .. AO-7). Quoted exactly from the server constants in
`inference/src/ReelBolt.Shared/Avatars/` (`AvatarConsentStatement`, `AvatarObjectStatement`,
`AvatarWorkspaceConsentStatement`, `AvatarWorkspaceObjectStatement`), which tests pin by SHA-256. Same rules as
section 3: compared ordinally, stored with the version and the language shown, and any change after counsel review
is a **new version id**. All wording below is a draft for counsel.

### 3a.1 The person statement that covers pictures (`avatar-consent-2026-12`, current)

The 2026-11 text covers "AI videos" only. This version is the 2026-11 text with "to generate AI videos" changed to
"to generate AI videos and pictures" ("per generare video e immagini" in Italian). Every new attestation and every
re-attestation is made under it. A row attested under `avatar-consent-2026-11` keeps working for video and is refused
in a picture until it is re-attested (decision AO-2).

**English (`en`):**

> I confirm that I am the person shown and heard in these files, or that I have that person's written
> permission; that the person is 18 or older; that they have given informed consent to their face, body and
> voice being used to generate AI videos and pictures in this project, and to these files being sent for that
> purpose to the AI providers this project uses; that I will keep a record of that consent; and that I will
> revoke or delete this avatar if they withdraw it.

As one line: `I confirm that I am the person shown and heard in these files, or that I have that person's written permission; that the person is 18 or older; that they have given informed consent to their face, body and voice being used to generate AI videos and pictures in this project, and to these files being sent for that purpose to the AI providers this project uses; that I will keep a record of that consent; and that I will revoke or delete this avatar if they withdraw it.`

**Italian (`it`):**

> Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto
> di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del
> suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in
> questo progetto, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da
> questo progetto; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la
> persona ritira il consenso.

As one line: `Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in questo progetto, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la persona ritira il consenso.`

### 3a.2 The object statement (`avatar-object-2026-11`)

What a member ticks when they make an avatar of an object, a product or a place. It is not a person's consent (an
object has none to give): it is the member's own attestation that the files show no identifiable person and that
they may use them. An automatic Vision check refuses a picture that shows a person; it is skipped when no Vision
provider is configured and fails closed when the configured one errors (decision AO-1).

**English (`en`):**

> I confirm that these files show an object, a product or a place and no identifiable person, and that I have
> the right to use them to generate AI videos and pictures in this project and to send them for that purpose to
> the AI providers this project uses.

As one line: `I confirm that these files show an object, a product or a place and no identifiable person, and that I have the right to use them to generate AI videos and pictures in this project and to send them for that purpose to the AI providers this project uses.`

**Italian (`it`):**

> Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che
> ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in questo progetto e di
> inviarli, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto.

As one line: `Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in questo progetto e di inviarli, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto.`

### 3a.3 The workspace statements (the workspace avatar library)

An Owner or Admin attests one of these when they save an avatar to the workspace avatar library or confirm a library
item again. They cover ANY project of the workspace, which a project statement never does: publishing a person to
the library is a new attestation, never a widening of the project one. The text is stored on the library item and on
every project import of it.

**Person, `avatar-consent-workspace-2026-11`, English (`en`):**

> I confirm that I am the person shown and heard in these files, or that I have that person's written
> permission; that the person is 18 or older; that they have given informed consent to their face, body and
> voice being used to generate AI videos and pictures in any project of this workspace, and to these files being
> sent for that purpose to the AI providers those projects use; that I will keep a record of that consent; and
> that I will revoke or delete this avatar if they withdraw it.

As one line: `I confirm that I am the person shown and heard in these files, or that I have that person's written permission; that the person is 18 or older; that they have given informed consent to their face, body and voice being used to generate AI videos and pictures in any project of this workspace, and to these files being sent for that purpose to the AI providers those projects use; that I will keep a record of that consent; and that I will revoke or delete this avatar if they withdraw it.`

**Person, `avatar-consent-workspace-2026-11`, Italian (`it`):**

> Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto
> di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del
> suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in
> qualsiasi progetto di questo spazio di lavoro, e all'invio di questi file, a tale scopo, ai provider di
> intelligenza artificiale usati da quei progetti; che conserverò una prova di tale consenso; e che revocherò
> o eliminerò questo avatar se la persona ritira il consenso.

As one line: `Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in qualsiasi progetto di questo spazio di lavoro, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da quei progetti; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la persona ritira il consenso.`

**Object, `avatar-object-workspace-2026-11`, English (`en`):**

> I confirm that these files show an object, a product or a place and no identifiable person, and that I have
> the right to use them to generate AI videos and pictures in any project of this workspace and to send them for
> that purpose to the AI providers those projects use.

As one line: `I confirm that these files show an object, a product or a place and no identifiable person, and that I have the right to use them to generate AI videos and pictures in any project of this workspace and to send them for that purpose to the AI providers those projects use.`

**Object, `avatar-object-workspace-2026-11`, Italian (`it`):**

> Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che
> ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in qualsiasi progetto di
> questo spazio di lavoro e di inviarli, a tale scopo, ai provider di intelligenza artificiale usati da quei
> progetti.

As one line: `Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in qualsiasi progetto di questo spazio di lavoro e di inviarli, a tale scopo, ai provider di intelligenza artificiale usati da quei progetti.`

Questions these add for counsel: **Q15** (below).

## 4. Terms & Conditions: proposed text

Target file `site/app/(en)/legal/terms/page.tsx`. Replace section **9.1** whole; add one paragraph to
section **11**; add one sentence to section **18**. No other section changes.

### 4.1 Replace section 9.1 (heading and body)

> **9.1 Real people: impersonation, likeness and consent**
>
> **ReelBolt is not a tool for impersonating real people.** You may show or make a real person's likeness or
> voice with the Service only in the ways this section allows. You must not create, or attempt to create:
>
> - any image, video or audio that impersonates, or purports to be, a real person who has not agreed to it,
>   whether a celebrity, a public figure or a private individual;
> - a deepfake or any other synthetic image, video or audio of an identifiable person, except an avatar or
>   a cloned voice made with that person's consent as described below;
> - a likeness or voice of anyone under 18, in any form;
> - a likeness or voice of a public figure, unless you are that person and the avatar or voice is your own;
> - non-consensual intimate imagery (NCII), or any other sexually explicit content, of anyone, with or
>   without consent;
> - new words put into the mouth of a real person in existing footage of them (for example by re-syncing
>   their lips), which a consent to an avatar or a voice does not cover;
> - anything intended to deceive, defraud, harass or harm any person.
>
> **Footage, images and audio you upload.** Where you upload footage, images or audio of a person, or use a
> voice, you confirm that you have that person's documented consent to use it as you intend.
>
> **Avatars and cloned voices.** The Service lets you create an avatar (reference pictures, short videos and
> short speech samples of a person, used to show that person in AI-generated video) or a cloned voice only
> after you confirm, in the words the Service shows you, that you are that person or have their written
> permission, that they are 18 or older, that they have given informed consent to that use and to their
> files being sent to the AI providers the project uses, that you will keep a record of their consent, and
> that you will revoke or delete the avatar or voice if they withdraw it. You must keep that record for as
> long as the avatar or voice exists and for a reasonable period after, and give it to us if we ask for it
> in connection with a complaint or a request from an authority. An avatar or a cloned voice can be used only
> in the project it was made in.
>
> **Labelling AI-generated video.** Video made with an avatar is marked by the Service as AI-generated, in
> the file's data and, unless you choose otherwise, with a visible label. You must not remove, hide or alter
> that marking or label. If you turn the visible label off, you must tell the people who see the video, in
> another clear way, that it was generated or altered with AI.
>
> **When consent ends.** If the person withdraws consent, you must revoke or delete the avatar or voice at
> once. Revoking stops any further use, including use in runs already in progress before their next
> generation. Videos already made are your files; you are responsible for deciding whether to keep, use or
> delete them, and you must not use them in a way the person has not agreed to.
>
> **What we may do.** We may revoke or delete an avatar or a cloned voice, and suspend or terminate your
> access to the Service as described in Section 15, if we believe this section has been broken or if the
> person shown asks us to and we cannot confirm their consent. We keep a record of when an avatar or voice
> was created, revoked and deleted, without the pictures or recordings, and we may give it to an authority
> where the law requires it. Our Acceptable Use Policy restates these rules and explains how to report abuse.

Notes for counsel on 9.1: the "public figure" ban is owner decision AV-8 (banned unless the basis is `Self`);
"minors refused outright" is AV-12; the lip-sync exclusion follows the design's "Risks". Whether "a
reasonable period after" should be a fixed period (and which) is Q8. Whether we may revoke on the subject's
request alone, before hearing the account holder, is Q9.

### 4.2 Add to section 11 (Third-party AI providers and your content), after its first paragraph

> If a step uses an avatar, the avatar's reference pictures, short videos and speech samples are among the
> content sent to the provider that generates the video. The Privacy Policy names those providers.

(Phase 1 ships no such step; the sentence is in the phase 1 Terms so phase 2 needs no re-acceptance, see
section 2. Counsel may prefer to hold it to phase 2, Q1.)

### 4.3 Add to section 18 (Indemnity), as a second sentence of the paragraph

> This includes any claim by a person whose likeness or voice you used, or caused to be used, through the
> Service without the consent this agreement requires.

Note: the live indemnity applies only to business users; a consumer is not covered. Whether a consumer
should be able to create an avatar of anyone other than themselves at all is Q10.

## 5. Acceptable Use Policy: proposed text

Target file `site/app/(en)/legal/acceptable-use/page.tsx`. The page says the Terms are binding and it only
restates them, so it moves with the Terms bump (it has no `legal_documents` row of its own).

- Replace section **1** (heading "No impersonation or synthetic likeness of real people") with the heading
  "Real people: impersonation, likeness and consent" and the bullet list from 4.1 above, unchanged.
- Replace section **2** body with the paragraphs "Footage, images and audio you upload", "Avatars and cloned
  voices" and "Labelling AI-generated video" from 4.1, shortened as counsel sees fit.
- Add to section **5** (Report abuse), after the first sentence:

> If you are the person shown in an avatar or heard in a cloned voice and you did not agree, or you have
> withdrawn your consent, you can also write to privacy@reelbolt.ai. We will make the avatar or voice
> unusable while we look into it and tell you what we did.

(The mailbox is the live `PRIVACY_EMAIL`; render it with the constant, not as typed text.)

## 6. Privacy Policy: proposed text

Target file `site/app/(en)/legal/privacy/page.tsx`. Part A is needed **before phase 1 ships** (we store the
media and the consent record). Part B is needed **before phase 2 ships** (media go to a provider).

### Part A (phase 1)

**6.A1 Section 3 (What we collect): add a bullet after "Uploaded content".**

> **Avatars.** If you create an avatar of a person, we keep the reference pictures, short videos and short
> speech samples you attach to it, the person's name as you type it, whether you are that person or have
> their written permission, your confirmation that they are 18 or older, a signed release if you choose to
> upload one, and the consent record: who confirmed, when, the exact statement shown, its version and its
> language. We also keep a log of when the avatar was created, changed, revoked and deleted. The log holds
> no pictures, recordings, links or the person's name. The person shown may not be a user of the Service.

**6.A2 Section 4 (Why we collect it, and our lawful basis): add a bullet after "Uploaded content".**

> **Avatars** are processed only to show the person in AI-generated video in the project where the avatar
> was made, and to keep the proof that they agreed. We treat a person's face, body and voice in an avatar
> with the care the law requires for sensitive data. Lawful basis: for the person shown, their explicit
> consent (Article 6(1)(a) and, where Article 9 GDPR applies, Article 9(2)(a) GDPR), which the account
> holder collects and confirms to us before the avatar can be created; for you as the account holder,
> performance of our contract with you. The consent record and the log are kept so that we can show when and
> how consent was confirmed and that the avatar was deleted. Lawful basis: our legitimate interest in proving
> consent and in defending and responding to complaints (Article 6(1)(f) GDPR) and, where it applies, our
> legal obligation. A signed release you upload is kept only as that proof and is never sent to any AI
> provider.

Counsel: the controller/processor split for the depicted person's data is open (Q4): either ReelBolt is
controller for avatar data (as it is for uploads today) relying on consent collected by the customer, or
the customer is controller and ReelBolt its processor under the DPA, in which case this bullet changes
wording and the DPA needs an avatar annex.

**6.A3 Section 4, the "Safety screening" paragraph: add one sentence.**

> Pictures, videos and audio attached to an avatar are screened before the avatar can be used, and an
> avatar cannot be created from a file that has not been screened yet.

(Accurate only when screening is enabled on the deployment; for the hosted service it is, per
`docs/content-moderation.md`. If counsel prefers no promise, drop it.)

**6.A4 Section 10 (Retention): add a paragraph before `DISCLOSED_DATA_RETENTION_STATEMENT`.**

> An avatar and its files are kept until the date you choose when you create it (between 1 day and 2 years;
> one year unless you change it), and are then deleted automatically. If you revoke the consent, the avatar
> can no longer be used from that moment and is deleted 30 days later; you can delete it sooner yourself.
> Deleting an avatar deletes its reference files and any release you uploaded. The log of when the avatar was
> created, revoked and deleted, which holds no pictures, recordings or name, is kept for 3 years and then
> deleted. Videos you made with the avatar are your files and follow the retention of your other content.

(The 3-year log is AV-7, "confirm with counsel", Q7. The 30-day revoked grace and the 6-hourly sweep are
the design's constants.)

**6.A5 Section 11 (Your rights): add a paragraph after the list.**

> **If you are shown in an avatar.** You may not have an account with us. You can withdraw your consent
> through the account holder who made the avatar, or by writing to privacy@reelbolt.ai. When you write to
> us, we make the avatar unusable while we look into it, tell the account holder, and delete it if consent
> cannot be confirmed. Copies a provider already received are covered by that provider's own terms (see
> Section 6.1).

(Render the mailbox with `PRIVACY_EMAIL`. Engineering consequence: the design gives no platform-side
revoke today; an admin revoke route or runbook is needed before this promise is published, Q9.)

**6.A6 Section 12 (Children): add a sentence.**

> Avatars and cloned voices of anyone under 18 are not allowed, and the Service asks you to confirm the
> person's age before an avatar is created. We do not check age ourselves.

### Part B (phase 2, then phase 3)

**6.B1 Section 6.1 provider table** (edited in `site/lib/provider-data-handling.ts`, not in the page, per
`docs/provider-data-handling.md` "Adding or changing a row"). Extend the MiniMax row's `sent` with:

> For a video that uses an avatar: the avatar's reference pictures, short reference videos and short speech
> samples, sent with each request (and, if the provider keeps uploaded files, for up to the time its terms
> state).

Phase 3 extends the fal row the same way, naming the still and the narration audio sent to animate it.
Before either row ships, the vendor's platform terms must be read (design "Vendor facts" M13) and the
retention of request inputs stated only if read from the terms.

**6.B2 Section 9 (International transfers): add a sentence** naming the transfer mechanism for each
provider that receives avatar media, once counsel confirms where it processes (Q5).

**6.B3 New paragraph in Section 4 (or a new subsection), on marking.**

> **Marking AI-generated video.** Video made with an avatar carries a machine-readable mark that it was
> generated with AI and, unless the account holder turns it off and discloses it another way, a visible
> label. We record in the video's details that it was AI-generated and which avatar was used.

## 7. Questions for counsel

- **Q1** Phase 2 provider rows: Privacy-only acknowledgement enough, given the drafted 9.1 and 11 already
  say avatar media go to "the AI providers the project uses"? Or must the provider list be in the Terms?
- **Q2** Approve the Italian consent statement (and "permesso scritto" versus "autorizzazione scritta").
- **Q3** Is an account holder's attestation, plus an optional signed release (AV-4 B), a sufficient way to
  evidence the subject's explicit consent under Art. 7(1) and Art. 9(2)(a)? Should a release be required
  for `WrittenPermission`, and should the product offer a template release (en and it)?
- **Q4** Controller or processor for the depicted person's data? Does the DPA need an avatar annex?
- **Q5** Transfers: MiniMax's and fal's processing locations and the mechanism (SCCs, adequacy); whether
  MiniMax's platform-specific terms permit our use and how long it keeps request inputs.
- **Q6** Is avatar media biometric data under Art. 4(14) / Art. 9 when used to condition a generative model
  (we treat it as if it were)? Is a DPIA mandatory (Garante list, Art. 35(3)) or advisable?
- **Q7** Audit log retention of 3 years (AV-7): right period? Basis (legitimate interest versus legal
  obligation under L. 132/2025 / art. 612-quater c.p. evidence)?
- **Q8** How long must the customer keep the subject's consent record after deletion? Fixed period?
- **Q9** May we revoke an avatar on the subject's request alone, before hearing the account holder? What
  must we tell each side?
- **Q10** Consumers: should a consumer account be allowed avatars of anyone other than themselves (the
  indemnity does not reach consumers)?
- **Q11** AI Act Art. 50: is ReelBolt a "provider" under 50(2) (marking duty, and the 2 December 2026
  transition date if the Digital Omnibus applies) and the customer a "deployer" under 50(4)? Is the
  drafted labelling clause enough for the deployer side?
- **Q12** Public figures (AV-8): is "banned unless the account holder is that person" enforceable as
  worded, and does art. 97 L. 633/1941 need mentioning?
- **Q13** Is a Terms bump (re-acceptance) right for the phase 1 change, rather than notice under Section 19?
- **Q14** Italian versions of the Terms, Privacy and AUP: the legal pages are English-only today; do the
  avatar clauses need an Italian legal version for Italian consumers?
- **Q15** The statements of section 3a: (a) does the 2026-12 person text cover generated pictures adequately, and
  is keeping 2026-11 rows usable for video only (re-attestation before a picture) the right transition; (b) is the
  object statement plus an automatic person check, skipped when no Vision provider is configured, a sufficient guard
  against a person's photo being uploaded as an "object"; (c) may an Owner or Admin attest a person's consent for
  every project of the workspace, and must the subject's consent record then name the workspace; (d) an object
  avatar's files (and the library copies of an object) have no retention date: is that acceptable for files that may show a product
  owned by a third party, or a place where people can be seen in the background.
