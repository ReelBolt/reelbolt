import type { JSX } from 'react';
import Link from 'next/link';
import { Cite } from '@/components/blog/Cite';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { Figure } from '@/components/blog/Figure';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// Refreshed 2026-11-11 for the broader frame: the reader has raw footage and a story, and is not
// assumed to have a software product. Every statement about Runway comes from Runway's own public
// pages, read on 2026-11-11 and listed in the sources. The help.runwayml.com articles were read
// through the help center's own public article feed, because the rendered pages did not load for
// our reader that day. Nothing was tested hands-on, and no Runway price appears anywhere.
// ReelBolt facts follow docs/user-guide (recipes/choose-a-recipe,
// derush-edit, subtitles, voiceover, generated-b-roll, ai-clip-from-idea; projects/avatars;
// design-system; editor/enhance-a-clip, blur-or-mask-an-object, export-your-film,
// share-for-review; account/plans-and-credits, reading-the-estimate). Every ReelBolt figure comes
// from the plan catalog or from the constants the Terms and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-runway',
  title: 'ReelBolt vs Runway: edit your footage or generate it?',
  description:
    'Runway generates and reshapes video with AI models. ReelBolt cuts the clips you already shot into a short finished video. See where each is the better choice.',
  date: '2026-10-17',
  updated: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 12,
  unlisted: false,
  faqs: [
    {
      q: 'Do I need a software product to use ReelBolt?',
      a: 'No. ReelBolt works from footage you filmed: a shop, an event, a lesson, a cause, a product. You upload the clips and write a sentence or two about what the video is for. Software teams are one audience among several.',
    },
    {
      q: 'Can ReelBolt generate a video from a prompt like Runway?',
      a: 'Only in a small way, and not on the free trial. On the Pro plan and the plans above it, ReelBolt can generate a clip of 6 or 10 seconds from a description, and extra shots for an edit of your own footage, under a spending cap. It is not built to make a whole video from a prompt. Runway is designed for that.',
    },
    {
      q: 'Can Runway edit footage I already shot?',
      a: 'In part, going by its own pages. Runway’s help center describes a timeline where you split, trim and reorder clips, and its product pages describe edits that change what is in a clip, for clips up to 30 seconds. The same help center lists its standalone Remove silence, Subtitles and Transcript tools as deprecated, with no alternative named. Runway’s Agent accepts footage, and we have not tested what it does with a set of raw clips.',
    },
    {
      q: 'Does ReelBolt have characters or avatars?',
      a: 'ReelBolt has no invented characters. It has avatars of real adults, made from each person’s own pictures, short videos and voice recordings. They appear in clips of about 15 seconds at most, are part of the Pro plan and the plans above it, and are still being rolled out. Runway offers Characters, Add Performance and Add Dialogue for character work.',
    },
    {
      q: 'Will generated shots run up my bill in ReelBolt?',
      a: 'Generated shots are bought under a spending cap, and the estimate shows the most they can cost before the run starts. If the plan would go over a cap, nothing is bought. Runway’s help center says its Agent can also show an estimated credit cost and wait for your approval, in a mode you switch on.',
    },
    {
      q: 'Can I connect both tools to Claude or another AI assistant?',
      a: 'Yes. Runway’s MCP page names ChatGPT, Claude.ai, Claude Desktop, Grok Bot, Muse, Cursor and Replit, and says it uses your Runway credits. Runway also has a separate developer plugin for Claude Code, Codex and Cursor, which its docs say does not generate media. ReelBolt has a connection for Claude Code and other AI assistants, which works with the same permissions as the assistant in the app.',
    },
    {
      q: 'Why are there no Runway prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and a figure copied here would go stale. Runway’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsRunway(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who has Runway and
        ReelBolt on the shortlist: a creator, a small business, a teacher, a nonprofit, an agency or
        a software team. We make ReelBolt, so read it with that in mind. Every statement about Runway comes from
        Runway’s own public pages, read on {CHECKED} and linked in the sources at the end. We read
        the help center articles through the help center’s own article feed, not as rendered pages.
        We did not test Runway hands-on, and we give no Runway prices.
      </p>
      <p>
        The short version: Runway makes and reshapes footage with AI models. You describe a scene,
        or hand it a clip to change. ReelBolt takes the clips you already filmed and gives back a
        short finished cut that has already been reviewed. If the video you need has not been
        filmed, or a clip has to become something it is not, Runway is the better choice. If the
        footage is sitting in your camera roll and nobody has had time to edit it, that is the job
        ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        Runway describes its creative product as “an all-in-one cloud-based creative platform that
        offers endless ways to generate and edit video, images and audio in one workspace”, built
        for individuals and teams of all sizes <Cite n={[1]} />. It offers its own video models next
        to models from other labs <Cite n={[1, 2, 3]} />. You can work tool by tool, chain steps in
        node-based workflows, or use Runway Agent, which creates “end-to-end videos with simple
        conversations” <Cite n={[2]} />. Its home page speaks to brands, companies, agencies and
        studios <Cite n={[1]} />, and there are apps for iPhone and Android <Cite n={[1]} />.
      </p>
      <p>
        ReelBolt is an AI video team for anyone with a story to tell and no time to tell it. It is a
        public alpha, so expect some rough edges. You upload the clips you already shot, between 1
        and 20 for a highlight edit, and write a sentence or two about what the video is for. An AI
        editor chooses what to keep and leaves out dead air, false starts and retakes. A reviewer
        scores the draft, and a low score sends it back for another pass before you see it. The
        result is a short video, about 90 seconds or less, in the shape you chose. A run takes
        minutes for a small job and sometimes half an hour for a big one. After that you can ask for
        changes, or open the cut in an editor that is labeled Early preview.
      </p>
      <p>
        Raw clips are the usual way in, not the only one. Other recipes start from a finished video
        that needs subtitles, from a script to be read aloud, or, for software teams, from an app’s
        source code.
      </p>

      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={`ReelBolt and Runway compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'Runway']}
        rows={[
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>Text, images, video or audio for its generative tools; an Agent you brief in conversation, with images, footage or documents attached <Cite n={[2, 6, 21]} /></> },
            ],
          },
          {
            label: 'How a video gets made',
            cells: [
              'An AI editor cuts your footage, then a reviewer scores the draft before you see it',
              { text: <>You generate with a model, build a node-based workflow, or ask Runway Agent; Agent generates automatically unless you tell it to ask first <Cite n={[2, 20]} /></> },
            ],
          },
          {
            label: 'Generated video',
            cells: [
              'On Pro and up only: extra shots for an edit, or a clip of 6 or 10 seconds from a description, under a spending cap',
              { text: <>The core of the product: its own video models and models from other labs <Cite n={[1, 2, 3]} /></> },
            ],
          },
          {
            label: 'Timeline editing',
            cells: [
              'A multi-track editor, labeled Early preview, with transcript editing and silence removal',
              { text: <>A built-in timeline to cut, reorder and add assets; Studio splits, trims and detects shots; images are not supported in the timeline <Cite n={[18]} /></> },
            ],
          },
          {
            label: 'Silence removal, transcript and subtitles',
            cells: [
              'Dead air, false starts and retakes left out of the first cut; burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>The help center lists the standalone Remove silence, Subtitles and Transcript tools as deprecated, with no alternative named <Cite n={[19]} /></> },
            ],
          },
          {
            label: 'Changing what is in a clip',
            cells: [
              'In the editor, on Creator and up: blur, pixelate or highlight an object you name; nothing repaints a scene',
              { text: <>Edit one frame and apply the change to the rest of the video, for clips up to 30 seconds at 1080p <Cite n={[2, 7]} /></> },
            ],
          },
          {
            label: 'Upscaling',
            cells: [
              'In the editor, one clip at a time: a sharper picture up to the size your plan allows; the AI version is not part of the free trial',
              { text: <>Upscale Video; 4K upscaling from Standard, unlimited on Pro and Max; uploaded videos up to 40 seconds <Cite n={[2, 3, 28]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>Agent sizes a creative for 9:16, 16:9 and 1:1; Expand Ratio fills a new frame for clips of 2 to 30 seconds <Cite n={[6, 27]} /></> },
            ],
          },
          {
            label: 'Voice and dubbing',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker; dubs change the sound only',
              { text: <>Music, voice and sound effects generation from Standard; custom voices from Pro; a Voice Dubbing node covering 29 languages <Cite n={[3, 23, 24, 30]} /></> },
            ],
          },
          {
            label: 'Avatars and characters',
            cells: [
              'A real adult, from their own pictures and recordings, in clips of about 15 seconds at most; Pro and up, still rolling out; no invented characters',
              { text: <>Characters, real-time conversational video agents made from a single image; Add Performance; Add Dialogue, with automatic lip sync <Cite n={[2, 8, 9]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>Brand Kits: one on Pro, up to three on Max, up to 100 on Team <Cite n={[3, 10]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>Frame-accurate comments on Team and Enterprise; Team is for two to nine people with one pooled credit balance <Cite n={[10, 26]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'A video file at a resolution set by plan, with no HDR or ProRes output; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>No watermark from Standard; HDR, ProRes and image sequences on Max; plugins for Premiere Pro, After Effects and DaVinci Resolve <Cite n={[3, 11, 29]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits; no phone app',
              { text: <>Cloud-based, with apps for iPhone and Android; the iOS app does not support iPad <Cite n={[1, 31]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>A developer API with its own credits; Runway MCP for ChatGPT, Claude.ai, Claude Desktop, Cursor and other apps, using your Runway credits <Cite n={[4, 5, 32]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A free plan with one-time credits; every video carries a watermark, and more credits cannot be bought on it <Cite n={[3, 25]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-runway-is-stronger">Where Runway is the better choice</h2>
      <p>
        <strong>Video nobody filmed.</strong> Generated footage is the center of Runway. It offers
        its own video models next to models from other labs, and its Standard plan lists access to
        all of its AI video and image models <Cite n={[1, 2, 3]} />. Its product page lists
        generating video from text, an image, a video or audio <Cite n={[2]} />. ReelBolt generates
        video only on the Pro plan and the plans above it, and only in small pieces, such as extra
        shots that fill a gap in an edit or one clip of 6 or 10 seconds from a description. We make no
        claim that those shots match Runway’s. On the free trial, ReelBolt has no recipe that turns
        only a prompt or only photos into a video.
      </p>
      <p>
        <strong>Changing what is inside a clip.</strong> Runway’s product page describes Aleph 2.0
        and Edit Studio this way: “Edit a frame in a video, apply it to the rest of the video”{' '}
        <Cite n={[2]} />. Its Aleph page says you can edit clips up to 30 seconds long at 1080p{' '}
        <Cite n={[7]} />. Expand Ratio fills in a new frame around a clip of 2 to 30 seconds{' '}
        <Cite n={[27]} />, and Upscale Video raises the resolution of a video, including uploaded
        videos up to 40 seconds <Cite n={[2, 28]} />. ReelBolt’s editor can{' '}
        <Link href="/blog/enhance-a-clip">sharpen one clip or extend its frame</Link>, and on the
        Creator plan and up it can blur, pixelate or highlight an object you name. Nothing in
        ReelBolt repaints a scene from one edited frame.
      </p>
      <p>
        <strong>Characters and performances.</strong> Runway’s Characters are “real-time
        conversational video agents, made from a single image” <Cite n={[8]} />. Add Performance
        maps your voice and expressions onto an image or a character <Cite n={[2]} />, and Add
        Dialogue makes a character speak the words you type, with a voice and automatic lip sync{' '}
        <Cite n={[2, 9]} />. ReelBolt has no invented characters and nothing that holds a live
        conversation. Its <Link href="/blog/reelbolt-avatars">avatars</Link> are narrower: a
        likeness of a real adult, made from that person’s own pictures and recordings, in clips of
        about 15 seconds at most.
      </p>
      <p>
        <strong>From an idea to a video in one conversation.</strong> Runway’s Agent page says:
        “Describe what you need and attach any images, footage or past campaign data you have”{' '}
        <Cite n={[6]} />. The help center says Agent also accepts documents such as PDF, CSV, text
        and Markdown files, and fonts <Cite n={[21]} />, and that on the Team plan it can connect to
        Dropbox, Figma or Notion to reference briefs, designs and files <Cite n={[22]} />. ReelBolt
        is a narrower thing. You pick a recipe, give it your clips and a short brief, and press
        Run.
      </p>
      <p>
        <strong>Ads, and versions for other markets.</strong> Runway’s changelog says Runway Ads is
        on every paid plan: you connect Meta, Google and TikTok, generate variants of your top ads
        and publish the ones your team approves <Cite n={[16]} />. Its Agent page says Agent
        translates a top-performing ad for every market you sell in <Cite n={[6]} />, and the help
        center lists a Voice Dubbing node that covers 29 languages <Cite n={[30]} />. ReelBolt does
        not connect to ad accounts and does not post a video for you. It can add subtitles in
        another language and dub a video, and a dub changes the sound only.
      </p>
      <p>
        <strong>Finishing in a professional editor.</strong> Runway has plugins that generate, edit
        and extend footage inside Premiere Pro, After Effects and DaVinci Resolve. The installer is
        free and generations use your Runway credits <Cite n={[11]} />. Its Max plan lists HDR,
        ProRes and image sequences <Cite n={[3, 29]} />. ReelBolt exports a standard video file, with
        no HDR, ProRes or image-sequence output. It can also export OTIO, EDL and Final Cut XML and
        prepare a hand-off package with the cut and your video files. That package is an Early
        preview, and we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut Pro.
      </p>
      <p>
        <strong>A developer platform, and company requirements.</strong> Runway sells API credits in
        its developer portal and publishes a table of credits per second for each model{' '}
        <Cite n={[5]} />. API credits are separate from the credits and plans bought in the web app{' '}
        <Cite n={[32]} />. There is also a developer plugin for Claude Code, Codex and Cursor, which
        Runway’s docs say does not generate media <Cite n={[17, 35]} />. Runway states that it holds
        SOC 2 Type II and ISO/IEC 27001 certifications <Cite n={[15]} /> and that it implements C2PA
        so AI-generated content can be traced to its source <Cite n={[14]} />. ReelBolt has{' '}
        <Link href="/blog/reelbolt-public-api">an API with webhooks</Link> on its paid plans. It
        starts ReelBolt’s own recipes and does not call a video model directly. ReelBolt does not
        sign its videos with C2PA, and the whole product is a public alpha. If your company has
        security or compliance requirements today, Runway is the safer choice.
      </p>
      <p>
        <strong>A phone app.</strong> Runway has apps for iPhone and Android <Cite n={[1]} />,
        though its help center says the iOS app does not support iPad <Cite n={[31]} />. ReelBolt has no app to install. It runs in a browser, and its editor has
        a phone view for simpler edits.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It starts from a camera roll and makes the first cut.</strong> Give ReelBolt the
        clips from your phone: the shop, the event, the lesson, the product. An AI editor decides
        what to keep, a reviewer scores the result, and a low score sends the edit back for another
        pass before you see it. You do not open a timeline or write a prompt for each shot. Several
        different shots give the editor something to cut together. One long take can only be
        trimmed.{' '}
        <Link href="/blog/what-a-highlight-edit-removes">What a highlight edit removes</Link> shows
        the kind of thing that gets cut.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one audio clip on another and a Remove silence button above them."
        width={1280}
        height={214}
        caption="Look at the clips on the timeline, where you can change the cut afterwards."
      />
      <p>
        <strong>Cleaning up raw footage is the main job.</strong> Runway’s help center lists its
        standalone Remove silence, Subtitles and Transcript tools as deprecated, each with no
        alternative named <Cite n={[19]} />. Runway does have a timeline: its help center describes
        a built-in timeline editor to cut, reorder and add assets, and a Studio where you split,
        trim and move clips and where Detect Shots splits a clip at each scene change it finds{' '}
        <Cite n={[18]} />. Runway’s Agent accepts footage too <Cite n={[6]} />, and we have not
        tested what it does with a set of raw clips, so we do not say what Runway cannot do. What we
        can say is that removing dead air, cutting on sentences and adding subtitles are the
        center of ReelBolt.
      </p>
      <p>
        <strong>What you filmed stays what you filmed.</strong> A highlight edit only chooses what
        to keep. It uses your own picture and your own sound, with no generated shots and no new
        voice. That matters when the video is a customer speaking or the people your organization
        helps.
      </p>
      <p>
        <strong>The same clips can go further.</strong> Other recipes add titles and callouts, write
        and voice a narration with captions, burn in subtitles and produce the SRT and VTT files,
        translate the subtitles, or dub the video. The run page has no download button for the
        subtitle files yet, which is one of the rough edges. One makes a cover picture for very few credits, and the assistant can write the text that goes with the post. You choose the shape
        before each run: landscape, vertical, square or portrait. A finished video cannot be re-cut
        to another shape yet, so a second shape is another run. Music and sound effects come from
        files you upload, because there is no built-in music library. On the Creator plan and up you
        can also generate a music track or a sound effect from a description.
      </p>
      <p>
        <strong>Generated shots are the exception, and they have a ceiling.</strong> On Pro and up,
        one recipe adds AI-generated shots to an edit of your footage: a cold open, cutaways or an
        end card. The shots are planned first and bought under a spending cap. If the plan would
        go over a cap, nothing is bought. The estimate shows the most the generated video can cost
        before the run starts, and a clip that was already made in the project with the same
        description and settings is reused at no charge. Runway’s help center says Agent has an “Ask before
        generating media” mode that shows the model, the prompt and an estimated credit cost for
        each step and waits for your go-ahead, and that the default mode generates automatically{' '}
        <Cite n={[20]} />.
      </p>
      <p>
        <strong>A real person’s voice or likeness needs recorded consent.</strong> In ReelBolt a
        cloned voice and an avatar each need the person’s agreement, which you record in the
        project, and you can withdraw it or delete the voice or avatar at any time. The person
        behind an avatar must be an adult, and every avatar clip is marked as AI-generated. Runway
        has its own consent steps for custom voices and its own rules for a person’s likeness,
        described in its help center, its usage policy and its post about Characters{' '}
        <Cite n={[12, 13, 23, 24]} />. We do not compare the two processes here.
      </p>
      <p>
        <strong>Review by someone with no account.</strong> In ReelBolt’s editor you can send one
        finished film to a client or a colleague as a review link. They watch it without an
        account, leave notes at exact moments, and approve it or ask for changes. They cannot
        download the film or see the rest of your project. At Runway, commenting on a generation at
        an exact frame is part of the Team and Enterprise plans, and Team is for groups of two to
        nine people <Cite n={[10, 26]} />. Runway’s help center says a shared asset link lets the
        recipient view and download the asset <Cite n={[34]} />.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> ReelBolt builds a brand kit
        from a brand PDF or a design-tool export, and it also reads design token and CSS files if
        your team keeps them. Reading your files is free. AI fills the gaps only after you confirm
        an estimate. Runway lists one Brand Kit on Pro, up to three on Max and up to 100 on Team{' '}
        <Cite n={[3, 10]} />, and its help center says Runway Ads can build a Brand Kit from your
        website, a PDF or a Figma file <Cite n={[33]} />.
      </p>

      <h2 id="pricing-approach">Pricing approach</h2>
      <p>
        Runway has a free plan, described as free forever, with a one-time credit allowance and a
        selection of models to test ideas <Cite n={[3]} />. Every video made on it carries a Runway
        watermark, and credits cannot be bought on it <Cite n={[25]} />. The individual paid plans
        are Standard, Pro and Max, each with a monthly credit allowance, billed monthly or yearly{' '}
        <Cite n={[3]} />. Standard removes watermarks and adds 4K upscaling and music, voice and
        sound effects generation. Pro lists unlimited 4K upscaling, one custom voice clone, one
        Brand Kit and Runway MCP. Max lists HDR, ProRes and image sequences <Cite n={[3]} />. The
        Team plan is for two to nine people and includes everything in Max plus shared projects,
        one pooled credit balance and frame-accurate commenting <Cite n={[10]} />. Monthly credits
        on Standard and Pro do not roll over, on Max up to one month of unused credits rolls over,
        and additional credits you buy do not expire <Cite n={[3]} />. API usage is billed
        separately, with credits bought in the developer portal <Cite n={[5, 32]} />. Current
        figures are on{' '}
        <a href="https://runway.com/pricing" rel="nofollow noopener" target="_blank">
          Runway’s pricing page<span className="sr-only"> (opens in a new tab)</span>
        </a>
        .
      </p>
      <p>
        ReelBolt plans are credit based, and no card is required to start. A trial workspace has{' '}
        {TRIAL_CREDITS} one-time credits. When you verify your email, the account also receives a
        one-time grant of {FIRST_VIDEO_GRANT_CREDITS} credits, which expires{' '}
        {FIRST_VIDEO_GRANT_EXPIRY} after it is granted. Trial exports are {TRIAL_RESOLUTION} with a
        watermark. The paid plans are Creator, Pro, Team and Enterprise. Creator exports up to{' '}
        {CREATOR_RESOLUTION} with no watermark and Pro up to {PRO_RESOLUTION}. AI-generated clips
        and avatars are part of Pro and the plans above it. The{' '}
        <Link href="/pricing">pricing page</Link> has the current prices and what each plan
        includes.
      </p>
      <p>
        Before a run starts, ReelBolt shows the credits it is expected to use, as a likely amount
        and the range it usually falls in. Generated video is shown separately, as the most it can
        cost. If a step fails because of a problem on our side, the credits held for it are
        refunded. Top-up packs are for paid plans and expire after{' '}
        {catalog('topUp.expiresAfterMonths')} months.
      </p>
      <Figure
        name="run-estimate"
        alt="The run estimate in ReelBolt, showing the estimated credits for a run, a rough-estimate note and the credits the workspace has available."
        width={1240}
        height={218}
        caption="Look at the credit estimate, which appears before the run starts. The figures are from a demo workspace; yours depend on the recipe and your footage."
      />
      <p>
        A credit buys something different at each company, so comparing one tool’s credit with
        another’s will mislead you. Compare the cost of the video you actually need.
      </p>

      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <p>Pick Runway if any of these is true:</p>
      <ul>
        <li>The video is mostly footage nobody filmed, or a character speaking your lines.</li>
        <li>You need to change what is inside a short clip, or expand or upscale it.</li>
        <li>You want to brief an agent in conversation, starting from an idea or a document.</li>
        <li>You make ad variants and want to publish them from the same place.</li>
        <li>
          A professional editor will finish the work in Premiere Pro, After Effects or DaVinci
          Resolve, or the delivery spec asks for HDR, ProRes or image sequences.
        </li>
        <li>
          You want to call video models from your own code, or your company needs published
          security certifications.
        </li>
        <li>You want an app on your phone.</li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You want a short video, about 90 seconds or less, made from what you really filmed.</li>
        <li>You would rather react to a first cut than write prompts or build a timeline.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        Someone who needs a shot that cannot be filmed is better served by Runway. A shop owner or
        a volunteer with a camera roll of clips and no editor is who ReelBolt is built for. Some
        people will need both kinds of video, and nothing stops you from using both tools.
      </p>
      <p>
        If ReelBolt sounds like the fit,{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks
        the first run screen by screen, and{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> lists everything ReelBolt can
        make today and what it cannot. For the wider field,{' '}
        <Link href="/blog/ai-video-editors-for-people-who-do-not-edit-2026">
          AI video tools for people who do not edit
        </Link>{' '}
        sorts the tools by kind.
      </p>

      <SourceList
        sources={[
          { id: 'source-1', label: 'Runway, home page', url: 'https://runway.com/', accessed: CHECKED },
          { id: 'source-2', label: 'Runway, product', url: 'https://runway.com/product', accessed: CHECKED },
          { id: 'source-3', label: 'Runway, pricing', url: 'https://runway.com/pricing', accessed: CHECKED },
          { id: 'source-4', label: 'Runway, MCP', url: 'https://runway.com/mcp', accessed: CHECKED },
          { id: 'source-5', label: 'Runway, API pricing documentation', url: 'https://docs.dev.runwayml.com/guides/pricing/', accessed: CHECKED },
          { id: 'source-6', label: 'Runway, Agent', url: 'https://runway.com/product/agent', accessed: CHECKED },
          { id: 'source-7', label: 'Runway, Aleph 2.0', url: 'https://runway.com/product/aleph-2', accessed: CHECKED },
          { id: 'source-8', label: 'Runway, Characters', url: 'https://runway.com/product/characters', accessed: CHECKED },
          { id: 'source-9', label: 'Runway, Add Dialogue', url: 'https://runway.com/apps/add-dialogue', accessed: CHECKED },
          { id: 'source-10', label: 'Runway, Team plan', url: 'https://runway.com/team-plan', accessed: CHECKED },
          { id: 'source-11', label: 'Runway, plugins', url: 'https://runway.com/plugins', accessed: CHECKED },
          { id: 'source-12', label: 'Runway, usage policy', url: 'https://runway.com/safety/usage-policy', accessed: CHECKED },
          { id: 'source-13', label: 'Runway, Building interactive AI characters responsibly', url: 'https://runway.com/news/building-interactive-ai-characters-responsibly', accessed: CHECKED },
          { id: 'source-14', label: 'Runway, safety', url: 'https://runway.com/safety', accessed: CHECKED },
          { id: 'source-15', label: 'Runway, data security', url: 'https://runway.com/data-security', accessed: CHECKED },
          { id: 'source-16', label: 'Runway, changelog', url: 'https://runway.com/changelog', accessed: CHECKED },
          { id: 'source-17', label: 'Runway developer docs, MCP guide', url: 'https://docs.dev.runwayml.com/guides/mcp/', accessed: CHECKED },
          { id: 'source-18', label: 'Runway help, trimming and assembling clips in Studio', url: 'https://help.runwayml.com/hc/en-us/articles/52685547867667-Trimming-and-Assembling-Clips-in-Studio', accessed: CHECKED },
          { id: 'source-19', label: 'Runway help, deprecated standalone tools', url: 'https://help.runwayml.com/hc/en-us/articles/40213860628371-Deprecated-Standalone-Tools', accessed: CHECKED },
          { id: 'source-20', label: 'Runway help, creating with Runway Agent', url: 'https://help.runwayml.com/hc/en-us/articles/51601639579667-Creating-with-Runway-Agent', accessed: CHECKED },
          { id: 'source-21', label: 'Runway help, uploading documents and fonts to Runway Agent', url: 'https://help.runwayml.com/hc/en-us/articles/54355254443923-Uploading-Documents-and-Fonts-to-Runway-Agent', accessed: CHECKED },
          { id: 'source-22', label: 'Runway help, using connectors with Agent', url: 'https://help.runwayml.com/hc/en-us/articles/53908901085459-Using-Connectors-with-Agent', accessed: CHECKED },
          { id: 'source-23', label: 'Runway help, voice verification', url: 'https://help.runwayml.com/hc/en-us/articles/29828787723411-Voice-verification', accessed: CHECKED },
          { id: 'source-24', label: 'Runway help, creating a custom voice', url: 'https://help.runwayml.com/hc/en-us/articles/24388305544083-Creating-a-custom-voice', accessed: CHECKED },
          { id: 'source-25', label: 'Runway help, Free plan details', url: 'https://help.runwayml.com/hc/en-us/articles/50404627334547-Free-plan-details', accessed: CHECKED },
          { id: 'source-26', label: 'Runway help, commenting on assets', url: 'https://help.runwayml.com/hc/en-us/articles/54125589421971-Commenting-on-assets', accessed: CHECKED },
          { id: 'source-27', label: 'Runway help, expanding a video’s aspect ratio with Edit Studio', url: 'https://help.runwayml.com/hc/en-us/articles/52264567609363-Expanding-a-video-s-aspect-ratio-with-Edit-Studio', accessed: CHECKED },
          { id: 'source-28', label: 'Runway help, can I generate videos in 4K', url: 'https://help.runwayml.com/hc/en-us/articles/26115194070675-Can-I-generate-videos-in-4K', accessed: CHECKED },
          { id: 'source-29', label: 'Runway help, exporting videos in ProRes and PNG sequence formats', url: 'https://help.runwayml.com/hc/en-us/articles/54396547993491-Exporting-Videos-in-ProRes-and-PNG-Sequence-Formats', accessed: CHECKED },
          { id: 'source-30', label: 'Runway help, supported languages in generative audio', url: 'https://help.runwayml.com/hc/en-us/articles/33505227741587-Supported-Languages-in-Generative-Audio', accessed: CHECKED },
          { id: 'source-31', label: 'Runway help, mobile device compatibility', url: 'https://help.runwayml.com/hc/en-us/articles/15913840746003-Mobile-device-compatibility', accessed: CHECKED },
          { id: 'source-32', label: 'Runway help, Runway API FAQs', url: 'https://help.runwayml.com/hc/en-us/articles/21668552945171-Runway-API-FAQs', accessed: CHECKED },
          { id: 'source-33', label: 'Runway help, introduction to Runway Ads', url: 'https://help.runwayml.com/hc/en-us/articles/56690810792339-Introduction-to-Runway-Ads', accessed: CHECKED },
          { id: 'source-34', label: 'Runway help, how to share an asset', url: 'https://help.runwayml.com/hc/en-us/articles/25562277393427-How-to-share-an-asset', accessed: CHECKED },
          { id: 'source-35', label: 'Runway developer docs, plugin guide', url: 'https://docs.dev.runwayml.com/guides/plugin/', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on {CHECKED} from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
