import { test } from 'node:test';
import assert from 'node:assert/strict';

import { launchFilm } from './launch-film.ts';
import { videoObjectSchema } from './video-schema.ts';

const base = { NEXT_PUBLIC_LAUNCH_FILM_BASE: 'https://cdn.example' };

// The caller guards the null case: `Hero.tsx` only renders the JsonLd when `launchFilm()` returned
// a film, so `videoObjectSchema` is only ever handed a real one. A null film never reaches here.
test('the schema is a VideoObject with an absolute content url', () => {
  const film = launchFilm(base)!;
  const schema = videoObjectSchema(film, 'https://reelbolt.ai');
  assert.equal(schema['@type'], 'VideoObject');
  assert.equal(schema.contentUrl, 'https://cdn.example/launch/launch-film-v4.mp4');
});

test('thumbnailUrl is omitted when there is no poster, never pointed at a file that is not there', () => {
  const film = launchFilm(base)!;
  const schema = videoObjectSchema(film, 'https://reelbolt.ai');
  assert.ok(!('thumbnailUrl' in schema));
});

test('a configured poster becomes an absolute thumbnailUrl', () => {
  const film = launchFilm({ ...base, NEXT_PUBLIC_LAUNCH_FILM_POSTER_PATH: '/launch/poster.jpg' })!;
  const schema = videoObjectSchema(film, 'https://reelbolt.ai');
  assert.equal(schema.thumbnailUrl, 'https://cdn.example/launch/poster.jpg');
});

test('duration and uploadDate are omitted, not invented, when the film lacks them', () => {
  const film = launchFilm({
    ...base,
    NEXT_PUBLIC_LAUNCH_FILM_DURATION: '',
    NEXT_PUBLIC_LAUNCH_FILM_UPLOAD_DATE: '',
  })!;
  const schema = videoObjectSchema(film, 'https://reelbolt.ai');
  assert.ok(!('duration' in schema));
  assert.ok(!('uploadDate' in schema));
});

test('the film’s real metadata reaches the schema', () => {
  const film = launchFilm(base)!;
  const schema = videoObjectSchema(film, 'https://reelbolt.ai');
  assert.equal(schema.duration, 'PT1M1S');
  assert.equal(schema.uploadDate, '2026-10-20');
});

test('a missing description adds no fabricated key', () => {
  const film = { ...launchFilm(base)!, description: '' };
  const schema = videoObjectSchema(film, 'https://reelbolt.ai');
  assert.ok(!('description' in schema));
});
