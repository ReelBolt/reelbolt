import { describe, it, expect } from 'vitest';
import { agentRoleLabel, agentProgressPhrase, stepRoleLabel, stepProgressPhrase, hasAgentRole } from './agent-roles';
import { AGENT_TYPE_GROUP } from './constants';

describe('agentRoleLabel / agentProgressPhrase', () => {
  it('gives the plain-language job of a built-in agent', () => {
    expect(agentRoleLabel('NarrationWriter')).toBe('Writes the narration');
    expect(agentRoleLabel('VideoStoryEditor')).toBe('Picks the best clips');
    expect(agentProgressPhrase('NarrationWriter')).toBe('Writing the narration…');
  });

  it('falls back to a generic phrase for custom and unknown agents, never the enum name', () => {
    expect(agentRoleLabel('Custom')).toBe('Runs your custom AI agent');
    expect(agentRoleLabel('NoSuchAgent')).toBe('Runs your custom AI agent');
    expect(agentRoleLabel(undefined)).toBe('Runs your custom AI agent');
    expect(agentProgressPhrase(null)).toBe('Running your custom agent…');
  });

  it('covers every agent type the dashboard knows about', () => {
    for (const agentType of Object.keys(AGENT_TYPE_GROUP)) {
      if (agentType === 'Custom') continue;
      expect(hasAgentRole(agentType), agentType).toBe(true);
    }
  });

  it('never leaks an internal name into a role or progress phrase', () => {
    for (const agentType of Object.keys(AGENT_TYPE_GROUP)) {
      const text = `${agentRoleLabel(agentType)} ${agentProgressPhrase(agentType)}`;
      expect(text).not.toMatch(/[a-z][A-Z]/); // no PascalCase / camelCase identifiers
      expect(text).not.toMatch(/ffmpeg|schema|Remotion|LLM/i);
    }
  });
});

describe('stepRoleLabel / stepProgressPhrase', () => {
  it('uses the agent role for Agent steps', () => {
    expect(stepRoleLabel('Agent', 'MusicSupervisor')).toBe('Chooses the music');
    expect(stepProgressPhrase('Agent', 'MusicSupervisor')).toBe('Choosing the music…');
  });

  it('uses the step job for deterministic steps whatever placeholder agent they carry', () => {
    expect(stepRoleLabel('VideoAnalyze', 'VideoTransform')).toBe('Watches your footage');
    expect(stepRoleLabel('VideoCompile', 'VideoTransform')).toBe('Puts the final video together');
    expect(stepProgressPhrase('Voiceover', 'VideoTransform')).toBe('Recording the voiceover…');
  });

  it('names a review loop by its reviewer when it has one', () => {
    expect(stepRoleLabel('ReviewLoop', 'VideoReviewAgent')).toBe('Watches and checks the edit');
    expect(stepRoleLabel('ReviewLoop', null)).toBe('Checks the result and retries if needed');
  });
});
