import type { JSX } from 'react';
import Link from 'next/link';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { Figure } from '@/components/blog/Figure';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';

import { Cite } from '@/components/blog/Cite';
export const meta: PostMeta = {
  slug: 'product-promo-video-tools-2026',
  title: 'Choosing a tool for product promo videos in 2026',
  description: 'Descript, Runway, HeyGen, invideo, Arcade and ReelBolt compared for product promo videos: what each starts from, where each is strongest, and how each prices.',
  date: '2026-10-17',
  updated: '2026-11-06',
  tags: ['comparisons'],
  readingMinutes: 7,
  unlisted: false,
  hero: { src: 'dashboard', alt: 'The ReelBolt dashboard home for a demo workspace.', width: 1280, height: 800 },
  faqs: [
    { q: 'Which of these tools work with Claude Code?', a: 'All six publish an MCP connection. HeyGen, invideo and Arcade name Claude Code on their pages, and ReelBolt’s MCP server connects Claude Code over OAuth. Descript and Runway name Claude more generally.' },
    { q: 'Which tools make AI avatars?', a: 'Descript, HeyGen and invideo offer AI avatars, and Runway offers Characters. ReelBolt makes avatars of real people, from their own pictures, short videos and voice recordings and with their recorded consent, for short generated clips; it has no stock avatars. We found none on Arcade’s pages.' },
    { q: 'Can I compare these tools on price per credit?', a: 'No. Each company defines a credit differently and spends it on different things. Price the video you actually need in each tool, using each company’s pricing page.' },
    { q: 'Is this comparison neutral?', a: 'We make ReelBolt, so we have an interest. To keep it checkable, every statement about the other tools comes from their own public pages, linked in the sources, and each tool’s strengths are listed.' },
  ],
};

export default function ProductPromoVideoTools2026(): JSX.Element {
  return (
    <>
      <p>This guide is for founders, product marketers and growth leads who need a product promo video without a production team. It covers five tools a buyer often weighs, Descript, Runway, HeyGen, invideo and Arcade, alongside ReelBolt, which we make. We picked the five because each is a strong example of one way to get a promo made; that choice is our judgment, not a market ranking. Every statement about another company’s product comes from that company’s own public pages, linked in the sources.</p>
      <p>The one-sentence answer: these tools start from different places (your recording, a prompt, an AI presenter, a captured demo or your product’s repository and footage), and the right pick depends on where your video starts.</p>
      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={'Six tools for product promo videos, from each company’s public pages on 2026-10-17'}
        columns={['ReelBolt', 'Descript', 'Runway', 'HeyGen', 'invideo', 'Arcade']}
        rows={[
      { label: 'Starts from', cells: ['Your footage, GitHub repo and brand kit', { text: <>Your recordings <Cite n={[1]} /></> }, { text: <>Prompts, images and clips <Cite n={[5]} /></> }, { text: <>Text, slides or a prompt <Cite n={[9]} /></> }, { text: <>Prompts and your footage <Cite n={[13, 14]} /></> }, { text: <>Your Arcade demos, a prompt and brand kit <Cite n={[18]} /></> }] },
      { label: 'Edits your own footage', cells: ['Yes: full timeline', { text: <>Yes: transcript editing <Cite n={[1]} /></> }, { text: <>Partly: generative edits <Cite n={[5]} /></> }, { text: <>Partly: text-based editor <Cite n={[9]} /></> }, { text: <>Yes: timeline <Cite n={[13]} /></> }, { text: <>Partly: chat or timeline <Cite n={[18]} /></> }] },
      { label: 'Generated video', cells: ['B-roll on Pro and up', { text: <>Yes: b-roll <Cite n={[1]} /></> }, { text: <>Yes: own and other companies’ models <Cite n={[4, 6]} /></> }, { text: <>Yes: Video Agent <Cite n={[9]} /></> }, { text: <>Yes: many models <Cite n={[13]} /></> }, { text: <>Yes: AI Video <Cite n={[18]} /></> }] },
      { label: 'Avatars or characters', cells: ['Yes: avatars of consenting real people, in short clips; no stock avatars', { text: <>Yes <Cite n={[1]} /></> }, { text: <>Partly: Characters <Cite n={[5]} /></> }, { text: <>Yes: core product <Cite n={[9]} /></> }, { text: <>Yes <Cite n={[13]} /></> }, 'Not found'] },
      { label: 'Brand kit', cells: ['Yes: imports design tokens', { text: <>Yes: Brand Studio <Cite n={[1]} /></> }, { text: <>Yes: Brand Kit on Pro <Cite n={[6]} /></> }, { text: <>Yes: paid plans <Cite n={[10]} /></> }, 'Not covered', { text: <>Yes <Cite n={[18]} /></> }] },
      { label: 'Team', cells: ['Team plan with roles', { text: <>Per person; SSO on Enterprise <Cite n={[2]} /></> }, { text: <>Team plan listed <Cite n={[6]} /></> }, { text: <>Per seat on Business <Cite n={[10]} /></> }, { text: <>Per seat, guest seats <Cite n={[14]} /></> }, { text: <>Per seat on Growth <Cite n={[19]} /></> }] },
      { label: 'Voice cloning', cells: ['Yes, with the speaker’s consent', { text: <>Yes, paid plans <Cite n={[2]} /></> }, { text: <>Yes, Pro and Max <Cite n={[6]} /></> }, { text: <>Yes <Cite n={[9]} /></> }, { text: <>Yes <Cite n={[13]} /></> }, { text: <>Through your own ElevenLabs account <Cite n={[25]} />; AI voiceover <Cite n={[17]} /></> }] },
      { label: 'MCP and Claude Code', cells: ['Yes, Claude Code over OAuth', { text: <>Yes, names Claude <Cite n={[3]} /></> }, { text: <>Yes, names Claude.ai and Desktop <Cite n={[7]} /></> }, { text: <>Yes, incl. Claude Code <Cite n={[12]} /></> }, { text: <>Yes, incl. Claude Code <Cite n={[15]} /></> }, { text: <>Yes, incl. Claude Code <Cite n={[20, 23]} /></> }] },
      { label: 'Public API', cells: ['No: outside access is through its MCP server', { text: <>Yes, paid plans <Cite n={[3]} /></> }, { text: <>Yes, developer API <Cite n={[8]} /></> }, { text: <>Yes, pay as you go <Cite n={[11]} /></> }, 'Not found', { text: <>Enterprise only <Cite n={[24]} /></> }] },
      { label: 'Top export', cells: ['Set by plan; see pricing', { text: <>4K from Creator tier <Cite n={[2]} /></> }, { text: <>4K upscaling from Standard <Cite n={[6]} /></> }, { text: <>Up to 4K from Pro <Cite n={[10]} /></> }, { text: <>4K own footage from Starter <Cite n={[14]} /></> }, { text: <>1080p default <Cite n={[21]} />; 4K on the AI Video Pro tier from Oct 19, 2026 <Cite n={[22]} /></> }] },
      { label: 'Pricing model', cells: ['Credits; free trial', { text: <>Per person, credits and media hours <Cite n={[2]} /></> }, { text: <>Credits; free plan <Cite n={[6]} /></> }, { text: <>Credits; free plan <Cite n={[10]} /></> }, { text: <>Credits per seat <Cite n={[14]} /></> }, { text: <>Credits by video length; per-seat Growth on Oct 17, 2026 <Cite n={[19]} />; AI Video Pro tier from Oct 19, 2026 <Cite n={[22]} /></> }] },
        ]}
      />
      <p>“Not found” means we looked for it on the company’s pages we checked and did not find it; it may still exist. “Not covered” means our sources did not address it.</p>
      <h2 id="what-each-tool-is-strongest-at">What each tool is strongest at</h2>
      <h3 id="descript">Descript</h3>
      <p>Descript is built around recording and editing through the transcript. It has a screen recorder and remote recording, removes filler words and retakes, and its AI co-editor turns a raw recording into a cut <Cite n={[1, 2]} />. It also offers AI avatars and eye-contact correction, and a public API for every paying user <Cite n={[1, 3]} />. Choose it when you are the one on camera or on the mic.</p>
      <h3 id="runway">Runway</h3>
      <p>Runway builds its own generative video models and also offers models from other companies in its suite <Cite n={[4, 6]} />. You can edit one frame and apply the change across a video, map your voice and expressions onto a character, and build videos through its Agent <Cite n={[5]} />. Its Max plan adds HDR and ProRes <Cite n={[6]} />. Choose it when most of your video does not exist yet.</p>
      <h3 id="heygen">HeyGen</h3>
      <p>HeyGen makes videos presented by AI avatars and digital twins, from text, images, audio or a PPT or PDF deck, and translates them with lip-sync <Cite n={[9]} />. Its remote MCP is available on all plans <Cite n={[12]} />. Choose it for presenter-led explainers and multilingual outreach.</p>
      <h3 id="invideo">invideo</h3>
      <p>invideo is an agentic timeline editor with access to many third-party image and video models, agents you can build or bring, and multiplayer editing <Cite n={[13]} />. Through its MCP you can export an editable project to Premiere Pro, Final Cut Pro or DaVinci Resolve <Cite n={[15]} />. Choose it when you want to direct the agents and the models yourself.</p>
      <h3 id="arcade">Arcade</h3>
      <p>Arcade makes on-brand product videos and interactive, clickable demos, with branching, CTAs, forms and analytics on its paid plans <Cite n={[17, 19]} />. AI Video turns your Arcade demos, a prompt and your brand kit into teasers, sizzle reels and product overviews <Cite n={[18]} />. Choose it when you want a demo buyers can click as well as a video.</p>
      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>ReelBolt starts from the product you are promoting. Connect a GitHub repository and branch, pick the files that matter, and the project updates when you push. The brand kit imports from design tokens, CSS, Tailwind, Storybook or a brand PDF.</p>
      <p>It then edits your real footage. It removes silences, and on Pro, Team and Enterprise an edit room of several AI editors and a director decides what to keep. Tracked screen inserts place your own video on a green, blue or magenta screen and follow it as it moves, and object tracking can blur, pixelate or fill details such as license plates. Generated b-roll fills gaps in the cut under a spend cap, and narration, pickups in a cloned voice the speaker has consented to, and dubs are written to fit the edit.</p>
      <Figure name="timeline-editor" alt={'The ReelBolt timeline editor with video and audio clips on separate tracks.'} width={1280} height={800} caption="Look at the separate video and audio tracks on the timeline." />
      <p>Every run shows a credit estimate before it starts, and the assistant shows the exact change and its cost before it acts. Claude Code connects to the same tools through ReelBolt’s MCP server.</p>
      <p>ReelBolt can also put a real person on screen as an avatar, made from their own pictures, short videos and voice recordings, with their recorded consent. The avatar appears in short generated clips, and a lower-cost mode animates a still picture of the person to say a short script, 5 to 15 seconds, in their own voice. Clips made with an avatar are marked as AI-generated. There are no stock avatars and no full-length presenter videos. Avatars come with Pro, Team and Enterprise, not with Creator or the free trial. Avatars are still being rolled out, so they may not be switched on in your workspace yet.</p>
      <p>ReelBolt has no interactive demos. Its dubs keep the speaker’s original mouth movement.</p>
      <h2 id="pricing-approach">Pricing approach</h2>
      <p>Every tool here uses credits in some form, and a credit means something different at each one, so compare the cost of the video you need.</p>
      <ul><li>Descript: free plan with a 720p watermarked export; paid plans per person per month with media hours and AI credits; 4K from the Creator tier. <a href="https://www.descript.com/pricing" rel="nofollow noopener" target="_blank">Pricing<span className="sr-only"> (opens in a new tab)</span></a> <Cite n={[2]} /></li><li>Runway: free plan with one-time credits; paid plans with monthly credits; watermark removal and 4K upscaling from Standard; voice cloning and MCP from Pro. <a href="https://runway.com/pricing" rel="nofollow noopener" target="_blank">Pricing<span className="sr-only"> (opens in a new tab)</span></a> <Cite n={[6]} /></li><li>HeyGen: free plan with a few short videos a month at up to 1080p; watermark removal from Creator; Business adds a per-seat fee; API billed separately. <a href="https://www.heygen.com/pricing" rel="nofollow noopener" target="_blank">Pricing<span className="sr-only"> (opens in a new tab)</span></a> <Cite n={[10, 11]} /></li><li>invideo: per-seat plans with monthly credits per seat, billed monthly or annually; unused credits do not roll over; its help center describes a free plan with a weekly credit quota. <a href="https://invideo.io/pricing" rel="nofollow noopener" target="_blank">Pricing<span className="sr-only"> (opens in a new tab)</span></a> <Cite n={[14, 16]} /></li><li>Arcade: on October 17, 2026, a free plan, a per-seat Growth plan that adds MP4 export and watermark removal, and Enterprise with the REST API; for AI Video, a new credit model with a Pro tier starts October 19, 2026. <a href="https://www.arcade.software/pricing" rel="nofollow noopener" target="_blank">Pricing<span className="sr-only"> (opens in a new tab)</span></a> <Cite n={[19, 22, 24]} /></li><li>ReelBolt: free trial with {catalog('trial.creditsOneTime')} one-time credits; Creator {catalog('creator.priceMonthly')}, Pro {catalog('pro.priceMonthly')} and Team {catalog('team.priceMonthly')}; generated b-roll from Pro. <Link href="/pricing">Pricing</Link></li></ul>
      <Figure name="billing-plans" alt={'The ReelBolt Plan and billing page with the plan cards side by side.'} width={1280} height={486} caption="Look at the plan cards side by side to compare what each plan includes." />
      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <ul><li>You record yourself or guests and like editing words in a transcript: Descript.</li><li>Your video is mostly scenes nobody has filmed: Runway.</li><li>You need a presenter, or the same video in many languages with lip-sync: HeyGen.</li><li>You want to choose the agents and models and finish in Premiere Pro or Resolve: invideo.</li><li>You want a clickable demo as well as a teaser video: Arcade.</li><li>You have screen recordings and product footage, a repository and a design system, and want them cut into an on-brand promo from Claude Code or the dashboard: ReelBolt.</li></ul>
      <SourceList
        sources={[
          { id: 'source-1', label: 'Descript, home page', url: 'https://www.descript.com/', accessed: '2026-10-17' },
          { id: 'source-2', label: 'Descript, pricing', url: 'https://www.descript.com/pricing', accessed: '2026-10-17' },
          { id: 'source-3', label: 'Descript, API', url: 'https://www.descript.com/api', accessed: '2026-10-17' },
          { id: 'source-4', label: 'Runway, home page', url: 'https://runway.com/', accessed: '2026-10-17' },
          { id: 'source-5', label: 'Runway, product', url: 'https://runway.com/product', accessed: '2026-10-17' },
          { id: 'source-6', label: 'Runway, pricing', url: 'https://runway.com/pricing', accessed: '2026-10-17' },
          { id: 'source-7', label: 'Runway, MCP', url: 'https://runway.com/mcp', accessed: '2026-10-17' },
          { id: 'source-8', label: 'Runway, API pricing documentation', url: 'https://docs.dev.runwayml.com/guides/pricing/', accessed: '2026-10-17' },
          { id: 'source-9', label: 'HeyGen, home page', url: 'https://www.heygen.com/', accessed: '2026-10-17' },
          { id: 'source-10', label: 'HeyGen, pricing', url: 'https://www.heygen.com/pricing', accessed: '2026-10-17' },
          { id: 'source-11', label: 'HeyGen, FAQ', url: 'https://www.heygen.com/faq', accessed: '2026-10-17' },
          { id: 'source-12', label: 'HeyGen, MCP overview', url: 'https://developers.heygen.com/mcp/overview', accessed: '2026-10-17' },
          { id: 'source-13', label: 'invideo, home page', url: 'https://invideo.io/', accessed: '2026-10-17' },
          { id: 'source-14', label: 'invideo, pricing', url: 'https://invideo.io/pricing', accessed: '2026-10-17' },
          { id: 'source-15', label: 'invideo, “Introducing invideo MCP” (published 2026-09-30)', url: 'https://invideo.io/news/introducing-invideo-mcp/', accessed: '2026-10-17' },
          { id: 'source-16', label: 'invideo help center, “Can I use invideo AI for free?”', url: 'https://help.invideo.io/en/articles/9380226-can-i-use-invideo-ai-for-free', accessed: '2026-10-17' },
          { id: 'source-17', label: 'Arcade, home page', url: 'https://www.arcade.software/', accessed: '2026-10-17' },
          { id: 'source-18', label: 'Arcade docs, “Welcome to AI Video”', url: 'https://docs.arcade.software/kb/ai-video/welcome-to-ai-video.md', accessed: '2026-10-17' },
          { id: 'source-19', label: 'Arcade, pricing', url: 'https://www.arcade.software/pricing', accessed: '2026-10-17' },
          { id: 'source-20', label: 'Arcade docs, MCP', url: 'https://docs.arcade.software/kb/interactive-demo/leverage/mcp.md', accessed: '2026-10-17' },
          { id: 'source-21', label: 'Arcade docs, sharing and exporting', url: 'https://docs.arcade.software/kb/ai-video/ai-video/sharing-and-exporting.md', accessed: '2026-10-17' },
          { id: 'source-22', label: 'Arcade docs, “Credit Guide (Starting October 2026)”', url: 'https://docs.arcade.software/kb/interactive-demo/admin/plans-and-ai-credits/credit-guide-starting-october-2026.md', accessed: '2026-10-17' },
          { id: 'source-23', label: 'Arcade docs, MCP supported clients', url: 'https://docs.arcade.software/kb/interactive-demo/leverage/mcp/supported-clients.md', accessed: '2026-10-17' },
          { id: 'source-24', label: 'Arcade docs, REST API', url: 'https://docs.arcade.software/kb/interactive-demo/leverage/advanced-features/rest-api.md', accessed: '2026-10-17' },
          { id: 'source-25', label: 'Arcade docs, “Audio”', url: 'https://docs.arcade.software/kb/interactive-demo/build/interactive-demo/edit/audio.md', accessed: '2026-10-17' },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on 2026-10-17 from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
