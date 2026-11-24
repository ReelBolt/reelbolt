import { expect, type CDPSession, type Locator, type Page } from '@playwright/test';
import type { Timeline } from './api';
import type { Seed } from './seed';

/** TimelineCanvas geometry (web/components/editor/timeline/paint.ts): ruler + marker lane above the tracks. */
export const RULER_HEIGHT = 28;
export const MARKER_LANE_HEIGHT = 14;
/** Default zoom: 60 px per second at 30 fps. */
export const PX_PER_FRAME = 2;

export type PatchRecord = { status: number; ops: string[]; baseRevision?: number; body: string };

/**
 * One open timeline page: records every PATCH the sync client sends, knows where clips are drawn on the
 * single <canvas>, and drives input the way the device would (mouse on desktop, touch on phones).
 */
export class EditorPage {
  readonly patches: PatchRecord[] = [];
  readonly isPhone: boolean;
  /** Phone or tablet: a finger, so taps are real touches (the tablet keeps the desktop layout, RM.15). */
  readonly isTouch: boolean;
  private cdp?: CDPSession;

  private constructor(
    readonly page: Page,
    readonly seed: Seed,
    readonly timelineId: string,
    readonly projectName: string,
  ) {
    this.isPhone = projectName !== 'desktop' && projectName !== 'tablet';
    this.isTouch = projectName !== 'desktop';
  }

  static async open(page: Page, seed: Seed, timelineId: string, projectName: string): Promise<EditorPage> {
    const ed = new EditorPage(page, seed, timelineId, projectName);
    // The preview defaults to Auto (a phone or software-graphics browser starts on the Simple preview). The suite's
    // existing specs assert the live compositor, so they run on the explicit "Half" pick unless a spec has already
    // chosen otherwise (a spec that wants Auto registers its own init script BEFORE opening the editor).
    await page.addInitScript(() => {
      try {
        if (localStorage.getItem('reelbolt:editor:preview-quality') === null) localStorage.setItem('reelbolt:editor:preview-quality', 'half');
      } catch { /* storage off */ }
    });
    page.on('response', async (res) => {
      const req = res.request();
      if (req.method() !== 'PATCH' || !res.url().includes(`/edit-timelines/${timelineId}`)) return;
      let ops: string[] = [];
      let baseRevision: number | undefined;
      try {
        const body = req.postDataJSON() as { ops?: Array<{ type: string }>; baseRevision?: number };
        ops = (body.ops ?? []).map((o) => o.type);
        baseRevision = body.baseRevision;
      } catch { /* keepalive/beacon bodies may not be readable */ }
      let text = '';
      try { text = (await res.text()).slice(0, 600); } catch { /* navigated away */ }
      ed.patches.push({ status: res.status(), ops, baseRevision, body: text });
    });
    await ed.goto();
    return ed;
  }

  get url(): string {
    return `/app/editor/${this.seed.projectId}/timeline/${this.timelineId}`;
  }

  async goto(): Promise<void> {
    await this.page.goto(this.url);
    await expect(this.page.getByTestId('editor-workspace')).toBeVisible({ timeout: 45_000 });
    // The editor remembers the last page, and Deliver (like Media) has no timeline: start from Edit so a re-open after a render has one.
    if (!this.isPhone && !(await this.canvas.isVisible().catch(() => false))) await this.workspace('edit');
    await expect(this.canvas).toBeVisible({ timeout: 45_000 });
    await expect(this.chip).toHaveAttribute('data-status', 'saved', { timeout: 30_000 });
  }

  async reload(): Promise<void> {
    await this.page.reload();
    // T-001 remembers the last page; a reload can land on Media (no timeline) or Cut (overview canvas),
    // so wait for the workspace, not for the timeline canvas.
    await expect(this.page.getByTestId('editor-workspace')).toBeVisible({ timeout: 45_000 });
    await expect(this.chip).toHaveAttribute('data-status', 'saved', { timeout: 30_000 });
  }

  /* ------------------------------------------------------------------ locators */

  get canvas(): Locator { return this.page.getByTestId('timeline-canvas'); }
  get chip(): Locator { return this.page.getByTestId('save-state-chip'); }
  get viewer(): Locator { return this.page.getByTestId('program-canvas'); }
  testId(id: string): Locator { return this.page.getByTestId(id).first(); }

  /* ------------------------------------------------------------------ server state */

  serverTimeline(): Promise<Timeline> {
    return this.seed.api.getTimeline(this.seed.projectId, this.timelineId);
  }

  /** Waits until the chip says the latest edit is stored ("Saved · rev N"), then returns the server copy. */
  async expectSaved(timeout = 20_000): Promise<Timeline> {
    await expect(this.chip).toHaveText(/^\s*(Saved|Salvato)/, { timeout });
    return this.serverTimeline();
  }

  /** Polls the SERVER until `predicate` holds (the chip can lie; the stored revision cannot). */
  async expectServer(predicate: (tl: Timeline) => boolean, message: string, timeout = 20_000): Promise<Timeline> {
    let last!: Timeline;
    try {
      await expect.poll(async () => {
        last = await this.serverTimeline();
        // A predicate that throws on a document that is not stored yet (an undefined grade, say) means "not yet".
        try { return predicate(last); } catch { return false; }
      }, { message, timeout }).toBe(true);
    } catch {
      const chip = await this.chip.textContent({ timeout: 1000 }).catch(() => '?');
      throw new Error(`${message}: server rev ${last?.revision}; chip "${chip?.trim()}"; PATCHes ${this.patchSummary()}`);
    }
    return last;
  }

  /** Last PATCHes as `status[op,op] body…`, for failure messages. */
  patchSummary(n = 3): string {
    return JSON.stringify(this.patches.slice(-n).map((p) => `${p.status}[${p.ops.join(',')}] ${p.body.slice(0, 160)}`));
  }

  /* ------------------------------------------------------------------ layout and input */

  async canvasBox(): Promise<{ x: number; y: number; width: number; height: number }> {
    await this.showTimeline();
    // Late banners (linked-sound offer, notices) push the lanes down: wait until the box stops moving, and bring it on screen
    // (a touchscreen tap outside the viewport is silently lost).
    if (!this.keepScroll) await this.canvas.evaluate((el) => {
      // A tall canvas on a phone is aligned to the top of its pane: centring can leave the ruler above the pane, under the segmented control.
      el.scrollIntoView({ block: el.getBoundingClientRect().height > window.innerHeight * 0.5 && window.innerWidth < 700 ? 'start' : 'center', inline: 'nearest' });
    });
    let box = await this.canvas.boundingBox();
    for (let i = 0; i < 20; i++) {
      await this.page.waitForTimeout(100);
      const next = await this.canvas.boundingBox();
      if (next && box && next.y === box.y && next.x === box.x) break;
      box = next;
    }
    expect(box, 'timeline canvas box').toBeTruthy();
    return box!;
  }

  /** Centre y of a track lane (index in document order, heights from the document). */
  async laneY(trackIndex: number): Promise<number> {
    const cv = await this.canvasBox();
    const doc = (await this.serverTimeline()).document;
    let top = cv.y + RULER_HEIGHT + MARKER_LANE_HEIGHT;
    for (let i = 0; i < trackIndex; i++) top += Number(doc.tracks[i].height ?? 56);
    const y = top + Number(doc.tracks[trackIndex].height ?? 56) / 2;
    // A lane the pane has scrolled out from under an overlay (phone/tablet chrome) would take the tap: bring the pane to its top once.
    const hit = await this.canvas.evaluate((el, pt) => {
      const e = document.elementFromPoint(pt.x, pt.y);
      return !!e && (e === el || el.contains(e) || e.contains(el));
    }, { x: cv.x + cv.width / 2, y }).catch(() => true);
    if (!hit && !this.laneRetry && (this.page.viewportSize()?.width ?? 0) >= 700) {
      // Sticky tablet chrome (the clip tools) covers the lane: scroll the timeline pane so the lane sits just below the overlay.
      this.laneRetry = true;
      try {
        const moved = await this.canvas.evaluate((el, pt) => {
          const over = document.elementFromPoint(pt.x, pt.y);
          // Only chrome that sits ABOVE the lane (sticky tools) is cleared; a lane under the bottom bar is left to the caller.
          if (!over || over.getBoundingClientRect().top > pt.y - pt.half) return false;
          const delta = over.getBoundingClientRect().bottom - (pt.y - pt.half) + 6;
          for (let p = el.parentElement; p; p = p.parentElement) {
            if (p.scrollHeight > p.clientHeight + 1 && p.scrollTop > 0) {
              p.scrollTop = Math.max(0, p.scrollTop - delta);
              return true;
            }
          }
          return false;
        }, { x: cv.x + cv.width / 2, y, half: Number(doc.tracks[trackIndex].height ?? 56) / 2 });
        if (moved) {
          await this.page.waitForTimeout(200);
          this.keepScroll = true;
          return await this.laneY(trackIndex);
        }
      } finally {
        this.laneRetry = false;
        this.keepScroll = false;
      }
    }
    return y;
  }
  private laneRetry = false;
  private keepScroll = false;

  async frameX(frame: number): Promise<number> {
    const cv = await this.canvasBox();
    // The phone clip strip keeps the playhead at its centre (the canvas says so); everywhere else frame 0 is the left edge.
    const lock = await this.canvas.evaluate((el) => (el.getAttribute('data-centre-lock') === 'true' ? { playhead: Number(el.getAttribute('data-playhead-frame')), ppf: Number(el.getAttribute('data-ppf')) } : null));
    if (lock) return cv.x + cv.width / 2 + (frame - lock.playhead) * lock.ppf;
    return cv.x + frame * PX_PER_FRAME;
  }

  async tapAt(x: number, y: number, modifiers?: Array<'Shift' | 'Control'>): Promise<void> {
    if (this.isTouch && !modifiers) {
      await this.page.touchscreen.tap(x, y);
      return;
    }
    for (const m of modifiers ?? []) await this.page.keyboard.down(m);
    await this.page.mouse.click(x, y);
    for (const m of modifiers ?? []) await this.page.keyboard.up(m);
  }

  /** Picks a trim mode whether the toolbar shows the four-way control or has folded it into its menu. */
  async chooseTrimMode(mode: 'ripple' | 'roll' | 'slip' | 'slide'): Promise<void> {
    const option = this.testId(`trim-mode-${mode}`);
    if (!(await option.isVisible())) await this.tap(this.testId('trim-mode'));
    await this.tap(option);
  }

  /** Presses a frame stepper whether it is on the toolbar or folded behind its button; the panel is closed again after. */
  async stepper(id: string): Promise<void> {
    const button = this.testId(id);
    if (await button.isVisible()) return this.tap(button);
    await this.tap(this.testId('steppers-menu'));
    await this.tap(button);
    await this.tap(this.testId('steppers-menu'));
    await this.testId('steppers-menu-panel').waitFor({ state: 'hidden' });
  }

  async tap(locator: Locator): Promise<void> {
    await locator.scrollIntoViewIfNeeded();
    if (this.isTouch) await locator.tap();
    else await locator.click();
  }

  /** Selects the clip drawn at `frame` on track `trackIndex`. */
  async selectClipAt(frame: number, trackIndex = 0, modifiers?: Array<'Shift' | 'Control'>): Promise<void> {
    await this.tapAt(await this.frameX(frame), await this.laneY(trackIndex), modifiers);
  }

  /** Moves the playhead by typing a timecode (works on every layout). */
  async seek(frame: number): Promise<void> {
    const fps = 30;
    const tc = [Math.floor(frame / fps / 3600), Math.floor(frame / fps / 60) % 60, Math.floor(frame / fps) % 60, frame % fps]
      .map((n) => String(n).padStart(2, '0')).join(':');
    const field = this.testId('timecode-field');
    await this.closeDrawer();
    // Deliver is its own page (no timecode field, no timeline): coming back from a render, return to Edit first.
    if (!(await field.isVisible().catch(() => false)) && !this.isPhone) await this.workspace('edit');
    // Wave 3 T-017: with the viewer on Source (a placed source stays there) the transport is the source's; the playhead is Record's.
    const modeSwitch = this.testId('viewer-mode-switch');
    if ((await modeSwitch.getAttribute('data-mode', { timeout: 500 }).catch(() => null)) === 'source') await this.tap(this.testId('viewer-mode-record'));
    await field.click();
    await field.fill(tc);
    await field.press('Enter');
    await expect(this.testId('status-timecode')).toHaveText(tc);
  }

  /** Real touch drag through CDP `Input.dispatchTouchEvent` (Chromium only). */
  async touchDrag(x0: number, y0: number, x1: number, y1: number, steps = 12): Promise<void> {
    this.cdp ??= await this.page.context().newCDPSession(this.page);
    await this.cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: x0, y: y0, id: 1 }] });
    for (let i = 1; i <= steps; i++) {
      await this.cdp.send('Input.dispatchTouchEvent', {
        type: 'touchMove',
        touchPoints: [{ x: x0 + ((x1 - x0) * i) / steps, y: y0 + ((y1 - y0) * i) / steps, id: 1 }],
      });
      await this.page.waitForTimeout(16);
    }
    await this.cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  }

  async mouseDrag(x0: number, y0: number, x1: number, y1: number, steps = 12): Promise<void> {
    await this.page.mouse.move(x0, y0);
    await this.page.mouse.down();
    await this.page.mouse.move(x1, y1, { steps });
    await this.page.mouse.up();
  }

  /* ------------------------------------------------------------------ phone-aware editing actions (RM.16) */

  /** Phone: selects the clip drawn under the (centred) playhead unless one is already selected, so the action bar offers Split. No-op elsewhere. */
  async selectClipUnderPlayhead(): Promise<void> {
    if (!this.isPhone) return;
    const bar = this.page.getByTestId('phone-action-bar');
    if ((await bar.getAttribute('data-mode')) === 'clip') return;
    const cv = await this.canvasBox();
    await this.tapAt(cv.x + cv.width / 2, await this.laneY(0));
    await expect(bar).toHaveAttribute('data-mode', 'clip');
  }

  /** Splits at the playhead: the toolbar scissors on desktop and tablet, the action bar's Split on a phone (selecting the clip under the playhead first when nothing is selected). */
  async splitAtPlayhead(): Promise<void> {
    if (this.isPhone) {
      // The action bar offers Split once a clip is selected: with nothing selected, tap the clip under the (centred) playhead.
      await this.selectClipUnderPlayhead();
      return this.tap(this.page.getByTestId('phone-action-split'));
    }
    await this.tap(this.testId('blade-at-playhead'));
  }

  /**
   * Runs one Clip-menu action on the selection: Clip menu (desktop, tablet) or the action bar and its More sheet (phone).
   * `close-gap` and `close-gaps` are desktop and tablet only (the phone has no gap tool; it nudges through its Nudge sheet).
   */
  async clipAction(name: 'delete' | 'lift' | 'duplicate' | 'split' | 'unlink' | 'freeze-frame' | 'close-gap' | 'close-gaps' | 'nudge-forward'): Promise<void> {
    if (!this.isPhone) {
      await this.tap(this.testId('clip-actions-open'));
      await this.tap(this.testId(`clip-action-${name}`));
      return;
    }
    const direct: Record<string, string> = { delete: 'phone-action-delete', duplicate: 'phone-action-duplicate', split: 'phone-action-split' };
    const more: Record<string, string> = { lift: 'phone-more-lift', unlink: 'phone-more-unlink', 'freeze-frame': 'phone-more-freeze' };
    if (name === 'nudge-forward') {
      await this.tap(this.page.getByTestId('phone-action-nudge'));
      return this.tap(this.page.getByTestId('phone-nudge+1'));
    }
    if (direct[name]) return this.tap(this.page.getByTestId(direct[name]));
    if (more[name]) {
      await this.tap(this.page.getByTestId('phone-action-more'));
      return this.tap(this.page.getByTestId(more[name]));
    }
    throw new Error(`The "${name}" action is desktop and tablet only; call runOn('desktop', 'tablet') first`);
  }

  /** The "Add" menu (desktop toolbar) or the action bar's Add media (phone, nothing selected). */
  async openAddMenu(): Promise<void> {
    if (this.isPhone) return this.tap(this.page.getByTestId('phone-action-add-media'));
    await this.tap(this.testId('add-menu'));
  }

  /** Undo by the on-screen button: the toolbar's on desktop and tablet, the header's on a phone. */
  async undo(): Promise<void> {
    await this.tap(this.isPhone ? this.page.getByTestId('phone-undo') : this.testId('undo'));
  }

  /** Redo by the on-screen button: the toolbar's on desktop and tablet, the header's on a phone. */
  async redo(): Promise<void> {
    await this.tap(this.isPhone ? this.page.getByTestId('phone-redo') : this.testId('redo'));
  }

  /** Zoom to fit: the toolbar button on desktop, the strip's Fit chip on a phone. */
  async zoomToFit(): Promise<void> {
    await this.tap(this.isPhone ? this.page.getByTestId('strip-fit') : this.testId('zoom-fit'));
  }

  /**
   * How many clips are selected: the Clip menu's "N clips selected" line on desktop and tablet, the strip's spoken
   * description on a phone ("No clip selected" / "..., and N more selected"). Returns -1 when the line is unreadable.
   */
  async selectedCount(): Promise<number> {
    if (this.isPhone) {
      const text = (await this.testId('strip-description').textContent()) ?? '';
      if (/^No clip selected/i.test(text)) return 0;
      const more = /and (\d+) more selected/i.exec(text);
      return more ? 1 + Number(more[1]) : 1;
    }
    const open = this.testId('clip-actions-open');
    if (await open.isDisabled()) return 0;
    if (!(await this.testId('clip-actions-menu').isVisible())) await this.tap(open);
    const text = (await this.testId('clip-actions-menu').textContent()) ?? '';
    await this.page.keyboard.press('Escape');
    await expect(this.testId('clip-actions-menu')).toBeHidden();
    const m = /(\d+) clips? selected/.exec(text);
    return m ? Number(m[1]) : -1;
  }

  /** Switches the phone's Select mode on or off through the action bar (no-op on desktop, where Ctrl/Shift-click selects). */
  async setSelectMode(on: boolean): Promise<void> {
    if (!this.isPhone) return;
    const mode = await this.page.getByTestId('phone-action-bar').getAttribute('data-mode');
    if (on && mode !== 'select') await this.tap(this.page.getByTestId('phone-action-select'));
    if (!on && mode === 'select') await this.tap(this.page.getByTestId('phone-action-select-done'));
  }

  /** True when the editing controls that change the document are disabled (view-only): phone action bar buttons or the desktop toolbar. */
  async editingDisabled(): Promise<boolean> {
    if (!this.isPhone) return this.testId('blade-at-playhead').isDisabled();
    return this.page.getByTestId('phone-action-add-media').isDisabled();
  }

  /* ------------------------------------------------------------------ panels (desktop tabs / phone segments) */

  /** The phone surface opens one sheet per dock tool; the sheet hosts the same panel the desktop shows. */
  private async dock(item: 'media' | 'edit' | 'audio' | 'look' | 'captions'): Promise<void> {
    if (!this.isPhone) return;
    const sheet = this.page.getByTestId('phone-sheet');
    const button = this.page.getByTestId(`dock-${item}`);
    if (!(await sheet.isVisible().catch(() => false)) || (await button.getAttribute('aria-pressed')) !== 'true') {
      if ((await button.getAttribute('aria-pressed')) === 'true') return;
      await this.tap(button);
    }
    await sheet.waitFor({ state: 'visible' });
  }

  /** Opens the Remove silence dialog: the timeline toolbar button on desktop and tablet, the Captions sheet on a phone. */
  async openRemoveSilence(): Promise<void> {
    if (this.isPhone) {
      await this.dock('captions');
      await this.tap(this.page.getByTestId('phone-captions-remove-silence'));
      return;
    }
    await this.tap(this.testId('remove-silence-open'));
  }

  /** Closes the open phone sheet so the timeline strip is the whole working area again. */
  private async closeSheet(): Promise<void> {
    const close = this.page.getByTestId('phone-sheet-close');
    if (await close.isVisible().catch(() => false)) await this.tap(close);
  }

  async showTimeline(): Promise<void> {
    if (this.isPhone) {
      await this.closeSheet();
      return;
    }
    // Deliver is its own page (no timeline): coming back from a render, return to Edit first.
    if (!(await this.canvas.isVisible().catch(() => false))) await this.workspace('edit');
  }

  /** Opens a left-panel tab by its test id (e.g. `tab-structure`) or role name (e.g. `Markers`). */
  async openPanelTab(tab: string | RegExp): Promise<void> {
    await this.dock('captions');
    const loc = typeof tab === 'string' && tab.startsWith('tab-') ? this.page.getByTestId(tab) : this.page.getByRole('tab', { name: tab });
    // On a phone the desktop panels sit under "More tools" in the Captions sheet.
    if (this.isPhone && !(await loc.first().isVisible().catch(() => false))) await this.tap(this.page.getByTestId('phone-captions-more'));
    // Tablet widths (<= 1100 px) keep the right-hand panels in a drawer behind the "Inspector" button.
    if (!(await loc.first().isVisible().catch(() => false))) await this.openDrawer();
    await this.tap(loc.first());
  }

  async openInspector(): Promise<void> {
    if (this.isPhone) return this.dock('edit');
    await this.openDrawer();
  }

  /** Tablet layout: closes the right-hand drawer when it is open (it overlays the viewer and the timeline's right edge). */
  async closeDrawer(): Promise<void> {
    const toggle = this.testId('inspector-toggle');
    if (!(await toggle.isVisible().catch(() => false))) return;
    if ((await toggle.getAttribute('aria-expanded')) === 'true') {
      await this.tap(toggle);
      await expect(this.testId('panel-right')).toHaveCount(0, { timeout: 10_000 });
    }
  }

  /** Tablet layout: opens the right-hand drawer when the "Inspector" button is on screen and the drawer is closed (no-op otherwise). */
  async openDrawer(): Promise<void> {
    const toggle = this.testId('inspector-toggle');
    if (!(await toggle.isVisible().catch(() => false))) return;
    if ((await toggle.getAttribute('aria-expanded')) !== 'true') await this.tap(toggle);
    await expect(this.testId('panel-right')).toBeVisible({ timeout: 10_000 });
  }

  async workspace(name: 'media' | 'cut' | 'edit' | 'compose' | 'color' | 'audio' | 'deliver'): Promise<void> {
    if (this.isPhone) {
      // The phone has no workspace bar: the dock opens a sheet per tool, the header's Export opens Deliver.
      if (name === 'edit') return this.closeSheet();
      if (name === 'deliver') {
        await this.tap(this.page.getByTestId('phone-export'));
        await this.page.getByTestId('phone-sheet').waitFor({ state: 'visible' });
        return;
      }
      if (name === 'cut' || name === 'compose') throw new Error(`The ${name} workspace is not on the phone surface (desktop and tablet only)`);
      return this.dock(name === 'color' ? 'look' : name);
    }
    await this.closeDrawer();
    await this.tap(this.page.getByTestId(`workspace-${name}`));
    // §8.2/T-006: Colour has no gate — the node stack, the tools tabs and the scopes are on screen at once.
  }

  /** Switches the mixer's "Advanced audio" on (EQ, dynamics, buses, ducking); no-op when on or not on screen (phone). */
  async openAdvancedAudio(waitMs = 10_000): Promise<void> {
    const toggle = this.page.getByTestId('advanced-audio');
    const shown = await toggle.waitFor({ state: 'visible', timeout: waitMs }).then(() => true, () => false);
    if (shown && !(await toggle.isChecked())) await toggle.check({ force: true });
  }
}
