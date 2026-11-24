'use client';

import React from 'react';
import Link from 'next/link';
import { Button, Menu } from '@mantine/core';
import { IconChevronLeft, IconMoon, IconDots, IconKeyboard, IconMessageCircle, IconSend } from '@tabler/icons-react';
import type { WorkspaceId } from '@/lib/editing/store';
import { EditorIcon } from '../ui/EditorIcon';
import { useEditorDisplay } from '../ui/EditorDisplay';
import { DisplayMenu } from '../prefs/DisplayMenu';
import { WorkspaceBar } from './WorkspaceBar';
import { EarlyPreviewLabel } from './EarlyPreviewLabel';
import classes from './workspace.module.css';
import { useT } from '@/i18n';

export interface WorkspaceHeaderProps {
  name: string;
  revision: number;
  /** Where "back" goes: the hub's Timelines view of this project. */
  backHref: string;
  /** The timeline's phone review page. */
  reviewHref: string;
  workspace: WorkspaceId;
  onWorkspace: (workspace: WorkspaceId) => void;
  /** The saved-layouts menu (desktop only). */
  layoutMenu?: React.ReactNode;
  onShortcuts: () => void;
  /** Opens the Deliver workspace, where renders are made. */
  onExport: () => void;
  /** Phone layout: the switcher moves to the bottom dock and the secondary actions into one menu. */
  compact?: boolean;
  /**
   * Tablet (RM.15): the switcher lives in the 56 px rail beside the editor, the secondary actions sit in one
   * "More" menu (with the honest tablet note), and Export stays a full-size button.
   */
  tablet?: boolean;
  /** T-007: the live page chord per workspace, so the switcher's tooltip and AT state it. */
  chords?: Partial<Record<WorkspaceId, string>>;
  /** T-002: the page h1; focus moves here on a switch (never on the first mount). */
  headingRef?: React.RefObject<HTMLHeadingElement | null>;
  /** The "AI tools" launcher button. */
  aiTools?: React.ReactNode;
  /** Background results waiting for the user's OK (a chip), shown first in the right-hand group. */
  pending?: React.ReactNode;
  /** The Simple | Full switch (desktop and tablet; the phone has one surface). */
  shellSwitch?: React.ReactNode;
}

/**
 * The workspace header: back to the timelines, the timeline's name and revision, the workspace
 * switcher in the middle, and the few global actions on the right (review page, layouts, keyboard
 * shortcuts, export). One row, 48px, on every workspace. The "Early preview" label sits with the
 * name, so on a phone it never takes a control's slot.
 */
function SurroundItem() {
  const t = useT('editor.shell.workspaceHeader');
  const { surround, setSurround } = useEditorDisplay();
  return (
    <Menu.Item mih={44} leftSection={<IconMoon size={16} />} closeMenuOnClick={false} onClick={() => setSurround(surround === 'dark' ? 'match' : 'dark')} data-testid="more-surround">
      {surround === 'dark' ? t('editorAlwaysDarkOn') : t('editorAlwaysDarkOff')}
    </Menu.Item>
  );
}

export function WorkspaceHeader({ name, revision, backHref, reviewHref, workspace, onWorkspace, layoutMenu, onShortcuts, onExport, compact = false, tablet = false, chords, headingRef, pending, aiTools, shellSwitch }: WorkspaceHeaderProps) {
  const t = useT('editor.shell.workspaceHeader');
  const wb = useT('editor.shell.workspaceBar');
  return (
    <>
    {/* Two columns on every layout: the page switcher left the header for its own band, and a third, empty column
        left the actions in the middle of the row instead of at its end. */}
    <header className={classes.header} style={{ gridTemplateColumns: 'minmax(0, 1fr) auto' }}>
      <h1 ref={headingRef} tabIndex={-1} className={classes.pageHeading} data-testid="page-heading">
        {wb(`names.${workspace}.label`)}
      </h1>
      <div className={classes.headerStart}>
        <EditorIcon icon={IconChevronLeft} size="lg" href={backHref} label={t('backToAllTimelines')} tooltip={t('allTimelines')} data-testid="back-to-timelines" />
        <span className={classes.title} title={name}>
          {name}
        </span>
        <span className={classes.revision} title={t('savedVersion')}>{t('version', { revision })}</span>
        <EarlyPreviewLabel />
      </div>

      <div className={classes.headerEnd}>
        {aiTools}
        {pending}
        {!compact && shellSwitch}
        {tablet ? (
          <>
            {/* Saved layouts are panel sizes: they matter once the side panels are panels, not drawers. */}
            {layoutMenu ? (
              <span className={classes.tabletLayouts} data-testid="tablet-layouts">
                {layoutMenu}
              </span>
            ) : null}
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <EditorIcon icon={IconDots} size="lg" label={t('more')} tooltip={false} data-testid="header-more" aria-haspopup="menu" />
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item mih={44} leftSection={<IconMessageCircle size={16} />} component={Link} href={reviewHref} data-testid="open-review-page">
                  {t('reviewPage')}
                </Menu.Item>
                <Menu.Item mih={44} leftSection={<IconKeyboard size={16} />} onClick={onShortcuts} data-testid="open-keymap">
                  {t('keyboardShortcuts')}
                </Menu.Item>
                <SurroundItem />
                <Menu.Divider />
                <div className={classes.tabletNote} data-testid="tablet-note">
                  {t('tabletWorksNotCertified')}
                </div>
              </Menu.Dropdown>
            </Menu>
            <Button size="md" fz="sm" mih={44} ml={4} miw="max-content" style={{ flexShrink: 0 }} leftSection={<IconSend size={16} stroke={1.8} />} onClick={onExport} data-testid="open-deliver">
              {t('export')}
            </Button>
          </>
        ) : compact ? (
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <EditorIcon icon={IconDots} size="lg" label={t('more2')} tooltip={false} data-testid="header-more" aria-haspopup="menu" />
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item mih={44} leftSection={<IconSend size={16} />} onClick={onExport} data-testid="more-export">
                {t('export2')}
              </Menu.Item>
              <Menu.Item mih={44} leftSection={<IconMessageCircle size={16} />} component={Link} href={reviewHref} data-testid="open-review-page">
                {t('reviewPage2')}
              </Menu.Item>
              <Menu.Item mih={44} leftSection={<IconKeyboard size={16} />} onClick={onShortcuts} data-testid="open-keymap">
                {t('keyboardShortcuts')}
              </Menu.Item>
              <SurroundItem />
            </Menu.Dropdown>
          </Menu>
        ) : (
          <>
            <EditorIcon icon={IconMessageCircle} href={reviewHref} label={t('reviewPage3')} tooltip={t('reviewPageWatchThe')} data-testid="open-review-page" />
            <DisplayMenu />
            {layoutMenu}
            <EditorIcon icon={IconKeyboard} size="lg" label={t('keyboardShortcuts2')} tooltip={t('keyboardShortcutsTip')} onClick={onShortcuts} data-testid="open-keymap" />
            <Button size="xs" fz="xs" ml={4} miw="max-content" style={{ flexShrink: 0 }} leftSection={<IconSend size={14} stroke={1.8} />} onClick={onExport} data-testid="open-deliver">
              {t('export3')}
            </Button>
          </>
        )}
      </div>
    </header>
    {!compact && !tablet && (
      <div className={classes.pageBand} data-testid="page-band">
        <WorkspaceBar active={workspace} onChange={onWorkspace} chords={chords} />
      </div>
    )}
    </>
  );
}
