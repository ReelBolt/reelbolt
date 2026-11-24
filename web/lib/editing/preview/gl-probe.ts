/**
 * The ONE throw-away WebGL2 context of the page: it answers "can this browser make the compositor's
 * context" and "is its renderer a CPU rasteriser" from a single `getContext`, then releases it
 * (`WEBGL_lose_context`) so the only context the page keeps is the compositor's own. Browsers cap live
 * contexts (about 16); two probes plus the compositor used to eat into that.
 *
 * The probe asks for the compositor's exact attributes (`COMPOSITOR_CONTEXT_ATTRIBUTES`): a browser that
 * can make a default-attribute context but not these must not pass the probe and then fail in the
 * compositor.
 */

import { COMPOSITOR_CONTEXT_ATTRIBUTES, isSoftwareRenderer } from './gl-compositor';

export interface GlProbe {
  /** A WebGL2 context with the compositor's attributes could be created. */
  webGl2: boolean;
  /** The context is a CPU rasteriser (SwiftShader, llvmpipe...). */
  software: boolean;
}

let cached: GlProbe | null = null;

export function probeGl(): GlProbe {
  if (cached) return cached;
  if (typeof document === 'undefined') return { webGl2: false, software: false };
  let result: GlProbe = { webGl2: false, software: false };
  try {
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl2', COMPOSITOR_CONTEXT_ATTRIBUTES) as WebGL2RenderingContext | null;
    if (gl) {
      let renderer: string | null = null;
      try {
        const info = gl.getExtension?.('WEBGL_debug_renderer_info');
        renderer = info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : String(gl.getParameter(gl.RENDERER));
      } catch {
        renderer = null;
      }
      result = { webGl2: true, software: isSoftwareRenderer(renderer) };
      try {
        gl.getExtension?.('WEBGL_lose_context')?.loseContext();
      } catch {
        /* the context is released with its canvas anyway */
      }
    }
  } catch {
    result = { webGl2: false, software: false };
  }
  cached = result;
  return result;
}

/** Forget the answer so the next `probeGl` asks the browser again (the user turned graphics acceleration on, "Try again"). */
export function resetGlProbe(): void {
  cached = null;
}
