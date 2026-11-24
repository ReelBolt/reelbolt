/**
 * Committed fixtures for the media-pool contract.
 *
 * These are the whole test substrate for this feature: no backend is started, no live API is
 * called, and every component test renders from here. Two rules shape the set:
 *
 *   1. **Every wire shape is represented, including its unwelcome states.** All three derivative
 *      kinds, a pool item with `probe: null` (never analysed), a probe whose individual fields are
 *      null, a source with NO derivative rows at all, and BOTH mediaRef prefixes.
 *   2. **Each fixture is paired with the schema definition it must satisfy** in `VALID_FIXTURES`,
 *      and `INVALID_FIXTURES` holds the deliberately-broken copies whose rejection proves that ajv
 *      is actually running — an ajv that silently no-ops would pass the positive cases and fail
 *      nothing, which is the exact failure mode the validation test exists to catch.
 */

import {
  derivativeContentUrl,
  filmstripSheetContentUrl,
  mediaFilmstripKey,
} from '../api/media';
import type {
  FilmstripIndex,
  MediaBin,
  MediaBinItemDetail,
  MediaDerivativeRow,
  MediaFilmstripResponse,
  MediaPoolItem,
  MediaProbe,
  RequestMediaDerivativesResponse,
} from './types';

const uuid = (n: number): string => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

/**
 * A probe carrying only the five ORIGINAL fields, with all six appended ones null.
 *
 * That is not a contrived shape: it is exactly what the API returns for any artifact persisted
 * before `videoCodec`/`audioCodec`/`container`/`audioChannels`/`audioSampleRateHz`/`bitrateBps`
 * existed, so a test that wants "a probe with nothing but a duration and a rate" should build one
 * through here rather than hand-writing a literal that silently goes stale the next time the DTO
 * grows a field.
 */
export function preFixProbe(
  partial: Pick<MediaProbe, 'durationSec' | 'fpsNum' | 'fpsDen' | 'width' | 'height'>,
): MediaProbe {
  return {
    videoCodec: null,
    audioCodec: null,
    container: null,
    audioChannels: null,
    audioSampleRateHz: null,
    bitrateBps: null,
    ...partial,
  };
}

/** The project every fixture belongs to. */
export const FIXTURE_PROJECT_ID = uuid(9);

/** Stable ids, so a failure message names the same thing every run. */
export const FIXTURE_IDS = {
  videoFile: uuid(101),
  stepResultRender: uuid(102),
  audioFile: uuid(103),
  imageFile: uuid(104),
  proxyRow: uuid(201),
  waveformRow: uuid(202),
  thumbnailRow: uuid(203),
  nullFieldProxyRow: uuid(204),
  waveformPeaksRow: uuid(205),
  filmstripRow: uuid(206),
  keyframeIndexRow: uuid(207),
  stillsRow: uuid(209),
  stabilizedProxyRow: uuid(210),
  rushesBin: uuid(301),
  interviewsBin: uuid(302),
  bRollBin: uuid(303),
  musicBin: uuid(304),
  videoPin: uuid(401),
  audioPinA: uuid(402),
  audioPinB: uuid(403),
  danglingPin: uuid(404),
  sheetRow: uuid(208),
} as const;

// ─────────────────────────────────────────────────────────────────────────────
// Pool items — both prefixes, every kind, and the two "never analysed" shapes
// ─────────────────────────────────────────────────────────────────────────────

/** A `file:` video with a fully populated probe. */
export const VIDEO_FILE_ITEM: MediaPoolItem = {
  mediaRef: `file:${FIXTURE_IDS.videoFile}`,
  kind: 'video',
  fileName: 'b-roll-city.mp4',
  mimeType: 'video/mp4',
  sizeBytes: 48_112_004,
  category: 'userFiles',
  probe: {
    durationSec: 12.5,
    fpsNum: 30000,
    fpsDen: 1001,
    width: 1920,
    height: 1080,
    videoCodec: 'h264',
    audioCodec: 'aac',
    // ffprobe's format_name VERBATIM for a sniffed MP4 — a probe list, not one container name.
    container: 'mov,mp4,m4a,3gp,3g2,mj2',
    audioChannels: 2,
    audioSampleRateHz: 48000,
    bitrateBps: 30_791_683,
  },
  bins: [{ binId: FIXTURE_IDS.rushesBin, itemId: FIXTURE_IDS.videoPin }],
  filmstripUrl: mediaFilmstripKey(FIXTURE_PROJECT_ID, `file:${FIXTURE_IDS.videoFile}`),
};

/** A `step_result:` render that was never analysed — `probe` is null, not invented. */
export const STEP_RESULT_ITEM: MediaPoolItem = {
  mediaRef: `step_result:${FIXTURE_IDS.stepResultRender}`,
  kind: 'video',
  fileName: 'render-pass-1.mp4',
  mimeType: 'video/mp4',
  sizeBytes: 9_400_120,
  category: 'outputFiles',
  probe: null,
  bins: [],
  filmstripUrl: mediaFilmstripKey(FIXTURE_PROJECT_ID, `step_result:${FIXTURE_IDS.stepResultRender}`),
};

/** An audio file whose probe EXISTS but carries null fields — every one must degrade, not NaN. */
export const AUDIO_FILE_ITEM: MediaPoolItem = {
  mediaRef: `file:${FIXTURE_IDS.audioFile}`,
  kind: 'audio',
  fileName: 'interview-room-tone.wav',
  mimeType: 'audio/wav',
  sizeBytes: 7_006_400,
  category: 'agentFiles',
  probe: {
    durationSec: null,
    fpsNum: null,
    fpsDen: null,
    width: null,
    height: null,
    videoCodec: null,
    audioCodec: null,
    container: null,
    audioChannels: null,
    audioSampleRateHz: null,
    bitrateBps: null,
  },
  bins: [
    { binId: FIXTURE_IDS.musicBin, itemId: FIXTURE_IDS.audioPinA },
    { binId: FIXTURE_IDS.rushesBin, itemId: FIXTURE_IDS.audioPinB },
  ],
  filmstripUrl: mediaFilmstripKey(FIXTURE_PROJECT_ID, `file:${FIXTURE_IDS.audioFile}`),
};

/** A still image with no probe and no derivatives — the emptiest legitimate pool item. */
export const IMAGE_FILE_ITEM: MediaPoolItem = {
  mediaRef: `file:${FIXTURE_IDS.imageFile}`,
  kind: 'image',
  fileName: 'logo-mark.png',
  mimeType: 'image/png',
  sizeBytes: 24_480,
  category: 'userFiles',
  probe: null,
  bins: [],
  filmstripUrl: mediaFilmstripKey(FIXTURE_PROJECT_ID, `file:${FIXTURE_IDS.imageFile}`),
};

/** The default pool: four items covering both prefixes, all three kinds and both probe states. */
export const POOL: MediaPoolItem[] = [
  VIDEO_FILE_ITEM,
  STEP_RESULT_ITEM,
  AUDIO_FILE_ITEM,
  IMAGE_FILE_ITEM,
];

/** A pool with a single item that is never analysed and has no derivatives. */
export const POOL_UNANALYSED_ONLY: MediaPoolItem[] = [IMAGE_FILE_ITEM];

/** The loading/empty shape the pool legitimately returns for a project with nothing in it. */
export const EMPTY_POOL: MediaPoolItem[] = [];

// ─────────────────────────────────────────────────────────────────────────────
// Derivatives — a flat row list carrying ALL THREE kinds
// ─────────────────────────────────────────────────────────────────────────────

const contentUrl = (id: string): string =>
  derivativeContentUrl(FIXTURE_PROJECT_ID, { id, contentUrl: '' });

/** One list carrying a row of EVERY landed derivative kind (all eight). */
export const DERIVATIVES_ALL_KINDS: MediaDerivativeRow[] = [
  {
    id: FIXTURE_IDS.proxyRow,
    kind: 'proxy',
    mimeType: 'video/mp4',
    sizeBytes: 4_311_040,
    durationSec: 12.5,
    width: 960,
    height: 540,
    contentUrl: contentUrl(FIXTURE_IDS.proxyRow),
  },
  {
    id: FIXTURE_IDS.waveformRow,
    kind: 'waveform',
    mimeType: 'image/png',
    sizeBytes: 51_200,
    durationSec: 12.5,
    width: 1800,
    height: 120,
    contentUrl: contentUrl(FIXTURE_IDS.waveformRow),
  },
  {
    id: FIXTURE_IDS.thumbnailRow,
    kind: 'thumbnail',
    mimeType: 'image/jpeg',
    sizeBytes: 18_004,
    durationSec: null,
    width: 320,
    height: 180,
    contentUrl: contentUrl(FIXTURE_IDS.thumbnailRow),
  },
  {
    id: FIXTURE_IDS.waveformPeaksRow,
    kind: 'waveformPeaks',
    mimeType: 'application/json',
    sizeBytes: 1_220_480,
    durationSec: 12.5,
    width: null,
    height: null,
    contentUrl: contentUrl(FIXTURE_IDS.waveformPeaksRow),
  },
  {
    // A filmstrip row exists in the model, so a listing can carry one. What its object holds is the
    // backend's business — this fixture pins only that the row is accepted and its kind survives.
    id: FIXTURE_IDS.filmstripRow,
    kind: 'filmstrip',
    mimeType: 'application/json',
    sizeBytes: 4_096,
    durationSec: null,
    width: null,
    height: null,
    contentUrl: contentUrl(FIXTURE_IDS.filmstripRow),
  },
  {
    id: FIXTURE_IDS.keyframeIndexRow,
    kind: 'keyframeIndex',
    mimeType: 'application/json',
    sizeBytes: 65_536,
    durationSec: null,
    width: null,
    height: null,
    contentUrl: contentUrl(FIXTURE_IDS.keyframeIndexRow),
  },
  {
    id: FIXTURE_IDS.stillsRow,
    kind: 'stills',
    mimeType: 'application/json',
    sizeBytes: 2_048,
    durationSec: null,
    width: null,
    height: null,
    contentUrl: contentUrl(FIXTURE_IDS.stillsRow),
  },
  {
    id: FIXTURE_IDS.stabilizedProxyRow,
    kind: 'stabilizedProxy',
    mimeType: 'video/mp4',
    sizeBytes: 4_400_000,
    durationSec: 12.5,
    width: 960,
    height: 540,
    contentUrl: contentUrl(FIXTURE_IDS.stabilizedProxyRow),
  },
];

/** A single row whose nullable fields are all null — the "no NaN / no undefined" stress case. */
export const DERIVATIVES_NULL_FIELDS: MediaDerivativeRow[] = [
  {
    id: FIXTURE_IDS.nullFieldProxyRow,
    kind: 'proxy',
    mimeType: null,
    sizeBytes: 0,
    durationSec: null,
    width: null,
    height: null,
    contentUrl: contentUrl(FIXTURE_IDS.nullFieldProxyRow),
  },
];

/** A source with no generated derivatives: an empty list, which is a normal state, not an error. */
export const DERIVATIVES_NONE: MediaDerivativeRow[] = [];

/** Every derivative list the component tests drive the UI with. */
export const DERIVATIVE_FIXTURES: { name: string; rows: MediaDerivativeRow[] }[] = [
  { name: 'all eight derivative kinds', rows: DERIVATIVES_ALL_KINDS },
  { name: 'all nullable fields null', rows: DERIVATIVES_NULL_FIELDS },
  { name: 'no derivatives generated', rows: DERIVATIVES_NONE },
];

// ─────────────────────────────────────────────────────────────────────────────
// Bins — a flat list expressing a three-deep tree through parentBinId only
// ─────────────────────────────────────────────────────────────────────────────

export const BINS: MediaBin[] = [
  { id: FIXTURE_IDS.rushesBin, name: 'Rushes', parentBinId: null, sortOrder: 0, itemCount: 12 },
  {
    id: FIXTURE_IDS.interviewsBin,
    name: 'Interviews',
    parentBinId: FIXTURE_IDS.rushesBin,
    sortOrder: 0,
    itemCount: 3,
  },
  {
    id: FIXTURE_IDS.bRollBin,
    name: 'B-roll',
    parentBinId: FIXTURE_IDS.interviewsBin,
    sortOrder: 0,
    itemCount: 0,
  },
  { id: FIXTURE_IDS.musicBin, name: 'Music', parentBinId: null, sortOrder: 1, itemCount: 2 },
];

/** Healthy defaults for optimistic-count assertions. */
export const EMPTY_BINS: MediaBin[] = [];

/**
 * The filmstrip index, in the landed wire shape — with a PARTIAL final sheet, so
 * `resolveFilmstripThumb` is exercised on a sheet that is not full.
 *
 * `contentUrl` is the server-built path that addresses a sheet through the derivative ROW owning the
 * density plus the sheet's own 0-based index — sheets 1..N-1 have no row of their own, which is why
 * a sheet carries a URL rather than a storage leaf.
 */
export const FILMSTRIP_INDEX: FilmstripIndex = {
  densities: [
    {
      densitySec: 1,
      thumbWidth: 160,
      thumbHeight: 90,
      cols: 10,
      rows: 10,
      sheetCount: 2,
      sheets: [
        {
          firstThumbIndex: 0,
          thumbCount: 100,
          firstTimeSec: 0,
          contentUrl: filmstripSheetContentUrl(FIXTURE_PROJECT_ID, FIXTURE_IDS.filmstripRow, 0),
        },
        {
          firstThumbIndex: 100,
          thumbCount: 37,
          firstTimeSec: 100,
          contentUrl: filmstripSheetContentUrl(FIXTURE_PROJECT_ID, FIXTURE_IDS.filmstripRow, 1),
        },
      ],
    },
  ],
};

/** The envelope `GET .../media/{mediaRef}/filmstrip` returns, wrapping {@link FILMSTRIP_INDEX}. */
export const FILMSTRIP_RESPONSE: MediaFilmstripResponse = { filmstrip: FILMSTRIP_INDEX };

/**
 * An index whose sheet bytes are unreachable — the per-sheet degradation. The sheet is KEPT (so the
 * flat thumbnail sequence is not renumbered) and its URL is empty, which the strip renders as a
 * stated per-cell state rather than a broken background.
 */
export const FILMSTRIP_INDEX_UNREACHABLE_SHEET: FilmstripIndex = {
  densities: [
    {
      densitySec: 1,
      thumbWidth: 160,
      thumbHeight: 90,
      cols: 10,
      rows: 10,
      sheetCount: 1,
      sheets: [{ firstThumbIndex: 0, thumbCount: 12, firstTimeSec: 0, contentUrl: '' }],
    },
  ],
};

/** An index that exists but carries no density at all — reported as zero, never guessed at. */
export const FILMSTRIP_INDEX_EMPTY: FilmstripIndex = { densities: [] };

/** A density that exists but carries no sheet at all — treated exactly like no density. */
export const FILMSTRIP_INDEX_NO_SHEETS: FilmstripIndex = {
  densities: [
    { densitySec: 1, thumbWidth: 160, thumbHeight: 90, cols: 10, rows: 10, sheetCount: 0, sheets: [] },
  ],
};

// ─────────────────────────────────────────────────────────────────────────────
// Bin item listings — the real membership read, including the dangling pin
// ─────────────────────────────────────────────────────────────────────────────

/**
 * What `GET .../media/bins/{rushesBin}/items` returns: two healthy rows and one DANGLING pin whose
 * source row is gone. The dangling row keeps its id and mediaRef precisely so it can be removed.
 */
export const BIN_ITEMS_RUSHES: MediaBinItemDetail[] = [
  {
    id: FIXTURE_IDS.videoPin,
    binId: FIXTURE_IDS.rushesBin,
    mediaRef: `file:${FIXTURE_IDS.videoFile}`,
    sourceKind: 'ProjectFile',
    sortOrder: 0,
    createdAt: '2026-10-03T08:00:00Z',
    kind: 'video',
    fileName: 'b-roll-city.mp4',
    mimeType: 'video/mp4',
    sizeBytes: 48_112_004,
  },
  {
    id: FIXTURE_IDS.audioPinB,
    binId: FIXTURE_IDS.rushesBin,
    mediaRef: `file:${FIXTURE_IDS.audioFile}`,
    sourceKind: 'ProjectFile',
    sortOrder: 1,
    createdAt: '2026-10-03T08:01:00Z',
    kind: 'audio',
    fileName: 'interview-room-tone.wav',
    mimeType: 'audio/wav',
    sizeBytes: 7_006_400,
  },
  {
    id: FIXTURE_IDS.danglingPin,
    binId: FIXTURE_IDS.rushesBin,
    mediaRef: `file:${FIXTURE_IDS.imageFile}`,
    sourceKind: 'ProjectFile',
    sortOrder: 2,
    createdAt: '2026-10-03T08:02:00Z',
    // Every resolution field is null: the source row behind this pin is gone. Nothing is defaulted
    // in its place — a 0-byte file or a blank name would be a fabricated fact.
    kind: null,
    fileName: null,
    mimeType: null,
    sizeBytes: null,
  },
];

/** What the Music bin holds — a single healthy pin, so a second bin's listing is distinguishable. */
export const BIN_ITEMS_MUSIC: MediaBinItemDetail[] = [
  {
    id: FIXTURE_IDS.audioPinA,
    binId: FIXTURE_IDS.musicBin,
    mediaRef: `file:${FIXTURE_IDS.audioFile}`,
    sourceKind: 'ProjectFile',
    sortOrder: 0,
    createdAt: '2026-10-03T08:03:00Z',
    kind: 'audio',
    fileName: 'interview-room-tone.wav',
    mimeType: 'audio/wav',
    sizeBytes: 7_006_400,
  },
];

/** An empty bin's listing: an empty array, which is a normal answer and not an error. */
export const BIN_ITEMS_EMPTY: MediaBinItemDetail[] = [];

/** Every bin listing the component tests drive the UI with, keyed by the bin it belongs to. */
export const BIN_ITEM_FIXTURES: { name: string; binId: string; rows: MediaBinItemDetail[] }[] = [
  { name: 'two healthy pins and one dangling pin', binId: FIXTURE_IDS.rushesBin, rows: BIN_ITEMS_RUSHES },
  { name: 'one healthy pin', binId: FIXTURE_IDS.musicBin, rows: BIN_ITEMS_MUSIC },
  { name: 'no pins', binId: FIXTURE_IDS.bRollBin, rows: BIN_ITEMS_EMPTY },
];

/** The `202 Accepted` body a derivative request returns. */
export const DERIVATIVE_REQUEST_ACCEPTED: RequestMediaDerivativesResponse = {
  mediaRef: `file:${FIXTURE_IDS.videoFile}`,
  kinds: ['proxy', 'waveform', 'waveformPeaks', 'thumbnail', 'filmstrip', 'keyframeIndex'],
  requestedBy: 'editor-open',
};

// ─────────────────────────────────────────────────────────────────────────────
// The validation manifest
// ─────────────────────────────────────────────────────────────────────────────

/** Every valid fixture, paired with the `$defs` entry it must satisfy. */
export type FixtureDef =
  | 'MediaPoolList'
  | 'MediaDerivativeList'
  | 'MediaBinList'
  | 'MediaBinItemDetailList'
  | 'MediaFilmstripResponse'
  | 'RequestMediaDerivativesResponse';

export const VALID_FIXTURES: {
  name: string;
  def: FixtureDef;
  data: unknown;
}[] = [
  { name: 'pool with both mediaRef prefixes and probe: null', def: 'MediaPoolList', data: POOL },
  { name: 'pool with a single unanalysed item', def: 'MediaPoolList', data: POOL_UNANALYSED_ONLY },
  { name: 'empty pool', def: 'MediaPoolList', data: EMPTY_POOL },
  ...DERIVATIVE_FIXTURES.map((f) => ({
    name: `derivatives: ${f.name}`,
    def: 'MediaDerivativeList' as const,
    data: f.rows,
  })),
  { name: 'nested bins', def: 'MediaBinList', data: BINS },
  { name: 'no bins', def: 'MediaBinList', data: EMPTY_BINS },
  ...BIN_ITEM_FIXTURES.map((f) => ({
    name: `bin items: ${f.name}`,
    def: 'MediaBinItemDetailList' as const,
    data: f.rows,
  })),
  { name: 'filmstrip index with a partial final sheet', def: 'MediaFilmstripResponse', data: FILMSTRIP_RESPONSE },
  {
    name: 'filmstrip index with zero densities',
    def: 'MediaFilmstripResponse',
    data: { filmstrip: FILMSTRIP_INDEX_EMPTY },
  },
  {
    name: 'a derivative request acceptance',
    def: 'RequestMediaDerivativesResponse',
    data: DERIVATIVE_REQUEST_ACCEPTED,
  },
];

/**
 * Deliberately-invalid fixtures. Each names the one thing it breaks, so a failure reports WHAT
 * stopped validating rather than merely that something did.
 */
export const INVALID_FIXTURES: {
  name: string;
  def: FixtureDef;
  data: unknown;
}[] = [
  {
    // A bare GUID instead of an opaque token: the single most likely way to get this contract
    // wrong, and the reason the token exists at all.
    name: 'a pool item whose mediaRef is a bare GUID with no prefix',
    def: 'MediaPoolList',
    data: [{ ...VIDEO_FILE_ITEM, mediaRef: FIXTURE_IDS.videoFile }],
  },
  {
    name: 'a pool item missing its required probe key',
    def: 'MediaPoolList',
    data: [{ ...STEP_RESULT_ITEM, probe: undefined }],
  },
  {
    name: 'a pool item claiming an unknown media kind',
    def: 'MediaPoolList',
    data: [{ ...VIDEO_FILE_ITEM, kind: 'hologram' }],
  },
  {
    name: 'a derivative row claiming an unknown derivative kind',
    def: 'MediaDerivativeList',
    data: [{ ...DERIVATIVES_ALL_KINDS[0], kind: 'subtitle' }],
  },
  {
    name: 'a derivative row with no contentUrl',
    def: 'MediaDerivativeList',
    data: [{ ...DERIVATIVES_ALL_KINDS[0], contentUrl: '' }],
  },
  {
    name: 'a bin with no itemCount',
    def: 'MediaBinList',
    data: [{ id: FIXTURE_IDS.rushesBin, name: 'Rushes', parentBinId: null, sortOrder: 0 }],
  },
  {
    name: 'a bin whose name is empty',
    def: 'MediaBinList',
    data: [{ ...BINS[0], name: '' }],
  },
  {
    // The membership list is how a removal learns its itemId; a pin that cannot name its bin is
    // unusable, and this is the shape that would silently strand a clip in a bin forever.
    name: 'a pool item whose bin membership has no itemId',
    def: 'MediaPoolList',
    data: [{ ...VIDEO_FILE_ITEM, bins: [{ binId: FIXTURE_IDS.rushesBin }] }],
  },
  {
    name: 'a pool item missing its filmstripUrl',
    def: 'MediaPoolList',
    data: [{ ...VIDEO_FILE_ITEM, filmstripUrl: '' }],
  },
  {
    name: 'a probe whose container is a number',
    def: 'MediaPoolList',
    data: [
      {
        ...VIDEO_FILE_ITEM,
        probe: { ...VIDEO_FILE_ITEM.probe, container: 42 },
      },
    ],
  },
  {
    name: 'a bin item detail missing its kind key',
    def: 'MediaBinItemDetailList',
    data: [{ ...BIN_ITEMS_RUSHES[0], kind: undefined }],
  },
  {
    name: 'a filmstrip sheet whose contentUrl is empty (the wire always sends one)',
    def: 'MediaFilmstripResponse',
    data: {
      filmstrip: {
        densities: [
          {
            densitySec: 1,
            thumbWidth: 160,
            thumbHeight: 90,
            cols: 10,
            rows: 10,
            sheetCount: 1,
            sheets: [{ firstThumbIndex: 0, thumbCount: 12, firstTimeSec: 0, contentUrl: '' }],
          },
        ],
      },
    },
  },
  {
    name: 'a density whose cols is zero',
    def: 'MediaFilmstripResponse',
    data: {
      filmstrip: {
        densities: [
          {
            densitySec: 1,
            thumbWidth: 160,
            thumbHeight: 90,
            cols: 0,
            rows: 10,
            sheetCount: 0,
            sheets: [],
          },
        ],
      },
    },
  },
];
