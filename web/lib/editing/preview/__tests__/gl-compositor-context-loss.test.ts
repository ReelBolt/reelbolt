import { describe, expect, it, vi } from 'vitest';
import { createGlCompositor } from '../gl-compositor';

function fakeGl() {
  const calls: string[] = [];
  const target: Record<string, unknown> = {
    VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, ARRAY_BUFFER: 5, STATIC_DRAW: 6, FLOAT: 7,
    BLEND: 8, SRC_ALPHA: 9, ONE_MINUS_SRC_ALPHA: 10, TRIANGLES: 11, TEXTURE0: 12, TEXTURE_2D: 13, COLOR_BUFFER_BIT: 14,
  };
  const gl = new Proxy(target, {
    get(t, name: string) {
      if (name in t) return t[name];
      return (...args: unknown[]) => {
        calls.push(name);
        void args;
        if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
        if (name.startsWith('create')) return {};
        return null;
      };
    },
  }) as unknown as WebGL2RenderingContext;
  return { gl, calls };
}

function fakeCanvas(gl: WebGL2RenderingContext) {
  const handlers = new Map<string, Set<(e: Event) => void>>();
  const canvas = {
    width: 320,
    height: 180,
    getContext: () => gl,
    addEventListener: (type: string, fn: (e: Event) => void) => {
      if (!handlers.has(type)) handlers.set(type, new Set());
      handlers.get(type)!.add(fn);
    },
    removeEventListener: (type: string, fn: (e: Event) => void) => handlers.get(type)?.delete(fn),
  } as unknown as HTMLCanvasElement;
  const fire = (type: string, event: Event) => handlers.get(type)?.forEach((fn) => fn(event));
  return { canvas, fire, handlers };
}

describe('gl-compositor context loss', () => {
  it('cancels the loss, suppresses draws, rebuilds on restore', () => {
    const { gl, calls } = fakeGl();
    const { canvas, fire } = fakeCanvas(gl);
    const onContextLost = vi.fn();
    const onContextRestored = vi.fn();
    const compositor = createGlCompositor(canvas, { onContextLost, onContextRestored })!;
    const programsBefore = calls.filter((c) => c === 'createProgram').length;

    const preventDefault = vi.fn();
    fire('webglcontextlost', { preventDefault } as unknown as Event);
    expect(preventDefault).toHaveBeenCalled();
    expect(onContextLost).toHaveBeenCalled();

    calls.length = 0;
    expect(compositor.draw([{ layerId: 'a', image: null, opacity: 1 }])).toEqual({ repeats: 0, blank: [] });
    compositor.clear();
    compositor.drawOverlay({} as CanvasImageSource);
    expect(calls).toEqual([]);

    fire('webglcontextrestored', {} as Event);
    expect(calls.filter((c) => c === 'createProgram').length).toBe(4);
    expect(programsBefore).toBe(4);
    expect(onContextRestored).toHaveBeenCalledWith(true);
    calls.length = 0;
    compositor.draw([]);
    expect(calls).toContain('clear');
  });

  it('removes its listeners on dispose', () => {
    const { gl } = fakeGl();
    const { canvas, handlers } = fakeCanvas(gl);
    createGlCompositor(canvas)!.dispose();
    expect(handlers.get('webglcontextlost')?.size).toBe(0);
    expect(handlers.get('webglcontextrestored')?.size).toBe(0);
  });
});
