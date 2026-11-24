'use client';

import React from 'react';
import { Menu, Tabs, Tooltip } from '@mantine/core';
import { IconDots } from '@tabler/icons-react';
import classes from './workspace.module.css';

export interface PanelTab {
  value: string;
  label: string;
  icon?: React.ComponentType<{ size?: number; stroke?: number }>;
  /** Rendered only while the tab is active. */
  content: React.ReactNode;
  testId?: string;
  /** T-008: the content is the surface's own EditorPanel (it owns padding and scroll). */
  flush?: boolean;
}

export interface PanelTabsProps {
  value: string | null;
  onChange: (value: string | null) => void;
  tabs: readonly PanelTab[];
  /** Accessible name of the tab list. */
  label: string;
}

/** Measured: an icon-only tab is 26 px (6 px padding either side, a 14 px glyph); a labelled one adds 18 px and the label. */
const ICON_TAB_PX = 26;
const LABEL_EXTRA_PX = 18;
/** A tab with no icon cannot shrink: its padding plus its label. */
const TEXT_TAB_EXTRA_PX = 20;

export type TabFit = 'full' | 'active' | 'icons';

/**
 * How the tabs fit a row `available` px wide, in order of preference:
 *   full    every tab shows its label;
 *   active  icon-only tabs, the active one keeps its label;
 *   icons   icon-only tabs (every tab keeps its accessible name and a tooltip);
 * and when even those do not fit, `overflow` is true and the tabs are also listed in a "More" menu.
 * A tab with no icon always shows its label. Pure, so it is unit tested.
 */
export function fitTabs(labelWidths: readonly number[], hasIcon: readonly boolean[], activeIndex: number, available: number): { fit: TabFit; overflow: boolean } {
  const tabW = (i: number, labelled: boolean): number => (hasIcon[i] ? (labelled ? ICON_TAB_PX + LABEL_EXTRA_PX + labelWidths[i] : ICON_TAB_PX) : TEXT_TAB_EXTRA_PX + labelWidths[i]);
  const total = (labelled: (i: number) => boolean): number => labelWidths.reduce((sum, _w, i) => sum + tabW(i, labelled(i)), 0);
  if (total(() => true) <= available) return { fit: 'full', overflow: false };
  if (total((i) => i === activeIndex) <= available) return { fit: 'active', overflow: false };
  return { fit: 'icons', overflow: total(() => false) > available };
}

/**
 * A side panel's tabs: ONE row that never clips a label. Labels shrink to icons when they do not fit and a "More" menu
 * lists every tab when even the icons do not. Only the active tab's content is mounted, and it scrolls on its own so the
 * tab row stays put.
 */
export function PanelTabs({ value, onChange, tabs, label }: PanelTabsProps) {
  const listRef = React.useRef<HTMLDivElement | null>(null);
  const [fit, setFit] = React.useState<{ fit: TabFit; overflow: boolean }>({ fit: 'full', overflow: false });
  const activeIndex = tabs.findIndex((t) => t.value === value);

  React.useLayoutEffect(() => {
    const list = listRef.current;
    if (!list) return;
    const measure = () => {
      // Not laid out (hidden, or no layout engine): nothing to measure.
      if (list.clientWidth === 0) return;
      const widths = Array.from(list.querySelectorAll<HTMLElement>('[data-tab-label]')).map((el) => el.scrollWidth);
      // The row's padding (6 px each side) and the More button are not available to tabs.
      const next = fitTabs(widths, tabs.map((t) => Boolean(t.icon)), activeIndex, list.clientWidth - 12);
      setFit((prev) => (prev.fit === next.fit && prev.overflow === next.overflow ? prev : next));
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(list);
    return () => ro.disconnect();
  }, [tabs, activeIndex]);

  return (
    <Tabs value={value} onChange={onChange} keepMounted={false} className={classes.pane} color="violet">
      <div className={classes.paneTabsRow}>
        <Tabs.List ref={listRef} className={classes.paneTabs} aria-label={label}>
          {tabs.map(({ value: v, label: tabLabel, icon: Icon, testId }) => {
            const collapsed = fit.fit !== 'full' && Icon !== undefined && (fit.fit === 'icons' || v !== value);
            return (
              <Tooltip key={v} label={tabLabel} disabled={!collapsed} openDelay={400}>
                <Tabs.Tab
                  value={v}
                  className={classes.paneTab}
                  leftSection={Icon ? <Icon size={14} stroke={1.7} /> : undefined}
                  data-testid={testId}
                  data-collapsed={collapsed ? 'true' : undefined}
                >
                  <span data-tab-label className={collapsed ? classes.paneTabLabelHidden : classes.paneTabLabel}>
                    {tabLabel}
                  </span>
                </Tabs.Tab>
              </Tooltip>
            );
          })}
        </Tabs.List>
        {fit.overflow ? (
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <button type="button" className={classes.paneTabsMore} aria-label={`More ${label.toLowerCase()}`} data-testid="panel-tabs-more">
                <IconDots size={16} aria-hidden />
              </button>
            </Menu.Target>
            <Menu.Dropdown>
              {tabs.map(({ value: v, label: tabLabel, icon: Icon }) => (
                <Menu.Item key={v} leftSection={Icon ? <Icon size={14} stroke={1.7} /> : undefined} onClick={() => onChange(v)} data-active={v === value ? 'true' : undefined} fw={v === value ? 700 : 400}>
                  {tabLabel}
                </Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        ) : null}
      </div>
      {tabs.map(({ value: v, content, flush }) => (
        <Tabs.Panel key={v} value={v} className={[classes.paneBody, flush ? classes.paneBodyFlush : ''].filter(Boolean).join(' ')}>
          {content}
        </Tabs.Panel>
      ))}
    </Tabs>
  );
}
