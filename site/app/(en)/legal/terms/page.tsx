import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import {
  COMPANY_LEGAL_NAME,
  COURTS_JURISDICTION,
  CONTROLLER_NAME,
  CREDIT_EXPIRY_PERIOD,
  FIRST_VIDEO_GRANT_CREDITS,
  FIRST_VIDEO_GRANT_EXPIRY,
  GOVERNING_LAW,
  LIABILITY_CAP,
  OPERATOR_STATEMENT,
  PAYMENT_PROVIDER_STATEMENT,
  PRIVACY_EMAIL,
  FAILED_RUN_POLICY,
  REFUND_POLICY,
  SERVICE_AGREEMENT_REFERENCE,
  SUPPORT_EMAIL,
  TERMS_LAST_UPDATED,
  TRADEMARK_STATEMENT,
} from '@/lib/legal-placeholders';
import { TRAINING_CLAIM_VENDORS } from '@/lib/provider-data-handling';

export const metadata = buildMetadata({
  title: 'Terms & Conditions',
  description: `The terms governing your use of the ${siteConfig.name} hosted service, your account, and this marketing website.`,
  path: '/legal/terms',
});

const h2 = 'mt-10 font-display text-xl font-bold text-ink first:mt-0';
const p = 'mt-4 font-sans text-ink-muted';
const h3 = 'mt-6 font-display text-base font-bold text-ink';
const ul = 'mt-4 list-disc space-y-2 pl-5 font-sans text-ink-muted';

export default function TermsPage() {
  return (
    <>
      <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
        Last updated: {TERMS_LAST_UPDATED}
      </p>
      <h1 className="mt-2 font-display text-3xl font-bold text-ink sm:text-4xl">
        Terms &amp; Conditions
      </h1>
      <p className={p}>
        These Terms &amp; Conditions (&quot;Terms&quot;) govern your access to and use of the{' '}
        {siteConfig.name} hosted service (the &quot;Service&quot;), your account, and this public
        marketing website. By creating an account, subscribing, or otherwise browsing or using the
        Service or this website, you agree to these Terms. If you do not agree, please do not use
        the Service or this website.
      </p>

      <h2 className={h2}>1. Who these terms are with</h2>
      <p className={p}>
        These Terms are between you and {CONTROLLER_NAME}, who operates the {COMPANY_LEGAL_NAME}{' '}
        service (&quot;we&quot;, &quot;us&quot;, &quot;our&quot;). {OPERATOR_STATEMENT}
      </p>

      <h2 className={h2}>2. Acceptance</h2>
      <p className={p}>
        By accessing any page of this website, submitting the contact form, creating an account, or
        otherwise using any part of the Service, you confirm that you accept these Terms and agree
        to comply with them. We may update these Terms as described in Section 19.
      </p>

      <h2 className={h2}>3. Scope</h2>
      <p className={p}>
        These Terms cover both your use of this public marketing website and your use of the hosted{' '}
        {siteConfig.name} Service, including any account you create and any workspace you belong to.
        If you have entered into a separate written agreement with us covering the Service (
        {SERVICE_AGREEMENT_REFERENCE}), that agreement governs to the extent of any conflict with
        these Terms; where no separate agreement applies, these Terms govern in full.
      </p>

      <h2 className={h2}>3.1 Public alpha</h2>
      <p className={p}>
        The Service is a public alpha. It works and is open to everyone, but you should expect rough
        edges, and features, limits and prices may change as we learn from how it is used.
      </p>

      <h2 className={h2}>4. Accounts and workspaces</h2>
      <p className={p}>
        You may need to create an account to use the Service. You must provide accurate information
        when you register and keep it up to date, and you are responsible for keeping your account
        credentials confidential and for all activity that occurs under your account. The Service
        may allow you to create or join workspaces shared with other users; each workspace has roles
        that determine what its members may do, and you are responsible for the actions taken from
        your account within any workspace you belong to.
      </p>

      <h2 className={h2}>5. Plans, subscriptions and auto-renewal</h2>
      <p className={p}>
        Access to parts of the Service may require a paid plan or the purchase of credits. Paid
        subscriptions renew automatically at the end of each billing period unless you cancel before
        the renewal date. You can view your plan and cancel at any time from your account settings;
        cancellation takes effect at the end of the then-current billing period. Prices may change in
        future. We will give you at least one billing cycle&apos;s notice of any price change that
        affects a renewing subscription, and you can cancel before it takes effect. The price you pay
        is the one shown by the payment provider at checkout.
      </p>

      <h2 className={h2}>6. Credits</h2>
      <p className={p}>
        Where the Service is priced in credits, credits have no cash value and expire{' '}
        {CREDIT_EXPIRY_PERIOD} after they are added to your account. Credits are consumed as you use
        the Service. {FAILED_RUN_POLICY} Refunds are set out in Section 8.
      </p>
      <h3 className={h3}>First video on us</h3>
      <p className={p}>
        When you verify your email, your account on the Trial plan receives a one-time grant of{' '}
        {FIRST_VIDEO_GRANT_CREDITS} credits. The grant expires {FIRST_VIDEO_GRANT_EXPIRY} after it
        is granted, is given once per person (not once per account or workspace), and requires a
        verified email address. Trial plan limits apply; see the{' '}
        <a className="text-accent-ink hover:text-accent" href="/pricing">
          pricing page
        </a>
        . We may withhold or withdraw the free first video if we detect abuse or multiple accounts.
      </p>

      <h2 className={h2}>7. Payment</h2>
      <p className={p}>
        {PAYMENT_PROVIDER_STATEMENT} Your purchase is subject to the
        merchant of record&apos;s buyer terms, and any applicable taxes are collected at checkout as part of that purchase.
      </p>

      <h2 id="refunds" className={h2}>8. Cancellation and refunds</h2>
      <p className={p}>{REFUND_POLICY}.</p>
      <p className={p}>
        If you are a consumer in the European Union, you have a 14-day right of withdrawal for
        digital services. Because the Service is delivered digitally, that right is lost once you
        expressly request immediate performance and acknowledge that you lose the right of
        withdrawal when performance begins; the acknowledgement you are asked to give at sign-up or
        checkout is where that request and acknowledgement are made.
      </p>
      <p className={p}>
        A plain-language summary is on our{' '}
        <a className="text-accent-ink hover:text-accent" href="/legal/refunds">
          Refund and Cancellation page
        </a>
        .
      </p>

      <h2 className={h2}>9. Permitted use and restrictions</h2>
      <p className={p}>
        You may use the website and the Service for lawful purposes only, and in a way that does not
        infringe the rights of, restrict, or inhibit anyone else&apos;s use of them. In particular,
        you agree not to:
      </p>
      <ul className={ul}>
        <li>
          Scrape, crawl, or systematically extract content from the website using automated means,
          except for standard, well-behaved search-engine indexing
        </li>
        <li>Reverse engineer, decompile, or disassemble any part of the website, the Service, or their underlying software</li>
        <li>
          Submit the contact form, or otherwise interact with the website, in a way that constitutes
          spam, abuse, or automated misuse
        </li>
        <li>Attempt to gain unauthorized access to any part of the website, the Service, its systems, or related networks</li>
        <li>Introduce malware, or otherwise interfere with the normal operation of the website or the Service</li>
      </ul>
      <p className={p}>
        You are responsible for the content you generate with the Service, including any content
        produced using artificial intelligence. You agree not to use the Service to generate or
        distribute content that is unlawful, defamatory, or infringes the rights of any third party,
        or to create deceptive or harmful output.
      </p>

      <h2 className={h2}>9.1 Real people: impersonation, likeness and consent</h2>
      <p className={p}>
        <strong className="font-semibold text-ink">
          ReelBolt is not a tool for impersonating real people.
        </strong>{' '}
        You may show or make a real person&apos;s likeness or voice with the Service only in the
        ways this section allows. You must not create, or attempt to create:
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
      <p className={p}>
        <strong className="font-semibold text-ink">Footage, images and audio you upload.</strong>{' '}
        Where you upload footage, images or audio of a person, or use a voice, you confirm that you
        have that person&apos;s documented consent to use it as you intend.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Avatars and cloned voices.</strong> The Service
        lets you create an avatar (reference pictures, short videos and short speech samples of a
        person, used to show that person in AI-generated videos and pictures) or a cloned voice only after you
        confirm, in the words the Service shows you, that you are that person or have their written
        permission, that they are 18 or older, that they have given informed consent to that use and
        to their files being sent to the AI providers the project uses, that you will keep a record
        of their consent, and that you will revoke or delete the avatar or voice if they withdraw it.
        You must keep that record for as long as the avatar or voice exists and for a reasonable
        period after, and give it to us if we ask for it in connection with a complaint or a request
        from an authority. A cloned voice can be used only in the project it was made in. An avatar
        can be used in the project it was made in, and in other projects only as the next paragraphs
        say.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Videos and pictures.</strong> An avatar may be
        used to make AI-generated videos and AI-generated pictures. The statement you confirm names
        what the person agreed to. An avatar of a person can be used in a picture only if the
        statement you confirmed covers pictures; if it does not, the Service asks you to confirm the
        current statement again before the avatar is used in a picture.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">The workspace avatar library.</strong> An Owner
        or Admin of a workspace may save an avatar to the workspace avatar library. Members of the
        same workspace may then use that avatar in other projects of the workspace. An avatar never
        leaves the workspace. For an avatar of a person, this is allowed only if that person&apos;s
        consent covers use across the workspace. The member who saves it confirms that, in the
        words the Service shows: that you are the person shown and heard in the files, or that you
        have that person&apos;s written permission; that the person is 18 or older; that they have
        given informed consent to their face, body and voice being used to generate AI videos and
        pictures in any project of the workspace, and to their files being sent for that purpose to
        the AI providers those projects use. A cloned voice is not part of the library and stays in
        its project.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Avatars of objects.</strong> You may also make an
        avatar of an object, a product, a vehicle or a place. You confirm, in the words the Service
        shows, that the files show an object, a product or a place and no identifiable person, and
        that you have the right to use them to generate AI videos and pictures in the project (or,
        for a workspace avatar, in any project of the workspace) and to send them for that purpose
        to the AI providers those projects use. The Service checks the pictures automatically for
        people. If it finds a person in one, it refuses the avatar. This check does not replace
        your own confirmation.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Variants.</strong> A variant of an avatar is a new
        set of pictures made from it. A variant is covered by the same consent as the avatar it was
        made from, and it ends when that consent ends.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Labelling AI-generated video.</strong> Video made
        with an avatar is marked by the Service as AI-generated, in the file&apos;s data and, unless
        you choose otherwise, with a visible label. You must not remove, hide or alter that marking
        or label. If you turn the visible label off, you must tell the people who see the video, in
        another clear way, that it was generated or altered with AI.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">When consent ends.</strong> If the person
        withdraws consent, you must revoke or delete the avatar or voice at once. For an avatar in the
        workspace library, revoking applies to the library item, to every copy of it imported into a
        project, and to every variant. Revoking stops any further use, including use in runs already
        in progress before their next generation. Videos and pictures already made are your files; you are responsible for deciding whether to keep, use or delete
        them, and you must not use them in a way the person has not agreed to.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">What we may do.</strong> We may revoke or delete
        an avatar or a cloned voice, and suspend or terminate your access to the Service as described
        in Section 15, if we believe this section has been broken or if the person shown asks us to
        and we cannot confirm their consent. We keep a record of when an avatar or voice was created,
        revoked and deleted, without the pictures or recordings, and we may give it to an authority
        where the law requires it. Our{' '}
        <a className="text-accent-ink hover:text-accent" href="/legal/acceptable-use">
          Acceptable Use Policy
        </a>{' '}
        restates these rules and explains how to report abuse.
      </p>

      <h2 className={h2}>10. Your content</h2>
      <p className={p}>
        You retain all rights in the content you upload to, and generate with, the Service. We claim
        no ownership over your content; you grant us only the limited rights we need to store,
        process, and deliver it to you as part of providing the Service.
      </p>
      <p className={p}>
        If you connect your own AI provider account to the Service (&quot;bring your own
        provider&quot;), your use of that provider is governed by your own agreement with that
        provider, not by these Terms, and content you send to it is sent directly to that provider.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Community recipes.</strong> You may publish a
        recipe from your workspace to the public gallery. A recipe is the structure and settings of
        a workflow, the text you wrote in it, and a short preview video. When you publish a recipe,
        you give anyone a worldwide, royalty-free licence to view, copy, adapt and run the recipe
        and to show its preview, limited to the {siteConfig.name} gallery, website and app. The
        licence does not cover promotion outside them. You confirm that you own or control the
        rights to the recipe, the preview and everything shown in them.
      </p>
      <p className={p}>
        You can unpublish a recipe at any time. Unpublishing ends the public listing. Copies
        already made stay with the people who made them. Our licence to keep showing the preview
        ends when you unpublish, and the preview stops being shown within 24 hours, because of
        cache delay.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Takedown.</strong> If you believe a recipe
        infringes your rights or breaks the Acceptable Use Policy, email{' '}
        <a href={`mailto:${SUPPORT_EMAIL}`} className="text-accent-ink hover:text-accent">{SUPPORT_EMAIL}</a>{' '}
        with the link to the recipe, what is wrong, proof of your right where it concerns
        copyright, and how to reach you. We acknowledge or answer within 5 business days. We may
        hide or remove a recipe while we review it. We tell the author a category of reason.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">Reply from the author.</strong> If we removed
        your recipe, you can reply to the same address and explain why it is lawful. We review
        your reply, decide whether to restore the recipe or keep it removed, and tell you a
        category of reason. We may limit publishing for an account that repeatedly publishes
        content that breaks the rules.
      </p>

      <h2 className={h2}>11. Third-party AI providers and your content</h2>
      <p className={p}>
        Running a workflow sends some of your content to third-party AI providers. Which parts are
        sent depends on the steps you use: a script or a brief can go to a chat model, a transcript or
        a voice recording can go to a speech provider, and a frame of your footage can go to a vision
        or image provider.
      </p>
      <p className={p}>
        If a step uses an avatar, the avatar&apos;s reference pictures, short videos and speech
        samples are among the content sent to the provider that generates the video or the
        picture. That includes avatars of objects, and the pictures and video stills used to make
        or check an avatar or a variant. Pictures of an object are also shown to a vision provider
        so that it can check that no person is in them. The Privacy Policy names those providers.
      </p>
      <p className={p}>
        Each provider handles your content under its own terms, which we link to in our{' '}
        <a className="text-accent-ink hover:text-accent" href="/legal/privacy">
          Privacy Policy
        </a>
        . We cannot limit what a provider does with your content on your behalf.
      </p>
      <p className={p}>
        <strong className="font-semibold text-ink">
          Some of these providers claim a licence over what you send them and what they return,
          including a licence to train their own models on it.
        </strong>{' '}
        As of the date of these Terms, that is the published position of{' '}
        {TRAINING_CLAIM_VENDORS.join(', ')}. If you do not want a particular piece of footage sent to
        a provider whose terms allow training on it, do not use a step that calls that provider.
      </p>
      <p className={p}>
        If you connect your own provider account (see section 10), your agreement is with that
        provider and this section does not change it.
      </p>

      <h2 className={h2}>12. Intellectual property</h2>
      <p className={p}>
        Unless otherwise indicated, all content on this website and in the Service (including text,
        graphics, logos, and the {siteConfig.name} name and marks) is owned by or licensed to us and
        is protected by applicable intellectual property laws. You may view and print pages from the
        website for your own personal or internal business use, but you may not otherwise reproduce,
        modify, distribute, or republish any content without our prior written consent.
      </p>

      <h2 className={h2}>12.1 Trademarks</h2>
      <p className={p}>
        {TRADEMARK_STATEMENT} An application to register the word mark has been filed in Italy and
        is pending; no registration has been granted. You may not use the {siteConfig.name} name or
        logo, or a name or logo that could be mistaken for them, without our prior written consent.
        You may use the name in plain text to refer truthfully to the Service, for example in a
        review, a tutorial or a comparison.
      </p>
      <p className={p}>
        Where we publish parts of the {siteConfig.name} source code as open-source software, that
        licence, and any commercial licence we grant, covers the code only. Neither grants any right
        in the {siteConfig.name} name, logo or other marks unless we agree otherwise in writing. Any
        trademark policy we publish with the source code explains what a fork or a self-hosted copy
        may and may not do.
      </p>

      <h2 className={h2}>13. User submissions via the contact form</h2>
      <p className={p}>
        When you submit information through the contact form, you grant us a non-exclusive,
        worldwide, royalty-free license to use that information for the purpose of responding to
        your enquiry and for our internal business purposes related to that enquiry, consistent with
        our Privacy Policy. You represent and warrant that you have the right to submit the
        information you provide, and that it does not infringe or violate the rights of any third
        party.
      </p>

      <h2 className={h2}>14. Third-party links and services</h2>
      <p className={p}>
        This website and the Service may link to, or interoperate with, third-party websites or
        services that are not owned or controlled by us. We are not responsible for the content,
        privacy practices, or terms of any third-party website or service, and linking to them does
        not imply our endorsement. You access any third-party website or service entirely at your
        own risk.
      </p>

      <h2 className={h2}>14.1 Project Arachnid attribution</h2>
      <p className={p}>
        ReelBolt accesses Shield by Project Arachnid{' '}
        <a className="underline" href="https://projectarachnid.ca">
          https://projectarachnid.ca
        </a>
        , a program of the Canadian Centre for Child Protection Inc., to facilitate the detection and
        removal of child sexual abuse material and harmful-abusive material of children and survivors
        of child sexual abuse on its services.
      </p>

      <h2 className={h2}>15. Suspension and termination</h2>
      <p className={p}>
        We may suspend or terminate your access to the Service if you breach these Terms, if we are
        required to do so by law, or to protect the Service or other users. Where we do so we will
        give you notice where it is lawful and practical to do so. Where it is lawful, you can download
        your files from the Service before your account is deleted, and you can ask us for a copy of
        your personal data as described in Section 11 of our Privacy Policy.
      </p>

      <h2 className={h2}>16. Availability and &quot;as is&quot; provision</h2>
      <p className={p}>
        We aim to keep the website and the Service available and up to date, but we do not guarantee
        that they will always be available, uninterrupted, secure, or error-free. The website and
        the Service are provided &quot;as is&quot; and &quot;as available&quot;, without warranties
        of any kind, whether express or implied, to the fullest extent permitted by applicable law.
      </p>

      <h2 className={h2}>17. Limitation of liability</h2>
      <p className={p}>
        To the fullest extent permitted by applicable law, our total liability to you arising out of
        or in connection with your use of the website or the Service, whether in contract, tort
        (including negligence), or otherwise, shall not exceed {LIABILITY_CAP}. Nothing in these
        Terms excludes or limits any liability that cannot lawfully be excluded or limited,
        including liability for death or personal injury caused by negligence, fraud, or fraudulent
        misrepresentation, or any statutory rights you may have as a consumer that cannot be waived
        by agreement.
      </p>

      <h2 className={h2}>18. Indemnity</h2>
      <p className={p}>
        If you use the Service for business purposes, you agree to indemnify and hold us harmless
        from any claims, losses, liabilities, damages, costs, and expenses (including reasonable
        legal fees) arising out of your breach of these Terms or your misuse of the website or the
        Service. This includes any claim by a person whose likeness or voice you used, or caused to
        be used, through the Service without the consent this agreement requires. This section does
        not apply to you if you are a consumer.
      </p>

      <h2 className={h2}>19. Changes to these terms</h2>
      <p className={p}>
        We may revise these Terms by updating this page. The &quot;Last updated&quot; date at the
        top of this document shows the most recent revision. If a change is material, we will tell
        you by email or in the Service before it takes effect, and you may terminate your account
        and stop using the Service if you do not agree. Other changes apply once posted.
      </p>

      <h2 className={h2}>20. Governing law and jurisdiction</h2>
      <p className={p}>
        These Terms are governed by and construed in accordance with {GOVERNING_LAW},
        without regard to its conflict-of-laws principles. If you use the Service for business
        purposes, you agree to submit to the exclusive jurisdiction of {COURTS_JURISDICTION} to
        resolve any dispute arising out of or relating to these Terms or your use of the website or
        the Service. If you are a consumer, this clause does not take away the mandatory consumer
        protections of the country where you live, or your right to bring a claim in the courts of
        that country.
      </p>

      <h2 className={h2}>21. Contact</h2>
      <p className={p}>Questions about these Terms can be sent to:</p>
      <p className={p}>{OPERATOR_STATEMENT}</p>
      <p className={p}>
        <a href={`mailto:${SUPPORT_EMAIL}`} className="text-accent-ink hover:text-accent">{SUPPORT_EMAIL}</a>
        {' '}(general enquiries) or{' '}
        <a href={`mailto:${PRIVACY_EMAIL}`} className="text-accent-ink hover:text-accent">{PRIVACY_EMAIL}</a>
        {' '}(privacy-related enquiries)
      </p>
    </>
  );
}
