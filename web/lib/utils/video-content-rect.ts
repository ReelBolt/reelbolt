export interface ContentRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Where a video's picture actually sits inside an element showing it with
 * `object-fit: contain`. Overlays must be drawn in this rectangle, not across
 * the whole element: a portrait video in a landscape monitor is pillarboxed,
 * and coordinates normalized to the picture land in the black bars otherwise.
 * Falls back to the whole element while the video's dimensions are unknown.
 */
export function containedContentRect(
  boxWidth: number,
  boxHeight: number,
  mediaWidth: number | undefined,
  mediaHeight: number | undefined
): ContentRect {
  if (!mediaWidth || !mediaHeight || boxWidth <= 0 || boxHeight <= 0) {
    return { x: 0, y: 0, width: boxWidth, height: boxHeight };
  }

  const scale = Math.min(boxWidth / mediaWidth, boxHeight / mediaHeight);
  const width = mediaWidth * scale;
  const height = mediaHeight * scale;
  return { x: (boxWidth - width) / 2, y: (boxHeight - height) / 2, width, height };
}
