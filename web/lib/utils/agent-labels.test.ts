import { describe, expect, it } from 'vitest';
import {
  AGENT_DESCRIPTIONS, agentDisplayDescription, agentDisplayName, agentProgressPhrase, agentRoleDescription, agentRoleLabel,
  canonicalAgentType, humanizeIdentifier, stepProgressPhrase, stepRoleLabel,
} from './agent-labels';
import { agentRoleLabel as sharedRoleLabel, stepRoleLabel as sharedStepRoleLabel } from './agent-roles';

describe('agent labels', () => {
  it('resolves engine identifiers in any spelling to the shared builder role', () => {
    expect(canonicalAgentType('NARRATIONWRITER')).toBe('NarrationWriter');
    expect(canonicalAgentType('video_transform')).toBe('VideoTransform');
    expect(canonicalAgentType('NoSuchAgent')).toBeNull();
    expect(agentRoleLabel('NARRATIONWRITER')).toBe(sharedRoleLabel('NarrationWriter'));
    expect(agentRoleLabel('VIDEOTRANSFORM')).toBe(sharedRoleLabel('VideoTransform'));
    expect(stepRoleLabel('VideoCompile', 'VIDEOTRANSFORM')).toBe(sharedStepRoleLabel('VideoCompile', 'VideoTransform'));
  });

  it('keeps custom names and humanizes unknown identifiers instead of shouting them', () => {
    expect(agentRoleLabel('Custom', 'My hype writer')).toBe('My hype writer');
    expect(agentRoleLabel('LayoutArtist')).toBe('Layout artist');
    expect(humanizeIdentifier('VIDEO_TRANSFORM')).toBe('Video transform');
  });

  it('gives plain-language progress phrases', () => {
    expect(agentProgressPhrase('NARRATIONWRITER')).toBe('Writing the narration…');
    expect(stepProgressPhrase('VideoAnalyze', 'VideoTransform')).toBe('Watching your footage…');
    expect(stepProgressPhrase('VideoCompile', 'VideoTransform')).toBe('Putting the video together…');
  });

  it('describes every built-in agent without jargon', () => {
    for (const [type, line] of Object.entries(AGENT_DESCRIPTIONS)) {
      expect(agentRoleDescription(type)).toBe(line);
      expect(line).not.toMatch(/schema|enum|placement id|opaque|manifest/i);
    }
    expect(agentDisplayName({ name: 'CodeStructureAnalyzer', agentType: 'CodeStructureAnalyzer', isBuiltIn: true })).toBe(sharedRoleLabel('CodeStructureAnalyzer'));
    expect(agentDisplayName({ name: 'Mine', agentType: 'Custom', isBuiltIn: false })).toBe('Mine');
    expect(agentDisplayDescription({ description: 'schema-validated output', agentType: 'CodeStructureAnalyzer', isBuiltIn: true })).toBe(AGENT_DESCRIPTIONS.CodeStructureAnalyzer);
    expect(agentDisplayDescription({ description: 'Mine does X', agentType: 'Custom', isBuiltIn: false })).toBe('Mine does X');
  });
});
