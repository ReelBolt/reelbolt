import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

import {
  ALPHA_TELEMETRY_NOTICE,
  AMBIENT_SIGNAL_NOTICE,
  COMPANY_LEGAL_NAME,
  COMPANY_REGISTRATION_NUMBER,
  CONTROLLER_NAME,
  COURTS_JURISDICTION,
  GENERAL_EMAIL,
  PARTITA_IVA,
  PARTITA_IVA_LABEL,
  PAYMENT_PROVIDERS,
  FIRST_TOUCH_COOKIE_PERIOD,
  VISIT_RECORD_RETENTION_PERIOD,
  LOG_RETENTION_PERIOD,
  PAYMENT_PROVIDER_STATEMENT,
  MARKETING_EMAIL_PROVIDER,
  PRIVACY_EMAIL,
  SUPPORT_EMAIL,
  DISCLOSED_DATA_CATEGORIES,
  DISCLOSED_DATA_RETENTION_STATEMENT,
  DPO_CONTACT,
  DPO_STATEMENT,
  FIRST_VIDEO_GRANT_CREDITS,
  FIRST_VIDEO_GRANT_EXPIRY,
  OBSERVABILITY_PROVIDER,
  OBSERVABILITY_PROVIDER_REGION,
  HOSTING_PROVIDER,
  REPORTING_PROVIDER,
  STORAGE_PROVIDER,
  WEBSITE_DELIVERY_PROVIDER,
  TELEMETRY_RETENTION_PERIOD,
  LAST_UPDATED,
  REFUND_POLICY,
  IP_REPUTATION_PROVIDER,
  OPERATOR_STATEMENT,
  PHONE_NUMBER,
  REGISTERED_ADDRESS,
  TERMS_LAST_UPDATED,
} from './legal-placeholders.ts';
import { PROVIDER_DATA_HANDLING, TRAINING_CLAIM_VENDORS } from './provider-data-handling.ts';
import { ALPHA_NOTICE, productVersion } from './site-config.ts';

// The site's runner is `node --test "lib/**/*.test.ts"`, which cannot load `.tsx` (node refuses it
// with ERR_UNKNOWN_FILE_EXTENSION), so the legal pages cannot be rendered here. The pages are
// asserted the way the codebase asserts every other page-level string: by reading the source and
// pinning that it renders the shared constant, while the constant itself is asserted directly. The
// two together are what proves the sentence reaches the page.

/** Every non-test source file of the site's own code (app, components, lib, i18n), as file URLs. */
function sourceFiles(siteRoot: URL): URL[] {
  const out: URL[] = [];
  const walk = (dir: URL) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) walk(new URL(`${encodeURIComponent(entry.name)}/`, dir));
      else if (/\.tsx?$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name)) out.push(new URL(encodeURIComponent(entry.name), dir));
    }
  };
  for (const top of ['app/', 'components/', 'lib/', 'i18n/']) walk(new URL(top, siteRoot));
  return out;
}

const privacyPage = readFileSync(new URL('../app/(en)/legal/privacy/page.tsx', import.meta.url), 'utf8');
const termsPage = readFileSync(new URL('../app/(en)/legal/terms/page.tsx', import.meta.url), 'utf8');

// ── The operator statement (compliance-check condition 3) ───────────────────
// The five business facts are empty strings BY DESIGN: the operator is the ReelBolt trademark and
// there is no legal entity. The old "no [TOKEN]" check passed them because an empty string renders
// nothing, which is exactly the false negative the 2026-10-18 decision reverses: the check
// becomes "non-empty, OR covered by a statement the page renders". These tests are that check.

test('the operator statement names the controller, address and Partita IVA from the constants', () => {
  assert.ok(CONTROLLER_NAME.length > 0);
  assert.equal(COMPANY_LEGAL_NAME, 'ReelBolt');
  assert.ok(OPERATOR_STATEMENT.includes(CONTROLLER_NAME));
  assert.ok(OPERATOR_STATEMENT.includes(REGISTERED_ADDRESS));
  assert.ok(OPERATOR_STATEMENT.includes(PARTITA_IVA_LABEL));
});

test('the registered address is set and has an address shape (the literal is never asserted)', () => {
  assert.ok(REGISTERED_ADDRESS.length > 0);
  assert.match(REGISTERED_ADDRESS, /\d{5}/, 'a postal code');
  assert.match(REGISTERED_ADDRESS, /, Italy$/);
});

test('Partita IVA is set, formatted by one helper, and no placeholder token reaches a legal string', () => {
  assert.match(PARTITA_IVA, /^IT\d{11}$/);
  assert.equal(PARTITA_IVA_LABEL, `P.IVA ${PARTITA_IVA}`);
  const rendered = [
    OPERATOR_STATEMENT,
    PARTITA_IVA_LABEL,
    COURTS_JURISDICTION,
    SUPPORT_EMAIL,
    PRIVACY_EMAIL,
    GENERAL_EMAIL,
  ].join('\n');
  for (const token of ['PARTITA_IVA', 'TBD', 'TODO']) {
    assert.ok(!rendered.includes(token), `"${token}" must not appear in rendered legal strings`);
  }
});

test('the registration number stays empty (sole trader) and mailboxes and forum are pinned', () => {
  assert.equal(COMPANY_REGISTRATION_NUMBER, '');
  assert.equal(GENERAL_EMAIL, 'hello@reelbolt.ai');
  assert.equal(SUPPORT_EMAIL, 'support@reelbolt.ai');
  assert.equal(PRIVACY_EMAIL, 'privacy@reelbolt.ai');
  assert.equal(COURTS_JURISDICTION, 'the courts of Novara, Italy');
});

test('the payment provider is a list, so a second provider can be added without rewording', () => {
  assert.ok(Array.isArray(PAYMENT_PROVIDERS) && PAYMENT_PROVIDERS.length >= 1);
});

test('the DPO fact is empty by design, and the policy states no DPO is required', () => {
  assert.equal(DPO_CONTACT, '');
  assert.match(DPO_STATEMENT, /not appointed a Data Protection Officer/i);
  assert.match(DPO_STATEMENT, /not required to appoint one/i);
});

test('the phone fact stays empty, and nothing claims a published number', () => {
  assert.equal(PHONE_NUMBER, '');
});

test('the privacy policy renders the operator statement, the DPO statement and the privacy contact', () => {
  assert.match(privacyPage, /import \{[\s\S]*OPERATOR_STATEMENT[\s\S]*\} from '@\/lib\/legal-placeholders'/);
  assert.match(privacyPage, /\{OPERATOR_STATEMENT\}/);
  assert.match(privacyPage, /\{DPO_STATEMENT\}/);
  // The three blank fields are no longer rendered as empty rows.
  assert.doesNotMatch(privacyPage, /\{REGISTERED_ADDRESS\}/);
  assert.doesNotMatch(privacyPage, /\{COMPANY_REGISTRATION_NUMBER\}/);
  assert.doesNotMatch(privacyPage, /\{DPO_CONTACT\}/);
});

test('the terms render the operator statement where the registered address used to sit', () => {
  assert.match(termsPage, /import \{[\s\S]*OPERATOR_STATEMENT[\s\S]*\} from '@\/lib\/legal-placeholders'/);
  assert.match(termsPage, /\{OPERATOR_STATEMENT\}/);
  assert.doesNotMatch(termsPage, /\{REGISTERED_ADDRESS\}/);
});

// ── The five disclosed categories ───────────────────────────────────────────

test('the disclosure names each of the five alpha data categories', () => {
  assert.deepEqual(
    DISCLOSED_DATA_CATEGORIES.map((category) => category.label),
    [
      'Product telemetry',
      'Error tracking',
      'Funnel analytics',
      'The feedback channel',
      'Ambient product-health signals',
    ],
  );
});

test('the privacy policy renders every disclosed category', () => {
  assert.match(privacyPage, /DISCLOSED_DATA_CATEGORIES\.map\(/);
  assert.match(privacyPage, /category\.label/);
  assert.match(privacyPage, /category\.description/);
});

test('the disclosure sets 12 months of retention for the five categories, then delete or aggregate', () => {
  assert.equal(TELEMETRY_RETENTION_PERIOD, '12 months');
  assert.match(DISCLOSED_DATA_RETENTION_STATEMENT, /for 12 months, then delete or aggregate them/);
  assert.match(DISCLOSED_DATA_RETENTION_STATEMENT, /record of your telemetry choice is kept while your account exists, and deleted within 12 months after it is closed/);
  assert.doesNotMatch(DISCLOSED_DATA_RETENTION_STATEMENT, /not yet/i);
  assert.match(privacyPage, /\{DISCLOSED_DATA_RETENTION_STATEMENT\}/);
});

// ── The alpha notice (acceptance bullet 3) ──────────────────────────────────

test('the alpha-notice addition states that early-access telemetry is collected', () => {
  assert.match(ALPHA_TELEMETRY_NOTICE, /collect telemetry/i);
  assert.match(ALPHA_TELEMETRY_NOTICE, /alpha/i);
});

test('the alpha notice states the product-health signals are linked to the account, not anonymous', () => {
  assert.equal(
    AMBIENT_SIGNAL_NOTICE,
    'We also record product-health signals (a failed tool call, a refused request, a failed workflow ' +
      'step, or a chat that ended on an error) as metadata only, so we can see when the alpha is ' +
      'struggling. Each is linked to your account and workspace, and so to your email address; it is not ' +
      'anonymous. It carries no message content.',
  );
  assert.doesNotMatch(AMBIENT_SIGNAL_NOTICE, /anonymous product-health signals/i);
  assert.match(AMBIENT_SIGNAL_NOTICE, /not\s+anonymous/i);
  assert.match(AMBIENT_SIGNAL_NOTICE, /metadata only/i);
  assert.match(AMBIENT_SIGNAL_NOTICE, /no message content/i);
  // No absolute promise ("never", "can never").
  assert.doesNotMatch(AMBIENT_SIGNAL_NOTICE, /\bnever\b/i);
  assert.ok(ALPHA_TELEMETRY_NOTICE.includes(AMBIENT_SIGNAL_NOTICE));
});

test('the footer shows the pinned alpha notice and links the privacy policy next to it', () => {
  const footer = readFileSync(new URL('../components/layout/SiteFooter.tsx', import.meta.url), 'utf8');
  const alpha = (
    JSON.parse(readFileSync(new URL('../messages/en/layout.json', import.meta.url), 'utf8')) as {
      footer: { alpha: string; privacyTelemetry: string };
    }
  ).footer;
  assert.equal(alpha.alpha.replace('{version}', productVersion), ALPHA_NOTICE);
  assert.match(footer, /t\('alpha', \{ version: productVersion \}\)/);
  assert.match(footer, /href="\/legal\/privacy"[\s\S]{0,200}t\('privacyTelemetry'\)/);
});

// ── The impersonation prohibition (Terms §9.1) ──────────────────────────────

test('the Terms carry the impersonation and likeness rules as their own heading', () => {
  assert.match(termsPage, /9\.1 Real people: impersonation, likeness and consent/);
});

// The avatars bump (Terms and Privacy 2026-11-05) replaced the flat ban on depicting an identifiable
// person with a consent-based rule. The bans that remain unconditional are pinned here.
test('the Terms allow a likeness only with consent and keep the unconditional bans', () => {
  const flat = termsPage.replace(/\s+/g, ' ');
  assert.doesNotMatch(flat, /synthetic media that depicts an identifiable person/);
  assert.match(flat, /except an avatar or a cloned voice made with that person&apos;s consent/);
  assert.match(flat, /a likeness or voice of anyone under 18, in any form/);
  assert.match(flat, /unless you are that person and the avatar or voice is your own/);
  assert.match(flat, /sexually explicit content, of anyone, with or without consent/);
  assert.match(flat, /re-syncing their lips/);
  assert.match(flat, /You must not remove, hide or alter that marking or label\./);
  assert.match(flat, /If a step uses an avatar, the avatar&apos;s reference pictures/);
  assert.match(flat, /whose likeness or voice you used, or caused to be used/);
});

test('the acceptable use page restates the avatar rules and gives the depicted person a channel', () => {
  const flat = aupPage.replace(/\s+/g, ' ');
  assert.match(flat, /1\. Real people: impersonation, likeness and consent/);
  assert.doesNotMatch(flat, /synthetic media that depicts an identifiable person/);
  assert.match(flat, /a likeness or voice of anyone under 18, in any form/);
  assert.match(flat, /Labelling AI-generated video\./);
  assert.match(flat, /\{PRIVACY_EMAIL\}/);
  assert.match(flat, /make the avatar or voice unusable while we look into it/);
});

test('the Privacy Policy describes avatar data, its lawful basis, retention and the subject channel', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /<strong>Avatars\.<\/strong> If you create an avatar of a person/);
  assert.match(flat, /Article 9\(2\)\(a\) GDPR/);
  assert.match(flat, /never sent to any AI provider/);
  assert.match(flat, /an avatar cannot be created from a file that has not been screened yet/);
  assert.match(flat, /is deleted 30 days later/);
  assert.match(flat, /is kept for 3 years and then deleted/);
  assert.match(flat, /If you are shown in an avatar\./);
  assert.match(flat, /Avatars and cloned voices of anyone under 18 are not allowed/);
});

test('the Terms lead with the product-voice prohibition sentence', () => {
  assert.match(termsPage, /ReelBolt is not a tool for impersonating real people\./);
});

test('the Terms require documented consent for footage, images, audio and voices', () => {
  assert.match(termsPage, /documented consent to use it as you intend/);
});

// The recipe marketplace bump (Terms and Privacy 2026-11-10).
test('the Terms carry the gallery licence, unpublishing and takedown rules', () => {
  const flat = termsPage.replace(/\s+/g, ' ');
  assert.match(flat, /Community recipes\.<\/strong> You may publish a recipe/);
  assert.match(flat, /worldwide, royalty-free licence to view, copy, adapt and run the recipe/);
  assert.match(flat, /limited to the \{siteConfig\.name\} gallery, website and app/);
  assert.match(flat, /does not cover promotion outside them/);
  assert.match(flat, /you own or control the rights to the recipe, the preview/);
  assert.match(flat, /Copies already made stay with the people who made them/);
  assert.match(flat, /within 24 hours/);
  assert.match(flat, /acknowledge or answer within 5 business days/);
  assert.match(flat, /reply to the same address and explain why it is lawful/);
  assert.match(flat, /limit publishing for an account that repeatedly/);
  assert.doesNotMatch(flat, /\[[^\]]*open[^\]]*\]/i);
});

test('the acceptable use page covers community recipes, reports and removal', () => {
  const flat = aupPage.replace(/\s+/g, ' ');
  assert.match(flat, /5\.1 Community recipes/);
  assert.match(flat, /name, description, prompts, labels, narration lines and the preview video/);
  assert.match(flat, /read a recipe&apos;s prompts before you copy it/);
  assert.match(flat, /Signed-in users with a verified email can report a recipe/);
  assert.match(flat, /When several different people report a recipe, it is hidden until we have reviewed it/);
  assert.match(flat, /require a person to review every later recipe from the same author/);
  assert.match(flat, /Copies already made are not changed/);
  assert.match(flat, /\{SUPPORT_EMAIL\}/);
});

test('the Privacy Policy describes recipe data, moderation, basis and retention', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /<strong>Community recipes\.<\/strong> If you publish a recipe/);
  assert.match(flat, /author label\. It starts as your account display name/);
  assert.match(flat, /from 5 ratings, the average/);
  assert.match(flat, /who rated a recipe and who reported one, and we never show that/);
  assert.match(flat, /checked by OpenAI&apos;s moderation service/);
  assert.match(flat, /legitimate interest in running and keeping safe a public gallery/);
  assert.match(flat, /performance of our contract with you/);
  assert.match(flat, /Routine moderation log entries and handled reports are deleted 12 months after they were written or handled/);
  assert.match(flat, /Open reports are kept until they are handled/);
  assert.match(flat, /tied to a safety, illegal-content, copyright or rejected-by-moderation finding are kept longer, as needed for safety and legal duties/);
  assert.match(flat, /cut to a minimal record \(the recipe and the account only, no reason or note\) after 12 months, until the account is deleted/);
  assert.match(flat, /do not count toward hiding a recipe/);
  assert.match(flat, /we delete your recipes, ratings and reports/);
  assert.match(flat, /The moderation log stays, without your identity/);
  assert.match(flat, /published recipe text/);
});

test('the Privacy Policy discloses automatic safety screening through OpenAI', () => {
  assert.match(privacyPage, /Safety screening\./);
  assert.match(privacyPage, /videos you never open or share/);
  assert.match(privacyPage, /original video file is not/);
  assert.match(privacyPage, /up to 10 minutes/);
  assert.match(privacyPage, /Safety screening is the exception/);
  const openai = PROVIDER_DATA_HANDLING.find((row) => row.vendor.startsWith('OpenAI'));
  assert.ok(openai);
  assert.match(openai.sent, /automatic safety screening/);
});

// The avatar library bump (Terms and Privacy 2026-11-17): avatars of objects, pictures with avatars and
// the workspace avatar library. The Terms must not contradict the statements shown in the product
// (AvatarConsentStatement, AvatarObjectStatement, AvatarWorkspaceConsentStatement).
test('the Terms cover pictures, the workspace library, objects and variants', () => {
  const flat = termsPage.replace(/\s+/g, ' ');
  assert.doesNotMatch(flat, /An avatar or a cloned voice can be used only in the project it was made in/);
  assert.match(flat, /A cloned voice can be used only in the project it was made in/);
  assert.match(flat, /AI-generated videos and pictures/);
  assert.match(flat, /only if the statement you confirmed covers pictures/);
  assert.match(flat, /An Owner or Admin of a workspace may save an avatar to the workspace avatar library/);
  assert.match(flat, /Members of the same workspace may then use that avatar in other projects of the workspace/);
  assert.match(flat, /An avatar never leaves the workspace/);
  assert.match(flat, /only if that person&apos;s consent covers use across the workspace/);
  assert.match(flat, /to generate AI videos and pictures in any project of the workspace/);
  assert.match(flat, /that you are the person shown and heard in the files, or that you have that person&apos;s written permission; that the person is 18 or older/);
  assert.match(flat, /revoking applies to the library item, to every copy of it imported into a project, and to every variant/);
  assert.match(flat, /Avatars of objects\.<\/strong>/);
  assert.match(flat, /show an object, a product or a place and no identifiable person/);
  assert.match(flat, /checks the pictures automatically for people/);
  assert.match(flat, /it refuses the avatar/);
  assert.match(flat, /A variant is covered by the same consent as the avatar it was made from, and it ends when that consent ends/);
  assert.match(flat, /so that it can check that no person is in them/);
  // The Terms name no vendor for this; the Privacy Policy does.
  assert.doesNotMatch(flat, /Alibaba|Kuaishou|Meta \(/);
});

test('the Privacy Policy and the provider table cover objects, pictures, the library and the model makers', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /an avatar of a person or of an object/);
  assert.match(flat, /visible to and usable by the members of that workspace/);
  assert.match(flat, /AI-generated videos and\s+pictures in the project where the avatar was made/);
  assert.match(flat, /vision model provider of the workspace, which checks that no person is in them/);
  const fal = PROVIDER_DATA_HANDLING.find((row) => row.vendor.startsWith('fal.ai'));
  assert.ok(fal);
  for (const need of ['reference-to-video', 'composing a scene picture', 'picture editor', 'cutting an object out of a video', 'lip-sync',
    'Alibaba (Wan)', 'Google (Veo and the picture-edit model)', 'MiniMax for lip-sync', 'Kuaishou (Kling)', 'Meta (Segment Anything)', 'through fal.ai']) {
    assert.ok(fal.sent.includes(need), `the fal.ai row says: ${need}`);
  }
  assert.ok(PROVIDER_DATA_HANDLING.find((row) => row.vendor.startsWith('OpenAI'))?.sent.includes('up to six stills of that video and every frame that was cut out'));
});

// The new-user funnel bump (Privacy only, 2026-11-11). Each pinned sentence states something the
// code does: the click counter (TrackedLinkRedirectController), the visitor events and first touch
// (lib/site-events.ts, TelemetryVisitController), the sign-up carry (lib/attribution-signup-link.ts,
// the Go API's signup and OAuth state), the welcome answers and the first-step events.
test('the Privacy Policy describes short-link counts and that they need no consent', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /<strong>Short links\.<\/strong>/);
  assert.match(flat, /we add one to a counter for that link and that day/);
  assert.match(flat, /It holds no IP address, no cookie, no visitor ID and nothing else about the person who opened the link/);
  assert.match(flat, /nothing is stored on or read from your device/);
  // TrackedLinkRedirectController reads the user agent and the request-kind headers once
  // (LinkPreviewAgents); lib/tracked-link.ts forwards exactly those from the Pages relay.
  assert.match(flat, /We read the user-agent string of the request and the browser&apos;s request-type headers once, to tell a link preview fetched by an app, a crawler, or a page a browser loads ahead of time from a person opening the link\./);
  assert.match(flat, /The two kinds of request are counted separately, and neither the user-agent string nor those headers are kept with the count\./);
  const relay = readFileSync(new URL('./tracked-link.ts', import.meta.url), 'utf8');
  for (const header of ['sec-fetch-mode', 'sec-fetch-dest', 'sec-purpose', 'purpose', 'x-purpose', 'x-moz']) {
    assert.ok(relay.includes(`'${header}'`), `the relay forwards ${header}`);
  }
  // An uncounted click still lands on the site, with the link's name in the address (nginx @link_home too).
  assert.match(flat, /If the click cannot be counted at that moment, you are sent to the home page with the link&apos;s name as its one campaign label\./);
  assert.match(relay, /`\/\?utm_content=\$\{code\}`/);
  assert.match(flat, /The request itself is written to the server access logs described above/);
  assert.match(flat, /keeps nothing that identifies a person, so we do not ask for consent for it/);
  assert.match(flat, /Short-link click counts hold no personal data and are kept as statistics/);
});

test('the Privacy Policy names the campaign labels and what the consented visit records hold', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  for (const label of ['utm_source', 'utm_medium', 'utm_campaign', 'utm_content', 'utm_term']) {
    assert.ok(privacyPage.includes(label), `the privacy policy names ${label}`);
  }
  assert.match(flat, /They describe the link, not the person who follows it/);
  assert.match(flat, /and still only if you accepted, the website and the sign-up page send us short records/);
  assert.match(flat, /which page of the website you opened, which call-to-action button you pressed/);
  assert.match(flat, /A page is recorded as one name from a fixed list/);
  assert.match(flat, /It never holds anything you typed, and never the full page address/);
  assert.match(flat, /the first record we receive stores them once against your visitor ID as your first touch/);
  assert.match(flat, /The page address includes any campaign labels in the link you followed/);
  // One word for the same thing across the page.
  assert.doesNotMatch(flat, /campaign tags/);
});

test('the Privacy Policy says what a sign-up link carries, with or without cookies', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /<strong>What a sign-up link carries\.<\/strong>/);
  assert.match(flat, /Nothing on your device is used for this: the values travel in the link you click and in the sign-up request, whether or not you accepted cookies/);
  assert.match(
    flat,
    /The campaign labels in the link you followed may be saved with your account when you sign up; they say which of our posts or pages you came from, not who you are\./,
  );
  assert.match(flat, /If you sign up with Google or GitHub, the audience hint and, where we save them, the campaign labels and the landing path pass through that provider&apos;s sign-in address as part of a state value/);
  assert.match(flat, /Besides a random number, the state value holds only those short words and that path\./);
  assert.match(flat, /legitimate interest in knowing which of our posts and pages bring sign-ups \(Article 6\(1\)\(f\) GDPR\)/);
  assert.match(flat, /You can object at any time, as Section 11 explains/);
});

test('the Privacy Policy says the sign-up link names the landing path, and that it is saved with the campaign labels', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  // lib/attribution-signup-link.ts: `lp` rides only next to a campaign parameter and is a path only.
  assert.match(flat, /The sign-up buttons on the website add up to three things to the address of the sign-up page\./);
  assert.match(flat, /The third is the landing path: the path of the page of the website your visit began on \(for example\{' '\} <span className="font-mono text-xs">lp=\/pricing<\/span>\)\./);
  assert.match(flat, /It is added only together with campaign labels, and it is the path alone: nothing that followed a question mark in the address, and never the website that referred you\./);
  const links = readFileSync(new URL('./attribution-signup-link.ts', import.meta.url), 'utf8');
  assert.match(links, /export const LANDING_PARAM = 'lp';/);
  assert.match(links, /if \(campaign\) add\(LANDING_PARAM, sanitizeLandingPath\(carry\.landing\?\.landingPath\)\);/);
  // Saved with the campaign labels (the dashboard sends it as attribution.landingPath, the API stores it).
  assert.match(flat, /With them we save the time and the landing path the link carried; when the link carried none and the sign-up page was the first page you opened, we save that page&apos;s path\./);
  assert.match(flat, /<strong>The audience hint, the campaign labels and the landing path a sign-up link carries<\/strong>/);
  assert.match(flat, /whether it came from the cookie or from the campaign labels and landing path in your sign-up link/);
  assert.match(flat, /that page would no longer know which link you arrived on or where your visit began/);
});

test('the Privacy Policy says a failed Google or GitHub sign-in returns the carried values in the sign-in page address', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  // api/handlers/auth/oauth.go `oauthFail` + oauth_state.go `oauthCarryLoginQuery`.
  assert.match(flat, /If a sign-in with Google or GitHub does not complete \(for example you cancel it, or no account can be created\), you are sent back to the app&apos;s sign-in page, and the same values come back in the address of that page next to a short word for what went wrong: the audience hint and, where we save them, the campaign labels and the landing path\./);
  assert.match(flat, /This way they are not lost if you then sign up another way\./);
});

test('the storage inventory lists every entry the website writes, and the sign-up entry of the app', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  const row = (name: string, type: string) =>
    assert.ok(
      flat.includes(`<td className="py-2 pr-4 font-mono text-xs">${name}</td> <td className="py-2 pr-4">${type}</td>`),
      `the storage table has a row for ${name}`,
    );
  // The names the page prints are the names the code uses.
  row('rb_locale', 'First-party cookie');
  assert.match(readFileSync(new URL('../i18n/cookie.ts', import.meta.url), 'utf8'), /export const LOCALE_COOKIE = 'rb_locale';\nconst ONE_YEAR_SECONDS = 365 \* 24 \* 60 \* 60;/);
  assert.ok(!flat.includes('alpha-banner.dismissed'), 'the retired alpha notice key is no longer listed');
  row('reelbolt:promo-banner-dismissed', 'Session storage (not a cookie)');
  const promo = readFileSync(new URL('../components/promo/PromoBanner.tsx', import.meta.url), 'utf8');
  assert.match(promo, /const DISMISS_KEY = 'reelbolt:promo-banner-dismissed';/);
  assert.match(promo, /window\.sessionStorage\.setItem\(DISMISS_KEY, id\)/);
  assert.match(promo, /\[p\.kind, p\.endsAt \?\? '', p\.headline \?\? '', p\.percentOff \?\? ''\]/);
  assert.match(flat, /It holds a short description of that offer \(its kind, end date, headline and discount\) and nothing about you\./);
  row('rb-signup-intent:v1', 'Local storage (not a cookie), written by the app&apos;s sign-up and sign-in pages');
  assert.match(flat, /remembers those two values so that you arrive there once your account exists/);
  assert.match(flat, /a short code computed in your browser from your email address \(not the address itself\)/);
  assert.match(flat, /An entry that is not used lapses after three days at most \(after 30 minutes if a Google or GitHub sign-in did not come back in that time\)/);
  for (const name of ['rf-cookie-consent', 'rb_ft', 'rf-visitor-id', 'rf-visit-sent']) {
    assert.ok(flat.includes(`<td className="py-2 pr-4 font-mono text-xs">${name}</td>`), name);
  }
  // No promise the functional entries would break.
  assert.doesNotMatch(flat, /We do not set any cookie before you make a choice/);
  assert.match(flat, /We set no analytics or measurement cookie before you make a choice in the cookie banner\./);
  assert.match(flat, /A few entries do not depend on that choice, because they only remember something you did yourself: the language you picked, a notice or offer bar you closed, and where you were going when you started to sign up\./);

  // Every storage key the site's own code writes is one of the rows above. A new key fails here
  // until the table names it.
  const listed = ['rf-cookie-consent', 'rf-visitor-id', 'rf-visit-sent', 'reelbolt:promo-banner-dismissed'];
  const sources: [string, RegExp][] = [
    ['./consent.ts', /CONSENT_STORAGE_KEY = '([^']+)'/],
    ['./attribution.ts', /VISITOR_ID_KEY = '([^']+)'/],
    ['../components/analytics/AttributionTracker.tsx', /SESSION_FLAG = '([^']+)'/],
    ['../components/promo/PromoBanner.tsx', /DISMISS_KEY = '([^']+)'/],
  ];
  assert.deepEqual(sources.map(([file, re]) => re.exec(readFileSync(new URL(file, import.meta.url), 'utf8'))?.[1]), listed);
  const writers = sourceFiles(new URL('../', import.meta.url)).filter((file) => /(?:localStorage|sessionStorage)\b[^;\n]*\.setItem\(/.test(readFileSync(file, 'utf8')));
  assert.deepEqual(
    writers.map((file) => decodeURIComponent(file.pathname).split('/site/')[1]).sort(),
    ['components/analytics/AttributionTracker.tsx', 'components/consent/CookieConsentProvider.tsx', 'components/promo/PromoBanner.tsx'],
    'a new file writes to browser storage: add its key to Section 5 of the Privacy Policy and to this list',
  );
});

test("the sign-up entry the Privacy Policy describes is the one the app's code keeps", (t) => {
  // The app is a separate package (web/); a copy of site/ on its own skips the comparison.
  let intent: string;
  try {
    intent = readFileSync(new URL('../../web/lib/auth/signup-intent.ts', import.meta.url), 'utf8');
  } catch {
    t.skip('web/ is not next to site/ in this checkout');
    return;
  }
  assert.match(intent, /export const SIGNUP_INTENT_KEY = 'rb-signup-intent:v1';/);
  assert.match(intent, /export const EMAIL_INTENT_TTL_MS = 3 \* 24 \* 60 \* 60 \* 1000;/);
  assert.match(intent, /export const PROVIDER_INTENT_TTL_MS = 30 \* 60 \* 1000;/);
  // Two values and a local key of the address: nothing else is kept.
  assert.match(intent, /interface StoredIntent extends SignupIntent \{[^}]*at: number;[^}]*who\?: string;\s*\}/);
  assert.match(intent, /export interface SignupIntent \{\s*plan\?: string;\s*next\?: string;\s*\}/);
});

test('the Privacy Policy says the visitor ID is renewed with the cookie, and which other links carry the labels', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  // lib/attribution-capture.ts `planIdentity`: a fresh first-touch cookie comes with a fresh visitor id.
  assert.match(flat, /The cookie and the visitor ID belong together: on the website, when the cookie is gone and you visit again, a new cookie is set together with a new random visitor ID, and the same happens when the sign-up page sets the cookie\./);
  // Not a promise the app's sign-up page breaks: that page keeps the stored id when it has no
  // first touch to start (see the test on the sign-up page below).
  assert.doesNotMatch(flat, /the earlier ID is not used again/);
  assert.match(flat, /Persistent, until you clear it or withdraw consent\. Replaced by a new random ID whenever a new <span className="font-mono text-xs">rb_ft<\/span> cookie is set\./);
  const capture = readFileSync(new URL('./attribution-capture.ts', import.meta.url), 'utf8');
  assert.match(capture, /if \(input\.freshFirstTouchWritten\) \{\s*return \{ visitorId: input\.candidate, renewed: input\.storedVisitorId !== null \};/);
  // lib/attribution-signup-link.ts: the sign-in link, the language links and a link that leaves a
  // translated page carry the audience hint and the campaign labels in the address.
  assert.match(flat, /The Sign in link on the website carries the same things as the sign-up buttons/);
  assert.match(flat, /the language links in the footer, and a link from a translated page to a page that exists only in English/);
  assert.match(flat, /Nothing is stored for this either\./);
  const links = readFileSync(new URL('./attribution-signup-link.ts', import.meta.url), 'utf8');
  assert.match(links, /export function decorateEntryHref/);
  assert.match(links, /export function decorateCarryHref/);
});

// The app is a separate package (web/); a copy of site/ on its own skips these comparisons.
function appSource(t: { skip: (reason: string) => void }, path: string): string | null {
  try {
    return readFileSync(new URL(`../../web/${path}`, import.meta.url), 'utf8');
  } catch {
    t.skip('web/ is not next to site/ in this checkout');
    return null;
  }
}

test("the Privacy Policy says when the app's sign-up page keeps the stored visitor ID, and when it sets the cookie", (t) => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /The app&apos;s sign-up page sets the same cookie, for the browser session only, when you accepted cookies, no such cookie exists yet and the address of the sign-up page carries campaign labels\./);
  assert.match(flat, /There is one exception\. If you open the app&apos;s sign-up page while no cookie exists and its address carries no campaign labels, no new cookie is set, and the two sign-up records described next are sent with the visitor ID already stored in your browser, which may be the one an earlier visit or an earlier sign-up used\./);
  // Section 5 no longer says a sign-up link never stores anything.
  assert.doesNotMatch(flat, /following a sign-up link that carries an audience hint or campaign labels, stores nothing on your device/);
  assert.match(flat, /Following a sign-up link that carries an audience hint or campaign labels stores nothing on your device either, unless you accepted cookies: then the app&apos;s sign-up page may set the\{' '\} <span className="font-mono text-xs">rb_ft<\/span> cookie and the visitor ID from the campaign labels in its address/);
  const events = appSource(t, 'lib/attribution/visitor-events.ts');
  const tracker = appSource(t, 'components/analytics/SignupTracker.tsx');
  if (!events || !tracker) return;
  // No readable cookie and none written: the stored id is kept.
  assert.match(events, /if \(input\.freshFirstTouchWritten\) return input\.candidate;\s*return input\.storedVisitorId \?\? input\.candidate;/);
  // A fresh cookie is started only when the address carries campaign labels, and it is a session cookie.
  assert.match(tracker, /if \(mayStartFirstTouch && inCookie === null && hasCampaignParams\(landing\)\) \{/);
  assert.match(tracker, /writeSessionFirstTouch\(value\);/);
});

test('the Privacy Policy says what counts as sending the sign-up form, and that the cookie can come back', (t) => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /and that you opened the sign-up form, sent it, or pressed the Google or GitHub button on it\./);
  assert.match(flat, /and the cookie is removed when you sign up\. If you then go back on the sign-up page to correct your email address, the cookie is put back until you close the browser, so that the account you really use carries the same first touch\./);
  assert.match(flat, /removed when you sign up, or when you reject or withdraw\. If you go back on the sign-up page to correct your email address, it is put back until you close the browser\./);
  const buttons = appSource(t, 'components/auth/OAuthButtons.tsx');
  const tracker = appSource(t, 'components/analytics/SignupTracker.tsx');
  const form = appSource(t, 'components/auth/SignupForm.tsx');
  if (!buttons || !tracker || !form) return;
  assert.match(buttons, /if \(signup\) sendSignupSubmitted\(\);/);
  assert.match(tracker, /export function restoreFirstTouch\(\): void \{/);
  assert.match(tracker, /if \(!rememberedFirstTouch \|\| firstTouchCookie\(\) !== null\) return;\s*writeSessionFirstTouch\(rememberedFirstTouch\);/);
  assert.match(form, /restoreFirstTouch\(\);/);
});

test('the Privacy Policy says a pricing button names the plan, and what the sign-up entry holds', (t) => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /A sign-up button on the pricing page also names the plan you chose \(for example\{' '\} <span className="font-mono text-xs">plan=creator<\/span>\), so that the app can take you to that plan once your account exists; the plan is not saved with your account when you sign up/);
  const offers = readFileSync(new URL('./pricing/offers.ts', import.meta.url), 'utf8');
  assert.match(offers, /return signupEnabled \? `\/app\/signup\?plan=\$\{plan\.id\}` : '\/contact';/);
  const catalog = JSON.parse(readFileSync(new URL('./pricing/plans.catalog.json', import.meta.url), 'utf8')) as { plans: { id: string }[] };
  assert.ok(catalog.plans.some((plan) => plan.id === 'creator'), 'the example names a real plan');
  assert.match(flat, /Besides the two values, the entry holds the time it was written and a short code computed in your browser from your email address \(not the address itself\), so that only that account picks the entry up\. For a sign-up by email the code is added when you send the form; for a sign-up with Google or GitHub it is added once you are signed in\./);
  assert.doesNotMatch(flat, /For a sign-up by email it also holds a short code/);
  const intent = appSource(t, 'lib/auth/signup-intent.ts');
  if (!intent) return;
  // A provider sign-up is bound to whoever signs in next: the same code, written later.
  assert.match(intent, /if \(fresh && !stored\.who\) write\(\{ \.\.\.stored, who: userKey \}\);/);
  assert.match(intent, /write\(\{\s*at: now,/);
});

test('every visit record carries the first touch the cookie holds, as the Privacy Policy says', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /Every record is sent together with the first-touch details the cookie holds \(the campaign labels of the link you first arrived on, the website that referred you and the path of the first page you landed on\), and the first record we receive stores them once against your visitor ID as your first touch\./);
  // lib/attribution-capture.ts `beaconTouch`: the stored first touch, not the landing of the page open now.
  const capture = readFileSync(new URL('./attribution-capture.ts', import.meta.url), 'utf8');
  assert.match(capture, /if \(stored && stored\.visitorId === sanitizeVisitorId\(input\.visitorId\)\) return stored\.touch;\s*return input\.landing;/);
});

test('the Privacy Policy names the provider that serves the website and its short links', (t) => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.equal(WEBSITE_DELIVERY_PROVIDER, 'Cloudflare');
  assert.match(HOSTING_PROVIDER, /^Hetzner/);
  assert.ok(
    flat.includes('<td className="py-2 pr-4">Website delivery</td> <td className="py-2 pr-4">{WEBSITE_DELIVERY_PROVIDER}</td>'),
    'Section 6 has a row for the provider that serves the website',
  );
  assert.match(flat, /Serves the marketing website and its short links, and passes requests for the Service on to our servers\. It receives the IP address and the user-agent string of each request and limits how many requests one address can make in a short time/);
  // The hosting row no longer says the servers host the website.
  assert.match(flat, /<td className="py-2 pr-4">\{HOSTING_PROVIDER\}<\/td> <td className="py-2 pr-4">Hosts the Service and processes its server access logs<\/td>/);
  assert.doesNotMatch(flat, /Hosts the website and the Service/);
  // The short-links entry says the request passes through that provider first.
  assert.match(flat, /The request reaches our servers through\{' '\} \{WEBSITE_DELIVERY_PROVIDER\}, the provider that serves the website \(Section 6\)\. It receives the IP address and the user-agent string of the request, as it does for every page of the website, and limits how many short links one address can open in a short time\./);
  // What the sentence rests on: the Pages Function that answers a short link, and the per-address
  // rate-limit rules shipped for it.
  const fn = readFileSync(new URL('../functions/l/[code].ts', import.meta.url), 'utf8');
  assert.match(fn, /followTrackedLink/);
  let rules: string;
  try {
    rules = readFileSync(new URL('../../infra/cloudflare/rate-limit.links.pro.rules.json', import.meta.url), 'utf8');
  } catch {
    t.skip('infra/ is not next to site/ in this checkout');
    return;
  }
  assert.match(rules, /"ip\.src"/);
  assert.match(rules, /\/l\//);
});

test('the Privacy Policy names the reporting tool and says what the nightly copy holds', (t) => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.equal(REPORTING_PROVIDER, 'Microsoft Power BI');
  assert.equal(STORAGE_PROVIDER, 'Cloudflare R2');
  assert.ok(
    flat.includes('<td className="py-2 pr-4">Internal reporting</td> <td className="py-2 pr-4">{REPORTING_PROVIDER}</td>'),
    'Section 6 has a row for the reporting tool',
  );
  assert.match(flat, /Stores uploaded content and generated files, and the nightly reporting files described below the table/);
  assert.match(flat, /<strong>Internal reporting\.<\/strong> Our own statistics, including the sign-up and first-step figures described in Section 3, are produced with \{REPORTING_PROVIDER\}\. Each night a copy of the records they are built from is written to files held in\{' '\} \{STORAGE_PROVIDER\}, and the reporting tool reads those files\. Each night&apos;s files replace the ones from the night before\./);
  assert.match(flat, /The copy includes account records \(with the name and email address of the account\), workspace, billing and usage records, first-step records, welcome answers, the sign-up source stored with an account, and the visit records and first touch of visitors who accepted cookies, with their visitor IDs\. It holds no passwords and none of the files you upload or the videos made from them\./);
  // Section 3 points at it from the two entries whose purpose is "our own statistics".
  assert.match(flat, /Those statistics are produced with the reporting tool named in Section 6, from a nightly copy that holds your answers next to your account ID\./);
  assert.match(flat, /They are part of the nightly copy that our reporting tool reads \(Section 6\)\./);
  // No claim that the copy is anonymous: the account table in it has the name and the address.
  assert.doesNotMatch(flat, /no name or email|anonymous statistics/i);
  // The grant list of the reporting role is the fact behind the paragraph.
  let grants: string;
  try {
    grants = readFileSync(
      new URL('../../inference/src/ReelBolt.Inference.Api/Data/BiReadOnlyRole/PowerBiReadOnlyRoleSql.cs', import.meta.url),
      'utf8',
    );
  } catch {
    t.skip('inference/ is not next to site/ in this checkout');
    return;
  }
  for (const table of ['user_attribution', 'user_onboarding_profiles', 'visitor_attribution', 'product_events', 'usage_events', 'billing_payments']) {
    assert.ok(grants.includes(`"${table}"`), `the reporting role reads ${table}`);
  }
  const users = /\["application_users"\] = new\[\]\s*\{([^}]*)\}/.exec(grants)?.[1] ?? '';
  assert.match(users, /"display_name"/);
  assert.match(users, /"email"/);
  assert.doesNotMatch(users, /"password_hash"/);
  assert.doesNotMatch(grants, /"project_files"/);
});

test('the Privacy Policy describes the welcome questions and the first-step records', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /<strong>Welcome questions\.<\/strong> After you sign up we ask two questions/);
  assert.match(flat, /Answering is optional, and you can skip the questions/);
  assert.match(flat, /to choose your first recipe and the examples you see, and to count in our own statistics/);
  assert.match(flat, /They are deleted with your account/);
  assert.match(flat, /<strong>First-step records\.<\/strong>/);
  assert.match(flat, /uploaded your first file, downloaded your first video, and answered or skipped the welcome questions/);
  assert.match(flat, /It holds no file, file name, text or other content/);
  assert.match(flat, /written by our own servers when the step happens, not sent by your browser/);
  assert.match(flat, /First-step records are kept for\{' '\} \{TELEMETRY_RETENTION_PERIOD\} and then deleted/);
});

test('the storage inventory lists the visit marker and withdrawal removes it', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  assert.match(flat, /rf-visit-sent<\/td> <td className="py-2 pr-4">Session storage \(not a cookie\)<\/td>/);
  assert.match(flat, /Until you close the tab/);
  assert.match(flat, /we remove <span className="font-mono text-xs">rb_ft<\/span>,\{' '\} <span className="font-mono text-xs">rf-visitor-id<\/span> and\{' '\} <span className="font-mono text-xs">rf-visit-sent<\/span>/);
  // The key the page names is the key the tracker uses.
  const tracker = readFileSync(new URL('../components/analytics/AttributionTracker.tsx', import.meta.url), 'utf8');
  assert.match(tracker, /const SESSION_FLAG = 'rf-visit-sent';/);
  assert.match(tracker, /window\.sessionStorage\.removeItem\(SESSION_FLAG\)/);
});

test('the legal documents carry their own current revision date', () => {
  // Both moved together on November 17th, 2026 (avatars of objects, pictures, the workspace avatar library).
  assert.equal(LAST_UPDATED, 'November 17th, 2026');
  assert.equal(TERMS_LAST_UPDATED, 'November 17th, 2026');
  assert.match(termsPage, /\{TERMS_LAST_UPDATED\}/);
  assert.match(privacyPage, /\{LAST_UPDATED\}/);
});

// ── The Project Arachnid attribution (CSAM hash matching) ───────────────────
// The exact notice the provider requires, with ReelBolt as the entity and the provider's URL as the
// link. Pinned here because the ToS page is source-read (the test runner cannot load .tsx).

test('the Terms carry the Project Arachnid attribution verbatim', () => {
  assert.match(termsPage, /14\.1 Project Arachnid attribution/);
  assert.match(
    termsPage,
    /ReelBolt accesses Shield by Project Arachnid/,
  );
  assert.match(termsPage, /href="https:\/\/projectarachnid\.ca"/);
  assert.match(
    termsPage,
    /a program of the Canadian Centre for Child Protection Inc\., to facilitate the detection and\s+removal of child sexual abuse material and harmful-abusive material of children and survivors\s+of child sexual abuse on its services\./,
  );
});

// The footer's third-party disclosure is pinned by lib/footer-disclosure.test.ts, its own file.

// ── No legal token renders blank without a covering statement ───────────────

test('the empty registration facts are never interpolated without a covering statement', () => {
  const runnerEula = readFileSync(
    new URL('../app/(en)/legal/runner-eula/page.tsx', import.meta.url),
    'utf8',
  );
  for (const page of [termsPage, privacyPage, runnerEula]) {
    assert.doesNotMatch(page, /\{REGISTERED_ADDRESS\}/);
    assert.doesNotMatch(page, /\{COMPANY_REGISTRATION_NUMBER\}/);
      assert.doesNotMatch(page, /\{DPO_CONTACT\}/);
    assert.doesNotMatch(page, /\{PHONE_NUMBER\}/);
  }
  assert.match(termsPage, /\{OPERATOR_STATEMENT\}/);
  assert.match(privacyPage, /\{OPERATOR_STATEMENT\}/);
  assert.match(runnerEula, /\{OPERATOR_STATEMENT\}/);
});

// ── Public alpha, refund promise, founder price, first-video grant (T-005) ──

test('the refund policy promises 14 days, names support@, and is full with no deduction, one per customer', () => {
  assert.match(REFUND_POLICY, /full refund of your first subscription payment or of a credit top-up within 14 days/);
  assert.ok(REFUND_POLICY.includes(SUPPORT_EMAIL));
  assert.match(REFUND_POLICY, /full amount paid, with no deduction for credits you have already used/);
  assert.match(REFUND_POLICY, /credits granted by that payment are withdrawn/);
  assert.match(REFUND_POLICY, /One refund is available per customer/);
  assert.match(REFUND_POLICY, /the credits for the failed step are returned/);
});

test('the offer numbers are the decided ones', () => {
  assert.equal(FIRST_VIDEO_GRANT_CREDITS, 750);
  assert.equal(FIRST_VIDEO_GRANT_EXPIRY, '30 days');
});

test('the Terms state the public alpha, grant, refunds anchor and the consumer carve-out', () => {
  assert.match(termsPage, /The Service is a public alpha/);
  assert.match(termsPage, /\{FIRST_VIDEO_GRANT_CREDITS\}/);
  assert.match(termsPage, /id="refunds"/);
  assert.match(termsPage, /\{CONTROLLER_NAME\}/);
  assert.match(termsPage, /courts in the country|courts of\s+that country|bring a claim in the courts/);
  assert.match(privacyPage, /\{CONTROLLER_NAME\}/);
  assert.match(privacyPage, /marketing email/);
});

// Operator ruling 2026-10-26: the postal address is shown only on the legal pages (terms, privacy, runner
// licence), through the one constant, and in the email footer. The site footer and the contact, about and
// thank-you pages must not render it, directly or through OPERATOR_STATEMENT.
test('the address is not rendered by the footer, the contact, about or thank-you pages', () => {
  const surfaces = [
    '../components/layout/SiteFooter.tsx',
    '../components/contact/ContactAddress.tsx',
    '../components/pages/ContactPage.tsx',
    '../components/pages/ContactThankYouPage.tsx',
    '../components/pages/AboutPage.tsx',
  ];
  for (const path of surfaces) {
    const source = readFileSync(new URL(path, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /REGISTERED_ADDRESS/, `${path} must not use REGISTERED_ADDRESS`);
    assert.doesNotMatch(source, /OPERATOR_STATEMENT/, `${path} must not use OPERATOR_STATEMENT`);
  }
});

test('the legal pages do render the operator statement that carries the address', () => {
  for (const path of ['../app/(en)/legal/terms/page.tsx', '../app/(en)/legal/privacy/page.tsx']) {
    assert.match(readFileSync(new URL(path, import.meta.url), 'utf8'), /OPERATOR_STATEMENT/);
  }
});

test('the payment provider statement names Stripe as seller of record and keeps Paddle for existing subscriptions', () => {
  assert.match(PAYMENT_PROVIDER_STATEMENT, /Stripe \(Link\)/);
  assert.match(PAYMENT_PROVIDER_STATEMENT, /merchant of record/);
  assert.match(PAYMENT_PROVIDER_STATEMENT, /Managed Payments/);
  assert.match(PAYMENT_PROVIDER_STATEMENT, /Subscriptions that began on Paddle are still handled by Paddle/);
  for (const page of [termsPage, privacyPage]) {
    assert.match(page, /\{PAYMENT_PROVIDER_STATEMENT\}/, 'both pages render the one statement');
  }
});

test('marketing email is delivered by Brevo and listed as its own sub-processor row', () => {
  assert.equal(MARKETING_EMAIL_PROVIDER, 'Brevo');
  assert.match(privacyPage, /\{MARKETING_EMAIL_PROVIDER\}/);
});

// Product analytics text (initiative product-analytics): the figures come from the code, once each.
test('the product analytics disclosures match what the code does', () => {
  assert.equal(FIRST_TOUCH_COOKIE_PERIOD, '30 days');
  assert.equal(VISIT_RECORD_RETENTION_PERIOD, '13 months');
  assert.equal(LOG_RETENTION_PERIOD, '30 days');
  for (const token of ['rb_ft', 'rf-visitor-id', 'FIRST_TOUCH_COOKIE_PERIOD', 'VISIT_RECORD_RETENTION_PERIOD']) {
    assert.ok(privacyPage.includes(token), `the privacy policy mentions ${token}`);
  }
  // Withdrawal must not promise that Google's cookies are erased.
  assert.match(privacyPage, /Google Analytics cookies that are already in your browser are not deleted/);
  assert.doesNotMatch(privacyPage, /we (delete|erase) your Google Analytics cookies/i);
});

test('the privacy policy discloses the first-video abuse check and the provider constant', () => {
  assert.equal(IP_REPUTATION_PROVIDER, 'ipinfo.io');
  const page = readFileSync(new URL('../app/(en)/legal/privacy/page.tsx', import.meta.url), 'utf8');
  assert.match(page, /\{IP_REPUTATION_PROVIDER\}/);
  assert.match(page, /receives your sign-up IP address for fraud prevention/);
  assert.match(page, /withhold or withdraw the free\s+first video if we detect abuse or multiple accounts/);
  assert.match(page, /legitimate interest in preventing abuse of the free offer/);
  // No detail of how the check works.
  assert.doesNotMatch(page, /\bVPN\b|\bTor\b|disposable|the offer is granted|IP address to our logs/);
});

// Google Consent Mode (initiative ga-consent-mode): the tag is on the page before a choice, so the
// policy must say what Google receives then, and that a rejection ends it.
test('the privacy policy describes the cookieless Google signal before a choice, and that Reject ends it', () => {
  const flat = privacyPage.replace(/\s+/g, ' ');
  // Section 3: every kind of thing a cookieless hit carries, not a shortened list.
  assert.match(flat, /Before you make a choice in the cookie banner, Google Analytics runs in a cookieless mode/);
  assert.match(flat, /It sets no cookie and stores nothing on your device/);
  assert.match(
    flat,
    /the page address and title, the page that referred you, your browser language, your screen size, your browser and operating system details, and your IP address/,
  );
  assert.match(flat, /a random number that is new for every page you open/);
  // Enhanced measurement stays on, so the policy names the automatic interactions and does not
  // promise page views only.
  assert.match(
    flat,
    /Besides page views, Google Analytics also records some interactions on the page automatically, in the same cookieless way before you choose: the time you spend on a page, scrolling to the bottom of it, clicks on links that lead off our website, searches made on the site, file downloads, video engagement, and that a form was started or submitted \(not what you typed\)/,
  );
  assert.doesNotMatch(flat, /We measure page views only/);
  assert.match(flat, /If you reject cookies, Google Analytics is not loaded and receives nothing more from your visit/);
  // Section 4: the basis for the pre-choice signal, and that Reject is the way out of it.
  assert.match(flat, /legitimate interest in knowing whether the website is reached and works \(Article 6\(1\)\(f\)/);
  assert.match(flat, /receives only before you choose; rejecting cookies ends it/);
  // Section 5: the tag is listed as a similar technology that runs before a choice.
  assert.match(flat, /Google Analytics tag/);
  assert.match(flat, /Not loaded once you reject/);
  // Section 6.
  assert.match(flat, /cookieless page-visit signal before you choose, nothing after you reject/);
  // The old claims must not come back.
  assert.doesNotMatch(flat, /No analytics data is collected before you make that choice/);
  assert.doesNotMatch(flat, /anonymous page-visit/i);
  assert.doesNotMatch(flat, /if you reject, and sets its cookies/);
});

// ── Legal pages cleanup (Operator rulings 2026-10-26) ────────────────────────────

const flatPrivacy = privacyPage.replace(/\s+/g, ' ');
const flatTerms = termsPage.replace(/\s+/g, ' ');

test('neither page admits what it has not read, or points at internal documents', () => {
  for (const [name, page] of [['privacy', flatPrivacy], ['terms', flatTerms]] as const) {
    assert.doesNotMatch(page, /not established|not verified|not yet established|have not read|unverified/i, name);
    assert.doesNotMatch(page, /we are finalising|has not yet been set|the paid alpha added/i, name);
    assert.doesNotMatch(page, /docs\/[a-z-]+\.md|read-only account|We send the minimum/i, name);
  }
});

test('the AI providers section links each provider to its own terms and keeps the FLUX training warning', () => {
  assert.equal(PROVIDER_DATA_HANDLING.length, 7);
  for (const row of PROVIDER_DATA_HANDLING) {
    assert.ok(row.terms.length > 0, `${row.vendor} has a terms link`);
    for (const link of row.terms) assert.match(link.url, /^https:\/\//);
  }
  // Every vendor stays listed.
  const vendors = PROVIDER_DATA_HANDLING.map((row) => row.vendor).join(' | ');
  for (const name of ['Black Forest Labs', 'fal.ai', 'MiniMax', 'Higgsfield', 'OpenAI', 'Azure OpenAI', 'Anthropic', 'Gemini', 'DeepSeek', 'Fish Audio']) {
    assert.ok(vendors.includes(name), `${name} stays listed`);
  }
  assert.deepEqual(TRAINING_CLAIM_VENDORS, ['Black Forest Labs (FLUX)']);
  assert.match(flatTerms, /Some of these providers claim a licence over what you send them/);
  assert.match(flatTerms, /TRAINING_CLAIM_VENDORS\.join/);
  assert.match(flatTerms, /Each provider handles your content under its own terms/);
  assert.match(flatPrivacy, /Each provider handles the content we send it under its own terms/);
});

test('error monitoring names Grafana Cloud and its EU region', () => {
  assert.match(OBSERVABILITY_PROVIDER, /^Grafana Cloud/);
  assert.ok(privacyPage.includes('{OBSERVABILITY_PROVIDER}'));
  assert.ok(privacyPage.includes('OBSERVABILITY_PROVIDER_REGION ?'));
  assert.equal(OBSERVABILITY_PROVIDER_REGION, 'EU');
});

test('the Terms keep no founder-price lock and promise only notified price changes', () => {
  assert.doesNotMatch(flatTerms, /founder|for as long as that subscription/i);
  assert.match(flatTerms, /at least one billing cycle&apos;s notice of any price change/);
});

test('material changes to the Terms are notified, with a right to terminate; no acceptance by continued use', () => {
  assert.match(flatTerms, /If a change is material, we will tell you by email or in the Service before it takes effect, and you may terminate/);
  assert.doesNotMatch(flatTerms, /continued use[^.]*constitutes? acceptance/i);
});

test('the indemnity is limited to business use', () => {
  assert.match(flatTerms, /If you use the Service for business purposes, you agree to indemnify/);
  assert.match(flatTerms, /does not apply to you if you are a consumer/);
});

test('the refund policy keeps one refund per customer and allows refusing repeat refunds in case of abuse', () => {
  assert.match(REFUND_POLICY, /One refund is available per customer/);
  assert.match(REFUND_POLICY, /refuse repeat refund requests in case of abuse/);
});

test('the Terms drop the build-tool paragraph and the missing export reference', () => {
  assert.doesNotMatch(flatTerms, /Remotion|ffmpeg|grateful/);
  assert.doesNotMatch(flatTerms, /export your content/);
});

test('the pricing offer makes no lock-in promise', () => {
  const offer = readFileSync(new URL('./launch-offer.ts', import.meta.url), 'utf8');
  const faqs = readFileSync(new URL('./faqs.ts', import.meta.url), 'utf8');
  for (const source of [offer, faqs]) {
    assert.doesNotMatch(source, /locked in|for as long as (you stay subscribed|the subscription stays active)/i);
  }
});

const refundsPage = readFileSync(new URL('../app/(en)/legal/refunds/page.tsx', import.meta.url), 'utf8');
const aupPage = readFileSync(new URL('../app/(en)/legal/acceptable-use/page.tsx', import.meta.url), 'utf8');
const siteFooter = readFileSync(new URL('../components/layout/SiteFooter.tsx', import.meta.url), 'utf8');

test('the refund page quotes the merchant-of-record statement and the one-refund rule', () => {
  assert.match(refundsPage, /\{PAYMENT_PROVIDER_STATEMENT\}/);
  assert.match(PAYMENT_PROVIDER_STATEMENT, /Stripe Managed Payments/);
  assert.match(PAYMENT_PROVIDER_STATEMENT, /Paddle, which remains the merchant of record/);
  assert.match(refundsPage, /One refund is available per customer\. We may refuse repeat refund requests in case of abuse/);
  assert.match(REFUND_POLICY, /One refund is available per customer/);
  assert.match(refundsPage, /14-day right of withdrawal/);
  assert.match(refundsPage, /loses?|lost once you/);
});

test('the acceptable use page states the Terms bans and an existing contact', () => {
  assert.match(aupPage, /deepfake/);
  assert.match(aupPage, /non-consensual intimate imagery/);
  assert.match(aupPage, /documented consent/);
  assert.match(aupPage, /\{SUPPORT_EMAIL\}/);
});

test('the footer and the Terms link both new legal pages', () => {
  for (const href of ['/legal/acceptable-use', '/legal/refunds']) {
    assert.ok(siteFooter.includes(`href="${href}"`), `footer missing ${href}`);
    assert.ok(termsPage.includes(`href="${href}"`), `terms missing ${href}`);
  }
});
