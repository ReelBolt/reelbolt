import { describe, expect, it } from 'vitest';
import { changesNeedsNote, decisionSentence, expiryInWords, guestThreads, unreadLinkCount, type GuestComment, type ReviewLinkSummary } from '../guest-links';

describe('guest link helpers', () => {
  it('words a decision', () => {
    expect(decisionSentence({ decision: 'approved', guestName: 'Ana', at: '2026-10-26T12:00:00Z' })).toMatch(/^Approved by Ana, [A-Z][a-z]{2} \d{1,2}$/);
    expect(decisionSentence({ decision: 'changesRequested', guestName: 'Ana', note: ' Cut it ', at: '2026-10-26T12:00:00Z' })).toBe('Ana asked for changes: Cut it');
    expect(decisionSentence({ decision: 'changesRequested', guestName: 'Ana', note: null, at: '2026-10-26T12:00:00Z' })).toBe('Ana asked for changes');
  });
  it('words an expiry', () => {
    const now = Date.parse('2026-10-26T00:00:00Z');
    expect(expiryInWords('2026-10-25T00:00:00Z', now)).toBe('expired');
    expect(expiryInWords('2026-10-26T00:30:00Z', now)).toBe('in under an hour');
    expect(expiryInWords('2026-10-26T05:00:00Z', now)).toBe('in 5 hours');
    expect(expiryInWords('2026-11-01T01:00:00Z', now)).toBe('in 6 days');
  });
  it('needs a note for changes unless the guest commented', () => {
    expect(changesNeedsNote('  ', false)).toBe(true);
    expect(changesNeedsNote('  ', true)).toBe(false);
    expect(changesNeedsNote('x', false)).toBe(false);
  });
  it('counts unread on active links only', () => {
    const l = (o: Partial<ReviewLinkSummary>) => ({ id: 'a', label: null, status: 'active', expiresAt: '', createdAt: '', viewCount: 0, lastViewedAt: null, stale: false, guestComments: 0, unreadGuestActivity: true, ...o }) as ReviewLinkSummary;
    expect(unreadLinkCount([l({}), l({ status: 'revoked' }), l({ unreadGuestActivity: false })])).toBe(1);
    expect(unreadLinkCount(undefined)).toBe(0);
  });
  it('threads sort by time with replies under their root', () => {
    const c = (id: string, frame: number, parentId: string | null = null): GuestComment => ({ id, parentId, authorName: 'A', guest: true, body: id, frame, resolved: false, createdAt: `2026-10-26T00:00:0${frame % 10}Z` });
    const t = guestThreads([c('b', 90), c('a', 10), c('r', 10, 'b')]);
    expect(t.map((x) => x.root.id)).toEqual(['a', 'b']);
    expect(t[1].replies.map((x) => x.id)).toEqual(['r']);
  });
});
