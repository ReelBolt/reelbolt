import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  LANDING_PERSONAS,
  PERSONAS,
  PERSONA_LANDING_SLUGS,
  personaForPath,
  personaForSlug,
  personaLandingPath,
  sanitizePersona,
} from './attribution-persona.ts';

test('the persona set and the landing slugs are the ones in the funnel contract', () => {
  // docs/design/new-user-funnel.md section 2. The app, the API and BI share these tokens.
  assert.deepEqual([...PERSONAS], ['creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other']);
  assert.deepEqual(PERSONA_LANDING_SLUGS, {
    creator: 'creators',
    business: 'small-business',
    educator: 'educators',
    nonprofit: 'nonprofits',
    software: 'software-teams',
    agency: 'agencies',
  });
  assert.deepEqual(LANDING_PERSONAS, PERSONAS.filter((p) => p !== 'other'));
  assert.equal(personaLandingPath('business'), '/for/small-business');
});

test('sanitizePersona trims and lowercases, and drops everything that has no landing page', () => {
  assert.equal(sanitizePersona(' Creator '), 'creator');
  assert.equal(sanitizePersona('agency'), 'agency');
  for (const bad of ['other', 'creators', 'admin', '', '  ', 'creator&x=1', null, undefined]) {
    assert.equal(sanitizePersona(bad), undefined, String(bad));
  }
});

test('an audience page is recognised from its path, nothing else is', () => {
  assert.equal(personaForPath('/for/creators'), 'creator');
  assert.equal(personaForPath('/for/software-teams/'), 'software');
  assert.equal(personaForPath('/for/small-business?utm_source=x'), 'business');
  assert.equal(personaForSlug('nonprofits'), 'nonprofit');
  for (const path of ['/', '/pricing', '/for', '/for/', '/for/unknown', '/for/creators/more', '/it/for/creators', '/blog/for/creators', '', null]) {
    assert.equal(personaForPath(path), undefined, String(path));
  }
});
