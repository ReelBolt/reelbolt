/**
 * T-008b: the GL half of the xfade transitions. The compositor draws the outgoing and the incoming clip each into its
 * own canvas-sized target (black, opaque, as the render's padded legs are), then {@link XfadePass.draw} mixes the two with
 * the word's kernel (see `xfade.ts`) onto whatever target is bound.
 */

import { XFADE_FRAGMENT_SHADER } from './xfade';

const VERTEX = `#version 300 es
in vec2 aPos;
out vec2 vUv;
void main() {
  vUv = aPos * 0.5 + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}`;

/** Texture units the pass samples from; high enough to stay clear of the compositor's own (picture, LUTs, curves). */
const UNIT_A = 14;
const UNIT_B = 15;

export interface XfadeTargets {
  w: number;
  h: number;
  textures: [WebGLTexture, WebGLTexture];
  fbos: [WebGLFramebuffer, WebGLFramebuffer];
}

export interface XfadePass {
  /** The two canvas-sized targets (outgoing, incoming), reallocated when the size changes. */
  targets(w: number, h: number): XfadeTargets;
  /** Mixes the targets with word `index` at kernel progress `progress` (= 1 - t) onto the bound framebuffer. */
  draw(index: number, progress: number, w: number, h: number): void;
  dispose(): void;
}

function compile(gl: WebGL2RenderingContext, type: number, source: string): WebGLShader | null {
  const shader = gl.createShader(type);
  if (!shader) return null;
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    console.error('[preview] xfade shader failed', gl.getShaderInfoLog(shader));
    gl.deleteShader(shader);
    return null;
  }
  return shader;
}

/** Builds the pass; null when its program cannot be built (the transition then falls back to a dissolve). */
export function createXfadePass(gl: WebGL2RenderingContext): XfadePass | null {
  const vs = compile(gl, gl.VERTEX_SHADER, VERTEX);
  const fs = compile(gl, gl.FRAGMENT_SHADER, XFADE_FRAGMENT_SHADER);
  const program = gl.createProgram();
  if (!vs || !fs || !program) return null;
  gl.bindAttribLocation(program, 0, 'aPos');
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    console.error('[preview] xfade program link failed', gl.getProgramInfoLog(program));
    gl.deleteProgram(program);
    return null;
  }
  const uA = gl.getUniformLocation(program, 'uA');
  const uB = gl.getUniformLocation(program, 'uB');
  const uProgress = gl.getUniformLocation(program, 'uProgress');
  const uType = gl.getUniformLocation(program, 'uType');
  const uSize = gl.getUniformLocation(program, 'uSize');

  const vao = gl.createVertexArray();
  const vbo = gl.createBuffer();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);
  gl.useProgram(program);
  gl.uniform1i(uA, UNIT_A);
  gl.uniform1i(uB, UNIT_B);

  let current: XfadeTargets | null = null;
  const release = () => {
    if (!current) return;
    for (const t of current.textures) gl.deleteTexture(t);
    for (const f of current.fbos) gl.deleteFramebuffer(f);
    current = null;
  };
  const make = (w: number, h: number) => {
    const texture = gl.createTexture() as WebGLTexture;
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const fbo = gl.createFramebuffer() as WebGLFramebuffer;
    gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
    return { texture, fbo };
  };

  return {
    targets(w, h) {
      if (current && current.w === w && current.h === h) return current;
      release();
      const a = make(w, h);
      const b = make(w, h);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      current = { w, h, textures: [a.texture, b.texture], fbos: [a.fbo, b.fbo] };
      return current;
    },
    draw(index, progress, w, h) {
      if (!current) return;
      gl.disable(gl.BLEND);
      gl.useProgram(program);
      gl.bindVertexArray(vao);
      gl.activeTexture(gl.TEXTURE0 + UNIT_A);
      gl.bindTexture(gl.TEXTURE_2D, current.textures[0]);
      gl.activeTexture(gl.TEXTURE0 + UNIT_B);
      gl.bindTexture(gl.TEXTURE_2D, current.textures[1]);
      gl.uniform1f(uProgress, progress);
      gl.uniform1i(uType, index);
      gl.uniform2i(uSize, w, h);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
      gl.bindVertexArray(null);
    },
    dispose() {
      release();
      gl.deleteProgram(program);
      gl.deleteVertexArray(vao);
      gl.deleteBuffer(vbo);
    },
  };
}
