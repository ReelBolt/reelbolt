import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import { docsHref, type Locale } from '@/i18n/config';
import { useT, useCurrentLocale } from '@/i18n';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { Button } from '@/components/ui/Button';
import { Panel } from '@/components/ui/Panel';
import { Reveal } from '@/components/motion/Reveal';
import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import { runnerDownloadAvailable, runnerRelease, type RunnerArtifact, type RunnerPlatform } from '@/lib/runner-downloads';
import { runnerFileName } from '@/lib/runner-latest';
import { CopyButton, DetectedDownload, type PrimaryDownload } from './RunnerClient';
import { PageMessages } from '@/components/layout/PageMessages';

// THE RULE THIS PAGE FOLLOWS
//
// Its download list comes from the published latest.json, fetched and validated at build time by
// `scripts/fetch-runner-latest.mjs` (see `lib/runner-downloads`). Nothing is filled by hand. If
// the fetch fails or the data is invalid the page ships its honest "not available yet" state,
// noindex and out of the sitemap (decision D25; H5 "No invented facts"). Everything shown live —
// version, date, size, checksum, URL — is a field of latest.json; the notes below state only what
// the repository's release pipeline does. The runner page may name Docker and ffmpeg (the granted
// exception in docs/marketing-site.md); it is kept minimal.
//
// No structured data: a `SoftwareApplication` assertion is not warranted by what we can prove.

const downloadAvailable = runnerDownloadAvailable();

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.runner.title'),
    description: downloadAvailable ? t('pages.runner.descriptionAvailable') : t('pages.runner.descriptionSoon'),
    path: siteConfig.runnerPath,
    // A placeholder page should not be indexed, and a route that is not in the sitemap should not
    // claim otherwise. The moment a real artifact is published, this and the sitemap entry both
    // turn on together.
    noIndex: !downloadAvailable,
  });
}

function RunnerNotReady() {
  const t = useT('runner.notReady');
  return (
    <Section>
      <Panel tone="paper-2" className="p-6">
        <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {t('label')}
        </p>
        <p className="mt-3 max-w-2xl text-ink-muted">
          {t('p1')}
        </p>
        <p className="mt-3 max-w-2xl text-ink-muted">
          {t('p2')}
        </p>
      </Panel>
    </Section>
  );
}

const ORDER: readonly RunnerPlatform[] = ['windows-x64', 'linux-x64', 'linux-arm64'];

function formatSize(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDate(iso: string, locale: string): string {
  return new Date(iso).toLocaleDateString(locale === 'en' ? 'en-GB' : locale, { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
}

function Verify({ artifact }: { artifact: RunnerArtifact }) {
  const t = useT('runner');
  const file = runnerFileName(artifact.url);
  const cmd =
    artifact.platform === 'windows-x64'
      ? `Get-FileHash -Algorithm SHA256 -LiteralPath '.\\${file}'`
      : `sha256sum -- '${file}'`;
  return (
    <div className="mt-3 space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <code className="break-all font-mono text-xs text-ink">{`SHA-256 ${artifact.sha256}`}</code>
        <CopyButton text={artifact.sha256} label={t('copySha', { label: artifact.label })} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <code className="break-all font-mono text-xs text-ink-muted">{cmd}</code>
        <CopyButton text={cmd} label={t('copyCmd', { label: artifact.label })} />
      </div>
    </div>
  );
}

function ArtifactRow({ artifact }: { artifact: RunnerArtifact }) {
  const t = useT('runner');
  return (
    <li className="py-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <p className="font-display text-base font-bold text-ink">{artifact.label}</p>
          <p className="mt-1 font-mono text-xs text-ink-muted">
            {t(`platform.${artifact.platform}`)} · {formatSize(artifact.size)}
          </p>
        </div>
        <Button href={artifact.url} variant="accent" className="sm:shrink-0">
          {t('download')}
        </Button>
      </div>
      <Verify artifact={artifact} />
    </li>
  );
}

function RunnerDownloads() {
  const t = useT('runner');
  const locale = useCurrentLocale();
  if (!runnerRelease) return null;
  const artifacts = ORDER.flatMap((p) => runnerRelease!.artifacts.filter((a) => a.platform === p));
  const primaries: PrimaryDownload[] = ORDER.flatMap((p) => {
    const list = artifacts.filter((a) => a.platform === p);
    const a = list.find((x) => x.recommended) ?? list[0];
    return a ? [{ platform: p, platformName: t(`platform.${p}`), url: a.url, label: a.label }] : [];
  });
  const platforms = new Set(artifacts.map((a) => a.platform));
  const sourceUrl = `https://downloads.reelbolt.ai/runner/source/${encodeURIComponent(runnerRelease.version)}/`;
  const publisher = process.env.RUNNER_PUBLISHER_NAME?.trim();
  return (
    <Section>
      <h2 className="font-display text-2xl font-bold text-ink">
        {t('downloads.title')}
      </h2>
      <p className="mt-2 font-mono text-sm text-ink-muted">
        {t('downloads.version', { version: runnerRelease.version })}
        {runnerRelease.publishedAt ? t('downloads.released', { date: formatDate(runnerRelease.publishedAt, locale) }) : ''}
      </p>
      <DetectedDownload primaries={primaries} />
      <h3 className="mt-8 font-display text-lg font-bold text-ink">{t('downloads.all')}</h3>
      <ul className="mt-2 divide-y divide-line border-y border-line">
        {artifacts.map((artifact) => (
          <ArtifactRow key={artifact.url} artifact={artifact} />
        ))}
      </ul>
      <p className="mt-4 max-w-2xl text-sm text-ink-muted">
        {t('downloads.checksum')}
      </p>

      <div className="mt-8 grid gap-4 md:grid-cols-2">
        {platforms.has('windows-x64') && (
          <Panel tone="paper-2" className="p-5">
            <h3 className="font-display text-base font-bold text-ink">{t('downloads.windowsTitle')}</h3>
            <p className="mt-2 text-sm text-ink-muted">
              {publisher
                ? t('downloads.signedBy', { publisher })
                : ''}
              {t('downloads.windows')}
            </p>
          </Panel>
        )}
        {(platforms.has('linux-x64') || platforms.has('linux-arm64')) && (
          <Panel tone="paper-2" className="p-5">
            <h3 className="font-display text-base font-bold text-ink">{t('downloads.linuxTitle')}</h3>
            <p className="mt-2 text-sm text-ink-muted">
              {t('downloads.linux')}
              </p>
          </Panel>
        )}
      </div>

      <div className="mt-8 max-w-2xl text-sm text-ink-muted">
        <h3 className="font-display text-base font-bold text-ink">{t('downloads.requirementsTitle')}</h3>
        <p className="mt-2">
          {t.rich('downloads.requirements', {
            guide: (chunks) => (
              <a href={docsHref(locale, 'runner/')} className="text-ink underline underline-offset-2">
                {chunks}
              </a>
            ),
          })}
        </p>
        <p className="mt-4">
          {t.rich('downloads.openSource', {
            source: (chunks) => (
              <a href={sourceUrl} className="text-ink underline underline-offset-2">
                {chunks}
              </a>
            ),
          })}
        </p>
      </div>
    </Section>
  );
}

function RunnerPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('runner.page');
  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
            {t('title')}
          </h1>
          <p className="mt-4 max-w-2xl font-sans text-sm leading-relaxed text-ink-muted">
            {downloadAvailable
              ? t('introAvailable')
              : t('introSoon')}
          </p>
        </Container>
      </div>

      <Reveal as="div">
        {downloadAvailable ? <RunnerDownloads /> : <RunnerNotReady />}
      </Reveal>

      <Reveal as="div">
        <Section className="bg-paper-2">
          <div className="text-center">
            <h2 className="font-display text-2xl font-bold text-ink sm:text-3xl">
              {t('cta.title')}
            </h2>
            <p className="mx-auto mt-3 max-w-xl font-mono text-sm text-ink-muted">
              {t('cta.body')}
            </p>
            <div className="mt-8 flex flex-col items-center justify-center gap-4 sm:flex-row">
              {/* Deliberately not PrimaryCta: that one follows the D25 switch between "Start free"
                  and the contact path, and there is nothing to start free on here. A runner
                  question is a conversation in both states. */}
              <Button href="/contact?topic=runner" variant="accent" size="lg" skew>
                {t('cta.talk')}
              </Button>
              <Button href="/features" variant="outline" size="lg">
                {t('cta.seeFeatures')}
              </Button>
            </div>
          </div>
        </Section>
      </Reveal>
    </>
  );
}

export function RunnerPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={['runner']}>
      <RunnerPageBody locale={locale} />
    </PageMessages>
  );
}
