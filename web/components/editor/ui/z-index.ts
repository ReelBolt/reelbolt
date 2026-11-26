/**
 * Stacking for dialogs opened from the editor.
 *
 * The phone bottom sheet is at 250 (its backdrop at 249, `phone.module.css`), and a Mantine modal defaults to 200, so a dialog
 * opened from a sheet would sit under it. Mantine's dropdowns and popovers are at 300 and its notifications at 400, so a dialog
 * must stay below those to leave its own Select and Menu dropdowns clickable and keep toasts visible.
 * One value between them: above the sheet, below the dropdowns.
 */
export const EDITOR_MODAL_Z = 260;
