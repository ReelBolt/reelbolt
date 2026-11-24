/**
 * TypeScript mirror of the renderer's camera log profile library
 * (inference/src/ReelBolt.Shared/Editing/Color/CameraProfiles.cs and CameraDisplayTransform.cs).
 * Pure functions and data; no DOM. The C# side is authoritative: same constants, same formulas,
 * same order of operations, so the viewer shows exactly what the renderer exports.
 *
 * Our own implementation of the published curves and gamut matrices (no vendor LUT file is used
 * or redistributed). Matrices are row-major linear camera gamut to linear Rec.709 (D65 to D65).
 */

export type CameraProfileStatus = 'uncalibrated' | 'approximate' | 'calibrated';
export type CameraProfileRange = 'full' | 'legal';
export type CameraProfileYuvMatrix = 'bt709' | 'bt2020nc';

/** Row-major 3x3 matrix taking linear camera-gamut RGB to linear Rec.709 RGB. */
export type CameraGamutMatrix = readonly [
  number,
  number,
  number,
  number,
  number,
  number,
  number,
  number,
  number,
];

export interface CameraProfileDefinition {
  /** Stable camelCase identifier, persisted by name. */
  readonly id: CameraProfileId;
  /** Factual maker name of the curve, for example "Apple Log". */
  readonly displayName: string;
  /** The camera maker. */
  readonly maker: string;
  /** How far the profile has been verified. */
  readonly status: CameraProfileStatus;
  /** The signal range the profile expects, "full" or "legal"; the profile decides, not the file tag. */
  readonly expectedRange: CameraProfileRange;
  /** The YUV matrix the profile expects, "bt709" or "bt2020nc"; the profile decides, not the file tag. */
  readonly expectedYuvMatrix: CameraProfileYuvMatrix;
  /** Normalised code value 0..1 to scene linear, where 0.18 is 18% grey. */
  readonly decodeToLinear: (code: number) => number;
  /** Row-major 3x3 matrix taking linear camera-gamut RGB to linear Rec.709 RGB. */
  readonly gamutToRec709: CameraGamutMatrix;
}

/** The closed set of camera log profiles, in the canonical order. */
export const CAMERA_PROFILE_IDS = [
  'appleLog',
  'djiDLog',
  'djiDLogM',
  'sonySLog3',
  'panasonicVLog',
  'canonCLog3',
  'arriLogC3',
  'arriLogC4',
  'fujiFLog',
  'fujiFLog2',
  'nikonNLog',
  'redLog3G10',
] as const;

export type CameraProfileId = (typeof CAMERA_PROFILE_IDS)[number];

// BT.2020 -> Rec.709 (derived; equals ITU-R BT.2407).
const BT_2020: CameraGamutMatrix = [
  1.660491, -0.587641, -0.07285, -0.12455, 1.1329, -0.008349, -0.018151, -0.100579, 1.11873,
];

// D-Gamut -> Rec.709, as published by DJI.
const D_GAMUT: CameraGamutMatrix = [
  1.6746, -0.5797, -0.0949, -0.0981, 1.334, -0.2359, -0.041, -0.243, 1.284,
];

// S-Gamut3.Cine -> Rec.709 (derived).
const S_GAMUT3_CINE: CameraGamutMatrix = [
  1.626947, -0.540139, -0.086809, -0.178516, 1.417941, -0.239425, -0.044436, -0.19592, 1.240356,
];

// V-Gamut -> Rec.709, as published by Panasonic.
const V_GAMUT: CameraGamutMatrix = [
  1.806576, -0.695697, -0.110879, -0.17009, 1.305955, -0.135865, -0.025206, -0.154468, 1.179674,
];

// Canon Cinema Gamut -> Rec.709 (derived).
const CINEMA_GAMUT: CameraGamutMatrix = [
  1.923861, -0.798761, -0.125101, -0.204311, 1.495899, -0.291588, -0.023685, -0.420127, 1.443812,
];

// ALEXA Wide Gamut (AWG3) -> Rec.709, as published by ARRI.
const AWG3: CameraGamutMatrix = [
  1.617523, -0.537287, -0.080237, -0.070573, 1.334613, -0.26404, -0.021102, -0.226954, 1.248056,
];

// ARRI Wide Gamut 4 -> Rec.709 (derived from ARRI's published XYZ matrix).
const AWG4: CameraGamutMatrix = [
  1.893123443, -0.780881504, -0.112241939, -0.205700358, 1.340257489, -0.134557131, -0.012705743,
  -0.152184876, 1.164890619,
];

// REDWideGamutRGB -> Rec.709, as published by RED.
const RED_WIDE_GAMUT: CameraGamutMatrix = [
  1.98188, -0.900388, -0.08154, -0.178143, 1.500467, -0.322325, -0.101811, -0.535343, 1.637304,
];

// LogC4 constants, evaluated from the equations of the ARRI LogC4 specification (23 Jan 2025).
const C4_A = (262144.0 - 16.0) / 117.45;
const C4_B = (1023.0 - 95.0) / 1023.0;
const C4_C = 95.0 / 1023.0;
const C4_S = (7.0 * Math.log(2.0) * Math.pow(2.0, 7.0 - (14.0 * C4_C) / C4_B)) / (C4_A * C4_B);
const C4_T = (Math.pow(2.0, 14.0 * (-C4_C / C4_B) + 6.0) - 64.0) / C4_A;

function appleLog(p: number): number {
  const r0 = -0.05641088;
  const rt = 0.01;
  const c = 47.28711236;
  const b = 0.00964052;
  const g = 0.08550479;
  const d = 0.69336945;
  const pt = c * (rt - r0) * (rt - r0);
  if (p >= pt) return Math.pow(2.0, (p - d) / g) - b;
  if (p >= 0) return Math.sqrt(p / c) + r0;
  return r0;
}

function djiDLog(x: number): number {
  return x <= 0.14 ? (x - 0.0929) / 6.025 : (Math.pow(10.0, 3.89616 * x - 2.27752) - 0.0108) / 0.9892;
}

function sonySLog3(x: number): number {
  return x >= 171.2102946929 / 1023.0
    ? Math.pow(10.0, (x * 1023.0 - 420.0) / 261.5) * (0.18 + 0.01) - 0.01
    : ((x * 1023.0 - 95.0) * 0.01125) / (171.2102946929 - 95.0);
}

function panasonicVLog(x: number): number {
  return x < 0.181 ? (x - 0.125) / 5.6 : Math.pow(10.0, (x - 0.598206) / 0.241514) - 0.00873;
}

function canonCLog3(y: number): number {
  let v: number;
  if (y < 0.097465473) v = -(Math.pow(10.0, (0.12783901 - y) / 0.36726845) - 1) / 14.98325;
  else if (y <= 0.15277891) v = (y - 0.12512219) / 1.9754798;
  else v = (Math.pow(10.0, (y - 0.12240537) / 0.36726845) - 1) / 14.98325;
  return 0.9 * v; // Canon's x has 1.0 = 90% white; grey 0.18 convention needs 0.9 * x.
}

function arriLogC3(t: number): number {
  const cut = 0.010591;
  const a = 5.555556;
  const b = 0.052272;
  const c = 0.24719;
  const d = 0.385537;
  const e = 5.367655;
  const f = 0.092809;
  return t > e * cut + f ? (Math.pow(10.0, (t - d) / c) - b) / a : (t - f) / e;
}

function arriLogC4(e: number): number {
  return e >= 0
    ? (Math.pow(2.0, (14.0 * (e - C4_C)) / C4_B + 6.0) - 64.0) / C4_A
    : e * C4_S + C4_T;
}

function fujiFLog(x: number): number {
  const a = 0.555556;
  const b = 0.009468;
  const c = 0.344676;
  const d = 0.790453;
  const e = 8.735631;
  const f = 0.092864;
  const cut2 = 0.100537775223865;
  return x >= cut2 ? Math.pow(10.0, (x - d) / c) / a - b / a : (x - f) / e;
}

function fujiFLog2(x: number): number {
  const a = 5.555556;
  const b = 0.064829;
  const c = 0.245281;
  const d = 0.384316;
  const e = 8.799461;
  const f = 0.092864;
  const cut2 = 0.100686685370811;
  return x >= cut2 ? Math.pow(10.0, (x - d) / c) / a - b / a : (x - f) / e;
}

function nikonNLog(x: number): number {
  const code = x * 1023.0;
  return code < 452 ? Math.pow(code / 650.0, 3.0) - 0.0075 : Math.exp((code - 619.0) / 150.0);
}

function redLog3G10(y: number): number {
  const a = 0.224282;
  const b = 155.975327;
  const c = 0.01;
  const g = 15.1927;
  return y < 0 ? y / g - c : (Math.pow(10.0, y / a) - 1.0) / b - c;
}

const PROFILES: Record<CameraProfileId, CameraProfileDefinition> = {
  appleLog: {
    id: 'appleLog',
    displayName: 'Apple Log',
    maker: 'Apple',
    status: 'uncalibrated',
    expectedRange: 'legal',
    expectedYuvMatrix: 'bt2020nc',
    decodeToLinear: appleLog,
    gamutToRec709: BT_2020,
  },
  djiDLog: {
    id: 'djiDLog',
    displayName: 'D-Log',
    maker: 'DJI',
    status: 'uncalibrated',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: djiDLog,
    gamutToRec709: D_GAMUT,
  },
  // APPROXIMATE stand-in: DJI publishes no D-Log M specification. The D-Log curve and D-Gamut
  // are used as a rough approximation; this is NOT a calibrated D-Log M decoder.
  djiDLogM: {
    id: 'djiDLogM',
    displayName: 'D-Log M',
    maker: 'DJI',
    status: 'approximate',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: djiDLog,
    gamutToRec709: D_GAMUT,
  },
  sonySLog3: {
    id: 'sonySLog3',
    displayName: 'S-Log3',
    maker: 'Sony',
    status: 'uncalibrated',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: sonySLog3,
    gamutToRec709: S_GAMUT3_CINE,
  },
  panasonicVLog: {
    id: 'panasonicVLog',
    displayName: 'V-Log',
    maker: 'Panasonic',
    status: 'uncalibrated',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: panasonicVLog,
    gamutToRec709: V_GAMUT,
  },
  canonCLog3: {
    id: 'canonCLog3',
    displayName: 'Canon Log 3',
    maker: 'Canon',
    status: 'uncalibrated',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: canonCLog3,
    gamutToRec709: CINEMA_GAMUT,
  },
  arriLogC3: {
    id: 'arriLogC3',
    displayName: 'LogC3',
    maker: 'ARRI',
    status: 'uncalibrated',
    expectedRange: 'legal',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: arriLogC3,
    gamutToRec709: AWG3,
  },
  arriLogC4: {
    id: 'arriLogC4',
    displayName: 'LogC4',
    maker: 'ARRI',
    status: 'uncalibrated',
    expectedRange: 'legal',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: arriLogC4,
    gamutToRec709: AWG4,
  },
  fujiFLog: {
    id: 'fujiFLog',
    displayName: 'F-Log',
    maker: 'Fujifilm',
    status: 'uncalibrated',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt2020nc',
    decodeToLinear: fujiFLog,
    gamutToRec709: BT_2020,
  },
  fujiFLog2: {
    id: 'fujiFLog2',
    displayName: 'F-Log2',
    maker: 'Fujifilm',
    status: 'uncalibrated',
    expectedRange: 'full',
    expectedYuvMatrix: 'bt2020nc',
    decodeToLinear: fujiFLog2,
    gamutToRec709: BT_2020,
  },
  nikonNLog: {
    id: 'nikonNLog',
    displayName: 'N-Log',
    maker: 'Nikon',
    status: 'uncalibrated',
    expectedRange: 'legal',
    expectedYuvMatrix: 'bt2020nc',
    decodeToLinear: nikonNLog,
    gamutToRec709: BT_2020,
  },
  redLog3G10: {
    id: 'redLog3G10',
    displayName: 'Log3G10',
    maker: 'RED',
    status: 'uncalibrated',
    expectedRange: 'legal',
    expectedYuvMatrix: 'bt709',
    decodeToLinear: redLog3G10,
    gamutToRec709: RED_WIDE_GAMUT,
  },
};

/** Looks a profile up by its exact id. */
export function getCameraProfile(id: CameraProfileId): CameraProfileDefinition {
  return PROFILES[id];
}

/** True when `value` names a known profile. */
export function isCameraProfileId(value: unknown): value is CameraProfileId {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(PROFILES, value);
}

/** Highlight shoulder knee; the curve is the identity below it. */
export const CAMERA_DISPLAY_TRANSFORM_KNEE = 0.45;

function clamp01(value: number): number {
  return value < 0 ? 0 : value > 1 ? 1 : value;
}

function shoulder(x: number): number {
  const knee = CAMERA_DISPLAY_TRANSFORM_KNEE;
  return x <= knee ? x : knee + (1 - knee) * Math.tanh((x - knee) / (1 - knee));
}

function oetf(y: number): number {
  return y < 0.018 ? 4.5 * y : 1.099 * Math.pow(y, 0.45) - 0.099;
}

/**
 * Converts a normalised camera code triple (0..1, clamped) to a Rec.709 OETF-encoded full-swing
 * signal (0..1). Mirrors `CameraDisplayTransform.ToRec709`; range scaling to legal levels is the
 * renderer's job.
 */
export function toRec709(
  def: CameraProfileDefinition,
  r: number,
  g: number,
  b: number,
  exposureEv = 0,
): [number, number, number] {
  const lr = def.decodeToLinear(clamp01(r));
  const lg = def.decodeToLinear(clamp01(g));
  const lb = def.decodeToLinear(clamp01(b));
  const m = def.gamutToRec709;
  const gain = exposureEv === 0 ? 1.0 : Math.pow(2.0, exposureEv);
  const outR = Math.max(0, m[0] * lr + m[1] * lg + m[2] * lb) * gain;
  const outG = Math.max(0, m[3] * lr + m[4] * lg + m[5] * lb) * gain;
  const outB = Math.max(0, m[6] * lr + m[7] * lg + m[8] * lb) * gain;
  return [oetf(shoulder(outR)), oetf(shoulder(outG)), oetf(shoulder(outB))];
}
