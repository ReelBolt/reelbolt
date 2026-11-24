import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import { PRIVACY_EMAIL, SUPPORT_EMAIL, TERMS_LAST_UPDATED } from '@/lib/legal-placeholders';

export const metadata = buildMetadata({
  title: 'Acceptable Use Policy',
  description: `What you may and may not create with ${siteConfig.name}, and how to report abuse.`,
  path: '/legal/acceptable-use',
});

const h2 = 'mt-10 font-display text-xl font-bold text-ink first:mt-0';
const p = 'mt-4 font-sans text-ink-muted';
const ul = 'mt-4 list-disc space-y-2 pl-5 font-sans text-ink-muted';
const link = 'text-accent-ink hover:text-accent';

export default function AcceptableUsePage() {
  return (
    <>
      <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
        Last updated: {TERMS_LAST_UPDATED}
      </p>
      <h1 className="mt-2 font-display text-3xl font-bold text-ink sm:text-4xl">
        Acceptable Use Policy
      </h1>
      <p className={p}>
        This policy sets out what you may not create with {siteConfig.name}. It repeats the rules in
        Sections 9 and 9.1 of our{' '}
        <a className={link} href="/legal/terms">Terms &amp; Conditions</a>, which remain the binding
        text. If the two ever differ, the Terms apply.
      </p>

      <h2 className={h2}>1. Real people: impersonation, likeness and consent</h2>
      <p className={p}>
        {siteConfig.name} is not a tool for impersonating real people. You may show or make a real
        person&apos;s likeness or voice with the Service only in the ways this policy and the Terms
        allow. You must not create, or attempt to create:
      </p>
      <ul className={ul}>
        <li>
          any image, video or audio that impersonates, or purports to be, a real person who has not
          agreed to it, whether a celebrity, a public figure or a private individual
        </li>
        <li>
          a deepfake or any other synthetic image, video or audio of an identifiable person, except
          an avatar or a cloned voice made with that person&apos;s consent as described below
        </li>
        <li>a likeness or voice of anyone under 18, in any form</li>
        <li>
          a likeness or voice of a public figure, unless you are that person and the avatar or voice
          is your own
        </li>
        <li>
          non-consensual intimate imagery (NCII), or any other sexually explicit content, of anyone,
          with or without consent
        </li>
        <li>
          new words put into the mouth of a real person in existing footage of them (for example by
          re-syncing their lips), which a consent to an avatar or a voice does not cover
        </li>
        <li>anything intended to deceive, defraud, harass or harm any person</li>
      </ul>

      <h2 className={h2}>2. Consent for footage, images, avatars and voices</h2>
      <p className={p}>
        <strong className="font-semibold text-ink">Footage, images and audio you upload.</strong>{' '}
        Where you upload footage, images or audio of a person, or use a voice, you confirm that you
        have that person&apos;s documented consent to use it as you intend.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Avatars and cloned voices.</strong> Before you
        can create an avatar (reference pictures, short videos and short speech samples of a person,
        used to show that person in AI-generated videos and pictures) or a cloned voice, the Service asks you to
        confirm that you are that person or have their written permission, that they are 18 or
        older, that they have given informed consent to that use and to their files being sent to the
        AI providers the project uses, that you will keep a record of their consent, and that you
        will revoke or delete the avatar or voice if they withdraw it. A cloned voice can be used only
        in the project it was made in. An avatar can be used in other projects of the same workspace
        only if it was saved to the workspace avatar library, which for an avatar of a person needs
        that person&apos;s consent to cover use across the workspace (Terms 9.1).
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Labelling AI-generated video.</strong> Video made
        with an avatar is marked as AI-generated, in the file&apos;s data and, unless you choose
        otherwise, with a visible label. Do not remove, hide or alter that marking or label. If you
        turn the visible label off, tell the people who see the video, in another clear way, that it
        was generated or altered with AI.
      </p>

      <h2 className={h2}>3. Other prohibited content</h2>
      <p className={p}>
        You also may not use the Service to create or distribute content that is unlawful,
        defamatory, or infringes the rights of any third party, or to create deceptive or harmful
        output. Section 9 of the Terms has the full list.
      </p>

      <h2 className={h2}>4. How we enforce this policy</h2>
      <p className={p}>
        We act on reports we receive and on problems we find. We do not promise that every piece of
        content is checked in advance. Content checks run on parts of the Service where we have
        enabled them, and they are not a substitute for your own responsibility under this policy.
      </p>
      <p className={p}>
        If we find or are told that you have broken this policy, we may suspend or terminate your
        access to the Service, as described in Section 15 of the Terms. Where it is lawful and
        practical, we will tell you. We may also withhold or withdraw the free first video if we
        detect abuse or multiple accounts.
      </p>

      <h2 className={h2}>5. Report abuse</h2>
      <p className={p}>
        If you see content made with {siteConfig.name} that breaks this policy, or you believe a
        person&apos;s likeness or voice was used without their consent, write to{' '}
        <a href={`mailto:${SUPPORT_EMAIL}`} className={link}>{SUPPORT_EMAIL}</a>. If you are the
        person shown in an avatar or heard in a cloned voice and you did not agree, or you have
        withdrawn your consent, you can also write to{' '}
        <a href={`mailto:${PRIVACY_EMAIL}`} className={link}>{PRIVACY_EMAIL}</a>. We will make the
        avatar or voice unusable while we look into it and tell you what we did. Include a link or
        description of the content and, if you are the person affected, say so. We read every
        report.
      </p>

      <h2 className={h2}>5.1 Community recipes</h2>
      <p className={p}>
        This policy applies to everything you publish to the public gallery: the name, description,
        prompts, labels, narration lines and the preview video. Do not publish a recipe that is
        illegal, sexual, infringes someone else&apos;s rights, is spam, or misleads people about
        what it does or costs. People other than {siteConfig.name} write community recipes, so
        read a recipe&apos;s prompts before you copy it.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Reporting a recipe.</strong> Signed-in users
        with a verified email can report a recipe from its page. When several different people
        report a recipe, it is hidden until we have reviewed it. To send a takedown request, write
        to <a href={`mailto:${SUPPORT_EMAIL}`} className={link}>{SUPPORT_EMAIL}</a> with the link
        to the recipe, what is wrong, and how to reach you. The author can reply to the same
        address to explain why the recipe is lawful. Section 10 of the Terms describes both steps.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">What we may do.</strong> We may reject or remove
        a recipe, hide it while we review it, or require a person to review every later recipe from
        the same author. Copies already made are not changed.
      </p>
    </>
  );
}
