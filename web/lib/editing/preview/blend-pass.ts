/**
 * Blend modes in the viewer. The export blends a clip's picture with everything composited below it in
 * 8-bit gamma-encoded RGB (`BlendModes.cs` is the table; `docs/editor.md` "Blend modes"); this module is
 * the viewer's twin of that table, in two forms that cannot drift apart because they sit side by side:
 *
 *   - {@link blendChannel}: a pure TS function of (mode, backdrop A, layer B), integers 0..255, truncating
 *     integer division exactly where the C# says so. The tests pin it against the C# table.
 *   - the GLSL generated from the SAME {@link BLEND_TABLE} rows ({@link blendFragmentSource}).
 *
 * `out = A + alpha * (blend(A, B) - A)`: A is the backdrop (black under the bottom-most picture), B the
 * layer, alpha = opacity x fades x mask (whatever the layer drew into its own target).
 *
 * The GL side ({@link createBlendPass}) is only used for a frame that has a blending layer: those frames
 * composite into a ping-pong pair of offscreen textures so the shader can read the backdrop. A frame with
 * no blending layer never touches any of this (the compositor's normal path is unchanged).
 */

/** The blend words (everything `normal` is plain alpha-over and never goes through this module). */
export type BlendWord = 'normal' | 'multiply' | 'screen' | 'overlay' | 'softLight' | 'hardLight' | 'darken' | 'lighten' | 'add' | 'difference';

interface BlendRow {
  word: Exclude<BlendWord, 'normal'>;
  /** A = backdrop, B = layer, both integers 0..255. Result is an integer, clamped to 0..255 by the caller. */
  ts: (a: number, b: number) => number;
  /** The same formula as a GLSL ES 3.0 `int` expression over `int a, b`. */
  glsl: string;
}

const mulX = (x: number, a: number, b: number): number => x * Math.trunc((a * b) / 255);
const scrX = (x: number, a: number, b: number): number => 255 - x * Math.trunc(((255 - a) * (255 - b)) / 255);

/** The catalogue minus `normal`, in the C# `BlendModes.Words` order. The row index + 1 is the shader's mode id. */
export const BLEND_TABLE: readonly BlendRow[] = [
  { word: 'multiply', ts: (a, b) => mulX(1, a, b), glsl: '(a * b) / 255' },
  { word: 'screen', ts: (a, b) => scrX(1, a, b), glsl: '255 - ((255 - a) * (255 - b)) / 255' },
  {
    word: 'overlay',
    ts: (a, b) => (a < 128 ? mulX(2, a, b) : scrX(2, a, b)),
    glsl: '(a < 128) ? 2 * ((a * b) / 255) : 255 - 2 * (((255 - a) * (255 - b)) / 255)',
  },
  {
    word: 'softLight',
    // Pegtop; float, truncated, clamped. (ffmpeg all_expr: A*A/255+2*B*A*(255-A)/65025)
    ts: (a, b) => Math.floor((a * a) / 255 + (2 * b * a * (255 - a)) / 65025),
    glsl: 'int(clamp(floor(float(a) * float(a) / 255.0 + 2.0 * float(b) * float(a) * (255.0 - float(a)) / 65025.0), 0.0, 255.0))',
  },
  {
    word: 'hardLight',
    ts: (a, b) => (b < 128 ? mulX(2, a, b) : scrX(2, a, b)),
    glsl: '(b < 128) ? 2 * ((a * b) / 255) : 255 - 2 * (((255 - a) * (255 - b)) / 255)',
  },
  { word: 'darken', ts: (a, b) => Math.min(a, b), glsl: 'min(a, b)' },
  { word: 'lighten', ts: (a, b) => Math.max(a, b), glsl: 'max(a, b)' },
  { word: 'add', ts: (a, b) => Math.min(255, a + b), glsl: 'min(255, a + b)' },
  { word: 'difference', ts: (a, b) => Math.abs(a - b), glsl: 'abs(a - b)' },
];

const ROW_BY_WORD = new Map<string, { row: BlendRow; id: number }>(BLEND_TABLE.map((row, i) => [row.word, { row, id: i + 1 }]));

/** True when the word draws through the blend shader (a known word other than `normal`). */
export function isBlendingWord(word: string | null | undefined): boolean {
  return typeof word === 'string' && ROW_BY_WORD.has(word);
}

/** The shader's mode id for a blending word (1-based), or 0 for `normal`/unknown. */
export function blendModeId(word: string | null | undefined): number {
  return (typeof word === 'string' ? ROW_BY_WORD.get(word)?.id : undefined) ?? 0;
}

/** One 8-bit channel of `blend(A, B)`; `normal`/unknown returns the layer (B), as plain alpha-over would show at full alpha. */
export function blendChannel(word: string, a: number, b: number): number {
  const hit = ROW_BY_WORD.get(word);
  if (!hit) return b;
  return Math.max(0, Math.min(255, hit.row.ts(a, b)));
}

/** The composited pixel: backdrop `a`, layer colour `b` (RGB 0..255) at `alpha` 0..1, rounded to 8 bits like the render target. */
export function blendPixel(word: string, a: readonly [number, number, number], b: readonly [number, number, number], alpha: number): [number, number, number] {
  const out = [0, 0, 0] as [number, number, number];
  for (let i = 0; i < 3; i++) {
    const blended = blendChannel(word, a[i], b[i]);
    out[i] = Math.round(a[i] + alpha * (blended - a[i]));
  }
  return out;
}

/** The full-screen vertex shader (no attributes: a single oversized triangle from `gl_VertexID`). */
export const BLEND_VERTEX_SHADER = `#version 300 es
out vec2 vUv;
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  vUv = p;
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

/** The blend fragment shader, generated from {@link BLEND_TABLE}. `uMode` < 0 copies the backdrop. */
export function blendFragmentSource(): string {
  const branches = BLEND_TABLE.map((row, i) => `  if (mode == ${i + 1}) return ${row.glsl};`).join('\n');
  return `#version 300 es
precision highp float;
precision highp int;
uniform sampler2D uBackdrop;
uniform sampler2D uLayer;
uniform int uMode;
in vec2 vUv;
out vec4 outColor;
int blendCh(int mode, int a, int b) {
${branches}
  return b;
}
void main() {
  vec4 back = texture(uBackdrop, vUv);
  if (uMode < 0) {
    outColor = vec4(back.rgb, 1.0);
    return;
  }
  // The layer target holds premultiplied colour (see layerBegin); un-premultiply to the layer's own colour.
  vec4 lay = texture(uLayer, vUv);
  float alpha = lay.a;
  if (alpha <= 0.0) {
    outColor = vec4(back.rgb, 1.0);
    return;
  }
  ivec3 A = ivec3(floor(back.rgb * 255.0 + 0.5));
  ivec3 B = ivec3(clamp(floor(lay.rgb / alpha * 255.0 + 0.5), 0.0, 255.0));
  vec3 a = vec3(A);
  vec3 blended = vec3(
    float(clamp(blendCh(uMode, A.r, B.r), 0, 255)),
    float(clamp(blendCh(uMode, A.g, B.g), 0, 255)),
    float(clamp(blendCh(uMode, A.b, B.b), 0, 255)));
  outColor = vec4((a + alpha * (blended - a)) / 255.0, 1.0);
}`;
}

/** Texture units the pass reads on; above every unit the compositor's own programs use (0, 4, 5, 6). */
const BACKDROP_UNIT = 8;
const LAYER_UNIT = 9;

export interface BlendPass {
  /** True between {@link BlendPass.begin} and {@link BlendPass.end}: the compositor's "main target" is the current backdrop target. */
  readonly active: boolean;
  /** Starts a frame that has blending layers. False when the pass cannot run (the layers then draw as `normal`). */
  begin(width: number, height: number): boolean;
  /** Binds where drawing goes now: the layer's own target while a layer is open, else the current backdrop target. */
  bindCurrent(): void;
  /** Opens a layer: clears its private target and draws premultiplied into it. */
  layerBegin(): void;
  /** Closes the layer opened by {@link BlendPass.layerBegin} and blends it onto the backdrop with `word`. */
  layerEnd(word: string): void;
  /** The composited picture so far (an adjustment clip reads it); null when the pass is not running. */
  currentTexture(): WebGLTexture | null;
  /** An adjustment clip's result replaces the picture: draws now go to the spare target (cleared to opaque black)... */
  replaceBegin(): void;
  /** ...and this makes that target the picture. */
  replaceEnd(): void;
  /** Replaces the picture with `texture` (same orientation as the picture), e.g. its graded copy. */
  replaceWith(texture: WebGLTexture): void;
  /** Ends the frame: copies the composited backdrop to the real target that `bindFinal` binds. */
  end(bindFinal: () => void): void;
  /** Forgets every GL handle (context lost); the next `begin` rebuilds. */
  reset(): void;
  /** Frees everything. */
  dispose(): void;
}

interface Targets {
  w: number;
  h: number;
  textures: [WebGLTexture, WebGLTexture, WebGLTexture];
  fbos: [WebGLFramebuffer, WebGLFramebuffer, WebGLFramebuffer];
}

/**
 * @param restore called after the pass has used its own program: put the compositor's program, active
 *   texture unit and blend state back.
 */
export function createBlendPass(gl: WebGL2RenderingContext, restore: () => void): BlendPass {
  let program: WebGLProgram | null = null;
  let programFailed = false;
  let uBackdrop: WebGLUniformLocation | null = null;
  let uLayer: WebGLUniformLocation | null = null;
  let uMode: WebGLUniformLocation | null = null;
  let targets: Targets | null = null;
  /** Index (0/1) of the target holding the composited backdrop; the other one receives the next blend. */
  let current = 0;
  let layerOpen = false;
  let replacing = false;
  let active = false;

  const buildProgram = (): boolean => {
    if (program) return true;
    if (programFailed) return false;
    const vs = gl.createShader(gl.VERTEX_SHADER);
    const fs = gl.createShader(gl.FRAGMENT_SHADER);
    const prog = gl.createProgram();
    if (!vs || !fs || !prog) {
      programFailed = true;
      return false;
    }
    gl.shaderSource(vs, BLEND_VERTEX_SHADER);
    gl.compileShader(vs);
    gl.shaderSource(fs, blendFragmentSource());
    gl.compileShader(fs);
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
      console.error('[preview] blend program failed; blend modes draw as normal', gl.getShaderInfoLog(fs) ?? '', gl.getProgramInfoLog(prog));
      gl.deleteProgram(prog);
      programFailed = true;
      return false;
    }
    program = prog;
    uBackdrop = gl.getUniformLocation(prog, 'uBackdrop');
    uLayer = gl.getUniformLocation(prog, 'uLayer');
    uMode = gl.getUniformLocation(prog, 'uMode');
    return true;
  };

  const releaseTargets = () => {
    if (!targets) return;
    for (const t of targets.textures) gl.deleteTexture(t);
    for (const f of targets.fbos) gl.deleteFramebuffer(f);
    targets = null;
  };

  const targetsFor = (w: number, h: number): Targets | null => {
    if (targets && targets.w === w && targets.h === h) return targets;
    releaseTargets();
    const textures: WebGLTexture[] = [];
    const fbos: WebGLFramebuffer[] = [];
    for (let i = 0; i < 3; i++) {
      const texture = gl.createTexture();
      const fbo = gl.createFramebuffer();
      if (!texture || !fbo) return null;
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      textures.push(texture);
      fbos.push(fbo);
    }
    targets = { w, h, textures: textures as Targets['textures'], fbos: fbos as Targets['fbos'] };
    return targets;
  };

  /** Draws the full-screen triangle with the blend program: backdrop = target `from`, layer = scratch, into `into` (undefined = whatever is bound). */
  const run = (backdrop: WebGLTexture, into: WebGLFramebuffer | undefined, mode: number) => {
    const t = targets!;
    if (into !== undefined) gl.bindFramebuffer(gl.FRAMEBUFFER, into);
    gl.viewport(0, 0, t.w, t.h);
    gl.disable(gl.BLEND);
    gl.useProgram(program);
    gl.activeTexture(gl.TEXTURE0 + BACKDROP_UNIT);
    gl.bindTexture(gl.TEXTURE_2D, backdrop);
    gl.activeTexture(gl.TEXTURE0 + LAYER_UNIT);
    gl.bindTexture(gl.TEXTURE_2D, t.textures[2]);
    gl.uniform1i(uBackdrop, BACKDROP_UNIT);
    gl.uniform1i(uLayer, LAYER_UNIT);
    gl.uniform1i(uMode, mode);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.enable(gl.BLEND);
    restore();
  };

  return {
    get active() {
      return active;
    },
    begin(width, height) {
      if (width <= 0 || height <= 0 || !buildProgram() || !targetsFor(width, height)) return false;
      current = 0;
      layerOpen = false;
      active = true;
      return true;
    },
    bindCurrent() {
      if (!targets) return;
      gl.bindFramebuffer(gl.FRAMEBUFFER, layerOpen ? targets.fbos[2] : replacing ? targets.fbos[1 - current] : targets.fbos[current]);
    },
    currentTexture() {
      return active && targets ? targets.textures[current] : null;
    },
    replaceBegin() {
      if (!targets) return;
      replacing = true;
      gl.bindFramebuffer(gl.FRAMEBUFFER, targets.fbos[1 - current]);
      gl.viewport(0, 0, targets.w, targets.h);
      gl.clearColor(0, 0, 0, 1);
      gl.clear(gl.COLOR_BUFFER_BIT);
    },
    replaceEnd() {
      if (!targets || !replacing) return;
      replacing = false;
      current = 1 - current;
    },
    replaceWith(texture) {
      if (!targets) return;
      run(texture, targets.fbos[1 - current], -1);
      current = 1 - current;
    },
    layerBegin() {
      if (!targets) return;
      layerOpen = true;
      gl.bindFramebuffer(gl.FRAMEBUFFER, targets.fbos[2]);
      gl.viewport(0, 0, targets.w, targets.h);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      // Premultiplied result: colour * alpha, alpha accumulates as usual.
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    },
    layerEnd(word) {
      if (!targets) return;
      layerOpen = false;
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      const next = 1 - current;
      run(targets.textures[current], targets.fbos[next], blendModeId(word));
      current = next;
    },
    end(bindFinal) {
      if (!targets) {
        active = false;
        return;
      }
      active = false;
      bindFinal();
      run(targets.textures[current], undefined, -1);
    },
    reset() {
      program = null;
      programFailed = false;
      targets = null;
      layerOpen = false;
      replacing = false;
      active = false;
    },
    dispose() {
      releaseTargets();
      if (program) gl.deleteProgram(program);
      program = null;
      active = false;
    },
  };
}
