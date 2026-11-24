import type { JSX } from 'react';
import Link from 'next/link';
import { Cite } from '@/components/blog/Cite';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { Figure } from '@/components/blog/Figure';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// Written 2026-11-11 for a reader with raw footage and a story, who is not assumed to have a
// software product. Every statement about CapCut comes from CapCut's own public pages (product,
// pricing, help center, newsroom, Trust Center, app store listings), read on 2026-11-11 from a US
// address and listed in the sources. Nothing was tested hands-on, no CapCut price or credit figure
// appears, and no CapCut model is named. Where two CapCut pages disagree (the free plan's
// watermark), both are quoted. ReelBolt facts follow docs/user-guide (recipes/choose-a-recipe,
// derush-edit, subtitles, voiceover, run-settings, thumbnail-and-cover-text; workflows/
// request-changes; projects/avatars; design-system; editor/index, export-your-film,
// share-for-review; account/plans-and-credits, reading-the-estimate). Every ReelBolt figure comes
// from the plan catalog or from the constants the Terms and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-capcut',
  title: 'ReelBolt vs CapCut: an editor you work in, or a first cut made for you?',
  description:
    'CapCut is a mature, full video editor. ReelBolt is a public alpha that makes and reviews a first cut from your raw clips. See where each is the better choice.',
  date: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 13,
  unlisted: false,
  faqs: [
    {
      q: 'Is ReelBolt a replacement for CapCut?',
      a: 'No. CapCut is a full video editor with phone, desktop and web apps, templates and a wide set of AI tools. ReelBolt is a public alpha that does one narrower job: it takes your raw clips and a short brief, makes a short first cut, and reviews it before you see it. If you need a general editor for everything you make, choose CapCut.',
    },
    {
      q: 'I have never edited a video. Which one is easier to start with?',
      a: 'They ask different things of you. CapCut gives you an editor, templates and AI tools, and its App Store listing says beginners can get started in a matter of seconds. You still decide what the video is and build it. ReelBolt asks for your clips and a sentence or two about the video, then hands you a first cut to react to. If you want to make the choices yourself, start with CapCut. If you would rather react to a draft, start with ReelBolt.',
    },
    {
      q: 'Does CapCut also have AI that cuts raw footage?',
      a: 'Yes. CapCut’s own pages describe Auto Cut, which trims and syncs clips to music beats, speech pauses or text prompts on mobile and desktop, a tool that removes filler words and pauses on desktop, and EditPilot, an editing agent in CapCut for PC that takes one request at a time in English. We have not tested them, so we make no claim about which product cuts better.',
    },
    {
      q: 'Can I use both?',
      a: `Yes. A ReelBolt run ends in an MP4 file you can download, and CapCut’s web editor accepts files uploaded from your computer, MP4 included. You can let ReelBolt make the first cut and finish it in CapCut. On the ReelBolt free trial that file is ${TRIAL_RESOLUTION} with a watermark.`,
    },
    {
      q: 'Does ReelBolt have a phone app?',
      a: 'No. ReelBolt runs in a browser, and its editor has a phone view for simpler edits. CapCut has apps for iOS and Android, desktop apps for Windows and macOS, and a web editor.',
    },
    {
      q: 'Does ReelBolt have templates and a music library like CapCut?',
      a: 'No. A ReelBolt recipe is not a design template. It is a ready-made app for one kind of video, such as a highlight edit or subtitles. There is no built-in music library, so background music and sound effects come from files you upload. If you want to start from a template or from stock footage, CapCut is the better fit.',
    },
    {
      q: 'Why are there no CapCut prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and CapCut’s own pricing page says the displayed pricing is for reference only until you sign in. CapCut’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsCapcut(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who is deciding between
        CapCut and ReelBolt: a creator, a small business, a teacher, a nonprofit, an agency or a
        software team. We make ReelBolt, so read it with that in mind. Every statement about CapCut
        comes from CapCut’s own public pages, read on {CHECKED} from a US address and linked in the
        sources at the end. Several of those pages say a feature is available only in some regions
        or app versions <Cite n={[12, 18, 27]} />, so check what your own account shows. We did not
        test CapCut hands-on, and we give no CapCut prices.
      </p>
      <p>
        The short version: CapCut is a full video editor and the far more established product. By
        its own count it serves more than 700 million users every month <Cite n={[2]} />, and it
        runs on phones, on desktop computers and on the web. ReelBolt is a public alpha that does
        one narrower job. You drop in raw clips and say what the video is for, and an AI team makes
        a short first cut and reviews it before you see it. If you want to edit, or you need one
        editor for everything you make, choose CapCut. If the blank timeline is what keeps your
        video from getting made, that is the job ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        CapCut’s home page calls it an “AI-Powered Photo &amp; Video Editor for Everyone” and says
        it “has everything you need to create trending content for YouTube, Instagram, and beyond”{' '}
        <Cite n={[1]} />. Its newsroom describes “an all-in-one global creative platform serving
        more than 700 million users every month across 170+ markets and 30 languages”{' '}
        <Cite n={[2]} />. The site names marketers, social media creators, designers, and filmmakers
        and studios among the people it is for <Cite n={[1]} />. At the center is the video editor,
        which its navigation sums up as “Cut, arrange, and polish video on a timeline”{' '}
        <Cite n={[1]} />, with unlimited video, audio, image and text tracks <Cite n={[3]} />.
        Around it sit templates, AI tools and, on the PC app, an editing agent named EditPilot{' '}
        <Cite n={[1, 12]} />. You can start from your own footage, a template, a script, a prompt
        or a reference image <Cite n={[1, 7, 8, 9, 13]} />.
      </p>
      <p>
        ReelBolt is an AI video team for anyone with a story to tell and no time to tell it. It is a
        public alpha, so expect some rough edges. You upload the clips you already shot, between 1
        and 20 for a highlight edit, and write a sentence or two about what the video is for. An AI
        editor chooses what to keep and leaves out dead air, false starts and retakes. A reviewer
        scores the draft, and a low score sends it back for another pass before you see it. The
        result is one short video, about 90 seconds or less, in the shape you chose. A run takes
        minutes for a small job and sometimes half an hour for a big one.
      </p>
      <p>
        Each kind of video is a recipe: a ready-made app you pick, fill in and run. Raw clips are
        the usual way in, not the only one. For software teams there is also a recipe that makes a
        promo video from an app’s source code.
      </p>

      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={`ReelBolt and CapCut compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'CapCut']}
        rows={[
          {
            label: 'Stage',
            cells: [
              'Public alpha; the editor is labeled Early preview',
              { text: <>An established product: more than 700 million users every month, by its own count <Cite n={[2]} /></> },
            ],
          },
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>Your own footage, a template, a script, a prompt or a reference image <Cite n={[1, 7, 8, 9, 13]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor, then a reviewer that scores the draft before you see it',
              { text: <>You, on the timeline or from a template; AI tools such as Auto Cut on mobile and desktop, and the EditPilot agent on PC <Cite n={[1, 12, 15]} /></> },
            ],
          },
          {
            label: 'Editing by hand',
            cells: [
              'A timeline editor, labeled Early preview, for changes after the first cut',
              { text: <>The center of the product: a timeline with unlimited video, audio, image and text tracks <Cite n={[1, 3]} /></> },
            ],
          },
          {
            label: 'Templates and recipes',
            cells: [
              'Recipes: a ready-made app for each kind of video; no stock footage library',
              { text: <>Templates for a one-click start; a script-to-video tool that uses stock footage or your own clips <Cite n={[1, 9]} /></> },
            ],
          },
          {
            label: 'Recording',
            cells: [
              'No screen or camera recorder; the editor records a voiceover',
              { text: <>The web editor records a browser tab, the whole screen or a webcam <Cite n={[10]} /></> },
            ],
          },
          {
            label: 'Transcript, filler words and silences',
            cells: [
              'Dead air, false starts and retakes left out of the first cut; the editor can cut from the transcript, remove silence and find fillers when the footage was transcribed',
              { text: <>Transcript-based editing with transcripts in 100+ languages; removal of filler words, repetitions and pauses on desktop <Cite n={[11, 14]} /></> },
            ],
          },
          {
            label: 'Captions and subtitles',
            cells: [
              'Burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>Auto captions in 20+ languages on the web, with caption styles; subtitle translation in over 27 languages; a standalone SRT file on desktop and online <Cite n={[16, 17, 31]} /></> },
            ],
          },
          {
            label: 'Dubbing',
            cells: [
              'Yes, sound only: no lip sync',
              { text: <>AI dubbing in the desktop editor, in specific regions; lip sync in the desktop version, in 13 languages <Cite n={[18, 19, 20]} /></> },
            ],
          },
          {
            label: 'Voice',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker',
              { text: <>Over 1,000 AI voices; a custom voice made by reading a sentence aloud <Cite n={[19, 22]} /></> },
            ],
          },
          {
            label: 'Avatars',
            cells: [
              'A real adult, from their own pictures and recordings, in clips of about 15 seconds at most; Pro and up, still rolling out; no stock avatars',
              { text: <>A library of 1000+ digital human categories; an avatar made from a photo or from your own video <Cite n={[21]} /></> },
            ],
          },
          {
            label: 'Generated video and music',
            cells: [
              'AI-generated clips on Pro and up; music and sound-effect generation on Creator and up; no built-in music library',
              { text: <>Turns text, images or keyframes into video, with a choice of video models in its navigation <Cite n={[1]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>Brand kit of colors, logos and fonts on the Teams plan, editable by admins <Cite n={[6, 25, 26]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>Auto reframe across aspect ratios, in specific regions <Cite n={[27]} /></> },
            ],
          },
          {
            label: 'Short clips from one long video',
            cells: [
              'No: each run makes one short video',
              { text: <>Yes: long video to shorts finds highlights, tracks the subject and adapts 16:9 to 9:16 <Cite n={[28]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>Team spaces with roles; a link for review with comments and annotation; one extra member free, more on the Teams plan <Cite n={[3, 26]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>MP4 or MOV on the web; 2K and 4K depending on the platform; no import or export of other editors’ project files <Cite n={[29, 30, 32]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits',
              { text: <>iOS and Android apps, desktop apps for Windows and macOS, and a web editor <Cite n={[1, 4, 5, 38]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              {
                text: <>An official ChatGPT plugin; its terms of service mention an SDK and an API, without detail on the pages we read <Cite n={[13, 33]} /></>,
                note: 'We could not confirm whether the plugin is available to every account today.',
              },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A standing Free plan with basic editing tools and free templates, no card <Cite n={[1, 6]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-capcut-is-stronger">Where CapCut is the better choice</h2>
      <p>
        <strong>It is the mature product.</strong> CapCut says it serves more than 700 million
        users every month across 170+ markets <Cite n={[2]} />, and that its desktop app is
        “suitable for individuals ranging from beginners to experts” <Cite n={[5]} />. ReelBolt is
        a public alpha. It has rough edges, and its editor carries an Early preview label because
        some tools in it are still being finished. If you need one editor you can rely on today for
        everything you make, CapCut is the safer choice.
      </p>
      <p>
        <strong>A full editor in your hands.</strong> CapCut is built around its timeline, with
        unlimited video, audio, image and text tracks <Cite n={[3]} />. On export you choose the
        resolution, quality, frame rate and file format <Cite n={[7]} />, and its help center lists
        full 2K and 4K support on desktop, 4K with conditions online and limited 4K on mobile{' '}
        <Cite n={[30]} />. ReelBolt has a timeline editor too, labeled Early preview, but the
        product is built around the first cut being made for you. If editing by hand is how you
        like to work, CapCut is built for that and ReelBolt is not.
      </p>
      <p>
        <strong>Apps on your phone and your computer.</strong> CapCut has apps for iOS and Android{' '}
        <Cite n={[4, 38]} />, desktop apps for Windows and macOS <Cite n={[5]} /> and a web editor{' '}
        <Cite n={[1]} />, and its help center says Pro benefits apply on the mobile apps, the desktop
        apps and the web editor <Cite n={[37]} />. One gap on its side: the help center says the dedicated iPad app is in
        beta development and is unavailable in the U.S. App Store <Cite n={[39]} />. ReelBolt runs
        in a browser. Its editor has a phone view for simpler edits, and there is no app to
        install.
      </p>
      <p>
        <strong>Starting from a template, a script or a prompt.</strong> CapCut offers templates as
        a “one-click start” <Cite n={[1]} />. Its script-to-video tool builds a video from stock
        footage or your own clips <Cite n={[9]} />, and its AI video generator starts from a script
        or a reference image <Cite n={[8]} />. ReelBolt’s free trial has no recipe that turns only
        a text prompt or only photos into a video, and it has no stock footage library. If the video
        is for a business, note one thing on CapCut’s side: its Trust Center says only templates
        and materials labeled “Commercial Use” can be used for commercial purposes{' '}
        <Cite n={[40]} />.
      </p>
      <p>
        <strong>Tools for footage of someone talking.</strong> CapCut has transcript-based editing,
        with transcripts in 100+ languages <Cite n={[11]} />. On desktop it can remove filler
        words, repetitions and pauses, and search your footage by objects, speech, people and
        environments <Cite n={[14]} />. Auto Cut trims and syncs clips to music beats, speech
        pauses or text prompts on mobile and desktop <Cite n={[15]} />. ReelBolt’s editor can also
        cut from the transcript, find fillers and remove silence, once the footage has been analyzed
        and transcribed, and inside an editor that is an Early preview.
      </p>
      <p>
        <strong>Many short clips from one long video.</strong> CapCut’s long video to shorts tool
        finds the highlights in a video, trims them into clips and adapts 16:9 to 9:16 with subject
        tracking. CapCut says it suits video podcasts, educational videos, commentaries and
        speeches <Cite n={[28]} />. It also has auto reframe across aspect ratios, in specific
        regions <Cite n={[27]} />. ReelBolt makes one short video per run, and it cannot yet re-cut
        a finished video to another shape.
      </p>
      <p>
        <strong>Languages, lip sync and stock presenters.</strong> CapCut lists auto captions in
        20+ languages on the web <Cite n={[16]} /> and subtitle translation in over 27{' '}
        <Cite n={[17]} />. Its AI dubbing is in the desktop editor, in specific regions{' '}
        <Cite n={[18]} />. Lip sync is in the desktop version, with 13 languages and over 1,000 AI
        voices, and CapCut says it works on real people, AI avatars and pets{' '}
        <Cite n={[19, 20]} />. Its avatar page lists 1000+ digital human categories and avatars
        made from a photo or from your own video <Cite n={[21]} />. A ReelBolt dub changes the
        sound only, so the mouth on screen stays as it was filmed. ReelBolt’s{' '}
        <Link href="/blog/reelbolt-avatars">avatars</Link> are narrower: each one is made
        from a real adult’s own pictures and recordings, is shown in clips of about 15 seconds at
        most, is part of the Pro plan and the plans above it, and is still being rolled out. If you need a presenter nobody has to
        film, CapCut covers that and ReelBolt does not.
      </p>
      <p>
        <strong>Video made from nothing but a description.</strong> CapCut turns text, images or
        keyframes into video and lists a choice of video models in its navigation{' '}
        <Cite n={[1]} />. A July 2026 newsroom post says one of those models generates clips up to
        30 seconds long and takes up to 50 references for a single clip, rolling out to subscriber
        accounts in Europe, Asia, the Middle East and South America <Cite n={[34]} />. In
        ReelBolt, AI-generated clips are short, and they are part of the Pro plan and the plans
        above it.
      </p>
      <p>
        <strong>Recording, and a free plan you can stay on.</strong> CapCut’s web editor records a
        browser tab, the whole screen or a webcam <Cite n={[10]} />. ReelBolt does not record your
        screen or camera. CapCut also has a standing Free plan, with no credit card required{' '}
        <Cite n={[1, 6]} />. ReelBolt has a free trial with one-time credits, which is not a plan
        you can stay on.
      </p>
      <p>
        <strong>Labels for AI-made content.</strong> CapCut’s newsroom describes AI labels, C2PA
        Content Credentials and invisible watermarks that help people identify AI-generated
        content <Cite n={[2]} />. ReelBolt marks its avatar clips as AI-generated in the file and,
        by default, with a small label in the picture. It does not attach Content Credentials
        today.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It makes the first cut, then checks it.</strong> ReelBolt assumes you would rather
        react to a cut than build one. You do not open a timeline or pick a template first. An AI
        editor decides what to keep, a reviewer scores the result, and a low score sends the edit
        back for another pass before you see it. CapCut has AI tools on similar ground. Besides Auto
        Cut, its EditPilot agent trims spoken clips, adds captions and cleans up audio. CapCut
        describes it this way: “Say what you want to change in plain words, review the result, and
        keep shaping the video your way.” It is part of CapCut for PC, takes requests in English
        and works “through one clear task at a time” <Cite n={[12]} />. Its home page also
        describes a chat that builds a video from scratch <Cite n={[1]} />. We have
        not tested any of them, so we make no claim about which product cuts better. The difference
        we can state is where each one starts. CapCut is an editor with AI tools and an agent
        inside it. ReelBolt is a brief and a run, with an editor for afterwards.
      </p>
      <p>
        <strong>It is built for a camera roll and a short video.</strong> Give it the clips from
        your phone: the shop, the event, the lesson, the product. Several different shots give the
        editor something to cut together. One long take can only be trimmed. When the speech in
        your clips could be read, cuts are placed so they do not land inside a spoken word.{' '}
        <Link href="/blog/what-a-highlight-edit-removes">What a highlight edit removes</Link> shows
        the kind of thing that gets cut.
      </p>
      <p>
        <strong>Recipes for the job, not a toolbox.</strong> You pick what you want to end up with,
        and the recipe’s page asks only for what that needs and tells you when something is missing.
        One recipe makes the highlight edit. Others add titles and callouts, write and voice a
        narration with captions, burn in subtitles and produce the SRT and VTT files, translate the
        subtitles, or dub the video. The run page has no download button for the subtitle files yet,
        which is one of the rough edges. One makes a cover picture for very few credits, and the
        assistant can write the text that goes with the post. You choose the shape before each run:
        landscape, vertical, square or portrait. Music and sound effects come from files you upload,
        because there is no built-in music library.
      </p>
      <p>
        <strong>The first cut is a starting point, and you can change it.</strong> There are two
        ways. On the recipe’s page, a finished video lets you change a setting, such as the length
        or your notes, and make it again. The parts your change does not touch are kept from the
        first run at no cost, and the first video is kept too. Or open the cut in the editor and
        trim, reorder or add text by hand. In a narrated video you can also rewrite one line of
        the narration, and the lines you did not touch are reused. Two limits: the shape, the
        resolution and the style cannot be changed that way, so for those you run the recipe
        again, and the box where you describe a change in plain words works on animated videos,
        not on a footage edit.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one clip on the track below and a Remove silence button above them."
        width={1280}
        height={214}
        caption="A demo timeline in the editor (Early preview), with the clips of a cut on their tracks."
      />
      <p>
        <strong>You see the cost before you spend it.</strong> Before a run starts, ReelBolt shows
        the credits it is expected to use, as a likely amount and the range it usually falls in.
        You are charged for what the run actually uses. If a step fails because of a problem on our
        side, the credits held for it are refunded.
      </p>
      <Figure
        name="run-estimate"
        alt="The run estimate in ReelBolt, showing the estimated credits for a run, a rough-estimate note and the credits the workspace has available."
        width={1240}
        height={218}
        caption="Look at the credit estimate, which appears before the run starts."
      />
      <p>
        <strong>A real person’s voice or likeness needs recorded consent.</strong> In ReelBolt a
        cloned voice and an avatar each need the person’s agreement, which you record in the
        project, and you can withdraw it or delete the voice or avatar at any time. The person
        behind an avatar must be an adult. CapCut has its own rules for a personalized voice and
        for other people’s faces and voices, described on its voice page, its blog and its help
        center <Cite n={[22, 23, 24]} />. We do not compare the two processes here.
      </p>
      <p>
        <strong>Hand-off to a professional editor.</strong> ReelBolt exports the cut as OTIO, EDL
        and Final Cut XML, and can prepare a hand-off package with the cut and your video files.
        That package is an Early preview, and we have not yet opened one in Premiere Pro, DaVinci
        Resolve or Final Cut Pro. CapCut’s help center says: “Currently, CapCut does not support
        direct importing or exporting of project files from third-party editing software, including
        Adobe Premiere Pro” <Cite n={[32]} />. The route it suggests is to export a rendered file
        or rebuild the project <Cite n={[32]} />, and its subtitle tool can export a standalone
        SRT file <Cite n={[31]} />.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> CapCut’s brand kit saves and
        applies brand colors, logos and fonts across projects. It is part of the Teams plan and is
        edited by admins <Cite n={[6, 25, 26]} />. ReelBolt builds a brand kit from a brand PDF or
        a design-tool export, and it also reads design token and CSS files if your team keeps them.
        Reading your files is free. AI fills the gaps only after you confirm an estimate.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, ReelBolt also has
        recipes that make a promo video from an app’s source code. The footage recipes described
        above need no code.
      </p>

      <h2 id="pricing-approach">Plans and credits</h2>
      <p>
        CapCut’s web pricing page lists four plans: Free, Pro, Ultra and Teams{' '}
        <Cite n={[6]} />. Its help center also mentions a Standard tier, available only in certain
        regions and only on the mobile app <Cite n={[37]} />. Pro, Ultra and Teams each list a
        monthly allowance of AI credits, and Teams is sold by the seat and includes the brand kit{' '}
        <Cite n={[6]} />. The page says new users can start a 7-day free trial of Pro with no
        credit card, that availability may vary, and that displayed pricing is for reference only
        until you sign in <Cite n={[6]} />. Current figures are on{' '}
        <a href="https://www.capcut.com/pricing" rel="nofollow noopener" target="_blank">
          CapCut’s pricing page<span className="sr-only"> (opens in a new tab)</span>
        </a>
        .
      </p>
      <p>
        CapCut’s pages describe what the free plan includes in two ways, so test an export from
        your own account. Its online editor page says you can “make a video online without a
        watermark for both commercial use and personal needs” <Cite n={[7]} />. Its help center
        says: “Free accounts may face watermarks or bitrate limits on 4K exports; CapCut Pro
        subscribers get unrestricted 4K” <Cite n={[30]} />, and that a watermark on a template can
        come from a default ending clip or a built-in template element <Cite n={[35]} />. In the
        same way, one page says CapCut “offers most AI-powered video editing tools for free”{' '}
        <Cite n={[36]} />, while the pricing page lists “Access to AI features” under Pro{' '}
        <Cite n={[6]} />.
      </p>
      <p>
        ReelBolt plans are credit based, and no card is required to start. A trial workspace has{' '}
        {TRIAL_CREDITS} one-time credits. When you verify your email, the account also receives a
        one-time grant of {FIRST_VIDEO_GRANT_CREDITS} credits, which expires{' '}
        {FIRST_VIDEO_GRANT_EXPIRY} after it is granted. Trial exports are {TRIAL_RESOLUTION} with a
        watermark. The paid plans are Creator, Pro, Team and Enterprise. Creator exports up to{' '}
        {CREATOR_RESOLUTION} with no watermark and Pro up to {PRO_RESOLUTION}. AI-generated clips
        and avatars are part of Pro and the plans above it. Top-up packs are for paid plans and
        expire after {catalog('topUp.expiresAfterMonths')} months. The{' '}
        <Link href="/pricing">pricing page</Link> has the current prices and what each plan
        includes.
      </p>
      <p>
        A credit buys something different at each company, so comparing one tool’s credit with
        another’s will mislead you. Compare the cost of the video you actually need.
      </p>

      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <p>Pick CapCut if any of these is true:</p>
      <ul>
        <li>You want to edit the video yourself, or you enjoy doing it.</li>
        <li>You want an app on your phone or your computer.</li>
        <li>You want to start from a template, a script or a prompt, with no footage of your own.</li>
        <li>You have one long video and want several short clips from it.</li>
        <li>You need a dub with lip sync, or a presenter nobody has to film.</li>
        <li>You want a free plan you can stay on.</li>
        <li>You need a product that is well past its alpha.</li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You want a short video, about 90 seconds or less.</li>
        <li>You would rather fix a first cut than build one.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        Someone who edits on a phone every day and likes it has no reason to leave CapCut. A shop
        owner, a volunteer or a founder with a camera roll of clips and no editor is who ReelBolt
        is built for. Nothing stops you from using both: a ReelBolt run ends in an MP4 file you
        can download, and CapCut’s web editor accepts files uploaded from your computer{' '}
        <Cite n={[7, 29]} />, so the first cut can come from one and the finishing from the other.
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
          { id: 'source-1', label: 'CapCut, home page', url: 'https://www.capcut.com/', accessed: CHECKED },
          { id: 'source-2', label: 'CapCut newsroom, CapCut World (27 October 2026)', url: 'https://www.capcut.com/newsroom/capcut-world-la', accessed: CHECKED },
          { id: 'source-3', label: 'CapCut, creative suite', url: 'https://www.capcut.com/creative-suite', accessed: CHECKED },
          { id: 'source-4', label: 'CapCut on the App Store (US)', url: 'https://apps.apple.com/us/app/capcut-photo-video-editor/id1500855883', accessed: CHECKED },
          { id: 'source-5', label: 'CapCut, desktop video editor', url: 'https://www.capcut.com/tools/desktop-video-editor', accessed: CHECKED },
          { id: 'source-6', label: 'CapCut, pricing', url: 'https://www.capcut.com/pricing', accessed: CHECKED },
          { id: 'source-7', label: 'CapCut, online video editor', url: 'https://www.capcut.com/tools/online-video-editor', accessed: CHECKED },
          { id: 'source-8', label: 'CapCut, AI video generator', url: 'https://www.capcut.com/tools/ai-video-generator', accessed: CHECKED },
          { id: 'source-9', label: 'CapCut, script to video maker', url: 'https://www.capcut.com/tools/script-to-video-maker', accessed: CHECKED },
          { id: 'source-10', label: 'CapCut, online screen recorder', url: 'https://www.capcut.com/tools/online-screen-recorder', accessed: CHECKED },
          { id: 'source-11', label: 'CapCut, transcript-based editing', url: 'https://www.capcut.com/tools/video-transcript-editing', accessed: CHECKED },
          { id: 'source-12', label: 'CapCut, EditPilot', url: 'https://www.capcut.com/tools/edit-pilot', accessed: CHECKED },
          { id: 'source-13', label: 'CapCut, CapCut and ChatGPT', url: 'https://www.capcut.com/tools/capcut-x-codex', accessed: CHECKED },
          { id: 'source-14', label: 'CapCut, desktop AI tools', url: 'https://www.capcut.com/tools/desktop-ai-power', accessed: CHECKED },
          { id: 'source-15', label: 'CapCut help, Auto Cut (3 July 2026)', url: 'https://www.capcut.com/help/auto-cut-not-working', accessed: CHECKED },
          { id: 'source-16', label: 'CapCut, AI caption generator', url: 'https://www.capcut.com/tools/ai-caption-generator', accessed: CHECKED },
          { id: 'source-17', label: 'CapCut, video translator', url: 'https://www.capcut.com/tools/video-translator', accessed: CHECKED },
          { id: 'source-18', label: 'CapCut, AI dubbing', url: 'https://www.capcut.com/tools/ai-dubbing', accessed: CHECKED },
          { id: 'source-19', label: 'CapCut, lip sync', url: 'https://www.capcut.com/tools/lip-sync', accessed: CHECKED },
          { id: 'source-20', label: 'CapCut help, lip sync feature (24 March 2026)', url: 'https://www.capcut.com/help/lip-sync-feature', accessed: CHECKED },
          { id: 'source-21', label: 'CapCut, AI avatar', url: 'https://www.capcut.com/tools/ai-avatar', accessed: CHECKED },
          { id: 'source-22', label: 'CapCut, AI voice over', url: 'https://www.capcut.com/tools/ai-voice-over', accessed: CHECKED },
          { id: 'source-23', label: 'CapCut blog, AI voice cloner (31 December 2025)', url: 'https://www.capcut.com/resource/ai-voice-cloner', accessed: CHECKED },
          { id: 'source-24', label: 'CapCut help, is CapCut AI safe to use (14 August 2026)', url: 'https://www.capcut.com/help/is-capcut-ai-safe-to-use', accessed: CHECKED },
          { id: 'source-25', label: 'CapCut help, benefits of CapCut Teams (25 March 2026)', url: 'https://www.capcut.com/help/benefits-of-capcut-teams', accessed: CHECKED },
          { id: 'source-26', label: 'CapCut help, team collaboration (25 March 2026)', url: 'https://www.capcut.com/help/team-collaboration-feature', accessed: CHECKED },
          { id: 'source-27', label: 'CapCut, auto reframe', url: 'https://www.capcut.com/tools/auto-reframe', accessed: CHECKED },
          { id: 'source-28', label: 'CapCut, long video to shorts', url: 'https://www.capcut.com/tools/long-video-to-shorts', accessed: CHECKED },
          { id: 'source-29', label: 'CapCut, online video converter', url: 'https://www.capcut.com/tools/online-video-converter', accessed: CHECKED },
          { id: 'source-30', label: 'CapCut help, export videos (14 January 2026)', url: 'https://www.capcut.com/help/export-videos-in-capcut', accessed: CHECKED },
          { id: 'source-31', label: 'CapCut, add subtitles to video', url: 'https://www.capcut.com/tools/add-subtitles-to-video', accessed: CHECKED },
          { id: 'source-32', label: 'CapCut help, exporting a project to other editors (21 January 2026)', url: 'https://www.capcut.com/help/how-to-export-pro-project', accessed: CHECKED },
          { id: 'source-33', label: 'CapCut, terms of service (US, last updated 15 April 2026)', url: 'https://www.capcut.com/clause/terms-of-service?store_region=&lang=en', accessed: CHECKED },
          { id: 'source-34', label: 'CapCut newsroom, new video and image models (31 July 2026)', url: 'https://www.capcut.com/newsroom/giving-creators-more-control-with-dreamina-seedance-2-5-and-dola-seedream-5-0-pro', accessed: CHECKED },
          { id: 'source-35', label: 'CapCut help, remove a watermark when exporting (25 March 2026)', url: 'https://www.capcut.com/help/remove-watermark-when-exporting-video', accessed: CHECKED },
          { id: 'source-36', label: 'CapCut, AI video editor', url: 'https://www.capcut.com/tools/ai-video-editor', accessed: CHECKED },
          { id: 'source-37', label: 'CapCut help, subscription across platforms (13 August 2026)', url: 'https://www.capcut.com/help/capcut-subscription-multi-platform', accessed: CHECKED },
          { id: 'source-38', label: 'CapCut on Google Play (US)', url: 'https://play.google.com/store/apps/details?id=com.lemon.lvoverseas&hl=en_US&gl=US', accessed: CHECKED },
          { id: 'source-39', label: 'CapCut help, CapCut for iPad (6 January 2026)', url: 'https://www.capcut.com/help/capcut-for-ipad', accessed: CHECKED },
          { id: 'source-40', label: 'CapCut Trust Center, legal', url: 'https://www.capcut.com/trust/legal', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on {CHECKED} from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
