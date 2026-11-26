import { FAILED_RUN_POLICY } from './legal-placeholders.ts';

export interface Faq {
  question: string;
  answer: string;
}

// Rendered on the homepage AND emitted as FAQPage structured data from the same array, so the
// visible answer and the answer Google indexes can never drift apart — Google treats FAQ markup
// that isn't visible on the page as a violation, so these must stay a single source.
export const HOME_FAQS: Faq[] = [
  {
    question: 'What does ReelBolt actually make?',
    answer:
      'Finished short videos from what you already have: highlight edits from raw footage, narrated stories, captioned clips, testimonial reels and, for software teams, promo videos made from the product itself. You get an exported video file ready to publish, and you can open it in the editor first if you want to change anything.',
  },
  {
    question: 'Who is ReelBolt for?',
    answer:
      'Anyone who has a story to tell and no time to edit it: creators, small businesses, educators, nonprofits, software teams and agencies. You do not need editing skills or design skills. If you can film a few clips on a phone and say what the video is for, you can use it.',
  },
  {
    question: 'Do I need to give it footage, or does it create the video from scratch?',
    answer:
      'Most videos start from footage you already shot, which ReelBolt edits into a finished cut. Software teams can also start from their product’s code, with no footage at all. On the plans that include generated video, it can add clips and b-roll you never shot. On the free trial, no recipe makes a video from a text prompt alone or from photos alone.',
  },
  {
    question: 'Do I need to know how to edit video?',
    answer:
      'No. You describe what you want and the agents do the scripting, cutting, narration, titles and grading. If you like editing, every finished video opens as a timeline you can change yourself, and that part is optional.',
  },
  {
    question: 'How is this different from a template-based video maker?',
    answer:
      'Template tools drop your logo into somebody else’s animation. ReelBolt builds the video from your own footage, your own words and your brand kit, so what viewers see is your story and not a stock one.',
  },
  {
    question: 'Who reviews the quality?',
    answer:
      'A reviewer agent scores every draft against the brief before you see it, and drafts that fall short go back through production. On the plans with editing rooms, several AI editors and a director also weigh the big decisions before the cut is made.',
  },
  {
    question: 'Where does my data live, and which AI models are used?',
    answer:
      'On the hosted service, your projects, footage and finished videos stay in your workspace, and the AI services only receive what a job needs. On the plans that include it, you can connect your own AI provider account and choose which model each agent uses. The code is open source, so you can also run the whole platform on your own infrastructure.',
  },
  {
    question: 'Can it add a voiceover?',
    answer:
      'Yes. ReelBolt writes narration to fit the cut and speaks it in a voice you choose, or in a cloned voice once the speaker has recorded their consent. It can add subtitles and captions, translate the narration for dubs, and fix a flubbed line with a pickup in the cloned voice. The mouth movement on camera is not changed.',
  },
  {
    question: 'Can it generate footage I do not have, and what does that cost?',
    answer:
      'Yes, on the plans that include generated video. A shot director plans short clips such as cutaways, a cold open or an end card, and they are generated under a spending cap you set. Generated video uses credits, and the estimate shows the most it can cost before you start.',
  },
  {
    question: 'Can it blur license plates or other private details in my footage?',
    answer:
      'Yes. Name the object, such as a license plate, a badge or a screen, and ReelBolt finds it, follows it as the camera moves, and blurs, pixelates or fills it. Check the result before you publish, because no automatic tool can promise it caught every frame.',
  },
  {
    question: 'Can I change the cut myself?',
    answer:
      'Yes. Every finished video opens in the timeline editor, where you can trim, move clips, re-cut from the transcript, grade, mix the audio and add subtitles. You can also hand the edit to another editing app, or ask Assist for suggestions you accept or reject.',
  },
  {
    question: 'How long does one video take?',
    answer:
      'Minutes for a small job, sometimes half an hour for a big one. It depends on the length of your footage and how much you ask for. You can follow the progress of every run, and once a recipe works, running it again on new footage takes a click.',
  },
  {
    question: 'What can ReelBolt not do yet?',
    answer:
      'It is built for short videos, about a minute and a half or less. It does not make long presenter videos or image carousels, and it cannot yet re-cut a finished video into another shape. Several different shots make a real edit, while one long take can only be trimmed. ReelBolt is in public alpha, so expect rough edges.',
  },
];

// The pricing page's own set — rendered there and emitted as that page's FAQPage markup from this
// same array, for the same reason as HOME_FAQS above. Deliberately no numbers: every price, seat
// minimum and amount the reader sees is derived from `lib/pricing` at render time, and a figure
// typed into an answer here would be a second copy that drifts the moment the catalog changes.
// `lib/faqs.test.ts` asserts that no answer in this array contains a digit. See
// `docs/marketing-site.md`, "Structured data".
export const PRICING_FAQS: Faq[] = [
  {
    question: 'Is there a money-back guarantee?',
    answer:
      'Yes. If you are a new paying customer, you can ask for a full refund of your first subscription payment or of a credit top-up within two weeks of paying, by writing to support@reelbolt.ai. You get back the full amount you paid, with no deduction for credits you have used; the credits that payment granted are withdrawn, and there is one refund per customer. ' + FAILED_RUN_POLICY + ' The full wording is in the Terms.',
  },
  {
    question: 'What is the founder price?',
    answer:
      'During the public alpha, new subscribers get half off their plan. It is a promotional offer, open to new subscribers until the end of January. It is applied automatically at checkout, and there is no code to enter. Prices can change in future, with notice, as the Terms explain.',
  },
  {
    question: 'How do I know what a run will cost before it starts?',
    answer:
      'Before every run you see an estimate: about how many credits it will use, and usually a range from a low to a high figure. Once there are enough past runs at the resolution you picked, the range comes from them. Generated video is shown as an “up to” amount. You also see a separate amount your balance needs to start the run.',
  },
  {
    question: 'What uses credits, and what is free?',
    answer:
      'Credits pay for workflow steps, the AI tools in the picture editor, timeline suggestions, designed graphics, generated video, and assistant chat beyond your monthly allowance. File summaries, indexing and search, storage within your plan, brand imports and exports, work done with your own AI provider, and work done on your own paired machine are free. On per-seat plans, every seat adds its credits to one pool the workspace shares.',
  },
  {
    question: 'What happens if a run fails?',
    answer:
      FAILED_RUN_POLICY + ' If it fails because of the input, such as a file that can’t be read, the credits stay spent. You can ask the assistant why a run failed.',
  },
  {
    question: 'How does the output resolution change the cost?',
    answer:
      'You pick the resolution when you start a run, up to the highest your plan allows. Resolution is measured on the short side, so vertical and landscape videos are treated alike. Higher resolution uses more render credits and lower resolution uses fewer, and the estimate reflects the resolution you pick.',
  },
  {
    question: 'Can I cancel or change my plan?',
    answer:
      'Yes. Workspace owners and members with the billing role manage the plan from Plan and billing. Cancelling takes effect at the end of the period you have paid for, and you can undo it until then with Keep my plan. An upgrade takes effect once the payment goes through, with the price prorated, and the new credits arrive when the payment is confirmed. A downgrade starts at the next billing period. You can have one plan change waiting at a time.',
  },
  {
    question: 'How do promo codes and offers work?',
    answer:
      'Enter a code on the Plan and billing page in your workspace; you need a verified email address. For a discount code, the price you will pay comes from checkout and is shown before you confirm. Some codes add free credits instead. Credits from a promotion expire, and they are spent before your other credits. When an automatic offer is running, it shows as a badge on the plans it applies to.',
  },
  {
    question: 'What happens when I run out of credits?',
    answer:
      'The run is refused before it starts instead of failing halfway, and you see what it needed and what you have left. On a paid plan you can buy a credit pack from Plan and billing at any time; pack credits expire after the period shown on this page. Nothing tops up automatically.',
  },
  {
    question: 'Can I use my own AI provider or render on my own computer?',
    answer:
      'On the plans that include it, connect your own AI provider account and choose which model each agent uses. That usage costs no ReelBolt credits, because you pay the provider directly. Rendering and media work on a Windows or Linux machine you pair with your workspace costs no credits either, while model calls and billing stay on ReelBolt. The self-serve download for pairing a machine has not been released yet, so most runs today use ReelBolt’s capacity.',
  },
  {
    question: 'Is my footage used to train AI models?',
    answer:
      'No. We do not train models on your content, and we do not sell it. When ReelBolt uses its own AI capacity, only what a job needs is sent to the model provider for that job. If your material should stay with you, connect your own AI provider account, or run it yourself: the code is open source.',
  },
];

