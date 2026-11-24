// ReelBolt block library: offline map helpers shared by the map-* blocks (MIT). Needs d3.
import {geoNaturalEarth1, geoPath, geoInterpolate} from 'd3';
import type {GeoProjection} from 'd3';
import {COUNTRY_DATA} from './worldData.ts';

export type LonLat = [lon: number, lat: number];
export type City = {name: string; lon: number; lat: number};
type Feature = {type: 'Feature'; properties: {iso3: string; iso2: string; name: string}; geometry: {type: 'MultiPolygon'; coordinates: number[][][][]}};

/** Decode the delta-encoded rings of one country into GeoJSON-like coordinates (degrees). */
const decode = (rings: number[][]): number[][][][] =>
  rings.map((flat) => {
    const ring: number[][] = [];
    let x = 0;
    let y = 0;
    for (let i = 0; i < flat.length; i += 2) {
      x += flat[i];
      y += flat[i + 1];
      ring.push([x / 10, y / 10]);
    }
    ring.push(ring[0]);
    return [ring];
  });

let cache: Feature[] | null = null;
/** All countries as features (decoded once). */
export const countryFeatures = (): Feature[] => {
  cache ??= COUNTRY_DATA.map(([iso3, iso2, name, rings]) => ({
    type: 'Feature' as const,
    properties: {iso3, iso2, name},
    geometry: {type: 'MultiPolygon' as const, coordinates: decode(rings)},
  }));
  return cache;
};

/** Find a country by ISO alpha-3 or alpha-2 code (case-insensitive), or undefined. */
export const findCountry = (code: string): Feature | undefined => {
  const c = code.toUpperCase();
  return countryFeatures().find((f) => f.properties.iso3 === c || f.properties.iso2 === c);
};

/** Natural Earth projection fitted to the frame inside the 5% safe area (a little more for map margins). */
export const worldProjection = (width: number, height: number, margin = 0.07): GeoProjection => {
  const mx = width * margin;
  const my = height * margin;
  return geoNaturalEarth1().fitExtent([[mx, my], [width - mx, height - my]], {type: 'Sphere'});
};

/** SVG path string for all land, for a projection. */
export const landPath = (projection: GeoProjection): string =>
  geoPath(projection)({type: 'FeatureCollection', features: countryFeatures()} as never) ?? '';

/** Points along the great circle between two places, `steps + 1` of them. */
export const greatCircle = (a: LonLat, b: LonLat, steps = 80): LonLat[] => {
  const f = geoInterpolate(a, b);
  return Array.from({length: steps + 1}, (_, i) => f(i / steps) as LonLat);
};

/** Label placement that keeps a label inside the frame: flip to the left of the pin near the right edge. */
export const labelSide = (x: number, width: number): 'left' | 'right' => (x > width * 0.68 ? 'left' : 'right');
