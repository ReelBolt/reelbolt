/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { DropFrameLedger } from '../drop-frame-ledger';

describe('DropFrameLedger fresh vs repeated frames', () => {
  it('counts presented-with-repeat separately, so fresh = presented - repeated', () => {
    const ledger = new DropFrameLedger();
    for (let i = 0; i < 10; i++) ledger.recordPresented(1, 1000 + i * 33, i % 5 === 0);
    ledger.recordDropped(2);
    const s = ledger.snapshot();
    expect(s.presentedFrames).toBe(10);
    expect(s.repeatedFrames).toBe(2);
    expect(s.droppedFrames).toBe(2);
    expect(s.deadlines).toBe(12);
    ledger.reset();
    expect(ledger.snapshot().repeatedFrames).toBe(0);
  });
});
