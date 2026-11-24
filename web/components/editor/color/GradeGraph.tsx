'use client';

import React, { useEffect, useRef } from 'react';
import { IconChartLine, IconCircleDashed, IconCircleHalf2, IconColorPicker, IconTable } from '@tabler/icons-react';
import type { ChainNode, ChainSegment } from '@/lib/editing/color/grade-nodes';
import { chainNodes } from '@/lib/editing/color/grade-nodes';
import { useT } from '@/i18n';
import { useDefaultNameText } from '../shell/default-names';
import classes from './colour-depth.module.css';

export interface GradeGraphProps {
  /** The chain in application order (`gradeChain`): a colour group's stack before the clip's own. */
  segments: readonly ChainSegment[];
  /** The selected node's chain key, or null. */
  selectedKey: string | null;
  /** The soloed node's chain key, or null. */
  soloKey: string | null;
  onSelect: (node: ChainNode) => void;
  /** Enter on a node: move focus to the grade tools. */
  onFocusTools?: () => void;
}

const GLYPH = 11;

/**
 * The serial grade graph (pro UI wave 4): Input, every correction in the order it is applied, Output. It
 * reads and selects, nothing else: a click or the arrow keys select a node (the same selection as the node
 * list), Enter moves to the grade tools. A correction that is off is drawn hollow, the soloed one is marked,
 * and small glyphs say what each carries (primaries, curves, LUT, qualifier, window).
 */
export function GradeGraph({ segments, selectedKey, soloKey, onSelect, onFocusTools }: GradeGraphProps) {
  const t = useT('editor-color.graph');
  const nameText = useDefaultNameText();
  const all = chainNodes(segments);
  const buttons = useRef(new Map<string, HTMLButtonElement>());
  const focusKey = useRef<string | null>(null);
  useEffect(() => {
    if (!focusKey.current) return;
    buttons.current.get(focusKey.current)?.focus();
    focusKey.current = null;
  });
  // One tab stop: the selected node, or the first.
  const tabKey = all.some((n) => n.key === selectedKey) ? selectedKey : all[0]?.key ?? null;

  const labelOf = (n: ChainNode): string => (n.node.name ? nameText(n.node.name) : t('correctionN', { n: n.index + 1 }));
  const describe = (n: ChainNode, position: number): string => {
    const parts = [
      n.carries.primaries ? t('carries.primaries') : null,
      n.carries.curves ? t('carries.curves') : null,
      n.carries.lut ? t('carries.lut') : null,
      n.carries.qualifier ? t('carries.qualifier') : null,
      n.carries.window ? t('carries.window') : null,
    ].filter((p): p is string => p !== null);
    const state = n.key === soloKey ? t('stateSolo') : n.enabled ? t('stateOn') : t('stateOff');
    return t('nodeLabel', { position, name: labelOf(n), state, carries: parts.length > 0 ? parts.join(', ') : t('carries.nothing') });
  };

  const go = (from: number, to: number) => {
    const next = all[Math.min(all.length - 1, Math.max(0, to))];
    if (!next || next.key === all[from]?.key) return;
    focusKey.current = next.key;
    onSelect(next);
  };
  const onKey = (e: React.KeyboardEvent, index: number) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
      e.preventDefault();
      go(index, index + 1);
    } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      e.preventDefault();
      go(index, index - 1);
    } else if (e.key === 'Home') {
      e.preventDefault();
      go(index, 0);
    } else if (e.key === 'End') {
      e.preventDefault();
      go(index, all.length - 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      onSelect(all[index]);
      onFocusTools?.();
    }
  };

  const segmentLabel = (segment: ChainSegment): string => {
    const name = nameText(segment.owner.name);
    if (segment.owner.kind === 'group') return segment.stackEnabled ? t('groupSegment', { name }) : t('groupSegmentOff', { name });
    return segment.stackEnabled ? t('clipSegment', { name }) : t('clipSegmentOff', { name });
  };

  return (
    <div className={classes.graph} role="listbox" aria-orientation="horizontal" aria-label={t('label')} data-testid="grade-graph">
      <span className={classes.graphCap} aria-hidden="true" data-testid="grade-graph-input">
        {t('input')}
      </span>
      {segments.map((segment) => (
        <React.Fragment key={`${segment.owner.kind}:${segment.owner.id}`}>
          <span className={classes.graphLink} aria-hidden="true" />
          <div className={classes.graphSegment} role="group" aria-label={segmentLabel(segment)} data-owner={segment.owner.kind} data-testid={`grade-graph-segment-${segment.owner.kind}`}>
            <span className={classes.graphSegmentLabel} title={segmentLabel(segment)} aria-hidden="true">
              {segmentLabel(segment)}
            </span>
            <div className={classes.graphSegmentNodes}>
              {segment.nodes.length === 0 && (
                <span className={classes.graphSegmentLabel} aria-hidden="true" data-testid={`grade-graph-empty-${segment.owner.kind}`}>
                  {t('noCorrections')}
                </span>
              )}
              {segment.nodes.map((n, i) => {
                const index = all.findIndex((x) => x.key === n.key);
                const here = index + 1;
                return (
                  <React.Fragment key={n.key}>
                    {i > 0 && <span className={classes.graphLink} style={{ marginBottom: 0 }} aria-hidden="true" />}
                    <button
                      type="button"
                      ref={(el) => {
                        if (el) buttons.current.set(n.key, el);
                        else buttons.current.delete(n.key);
                      }}
                      className={classes.graphNode}
                      role="option"
                      aria-selected={n.key === selectedKey}
                      aria-label={describe(n, here)}
                      title={labelOf(n)}
                      tabIndex={n.key === tabKey ? 0 : -1}
                      data-enabled={n.enabled}
                      data-solo={n.key === soloKey}
                      data-testid={`grade-graph-node-${here}`}
                      onClick={() => onSelect(n)}
                      onKeyDown={(e) => onKey(e, index)}
                    >
                      {n.key === soloKey && (
                        <span className={classes.graphSoloMark} aria-hidden="true">
                          {t('soloShort')}
                        </span>
                      )}
                      <span aria-hidden="true">{here}</span>
                      <span className={classes.graphGlyphs} aria-hidden="true">
                        {n.carries.primaries && <IconCircleHalf2 size={GLYPH} data-glyph="primaries" />}
                        {n.carries.curves && <IconChartLine size={GLYPH} data-glyph="curves" />}
                        {n.carries.lut && <IconTable size={GLYPH} data-glyph="lut" />}
                        {n.carries.qualifier && <IconColorPicker size={GLYPH} data-glyph="qualifier" />}
                        {n.carries.window && <IconCircleDashed size={GLYPH} data-glyph="window" />}
                      </span>
                    </button>
                  </React.Fragment>
                );
              })}
            </div>
          </div>
        </React.Fragment>
      ))}
      <span className={classes.graphLink} aria-hidden="true" />
      <span className={classes.graphCap} aria-hidden="true" data-testid="grade-graph-output">
        {t('output')}
      </span>
    </div>
  );
}
