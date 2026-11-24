import type { TagColor } from '../types/project-organization';

export interface TagChipColors {
  /** Mantine palette name, for `color` props and borders. */
  mantine: TagColor;
  light: { bg: string; text: string };
  dark: { bg: string; text: string };
}

/** Closed palette words -> chip colours. Text always >= 4.5:1 on its background (checked in tests). */
export const TAG_COLOR_TOKENS: Record<TagColor, TagChipColors> = {
  gray: { mantine: "gray", light: { bg: "#f1f3f5", text: "#141619" }, dark: { bg: "#3c4044", text: "#f1f3f5" } },
  red: { mantine: "red", light: { bg: "#ffe3e3", text: "#791919" }, dark: { bg: "#733030", text: "#ffe3e3" } },
  orange: { mantine: "orange", light: { bg: "#ffe8cc", text: "#822b09" }, dark: { bg: "#734213", text: "#ffe8cc" } },
  yellow: { mantine: "yellow", light: { bg: "#fff3bf", text: "#8a4700" }, dark: { bg: "#71580b", text: "#fff3bf" } },
  green: { mantine: "green", light: { bg: "#d3f9d8", text: "#1a5325" }, dark: { bg: "#245d2e", text: "#d3f9d8" } },
  teal: { mantine: "teal", light: { bg: "#c3fae8", text: "#054c37" }, dark: { bg: "#0e5a44", text: "#c3fae8" } },
  blue: { mantine: "blue", light: { bg: "#d0ebff", text: "#0e3c67" }, dark: { bg: "#17456c", text: "#d0ebff" } },
  indigo: { mantine: "indigo", light: { bg: "#dbe4ff", text: "#202f77" }, dark: { bg: "#293871", text: "#dbe4ff" } },
  violet: { mantine: "violet", light: { bg: "#e5dbff", text: "#392576" }, dark: { bg: "#3b2a6f", text: "#e5dbff" } },
  pink: { mantine: "pink", light: { bg: "#ffdeeb", text: "#64122e" }, dark: { bg: "#6c2d43", text: "#ffdeeb" } },
};

/** Unknown words (e.g. from a newer server) render as gray. */
export function tagChipColors(color: string): TagChipColors {
  return (TAG_COLOR_TOKENS as Record<string, TagChipColors | undefined>)[color] ?? TAG_COLOR_TOKENS.gray;
}
