import type { Faq } from '@/lib/faqs';

// Rendered on /open-source AND emitted as FAQPage structured data from this one array, so the
// visible answer and the indexed answer cannot drift apart.
export const OPEN_SOURCE_FAQS: Faq[] = [
  {
    question: 'Is it really free?',
    answer:
      'The software is free to run under AGPL-3.0. You pay for your own server and for the AI services you connect.',
  },
  {
    question: 'What is the difference from the hosted version?',
    answer:
      'Hosted means no setup, no provider accounts to open and support by plan. Self-hosting means you run everything yourself and get community help through GitHub issues.',
  },
  {
    question: 'Can I use it at work?',
    answer:
      'Yes. Running it for your own team needs nothing from us. The video render engine has its own licence, and you hold it for your install. A company above the free size limit needs a Company License from the engine’s authors. If you want to embed ReelBolt in a product you sell, or run a changed version as a service without publishing your changes, ask us about a commercial licence at legal@reelbolt.ai.',
  },
  {
    question: 'Is it ready for production?',
    answer:
      'It is an early alpha and this is the first open-source release, so expect rough edges and changes between versions. If you need something steady today, the hosted service is the safer path.',
  },
  {
    question: 'Can I contribute?',
    answer:
      'Yes. You sign a contributor licence agreement once, with a single sentence on your first pull request, and you keep the copyright on what you write. The contributing guide and the issue list on GitHub are the place to start.',
  },
];
