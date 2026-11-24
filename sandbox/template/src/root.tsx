// Built with Remotion (https://www.remotion.dev). Using this code is subject to the Remotion license: https://www.remotion.dev/docs/license

import type React from 'react';
import {
  AbsoluteFill,
  Audio,
  Composition,
  Easing,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
  Sequence,
} from 'remotion';

// ── Scene 1: Cold open — grid flash + wordmark ──────────────────────────────
const SceneWordmark: React.FC<{ voiceoverSrc?: string }> = ({ voiceoverSrc }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  const gridOpacity = interpolate(frame, [0, 8, 60, 80], [0, 0.12, 0.12, 0], {
    extrapolateRight: 'clamp',
  });
  const fadeIn = spring({ frame, fps, config: { damping: 20, stiffness: 180 } });
  const scale = interpolate(fadeIn, [0, 1], [1.06, 1]);
  const glow = interpolate(frame, [0, 40, 80], [0, 1, 0.6], { extrapolateRight: 'clamp' });

  return (
    <AbsoluteFill
      style={{
        background: '#09090d',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        overflow: 'hidden',
      }}
    >
      {voiceoverSrc && <Audio src={staticFile(voiceoverSrc)} />}
      {/* grid */}
      <AbsoluteFill
        style={{
          opacity: gridOpacity,
          backgroundImage:
            'linear-gradient(rgba(99,102,241,0.5) 1px, transparent 1px), linear-gradient(90deg, rgba(99,102,241,0.5) 1px, transparent 1px)',
          backgroundSize: '80px 80px',
        }}
      />

      {/* glow blob */}
      <div
        style={{
          position: 'absolute',
          width: 700,
          height: 700,
          borderRadius: '50%',
          background: 'radial-gradient(circle, rgba(99,102,241,0.18) 0%, transparent 70%)',
          opacity: glow,
        }}
      />

      {/* wordmark */}
      <div
        style={{
          transform: `scale(${scale})`,
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          gap: 16,
        }}
      >
        <div
          style={{
            fontSize: 130,
            fontWeight: 900,
            fontFamily: "'Helvetica Neue', Arial, sans-serif",
            letterSpacing: -4,
            color: '#ffffff',
            opacity: fadeIn,
            textShadow: `0 0 60px rgba(99,102,241,${glow * 0.8})`,
          }}
        >
          ReelBolt
        </div>
        <div
          style={{
            fontSize: 34,
            fontWeight: 500,
            fontFamily: "'Helvetica Neue', Arial, sans-serif",
            letterSpacing: 10,
            color: '#a5b4fc',
            textTransform: 'uppercase',
            opacity: interpolate(frame, [12, 30], [0, 1], { extrapolateRight: 'clamp' }),
          }}
        >
          reelbolt.ai
        </div>
      </div>
    </AbsoluteFill>
  );
};

// ── Scene 2: Feature pills ──────────────────────────────────────────────────
const PILLS = [
  { icon: '⚡', label: 'AI-Powered Cuts' },
  { icon: '🎬', label: 'Multi-Track Studio' },
  { icon: '📱', label: 'Screen Compositing' },
  { icon: '🎵', label: 'Audio Intelligence' },
];

const ScenePills: React.FC<{ voiceoverSrc?: string }> = ({ voiceoverSrc }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  return (
    <AbsoluteFill
      style={{
        background: 'linear-gradient(135deg, #09090d 0%, #0f0f1a 100%)',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 36,
      }}
    >
      {voiceoverSrc && <Audio src={staticFile(voiceoverSrc)} />}
      {/* heading */}
      <div
        style={{
          fontSize: 64,
          fontWeight: 800,
          fontFamily: "'Helvetica Neue', Arial, sans-serif",
          color: '#ffffff',
          letterSpacing: -2,
          opacity: interpolate(frame, [0, 18], [0, 1], { extrapolateRight: 'clamp' }),
          textAlign: 'center',
        }}
      >
        Built for creators who ship.
      </div>

      {/* pills */}
      <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap', justifyContent: 'center' }}>
        {PILLS.map((pill, i) => {
          const delay = 10 + i * 8;
          const s = spring({ frame: Math.max(0, frame - delay), fps, config: { damping: 22, stiffness: 200 } });
          const ty = interpolate(s, [0, 1], [40, 0]);
          return (
            <div
              key={pill.label}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 14,
                background: 'rgba(99,102,241,0.12)',
                border: '1.5px solid rgba(99,102,241,0.35)',
                borderRadius: 100,
                padding: '18px 36px',
                opacity: s,
                transform: `translateY(${ty}px)`,
              }}
            >
              <span style={{ fontSize: 36 }}>{pill.icon}</span>
              <span
                style={{
                  fontSize: 30,
                  fontWeight: 700,
                  fontFamily: "'Helvetica Neue', Arial, sans-serif",
                  color: '#e0e7ff',
                  letterSpacing: -0.5,
                }}
              >
                {pill.label}
              </span>
            </div>
          );
        })}
      </div>
    </AbsoluteFill>
  );
};

// ── Scene 3: CTA — "Alpha Live Now" ────────────────────────────────────────
const SceneCta: React.FC<{ voiceoverSrc?: string }> = ({ voiceoverSrc }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  const mainSpring = spring({ frame, fps, config: { damping: 18, stiffness: 160 } });
  const badgeSpring = spring({ frame: Math.max(0, frame - 18), fps, config: { damping: 22, stiffness: 200 } });

  const pulse = interpolate(
    Math.sin((frame / 30) * Math.PI * 2),
    [-1, 1],
    [0.88, 1],
  );

  return (
    <AbsoluteFill
      style={{
        background: 'radial-gradient(ellipse at 50% 45%, #1e1b4b 0%, #09090d 70%)',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 32,
      }}
    >
      {voiceoverSrc && <Audio src={staticFile(voiceoverSrc)} />}
      {/* badge */}
      <div
        style={{
          background: 'rgba(99,102,241,0.18)',
          border: '1.5px solid rgba(129,140,248,0.5)',
          borderRadius: 100,
          padding: '10px 28px',
          fontSize: 26,
          fontWeight: 700,
          fontFamily: "'Helvetica Neue', Arial, sans-serif",
          color: '#a5b4fc',
          letterSpacing: 3,
          textTransform: 'uppercase',
          opacity: badgeSpring,
          transform: `scale(${interpolate(badgeSpring, [0, 1], [0.8, 1])})`,
        }}
      >
        🚀 Alpha Live Now
      </div>

      {/* main CTA */}
      <div
        style={{
          fontSize: 110,
          fontWeight: 900,
          fontFamily: "'Helvetica Neue', Arial, sans-serif",
          color: '#ffffff',
          letterSpacing: -4,
          lineHeight: 1,
          textAlign: 'center',
          opacity: mainSpring,
          transform: `scale(${interpolate(mainSpring, [0, 1], [0.9, 1])})`,
          textShadow: '0 0 80px rgba(99,102,241,0.4)',
        }}
      >
        Make your video.
        <br />
        <span style={{ color: '#818cf8' }}>Let AI ship it.</span>
      </div>

      {/* url */}
      <div
        style={{
          fontSize: 42,
          fontWeight: 600,
          fontFamily: "'Helvetica Neue', Arial, sans-serif",
          color: '#6366f1',
          letterSpacing: 1,
          opacity: interpolate(frame, [25, 45], [0, 1], { extrapolateRight: 'clamp' }),
          transform: `scale(${pulse})`,
        }}
      >
        reelbolt.ai
      </div>
    </AbsoluteFill>
  );
};

// ── Root ────────────────────────────────────────────────────────────────────
// 3 scenes @ 30fps:
//   0-90    Scene 1 (3s) — wordmark
//   60-150  Scene 2 (3s) — pills (10-frame crossfade overlap handled by opacity)
//   120-240 Scene 3 (4s) — CTA
// Total: 240 frames = 8s
//
// Voiceover convention: Per-scene audio is staged at `public/voiceover/scene-{n}.wav`
// and passed in as the staticFile() path `voiceover/scene-{n}.wav` (0-indexed, e.g.
// `voiceover/scene-0.wav`, `voiceover/scene-1.wav`, etc.).
// See docs/voiceover.md for the full staging specification that Backend
// scene-staging code targets when generating compositions with per-scene narration.
const LaunchPromo: React.FC<{
  voiceoverSrc0?: string;
  voiceoverSrc1?: string;
  voiceoverSrc2?: string;
}> = ({ voiceoverSrc0, voiceoverSrc1, voiceoverSrc2 }) => {
  return (
    <AbsoluteFill style={{ background: '#09090d' }}>
      <Sequence from={0} durationInFrames={100}>
        <SceneWordmark voiceoverSrc={voiceoverSrc0} />
      </Sequence>
      <Sequence from={90} durationInFrames={100}>
        <ScenePills voiceoverSrc={voiceoverSrc1} />
      </Sequence>
      <Sequence from={180} durationInFrames={120}>
        <SceneCta voiceoverSrc={voiceoverSrc2} />
      </Sequence>
    </AbsoluteFill>
  );
};

export const Root: React.FC = () => (
  <Composition
    id="LaunchPromo"
    component={LaunchPromo}
    durationInFrames={300}
    fps={30}
    width={1920}
    height={1080}
    defaultProps={{
      voiceoverSrc0: 'voiceover/scene-0.wav',
    }}
  />
);
