import { createTheme } from '@mantine/core';
import { EDITOR_MODAL_Z } from '@/components/editor/ui/z-index';

/**
 * The editor's fixed bottom chrome (status bar 28 px + phone dock 52 px + safe area) must never cover a dropdown
 * option. Mantine's popovers (Select, Menu, Combobox, Popover) only flip upward when the panel would leave the
 * viewport, so a short list opened near the bottom used to land under that chrome. The editor routes reserve the strip
 * as flip/shift padding (see EditorPopoverTheme); every other route keeps Mantine's defaults (shift 5 px per side,
 * flip 0), because there is no bottom bar there.
 */
export const EDITOR_BOTTOM_CHROME_PX = 96;

const FONT_DISPLAY = 'var(--font-display-face), "Chakra Petch", Inter, sans-serif';
const FONT_MONO = 'var(--font-mono-face), "JetBrains Mono", ui-monospace, monospace';
const FONT_BODY = 'Inter, -apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif';

// Brand scale derived from the marketing site's accent tokens (accent #A855F7, accent-strong #7C3AED,
// accent-ink #C4B5FD, accent-deep #2E1065). Shade 6 (#6D28D9, a step deeper than the site's #7C3AED so light-variant text clears 4.5:1 on its own tint) is the filled-control colour in BOTH schemes (white on it 7.1:1).
const violet = ['#F6F2FF', '#EBE3FE', '#DCCDFC', '#C4B5FD', '#B07CF8', '#A855F7', '#6D28D9', '#5B21B6', '#4C1D95', '#2E1065'] as const;

// Site paper tones: body #0B0B12 (dark[7]), surface #15131D (dark[6]), raised #1D1927 (dark[5]), hairline #2E2A3A (dark[4]).
const dark = ['#F5F3F8', '#C7C2D3', '#A79FB4', '#8C849A', '#2E2A3A', '#1D1927', '#15131D', '#0B0B12', '#08080D', '#050509'] as const;

// Status colours moved onto the site palette: Mantine's `blue` (Running) becomes the accent, `green` (Done) a muted green, `red` (Failed) the danger red.
const green = ['#F0F7F2', '#DCEEE1', '#BADCC4', '#92C7A2', '#6FB486', '#58A672', '#2A7048', '#235C3B', '#265839', '#183D26'] as const;
const red = ['#FFF1F2', '#FFE0E3', '#FFC2C9', '#FA9AA6', '#F26F81', '#E5485E', '#B8243C', '#9B1C31', '#7F1629', '#520D1A'] as const;

// Warning hues (Alert/Badge/Text colour="yellow" | "orange", 100+ uses): shade 6 is a deep amber/rust so light-variant text clears 4.5:1 on its own tint, shade 4 the light text colour in dark mode.
const yellow = ['#FDF8E6', '#FDF0CF', '#F9E3A8', '#F2C45C', '#F2C45C', '#B07A0F', '#7A4A00', '#663D00', '#523100', '#3D2400'] as const;
const orange = ['#FFF4EB', '#FDE4D0', '#FACBA6', '#F7B07F', '#F5A66B', '#C7611F', '#9A3F08', '#833407', '#6B2A05', '#4F1F03'] as const;

// Categorical hues (provider kinds, skill categories, event types): desaturated to sit beside the violet brand; shade 6 clears 4.5:1 as white-on-fill and as light-variant text, shade 4 is the dark-mode text colour.
const teal = ['#F5FBFB', '#E5F6F4', '#CCEDE9', '#A5DFD8', '#7FD1C8', '#4F9E97', '#1F6B66', '#1A5B57', '#164B47', '#113B38'] as const;
const cyan = ['#F5FBFD', '#E7F5F9', '#CFEAF4', '#AADBEB', '#86CBE3', '#5298B2', '#1F6580', '#1A566D', '#16475A', '#113846'] as const;
const indigo = ['#F8F9FE', '#EDEFFC', '#DBE0FA', '#C0C8F6', '#A5B1F2', '#7280CA', '#3F4FA3', '#36438B', '#2C3772', '#232B5A'] as const;
const grape = ['#FDF8FC', '#F9EDF7', '#F3DAEF', '#E9BFE2', '#E0A3D6', '#B36AA8', '#86307A', '#722968', '#5E2255', '#4A1A43'] as const;
const pink = ['#FEF8FA', '#FDEDF2', '#FBDAE6', '#F7BFD3', '#F4A3C0', '#CC648C', '#A32657', '#8B204A', '#721B3D', '#5A1530'] as const;
const lime = ['#FAFCF4', '#F3F8E4', '#E7F1CA', '#D5E6A2', '#C3DB7A', '#8CA347', '#566B14', '#495B11', '#3C4B0E', '#2F3B0B'] as const;

const gray = ['#F8F7FB', '#F1EFF6', '#E6E3EE', '#D6D2E1', '#B9B3C8', '#938CA5', '#625D70', '#4F4A5D', '#2E2A3A', '#1B1824'] as const;

// Mono small-caps field labels. 12px is the floor for any text (theme.fontsize.test.ts guards it).
const LABEL_STYLE = { fontFamily: FONT_MONO, fontSize: 12, letterSpacing: '0.06em', textTransform: 'uppercase', marginBottom: 4 } as const;

export const theme = createTheme({
  primaryColor: 'violet',
  primaryShade: { light: 6, dark: 6 },
  colors: { violet: [...violet], blue: [...violet], green: [...green], red: [...red], yellow: [...yellow], orange: [...orange], teal: [...teal], cyan: [...cyan], indigo: [...indigo], grape: [...grape], pink: [...pink], lime: [...lime], dark: [...dark], gray: [...gray] },
  fontFamily: FONT_BODY,
  fontFamilyMonospace: FONT_MONO,
  defaultRadius: 'xs',
  radius: { xs: '2px', sm: '2px', md: '2px', lg: '3px', xl: '4px' },
  cursorType: 'pointer',
  focusRing: 'auto',
  headings: {
    fontFamily: FONT_DISPLAY,
    fontWeight: '700',
    sizes: {
      h1: { fontSize: '2rem', lineHeight: '1.1' },
      h2: { fontSize: '1.5rem', lineHeight: '1.15' },
      h3: { fontSize: '1.2rem', lineHeight: '1.2' },
      h4: { fontSize: '1rem', lineHeight: '1.25' },
    },
  },
  shadows: { xs: 'none', sm: 'none', md: 'none', lg: '0 0 0 1px var(--mantine-color-default-border)', xl: '0 0 0 1px var(--mantine-color-default-border)' },
  other: { fontMono: FONT_MONO, fontDisplay: FONT_DISPLAY },
  components: {
    // Plain object, not Popover.extend(): this module runs on the server (app/layout.tsx).
    Popover: {
      // Fixed strategy: Mantine scrolls the selected option into view (scrollIntoView) right after a Select opens,
      // while the portalled dropdown is still unpositioned. An absolutely positioned dropdown makes that scroll the
      // WINDOW on a tall page, which pushes the trigger off screen and Popover's hideDetached then display:none's the
      // dropdown (the phone workflow "Step Type" Select). A fixed dropdown is always in the viewport, so nothing scrolls.
      defaultProps: { floatingStrategy: 'fixed' },
    },
    Button: {
      defaultProps: { size: 'sm' },
      styles: { root: { fontFamily: FONT_DISPLAY, fontWeight: 700, letterSpacing: '0.02em', textTransform: 'none' } },
    },
    ActionIcon: { defaultProps: { variant: 'subtle' } },
    Badge: {
      styles: { root: { fontFamily: FONT_MONO, fontWeight: 500, letterSpacing: '0.06em', textTransform: 'uppercase' } },
    },
    Paper: { defaultProps: { shadow: 'none', withBorder: true } },
    Card: { defaultProps: { shadow: 'none', withBorder: true, padding: 'md' } },
    Title: { styles: { root: { letterSpacing: '0' } } },
    // Every dialog opens above the phone bottom sheet (z 250) and below dropdowns (300) and toasts (400): see EDITOR_MODAL_Z.
    // Drawers keep Mantine's 200 (the app header's lift to 201 at 600-1023 px relies on it); an editor drawer that must cover the sheet sets the constant itself.
    Modal: { defaultProps: { shadow: 'none', radius: 'xs', zIndex: EDITOR_MODAL_Z, overlayProps: { backgroundOpacity: 0.6, blur: 0 } } },
    Drawer: { defaultProps: { shadow: 'none', overlayProps: { backgroundOpacity: 0.6, blur: 0 } } },
    Menu: { defaultProps: { shadow: 'none', withBorder: true } },
    Tooltip: { defaultProps: { radius: 0 } },
    TextInput: { defaultProps: { size: 'sm' }, styles: { label: LABEL_STYLE } },
    Textarea: { styles: { label: LABEL_STYLE } },
    Select: { styles: { label: LABEL_STYLE } },
    NumberInput: { styles: { label: LABEL_STYLE } },
    // Every password field gets a keyboard-reachable eye toggle (Mantine defaults to tabindex -1);
    // its 44px touch size is in global.css.
    PasswordInput: {
      defaultProps: { visibilityToggleFocusable: true, visibilityToggleButtonProps: { 'aria-label': 'Show password' } },
      styles: { label: LABEL_STYLE },
    },
    MultiSelect: { styles: { label: LABEL_STYLE } },
    Autocomplete: { styles: { label: LABEL_STYLE } },
    TagsInput: { styles: { label: LABEL_STYLE } },
    ColorInput: { styles: { label: LABEL_STYLE } },
    NativeSelect: { styles: { label: LABEL_STYLE } },
    FileInput: { styles: { label: LABEL_STYLE } },
    Tabs: {
      styles: { tab: { fontFamily: FONT_DISPLAY, fontWeight: 700, letterSpacing: '0.02em' } },
    },
    Table: {
      styles: {
        th: { fontFamily: FONT_MONO, fontSize: 12, fontWeight: 500, letterSpacing: '0.06em', textTransform: 'uppercase', color: 'var(--mantine-color-dimmed)' },
      },
    },
    Progress: { defaultProps: { radius: 0 } },
    Accordion: { styles: { item: { borderColor: 'var(--rb-border)' }, label: { fontFamily: FONT_DISPLAY, fontWeight: 700 }, control: { minHeight: 44 } } },
    SegmentedControl: { styles: { root: { border: '1px solid var(--rb-border)', background: 'var(--rb-surface)' }, label: { fontFamily: FONT_DISPLAY, fontWeight: 700 }, indicator: { boxShadow: 'none', background: 'var(--mantine-color-violet-6)' } } },
    Stepper: { styles: { stepLabel: { fontFamily: FONT_DISPLAY, fontWeight: 700 }, stepDescription: { fontFamily: FONT_MONO, fontSize: 12 } } },
    Pagination: { styles: { control: { fontFamily: FONT_MONO } } },
    Timeline: { styles: { itemTitle: { fontFamily: FONT_DISPLAY, fontWeight: 700 } } },
    // A deep trail wraps on a phone; without a row gap the wrapped line touches the one above it.
    Breadcrumbs: { styles: { root: { flexWrap: 'wrap', rowGap: 4 } } },
    Alert: { styles: { title: { fontFamily: FONT_DISPLAY, fontWeight: 700 }, root: { borderColor: 'color-mix(in srgb, var(--alert-color) 35%, transparent)' } } },
    Notification: { defaultProps: { radius: 'xs' }, styles: { title: { fontFamily: FONT_DISPLAY, fontWeight: 700 } } },
    Checkbox: { styles: { label: { fontFamily: FONT_BODY } } },
    // Control shapes (control-shape-restyle): variant B "blade". The geometry, colours, focus ring
    // and hit areas live in global.css; these entries only point Mantine's own radius/thumb vars at
    // the --rb-control-* tokens, so a size prop keeps scaling both the track and the knob.
    Slider: {
      styles: {
        root: { '--slider-radius': 'var(--rb-control-thumb-radius)' },
        thumb: { width: 'var(--rb-control-thumb-size)', height: 'var(--rb-control-thumb-size)' },
      },
    },
    RangeSlider: {
      styles: {
        root: { '--slider-radius': 'var(--rb-control-thumb-radius)' },
        thumb: { width: 'var(--rb-control-thumb-size)', height: 'var(--rb-control-thumb-size)' },
      },
    },
    Switch: {
      styles: {
        track: { borderRadius: 'var(--rb-control-switch-radius)' },
        thumb: { borderRadius: 'var(--rb-control-switch-radius)' },
      },
    },

    NavLink: {
      styles: {
        root: { fontFamily: FONT_DISPLAY, fontWeight: 600, letterSpacing: '0.02em', borderLeft: '3px solid transparent', minHeight: 'var(--nav-link-min-height, 40px)' },
      },
    },
    AppShell: { styles: { header: { borderBottomWidth: 2 }, navbar: { borderRightWidth: 1 } } },
  },
});
