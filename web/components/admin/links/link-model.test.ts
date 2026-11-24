import { describe, expect, it, vi } from 'vitest';
import { PERSONAS } from '@/lib/attribution/persona';
import { LINK_CHANNELS, LINK_MEDIA, type MarketingLink, type MarketingLinkFunnelItem } from '@/lib/types/marketing-links';
import {
  ACCOUNT_STEPS,
  CHANNEL_LABELS,
  CUSTOM_LANDING,
  DEFAULT_MEDIUM,
  LANDING_OPTIONS,
  LINK_PERSONAS,
  MEDIUM_LABELS,
  PERSONA_LABELS,
  barShares,
  biggestDrop,
  campaignSuggestions,
  costFormatOptions,
  costPerSignupMinor,
  createRequestOf,
  detailsOf,
  fieldOfErrorCode,
  fromMinorUnits,
  funnelCells,
  funnelRowOf,
  funnelTotals,
  isDestinationPath,
  isLinkCode,
  isLinkPersona,
  isPlacement,
  isUtmValue,
  landingChoiceOf,
  messageOfErrorCode,
  minorUnitDigits,
  normalizeCampaign,
  patchRequestOf,
  personaAfterLandingChange,
  personaChoices,
  personaLabel,
  personaOfLanding,
  share,
  slugify,
  startingCurrency,
  stepShares,
  suggestLinkCode,
  toMinorUnits,
  validateEditLink,
  validateNewLink,
  type NewLinkValues,
  periodCoversLink,
  isAppLandingPath,
} from './link-model';

const steps = (signups: number, verified: number, onboarded: number, firstProject: number, firstRun: number, firstVideo: number, paid: number) => ({
  signups,
  verified,
  onboarded,
  firstProject,
  firstRun,
  firstVideo,
  paid,
});

const link = (over: Partial<MarketingLink> = {}): MarketingLink => ({
  id: 'l1',
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
  totalClicks: 0,
  ...over,
});

const item = (over: Partial<MarketingLinkFunnelItem> = {}): MarketingLinkFunnelItem => ({
  linkId: 'l1',
  code: 'hn-launch',
  channel: 'hackernews',
  medium: 'community',
  campaign: 'launch-2026-11',
  placement: 'Show HN, first comment',
  destinationPath: '/for/software-teams',
  archived: false,
  costMinor: null,
  costCurrency: null,
  clicks: 0,
  botHits: 0,
  visitors: 0,
  ...steps(0, 0, 0, 0, 0, 0, 0),
  ...over,
});

const form = (over: Partial<NewLinkValues> = {}): NewLinkValues => ({
  placement: 'Show HN, first comment',
  channel: 'hackernews',
  medium: 'community',
  campaign: 'launch-2026-11',
  landing: '/for/software-teams',
  customPath: '',
  persona: 'software',
  cost: '',
  currency: 'EUR',
  code: 'hn-launch',
  ...over,
});

describe('the shared vocabulary', () => {
  it('has a label for every channel, medium and persona of the closed sets', () => {
    expect(Object.keys(CHANNEL_LABELS)).toEqual([...LINK_CHANNELS]);
    expect(Object.keys(MEDIUM_LABELS)).toEqual([...LINK_MEDIA]);
    expect(Object.keys(PERSONA_LABELS)).toEqual([...PERSONAS]);
  });

  // `other` is chosen in the app only (design section 2): a link is never given it.
  it('offers a link every audience with a landing page, and never "other"', () => {
    expect(LINK_PERSONAS).toEqual(['creator', 'business', 'educator', 'nonprofit', 'software', 'agency']);
    expect(isLinkPersona('creator')).toBe(true);
    expect(isLinkPersona('other')).toBe(false);
    expect(personaChoices('')).toEqual([...LINK_PERSONAS]);
    expect(personaChoices('agency')).toEqual([...LINK_PERSONAS]);
    // Every offered audience is the audience of a landing page the form lists.
    expect([...LINK_PERSONAS].sort()).toEqual(LANDING_OPTIONS.flatMap((option) => (option.persona ? [option.persona] : [])).sort());
  });

  it('still reads a link saved with "other", or with a token this build does not know', () => {
    expect(personaLabel('other')?.$msg).toBe('adminLinks.personas.other');
    expect(personaChoices('other')).toEqual([...LINK_PERSONAS, 'other']);
    expect(personaChoices('robots')).toEqual([...LINK_PERSONAS, 'robots']);
  });

  it('matches the design of record: 19 channels, 8 media', () => {
    expect(LINK_CHANNELS).toEqual([
      'hackernews', 'reddit', 'linkedin', 'x', 'producthunt', 'youtube', 'tiktok', 'instagram', 'facebook',
      'github', 'discord', 'newsletter', 'email', 'blog', 'directory', 'press', 'partner', 'ads', 'other',
    ]);
    expect(LINK_MEDIA).toEqual(['social', 'community', 'video', 'email', 'referral', 'paid', 'owned', 'press']);
  });

  it('preselects a medium of the closed set for every channel', () => {
    for (const channel of LINK_CHANNELS) expect(LINK_MEDIA).toContain(DEFAULT_MEDIUM[channel]);
    expect(DEFAULT_MEDIUM.hackernews).toBe('community');
    expect(DEFAULT_MEDIUM.linkedin).toBe('social');
    expect(DEFAULT_MEDIUM.youtube).toBe('video');
    expect(DEFAULT_MEDIUM.newsletter).toBe('email');
    expect(DEFAULT_MEDIUM.ads).toBe('paid');
    expect(DEFAULT_MEDIUM.press).toBe('press');
    expect(DEFAULT_MEDIUM.blog).toBe('owned');
  });
});

describe('landing pages', () => {
  it('offers the home page, the six audience pages, pricing, recipes and the blog', () => {
    expect(LANDING_OPTIONS.map((option) => option.path)).toEqual([
      '/',
      '/for/creators',
      '/for/small-business',
      '/for/educators',
      '/for/nonprofits',
      '/for/software-teams',
      '/for/agencies',
      '/pricing',
      '/recipes',
      '/blog',
    ]);
    // Every offered page is a destination the API accepts.
    for (const option of LANDING_OPTIONS) expect(isDestinationPath(option.path)).toBe(true);
  });

  // The app shows no cookie banner, so nobody sent straight to one of its pages is ever asked: a
  // link landing there can never show a visitor. No app page is offered; one typed by hand is
  // recognised, so the form can say what it costs.
  it('offers no page of the dashboard app, and recognises one typed as a custom path', () => {
    expect(LANDING_OPTIONS.some((option) => isAppLandingPath(option.path))).toBe(false);
    expect(landingChoiceOf('/app/signup')).toBe(CUSTOM_LANDING);
    expect(isAppLandingPath('/app/signup')).toBe(true);
    expect(isAppLandingPath(' /App/Login ')).toBe(true);
    expect(isAppLandingPath('/app')).toBe(true);
    expect(isAppLandingPath('/apple')).toBe(false);
    expect(isAppLandingPath('/for/creators')).toBe(false);
    expect(isAppLandingPath('')).toBe(false);
  });

  it('knows the audience of each audience page and of no other page', () => {
    expect(personaOfLanding('/for/creators')).toBe('creator');
    expect(personaOfLanding('/for/small-business')).toBe('business');
    expect(personaOfLanding('/for/educators')).toBe('educator');
    expect(personaOfLanding('/for/nonprofits')).toBe('nonprofit');
    expect(personaOfLanding('/for/software-teams')).toBe('software');
    expect(personaOfLanding('/for/agencies')).toBe('agency');
    expect(personaOfLanding('/')).toBeUndefined();
    expect(personaOfLanding('/pricing')).toBeUndefined();
    expect(personaOfLanding('/for/somebody-else')).toBeUndefined();
  });

  it('fills the audience in from the landing page until the admin picks another one', () => {
    expect(personaAfterLandingChange('/', '/for/educators', '')).toBe('educator');
    expect(personaAfterLandingChange('/for/educators', '/for/agencies', 'educator')).toBe('agency');
    expect(personaAfterLandingChange('/for/educators', '/pricing', 'educator')).toBe('');
    // Chosen by hand: it no longer matches the page, so it is left alone.
    expect(personaAfterLandingChange('/for/educators', '/for/agencies', 'creator')).toBe('creator');
    expect(personaAfterLandingChange('/', '/for/agencies', 'other')).toBe('other');
  });

  it('maps a stored destination onto the select', () => {
    expect(landingChoiceOf('/pricing')).toBe('/pricing');
    expect(landingChoiceOf('/features')).toBe(CUSTOM_LANDING);
  });
});

describe('validation, as the API does it', () => {
  it('accepts link codes of 2 to 40 lowercase letters, digits and hyphens', () => {
    expect(isLinkCode('hn-launch')).toBe(true);
    expect(isLinkCode('a1')).toBe(true);
    expect(isLinkCode('a'.repeat(40))).toBe(true);
    for (const bad of ['', 'a', '-ab', 'HN-launch', 'hn_launch', 'hn launch', 'hn.launch', 'a'.repeat(41), 'hn-launch\n']) {
      expect(isLinkCode(bad), bad).toBe(false);
    }
  });

  it('accepts campaign tokens that are UTM values', () => {
    expect(isUtmValue('launch-2026-11')).toBe(true);
    expect(isUtmValue('a')).toBe(true);
    expect(isUtmValue('spring.sale_v2~x+y')).toBe(true);
    for (const bad of ['', '-launch', 'Launch', 'launch 2026', 'launch/2026', 'é', 'a'.repeat(65)]) {
      expect(isUtmValue(bad), bad).toBe(false);
    }
  });

  it('normalises a campaign as typed: trimmed, lowercased, spaces to hyphens', () => {
    expect(normalizeCampaign('  Launch 2026 11 ')).toBe('launch-2026-11');
    expect(normalizeCampaign('Été')).toBe('été');
    expect(isUtmValue(normalizeCampaign('Été'))).toBe(false);
  });

  it('accepts only own-site paths as a destination', () => {
    for (const good of ['/', '/pricing', '/for/creators', '/app/signup', '/blog/my-post_1.html', '/a~b']) {
      expect(isDestinationPath(good), good).toBe(true);
    }
    for (const bad of [
      '',
      'pricing',
      '//evil.example',
      '/a//b',
      'https://evil.example/',
      '/pricing?x=1',
      '/pricing#top',
      '/a b',
      '/a\\b',
      '/../etc',
      '/a/./b',
      '/l',
      '/l/other-link',
      '/é',
      `/${'a'.repeat(200)}`,
    ]) {
      expect(isDestinationPath(bad), bad).toBe(false);
    }
    // /links is not a tracked link: only /l and /l/... would loop.
    expect(isDestinationPath('/links')).toBe(true);
  });

  it('accepts a one-line placement note of 1 to 160 characters', () => {
    expect(isPlacement('Show HN, first comment')).toBe(true);
    expect(isPlacement('  x  ')).toBe(true);
    expect(isPlacement('a'.repeat(160))).toBe(true);
    expect(isPlacement('')).toBe(false);
    expect(isPlacement('   ')).toBe(false);
    expect(isPlacement('a'.repeat(161))).toBe(false);
    expect(isPlacement('line one\nline two')).toBe(false);
    expect(isPlacement('tab\there')).toBe(false);
  });

  it('passes a complete new link and names every missing field', () => {
    expect(validateNewLink(form(), [])).toEqual({});
    const errors = validateNewLink(
      form({ placement: ' ', channel: '', medium: '', campaign: '', landing: CUSTOM_LANDING, customPath: '', code: '' }),
      [],
    );
    expect(Object.fromEntries(Object.entries(errors).map(([field, m]) => [field, m.$msg]))).toEqual({
      placement: 'adminLinks.errors.placementRequired',
      channel: 'adminLinks.errors.channelRequired',
      medium: 'adminLinks.errors.mediumRequired',
      campaign: 'adminLinks.errors.campaignRequired',
      destination: 'adminLinks.errors.destinationRequired',
      code: 'adminLinks.errors.codeRequired',
    });
  });

  it('refuses a wrong campaign, path, code, audience and cost, and a code that is taken', () => {
    expect(validateNewLink(form({ campaign: 'launch/2026' }), []).campaign?.$msg).toBe('adminLinks.errors.campaignInvalid');
    expect(validateNewLink(form({ landing: CUSTOM_LANDING, customPath: 'https://x.example' }), []).destination?.$msg).toBe(
      'adminLinks.errors.destinationInvalid',
    );
    expect(validateNewLink(form({ code: 'Not A Code' }), []).code?.$msg).toBe('adminLinks.errors.codeInvalid');
    expect(validateNewLink(form(), ['other', 'hn-launch']).code?.$msg).toBe('adminLinks.errors.codeTaken');
    expect(validateNewLink(form({ channel: 'myspace' }), []).channel?.$msg).toBe('adminLinks.errors.channelInvalid');
    expect(validateNewLink(form({ medium: 'carrier-pigeon' }), []).medium?.$msg).toBe('adminLinks.errors.mediumRequired');
    expect(validateNewLink(form({ persona: 'robots' }), []).persona?.$msg).toBe('adminLinks.errors.personaInvalid');
    // The API would take `other`; the form does not offer it, so a new link cannot carry it.
    expect(validateNewLink(form({ persona: 'other' }), []).persona?.$msg).toBe('adminLinks.errors.personaInvalid');
    for (const persona of LINK_PERSONAS) expect(validateNewLink(form({ persona }), []).persona).toBeUndefined();
    expect(validateNewLink(form({ cost: -1 }), []).cost?.$msg).toBe('adminLinks.errors.costInvalid');
    expect(validateNewLink(form({ cost: 10, currency: 'eu' }), []).cost?.$msg).toBe('adminLinks.errors.currencyInvalid');
    // The currency is only looked at when there is a cost.
    expect(validateNewLink(form({ cost: '', currency: '' }), [])).toEqual({});
  });

  it('validates the edit form with the same rules for the fields it has', () => {
    const values = detailsOf(link(), 'EUR');
    expect(validateEditLink(values)).toEqual({});
    expect(validateEditLink({ ...values, placement: '' }).placement?.$msg).toBe('adminLinks.errors.placementRequired');
    expect(validateEditLink({ ...values, landing: CUSTOM_LANDING, customPath: '/l/x' }).destination?.$msg).toBe(
      'adminLinks.errors.destinationInvalid',
    );
  });

  it('lets a link saved with "other" be edited without touching its audience', () => {
    const stored = link({ persona: 'other' });
    const values = detailsOf(stored, 'EUR');
    expect(values.persona).toBe('other');
    expect(validateEditLink(values)).toEqual({});
    expect(patchRequestOf(stored, { ...values, placement: 'A new note' })).toEqual({ placement: 'A new note' });
    expect(patchRequestOf(stored, { ...values, persona: 'creator' })).toEqual({ persona: 'creator' });
    expect(validateEditLink({ ...values, persona: 'robots' }).persona?.$msg).toBe('adminLinks.errors.personaInvalid');
  });

  it('maps every server refusal of a field onto that field and a sentence', () => {
    const fields: Record<string, string> = {
      code_invalid: 'code',
      link_code_taken: 'code',
      channel_invalid: 'channel',
      medium_invalid: 'medium',
      campaign_invalid: 'campaign',
      placement_invalid: 'placement',
      destination_path_invalid: 'destination',
      persona_invalid: 'persona',
      cost_invalid: 'cost',
    };
    for (const [code, field] of Object.entries(fields)) {
      expect(fieldOfErrorCode(code), code).toBe(field);
      expect(messageOfErrorCode(code)?.$msg, code).toMatch(/^adminLinks\.errors\./);
    }
    for (const code of ['field_immutable', 'invalid_body', 'something_new', null]) {
      expect(fieldOfErrorCode(code)).toBeNull();
      expect(messageOfErrorCode(code)).toBeNull();
    }
  });
});

describe('money', () => {
  // ISO 4217, from a fixed table. The browser's own figure (Intl) is not used: it is 0 for HUF,
  // IDR, COP, PKR and IQD on common builds, so the same amount would be stored as two numbers.
  it('takes the minor unit from ISO 4217, the same in every browser', () => {
    expect(minorUnitDigits('EUR')).toBe(2);
    expect(minorUnitDigits('USD')).toBe(2);
    expect(minorUnitDigits('JPY')).toBe(0);
    expect(minorUnitDigits('KRW')).toBe(0);
    expect(minorUnitDigits('KWD')).toBe(3);
    expect(minorUnitDigits('IQD')).toBe(3);
    for (const code of ['HUF', 'IDR', 'COP', 'PKR']) expect(minorUnitDigits(code), code).toBe(2);
    // Unknown codes, and any spelling of a known one.
    expect(minorUnitDigits('ZZZ')).toBe(2);
    expect(minorUnitDigits(' jpy ')).toBe(0);
    expect(minorUnitDigits('')).toBe(2);
  });

  it('never asks the browser for the exponent', () => {
    const intl = vi.spyOn(Intl, 'NumberFormat');
    expect(toMinorUnits(12500, 'HUF')).toBe(1250000);
    expect(fromMinorUnits(1250000, 'HUF')).toBe(12500);
    expect(intl).not.toHaveBeenCalled();
    intl.mockRestore();
  });

  it('writes and reads HUF, JPY, KWD and EUR with one exponent each', () => {
    expect(toMinorUnits(12500.5, 'HUF')).toBe(1250050);
    expect(fromMinorUnits(1250050, 'HUF')).toBe(12500.5);
    expect(toMinorUnits(500, 'JPY')).toBe(500);
    expect(fromMinorUnits(500, 'JPY')).toBe(500);
    expect(toMinorUnits(12.345, 'KWD')).toBe(12345);
    expect(fromMinorUnits(12345, 'KWD')).toBe(12.345);
    expect(toMinorUnits(49.9, 'EUR')).toBe(4990);
    expect(fromMinorUnits(4990, 'EUR')).toBe(49.9);
    // The forms use the same pair: what a form stores is what the next edit shows.
    for (const [amount, currency] of [[12500.5, 'HUF'], [500, 'JPY'], [12.345, 'KWD'], [49.9, 'EUR']] as const) {
      const body = createRequestOf(form({ cost: amount, currency }));
      const stored = link({ costMinor: body.costMinor as number, costCurrency: body.costCurrency as string });
      expect(detailsOf(stored, 'EUR')).toMatchObject({ cost: amount, currency });
    }
  });

  it('shows a cost with the decimals it was written with', () => {
    expect(costFormatOptions('HUF')).toEqual({ minimumFractionDigits: 2, maximumFractionDigits: 2 });
    expect(costFormatOptions('JPY')).toEqual({ minimumFractionDigits: 0, maximumFractionDigits: 0 });
    expect(costFormatOptions('KWD')).toEqual({ minimumFractionDigits: 3, maximumFractionDigits: 3 });
    expect(costFormatOptions('EUR')).toEqual({ minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const show = (minor: number, currency: string) =>
      new Intl.NumberFormat('en', { style: 'currency', currency, currencyDisplay: 'code', ...costFormatOptions(currency) })
        .format(fromMinorUnits(minor, currency))
        .replace(/\s/g, ' ');
    expect(show(1250050, 'HUF')).toBe('HUF 12,500.50');
    expect(show(500, 'JPY')).toBe('JPY 500');
    expect(show(12345, 'KWD')).toBe('KWD 12.345');
    expect(show(4990, 'EUR')).toBe('EUR 49.90');
  });

  it('converts by the currency minor unit', () => {
    expect(minorUnitDigits('EUR')).toBe(2);
    expect(minorUnitDigits('JPY')).toBe(0);
    expect(toMinorUnits(12.5, 'EUR')).toBe(1250);
    expect(toMinorUnits(19.99, 'USD')).toBe(1999);
    expect(toMinorUnits(500, 'JPY')).toBe(500);
    expect(fromMinorUnits(1250, 'EUR')).toBe(12.5);
    expect(fromMinorUnits(500, 'JPY')).toBe(500);
  });

  it('divides the cost by the signups, and has no figure without either', () => {
    expect(costPerSignupMinor(10000, 4)).toBe(2500);
    expect(costPerSignupMinor(10000, 0)).toBeNull();
    expect(costPerSignupMinor(null, 4)).toBeNull();
    expect(costPerSignupMinor(0, 4)).toBe(0);
  });

  it('has no cost per signup for a period shorter than the link\'s life: the cost is for all of its signups', () => {
    expect(costPerSignupMinor(10000, 4, true)).toBe(2500);
    expect(costPerSignupMinor(10000, 2, false)).toBeNull();
    const asOf = '2026-11-11T08:00:00Z';
    expect(periodCoversLink('2026-11-09T09:00:00Z', 7, asOf)).toBe(true);
    expect(periodCoversLink('2026-11-09T09:00:00Z', 30, asOf)).toBe(true);
    expect(periodCoversLink('2026-11-09T09:00:00Z', 1, asOf)).toBe(false);
    expect(periodCoversLink('2026-10-01T09:00:00Z', 30, asOf)).toBe(false);
    expect(periodCoversLink('2026-10-01T09:00:00Z', 90, new Date(asOf))).toBe(true);
    // An unreadable date is never taken as covered.
    expect(periodCoversLink('', 30, asOf)).toBe(false);
    expect(periodCoversLink('2026-11-09T09:00:00Z', 30, '')).toBe(false);
  });

  it('starts a cost in the currency of the newest link that has one, else EUR', () => {
    expect(startingCurrency([])).toBe('EUR');
    expect(startingCurrency([link(), link({ costMinor: 100, costCurrency: 'USD' }), link({ costMinor: 5, costCurrency: 'GBP' })])).toBe('USD');
  });
});

describe('the requests', () => {
  it('builds the POST body, leaving audience and cost out when not set', () => {
    expect(createRequestOf(form({ persona: '', campaign: ' Launch 2026 11 ', placement: '  Show HN  ', code: ' hn-launch ' }))).toEqual({
      code: 'hn-launch',
      channel: 'hackernews',
      medium: 'community',
      campaign: 'launch-2026-11',
      placement: 'Show HN',
      destinationPath: '/for/software-teams',
    });
    expect(createRequestOf(form({ landing: CUSTOM_LANDING, customPath: ' /features ', cost: 49.9, currency: 'eur' }))).toEqual({
      code: 'hn-launch',
      channel: 'hackernews',
      medium: 'community',
      campaign: 'launch-2026-11',
      placement: 'Show HN, first comment',
      destinationPath: '/features',
      persona: 'software',
      costMinor: 4990,
      costCurrency: 'EUR',
    });
  });

  it('starts the edit form from the stored link', () => {
    expect(detailsOf(link({ destinationPath: '/features', persona: null, costMinor: 4990, costCurrency: 'USD' }), 'EUR')).toEqual({
      placement: 'Show HN, first comment',
      landing: CUSTOM_LANDING,
      customPath: '/features',
      persona: '',
      cost: 49.9,
      currency: 'USD',
    });
    expect(detailsOf(link(), 'GBP')).toMatchObject({ landing: '/for/software-teams', customPath: '', persona: 'software', cost: '', currency: 'GBP' });
  });

  it('patches only what changed, and nothing when nothing did', () => {
    const stored = link({ costMinor: 4990, costCurrency: 'EUR' });
    const values = detailsOf(stored, 'EUR');
    expect(patchRequestOf(stored, values)).toEqual({});
    expect(patchRequestOf(stored, { ...values, placement: 'Show HN, second comment' })).toEqual({ placement: 'Show HN, second comment' });
    expect(patchRequestOf(stored, { ...values, landing: '/pricing' })).toEqual({ destinationPath: '/pricing' });
  });

  it('clears the audience and the cost with null, and sends the two cost fields together', () => {
    const stored = link({ costMinor: 4990, costCurrency: 'EUR' });
    const values = detailsOf(stored, 'EUR');
    expect(patchRequestOf(stored, { ...values, persona: '' })).toEqual({ persona: null });
    expect(patchRequestOf(stored, { ...values, cost: '' })).toEqual({ costMinor: null, costCurrency: null });
    expect(patchRequestOf(stored, { ...values, cost: 60 })).toEqual({ costMinor: 6000, costCurrency: 'EUR' });
    expect(patchRequestOf(stored, { ...values, currency: 'usd' })).toEqual({ costMinor: 4990, costCurrency: 'USD' });
    expect(patchRequestOf(link(), { ...detailsOf(link(), 'EUR'), cost: 5 })).toEqual({ costMinor: 500, costCurrency: 'EUR' });
  });
});

describe('the suggested code', () => {
  it('slugifies: accents folded, everything else a separator', () => {
    expect(slugify('Show HN, first comment!')).toBe('show-hn-first-comment');
    expect(slugify('  Città è bella  ')).toBe('citta-e-bella');
    expect(slugify('launch.2026_11')).toBe('launch-2026-11');
    expect(slugify('***')).toBe('');
  });

  it('joins the channel, the campaign and the first three words of the placement', () => {
    expect(suggestLinkCode({ channel: 'hackernews', campaign: 'launch-2026-11', placement: 'Show HN, first comment' }, [])).toBe(
      'hn-launch-2026-11-show-hn-first',
    );
    expect(suggestLinkCode({ channel: 'reddit', campaign: 'spring.sale', placement: 'r/videoediting weekly thread, top reply' }, [])).toBe(
      'reddit-spring-sale-r-videoediting-weekly',
    );
    expect(suggestLinkCode({ channel: 'youtube', campaign: 'demo', placement: '' }, [])).toBe('yt-demo');
  });

  it('is always a valid link code, whatever goes in', () => {
    const inputs = [
      { channel: 'producthunt', campaign: 'a-very-long-campaign-name-that-goes-on-and-on', placement: 'Launch day maker comment number one' },
      { channel: 'x', campaign: 'supercalifragilisticexpialidocious-and-then-some-more', placement: '' },
      { channel: 'other', campaign: 'Ünïcödé çàmpaign', placement: '日本語' },
      { channel: 'ads', campaign: 'q4', placement: '—' },
    ];
    for (const input of inputs) {
      const code = suggestLinkCode(input, []);
      expect(isLinkCode(code), `${JSON.stringify(input)} -> ${code}`).toBe(true);
      expect(code.length).toBeLessThanOrEqual(40);
      expect(code.endsWith('-')).toBe(false);
    }
  });

  it('suggests nothing while there is nothing to build from', () => {
    expect(suggestLinkCode({ channel: '', campaign: '', placement: '' }, [])).toBe('');
    expect(suggestLinkCode({ channel: 'x', campaign: '', placement: '' }, [])).toBe('');
    expect(suggestLinkCode({ channel: 'x', campaign: 'launch', placement: '' }, [])).toBe('x-launch');
  });

  it('is unique among the loaded links by a numeric suffix that still fits', () => {
    const parts = { channel: 'hackernews', campaign: 'launch', placement: 'Show HN' };
    expect(suggestLinkCode(parts, ['hn-launch-show-hn'])).toBe('hn-launch-show-hn-2');
    expect(suggestLinkCode(parts, ['hn-launch-show-hn', 'hn-launch-show-hn-2'])).toBe('hn-launch-show-hn-3');
    const long = { channel: 'x', campaign: 'a'.repeat(36), placement: '' };
    const first = suggestLinkCode(long, []);
    const second = suggestLinkCode(long, [first]);
    expect(first).toHaveLength(38);
    expect(second).not.toBe(first);
    expect(isLinkCode(second)).toBe(true);
    expect(second.endsWith('-2')).toBe(true);
  });
});

describe('the drop-off maths', () => {
  it('shares are fractions, and nothing when there is nothing to divide by', () => {
    expect(share(5, 20)).toBe(0.25);
    expect(share(0, 20)).toBe(0);
    expect(share(5, 0)).toBeNull();
  });

  it('gives each account step as a share of the step before', () => {
    expect(stepShares(steps(20, 15, 12, 10, 4, 3, 1))).toEqual({
      signups: null,
      verified: 0.75,
      onboarded: 0.8,
      firstProject: 10 / 12,
      firstRun: 0.4,
      firstVideo: 0.75,
      paid: 1 / 3,
    });
  });

  it('has no share after a step nobody reached', () => {
    expect(stepShares(steps(3, 0, 0, 0, 0, 0, 0))).toMatchObject({ verified: 0, onboarded: null, paid: null });
  });

  it('lets a share exceed 1, because steps are counted independently', () => {
    // Two accounts paid; only one of them has a finished video yet.
    expect(stepShares(steps(5, 5, 5, 5, 3, 1, 2)).paid).toBe(2);
  });

  it('draws each bar as a share of the signups, clamped to the bar', () => {
    expect(barShares(steps(20, 15, 12, 10, 4, 3, 1))).toEqual({
      signups: 1,
      verified: 0.75,
      onboarded: 0.6,
      firstProject: 0.5,
      firstRun: 0.2,
      firstVideo: 0.15,
      paid: 0.05,
    });
    expect(barShares(steps(0, 0, 0, 0, 0, 0, 0)).signups).toBe(0);
    expect(barShares(steps(2, 3, 0, 0, 0, 0, 0)).verified).toBe(1);
  });

  it('finds where most accounts stop: the largest loss in accounts', () => {
    expect(biggestDrop(steps(20, 15, 14, 10, 4, 3, 0))).toEqual({ from: 'firstProject', step: 'firstRun', lost: 6, of: 10 });
    // By accounts, not by percent: 1 of 1 lost (100 %) is not "where most stop" next to 9 of 20.
    expect(biggestDrop(steps(20, 11, 11, 11, 11, 1, 0))).toEqual({ from: 'firstRun', step: 'firstVideo', lost: 10, of: 11 });
    expect(biggestDrop(steps(20, 11, 1, 1, 1, 1, 0))?.step).toBe('onboarded');
  });

  it('takes the earliest step on a tie', () => {
    expect(biggestDrop(steps(10, 7, 4, 4, 4, 4, 4))).toEqual({ from: 'signups', step: 'verified', lost: 3, of: 10 });
  });

  it('marks nothing when nobody was lost', () => {
    expect(biggestDrop(steps(0, 0, 0, 0, 0, 0, 0))).toBeNull();
    expect(biggestDrop(steps(4, 4, 4, 4, 4, 4, 4))).toBeNull();
    // A later step that is higher is not a loss.
    expect(biggestDrop(steps(2, 2, 2, 2, 2, 2, 3))).toBeNull();
  });

  it('never counts clicks or visitors as a lost account', () => {
    // 1,000 clicks and 2 signups: the drop is still looked for among the accounts only.
    const cells = funnelCells({ clicks: 1000, visitors: 40, steps: steps(2, 2, 1, 1, 1, 1, 0) });
    expect(cells.filter((cell) => cell.marked).map((cell) => cell.key)).toEqual(['onboarded']);
  });
});

describe('the cells of a funnel row', () => {
  it('are the nine figures in column order', () => {
    const cells = funnelCells({ clicks: 200, visitors: 50, steps: steps(20, 15, 12, 10, 4, 3, 1) });
    expect(cells.map((cell) => cell.key)).toEqual(['clicks', 'visitors', ...ACCOUNT_STEPS]);
    expect(cells.map((cell) => cell.count)).toEqual([200, 50, 20, 15, 12, 10, 4, 3, 1]);
  });

  it('show visitors and signups as a share of clicks and later steps as a share of the step before', () => {
    const byKey = Object.fromEntries(
      funnelCells({ clicks: 200, visitors: 50, steps: steps(20, 15, 12, 10, 4, 3, 1) }).map((cell) => [cell.key, cell]),
    );
    expect(byKey.clicks).toMatchObject({ share: null, shareOf: null, bar: null });
    expect(byKey.visitors).toMatchObject({ share: 0.25, shareOf: 'clicks', bar: null });
    expect(byKey.signups).toMatchObject({ share: 0.1, shareOf: 'clicks', bar: 1 });
    expect(byKey.verified).toMatchObject({ share: 0.75, shareOf: 'previous', bar: 0.75, marked: false });
    expect(byKey.firstRun).toMatchObject({ share: 0.4, shareOf: 'previous', bar: 0.2, marked: true });
  });

  it('have no share of clicks when there were none', () => {
    const cells = funnelCells({ clicks: 0, visitors: 3, steps: steps(2, 1, 0, 0, 0, 0, 0) });
    expect(cells[1]).toMatchObject({ count: 3, share: null, shareOf: null });
    expect(cells[2]).toMatchObject({ count: 2, share: null, shareOf: null });
  });

  it('have no clicks or visitors for the accounts without a link, and no figure at all for a link the funnel lacks', () => {
    const unattributed = funnelCells({ clicks: null, visitors: null, steps: steps(8, 6, 2, 2, 1, 1, 0) });
    expect(unattributed.slice(0, 2).map((cell) => cell.count)).toEqual([null, null]);
    expect(unattributed[2]).toMatchObject({ key: 'signups', count: 8, share: null });
    expect(unattributed.find((cell) => cell.marked)?.key).toBe('onboarded');
    const missing = funnelCells({ clicks: null, visitors: null, steps: null });
    expect(missing).toHaveLength(9);
    expect(missing.every((cell) => cell.count === null && !cell.marked)).toBe(true);
  });
});

describe('totals', () => {
  const a = item({ linkId: 'a', clicks: 100, botHits: 4, visitors: 30, ...steps(10, 8, 6, 5, 3, 2, 1), costMinor: 5000, costCurrency: 'EUR' });
  const b = item({ linkId: 'b', clicks: 50, botHits: 1, visitors: 10, ...steps(5, 5, 2, 2, 1, 1, 0) });
  const c = item({ linkId: 'c', clicks: 10, visitors: 2, ...steps(5, 1, 0, 0, 0, 0, 0), costMinor: 2500, costCurrency: 'EUR' });

  it('sum every count over the links', () => {
    expect(funnelTotals([a, b, c])).toMatchObject({
      links: 3,
      clicks: 160,
      botHits: 5,
      visitors: 42,
      ...steps(20, 14, 8, 7, 4, 3, 1),
    });
  });

  it('sum the cost, and divide it by the signups of the links that have one', () => {
    const totals = funnelTotals([a, b, c]);
    expect(totals.costs).toEqual([{ currency: 'EUR', minor: 7500 }]);
    // 75.00 over the 15 signups of the two links with a cost, not over all 20.
    expect(totals.costPerSignup).toEqual({ currency: 'EUR', minor: 500 });
  });

  it('have no cost per signup when a link with a cost is older than the period', () => {
    // `c` was created before the period began: its 25.00 paid for signups the period does not show.
    const totals = funnelTotals([a, b, c], (row) => row.linkId !== 'c');
    expect(totals.costs).toEqual([{ currency: 'EUR', minor: 7500 }]);
    expect(totals.costPerSignup).toBeNull();
    // A link without a cost does not matter here, however old it is.
    expect(funnelTotals([a, b, c], (row) => row.linkId !== 'b').costPerSignup).toEqual({ currency: 'EUR', minor: 500 });
  });

  it('keep currencies apart and then have no single cost per signup', () => {
    const totals = funnelTotals([a, item({ linkId: 'd', ...steps(4, 0, 0, 0, 0, 0, 0), costMinor: 900, costCurrency: 'USD' })]);
    expect(totals.costs).toEqual([
      { currency: 'EUR', minor: 5000 },
      { currency: 'USD', minor: 900 },
    ]);
    expect(totals.costPerSignup).toBeNull();
  });

  it('have no cost when no link has one, and no cost per signup without a signup', () => {
    expect(funnelTotals([b])).toMatchObject({ costs: [], costPerSignup: null });
    expect(funnelTotals([item({ costMinor: 100, costCurrency: 'EUR' })]).costPerSignup).toBeNull();
    expect(funnelTotals([])).toMatchObject({ links: 0, clicks: 0, signups: 0, costs: [] });
  });
});

describe('joining links and funnel rows', () => {
  it('finds a link row by id, and has none for a link the funnel does not carry', () => {
    const rows = [item({ linkId: 'l1', clicks: 7 }), item({ linkId: 'l2', clicks: 9 })];
    expect(funnelRowOf(link({ id: 'l2' }), rows)?.clicks).toBe(9);
    expect(funnelRowOf(link({ id: 'l3' }), rows)).toBeNull();
  });

  it('suggests each existing campaign once, newest link first', () => {
    expect(
      campaignSuggestions([link({ campaign: 'launch-2026-11' }), link({ campaign: 'spring' }), link({ campaign: 'launch-2026-11' })]),
    ).toEqual(['launch-2026-11', 'spring']);
  });
});
