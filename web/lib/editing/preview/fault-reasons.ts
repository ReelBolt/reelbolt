/**
 * The words for each reason the viewer is not on the live compositor: one cause, one sentence, plain
 * language ("graphics acceleration" is the one technical term, because it is what the browser calls its
 * setting). The cause itself travels as `PreviewSession.fault`; never guess it from capabilities.
 */

const SINGLE_CLIP = 'the viewer plays a single clip at a time over its small copy. Layers above the top clip, titles and audio mixing are not previewed.';

/** The Simple preview is on: the user switched to it (or Auto did, on a phone). */
export const REASON_USER_CHOICE = `You are on the simple preview, so ${SINGLE_CLIP}`;

/** This browser gives the page no WebGL2 context. */
export const REASON_NO_WEBGL2 = `This browser is not giving the editor graphics acceleration, so the live preview cannot run and ${SINGLE_CLIP} Turn on graphics acceleration (hardware acceleration) in the browser settings, then choose Try live preview again.`;

/** WebGL2 and WebCodecs exist, but the compositor failed to start. */
export const REASON_COMPOSITOR_FAILED = `The live preview could not start on this device, so ${SINGLE_CLIP} Choose Try live preview again.`;

/** The graphics context was lost and the browser may still restore it. */
export const REASON_CONTEXT_LOST = 'The graphics context was lost. Reconnecting…';

/** The graphics context was lost and the compositor could not be rebuilt. */
export const REASON_CONTEXT_FAILED = 'The graphics context was lost and the live preview could not be rebuilt. Choose Try live preview again.';

/** The buttons. */
export const ACTION_USE_LIVE = 'Use live preview';
export const ACTION_RETRY_LIVE = 'Try live preview again';
