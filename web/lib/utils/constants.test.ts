import { describe, it, expect } from 'vitest';
import { AGENT_GROUP_COLORS, AGENT_TYPE_GROUP, getAgentGroup } from './constants';

/**
 * The video-editing agents that must share the single 'Video' group, so the agent picker, the
 * agents page and `AgentTypeBadge` render them together instead of splitting them across the pink
 * 'Custom' fallback. Adding a new video-editing agent (e.g. `ShotDirector`) means adding it here.
 */
const VIDEO_GROUPED_AGENT_TYPES = [
  'VideoStoryEditor',
  'VideoTransform',
  'MotionGraphicsPlanner',
  'MusicSupervisor',
  'SoundDesigner',
  'Colorist',
  'VideoEditDirector',
  'MotionGraphicsDirector',
  'ColorGradeDirector',
  'TakeSelector',
  'DailiesDirector',
] as const;

describe('getAgentGroup', () => {
  it('groups ShotDirector under Video', () => {
    expect(getAgentGroup('ShotDirector')).toBe('Video');
  });

  it.each(VIDEO_GROUPED_AGENT_TYPES)('groups %s under Video', (agentType) => {
    expect(getAgentGroup(agentType)).toBe('Video');
  });

  it('keeps VideoReviewAgent under Quality (deliberate — it judges the compiled edit)', () => {
    expect(getAgentGroup('VideoReviewAgent')).toBe('Quality');
  });

  it('falls back to Custom for an unknown agent type', () => {
    expect(getAgentGroup('NoSuchAgent')).toBe('Custom');
    expect(getAgentGroup('')).toBe('Custom');
  });

  it('falls back to Custom for the explicit Custom entry', () => {
    expect(getAgentGroup('Custom')).toBe('Custom');
  });

  it('gives every group a colour, so no agent renders uncoloured', () => {
    for (const group of Object.values(AGENT_TYPE_GROUP)) {
      expect(AGENT_GROUP_COLORS[group]).toBeDefined();
    }
    // 'Custom' is the fallback group `getAgentGroup` returns for an unmapped type, so its colour
    // matters even though no entry in AGENT_TYPE_GROUP necessarily produces it.
    expect(AGENT_GROUP_COLORS.Custom).toBeDefined();
  });
});

describe('AGENT_TYPE_GROUP', () => {
  it('maps ShotDirector exactly once, to Video', () => {
    expect(Object.keys(AGENT_TYPE_GROUP).filter((k) => k === 'ShotDirector')).toHaveLength(1);
    expect(AGENT_TYPE_GROUP.ShotDirector).toBe('Video');
  });

  it('keeps the Video colour distinct from the Custom fallback colour', () => {
    expect(AGENT_GROUP_COLORS.Video).not.toBe(AGENT_GROUP_COLORS.Custom);
  });
});
