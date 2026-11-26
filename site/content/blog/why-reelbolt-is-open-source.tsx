import type { JSX } from "react";
import Link from "next/link";
import type { PostMeta } from "@/lib/blog/types";
import { Callout } from "@/components/blog/Callout";
import { productVersion, RELEASE_URL } from "@/lib/site-config";

const REPO = "https://github.com/ReelBolt/reelbolt";

export const meta: PostMeta = {
  slug: "why-reelbolt-is-open-source",
  title: "Why ReelBolt is open source",
  description:
    "ReelBolt is now open source under AGPL-3.0. Why I did it, what changes for hosted customers, how self-hosting works, and how to contribute.",
  date: "2026-11-26",
  tags: ["open-source"],
  readingMinutes: 6,
  faqs: [
    {
      q: "Is ReelBolt free to self-host?",
      a: "The software is free under AGPL-3.0. You pay for your own server and for the model providers you connect, and you bring your own provider keys.",
    },
    {
      q: "Does anything change for hosted customers?",
      a: "No. The hosted service at reelbolt.ai works the same way, and you can now read the code that runs it.",
    },
    {
      q: "Can I use ReelBolt inside a closed-source product?",
      a: "Not under AGPL-3.0. A commercial licence exists for that, for running a modified version as a service without publishing your changes, and for contractual terms. Write to legal@reelbolt.ai.",
    },
    {
      q: "Is the Remotion render engine open source?",
      a: "No. Remotion is source-available under its own licence. If you self-host, you are the Remotion licensee for your install.",
    },
  ],
};

export default function WhyReelBoltIsOpenSource(): JSX.Element {
  return (
    <>
      <p>
        ReelBolt&apos;s source code is now public, under the GNU Affero General
        Public License v3 (AGPL-3.0). The repository is at{" "}
        <a href={REPO}>github.com/ReelBolt/reelbolt</a>. I&apos;m Tommaso, I
        wrote it, and I maintain it. This post covers why I did it and what it
        means, depending on whether you use the hosted service, want to run your
        own, or want to help.
      </p>

      <p>
        One caveat first. This is the first open-source release, version{" "}
        <a href={RELEASE_URL}>{productVersion}</a>, and the repository is new.
        Expect rough edges.
      </p>

      <h2 id="why-i-made-it-open-source">Why I made it open source</h2>

      <p>
        ReelBolt works on your product footage, your brand assets and your
        model-provider keys. That is a lot to hand to software you cannot read.
        Open source lets you check what runs on your material instead of taking
        my word for it.
      </p>

      <p>
        It also means nothing is locked in. If the hosted service stops suiting
        you, or if I stop being a good steward of the project, the code and a
        way to run it are yours.
      </p>

      <p>
        And the hosted service is still the easy path. Most teams want a
        finished video, not a stack to operate, so they pay for the hosted plans
        and I keep working on the project. That is how it pays for itself. The
        software is free; running it for you is the product.
      </p>

      <h2 id="what-it-means-for-customers">
        What it means for hosted customers
      </h2>

      <p>
        Nothing changes. Your account, projects and plans work as before. See{" "}
        <Link href="/pricing">the pricing page</Link> for what each plan
        includes.
      </p>

      <p>
        What you gain is the right to read the code behind the service, and a
        way out. If you ever want to leave, you can run the same codebase
        yourself.
      </p>

      <h2 id="for-self-hosters">For self-hosters</h2>

      <p>
        You get the full codebase and a Docker Compose setup that brings up the
        dashboard, the video pipeline and the render pipeline on one machine.
        The{" "}
        <a href={`${REPO}/blob/main/docs/user-guide/self-hosting.md`}>
          self-hosting guide
        </a>{" "}
        takes you from a fresh clone to a first workflow run, and the{" "}
        <a href="/docs/">user guide</a> covers the product itself.
      </p>

      <p>
        You bring your own model-provider keys and pay each provider directly.
        ReelBolt does not charge you, and the paid plans, credits and billing
        belong to the hosted service. You also run the stack yourself: sizing
        it, securing it, updating it and backing it up.
      </p>

      <p>
        In plain words, you need a machine with Docker and the Docker Compose
        plugin, a fair amount of memory and disk, and patience for a first build
        that compiles every service. The guide lists the exact requirements, so
        I won&apos;t repeat numbers here that may go stale.
      </p>

      <p>
        Support for self-hosted installs comes from the community and from me,
        on a best-effort basis, through GitHub issues. Given the alpha label,
        you will probably hit something that needs fixing. Please report it.
      </p>

      <Callout title="AGPL-3.0 in one paragraph">
        <p>
          You can run ReelBolt for yourself or your team on your own server, and
          nothing has to be published. If you change it and let other people use
          your changed version over a network, for example as a service, the
          AGPL requires you to offer those users the source of your changed
          version under the same licence. That network clause is the main
          difference from the plain GPL. It is not legal advice, so read the
          licence text if your case is close to the line.
        </p>
      </Callout>

      <Callout title="Remotion is not open source">
        <p>
          ReelBolt renders video with Remotion, which is source-available under
          its own licence. It is not open source, and the AGPL does not cover
          it. When you self-host, you are the Remotion licensee for your
          installation. Its free licence covers individuals, for-profit
          organisations of up to three people, non-profits and evaluators;
          larger organisations need a Company License. The Remotion team has confirmed ReelBolt’s own use of Remotion. Read the terms at{" "}
          <a href="https://www.remotion.dev/docs/license">
            remotion.dev/docs/license
          </a>{" "}
          before you install.
        </p>
      </Callout>

      <h2 id="for-contributors">For contributors</h2>

      <p>
        Pull requests are welcome. Start with{" "}
        <a href={`${REPO}/blob/main/CONTRIBUTING.md`}>CONTRIBUTING.md</a>,
        then pick up an issue or open one to discuss an idea before you write
        much code.
      </p>

      <p>
        Contributions need a Contributor License Agreement. The{" "}
        <a href={`${REPO}/blob/main/CLA.md`}>CLA</a> has a plain-language
        summary at the top. You sign once, by posting one sentence on your first
        pull request. You keep the copyright in your work. You give me the right
        to license it under the AGPL and also under a commercial licence, which
        is what lets the project offer both.
      </p>

      <p>
        In return, the CLA contains a promise: the public release will stay
        under AGPL-3.0, or under an OSI-approved licence that is at least as
        open. A commercial licence on top of that does not take the public one
        away.
      </p>

      <h2 id="the-projects-future">The project&apos;s future</h2>

      <p>
        What is open stays open. The commercial licence is for people who cannot
        use the AGPL: those embedding ReelBolt in a closed product, running a
        modified version as a service without publishing their changes, or
        needing contractual terms such as a warranty, indemnity or support. If
        that is you, write to{" "}
        <a href="mailto:legal@reelbolt.ai">legal@reelbolt.ai</a>. The{" "}
        <a href={`${REPO}/blob/main/COMMERCIAL.md`}>COMMERCIAL.md</a> file
        explains the details.
      </p>

      <p>
        The name and logo are my trademarks. The{" "}
        <a href={`${REPO}/blob/main/TRADEMARK.md`}>trademark policy</a> is
        short: if you fork, rename your fork. Saying that you run ReelBolt is
        fine.
      </p>

      <p>
        I won&apos;t publish roadmap dates. This is the first open-source release, the
        repository is new, and I would rather ship something and tell you than
        promise something and slip. What I can say is what I will not do: I
        won&apos;t close the public release or move it to a more restrictive
        licence.
      </p>

      <p>
        The code is at <a href={REPO}>github.com/ReelBolt/reelbolt</a>. If you
        would rather not run it yourself, the hosted service is at{" "}
        <Link href="/">reelbolt.ai</Link>, and the{" "}
        <Link href="/open-source">open-source page</Link> has the short version
        of all this.
      </p>
    </>
  );
}
