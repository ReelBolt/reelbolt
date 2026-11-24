import { createTheme, type MantineColorsTuple } from "@mantine/core";

// The window's theme. It follows the dashboard's design language (Mantine,
// violet as the one accent, dark by default when the system is dark) so the
// runner reads as part of ReelBolt, not as a separate utility.
//
// Geist replaces the browser default: it is bundled with the app (no network),
// has tabular figures for the numbers this window is full of, and stays neutral
// enough to sit next to the dashboard.

// A cool, low-saturation neutral scale for dark mode, one step deeper than
// Mantine's default grey so the violet accent reads without glowing.
const dark: MantineColorsTuple = [
  "#d4d4d8",
  "#b4b4bb",
  "#8b8b94",
  "#5f5f68",
  "#3b3b42",
  "#2c2c32",
  "#222227",
  "#1a1a1e",
  "#141417",
  "#0f0f12",
];

export const theme = createTheme({
  primaryColor: "violet",
  primaryShade: { light: 6, dark: 5 },
  colors: { dark },
  fontFamily: "'Geist Variable', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif",
  fontFamilyMonospace: "'Geist Mono Variable', ui-monospace, 'SF Mono', Menlo, Consolas, monospace",
  headings: {
    fontFamily: "'Geist Variable', ui-sans-serif, system-ui, sans-serif",
    fontWeight: "600",
    sizes: {
      h1: { fontSize: "1.75rem", lineHeight: "1.15" },
      h2: { fontSize: "1.25rem", lineHeight: "1.25" },
      h3: { fontSize: "1rem", lineHeight: "1.35" },
      h4: { fontSize: "0.9375rem", lineHeight: "1.4" },
    },
  },
  defaultRadius: "md",
  radius: { xs: "4px", sm: "6px", md: "8px", lg: "12px", xl: "16px" },
  cursorType: "pointer",
  focusRing: "auto",
  components: {
    Button: { defaultProps: { radius: "md" } },
    Switch: { defaultProps: { size: "md" } },
    Tooltip: { defaultProps: { openDelay: 300, withArrow: true } },
  },
});
