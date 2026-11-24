/**
 * Static scanner for touch targets on the phone surface (T-015 d). Every interactive control is 44 CSS px or more
 * in each direction. CSS modules are checked rule by rule; Mantine controls are checked tag by tag.
 */
export interface SmallTarget {
  line: number;
  text: string;
}

const MIN = 44;
const SIZE_DECL = /(^|[;\s{])(min-)?(width|height)\s*:\s*(\d+(?:\.\d+)?)px/g;

/** Selectors that are not controls: decoration, badges, text lines, drag-handle art. */
export const NON_INTERACTIVE_SELECTORS: readonly RegExp[] = [
  /\.headerSub\b/,
  /\.grabberPill\b/,
  /\.actionBadge\b/,
  /\.wordHandle::after/,
];

/** Rules in a CSS source whose width/height/min-* is under 44 px, ignoring the allow-listed non-controls. */
export function findSmallCssTargets(css: string): SmallTarget[] {
  const out: SmallTarget[] = [];
  const rule = /([^{}]+)\{([^{}]*)\}/g;
  for (let m = rule.exec(css); m; m = rule.exec(css)) {
    const selector = m[1].replace(/\/\*[\s\S]*?\*\//g, '').trim();
    if (!selector || selector.startsWith('@')) continue;
    if (NON_INTERACTIVE_SELECTORS.some((r) => r.test(selector))) continue;
    const body = m[2];
    SIZE_DECL.lastIndex = 0;
    for (let d = SIZE_DECL.exec(body); d; d = SIZE_DECL.exec(body)) {
      const px = Number(d[4]);
      // 1px rules are hairlines; real controls are never that thin.
      if (px > 1 && px < MIN) {
        const offset = m.index + m[0].indexOf(body) + d.index;
        out.push({ line: css.slice(0, offset).split('\n').length, text: `${selector} { ${d[2] ?? ''}${d[3]}: ${px}px }` });
      }
    }
  }
  return out;
}

const TAG_START = /<(ActionIcon|Button|UnstyledButton|SegmentedControl|Switch|Checkbox|NumberInput|TextInput|Select)\b/g;

/** The attribute text of a JSX opening tag, brace aware (an `=>` or `<Icon />` inside an attribute does not end it). */
function attributesFrom(tsx: string, start: number): string {
  let depth = 0;
  for (let i = start; i < tsx.length; i += 1) {
    const c = tsx[i];
    if (c === '{') depth += 1;
    else if (c === '}') depth -= 1;
    else if (c === '>' && depth === 0 && tsx[i - 1] !== '=') return tsx.slice(start, i);
  }
  return tsx.slice(start);
}

/** Mantine controls sized below 44 px with no 44 px floor on the same tag. */
export function findSmallMantineTargets(tsx: string): SmallTarget[] {
  const out: SmallTarget[] = [];
  const heightVar = /\bconst h\b[^;\n]*\b44\b/.test(tsx);
  for (let m = TAG_START.exec(tsx); m; m = TAG_START.exec(tsx)) {
    const attrs = attributesFrom(tsx, m.index + m[0].length);
    const floor = /\b(h|mih|miw|w)=\{(\d+)\}/g;
    let explicit = false;
    let tooSmall = false;
    for (let f = floor.exec(attrs); f; f = floor.exec(attrs)) {
      if (Number(f[2]) >= MIN) explicit = true;
      else tooSmall = true;
    }
    if (heightVar && /\bh=\{h\}/.test(attrs)) explicit = true;
    if (/\b(h|mih)=\{[^}]*\b44\b/.test(attrs) || /style=\{[A-Z_]*HEIGHT\b/.test(attrs)) explicit = true;
    const inlineFloor = /(minHeight|height)\s*:\s*(\d+)/.exec(attrs);
    if (inlineFloor) {
      if (Number(inlineFloor[2]) >= MIN) explicit = true;
      else tooSmall = true;
    }
    const sizeNum = /\bsize=\{(\d+)\}/.exec(attrs);
    if (m[1] === 'ActionIcon' && sizeNum && Number(sizeNum[1]) < MIN && !explicit) tooSmall = true;
    const smallSize = /\bsize="(xs|sm|compact-[a-z]+)"/.test(attrs);
    if (tooSmall || (smallSize && !explicit && !/\bphone\b|coarse/.test(attrs))) {
      out.push({ line: tsx.slice(0, m.index).split('\n').length, text: `<${m[1]}${attrs.replace(/\s+/g, ' ').slice(0, 90)}` });
    }
  }
  return out;
}
