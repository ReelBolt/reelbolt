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
// assumed to have a software product. Every statement about Arcade comes from Arcade's own public
// pages and docs, read on 2026-11-11 and listed in the sources. Nothing was tested hands-on, and no
// Arcade price appears anywhere. Since the first version of this post Arcade split into two
// separately billed products (AI Video and Interactive Demo), so the old "starting October 19"
// wording, the old home page quote and three moved source addresses are gone. Left out on purpose:
// Arcade's credit figures (its own pages disagree on some), its model names, voice cloning inside
// AI Video (documented only for its demo product), any statement about data use or AI training,
// and any comparison of consent processes. ReelBolt facts follow docs/user-guide
// (recipes/choose-a-recipe, derush-edit, subtitles, voiceover, tracked-screen-insert,
// promo-from-code; projects/avatars, give-your-code; design-system; editor/index,
// export-your-film, share-for-review; assistant/claude-code, post-text; api/index;
// account/plans-and-credits). Every ReelBolt figure comes from the plan catalog or from the
// constants the Terms and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-arcade',
  title: 'ReelBolt vs Arcade: a video built from a brief, or cut from your footage?',
  description:
    'Arcade builds a designed video from a prompt, your brand and your material. ReelBolt cuts the clips you already shot into a short video. See which fits.',
  date: '2026-10-17',
  updated: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 14,
  unlisted: false,
  faqs: [
    {
      q: 'Do I need a software product to use ReelBolt?',
      a: 'No. ReelBolt works from footage you filmed: a shop, an event, a lesson, a cause, a product. You upload the clips and write a sentence or two about what the video is for. Some of its recipes make a promo video from an app’s source code; the footage recipes described here need no code.',
    },
    {
      q: 'I have an idea and a brand, but no footage. Which one should I use?',
      a: 'Arcade. Its AI Video product starts from a prompt, your brand kit and whatever material you attach, and it builds the scenes, the voiceover and the music. ReelBolt is built around footage you bring. Nothing on its free trial turns a prompt or a set of photos into a video.',
    },
    {
      q: 'Can Arcade work with footage I already shot?',
      a: 'In part, going by its own docs. Arcade accepts video files, and its Chrome extension records your screen, camera and voice. A clip is placed in a scene, where you set its start point, its speed and its sound. On the AI Video pages we read, we did not find a tool that goes through a set of raw clips and cuts out silences and retakes. We have not tested it.',
    },
    {
      q: 'Does ReelBolt make interactive demos like Arcade?',
      a: 'No. ReelBolt makes videos and cover pictures. If you need a demo that people click through, with branching and forms, Arcade’s Interactive Demo product is built for that.',
    },
    {
      q: 'Does either one put an AI presenter on screen?',
      a: 'Not as the main product. Arcade’s own site sets it apart from avatar tools, which it says center on a digital presenter reading a script, and suggests recording yourself on camera with its browser extension. ReelBolt has a narrower kind of avatar: a likeness of a real adult, made from that person’s own pictures and recordings, in short clips of about 15 seconds at most, on the Pro plan and the plans above it, and still being rolled out.',
    },
    {
      q: 'Is Arcade one subscription or two?',
      a: 'Two, unless you are on a negotiated Enterprise contract. Arcade’s docs say AI Video and Interactive Demo share a login but are billed as separate products, and that paying for one does not include the other.',
    },
    {
      q: 'Can I use either tool from Claude Code or another AI assistant?',
      a: 'Yes. Arcade says its connection for AI assistants works on every AI Video plan, including Free, and lists Claude, ChatGPT, Gemini, Cursor and others as clients. ReelBolt connects to Claude Code, which then works on your projects with the same permissions as the assistant in its app. ReelBolt is a public alpha, so expect that connection to change.',
    },
    {
      q: 'Why are there no Arcade prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and a figure copied here would go stale. Arcade’s pricing pages have the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsArcade(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who has Arcade and
        ReelBolt on the shortlist: a creator, a small business, a teacher, a nonprofit, an agency or
        a software team. We make ReelBolt, so read it with that in mind. Every statement about
        Arcade comes from Arcade’s own public pages and docs, read on {CHECKED} and linked in the
        sources at the end. We did not test Arcade hands-on, and we give no Arcade prices.
      </p>
      <p>
        The short version: Arcade builds a designed video from a brief. You describe what you want
        and attach your brand and your material, and it builds the scenes, the voiceover and the
        music. It also makes product demos that people click through, as a separate product on a
        separate bill. ReelBolt works from what you already filmed. You drop in raw clips, say what
        the video is for, and get back a short first cut that has already been reviewed. If you
        have an idea and a brand but little or no footage, or you need a clickable demo, Arcade is
        the better choice. If the footage is sitting on your phone and nobody has had time to edit
        it, that is the job ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        Arcade’s home page calls it “The AI video playground for marketing teams”{' '}
        <Cite n={[1]} />. It is two products behind one login. AI Video makes “on-brand teaser
        videos, sizzle reels, and product overviews from a short description”, and Interactive Demo
        makes product demos a viewer clicks through <Cite n={[2, 11]} />. Arcade’s docs say the
        two are “billed and organized as two separate products” <Cite n={[2]} />. The AI Video docs
        name four jobs it is built around: product marketing, release announcements, sales
        follow-ups and paid ads <Cite n={[23]} />. You refine a video by chatting with its
        assistant or by editing its timeline <Cite n={[5]} />. Arcade runs on the web, with Chrome
        extensions and a desktop app for recording <Cite n={[4, 24]} />. Its home page says it is
        “Trusted by 37,000+ teams” <Cite n={[1]} />, which is Arcade’s own figure and one we have
        not checked.
      </p>
      <p>
        ReelBolt is an AI video team for anyone with a story to tell and no time to tell it. It is a
        public alpha, so expect some rough edges, and we have no customer stories to show yet. You
        upload the clips you already shot, between 1 and 20 for a highlight edit, and write a
        sentence or two about what the video is for. An AI editor chooses what to keep and leaves
        out dead air, false starts and retakes. A reviewer scores the draft, and a low score sends
        it back for another pass before you see it. The result is a short video, about 90 seconds or
        less, in the shape you chose. A run takes minutes for a small job and sometimes half an hour
        for a big one. After that you can ask for changes, or open the cut in an editor that is
        labeled Early preview.
      </p>
      <p>
        Raw clips are the usual way in, not the only one. For software teams there is also a recipe
        that makes a promo video from an app’s source code.
      </p>

      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={`ReelBolt and Arcade compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'Arcade']}
        rows={[
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>A prompt and a brand kit, plus what you attach: Arcade demos, web addresses, images, PDFs, Office documents, text files and videos; also a screen and camera recording from its Chrome extension <Cite n={[2, 3, 4]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor working from your clips, then a reviewer that scores the draft before you see it',
              { text: <>Its AI builds the video from your brief, including scenes, motion, voiceover and music; you change it by chat or on the timeline <Cite n={[5, 8]} /></> },
            ],
          },
          {
            label: 'What it does with your footage',
            cells: [
              'Chooses what to keep across your clips and joins it into one cut',
              { text: <>Places a recording in a scene, where you set its start point, its speed (0.5x to 2x) and its sound <Cite n={[5]} /></> },
            ],
          },
          {
            label: 'Dead air, false starts and retakes',
            cells: [
              'Left out of the first cut; the editor can also remove silence',
              'Not found for AI Video on the pages we read',
            ],
          },
          {
            label: 'Timeline editing',
            cells: [
              'A multi-track timeline, labeled Early preview, with transcript editing, multicam and color grading',
              { text: <>A timeline of scenes with voiceover, music and sound-effect tracks; reorder, trim, add, duplicate and delete scenes; a Design panel for each scene <Cite n={[5]} /></> },
            ],
          },
          {
            label: 'Captions and subtitles',
            cells: [
              'Burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>Captions switched on or off, including for speech in your own recording; no caption file export found on the pages we read <Cite n={[5]} /></> },
            ],
          },
          {
            label: 'Other languages',
            cells: [
              'Translated subtitles; a dub that changes the sound only, with no lip sync',
              { text: <>A voice and language chosen per scene; a variation of a finished video in another language; no lip-synced dubbing found on the pages we read <Cite n={[5, 9]} /></> },
            ],
          },
          {
            label: 'Voice',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker',
              { text: <>Synthetic voiceover only in AI Video, with the script, voice and language set per scene; your own speech stays in a recording you add <Cite n={[5]} /></> },
            ],
          },
          {
            label: 'A person on screen',
            cells: [
              'The people in your footage; an avatar of a real adult, from their own pictures and recordings, in clips of about 15 seconds at most, on Pro and up, still rolling out; no stock avatars',
              { text: <>Sets itself apart from avatar tools on its own site; record yourself on camera with its browser extension and it builds the video around you <Cite n={[8]} /></> },
            ],
          },
          {
            label: 'Generated scenes, video and music',
            cells: [
              'AI-generated clips on Pro and up; music and sound-effect generation on Creator and up; no built-in music library',
              { text: <>Scenes from a curated library or generated with AI, AI b-roll, 3D scenes and a composed instrumental track <Cite n={[5, 6, 9]} /></> },
            ],
          },
          {
            label: 'Interactive demos',
            cells: [
              'No',
              { text: <>Yes, as a separate product: clickable demos, with branching, call-to-action buttons, forms and insights on its paid demo plans <Cite n={[11]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>Brand kit built from your website address: colors, fonts, logos, tone and pronunciation; corrections you make in chat are saved to it <Cite n={[7]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>16:9, 1:1 or 9:16 when you create a video; a variation of a finished video can take a new aspect ratio <Cite n={[3, 9]} /></> },
            ],
          },
          {
            label: 'Length',
            cells: [
              'Built for short videos, about 90 seconds or less',
              { text: <>Up to 30 seconds on Free and 5 minutes on Pro; the length menu currently stops at 2 minutes <Cite n={[3, 10]} /></> },
            ],
          },
          {
            label: 'Recording',
            cells: [
              'No screen or camera recorder; the editor records a voiceover',
              { text: <>A Chrome extension that records your screen, camera and voice; a desktop app for recording demos <Cite n={[4, 24]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>A shared preview link; workspace roles and projects; up to 10 seats on Pro; comments are documented for demos, and we found none for AI Video <Cite n={[10, 12, 13, 27]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>MP4, 1080p at 30fps by default; 1080p with a watermark on Free, 4K with no watermark on Pro and Enterprise; no hand-off to another editor found on the pages we read <Cite n={[12, 17]} /></> },
            ],
          },
          {
            label: 'Posting to social accounts',
            cells: [
              'No: you download the video and post it yourself; the assistant can write the post text',
              { text: <>Its privacy policy describes connecting YouTube, LinkedIn, X, Instagram and TikTok to publish a video, where available <Cite n={[18]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits; no phone app',
              { text: <>On the web, with Chrome extensions and a desktop app for macOS and Windows; no phone app found on the pages we read <Cite n={[4, 24]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>A connection for AI assistants on every AI Video plan, naming Claude, ChatGPT, Gemini, Cursor and others; a REST API on Enterprise that creates interactive demos <Cite n={[14, 15, 16]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A free AI Video plan with one-time credits that do not refresh; 1080p exports with a watermark <Cite n={[10]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-arcade-is-stronger">Where Arcade is the better choice</h2>
      <p>
        <strong>A demo people can click through.</strong> Interactive Demo is Arcade’s second
        product. On its paid demo plans, a demo can have branching paths, call-to-action buttons,
        forms and viewing insights <Cite n={[11]} />. A demo you already made can also be turned
        into a video from its Share menu <Cite n={[3]} />. ReelBolt makes videos and cover
        pictures. It makes nothing a viewer clicks through, and nothing in it replaces a clickable
        demo.
      </p>
      <p>
        <strong>Starting from an idea, with little or no footage.</strong> Arcade’s docs say AI
        Video “turns a prompt, your Arcade demos, and your brand kit into a polished, on-brand
        video” <Cite n={[2]} />. Its enterprise page says it builds the whole video, “including
        scenes, motion, voiceover, and music” <Cite n={[8]} />. The docs also say a first draft is
        usually ready in under a minute <Cite n={[3]} />. That is Arcade’s statement, and we did
        not time it. ReelBolt is built around footage you bring. Nothing on its free trial turns a
        prompt or a set of photos into a video, and on Pro and the plans above it the most it
        generates from a description alone is a clip of 6 or 10 seconds. A ReelBolt run takes
        minutes for a small job and sometimes half an hour for a big one. If you have a message and
        a brand but not the footage, Arcade is the better choice.
      </p>
      <p>
        <strong>A designed, animated look.</strong> An Arcade video is made of scenes. You can
        generate a scene with AI or drop one in from a curated library <Cite n={[6]} />. Its
        October 2026 changelog describes true 3D scenes <Cite n={[9]} />, and the editing docs say
        Arcade composes a custom instrumental track for a video <Cite n={[5]} />. Ready-made
        templates include a product story, a product teaser, a brand film and a launch video{' '}
        <Cite n={[21]} />. A reference video’s “pacing, transitions, and camera work” can be
        carried into a new one <Cite n={[5]} />, and a workspace memory keeps choices such as the
        voice, the music and the pacing for next time <Cite n={[22]} />. Arcade’s home page puts it
        this way: “Unlike generative video tools, the output stays editable and on-brand, while
        still using generative AI where it adds value” <Cite n={[1]} />. ReelBolt can add titles
        and callouts to your footage. It has no scene library for you to pick from and no built-in
        music library.
      </p>
      <p>
        <strong>Recording built in.</strong> Arcade’s Chrome extension records the page you are on,
        takes screenshots, and adds your camera and voice. One take can open on your face and
        switch to your screen <Cite n={[4]} />. The changelog says recordings get a smooth cursor
        and automatic zooms <Cite n={[9]} />. A desktop app for macOS and Windows records demos{' '}
        <Cite n={[24]} />. ReelBolt does not record your screen or camera. You can record a
        voiceover in its editor, and everything else is footage you bring.
      </p>
      <p>
        <strong>Many kinds of source material.</strong> You can attach web addresses, images, PDFs,
        Office documents, text files and videos to a prompt <Cite n={[3]} />. The extension reads
        the page you are on, including a YouTube video, and reads Google Docs and Slides if your
        workspace uses Google Workspace <Cite n={[4]} />. Arcade’s privacy policy also describes
        connecting tools such as Google Drive, Notion, Linear and Figma, where available, so its AI
        can read documents you choose <Cite n={[18]} />. ReelBolt’s recipes start from your video
        clips, or from source code for one of them. A PDF or a web page is not something it turns
        into a video.
      </p>
      <p>
        <strong>A brand kit from your website address.</strong> Enter your company’s web address
        and Arcade builds a kit from the site: colors, fonts, logos, tone and pronunciation. When
        you correct something in chat, the correction is saved to the kit <Cite n={[7]} />.
        ReelBolt builds a brand kit from files you give it and does not read your website. If you
        have a site and no brand files, Arcade’s way is quicker.
      </p>
      <p>
        <strong>Versions of a finished video.</strong> Arcade’s November 2026 changelog describes
        making a variation of a finished video with a new aspect ratio, language or opening hook,
        while the original stays as it is, and adding voices in more languages{' '}
        <Cite n={[9]} />. ReelBolt can add translated subtitles or a dub to a video. It cannot yet
        re-cut a finished video to another shape, so a second shape is another run.
      </p>
      <p>
        <strong>A free plan at full HD, and seats included.</strong> Arcade’s free AI Video plan
        exports at 1080p with a watermark, and its Pro plan includes up to 10 seats at no added
        cost per seat <Cite n={[10]} />. ReelBolt’s trial exports are {TRIAL_RESOLUTION} with a
        watermark, and its Team plan is charged per seat.
      </p>
      <p>
        <strong>Reach into AI assistants, and posting.</strong> Arcade’s connection for AI
        assistants works on every AI Video plan, including Free <Cite n={[14]} />, and its docs
        list Claude, ChatGPT, Gemini, Codex, Cursor and Lovable among the clients{' '}
        <Cite n={[15]} />. Through it an assistant can create a brand kit, add public web pages as
        sources and generate a video <Cite n={[26]} />. A changelog entry dated November 4, 2026
        says you can also keep editing the video in the same conversation <Cite n={[9]} />. The
        documentation page for the connection still listed editing after generation as not
        possible when we read it <Cite n={[14]} />, and we did not test which is current. Arcade’s
        privacy policy also describes connecting YouTube, LinkedIn, X, Instagram and TikTok
        accounts to publish a video, where available <Cite n={[18]} />. ReelBolt connects to Claude
        Code and has{' '}
        <Link href="/blog/reelbolt-public-api">an API with webhooks</Link> on its paid plans, all
        of it alpha, and it does not post a video for you.
      </p>
      <p>
        <strong>Company requirements.</strong> Arcade’s security docs say it is SOC 2 Type II
        compliant <Cite n={[20]} />, and its Enterprise plan lists single sign-on, role-based
        access control, and data residency and retention controls <Cite n={[10]} />. ReelBolt is a
        public alpha. If your company needs a compliance report from its vendors today, Arcade is
        the safer choice.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It starts from a camera roll and makes the first cut.</strong> ReelBolt assumes the
        footage exists and the editing is what is missing. You give it several clips. An AI editor
        decides what to keep, a reviewer scores the result, and a low score sends the edit back for
        another pass before you see it. Arcade takes footage too. It accepts video files and its
        own recordings, and places a clip in a scene, where you set the start point, the speed and
        the sound <Cite n={[3, 5]} />. On the AI Video pages we read, we did not find a tool that
        goes through a set of raw clips and removes silences, false starts or retakes. That is what
        we did not find, not something Arcade says, and we have not tested it. The difference we
        can state is the starting point. Arcade starts from a brief and designs a video around it.
        ReelBolt starts from your clips.
      </p>
      <p>
        <strong>Several clips give it something to cut.</strong> Give it the clips from your phone:
        the shop, the event, the lesson, the product. Several different shots make a real edit. One
        long take can only be trimmed. When the speech in your clips could be read, cuts are placed
        so they do not land inside a spoken word.{' '}
        <Link href="/blog/what-a-highlight-edit-removes">What a highlight edit removes</Link> shows
        the kind of thing that gets cut.
      </p>
      <p>
        <strong>An editor for footage, for afterwards.</strong> You stay in charge of the cut.
        ReelBolt’s editor, labeled Early preview, is a multi-track timeline with transcript
        editing, silence removal, multicam and color grading. Arcade’s timeline, as its docs
        describe it, is a row of scenes with voiceover, music and sound-effect tracks, and a Design
        panel for the selected scene <Cite n={[5]} />. That suits a video made of designed scenes.
        In the AI Video docs we read, we did not find transcript editing, multicam or color
        grading.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one clip on the track below and a Remove silence button above them."
        width={1280}
        height={214}
        caption="Look at the Remove silence button above the clips on the timeline."
      />
      <p>
        <strong>The same clips can go further.</strong> Other recipes add titles and callouts, write
        and voice a narration with captions, burn in subtitles and produce the SRT and VTT files,
        translate the subtitles, or dub the video. The run page has no download button for the
        subtitle files yet, which is one of the rough edges. One makes a cover picture for very few credits, and the assistant can write the text that goes with the post. Another places a screen
        recording, a video or a picture on a green screen in your clip. You choose the shape before
        each run: landscape, vertical, square or portrait. Music and sound effects come from files
        you upload, because there is no built-in music library.
      </p>
      <p>
        <strong>A real voice, with recorded consent.</strong> Arcade’s docs say “Video generation
        currently supports synthetic voiceover only” <Cite n={[5]} />, and your own speech reaches
        the video through a recording you add. Its docs for the demo product describe connecting
        your own account at a voice provider to use a cloned voice <Cite n={[25]} />. We did not
        find that in the AI Video docs. ReelBolt narrates with a stock voice, or with a voice
        cloned from a real person who has given consent, which you record in the project and can
        withdraw. We do not compare the two companies’ consent processes here.
      </p>
      <p>
        <strong>Feedback from someone without an account.</strong> A ReelBolt review link lets a
        client or a boss watch one finished video without signing up, leave notes at exact moments,
        and approve it or ask for changes. Arcade’s docs describe a shared preview link, “a
        lower-quality version for quick review” <Cite n={[12]} />. Commenting is documented for its
        interactive demos <Cite n={[27]} />, and we found none for AI Video on the pages we read.
      </p>
      <p>
        <strong>Hand-off to your own editor.</strong> ReelBolt exports OTIO, EDL and Final Cut XML
        and can prepare a hand-off package with the cut and your video files. That package is an
        Early preview, and we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut
        Pro. Arcade publishes a video as an MP4 <Cite n={[12]} />. We found no export for another
        editing program on the pages we read.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> Both tools keep a brand kit.
        Arcade builds one from your website address, with one kit on Free and Pro and more on
        Enterprise <Cite n={[7, 10]} />. ReelBolt builds a brand kit from a brand PDF or a
        design-tool export, and it also reads design token and CSS files if your team keeps them.
        Reading your files is free. AI fills the gaps only after you confirm an estimate.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, a ReelBolt recipe
        makes a promo video from an app’s source code, which you upload as a zip or connect from
        GitHub. Arcade starts from a website address, a recording, a demo or
        documents. You can attach individual text and code files to a prompt{' '}
        <Cite n={[3]} />, and we found no code repository input on the pages we read.
      </p>

      <h2 id="pricing-approach">Pricing approach</h2>
      <p>
        Arcade sells two products, each with its own plans. AI Video has Free, Pro and Enterprise{' '}
        <Cite n={[10]} />. Interactive Demo has Free, Growth and Enterprise, with Growth charged
        per seat for up to 10 seats <Cite n={[11]} />. Arcade’s docs say the Growth plan “does not
        include AI Video or AI credits”, and that only an Enterprise contract can cover both
        products <Cite n={[19]} />. Without that contract, demos and videos are two
        subscriptions.
      </p>
      <p>
        On AI Video, the Free plan has one-time credits that do not refresh, a single seat, one
        brand kit, videos up to 30 seconds and 1080p exports with an Arcade watermark. Pro has a
        monthly credit allowance that you choose from several levels, videos up to 5 minutes, 4K
        exports with no watermark and up to 10 seats at no added cost per seat. Enterprise is
        custom <Cite n={[10]} />. The docs note that the length menu currently stops at 2 minutes{' '}
        <Cite n={[10]} />. Paid plans allow commercial use <Cite n={[10]} />.
      </p>
      <p>
        Generating a video costs more credits the longer it is, and the cost also depends on which
        of three model tiers you pick. Asking the AI to change a video costs credits, and changes
        you make yourself do not. Arcade’s pricing page says publishing and exporting “are free on
        every plan and do not use credits”. It also says Arcade does not sell one-off credit packs,
        that monthly credits do not roll over, and that credits on an annual plan carry over within
        the term <Cite n={[17]} />. Current figures are on{' '}
        <a href="https://www.arcade.software/pricing" rel="nofollow noopener" target="_blank">
          Arcade’s pricing page<span className="sr-only"> (opens in a new tab)</span>
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
        Before a ReelBolt run starts, you see the credits it is expected to use, as a likely amount
        and the range it usually falls in. If a step fails because of a problem on our side, the
        credits held for it are refunded. Top-up packs are for paid plans and expire after{' '}
        {catalog('topUp.expiresAfterMonths')} months.
      </p>
      <Figure
        name="run-estimate"
        alt="The run estimate in ReelBolt, showing the estimated credits for a run, a rough-estimate note and the credits the workspace has available."
        width={1240}
        height={218}
        caption="Look at the credit estimate, which appears before the run starts."
      />
      <p>
        A credit buys something different at each company, so comparing one tool’s credit with
        another’s will mislead you. Compare the cost of the video you actually need.
      </p>

      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <p>Pick Arcade if any of these is true:</p>
      <ul>
        <li>You have an idea, a brand and a website, and little or no footage.</li>
        <li>You want a designed, animated video with a voiceover and music made for it.</li>
        <li>You need a demo people can click through, with or without a video.</li>
        <li>You want to record your screen and yourself in the same tool that makes the video.</li>
        <li>Your source is a document, a deck or a web page.</li>
        <li>Your company needs a SOC 2 report from its vendors today.</li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You want the real people and places in your footage to carry the video.</li>
        <li>You want a short video, about 90 seconds or less.</li>
        <li>You would rather fix a first cut than build one.</li>
        <li>You want to hand the cut to your own editor, or send it to a client for notes.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        A marketing team with a launch to announce and a brand to keep to is who Arcade says it
        is for. A shop owner, a teacher, a volunteer or a founder with a camera roll of clips and no
        editor is who ReelBolt is built for. A designed explainer and a cut of real footage are
        different jobs, and nothing stops you from using a tool built for each.
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
          { id: 'source-1', label: 'Arcade, home page', url: 'https://www.arcade.software/', accessed: CHECKED },
          { id: 'source-2', label: 'Arcade docs, “Welcome to AI Video”', url: 'https://docs.arcade.software/kb/ai-video/welcome-to-ai-video.md', accessed: CHECKED },
          { id: 'source-3', label: 'Arcade docs, AI Video', url: 'https://docs.arcade.software/kb/ai-video/ai-video.md', accessed: CHECKED },
          { id: 'source-4', label: 'Arcade docs, AI Video Chrome extension overview', url: 'https://docs.arcade.software/kb/ai-video/ai-video/overview.md', accessed: CHECKED },
          { id: 'source-5', label: 'Arcade docs, editing a video', url: 'https://docs.arcade.software/kb/ai-video/ai-video/editing.md', accessed: CHECKED },
          { id: 'source-6', label: 'Arcade docs, scenes', url: 'https://docs.arcade.software/kb/ai-video/ai-video/scenes.md', accessed: CHECKED },
          { id: 'source-7', label: 'Arcade docs, brand kit', url: 'https://docs.arcade.software/kb/ai-video/brand-kit.md', accessed: CHECKED },
          { id: 'source-8', label: 'Arcade, enterprise page', url: 'https://www.arcade.software/enterprise', accessed: CHECKED },
          { id: 'source-9', label: 'Arcade, changelog (entries of October 20 and November 4, 2026)', url: 'https://www.arcade.software/changelog', accessed: CHECKED },
          { id: 'source-10', label: 'Arcade docs, AI Video plans, credits and pricing', url: 'https://docs.arcade.software/kb/ai-video/plans-credits-and-pricing.md', accessed: CHECKED },
          { id: 'source-11', label: 'Arcade, Interactive Demo pricing', url: 'https://www.arcade.software/pricing-interactive-demos', accessed: CHECKED },
          { id: 'source-12', label: 'Arcade docs, sharing and exporting', url: 'https://docs.arcade.software/kb/ai-video/ai-video/sharing-and-exporting.md', accessed: CHECKED },
          { id: 'source-13', label: 'Arcade docs, team management', url: 'https://docs.arcade.software/kb/ai-video/team-management.md', accessed: CHECKED },
          { id: 'source-14', label: 'Arcade docs, MCP', url: 'https://docs.arcade.software/kb/ai-video/mcp.md', accessed: CHECKED },
          { id: 'source-15', label: 'Arcade docs, MCP supported clients', url: 'https://docs.arcade.software/kb/ai-video/mcp/supported-clients.md', accessed: CHECKED },
          { id: 'source-16', label: 'Arcade docs, REST API', url: 'https://docs.arcade.software/kb/interactive-demo/leverage/advanced-features/rest-api.md', accessed: CHECKED },
          { id: 'source-17', label: 'Arcade, AI Video pricing', url: 'https://www.arcade.software/pricing', accessed: CHECKED },
          { id: 'source-18', label: 'Arcade, privacy policy', url: 'https://www.arcade.software/privacy', accessed: CHECKED },
          { id: 'source-19', label: 'Arcade docs, Interactive Demo plans and AI credits', url: 'https://docs.arcade.software/kb/interactive-demo/admin/plans-and-ai-credits.md', accessed: CHECKED },
          { id: 'source-20', label: 'Arcade docs, AI Video security', url: 'https://docs.arcade.software/kb/ai-video/security.md', accessed: CHECKED },
          { id: 'source-21', label: 'Arcade docs, templates', url: 'https://docs.arcade.software/kb/ai-video/ai-video/templates.md', accessed: CHECKED },
          { id: 'source-22', label: 'Arcade docs, memory and preferences', url: 'https://docs.arcade.software/kb/ai-video/ai-video/memory-and-preferences.md', accessed: CHECKED },
          { id: 'source-23', label: 'Arcade docs, AI Video use cases', url: 'https://docs.arcade.software/kb/ai-video/use-cases.md', accessed: CHECKED },
          { id: 'source-24', label: 'Arcade docs, recording an interactive demo', url: 'https://docs.arcade.software/kb/interactive-demo/build/interactive-demo/record.md', accessed: CHECKED },
          { id: 'source-25', label: 'Arcade docs, Interactive Demo audio', url: 'https://docs.arcade.software/kb/interactive-demo/build/interactive-demo/edit/audio.md', accessed: CHECKED },
          { id: 'source-26', label: 'Arcade docs, MCP available tools', url: 'https://docs.arcade.software/kb/ai-video/mcp/available-tools.md', accessed: CHECKED },
          { id: 'source-27', label: 'Arcade docs, Interactive Demo commenting', url: 'https://docs.arcade.software/kb/interactive-demo/build/interactive-demo/share/commenting.md', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on {CHECKED} from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
