// Source scanner for the editor's 12 px text floor (VF2). Used by no-small-fonts.test.ts.
export const MIN_FONT_PX = 12;
const REM_PX = 16;

export interface FontViolation {
  line: number;
  text: string;
}

function toPx(value: number, unit: string): number {
  return unit === 'px' ? value : value * REM_PX;
}

const RULES: RegExp[] = [
  /\bsize=["'](\d+(?:\.\d+)?)(px|rem)["']/g, // <Text size="10px">
  /\b(?:fz|fontSize)=\{?\s*["'](\d+(?:\.\d+)?)(px|rem)["']/g, // fz="10px"
  /\bfontSize:\s*["'](\d+(?:\.\d+)?)(px|rem)["']/g, // fontSize: '11px'
  /\bfont-size:\s*(\d+(?:\.\d+)?)(px|rem)\b/g, // CSS
  /\bfont(?:-size)?:\s*(?:bold |italic )?(\d+(?:\.\d+)?)(px|rem)\s/g, // CSS shorthand / ctx.font
  /\bfont\s*=\s*[`'"](?:bold |italic )?(\d+(?:\.\d+)?)(px|rem)\b/g, // ctx.font = '10px ...'
];
const BARE_NUMBER = /\b(?:fz|fontSize)(?:=\{|:)\s*(\d+(?:\.\d+)?)\s*[,}\s]/g; // fontSize: 11 / fz={11}

export function findSmallFonts(source: string): FontViolation[] {
  const out: FontViolation[] = [];
  source.split('\n').forEach((text, i) => {
    let bad = false;
    for (const re of RULES) {
      for (const m of text.matchAll(new RegExp(re.source, 'g'))) {
        if (toPx(Number(m[1]), m[2]) < MIN_FONT_PX) bad = true;
      }
    }
    // Mantine Badge xs/sm and Button compact-* render at 9-11 px unless fz is set.
    if (/<(?:Badge|Button)\b/.test(text) && /\bsize="(?:xs|sm|compact-xs|compact-sm)"/.test(text) && !/\bfz=/.test(text)) bad = true;
    for (const m of text.matchAll(new RegExp(BARE_NUMBER.source, 'g'))) {
      if (Number(m[1]) < MIN_FONT_PX && !/TEXT_LIMITS|draft\.|item\./.test(text)) bad = true;
    }
    if (bad) out.push({ line: i + 1, text: text.trim() });
  });
  return out;
}
