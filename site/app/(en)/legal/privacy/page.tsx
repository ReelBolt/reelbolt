import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import { CookiePreferencesLink } from '@/components/consent/CookiePreferencesLink';
import {
  COMPANY_LEGAL_NAME,
  CONTACT_RETENTION_PERIOD,
  CONTENT_RETENTION_PERIOD,
  DISCLOSED_DATA_CATEGORIES,
  DISCLOSED_DATA_RETENTION_STATEMENT,
  DPO_STATEMENT,
  CONTROLLER_NAME,
  EMAIL_PROVIDER,
  HOSTING_PROVIDER,
  WEBSITE_DELIVERY_PROVIDER,
  REPORTING_PROVIDER,
  LAST_UPDATED,
  LOG_RETENTION_PERIOD,
  FIRST_TOUCH_COOKIE_PERIOD,
  VISIT_RECORD_RETENTION_PERIOD,
  MARKETING_CONSENT_RETENTION_PERIOD,
  MODEL_PROVIDERS,
  OBSERVABILITY_DPA_URL,
  OBSERVABILITY_PROVIDER,
  OBSERVABILITY_PROVIDER_REGION,
  OPERATOR_STATEMENT,
  PAYMENT_PROVIDER,
  PAYMENT_PROVIDER_STATEMENT,
  MARKETING_EMAIL_PROVIDER,
  PENDING_SUBSCRIPTION_PERIOD,
  PRIVACY_EMAIL,
  STORAGE_PROVIDER,
  IP_REPUTATION_PROVIDER,
  SUPERVISORY_AUTHORITY,
  TELEMETRY_RETENTION_PERIOD,
  TRANSFER_MECHANISM,
} from '@/lib/legal-placeholders';
import {
  GOOGLE_API_SERVICES_USER_DATA_POLICY_URL,
  GOOGLE_DRIVE_READONLY_SCOPE,
  GOOGLE_LIMITED_USE_STATEMENT,
  GOOGLE_NO_MODEL_TRAINING_STATEMENT,
} from '@/lib/google-user-data';
import { PROVIDER_DATA_HANDLING } from '@/lib/provider-data-handling';

export const metadata = buildMetadata({
  title: 'Privacy Policy',
  description: `How ${siteConfig.name} collects, uses, and protects information from visitors to this marketing website and from users of the hosted service.`,
  path: '/legal/privacy',
});

const h2 = 'mt-10 font-display text-xl font-bold text-ink first:mt-0';
const p = 'mt-4 font-sans text-ink-muted';
const ul = 'mt-4 list-disc space-y-2 pl-5 font-sans text-ink-muted';

export default function PrivacyPolicyPage() {
  return (
    <>
      <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
        Last updated: {LAST_UPDATED}
      </p>
      <h1 className="mt-2 font-display text-3xl font-bold text-ink sm:text-4xl">
        Privacy Policy
      </h1>
      <p className={p}>
        This Privacy Policy explains how {COMPANY_LEGAL_NAME} (&quot;we&quot;, &quot;us&quot;, or
        &quot;our&quot;) collects, uses, discloses, and protects information in connection with the{' '}
        {siteConfig.name} public marketing website and the hosted {siteConfig.name} Service. Please
        read it carefully. If you have questions, contact us using the details in Section 14.
      </p>

      <h2 className={h2}>1. Who we are</h2>
      <p className={p}>
        The {siteConfig.name} website and Service are operated by {CONTROLLER_NAME}, who is the data controller.
      </p>
      <p className={p}>{OPERATOR_STATEMENT}</p>
      <p className={p}>
        Privacy enquiries:{' '}
        <a href={`mailto:${PRIVACY_EMAIL}`} className="text-accent-ink hover:text-accent">
          {PRIVACY_EMAIL}
        </a>
      </p>
      <p className={p}>{DPO_STATEMENT}</p>

      <h2 className={h2}>2. Scope</h2>
      <p className={p}>
        This policy covers two things: (a) the public marketing website you are currently reading —
        the pages served at the root domain, such as the homepage, features, pricing, about, and
        contact pages — and (b) the hosted {siteConfig.name} Service and the accounts you use to
        sign in to it. The website handles contact-form submissions, site analytics and the short
        links that lead to it. The Service
        processes account, billing and usage data, and the project files you upload, to do the work
        you ask of it.
      </p>

      <h2 className={h2}>3. What we collect</h2>
      <p className={p}>We collect the following categories of data:</p>
      <ul className={ul}>
        <li>
          <strong>Contact-form submissions.</strong> When you use the contact form, we collect the
          name, email address, company name (optional), and message you provide, and whether you
          ticked the optional box to receive our marketing email.
        </li>
        <li>
          <strong>Marketing preferences.</strong> If you ask to receive our marketing email, from the
          contact form, your account settings, checkout or anywhere else we offer it, we record your
          email address, where you asked, the exact wording you agreed to and when. If you confirm
          by clicking the link in our confirmation email, we also record when you clicked and the IP
          address you clicked from. If you later unsubscribe, we record when.
        </li>
        <li>
          <strong>Server access logs.</strong> Like most websites, our hosting infrastructure
          automatically records technical information about each request, including your IP
          address, user-agent string, the requested page address (without anything after a
          question mark), the page that referred you, the response status and a timestamp.
        </li>
        <li>
          <strong>Short links.</strong> Some of the links we post lead to the website through a
          short address on our own domain: <span className="font-mono text-xs">/l/</span> followed
          by the link&apos;s name. When such a link is opened, we add one to a counter for that link
          and that day, and then send the browser on to the page the link points to. The counter is
          only a number. It holds no IP address, no cookie, no visitor ID and nothing else about the
          person who opened the link, and nothing is stored on or read from your device. We read the
          user-agent string of the request and the browser&apos;s request-type headers once, to
          tell a link preview fetched by an app, a crawler, or a page a browser loads ahead of time
          from a person opening the link. The request-type headers are a few short words in which a
          browser says whether an address was opened as a page or fetched in the background. The two
          kinds of request are counted separately, and neither the user-agent string nor those
          headers are kept with the count. The request reaches our servers through{' '}
          {WEBSITE_DELIVERY_PROVIDER}, the provider that serves the website (Section 6). It receives
          the IP address and the user-agent string of the request, as it does for every page of the
          website, and limits how many short links one address can open in a short time. The
          request itself is written to the server
          access logs described above, like any other request. The page you are sent to has
          campaign labels in its address, which the entries on visit and sign-up source data below
          describe. If the click cannot be counted at that moment, you are sent to the home page
          with the link&apos;s name as its one campaign label.
        </li>
        <li>
          <strong>Analytics data.</strong> Our website uses Google Analytics. Before you make a
          choice in the cookie banner, Google Analytics runs in a cookieless mode. It sets no
          cookie and stores nothing on your device. It receives a basic page-visit signal: the
          page address and title, the page that referred you, your browser language, your screen
          size, your browser and operating system details, and your IP address, from which Google
          derives an approximate location. The page address includes any campaign labels in the
          link you followed. The signal carries a random number that is new for
          every page you open. Besides page views, Google Analytics also records some
          interactions on the page automatically, in the same cookieless way before you choose:
          the time you spend on a page, scrolling to the bottom of it, clicks on links that lead
          off our website, searches
          made on the site, file downloads, video engagement, and that a form was started or
          submitted (not what you typed). If you reject cookies,
          Google Analytics is not loaded and receives nothing more from your visit (the page you
          were on when you rejected may already have sent its page-visit signal). Only if you
          affirmatively accept cookies via the cookie banner does Google Analytics set its cookies
          and collect fuller usage data such as pages viewed, session duration, device/browser
          type, and approximate location derived from your IP address. We run no advertising, and
          Google Analytics advertising features stay switched off. The same choice also applies to
          the ReelBolt app: the app loads Google Analytics only once you accept, with Google
          signals and ad personalization switched off, and with identifying parts of the page
          address removed before anything is sent.
        </li>
        <li>
          <strong>Visit and sign-up source data.</strong> The address you arrive on may carry
          campaign labels. They are short words we put in our own links to say where a link was
          posted: <span className="font-mono text-xs">utm_source</span>,{' '}
          <span className="font-mono text-xs">utm_medium</span>,{' '}
          <span className="font-mono text-xs">utm_campaign</span>,{' '}
          <span className="font-mono text-xs">utm_content</span> and{' '}
          <span className="font-mono text-xs">utm_term</span> (on a short link,{' '}
          <span className="font-mono text-xs">utm_content</span> is the link&apos;s name). They
          describe the link, not the person who follows it. Only if you accept cookies in the cookie
          banner, the website gives your browser a random visitor ID (stored in your browser&apos;s
          local storage as <span className="font-mono text-xs">rf-visitor-id</span>) and a
          first-party cookie called <span className="font-mono text-xs">rb_ft</span>. The cookie
          remembers where you came from: the campaign labels in the link you followed, the website
          that referred you, the first page you landed on, your visitor ID and the time. It lasts
          {' '}{FIRST_TOUCH_COOKIE_PERIOD}. The app&apos;s sign-up page sets the same cookie, for
          the browser session only, when you accepted cookies, no such cookie exists yet and the
          address of the sign-up page carries campaign labels. The cookie and the visitor ID belong
          together: on the website, when the cookie is gone and you visit again, a new cookie is set
          together with a new random visitor ID, and the same happens when the sign-up page sets the
          cookie. There is one exception. If you open the app&apos;s sign-up page while no cookie
          exists and its address carries no campaign labels, no new cookie is set, and the two
          sign-up records described next are sent with the visitor ID already stored in your
          browser, which may be the one an earlier visit or an earlier sign-up used. When visit recording is switched on, and still only if
          you accepted, the website and the sign-up page send us short records: that a visit
          started, which page of the website you opened, which call-to-action button you pressed
          (such as Start free), and that you opened the sign-up form, sent it, or pressed the Google
          or GitHub button on it. A page is recorded as
          one name from a fixed list (for example the home page, the pricing page, or one named blog
          post), and a button as a fixed name for where it sits (for example the header). Each
          record holds your visitor ID, the kind of record, the time, and that one name or a source
          label (the campaign source, else the referring website, else the word
          &quot;direct&quot;). It never holds anything you typed, and never the full page address.
          Every record is sent together with the first-touch details the cookie holds (the
          campaign labels of the link you first arrived on, the website that referred you and the
          path of the first page you landed on), and the first record we
          receive stores them once against your visitor ID as your first touch. These records hold
          no IP address and no user-agent string, although the requests that carry them are also
          written to the server access logs described above. When you create an account, we store
          the same first-touch information against it (source label, campaign labels, referring
          website, first page and visitor ID), so that we can tell which channels bring sign-ups,
          and the cookie is removed when you sign up. If you then go back on the sign-up page to
          correct your email address, the cookie is put back until you close the browser, so that
          the account you really use carries the same first touch. If you never accepted cookies, nothing
          described in this entry is stored. The next entry describes what can still be saved when
          you sign up.
        </li>
        <li>
          <strong>What a sign-up link carries.</strong> The sign-up buttons on the website add up
          to three things to the address of the sign-up page. One is an audience hint, when you came
          through a page or a link made for one audience (for example{' '}
          <span className="font-mono text-xs">for=creator</span>). Another is the campaign labels
          of the link you arrived on. The third is the landing path: the path of the page of the
          website your visit began on (for example{' '}
          <span className="font-mono text-xs">lp=/pricing</span>). It is added only together with
          campaign labels, and it is the path alone: nothing that followed a question mark in the
          address, and never the website that referred you. A sign-up button on the pricing page
          also names the plan you chose (for example{' '}
          <span className="font-mono text-xs">plan=creator</span>), so that the app can take you to
          that plan once your account exists; the plan is not saved with your account when you sign
          up (Section 5 describes where the app keeps it in the meantime). Nothing on your device is used for this: the values travel in
          the link you click and in the sign-up request, whether or not you accepted cookies. The
          audience hint is saved with your new account as a suggestion, so that the welcome
          questions described below can start on the matching answer. The campaign labels in the
          link you followed may be saved with your account when you sign up; they say which of our
          posts or pages you came from, not who you are. With them we save the time and the landing
          path the link carried; when the link carried none and the sign-up page was the first page
          you opened, we save that page&apos;s path. If you accepted cookies, the
          first-touch cookie described above is used for this instead. If you sign up with Google
          or GitHub, the audience hint and, where we save them, the campaign labels and the landing
          path pass through that provider&apos;s sign-in address as part of a state value, which
          the provider sends back to us unchanged. Besides a random number, the state value holds
          only those short words and that path. If a sign-in with Google or GitHub does not
          complete (for example you cancel it, or no account can be created), you are sent back to
          the app&apos;s sign-in page, and the same values come back in the address of that page
          next to a short word for what went wrong: the audience hint and, where we save them, the
          campaign labels and the landing path. This way they are not lost if you then sign up
          another way. The Sign in link on the website carries the same things as the sign-up
          buttons, because signing in with Google or GitHub creates an account when you have none.
          Two kinds of link between pages of the website carry them as well, in the address only:
          the language links in the footer, and a link from a translated page to a page that exists
          only in English. Those links open the next page as a new document, and without the values
          in its address that page would no longer know which link you arrived on or where your
          visit began. Nothing is stored for this either.
        </li>
        <li>
          <strong>Account data.</strong> When you create an account, we collect the name, email
          address and password you provide, and the workspaces you create or join.
        </li>
        <li>
          <strong>Welcome questions.</strong> After you sign up we ask two questions: what you are
          here to make a video for (for example your channel, your business, your lessons, your
          cause, your software product or your clients) and what you have to start from (clips you
          already shot, an app or website you built, or just an idea). Each answer is one choice from a fixed list, with nothing to
          type. Answering is optional, and you can skip the questions. We store your answers, or
          that you skipped, and when, with your account, next to the audience hint from your
          sign-up link if there was one. We use them to choose your first recipe and the examples
          you see, and to count in our own statistics what kinds of people sign up and how far they
          get. Those statistics are produced with the reporting tool named in Section 6, from a
          nightly copy that holds your answers next to your account ID. They are deleted with your account.
        </li>
        <li>
          <strong>Billing data.</strong> When you subscribe or buy credits, payment is handled by our{' '}
          payment provider (currently {PAYMENT_PROVIDER}, see Section 6). We receive the subscription or transaction status we need to run your
          account; we do not receive or store your full payment card details.
        </li>
        <li>
          <strong>Usage data.</strong> We meter how the Service is used — for example which runs
          you start and the credits they consume — so that we can operate and bill the Service.
        </li>
        <li>
          <strong>First-step records.</strong> We record that certain steps happened on your
          account, most of them only the first time: for example that you created your first
          project, uploaded your first file, downloaded your first video, and answered or skipped
          the welcome questions. Each record holds the name of the step, the time, your account and
          workspace and, for some steps, one fixed word, such as your answer to the first
          welcome question or the recipe you picked. It holds no file, file name, text or
          other content. These records are written by our own servers when the step happens, not
          sent by your browser, so they do not depend on the choice you make about the funnel
          analytics described below. They are part of the nightly copy that our reporting tool reads
          (Section 6).
        </li>
        <li>
          <strong>Uploaded content.</strong> The files and text you upload to the Service, which we
          process to generate your videos.
        </li>
        <li>
          <strong>Avatars.</strong> If you create an avatar of a person or of an object, we keep the reference
          pictures, short videos and short speech samples you attach to it, the person&apos;s name as
          you type it, whether you are that person or have their written permission, your
          confirmation that they are 18 or older, a signed release if you choose to upload one, and
          the consent record: who confirmed, when, the exact statement shown, its version and its
          language. For an avatar of an object we keep the pictures and the confirmation that they
          show no person. If an Owner or Admin saves an avatar to the workspace avatar library, it is
          visible to and usable by the members of that workspace, and we keep that confirmation too.
          We also keep a log of when the avatar was created, changed, revoked and deleted.
          The log holds no pictures, recordings, links or the person&apos;s name. The person shown
          may not be a user of the Service.
        </li>
        <li>
          <strong>Community recipes.</strong> If you publish a recipe, the public gallery shows an
          author label. It starts as your account display name, and you can change it for each
          recipe. No other detail about you or your workspace is shown. The gallery also shows how
          many workspaces copied a recipe and, from 5 ratings, the average of its ratings. We store
          who rated a recipe and who reported one, and we never show that to the author or the
          public. We keep the versions of a recipe while it is published, and after removal only as
          long as we need them. We also keep the reports and a moderation log of approvals,
          rejections, removals and hides. Routine moderation log entries and handled reports are
          deleted 12 months after they were written or handled. Log entries, and their related
          trail, tied to a safety, illegal-content, copyright or rejected-by-moderation finding are
          kept longer, as needed for safety and legal duties. A dismissed report is cut to a minimal
          record (the recipe and the account only, no reason or note) after 12 months, until the
          account is deleted.
        </li>
        {DISCLOSED_DATA_CATEGORIES.map((category) => (
          <li key={category.label}>
            <strong>{category.label}.</strong> {category.description}
          </li>
        ))}
      </ul>
      <p className={p}>
        <strong>First-video abuse check.</strong> When you create an account, {IP_REPUTATION_PROVIDER}{' '}
        receives your sign-up IP address for fraud prevention. We may withhold or withdraw the free
        first video if we detect abuse or multiple accounts.
      </p>

      <h2 className={h2}>4. Why we collect it, and our lawful basis</h2>
      <p className={p}>
        We only process personal data where we have a valid lawful basis to do so. The list below
        summarizes the purpose and legal basis for each category described in Section 3.
      </p>
      <ul className={ul}>
        <li>
          <strong>Contact-form data</strong> is processed to respond to your enquiry and, where
          applicable, to take steps you request prior to entering into a contract with us. Lawful
          basis: performance of, or steps prior to, a contract, and/or our legitimate interest in
          responding to enquiries directed at us.
        </li>
        <li>
          <strong>Server access logs</strong> are processed to operate, secure, and troubleshoot the
          website (for example, detecting abuse or diagnosing errors). Lawful basis: legitimate
          interest in keeping the website secure and functioning correctly.
        </li>
        <li>
          <strong>Analytics data</strong> is processed to understand how visitors use the site and
          to improve it. Lawful basis: your consent, obtained through the cookie banner described in
          Section 5, for Google Analytics cookies and the fuller measurement they allow; and our
          legitimate interest in knowing whether the website is reached and works (Article 6(1)(f)
          GDPR) for the cookieless page-visit signal described in Section 3, which Google
          Analytics receives only before you choose; rejecting cookies ends it. You may withdraw your
          consent at any time. For questions about the cookieless signal, contact us using the
          details in Section 14.
        </li>
        <li>
          <strong>Short-link counts</strong> tell us how often each link we post is opened. Counting
          stores nothing on your device and keeps nothing that identifies a person, so we do not ask
          for consent for it. Handling the request itself, like any other request to the website,
          rests on our legitimate interest in running the website and knowing which of our posts
          are opened (Article 6(1)(f) GDPR).
        </li>
        <li>
          <strong>Visit and sign-up source data</strong> is processed to measure which links, pages
          and buttons bring sign-ups, so we can decide where to spend our effort. Lawful basis: your
          consent, obtained through the same cookie banner.
        </li>
        <li>
          <strong>The audience hint, the campaign labels and the landing path a sign-up link carries</strong> are
          processed for the same purpose, and the audience hint also to start the welcome questions
          on a fitting answer. They are not read from or stored on your device, so the cookie banner
          does not cover them. Lawful basis: our legitimate interest in knowing which of our posts
          and pages bring sign-ups (Article 6(1)(f) GDPR). You can object at any time, as Section 11
          explains.
        </li>
        <li>
          <strong>Account, billing and usage data</strong> are processed to provide the Service you
          signed up for, to bill you, and to keep the Service secure and functioning. Lawful basis:
          performance of our contract with you, compliance with our accounting obligations, and our
          legitimate interest in operating and securing the Service.
        </li>
        <li>
          <strong>Welcome answers</strong> are processed to set up your first project and examples
          to fit what you told us, and to count in our own statistics what kinds of people sign up
          and how far they get. Lawful basis: performance of our contract with you, for the set-up
          (Article 6(1)(b) GDPR), and our legitimate interest in understanding who uses the Service,
          for the statistics (Article 6(1)(f) GDPR).
        </li>
        <li>
          <strong>First-step records</strong> are processed to see where new accounts get stuck, so
          that we can fix it. Lawful basis: our legitimate interest in operating and improving the
          Service (Article 6(1)(f) GDPR).
        </li>
        <li>
          <strong>Uploaded content</strong> is processed to carry out the work you ask the Service to
          do — for example, to generate a video from your files. Lawful basis: performance of our
          contract with you.
        </li>
        <li>
          <strong>Avatars</strong> are processed only to show the person in AI-generated videos and
          pictures in the project where the avatar was made, or, if it is saved to the workspace
          avatar library, in the projects of that workspace, and to keep the proof that they agreed.
          Pictures of an object are sent to the vision model provider of the workspace, which checks
          that no person is in them; the avatar is refused if one is found. We treat a
          person&apos;s face, body and voice in an avatar with the care the law requires for
          sensitive data. Lawful basis: for the person shown, their explicit consent (Article 6(1)(a)
          and, where Article 9 GDPR applies, Article 9(2)(a) GDPR), which the account holder collects
          and confirms to us before the avatar can be created; for you as the account holder,
          performance of our contract with you. The consent record and the log are kept so that we
          can show when and how consent was confirmed and that the avatar was deleted. Lawful basis:
          our legitimate interest in proving consent and in defending and responding to complaints
          (Article 6(1)(f) GDPR) and, where it applies, our legal obligation. A signed release you
          upload is kept only as that proof and is never sent to any AI provider.
        </li>
        <li>
          <strong>Marking AI-generated video.</strong> Video made with an avatar carries a
          machine-readable mark that it was generated with AI and, unless the account holder turns it
          off and discloses it another way, a visible label. We record in the video&apos;s details that
          it was AI-generated and which avatar was used.
        </li>
        <li>
          <strong>Marketing email</strong> (product news, tips and offers) is sent only if you have
          opted in and confirmed. Lawful basis: your consent (Article 6(1)(a) GDPR). You can withdraw
          it at any time, as Section 11 explains. Withdrawing does not affect email we sent before
          you did.
        </li>
        <li>
          <strong>Records of your marketing choices</strong> are kept so we can show when and how
          you agreed, and so we never email you again after you unsubscribe. Lawful basis: our
          legitimate interest in proving consent and honoring your opt-out (Article 6(1)(f) GDPR).
        </li>
        <li>
          <strong>Records of your telemetry choice</strong> are kept so we can show whether you
          granted or declined telemetry. Lawful basis: our legitimate interest in evidencing and
          honoring your choice, not the consent that governs the telemetry itself (Article 6(1)(f)
          GDPR). The record is tied to your account and can be linked to your email address, so it
          is not anonymous.
        </li>
        <li>
          <strong>Product telemetry, error tracking and product-health signals</strong> are processed
          to run the Service, to find and fix failures, and to see when the alpha is struggling.
          Lawful basis: our legitimate interest in operating, securing and improving the Service
          (Article 6(1)(f) GDPR).
        </li>
        <li>
          <strong>Funnel analytics and browser-side error reports</strong> are collected only with
          your consent (Article 6(1)(a) GDPR), asked for in the product and off until you allow it.
          You may withdraw at any time, and declining changes nothing about what you can do.
        </li>
        <li>
          <strong>Feedback you send us</strong> is processed to read and act on your report. Lawful
          basis: your consent when you choose to attach context, and our legitimate interest in
          responding to what you tell us (Article 6(1)(a) and 6(1)(f) GDPR).
        </li>
      </ul>
      <p className={p}>
        <strong>Safety screening.</strong> We automatically screen files you upload and images, audio
        and videos you create against our content rules, to prevent illegal and harmful content. This
        also covers videos you never open or share. For this we send up to 20 downscaled frames and
        the text of a file or a transcript to OpenAI or Azure OpenAI. To make the transcript, we send
        up to 10 minutes of the audio to a transcription provider. The original video file is not
        sent. A result is stored with the file. Lawful basis: our legitimate interest in keeping the
        Service safe and lawful (Article 6(1)(f)
        GDPR). Pictures, videos and audio attached to an avatar are screened before the avatar can be
        used, and an avatar cannot be created from a file that has not been screened yet.
      </p>
      <p className={p}>
        <strong>Community recipes</strong> (Section 3). The text of a recipe you publish is checked
        by OpenAI&apos;s moderation service, one of the providers named for safety screening above, and the
        preview video is screened and scanned for illegal material like other videos. Lawful basis:
        our legitimate interest in running and keeping safe a public gallery (Article 6(1)(f)
        GDPR), and, for publishing the recipe itself, performance of our contract with you
        (Article 6(1)(b) GDPR).
      </p>
      <p className={p}>
        <strong>Abuse prevention for the free first video</strong> (Section 3). Lawful basis: our
        legitimate interest in preventing abuse of the free offer (Article 6(1)(f) GDPR).
      </p>
      <p className={p}>
        The payment provider&apos;s checkout may offer its own box for receiving the provider&apos;s
        marketing email. That choice belongs to the provider. We do not receive it as
        permission to send you {siteConfig.name} marketing email, and we never use it for that.
      </p>

      <h2 className={h2}>5. Cookies and similar technologies</h2>
      <p className={p}>
        We use a small number of storage mechanisms, summarized below. We set no analytics or
        measurement cookie before you make a choice in the cookie banner. Your initial choice itself is remembered in
        your browser&apos;s local storage, not a cookie. A few entries do not depend on that choice,
        because they only remember something you did yourself: the language you picked, a notice or
        offer bar you closed, and where you were going when you started to sign up. The table says
        for each one when it is written. One thing does run before you choose and
        is not a cookie: the Google Analytics tag loads on every page of the website and sends the
        cookieless page-visit signal described in Section 3. It stops once you reject. Opening one
        of our short links stores nothing on your device. Following a sign-up link that carries an
        audience hint or campaign labels stores nothing on your device either, unless you accepted
        cookies: then the app&apos;s sign-up page may set the{' '}
        <span className="font-mono text-xs">rb_ft</span> cookie and the visitor ID from the campaign
        labels in its address, as the table below and Section 3 describe.
      </p>
      <div className="mt-4 overflow-x-auto">
        <table className="w-full min-w-[560px] border-collapse text-left text-sm text-ink-muted">
          <thead>
            <tr className="border-b border-line-strong text-ink">
              <th className="py-2 pr-4 font-semibold">Name</th>
              <th className="py-2 pr-4 font-semibold">Type</th>
              <th className="py-2 pr-4 font-semibold">Purpose</th>
              <th className="py-2 pr-4 font-semibold">Duration</th>
            </tr>
          </thead>
          <tbody>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">rf-cookie-consent</td>
              <td className="py-2 pr-4">Local storage (not a cookie)</td>
              <td className="py-2 pr-4">
                Strictly necessary: remembers whether you accepted or rejected analytics cookies,
                so we don&apos;t ask again on every visit.
              </td>
              <td className="py-2 pr-4">Persistent, until you clear it or change your choice</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">rb_locale</td>
              <td className="py-2 pr-4">First-party cookie</td>
              <td className="py-2 pr-4">
                Functional: remembers the language you picked, so that the website and the app open
                in it, and so that emails we send you while no language is saved in your account are
                written in it. Set only when you pick a language yourself: a language link in the
                website&apos;s footer, or the language menu in the app. It holds the language code
                and nothing else.
              </td>
              <td className="py-2 pr-4">One year</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">reelbolt:promo-banner-dismissed</td>
              <td className="py-2 pr-4">Session storage (not a cookie)</td>
              <td className="py-2 pr-4">
                Functional: remembers which offer bar you closed, so it stays closed in this browser
                tab. Written only when you close the bar. It holds a short description of that offer
                (its kind, end date, headline and discount) and nothing about you.
              </td>
              <td className="py-2 pr-4">Until you close the tab</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">rb-signup-intent:v1</td>
              <td className="py-2 pr-4">Local storage (not a cookie), written by the app&apos;s sign-up and sign-in pages</td>
              <td className="py-2 pr-4">
                Functional: when the link that brought you to sign up named a plan or a page to
                return to, remembers those two values so that you arrive there once your account
                exists. Written only when you send the sign-up form or press the Google or GitHub
                button. Besides the two values, the entry holds the time it was written and a short code computed in your browser
                from your email address (not the address itself), so that only that account picks
                the entry up. For a sign-up by email the code is added when you send the form; for a
                sign-up with Google or GitHub it is added once you are signed in. The entry is read only by the app in your browser and is not used for
                measurement.
              </td>
              <td className="py-2 pr-4">
                Removed when used. An entry that is not used lapses after three days at most (after
                30 minutes if a Google or GitHub sign-in did not come back in that time) and is
                deleted the next time you open the app signed in on that browser.
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">rb_ft</td>
              <td className="py-2 pr-4">First-party cookie</td>
              <td className="py-2 pr-4">
                Acquisition measurement: remembers the campaign labels, referring website and first
                page of your first visit, with your visitor ID and the time. Set
                only after you accept; removed when you sign up, or when you reject or withdraw. If
                you go back on the sign-up page to correct your email address, it is put back until
                you close the browser.
              </td>
              <td className="py-2 pr-4">
                {FIRST_TOUCH_COOKIE_PERIOD} (a session cookie when the app&apos;s sign-up page sets it)
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">rf-visitor-id</td>
              <td className="py-2 pr-4">Local storage (not a cookie)</td>
              <td className="py-2 pr-4">
                Acquisition measurement: a random ID that ties your visits together without naming
                you. Created only after you accept; removed when you reject or withdraw.
              </td>
              <td className="py-2 pr-4">
                Persistent, until you clear it or withdraw consent. Replaced by a new random ID
                whenever a new <span className="font-mono text-xs">rb_ft</span> cookie is set.
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">rf-visit-sent</td>
              <td className="py-2 pr-4">Session storage (not a cookie)</td>
              <td className="py-2 pr-4">
                Acquisition measurement: marks that this browser tab has already sent its visit
                record, so that one visit is counted once. Set only after you accept; removed when
                you reject or withdraw.
              </td>
              <td className="py-2 pr-4">Until you close the tab</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">Google Analytics tag</td>
              <td className="py-2 pr-4">Script (no cookie, nothing stored on your device)</td>
              <td className="py-2 pr-4">
                Analytics: sends the cookieless page-visit signal and automatic interaction
                measurements described in Section 3 while you have not chosen. Not loaded once you
                reject.
              </td>
              <td className="py-2 pr-4">Signals sent while you are on a page</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4 font-mono text-xs">_ga</td>
              <td className="py-2 pr-4">Cookie (Google Analytics)</td>
              <td className="py-2 pr-4">Analytics: distinguishes unique visitors. Set only after you accept.</td>
              <td className="py-2 pr-4">Typically 2 years</td>
            </tr>
            <tr className="align-top">
              <td className="py-2 pr-4 font-mono text-xs">_ga_&lt;container-id&gt;</td>
              <td className="py-2 pr-4">Cookie (Google Analytics)</td>
              <td className="py-2 pr-4">
                Analytics: persists session state for a specific Google Analytics property. Set
                only after you accept.
              </td>
              <td className="py-2 pr-4">Typically 2 years (session cookie variants: 24 hours)</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p className={p}>
        You can change your mind at any time: <CookiePreferencesLink className="text-accent-ink hover:text-accent" /> reopens the
        consent banner so you can accept or reject analytics cookies again. If you reject or
        withdraw, we remove <span className="font-mono text-xs">rb_ft</span>,{' '}
        <span className="font-mono text-xs">rf-visitor-id</span> and{' '}
        <span className="font-mono text-xs">rf-visit-sent</span> and stop Google Analytics from
        collecting anything further, on later visits too for as long as your choice is stored.
        Google Analytics cookies that are already in your browser are not deleted by this: they expire on their own
        schedule, or you can delete them in your browser settings.
      </p>

      <h2 className={h2}>6. Sharing and processors</h2>
      <p className={p}>
        We do not sell your personal data. We share limited data with the service providers below.
        Each acts on our behalf under appropriate contractual terms, except {PAYMENT_PROVIDER}, as
        explained after the table.
      </p>
      <div className="mt-4 overflow-x-auto">
        <table className="w-full min-w-[560px] border-collapse text-left text-sm text-ink-muted">
          <thead>
            <tr className="border-b border-line-strong text-ink">
              <th className="py-2 pr-4 font-semibold">Service</th>
              <th className="py-2 pr-4 font-semibold">Provider</th>
              <th className="py-2 pr-4 font-semibold">Purpose</th>
            </tr>
          </thead>
          <tbody>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Payment</td>
              <td className="py-2 pr-4">{PAYMENT_PROVIDER}</td>
              <td className="py-2 pr-4">
                Sells subscriptions and credits to you as the merchant of record, as an independent
                controller
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Cloud hosting</td>
              <td className="py-2 pr-4">{HOSTING_PROVIDER}</td>
              <td className="py-2 pr-4">Hosts the Service and processes its server access logs</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Website delivery</td>
              <td className="py-2 pr-4">{WEBSITE_DELIVERY_PROVIDER}</td>
              <td className="py-2 pr-4">
                Serves the marketing website and its short links, and passes requests for the
                Service on to our servers. It receives the IP address and the user-agent string of
                each request and limits how many requests one address can make in a short time
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Storage</td>
              <td className="py-2 pr-4">{STORAGE_PROVIDER}</td>
              <td className="py-2 pr-4">
                Stores uploaded content and generated files, and the nightly reporting files
                described below the table
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Email</td>
              <td className="py-2 pr-4">{EMAIL_PROVIDER}</td>
              <td className="py-2 pr-4">Delivers contact-form submissions and transactional email</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Marketing email</td>
              <td className="py-2 pr-4">{MARKETING_EMAIL_PROVIDER}</td>
              <td className="py-2 pr-4">Delivers marketing email, only from our own list of people who opted in</td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">AI model providers</td>
              <td className="py-2 pr-4">{MODEL_PROVIDERS}</td>
              <td className="py-2 pr-4">
                Process data sent for ReelBolt-supplied inference when a workflow runs, and for
                automatic safety screening (OpenAI or Azure OpenAI, and the transcription provider) and the check of published recipe text (OpenAI&apos;s moderation service)
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Telemetry and error monitoring</td>
              <td className="py-2 pr-4">{OBSERVABILITY_PROVIDER}</td>
              <td className="py-2 pr-4">
                Stores and displays product telemetry, error reports and product-health signals
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">Internal reporting</td>
              <td className="py-2 pr-4">{REPORTING_PROVIDER}</td>
              <td className="py-2 pr-4">
                Produces our own statistics from the nightly reporting files described below the
                table
              </td>
            </tr>
            <tr className="border-b border-line align-top">
              <td className="py-2 pr-4">IP reputation</td>
              <td className="py-2 pr-4">{IP_REPUTATION_PROVIDER}</td>
              <td className="py-2 pr-4">
                Receives your sign-up IP address for fraud prevention on the first-video offer
              </td>
            </tr>
            <tr className="align-top">
              <td className="py-2 pr-4">Analytics</td>
              <td className="py-2 pr-4">Google Analytics (Google Ireland Ltd)</td>
              <td className="py-2 pr-4">
                Processes the analytics data described in Section 3. On the website it receives a
                cookieless page-visit signal before you choose, nothing after you reject, and sets
                its cookies only once you have given consent; in the app it is loaded only once you
                have given consent
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p className={p}>
        {PAYMENT_PROVIDER_STATEMENT} The merchant of record takes the payment, charges any tax and
        issues the invoice, and decides how it uses the payment and billing details it collects from
        you, so it is an independent controller of that data under its own privacy notice, not a
        processor acting for us. We receive only what we need to run your account, such as your
        subscription status, the transactions on it and any discount applied.
      </p>
      <p className={p}>
        The AI model providers listed above are the model providers you may be routed through when{' '}
        {siteConfig.name} supplies the AI for a run. If you connect your own AI provider account
        (&quot;bring your own provider&quot;), that traffic goes to your own provider under your own
        agreement with it — not to the providers listed above. Safety screening is the exception: it
        always uses the providers we choose.
      </p>
      <p className={p}>
        Product telemetry, error reports and product-health signals (Section 3) are exported to{' '}
        {OBSERVABILITY_PROVIDER}
        {OBSERVABILITY_PROVIDER_REGION ? ` in the ${OBSERVABILITY_PROVIDER_REGION} region` : ''}, which stores and displays them on our behalf under Grafana&apos;s{' '}
        <a
          href={OBSERVABILITY_DPA_URL}
          className="text-accent-ink underline underline-offset-2 hover:text-accent"
        >
          data processing agreement
        </a>
        .
      </p>

      <p className={p}>
        <strong>Internal reporting.</strong> Our own statistics, including the sign-up and
        first-step figures described in Section 3, are produced with {REPORTING_PROVIDER}. Each
        night a copy of the records they are built from is written to files held in{' '}
        {STORAGE_PROVIDER}, and the reporting tool reads those files. Each night&apos;s files
        replace the ones from the night before. The copy includes account records (with the name and
        email address of the account), workspace, billing and usage records, first-step records,
        welcome answers, the sign-up source stored with an account, and the visit records and first
        touch of visitors who accepted cookies, with their visitor IDs. It holds no passwords and
        none of the files you upload or the videos made from them.
      </p>

      <h3 className="mt-8 font-display text-base font-bold text-ink">
        6.1 What each AI provider does with your content
      </h3>
      <p className={p}>
        Each provider handles the content we send it under its own terms, linked below. These are the
        same providers the Terms refer to.
      </p>
      <div className="mt-4 overflow-x-auto">
        <table className="w-full min-w-[560px] border-collapse text-left text-sm text-ink-muted">
          <thead>
            <tr className="border-b border-line-strong text-ink">
              <th className="py-2 pr-4 font-semibold">Provider</th>
              <th className="py-2 pr-4 font-semibold">What is sent</th>
              <th className="py-2 pr-4 font-semibold">Its terms</th>
            </tr>
          </thead>
          <tbody>
            {PROVIDER_DATA_HANDLING.map((row) => (
              <tr key={row.vendor} className="border-b border-line align-top">
                <td className="py-2 pr-4 font-semibold text-ink">{row.vendor}</td>
                <td className="py-2 pr-4">{row.sent}</td>
                <td className="py-2 pr-4">
                  <ul className="space-y-1">
                    {row.terms.map((link) => (
                      <li key={link.url}>
                        <a
                          href={link.url}
                          className="text-accent-ink underline underline-offset-2 hover:text-accent"
                        >
                          {link.label}
                        </a>
                      </li>
                    ))}
                  </ul>
                  {row.trainingWarning ? (
                    <p className="mt-2">
                      <strong className="font-semibold text-ink">May train on your content.</strong>{' '}
                      {row.trainingWarning}
                    </p>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h2 className={h2}>7. Google user data</h2>
      <p className={p}>
        You can sign in to {siteConfig.name} with Google, and you can link a Google Drive folder to a
        project. This section explains the Google user data involved in both, and what we do with it.
      </p>
      <h3 className="mt-8 font-display text-base font-bold text-ink">7.1 Sign in with Google</h3>
      <p className={p}>
        If you sign in with Google we receive your email address, your name and your profile picture,
        and we use them only to create your account and authenticate you. We store your email address
        and name with your account; we do not receive your Google password.
      </p>
      <h3 className="mt-8 font-display text-base font-bold text-ink">7.2 Google Drive sync</h3>
      {/* `[overflow-wrap:anywhere]` so the 47-character Drive scope URL can break; without it the
          unbreakable token overflows the page at phone width (WCAG 1.4.10 Reflow). */}
      <p className={`${p} [overflow-wrap:anywhere]`}>
        When you link a Google Drive folder to a project, we ask Google for read-only access to your
        Drive ({GOOGLE_DRIVE_READONLY_SCOPE}), and only to the files and folders you choose to link.
        We read those files so we can copy them into that {siteConfig.name} project: the sync is a
        one-way copy, and the files in your Drive are never modified or deleted. Your Drive content
        is stored with the rest of your project content (Section 6).
      </p>
      <h3 className="mt-8 font-display text-base font-bold text-ink">7.3 Storage, sharing and retention</h3>
      <p className={p}>
        The access token we use for Drive is encrypted at rest. We do not sell your Google user data
        and we do not share it beyond the sub-processors named in Section 6, which process it only to
        provide the Service to you. A linked folder&apos;s content stays in your project until you
        disconnect the link or delete your account; deleting your account deletes it with the rest of
        your content (Section 10).
      </p>
      <h3 className="mt-8 font-display text-base font-bold text-ink">7.4 How to revoke access</h3>
      <p className={p}>
        You can disconnect a linked Google Drive folder at any time from the project&apos;s settings,
        which stops the sync and removes the stored token. You can also revoke {siteConfig.name}&apos;s
        access to your Google account at any time from{' '}
        <a
          href="https://myaccount.google.com/permissions"
          className="text-accent-ink underline underline-offset-2 hover:text-accent"
        >
          your Google account permissions page
        </a>
        .
      </p>
      <p className={p}>
        {GOOGLE_LIMITED_USE_STATEMENT}{' '}
        <a
          href={GOOGLE_API_SERVICES_USER_DATA_POLICY_URL}
          className="text-accent-ink underline underline-offset-2 hover:text-accent"
        >
          Read the Google API Services User Data Policy
        </a>
        .
      </p>
      <p className={p}>{GOOGLE_NO_MODEL_TRAINING_STATEMENT}</p>

      <h2 className={h2}>8. The desktop runner</h2>
      <p className={p}>
        If you install and pair the {siteConfig.name} desktop runner, we collect a device
        identifier, the runner&apos;s operating system, and its hardware and software capabilities,
        so that the Service can send it work it can perform. The runner does not access files on
        your computer other than the files it is sent for a job. You can unpair a device at any
        time.
      </p>

      <h2 className={h2}>9. International transfers</h2>
      <p className={p}>
        Some of the processors listed in Section 6 may process data outside of your own country or
        region. Where that happens, we rely on the following transfer mechanism to ensure your data
        continues to receive an adequate level of protection: {TRANSFER_MECHANISM}. This includes{' '}
        {IP_REPUTATION_PROVIDER}, which receives your sign-up IP address for the first-video abuse
        check described in Section 3, and the data Google Analytics receives from the website and
        from the app.
      </p>

      <h2 className={h2}>10. Retention</h2>
      <p className={p}>
        We keep contact-form submissions for {CONTACT_RETENTION_PERIOD}, after which they are
        deleted or anonymized unless a longer period is required to resolve an ongoing enquiry or
        comply with a legal obligation. Server access logs are retained for {LOG_RETENTION_PERIOD}
        for security and troubleshooting purposes, after which they are automatically purged.
        Analytics data is retained according to Google Analytics&apos; own configured retention
        settings for our property. Visit and sign-up records, including the page and button records
        and the first touch stored against a visitor ID, are deleted after{' '}
        {VISIT_RECORD_RETENTION_PERIOD}. The first-touch source we store against your account,
        whether it came from the cookie or from the campaign labels and landing path in your sign-up link, is
        kept for as long as the account exists and is deleted with it; after that, any visit records
        that carried your visitor ID can no longer be linked to you and are deleted within{' '}
        {VISIT_RECORD_RETENTION_PERIOD} of the visit. Short-link click counts hold no personal data
        and are kept as statistics, with no fixed end date.
      </p>
      <p className={p}>
        Your welcome answers, and the audience hint from your sign-up link, are kept for as long as
        your account exists and are deleted with it. First-step records are kept for{' '}
        {TELEMETRY_RETENTION_PERIOD} and then deleted.
      </p>
      <p className={p}>
        Account content — your uploaded files and the videos the Service generates from them — is
        retained for {CONTENT_RETENTION_PERIOD}, after which the account and its content are
        deleted. You may ask us to remove your content sooner under Section 11.
      </p>
      <p className={p}>
        For marketing email, a sign-up you never confirm is deleted after{' '}
        {PENDING_SUBSCRIPTION_PERIOD}. While you are opted in, we keep the record of your consent,
        and for {MARKETING_CONSENT_RETENTION_PERIOD} after you withdraw it, so we can show what you
        agreed to and when. After that, we keep only a suppression entry: a keyed
        hash of your email address and the date you unsubscribed, which lets us make sure we never
        email you again without storing the address itself. If you delete your account, we delete
        your marketing records and keep only that suppression entry; if you never opted in, there
        is nothing to keep. If you later opt in again and confirm, the suppression entry is
        removed.
      </p>
      <p className={p}>
        An avatar and its files are kept until the date you choose when you create it (between 1 day
        and 2 years; one year unless you change it), and are then deleted automatically. If you
        revoke the consent, the avatar can no longer be used from that moment and is deleted 30 days
        later; you can delete it sooner yourself. Deleting an avatar deletes its reference files and
        any release you uploaded. The log of when the avatar was created, revoked and deleted, which
        holds no pictures, recordings or name, is kept for 3 years and then deleted. Videos you made
        with the avatar are your files and follow the retention of your other content.
      </p>
      <p className={p}>
        Routine moderation log entries and handled reports for community recipes are deleted 12
        months after they were written or handled. Open reports are kept until they are handled.
        Log entries, and their related trail, tied to a safety, illegal-content, copyright or
        rejected-by-moderation finding are kept longer, as needed for safety and legal duties. A
        dismissed report is cut to a minimal record (the recipe and the account only, no reason or
        note) after 12 months, until the account is deleted, so repeated dismissed reports do not
        count toward hiding a recipe. If you delete your account, we delete your recipes, ratings
        and reports. The moderation log stays, without your identity. Copies other people made of your recipe stay with them.
      </p>
      <p className={p}>{DISCLOSED_DATA_RETENTION_STATEMENT}</p>

      <h2 className={h2}>11. Your rights</h2>
      <p className={p}>
        Depending on where you live, you may have some or all of the following rights over your
        personal data:
      </p>
      <ul className={ul}>
        <li>The right to access the personal data we hold about you</li>
        <li>The right to have inaccurate personal data corrected (rectification)</li>
        <li>The right to have your personal data deleted in certain circumstances (erasure)</li>
        <li>The right to restrict how we process your personal data in certain circumstances</li>
        <li>The right to receive your data in a portable format (data portability)</li>
        <li>The right to object to processing based on legitimate interest</li>
        <li>
          The right to withdraw consent at any time, for processing based on consent (such as
          analytics and marketing email)
        </li>
        <li>
          The right to lodge a complaint with a supervisory authority, in particular{' '}
          {SUPERVISORY_AUTHORITY}
        </li>
      </ul>
      <p className={p}>
        To exercise any of these rights, contact us using the details in Section 14. We will
        respond in accordance with applicable law.
      </p>
      <p className={p}>
        <strong>If you are shown in an avatar.</strong> You may not have an account with us. You can
        withdraw your consent through the account holder who made the avatar, or by writing to{' '}
        <a href={`mailto:${PRIVACY_EMAIL}`} className="text-accent-ink hover:text-accent">
          {PRIVACY_EMAIL}
        </a>
        . When you write to us, we make the avatar unusable while we look into it, tell the account
        holder, and delete it if consent cannot be confirmed. Copies a provider already received are
        covered by that provider&apos;s own terms (see Section 6.1).
      </p>
      <p className={p}>
        <strong>Your right to object to marketing.</strong> You can object at any time, free of
        charge and without giving a reason, to our use of your personal data for direct marketing.
        Once you do, we stop using it for that purpose. To unsubscribe, click the unsubscribe link
        in any marketing email (it works in one click, with no need to sign in), turn marketing
        email off in your account Settings, or write to{' '}
        <a href={`mailto:${PRIVACY_EMAIL}`} className="text-accent-ink hover:text-accent">
          {PRIVACY_EMAIL}
        </a>
        . Unsubscribing takes effect immediately and does not stop the emails you need to use the
        Service, such as receipts and security notices.
      </p>

      <h2 className={h2}>12. Children</h2>
      <p className={p}>
        The website and the Service are directed at businesses and professionals evaluating or
        using {siteConfig.name}. They are not directed at, and we do not knowingly collect personal
        data from, children. If you believe a child has provided us with personal data, please
        contact us so we can delete it. Avatars and cloned voices of anyone under 18 are not
        allowed, and the Service asks you to confirm the person&apos;s age before an avatar is
        created. We do not check age ourselves.
      </p>

      <h2 className={h2}>13. Changes to this policy</h2>
      <p className={p}>
        We may update this policy from time to time to reflect changes in our practices or for
        legal, operational, or regulatory reasons. We will update the &quot;Last updated&quot; date
        at the top of this page when we do. When a change is material, we will also tell you about
        it in the Service or by email.
      </p>

      <h2 className={h2}>14. How to contact us</h2>
      <p className={p}>If you have questions about this policy or how we handle your data, contact us at:</p>
      <p className={p}>{OPERATOR_STATEMENT}</p>
      <p className={p}>
        Privacy enquiries:{' '}
        <a href={`mailto:${PRIVACY_EMAIL}`} className="text-accent-ink hover:text-accent">{PRIVACY_EMAIL}</a>
      </p>
      <p className={p}>{DPO_STATEMENT}</p>
    </>
  );
}
