import { test } from 'node:test';
import assert from 'node:assert/strict';

import { LAUNCH_FILM_BASE_DEFAULT, launchFilm } from './launch-film.ts';

test('an empty base disables the film entirely', () => {
  assert.equal(launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: '' }), null);
});
test('the default base is the live public one, so the film ships on', () => {
  assert.equal(LAUNCH_FILM_BASE_DEFAULT, 'https://cdn.reelbolt.ai');
  assert.equal(launchFilm({})?.sources[0].src, 'https://cdn.reelbolt.ai/launch/launch-film-v4.mp4');
});
test('every path has a default off the base', () => {
  const f = launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example' });
  assert.equal(f?.sources.length, 1);
  assert.equal(f?.sources[0].src, 'https://cdn.example/launch/launch-film-v4.mp4');
  assert.equal(f?.poster, null); // no poster file exists; never point at one
  assert.equal(f?.width, 1920);
  assert.equal(f?.height, 1080);
  assert.equal(f?.captions, null);
});
test('a configured poster is absolute against the base', () => {
  const f = launchFilm({
    NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example',
    NEXT_PUBLIC_LAUNCH_FILM_POSTER_PATH: '/launch/poster.jpg',
  });
  assert.equal(f?.poster, 'https://cdn.example/launch/poster.jpg');
});
test('a configured film carries its metadata and both sources', () => {
  const f = launchFilm({
    NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example',
    NEXT_PUBLIC_LAUNCH_FILM_WEBM_PATH: '/launch/launch-film-v4.webm',
    NEXT_PUBLIC_LAUNCH_FILM_DURATION: 'PT1M1S',
    NEXT_PUBLIC_LAUNCH_FILM_UPLOAD_DATE: '2026-10-20',
  });
  assert.equal(f?.sources.length, 2);
  assert.equal(f?.durationIso, 'PT1M1S');
  assert.equal(f?.uploadDate, '2026-10-20');
});
test('the caption track is present only when configured', () => {
  const base = { NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example' };
  assert.equal(launchFilm(base)?.captions, null);
  assert.equal(
    launchFilm({ ...base, NEXT_PUBLIC_LAUNCH_FILM_CAPTIONS_PATH: '/launch/launch-film.vtt' })?.captions?.src,
    'https://cdn.example/launch/launch-film.vtt',
  );
});
test('a trailing slash on the base never doubles up', () => {
  assert.equal(
    launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example/' })?.sources[0].src,
    'https://cdn.example/launch/launch-film-v4.mp4',
  );
});
test('a non-numeric width falls back to 1920x1080 rather than NaN', () => {
  const f = launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example', NEXT_PUBLIC_LAUNCH_FILM_WIDTH: 'wide' });
  assert.equal(f?.width, 1920);
});
test('missing duration and upload date are absent, not invented', () => {
  const f = launchFilm({
    NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example',
    NEXT_PUBLIC_LAUNCH_FILM_DURATION: '',
    NEXT_PUBLIC_LAUNCH_FILM_UPLOAD_DATE: '',
  });
  assert.equal(f?.durationIso, null);
  assert.equal(f?.uploadDate, null);
});
test('the film’s real metadata is the default', () => {
  const f = launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example' });
  assert.equal(f?.durationIso, 'PT1M1S');
  assert.equal(f?.uploadDate, '2026-10-20');
});
test('the default description is one short factual line, not the site’s meta blurb', () => {
  const f = launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example' });
  assert.equal(f?.description, 'The ReelBolt launch film.');
  // The VideoObject needs a non-empty description, and the figcaption stays to one line.
  assert.ok((f?.description.length ?? 0) > 0);
  assert.ok((f?.description.length ?? 0) <= 40);
});
test('a configured description overrides the default', () => {
  const f = launchFilm({
    NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example',
    NEXT_PUBLIC_LAUNCH_FILM_DESCRIPTION: 'A configured line.',
  });
  assert.equal(f?.description, 'A configured line.');
});
test('a base with no source at all is not a film', () => {
  // An empty `sources` array would render a <video> that can never play and would make
  // `videoObjectSchema` dereference `sources[0]`. (QA Q-T2.)
  assert.equal(
    launchFilm({ NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example', NEXT_PUBLIC_LAUNCH_FILM_SRC_PATH: '' }),
    null,
  );
  assert.equal(
    launchFilm({
      NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example',
      NEXT_PUBLIC_LAUNCH_FILM_SRC_PATH: '',
      NEXT_PUBLIC_LAUNCH_FILM_WEBM_PATH: '/launch/film.webm',
    })?.sources.length,
    1,
  );
});
