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
// software product. This post is deliberately short. Every statement about Canva comes from a page
// that was read in full on 2026-11-11: Canva's App Store listing, the title of its Google Play
// listing, and canva.dev (developer documentation, changelog, developer blog). Canva's main website
// (video editor page, pricing, help center, newsroom) did not load for the reader that day, so
// nothing that could only be sourced from it appears here: no timeline details, no AI video tools,
// no voice, dubbing or avatar features, no brand kits, no plan contents. The post says so to the
// reader. Before extending it, a person must open the canva.com pages in a browser. Nothing was
// tested hands-on, and no Canva price appears. ReelBolt facts follow docs/user-guide
// (recipes/choose-a-recipe, derush-edit, subtitles, thumbnail-and-cover-text; workflows/app-inputs,
// request-changes; editor/index, export-your-film, share-for-review; account/plans-and-credits,
// reading-the-estimate). Every ReelBolt figure comes from the plan catalog or from the constants
// the Terms and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-canva',
  title: 'ReelBolt vs Canva for video: one creative platform, or a first cut made for you?',
  description:
    'Canva is an established creative platform with video in it. ReelBolt is a public alpha that makes a first cut from your raw clips. See who should pick which.',
  date: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 10,
  unlisted: false,
  faqs: [
    {
      q: 'Is ReelBolt a replacement for Canva?',
      a: 'No. Canva describes itself as one platform for graphic design, photo editing and video creation, with apps for phones, tablets and computers. ReelBolt is a public alpha that does one narrower job: it takes your raw clips and a short brief, makes a short first cut, and reviews it before you see it. If you need one tool for everything you design, choose Canva.',
    },
    {
      q: 'Why does this comparison say so little about Canva’s video editor?',
      a: 'Because we only write what we could read. On 2026-11-11 we could read Canva’s app store listings and its developer documentation in full. Canva’s main website, help center and newsroom did not load for the tool we use to read sources. So this post does not describe Canva’s timeline, its AI video tools, its voice, dubbing or avatar features, its brand kits or what each plan includes. Canva’s own site is the place to check those.',
    },
    {
      q: 'Does Canva have AI that turns raw footage into a video?',
      a: 'We cannot say from the pages we could read. Canva has AI features: its app is named “Canva AI Photo & Video Editor”, and its App Store listing mentions an AI-powered Bulk Create. The Canva pages that describe its AI video tools are among those we could not read, and we have not tested Canva, so this post makes no claim either way. Check Canva’s site before you decide.',
    },
    {
      q: 'Can I use both?',
      a: `Yes. A ReelBolt run ends in an MP4 file you can download, and Canva’s developer documentation describes uploading video files as assets. We could not read the Canva help page that lists upload formats and size limits, so check it first. On the ReelBolt free trial that file is ${TRIAL_RESOLUTION} with a watermark.`,
    },
    {
      q: 'Does ReelBolt have a phone app?',
      a: 'No. ReelBolt runs in a browser, and its editor has a phone view for simpler edits. Canva’s App Store listing covers iPhone, iPad, Mac and Apple Vision, and Canva is also listed on Google Play.',
    },
    {
      q: 'Does ReelBolt make social graphics, slides or documents like Canva?',
      a: 'No. ReelBolt makes short videos. Around a video it can make a cover picture with a title on it and write the text that goes with the post. It does not design posters, slides, documents or image carousels. For those, Canva is the better fit.',
    },
    {
      q: 'Why are there no Canva prices in this comparison?',
      a: 'We do not state another company’s prices. They change, and plans are built differently at each company. In this case we also could not read Canva’s pricing page on the day we checked. Canva’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsCanva(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who is deciding between
        Canva and ReelBolt for video: a creator, a small business, a teacher, a nonprofit, an agency
        or a software team. We make ReelBolt, so read it with that in mind. We did not test Canva
        hands-on, and we give no Canva prices.
      </p>
      <p>
        One limit matters more than usual here. On {CHECKED} we could read two kinds of Canva page
        in full: its app store listings and its developer documentation{' '}
        <Cite n={[1, 2, 3]} />. Canva’s main website, help center and newsroom did not load for the
        tool we use to read sources, so nothing in this post comes from them. That makes this a
        short comparison. It does not describe Canva’s video timeline, its AI video tools, its
        voice, dubbing or avatar features, its brand kits, or what each Canva plan includes, and it
        draws no conclusion from their absence here. For those, read Canva’s own site.
      </p>
      <p>
        The short version: Canva is the far more established product, and video is one part of it.
        Its App Store listing describes one platform for graphic design, photo editing and video
        creation <Cite n={[1]} />. ReelBolt is a public alpha that does one narrower job. You drop
        in raw clips and say what the video is for, and an AI team makes a short first cut and
        reviews it before you see it. If you want one place to design everything around the video,
        an app on your phone, or several people working at the same moment, choose Canva. If the
        blank timeline is what keeps your video from getting made, that is the job ReelBolt is
        built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        Canva’s App Store listing says: “Canva is your all-in-one creative platform for stunning
        graphic design, photo editing, and video creation.” <Cite n={[1]} /> The app is named “Canva
        AI Photo &amp; Video Editor” there, and “Canva: AI Photo &amp; Video Editor” on Google Play{' '}
        <Cite n={[1, 2]} />. Of the mobile app, the listing says: “Add captions, apply cinematic
        effects like slow motion, sync your edits to music with Beat Sync” <Cite n={[1]} />. It
        also says that team members collaborate in real time and that Canva is free for eligible
        K-12 teachers and students <Cite n={[1]} />. For developers, Canva documents a REST API and
        a server that AI assistants can connect to <Cite n={[3, 8]} />.
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
        caption={`ReelBolt and Canva compared, from the public pages we could read on ${CHECKED}`}
        columns={['ReelBolt', 'Canva']}
        rows={[
          {
            label: 'Stage',
            cells: [
              'Public alpha; the editor is labeled Early preview',
              'An established product; video is one part of a wider creative platform',
            ],
          },
          {
            label: 'What it covers',
            cells: [
              'Short videos from your clips; a cover picture and post text to go with them',
              { text: <>Graphic design, photo editing and video creation in one platform <Cite n={[1]} /></> },
            ],
          },
          {
            label: 'Files it takes in',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>Per its developer documentation: image and video uploads as assets; PowerPoint, Keynote, Word, PDF, Photoshop and Illustrator files imported as designs <Cite n={[4, 5]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor, then a reviewer that scores the draft before you see it',
              'Not compared here: Canva has AI features, and we could not read the pages that describe its AI video tools',
            ],
          },
          {
            label: 'Editing by hand',
            cells: [
              'A timeline editor, labeled Early preview, for changes after the first cut',
              { text: <>A video editor in its apps; the mobile app lists captions, slow-motion effects and Beat Sync, which syncs edits to music <Cite n={[1]} /></> },
            ],
          },
          {
            label: 'Working together',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles; no live co-editing of a timeline',
              { text: <>Real-time collaboration with team members; comments and replies on designs <Cite n={[1, 9]} /></> },
            ],
          },
          {
            label: 'Many versions of one design',
            cells: [
              'No: each run makes one short video',
              { text: <>Bulk Create for design variations; templates filled in from your own data, video fields included, on Pro, Teams or Enterprise <Cite n={[1, 7]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>Through its API: MP4 from 480p to 4K, horizontal or vertical, plus GIF, PDF, PPTX, PNG and JPG; quality varies by plan <Cite n={[6, 9]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits',
              { text: <>Apps for iPhone, iPad, Mac and Apple Vision; also listed on Google Play <Cite n={[1, 2]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>A REST API with published rate limits; a server AI assistants connect to, with about 47 tools by our count, none specific to editing or generating video <Cite n={[3, 6, 8, 9]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A Free plan; 5GB of storage for everyone; free for eligible K-12 teachers and students <Cite n={[1, 9]} /></> },
            ],
          },
        ]}
      />
      <p>
        The table has no rows for captions in detail, voice, dubbing, avatars, generated video,
        brand kits or output shapes. ReelBolt does some of those and Canva may well do all of them.
        We could not read the Canva pages that would say, so we left the rows out.
      </p>

      <h2 id="where-canva-is-stronger">Where Canva is the better choice</h2>
      <p>
        <strong>It is the established product, and video is only part of it.</strong> Canva covers
        graphic design, photo editing and video creation in one place <Cite n={[1]} />. Its
        developer documentation lists the files it imports as designs, PowerPoint, Keynote, Word,
        PDF, Photoshop and Illustrator among them <Cite n={[4]} />. ReelBolt is a public alpha. It
        has rough edges, and its editor carries an Early preview label because some tools in it are
        still being finished. ReelBolt makes short videos. It does not design posters, slides,
        documents or image carousels. If the video is one piece of a larger set of things you
        design, Canva is the better choice.
      </p>
      <p>
        <strong>An app on your phone, tablet or computer.</strong> Canva’s App Store listing covers
        iPhone, iPad, Mac and Apple Vision, and Canva is also listed on Google Play{' '}
        <Cite n={[1, 2]} />. ReelBolt runs in a browser. Its editor has a phone view for simpler
        edits, and there is no app to install.
      </p>
      <p>
        <strong>Editing by hand.</strong> Canva’s listing describes editing in the mobile app:
        captions, effects such as slow motion, and Beat Sync, which syncs your edits to music{' '}
        <Cite n={[1]} />. ReelBolt has a real timeline editor too, but it is there for changing a
        cut that was made for you, and it is an Early preview. If you like making the choices
        yourself, start with Canva.
      </p>
      <p>
        <strong>Several people working at the same moment.</strong> Canva’s listing says team
        members collaborate in real time <Cite n={[1]} />, and its developer documentation lists
        tools for commenting on a design and replying to comments <Cite n={[9]} />. ReelBolt has
        timecoded comments, a review link for a guest with no account, and a Team plan with a
        shared workspace and roles. It has no live co-editing. If someone else saves a newer
        version of a timeline while you are editing it, your view turns read-only until you reload.
      </p>
      <p>
        <strong>Many versions of one design.</strong> The listing describes an “AI-powered Bulk
        Create” that can “automatically generate thousands of design variations”{' '}
        <Cite n={[1]} />. Canva’s autofill guide describes filling brand templates with your own
        data on Pro, Teams or Enterprise, and says image and video fields accept uploaded assets
        only <Cite n={[7]} />. The developer changelog says video autofill became generally
        available on 23 September 2026 <Cite n={[11]} />. ReelBolt makes one short video per run.
      </p>
      <p>
        <strong>Automation that is past its alpha.</strong> Canva documents a REST API{' '}
        <Cite n={[3]} /> with published rate limits, such as 20 export requests per minute for each
        user <Cite n={[6]} />. It also runs a server that AI assistants connect to, with about 47
        tools by our count for designs, assets, comments, imports and exports{' '}
        <Cite n={[8, 9]} />. A post on its developer blog, dated 27 October 2026, says you can add
        it to your own assistant “with no waiting on approval” <Cite n={[10]} />. ReelBolt has{' '}
        <Link href="/blog/reelbolt-public-api">an API with webhooks</Link> on its paid plans and a
        connection for Claude Code, and all of it is alpha.
      </p>
      <p>
        <strong>More kinds of file out.</strong> Canva’s API exports a design as MP4, GIF, PDF,
        PPTX, PNG or JPG. MP4 comes at 480p, 720p, 1080p or 4K, horizontal or vertical{' '}
        <Cite n={[6]} />, and the quality you can export varies by plan <Cite n={[9]} />. A ReelBolt
        run ends in a video file.
      </p>
      <p>
        <strong>A Free plan, and an offer for schools.</strong> Canva’s developer documentation
        refers to a Free plan next to its paid ones <Cite n={[9]} />. The App Store listing says
        “Save your best work with 5GB for everyone” and that Canva is free for eligible K-12
        teachers and students <Cite n={[1]} />. ReelBolt has a free trial with one-time credits,
        which is not a plan you can stay on, and it has no education plan.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It makes the first cut, then checks it.</strong> ReelBolt assumes you would rather
        react to a cut than build one. You do not open a timeline or pick a template first. An AI
        editor decides what to keep, a reviewer scores the result, and a low score sends the edit
        back for another pass before you see it. Canva has AI features of its own. The pages that
        describe its AI video tools are among those we could not read, and we have not tested
        them, so we make no claim about what Canva does with raw footage or about which product
        cuts better. What we can state is what ReelBolt is: a brief and a run, with an editor for
        afterwards.
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
        <strong>Recipes for the job.</strong> You pick what you want to end up with, and the
        recipe’s page asks only for what that needs and tells you when something is missing. One
        recipe makes the highlight edit. Others add titles and callouts, write and voice a narration
        with captions, burn in subtitles and produce the SRT and VTT files, translate the subtitles,
        or dub the video without lip sync. The run page has no download button for the subtitle
        files yet, which is one of the rough edges. One makes a cover picture for very few credits. You
        choose the shape before each run: landscape, vertical, square or portrait. A finished video
        cannot be re-cut to another shape yet. Music and sound effects come from files you upload,
        because there is no built-in music library.
      </p>
      <p>
        <strong>The first cut is a starting point, and you can change it.</strong> There are two
        ways. On the recipe’s page, a finished video lets you change a setting, such as the length
        or your notes, and make it again. The parts your change does not touch are kept from the
        first run at no cost, and the first video is kept too. Or open the cut in the editor and
        trim, reorder or add text by hand. The shape, the resolution and the style cannot be
        changed that way, so for those you run the recipe again.
      </p>
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
        <strong>Hand-off to a professional editor.</strong> ReelBolt exports the cut as OTIO, EDL
        and Final Cut XML, and can prepare a hand-off package with the cut and your video files.
        That package is an Early preview, and we have not yet opened one in Premiere Pro, DaVinci
        Resolve or Final Cut Pro. The export formats in Canva’s developer documentation are
        finished files: MP4, GIF, PDF, PPTX, PNG, JPG, CSV and HTML <Cite n={[6]} />. We found no
        editing timeline format in that list. We could not read the help pages for downloads from
        Canva’s own editor, so treat this as what the API offers and not as the whole picture.
      </p>
      <p>
        <strong>Automation aimed at the edit.</strong> Canva’s automation fills in templates and
        exports designs, video fields and MP4 included <Cite n={[7, 9, 10]} />. None of the tools in
        its list for AI assistants is specific to cutting or generating video <Cite n={[9]} />. A
        ReelBolt API call starts a run of a recipe, and a webhook tells your system when the run
        ends. It is alpha, and it is part of the paid plans.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, some ReelBolt recipes
        make a promo video from an app’s source code. The footage recipes described here need no
        code.
      </p>

      <h2 id="pricing-approach">Plans and credits</h2>
      <p>
        We could not read Canva’s pricing page, so this part is short. Canva’s developer
        documentation refers to a Free plan and marks some tools as “Pro and above”{' '}
        <Cite n={[9]} />. Its autofill guide names Pro, Teams and Enterprise <Cite n={[7]} />, and
        its App Store listing lists purchases named Canva Pro, Canva Business, Canva Teams and
        Canva Credit <Cite n={[1]} />. The developer changelog also mentions AI credit quotas{' '}
        <Cite n={[11]} />. We do not describe what each plan includes, because we could not check
        it. Current plans and figures are on{' '}
        <a href="https://www.canva.com/pricing/" rel="nofollow noopener" target="_blank">
          Canva’s pricing page<span className="sr-only"> (opens in a new tab)</span>
        </a>
        .
      </p>
      <p>
        ReelBolt plans are credit based, and no card is required to start. A trial workspace has{' '}
        {TRIAL_CREDITS} one-time credits. When you verify your email, the account also receives a
        one-time grant of {FIRST_VIDEO_GRANT_CREDITS} credits, which expires{' '}
        {FIRST_VIDEO_GRANT_EXPIRY} after it is granted. Trial exports are {TRIAL_RESOLUTION} with a
        watermark. The paid plans are Creator, Pro, Team and Enterprise. Creator exports up to{' '}
        {CREATOR_RESOLUTION} with no watermark and Pro up to {PRO_RESOLUTION}. Top-up packs are for
        paid plans and expire after {catalog('topUp.expiresAfterMonths')} months. The{' '}
        <Link href="/pricing">pricing page</Link> has the current prices and what each plan
        includes.
      </p>
      <p>
        A credit buys something different at each company, so comparing one tool’s credit with
        another’s will mislead you. Compare the cost of the video you actually need.
      </p>

      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <p>Pick Canva if any of these is true:</p>
      <ul>
        <li>You want one place for the graphics, slides or documents around the video, as well as the video.</li>
        <li>You want an app on your phone, tablet or computer.</li>
        <li>You want to edit the video yourself.</li>
        <li>Several people need to work on the same design at the same moment.</li>
        <li>You need many versions of one design, filled in from your own data.</li>
        <li>You are a K-12 teacher or student who qualifies for Canva’s education offer.</li>
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
        Someone who already makes everything in Canva and likes editing there has no reason to
        leave. A shop owner, a volunteer or a founder with a camera roll of clips and no editor is
        who ReelBolt is built for. Nothing stops you from using both. A ReelBolt run ends in an MP4
        file you can download, and Canva’s developer documentation describes uploading video files
        as assets <Cite n={[5]} />. We could not read the help page that lists Canva’s upload
        formats and size limits, so check it before you count on that.
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
          { id: 'source-1', label: 'Canva on the App Store (US)', url: 'https://apps.apple.com/us/app/canva-ai-photo-video-editor/id897446215', accessed: CHECKED },
          { id: 'source-2', label: 'Canva on Google Play (listing title only)', url: 'https://play.google.com/store/apps/details?id=com.canva.editor', accessed: CHECKED },
          { id: 'source-3', label: 'Canva developers, Connect APIs', url: 'https://www.canva.dev/docs/connect/', accessed: CHECKED },
          { id: 'source-4', label: 'Canva developers, design imports', url: 'https://www.canva.dev/docs/connect/api-reference/design-imports/', accessed: CHECKED },
          { id: 'source-5', label: 'Canva developers, create asset upload job', url: 'https://www.canva.dev/docs/connect/api-reference/assets/create-asset-upload-job/', accessed: CHECKED },
          { id: 'source-6', label: 'Canva developers, create design export job', url: 'https://www.canva.dev/docs/connect/api-reference/exports/create-design-export-job/', accessed: CHECKED },
          { id: 'source-7', label: 'Canva developers, autofill guide', url: 'https://www.canva.dev/docs/connect/autofill-guide/', accessed: CHECKED },
          { id: 'source-8', label: 'Canva developers, Canva MCP server', url: 'https://www.canva.dev/docs/apps/mcp/', accessed: CHECKED },
          { id: 'source-9', label: 'Canva developers, Canva MCP tools', url: 'https://www.canva.dev/docs/apps/mcp/tools/', accessed: CHECKED },
          { id: 'source-10', label: 'Canva developer blog, how to use Canva MCP in your AI application (27 October 2026)', url: 'https://www.canva.dev/blog/developers/canva-mcp-ga/', accessed: CHECKED },
          { id: 'source-11', label: 'Canva developers, Connect API changelog', url: 'https://www.canva.dev/docs/connect/changelog/', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Features checked on {CHECKED} from each vendor’s public pages; they may have changed. Canva’s pricing page and main website could not be read that day, so the Canva side rests on its app store listings and developer documentation. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
