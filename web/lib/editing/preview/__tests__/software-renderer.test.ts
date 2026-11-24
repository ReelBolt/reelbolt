/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { isSoftwareRenderer } from '../gl-compositor';

describe('isSoftwareRenderer', () => {
  it('names the CPU rasterisers', () => {
    expect(isSoftwareRenderer('ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver)')).toBe(true);
    expect(isSoftwareRenderer('llvmpipe (LLVM 15.0.7, 256 bits)')).toBe(true);
    expect(isSoftwareRenderer('Mesa softpipe')).toBe(true);
    expect(isSoftwareRenderer('Microsoft Basic Render Driver / Software Renderer')).toBe(true);
  });
  it('does not call hardware software', () => {
    expect(isSoftwareRenderer('ANGLE (AMD, AMD Radeon AI PRO R9700 (radeonsi gfx1201 ACO), OpenGL ES 3.2)')).toBe(false);
    expect(isSoftwareRenderer('ANGLE (Apple, ANGLE Metal Renderer: Apple M2, Unspecified Version)')).toBe(false);
    expect(isSoftwareRenderer(null)).toBe(false);
  });
});
