import { describe, expect, it, vi } from 'vitest';
import { startVoTake, VO_LIMIT_MESSAGE, VO_MAX_BYTES, VO_MAX_SECONDS } from '../vo-recorder';

class FakeRecorder {
  static last: FakeRecorder;
  state = 'inactive';
  ondataavailable: ((e: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  constructor() {
    FakeRecorder.last = this;
  }
  start() {
    this.state = 'recording';
  }
  stop() {
    this.state = 'inactive';
    this.onstop?.();
  }
}

async function open(now: () => number, onLimit: (m: string) => void) {
  vi.stubGlobal('MediaRecorder', FakeRecorder);
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: async () => ({ getTracks: () => [] }) } });
  return startVoTake('audio/webm', { now, onLimit });
}
const chunk = (size: number) => ({ data: { size } as Blob });

describe('voiceover recording limit', () => {
  it('says so once when the length limit is reached', async () => {
    let t = 0;
    const onLimit = vi.fn();
    await open(() => t, onLimit);
    FakeRecorder.last.ondataavailable?.(chunk(10));
    expect(onLimit).not.toHaveBeenCalled();
    t = VO_MAX_SECONDS * 1000;
    FakeRecorder.last.ondataavailable?.(chunk(10));
    FakeRecorder.last.ondataavailable?.(chunk(10));
    expect(onLimit).toHaveBeenCalledTimes(1);
    expect(onLimit).toHaveBeenCalledWith(VO_LIMIT_MESSAGE);
  });
  it('says so when the size limit is reached', async () => {
    const onLimit = vi.fn();
    await open(() => 0, onLimit);
    FakeRecorder.last.ondataavailable?.(chunk(VO_MAX_BYTES));
    expect(onLimit).toHaveBeenCalledWith(VO_LIMIT_MESSAGE);
  });
});
