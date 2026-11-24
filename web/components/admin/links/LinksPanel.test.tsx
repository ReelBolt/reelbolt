// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { renderWithI18n } from '@/test/i18n';
import type { MarketingLink, MarketingLinkFunnel, MarketingLinkFunnelItem } from '@/lib/types/marketing-links';

const H = vi.hoisted(() => ({ fetch: vi.fn(), show: vi.fn() }));
vi.stubGlobal('fetch', H.fetch);
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
vi.mock('@mantine/notifications', () => ({ notifications: { show: (a: unknown) => H.show(a) } }));

import { LinksPanel } from './LinksPanel';

const ORIGIN = window.location.origin;
const LINKS_URL = '/api/v1/marketing/admin/links';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const HN: MarketingLink = {
  id: 'id-hn',
  code: 'hn-launch',
  channel: 'hackernews',
  medium: 'community',
  campaign: 'launch-2026-11',
  placement: 'Show HN, first comment',
  destinationPath: '/for/software-teams',
  persona: 'software',
  costMinor: null,
  costCurrency: null,
  createdAt: '2026-11-10T09:00:00Z',
  archivedAt: null,
  shortPath: '/l/hn-launch',
  longPath: '/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software',
  totalClicks: 200,
};
const ADS: MarketingLink = {
  id: 'id-ads',
  code: 'yt-sponsor',
  channel: 'youtube',
  medium: 'paid',
  campaign: 'creators-q4',
  placement: 'Sponsored segment, editing channel',
  destinationPath: '/for/creators',
  persona: 'creator',
  costMinor: 30000,
  costCurrency: 'EUR',
  createdAt: '2026-11-09T09:00:00Z',
  archivedAt: null,
  shortPath: '/l/yt-sponsor',
  longPath: '/for/creators?utm_source=youtube&utm_medium=paid&utm_campaign=creators-q4&utm_content=yt-sponsor&for=creator',
  totalClicks: 100,
};
const OLD: MarketingLink = {
  ...HN,
  id: 'id-old',
  code: 'old-post',
  placement: 'A post from last year',
  campaign: 'archive',
  archivedAt: '2026-11-01T00:00:00Z',
  shortPath: '/l/old-post',
  longPath: '/?utm_content=old-post',
};

const funnelItem = (link: MarketingLink, counts: Partial<MarketingLinkFunnelItem>): MarketingLinkFunnelItem => ({
  linkId: link.id,
  code: link.code,
  channel: link.channel,
  medium: link.medium,
  campaign: link.campaign,
  placement: link.placement,
  destinationPath: link.destinationPath,
  archived: link.archivedAt !== null,
  costMinor: link.costMinor,
  costCurrency: link.costCurrency,
  clicks: 0,
  botHits: 0,
  visitors: 0,
  signups: 0,
  verified: 0,
  onboarded: 0,
  firstProject: 0,
  firstRun: 0,
  firstVideo: 0,
  paid: 0,
  ...counts,
});

const FUNNEL: MarketingLinkFunnel = {
  days: 30,
  generatedAt: '2026-11-11T08:00:00Z',
  urlCaptureEnabled: false,
  items: [
    funnelItem(HN, { clicks: 200, botHits: 12, visitors: 50, signups: 20, verified: 15, onboarded: 14, firstProject: 10, firstRun: 4, firstVideo: 3, paid: 0 }),
    funnelItem(ADS, { clicks: 100, visitors: 20, signups: 5, verified: 4, onboarded: 1, firstProject: 1, firstRun: 1, firstVideo: 1, paid: 1 }),
    funnelItem(OLD, { clicks: 7, signups: 1 }),
  ],
  unattributed: { signups: 8, verified: 6, onboarded: 2, firstProject: 2, firstRun: 1, firstVideo: 1, paid: 0 },
};

let links: MarketingLink[];
let funnel: MarketingLinkFunnel;
let funnelStatus: number;
let postResponse: (body: Record<string, unknown>) => Response;
let patchResponse: (id: string, body: Record<string, unknown>) => Response;
let writeText: ReturnType<typeof vi.fn>;

const realScrollIntoView = Element.prototype.scrollIntoView;
const realMatchMedia = window.matchMedia;

afterEach(() => {
  Element.prototype.scrollIntoView = realScrollIntoView;
  window.matchMedia = realMatchMedia;
});

beforeEach(() => {
  // Mantine's Combobox scrolls the selected option into view on a timer; jsdom has no scrollIntoView.
  Element.prototype.scrollIntoView = vi.fn();
  H.fetch.mockReset();
  H.show.mockReset();
  links = [HN, ADS, OLD];
  funnel = FUNNEL;
  funnelStatus = 200;
  postResponse = (body) =>
    json({
      ...HN,
      id: 'id-new',
      ...body,
      persona: body.persona ?? null,
      costMinor: body.costMinor ?? null,
      costCurrency: body.costCurrency ?? null,
      shortPath: `/l/${String(body.code)}`,
      longPath: `${String(body.destinationPath)}?utm_source=${String(body.channel)}&utm_medium=${String(body.medium)}&utm_campaign=${String(body.campaign)}&utm_content=${String(body.code)}`,
      totalClicks: 0,
    });
  patchResponse = (id, body) => json({ ...(links.find((link) => link.id === id) ?? HN), ...body });
  writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
  H.fetch.mockImplementation((url: string, init?: RequestInit) => {
    const body = init?.body ? (JSON.parse(init.body as string) as Record<string, unknown>) : {};
    if (url.startsWith(`${LINKS_URL}/funnel`)) return Promise.resolve(funnelStatus === 200 ? json(funnel) : json({}, funnelStatus));
    if (url === LINKS_URL && init?.method === 'POST') return Promise.resolve(postResponse(body));
    if (url === LINKS_URL) return Promise.resolve(json({ items: links }));
    if (url.startsWith(`${LINKS_URL}/`) && init?.method === 'PATCH') {
      return Promise.resolve(patchResponse(url.slice(LINKS_URL.length + 1), body));
    }
    return Promise.resolve(json({}, 404));
  });
});

const renderPanel = (locale = 'en') =>
  renderWithI18n(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <LinksPanel />
    </SWRConfig>,
    { locale },
  );

const calls = (method: string) =>
  H.fetch.mock.calls
    .filter((call) => ((call[1] as RequestInit | undefined)?.method ?? 'GET') === method)
    .map((call) => ({
      url: call[0] as string,
      body: (call[1] as RequestInit | undefined)?.body ? JSON.parse((call[1] as RequestInit).body as string) : undefined,
    }));

const row = (code: string) => screen.findByTestId(`link-row-${code}`);
const cell = (within_: HTMLElement, key: string) => within(within_).getByTestId(`cell-${key}`);

async function openMenu(code: string) {
  fireEvent.click(await screen.findByTestId(`link-actions-${code}`));
}

const change = (testId: string, value: string) => fireEvent.change(screen.getByTestId(testId), { target: { value } });

describe('LinksPanel: the table', () => {
  it('shows one row per link with where it is posted, its channel, campaign and landing page', async () => {
    renderPanel();
    const hn = await row('hn-launch');
    expect(within(hn).getByText('hn-launch')).toBeTruthy();
    expect(within(hn).getByText('Show HN, first comment')).toBeTruthy();
    expect(within(hn).getByText('Channel: Hacker News (Community)')).toBeTruthy();
    expect(within(hn).getByText('Campaign: launch-2026-11')).toBeTruthy();
    expect(within(hn).getByText('Landing page: /for/software-teams')).toBeTruthy();
    expect(within(hn).getByText('Audience: Software teams')).toBeTruthy();
    expect(within(hn).getByRole('rowheader')).toBeTruthy();
  });

  it('is a labelled table whose header groups the figures by who they count', async () => {
    renderPanel();
    const table = await screen.findByRole('table', { name: 'Tracked links and their funnel' });
    for (const name of ['Everyone', 'Accepted cookies', 'Accounts']) {
      expect(within(table).getByRole('columnheader', { name })).toBeTruthy();
    }
    for (const name of [
      'Link', 'Clicks', 'Visitors', 'Signups', 'Verified', 'Told us what they make', 'First project', 'First run',
      'First finished video', 'Paid', 'Cost', 'Cost per signup', 'Actions',
    ]) {
      expect(within(table).getByRole('columnheader', { name }), name).toBeTruthy();
    }
    expect(within(table).getByRole('columnheader', { name: 'Accounts' }).getAttribute('colspan')).toBe('7');
  });

  it('shows every count with its share: of clicks for visitors and signups, of the step before after that', async () => {
    renderPanel();
    const hn = await row('hn-launch');
    await waitFor(() => expect(cell(hn, 'clicks').textContent).toContain('200'));
    expect(cell(hn, 'clicks').textContent).toContain('+12 automated');
    expect(cell(hn, 'visitors').textContent).toBe('5025% of clicks');
    expect(cell(hn, 'signups').textContent).toBe('2010% of clicks');
    expect(cell(hn, 'verified').textContent).toBe('1575% of the step before');
    expect(cell(hn, 'onboarded').textContent).toBe('1493% of the step before');
    expect(cell(hn, 'firstProject').textContent).toBe('1071% of the step before');
    expect(cell(hn, 'firstVideo').textContent).toBe('375% of the step before');
    expect(cell(hn, 'paid').textContent).toBe('00% of the step before');
  });

  it('marks where most accounts stop, in the cell and in words under the link', async () => {
    renderPanel();
    const hn = await row('hn-launch');
    await waitFor(() => expect(cell(hn, 'firstRun').getAttribute('data-marked')).toBe('true'));
    // 10 had a project, 4 started a run: the largest loss of the row.
    expect(cell(hn, 'firstRun').textContent).toBe('440% of the step beforeMost accounts stop before this step');
    expect(within(hn).getAllByText('Most accounts stop before this step')).toHaveLength(1);
    expect(within(hn).getByTestId('stop-line').textContent).toBe('Most stop before a first run · 6 of 10 accounts');
    for (const key of ['signups', 'verified', 'onboarded', 'firstProject', 'firstVideo', 'paid']) {
      expect(cell(hn, key).getAttribute('data-marked'), key).toBeNull();
    }

    const ads = await row('yt-sponsor');
    expect(cell(ads, 'onboarded').getAttribute('data-marked')).toBe('true');
    expect(within(ads).getByTestId('stop-line').textContent).toBe('Most stop before telling us what they make · 3 of 4 accounts');
  });

  it('shows the cost and the cost per signup only where a cost is set', async () => {
    renderPanel();
    const ads = await row('yt-sponsor');
    await waitFor(() => expect(within(ads).getByTestId('cell-cost').textContent).toBe('€300.00'));
    // 300.00 over the 5 signups of the period.
    expect(within(ads).getByTestId('cell-cost-per-signup').textContent).toBe('€60.00');
    const hn = await row('hn-launch');
    expect(within(hn).getByTestId('cell-cost').textContent).toBe('—Does not apply');
    expect(within(hn).getByTestId('cell-cost-per-signup').textContent).toBe('—Does not apply');
  });

  // The decimals come from a fixed ISO 4217 table, not from the browser: 1,250,050 minor units of
  // forint are 12,500.50, whatever the browser would round a forint amount to.
  it('shows a cost with the decimals of its currency under ISO 4217', async () => {
    const withCost = (costMinor: number, costCurrency: string) => {
      links = [HN, { ...ADS, costMinor, costCurrency }, OLD];
      funnel = { ...FUNNEL, items: FUNNEL.items.map((item) => (item.linkId === ADS.id ? { ...item, costMinor, costCurrency } : item)) };
    };
    const shown = async () => {
      const view = renderPanel();
      const ads = await row('yt-sponsor');
      await waitFor(() => expect(within(ads).getByTestId('cell-cost').textContent).not.toBe(''));
      const text = [within(ads).getByTestId('cell-cost').textContent, within(ads).getByTestId('cell-cost-per-signup').textContent];
      view.unmount();
      return text.map((value) => (value ?? '').replace(/\s/g, ' '));
    };
    withCost(1250050, 'HUF');
    expect(await shown()).toEqual(['HUF 12,500.50', 'HUF 2,500.10']);
    withCost(50000, 'JPY');
    expect(await shown()).toEqual(['¥50,000', '¥10,000']);
    withCost(12345, 'KWD');
    expect(await shown()).toEqual(['KWD 12.345', 'KWD 2.469']);
  });

  it('has a totals row over the links shown and a row for the accounts with no link', async () => {
    renderPanel();
    const total = await screen.findByTestId('link-row-total');
    expect(within(total).getByText('Total of 2 links')).toBeTruthy();
    expect(cell(total, 'clicks').textContent).toContain('300');
    expect(cell(total, 'visitors').textContent).toBe('7023% of clicks');
    expect(cell(total, 'signups').textContent).toBe('258.3% of clicks');
    // 25 signed up, 19 verified: the largest loss over both links (a tie with first project, the earlier step wins).
    expect(cell(total, 'verified').textContent).toBe('1976% of the step beforeMost accounts stop before this step');
    expect(cell(total, 'firstRun').textContent).toBe('545% of the step before');
    expect(cell(total, 'paid').textContent).toBe('125% of the step before');
    expect(within(total).getByTestId('cell-cost').textContent).toBe('€300.00');
    expect(within(total).getByTestId('cell-cost-per-signup').textContent).toBe('€60.00links with a cost');

    const unattributed = screen.getByTestId('link-row-unattributed');
    expect(within(unattributed).getByText('Unattributed accounts')).toBeTruthy();
    // Every account of the period is either on a link row or here: a campaign value that is no
    // link's code is counted here too, so the rows add up.
    expect(
      within(unattributed).getByText(
        'Accounts created in the period that belong to no link: no link code, or a campaign value that matches no link. With the link rows, this is every account of the period.',
      ),
    ).toBeTruthy();
    expect(cell(unattributed, 'clicks').textContent).toBe('—Does not apply');
    expect(cell(unattributed, 'visitors').textContent).toBe('—Does not apply');
    expect(cell(unattributed, 'signups').textContent).toBe('8');
    expect(cell(unattributed, 'verified').textContent).toBe('675% of the step before');
    expect(cell(unattributed, 'onboarded').getAttribute('data-marked')).toBe('true');
  });

  it('keeps archived links out until asked, and then counts them in the totals', async () => {
    renderPanel();
    await row('hn-launch');
    expect(screen.queryByTestId('link-row-old-post')).toBeNull();
    fireEvent.click(screen.getByTestId('links-show-archived'));
    const old = await row('old-post');
    expect(within(old).getByText('Archived')).toBeTruthy();
    expect(within(screen.getByTestId('link-row-total')).getByText('Total of 3 links')).toBeTruthy();
    expect(cell(screen.getByTestId('link-row-total'), 'clicks').textContent).toContain('307');
  });

  it('reads the funnel again for the period that is chosen', async () => {
    renderPanel();
    await row('hn-launch');
    expect(calls('GET').map((c) => c.url)).toContain(`${LINKS_URL}/funnel?days=30`);
    const period = screen.getByTestId('links-period') as HTMLSelectElement;
    expect([...period.options].map((option) => option.textContent)).toEqual(['Last 7 days', 'Last 30 days', 'Last 90 days', 'Last 365 days']);
    fireEvent.change(period, { target: { value: '7' } });
    await waitFor(() => expect(calls('GET').map((c) => c.url)).toContain(`${LINKS_URL}/funnel?days=7`));
    fireEvent.change(period, { target: { value: '365' } });
    await waitFor(() => expect(calls('GET').map((c) => c.url)).toContain(`${LINKS_URL}/funnel?days=365`));
  });

  // A link's cost is for its whole life. Divided by the signups of a shorter period, the same
  // placement read cheap at 30 days and expensive at 7: the figure is only shown when the period
  // reaches back to the link's creation, and says why when it does not.
  it('shows cost per signup only when the period covers the link\'s whole life', async () => {
    // The sponsored link was created two days before the figures were read; this period is one day.
    funnel = { ...FUNNEL, days: 1 };
    renderPanel();
    const ads = await row('yt-sponsor');
    expect(within(ads).getByTestId('cell-cost').textContent).toBe('€300.00');
    expect(within(ads).getByTestId('cell-cost-per-signup').textContent).toBe("Choose a period that covers the link's whole life");
    expect(within(ads).getByTestId('cell-cost-per-signup').textContent).not.toContain('€');
    // The totals row follows: its cost is for signups this period does not show.
    const total = screen.getByTestId('link-row-total');
    expect(within(total).getByTestId('cell-cost').textContent).toBe('€300.00');
    expect(within(total).getByTestId('cell-cost-per-signup').textContent).toBe('—Does not apply');
    expect(screen.getByTestId('links-legend').textContent).toContain('shown only when the period reaches back to the day the link was created');
  });

  it('lists the links without counts when the funnel cannot be read, never as zeros', async () => {
    funnelStatus = 500;
    renderPanel();
    const hn = await row('hn-launch');
    expect(await screen.findByTestId('links-funnel-error')).toBeTruthy();
    expect(cell(hn, 'clicks').textContent).toBe('—No figure');
    expect(cell(hn, 'signups').textContent).toBe('—No figure');
    expect(within(hn).queryByTestId('stop-line')).toBeNull();
    expect(screen.queryByTestId('link-row-total')).toBeNull();
    expect(screen.queryByTestId('link-row-unattributed')).toBeNull();
    // The link's own cost is still known; the cost per signup is not.
    const ads = await row('yt-sponsor');
    expect(within(ads).getByTestId('cell-cost').textContent).toBe('€300.00');
    expect(within(ads).getByTestId('cell-cost-per-signup').textContent).toBe('—Does not apply');
  });

  it('shows an empty state with the way to create the first link', async () => {
    links = [];
    funnel = { ...FUNNEL, items: [] };
    renderPanel();
    expect(await screen.findByText('No links yet')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    fireEvent.click(screen.getByTestId('new-link'));
    expect(await screen.findByTestId('new-link-form')).toBeTruthy();
  });

  it('says so when the links cannot be loaded', async () => {
    H.fetch.mockImplementation(() => Promise.resolve(json({ message: 'Forbidden' }, 403)));
    renderPanel();
    expect(await screen.findByTestId('links-error')).toBeTruthy();
    expect(screen.getByText('Could not load the links')).toBeTruthy();
  });

  it('lays the same figures out as one card per link on a phone', async () => {
    window.matchMedia = ((query: string) => ({
      matches: query.includes('max-width: 48em'),
      media: query,
      onchange: null,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    renderPanel();
    expect(await screen.findByTestId('links-cards')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    const hn = await row('hn-launch');
    await waitFor(() => expect(cell(hn, 'clicks').textContent).toContain('Clicks200'));
    expect(cell(hn, 'firstRun').textContent).toBe('First run440% of the step beforeMost accounts stop before this step');
    expect(cell(hn, 'firstRun').getAttribute('data-marked')).toBe('true');
    expect(within(hn).getByTestId('stop-line').textContent).toContain('Most stop before a first run');
    expect(within(hn).getByTestId('link-actions-hn-launch')).toBeTruthy();
    expect(screen.getByTestId('link-row-total')).toBeTruthy();
    const unattributed = screen.getByTestId('link-row-unattributed');
    expect(within(unattributed).queryByTestId('cell-clicks')).toBeNull();
    expect(cell(unattributed, 'signups').textContent).toBe('Signups8');
  });
});

describe('LinksPanel: the populations note', () => {
  it('explains the three populations in plain words', async () => {
    renderPanel();
    const note = await screen.findByTestId('links-note');
    // Clicks are openings, not people: a repeat click and a mail scanner each count.
    const clicks = within(note).getByText(/^Clicks count the times the short link was opened, not unique people/);
    expect(clicks.textContent).toContain('No cookie consent is needed');
    expect(clicks.textContent).toContain('clicks from email run high');
    expect(note.textContent).not.toContain('count everyone who followed');
    expect(within(note).getByText(/^Visitors are only the people who accepted cookies on the site/)).toBeTruthy();
    expect(within(note).getByText(/^Signups and every step after them are accounts, counted on the server/)).toBeTruthy();
  });

  it('says so when this server counts no visitors at all, and says nothing when the API does not report it', async () => {
    renderPanel();
    await screen.findByTestId('links-note');
    expect(screen.queryByTestId('links-note-visits-off')).toBeNull();
    cleanup();
    funnel = { ...FUNNEL, visitIngestEnabled: false };
    renderPanel();
    const off = await screen.findByTestId('links-note-visits-off');
    expect(off.textContent).toContain('TELEMETRY_VISIT_INGEST_ENABLED');
    expect(off.textContent).toContain('Visitors reads 0 for every link');
    cleanup();
    funnel = { ...FUNNEL, visitIngestEnabled: true };
    renderPanel();
    await screen.findByTestId('links-note');
    expect(screen.queryByTestId('links-note-visits-off')).toBeNull();
  });

  it('says account counts per link are a minimum while URL capture is off', async () => {
    renderPanel();
    const off = await screen.findByTestId('links-note-capture-off');
    expect(off.textContent).toContain('URL capture is off');
    expect(off.textContent).toContain('the account figures per link are a minimum');
    expect(off.textContent).toContain('ATTRIBUTION_URL_CAPTURE_ENABLED');
    expect(screen.queryByTestId('links-note-capture-on')).toBeNull();
  });

  it('says accounts are counted on their link without a cookie while URL capture is on', async () => {
    funnel = { ...FUNNEL, urlCaptureEnabled: true };
    renderPanel();
    const on = await screen.findByTestId('links-note-capture-on');
    expect(on.textContent).toContain('URL capture is on');
    expect(on.textContent).toContain('without a cookie');
    expect(screen.queryByTestId('links-note-capture-off')).toBeNull();
  });

  it('claims neither while the setting is unknown', async () => {
    funnelStatus = 500;
    renderPanel();
    await screen.findByTestId('links-funnel-error');
    expect(screen.queryByTestId('links-note-capture-on')).toBeNull();
    expect(screen.queryByTestId('links-note-capture-off')).toBeNull();
  });
});

describe('LinksPanel: row actions', () => {
  it('copies the short address of a link', async () => {
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Copy short link' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(`${ORIGIN}/l/hn-launch`));
    await waitFor(() => expect(H.show).toHaveBeenCalledWith(expect.objectContaining({ message: 'Short link copied' })));
    expect(writeText).toHaveBeenCalledTimes(1);
  });

  it('copies the long address of a link', async () => {
    renderPanel();
    await openMenu('yt-sponsor');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Copy long link' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(`${ORIGIN}${ADS.longPath}`));
    await waitFor(() => expect(H.show).toHaveBeenCalledWith(expect.objectContaining({ message: 'Long link copied' })));
    expect(writeText).toHaveBeenCalledTimes(1);
  });

  it('shows both addresses to copy by hand when the browser refuses the clipboard', async () => {
    writeText.mockRejectedValue(new Error('denied'));
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Copy short link' }));
    const fallback = await screen.findByTestId('link-urls-fallback');
    expect(within(fallback).getByText('Could not copy. Select the link below and copy it by hand.')).toBeTruthy();
    expect(within(fallback).getByTestId('link-short-url').textContent).toBe(`${ORIGIN}/l/hn-launch`);
    expect(within(fallback).getByTestId('link-long-url').textContent).toBe(`${ORIGIN}${HN.longPath}`);
    expect(H.show).not.toHaveBeenCalled();
  });

  it('archives after a confirm that says what archiving does', async () => {
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));
    const confirm = await screen.findByTestId('link-archive-confirm');
    expect(screen.getByText('Archive hn-launch?')).toBeTruthy();
    expect(confirm.textContent).toContain('it sends people to the home page and stops counting clicks');
    expect(confirm.textContent).toContain('the code is never reused');
    expect(confirm.textContent).toContain('You can unarchive it at any time');
    expect(calls('PATCH')).toEqual([]);

    const reads = calls('GET').length;
    fireEvent.click(screen.getByTestId('link-archive-confirm-button'));
    await waitFor(() => expect(calls('PATCH')).toEqual([{ url: `${LINKS_URL}/id-hn`, body: { archived: true } }]));
    await waitFor(() => expect(H.show).toHaveBeenCalledWith(expect.objectContaining({ message: 'Link archived' })));
    // The list and the funnel are read again, so the row comes from the server.
    await waitFor(() => expect(calls('GET').length).toBeGreaterThanOrEqual(reads + 2));
    await waitFor(() => expect(screen.queryByTestId('link-archive-confirm')).toBeNull());
  });

  it('does not archive when the confirm is cancelled', async () => {
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));
    const confirm = await screen.findByTestId('link-archive-confirm');
    fireEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByTestId('link-archive-confirm')).toBeNull());
    expect(calls('PATCH')).toEqual([]);
  });

  it('unarchives an archived link', async () => {
    renderPanel();
    await row('hn-launch');
    fireEvent.click(screen.getByTestId('links-show-archived'));
    await openMenu('old-post');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Unarchive' }));
    const confirm = await screen.findByTestId('link-archive-confirm');
    expect(confirm.textContent).toContain('The short link sends people to /for/software-teams again and counts clicks again.');
    fireEvent.click(screen.getByTestId('link-archive-confirm-button'));
    await waitFor(() => expect(calls('PATCH')).toEqual([{ url: `${LINKS_URL}/id-old`, body: { archived: false } }]));
    await waitFor(() => expect(H.show).toHaveBeenCalledWith(expect.objectContaining({ message: 'Link unarchived' })));
  });

  it('keeps the confirm open and says so when archiving fails', async () => {
    patchResponse = () => json({ error: 'invalid_body' }, 400);
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));
    await screen.findByTestId('link-archive-confirm');
    fireEvent.click(screen.getByTestId('link-archive-confirm-button'));
    await waitFor(() =>
      expect(H.show).toHaveBeenCalledWith(expect.objectContaining({ message: 'Could not archive the link. Please try again.', color: 'red' })),
    );
    expect(screen.getByTestId('link-archive-confirm')).toBeTruthy();
  });

  it('edits the placement and sends only what changed', async () => {
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByTestId('edit-link-form');
    expect(screen.getByText('Edit hn-launch')).toBeTruthy();
    expect(screen.getByTestId('edit-link-fixed').textContent).toBe('Fixed for this link: Hacker News (Community), campaign launch-2026-11.');
    expect((screen.getByTestId('link-placement') as HTMLInputElement).value).toBe('Show HN, first comment');
    expect((screen.getByTestId('link-landing') as HTMLSelectElement).value).toBe('/for/software-teams');
    expect((screen.getByTestId('link-persona') as HTMLSelectElement).value).toBe('software');
    // Nothing changed yet: nothing to save.
    expect((screen.getByTestId('edit-link-save') as HTMLButtonElement).disabled).toBe(true);

    change('link-placement', 'Show HN, second comment');
    fireEvent.click(screen.getByTestId('edit-link-save'));
    await waitFor(() =>
      expect(calls('PATCH')).toEqual([{ url: `${LINKS_URL}/id-hn`, body: { placement: 'Show HN, second comment' } }]),
    );
    await waitFor(() => expect(H.show).toHaveBeenCalledWith(expect.objectContaining({ message: 'Link updated' })));
    await waitFor(() => expect(screen.queryByTestId('edit-link-form')).toBeNull());
  });

  it('edits the landing page, the audience and the cost', async () => {
    renderPanel();
    await openMenu('yt-sponsor');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByTestId('edit-link-form');
    expect((screen.getByTestId('link-cost') as HTMLInputElement).value).toBe('300');
    expect((screen.getByTestId('link-currency') as HTMLInputElement).value).toBe('EUR');

    // The audience followed the old page, so it follows the new one.
    change('link-landing', '/for/educators');
    expect((screen.getByTestId('link-persona') as HTMLSelectElement).value).toBe('educator');
    change('link-cost', '');
    fireEvent.click(screen.getByTestId('edit-link-save'));
    await waitFor(() =>
      expect(calls('PATCH')).toEqual([
        {
          url: `${LINKS_URL}/id-ads`,
          body: { destinationPath: '/for/educators', persona: 'educator', costMinor: null, costCurrency: null },
        },
      ]),
    );
  });

  // "Other" is chosen in the app only. A link is never given it; one saved earlier still shows it.
  it('does not offer "Other" as an audience', async () => {
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByTestId('edit-link-form');
    const options = [...(screen.getByTestId('link-persona') as HTMLSelectElement).options];
    expect(options.map((option) => option.value)).toEqual(['', 'creator', 'business', 'educator', 'nonprofit', 'software', 'agency']);
    expect(options.map((option) => option.textContent)).not.toContain('Other');
  });

  it('keeps "Other" on a link saved with it, and an edit of something else leaves the audience alone', async () => {
    links = [{ ...HN, persona: 'other' }, ADS, OLD];
    renderPanel();
    expect(within(await row('hn-launch')).getByText('Audience: Other')).toBeTruthy();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByTestId('edit-link-form');
    const select = screen.getByTestId('link-persona') as HTMLSelectElement;
    expect(select.value).toBe('other');
    expect([...select.options].at(-1)?.textContent).toBe('Other (no longer offered)');
    expect((screen.getByTestId('edit-link-save') as HTMLButtonElement).disabled).toBe(true);

    change('link-placement', 'Show HN, second comment');
    fireEvent.click(screen.getByTestId('edit-link-save'));
    await waitFor(() =>
      expect(calls('PATCH')).toEqual([{ url: `${LINKS_URL}/id-hn`, body: { placement: 'Show HN, second comment' } }]),
    );
  });

  // The app's own pages show no cookie banner: a link that lands on one can never show a visitor.
  it('does not offer the sign-up page as a landing page, and says what an app path typed by hand costs', async () => {
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByTestId('edit-link-form');
    const offered = [...(screen.getByTestId('link-landing') as HTMLSelectElement).options].map((option) => option.value);
    expect(offered).not.toContain('/app/signup');
    expect(screen.queryByTestId('link-app-landing-note')).toBeNull();
    change('link-landing', 'custom');
    change('link-custom-path', '/app/signup');
    const note = screen.getByTestId('link-app-landing-note');
    expect(note.textContent).toContain('Nobody is asked about cookies');
    expect(note.textContent).toContain('shows no visitors');
    // A hint, not a refusal: the path is still a valid destination.
    expect((screen.getByTestId('edit-link-save') as HTMLButtonElement).disabled).toBe(false);
    change('link-custom-path', '/features');
    expect(screen.queryByTestId('link-app-landing-note')).toBeNull();
  });

  it('validates a custom landing path like the server and shows a server refusal on its field', async () => {
    patchResponse = () => json({ error: 'destination_path_invalid', field: 'destinationPath' }, 400);
    renderPanel();
    await openMenu('hn-launch');
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByTestId('edit-link-form');
    change('link-landing', 'custom');
    change('link-custom-path', 'https://evil.example/');
    expect(screen.getByText(/^Start with \/ and use only letters, digits and/)).toBeTruthy();
    expect((screen.getByTestId('edit-link-save') as HTMLButtonElement).disabled).toBe(true);
    expect(calls('PATCH')).toEqual([]);

    change('link-custom-path', '/features');
    expect(screen.queryByText(/^Start with \/ and use only letters, digits and/)).toBeNull();
    fireEvent.click(screen.getByTestId('edit-link-save'));
    await waitFor(() => expect(calls('PATCH')).toHaveLength(1));
    expect(calls('PATCH')[0].body).toEqual({ destinationPath: '/features', persona: null });
    // The server is the authority: its refusal lands on the field, and the dialog stays open.
    expect(await screen.findByText(/^Start with \/ and use only letters, digits and/)).toBeTruthy();
    expect(screen.getByTestId('edit-link-form')).toBeTruthy();
  });
});

describe('LinksPanel: a new link', () => {
  async function openNewLink() {
    renderPanel();
    await row('hn-launch');
    fireEvent.click(screen.getByTestId('new-link'));
    await screen.findByTestId('new-link-form');
  }

  it('preselects the medium from the channel, suggests the code and fills in the audience', async () => {
    await openNewLink();
    expect((screen.getByTestId('link-landing') as HTMLSelectElement).value).toBe('/');
    expect((screen.getByTestId('link-currency') as HTMLInputElement).value).toBe('EUR');

    change('link-placement', 'Launch post, pinned');
    change('link-channel', 'linkedin');
    expect((screen.getByTestId('link-medium') as HTMLSelectElement).value).toBe('social');
    change('link-channel', 'reddit');
    expect((screen.getByTestId('link-medium') as HTMLSelectElement).value).toBe('community');
    change('link-campaign', 'Spring Sale');
    expect((screen.getByTestId('link-campaign') as HTMLInputElement).value).toBe('spring-sale');
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('reddit-spring-sale-launch-post-pinned');
    expect(screen.getByText(/^The short link will be \/l\/reddit-spring-sale-launch-post-pinned\./)).toBeTruthy();

    change('link-landing', '/for/small-business');
    expect((screen.getByTestId('link-persona') as HTMLSelectElement).value).toBe('business');
    change('link-landing', '/pricing');
    expect((screen.getByTestId('link-persona') as HTMLSelectElement).value).toBe('');
  });

  it('keeps a medium and a code the admin chose by hand', async () => {
    await openNewLink();
    change('link-channel', 'newsletter');
    expect((screen.getByTestId('link-medium') as HTMLSelectElement).value).toBe('email');
    change('link-medium', 'paid');
    change('link-channel', 'x');
    expect((screen.getByTestId('link-medium') as HTMLSelectElement).value).toBe('paid');

    change('link-campaign', 'launch');
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('x-launch');
    change('link-code', 'My Own Code');
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('my-own-code');
    change('link-placement', 'Thread reply');
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('my-own-code');
    // The suggestion stays one click away.
    fireEvent.click(screen.getByTestId('link-code-suggestion'));
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('x-launch-thread-reply');
    change('link-placement', 'Thread reply two');
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('x-launch-thread-reply-two');
  });

  it('suggests a code no loaded link uses, archived ones included', async () => {
    links = [{ ...HN, code: 'x-launch' }, { ...OLD, code: 'x-launch-2' }];
    funnel = { ...FUNNEL, items: [] };
    renderPanel();
    await row('x-launch');
    fireEvent.click(screen.getByTestId('new-link'));
    await screen.findByTestId('new-link-form');
    change('link-channel', 'x');
    change('link-campaign', 'launch');
    expect((screen.getByTestId('link-code') as HTMLInputElement).value).toBe('x-launch-3');
  });

  it('validates the campaign and the code as they are typed', async () => {
    await openNewLink();
    change('link-campaign', 'launch/2026');
    expect(screen.getByText(/^Use lowercase letters, digits and \. _ ~ \+ - only/)).toBeTruthy();
    change('link-campaign', 'launch-2026');
    expect(screen.queryByText(/^Use lowercase letters, digits and \. _ ~ \+ - only/)).toBeNull();

    change('link-code', 'a');
    expect(screen.getByText(/^Use lowercase letters, digits and hyphens only/)).toBeTruthy();
    change('link-code', 'hn-launch');
    expect(screen.getByText(/^Another link already uses this code\./)).toBeTruthy();
    change('link-code', 'hn-launch-b');
    expect(screen.queryByText(/^Another link already uses this code\./)).toBeNull();
    // Nothing was sent, and a missing field is not shouted about before Create is pressed.
    expect(screen.queryByText('Say where you will post it.')).toBeNull();
    expect(calls('POST')).toEqual([]);
  });

  it('offers the campaigns of the existing links', async () => {
    await openNewLink();
    fireEvent.focus(screen.getByTestId('link-campaign'));
    fireEvent.click(screen.getByTestId('link-campaign'));
    expect(await screen.findByRole('option', { name: 'launch-2026-11', hidden: true })).toBeTruthy();
    expect(screen.getByRole('option', { name: 'creators-q4', hidden: true })).toBeTruthy();
    // The archived link's campaign is offered too: a campaign outlives one link.
    expect(screen.getByRole('option', { name: 'archive', hidden: true })).toBeTruthy();
    fireEvent.click(screen.getByRole('option', { name: 'creators-q4', hidden: true }));
    expect((screen.getByTestId('link-campaign') as HTMLInputElement).value).toBe('creators-q4');
  });

  it('names what is missing and sends nothing', async () => {
    await openNewLink();
    fireEvent.click(screen.getByTestId('new-link-submit'));
    expect(await screen.findByText('Say where you will post it.')).toBeTruthy();
    expect(screen.getByText('Choose a channel.')).toBeTruthy();
    expect(screen.getByText('Give the campaign a name.')).toBeTruthy();
    expect(screen.getByText('The link needs a code.')).toBeTruthy();
    expect(calls('POST')).toEqual([]);
  });

  it('creates the link, then shows the short and the long address with a copy button each', async () => {
    await openNewLink();
    change('link-placement', 'Show HN, first comment');
    change('link-channel', 'hackernews');
    change('link-campaign', 'relaunch');
    change('link-landing', '/for/creators');
    change('link-cost', '49.9');
    const reads = calls('GET').length;
    fireEvent.click(screen.getByTestId('new-link-submit'));

    await waitFor(() => expect(calls('POST')).toHaveLength(1));
    expect(calls('POST')[0]).toEqual({
      url: LINKS_URL,
      body: {
        code: 'hn-relaunch-show-hn-first',
        channel: 'hackernews',
        medium: 'community',
        campaign: 'relaunch',
        placement: 'Show HN, first comment',
        destinationPath: '/for/creators',
        persona: 'creator',
        costMinor: 4990,
        costCurrency: 'EUR',
      },
    });

    const created = await screen.findByTestId('link-created');
    expect(screen.getByText('Link created')).toBeTruthy();
    const short = `${ORIGIN}/l/hn-relaunch-show-hn-first`;
    const long = `${ORIGIN}/for/creators?utm_source=hackernews&utm_medium=community&utm_campaign=relaunch&utm_content=hn-relaunch-show-hn-first`;
    expect(within(created).getByTestId('link-short-url').textContent).toBe(short);
    expect(within(created).getByTestId('link-long-url').textContent).toBe(long);
    // Which one to use where.
    expect(within(created).getByText(/^Use it where space is tight or a clean link matters/)).toBeTruthy();
    expect(within(created).getByText(/^Goes straight to the page and works without the redirect\. Clicks on it are not counted/)).toBeTruthy();

    fireEvent.click(within(created).getByTestId('link-short-url-copy'));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(short));
    await waitFor(() => expect(within(created).getByTestId('link-short-url-copy').textContent).toBe('Copied'));
    fireEvent.click(within(created).getByTestId('link-long-url-copy'));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(long));

    // The table behind is read again.
    await waitFor(() => expect(calls('GET').length).toBeGreaterThanOrEqual(reads + 2));
    fireEvent.click(screen.getByTestId('link-created-done'));
    await waitFor(() => expect(screen.queryByTestId('link-created')).toBeNull());
  });

  it('sends a custom own-site path and no audience or cost when none is set', async () => {
    await openNewLink();
    change('link-placement', 'Footer of the docs');
    change('link-channel', 'other');
    change('link-campaign', 'evergreen');
    change('link-landing', 'custom');
    change('link-custom-path', '/l/another');
    fireEvent.click(screen.getByTestId('new-link-submit'));
    expect(await screen.findByText(/^Start with \/ and use only letters, digits and/)).toBeTruthy();
    expect(calls('POST')).toEqual([]);

    change('link-custom-path', '/features');
    fireEvent.click(screen.getByTestId('new-link-submit'));
    await waitFor(() => expect(calls('POST')).toHaveLength(1));
    expect(calls('POST')[0].body).toEqual({
      code: 'other-evergreen-footer-of-the',
      channel: 'other',
      medium: 'referral',
      campaign: 'evergreen',
      placement: 'Footer of the docs',
      destinationPath: '/features',
    });
  });

  it('puts a taken code back on the code field when the server says so', async () => {
    postResponse = () => json({ error: 'link_code_taken', field: 'code' }, 409);
    await openNewLink();
    change('link-placement', 'Thread');
    change('link-channel', 'x');
    change('link-campaign', 'launch');
    fireEvent.click(screen.getByTestId('new-link-submit'));
    expect(await screen.findByText(/^Another link already uses this code\./)).toBeTruthy();
    expect(screen.queryByTestId('link-created')).toBeNull();
    expect(screen.getByTestId('new-link-form')).toBeTruthy();
    // Editing the code retires the refusal.
    change('link-code', 'x-launch-thread-b');
    expect(screen.queryByText(/^Another link already uses this code\./)).toBeNull();
  });

  it('says the link was not saved on any other failure', async () => {
    postResponse = () => json({ message: 'boom' }, 500);
    await openNewLink();
    change('link-placement', 'Thread');
    change('link-channel', 'x');
    change('link-campaign', 'launch');
    fireEvent.click(screen.getByTestId('new-link-submit'));
    expect((await screen.findByTestId('new-link-error')).textContent).toBe('Could not save the link. Please try again.');
    expect(screen.queryByTestId('link-created')).toBeNull();
  });
});

describe('LinksPanel in Italian', () => {
  it('renders the table, the note and the form in Italian', async () => {
    renderPanel('it');
    const table = await screen.findByRole('table', { name: 'Link tracciati e il loro funnel' });
    for (const name of ['Clic', 'Visitatori', 'Registrazioni', 'Ci ha detto cosa crea', 'Prima esecuzione', 'Costo per registrazione']) {
      expect(within(table).getByRole('columnheader', { name }), name).toBeTruthy();
    }
    const hn = await row('hn-launch');
    await waitFor(() => expect(within(hn).getByTestId('stop-line').textContent).toBe('La maggior parte si ferma prima della prima esecuzione · 6 account su 10'));
    expect(within(hn).getByText('Canale: Hacker News (Community)')).toBeTruthy();
    expect((await screen.findByTestId('links-note-capture-off')).textContent).toContain("L'acquisizione dall'URL è disattivata");
    expect(within(await row('yt-sponsor')).getByTestId('cell-cost').textContent).toMatch(/^300,00\s€$/);
    expect(screen.getByText('Account senza link')).toBeTruthy();

    fireEvent.click(screen.getByTestId('new-link'));
    await screen.findByTestId('new-link-form');
    expect(screen.getByLabelText(/^Dove lo pubblicherai\?/)).toBeTruthy();
    fireEvent.click(screen.getByTestId('new-link-submit'));
    expect(await screen.findByText('Scegli un canale.')).toBeTruthy();
  });
});
