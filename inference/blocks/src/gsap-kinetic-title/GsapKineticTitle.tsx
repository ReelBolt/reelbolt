// ReelBolt block "gsap-kinetic-title" (MIT). A title whose characters rise in with a GSAP timeline that is
// driven only by the frame: the timeline is built paused and sought to frame / fps in a layout effect.
import React, {useLayoutEffect, useRef} from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';
import {gsap} from 'gsap';
import {SplitText} from 'gsap/SplitText';

gsap.registerPlugin(SplitText);

export type GsapKineticTitleProps = {
  /** The title text. */
  text: string;
  /** Text colour. Default '#ffffff'. */
  color?: string;
  /** Font size in px. Default 120. */
  fontSize?: number;
  /** CSS font-family. Default 'Inter, sans-serif' (a bundled font). */
  fontFamily?: string;
  /** Seconds between consecutive characters. Default 0.03. */
  staggerSeconds?: number;
  /** Seconds each character takes to land. Default 0.6. */
  durationSeconds?: number;
};

export const GsapKineticTitle: React.FC<GsapKineticTitleProps> = ({
  text,
  color = '#ffffff',
  fontSize = 120,
  fontFamily = 'Inter, sans-serif',
  staggerSeconds = 0.03,
  durationSeconds = 0.6,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const containerRef = useRef<HTMLDivElement>(null);
  const titleRef = useRef<HTMLDivElement>(null);
  const timelineRef = useRef<gsap.core.Timeline | null>(null);

  // Build the paused timeline once per input change; revert everything on cleanup.
  useLayoutEffect(() => {
    const ctx = gsap.context(() => {
      const split = SplitText.create(titleRef.current as HTMLElement, {type: 'chars,words'});
      const tl = gsap.timeline({paused: true});
      tl.from(split.chars, {
        yPercent: 110,
        opacity: 0,
        rotate: 8,
        duration: durationSeconds,
        ease: 'power3.out',
        stagger: staggerSeconds,
      });
      timelineRef.current = tl;
    }, containerRef);
    return () => {
      timelineRef.current = null;
      ctx.revert();
    };
  }, [text, fontSize, fontFamily, staggerSeconds, durationSeconds]);

  // Every frame is a pure function of the frame number.
  useLayoutEffect(() => {
    timelineRef.current?.seek(frame / fps);
  });

  return (
    <AbsoluteFill ref={containerRef} style={{justifyContent: 'center', alignItems: 'center', padding: '0 10%'}}>
      <div
        ref={titleRef}
        style={{fontFamily, fontSize, fontWeight: 800, lineHeight: 1.15, color, textAlign: 'center'}}
      >
        {text}
      </div>
    </AbsoluteFill>
  );
};
