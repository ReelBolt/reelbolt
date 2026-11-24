import React from 'react';
import { useCurrentFrame } from 'remotion';
import { opacityAt, posAt, scaleAt, cameraAt, sceneProgress, sceneIndexAt } from './model.js';

const FONT = 'DejaVu Sans, Helvetica, Arial, sans-serif';

const Deco: React.FC<{ s: any; W: number; H: number }> = ({ s, W, H }) => {
  const c = s.accent || '#ffffff';
  const o = s.decoOpacity ?? 0.12;
  if (s.deco === 'stripes')
    return <div style={{ position: 'absolute', inset: 0, opacity: o, backgroundImage: `repeating-linear-gradient(45deg, ${c} 0 18px, transparent 18px 54px)` }} />;
  if (s.deco === 'grid')
    return <div style={{ position: 'absolute', inset: 0, opacity: o, backgroundImage: `linear-gradient(${c} 1px, transparent 1px), linear-gradient(90deg, ${c} 1px, transparent 1px)`, backgroundSize: '40px 40px' }} />;
  if (s.deco === 'dots')
    return <div style={{ position: 'absolute', inset: 0, opacity: o, backgroundImage: `radial-gradient(${c} 3px, transparent 4px)`, backgroundSize: '36px 36px' }} />;
  if (s.deco === 'rings')
    return <div style={{ position: 'absolute', left: W * 0.55, top: H * 0.3, width: 520, height: 520, borderRadius: '50%', border: `40px solid ${c}`, opacity: o }} />;
  return null;
};

const ElementView: React.FC<{ el: any; f: number; frames: number; ox: number; oy: number }> = ({ el, f, frames, ox, oy }) => {
  const op = opacityAt(el, f);
  if (op <= 0.001) return null;
  const [px, py] = posAt(el, f, frames);
  const sc = scaleAt(el, f);
  const st: React.CSSProperties = {
    position: 'absolute', left: px - ox, top: py - oy, width: el.w, height: el.h,
    opacity: op, transform: `scale(${sc})`, fontFamily: FONT, boxSizing: 'border-box',
  };
  const label = el.label ? (
    <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: el.textColor || '#fff', fontSize: el.fontSize || 22, fontWeight: 700 }}>{el.label}</div>
  ) : null;
  let body: React.ReactNode = null;
  switch (el.kind) {
    case 'rect':
      body = <div style={{ ...st, background: el.color, borderRadius: el.radius ?? 14, border: el.border }}>{label}</div>; break;
    case 'circle':
    case 'ball':
      body = <div style={{ ...st, background: el.color, borderRadius: '50%', boxShadow: 'inset -8px -8px 0 rgba(0,0,0,0.15)' }}>{label}</div>; break;
    case 'text':
      body = <div style={{ ...st, color: el.color, fontSize: el.fontSize || 48, fontWeight: el.weight || 800, whiteSpace: 'nowrap', lineHeight: 1.1 }}>{el.text}</div>; break;
    case 'typed': {
      const n = Math.max(0, Math.floor((f - el.vis[0].in) / el.cpf) + 1);
      body = <div style={{ ...st, color: el.color, fontSize: el.fontSize || 22, fontFamily: el.mono ? 'DejaVu Sans Mono, monospace' : FONT, whiteSpace: 'pre', lineHeight: 1.2 }}>{el.text.slice(0, n)}</div>;
      break;
    }
    case 'char':
      body = (
        <div style={st}>
          <div style={{ position: 'absolute', left: el.w * 0.2, top: el.h * 0.38, width: el.w * 0.6, height: el.h * 0.62, background: el.color, borderRadius: 12 }} />
          <div style={{ position: 'absolute', left: el.w * 0.15, top: 0, width: el.w * 0.7, height: el.w * 0.7, background: el.color2 || el.color, borderRadius: '50%' }} />
          <div style={{ position: 'absolute', left: el.w * 0.32, top: el.w * 0.22, width: 9, height: 9, background: '#111', borderRadius: '50%' }} />
          <div style={{ position: 'absolute', left: el.w * 0.58, top: el.w * 0.22, width: 9, height: 9, background: '#111', borderRadius: '50%' }} />
        </div>
      ); break;
    case 'cursor': {
      const rip = (el.clicks || []).filter((c: number) => f >= c && f < c + 12);
      body = (
        <>
          {rip.map((c: number) => {
            const r = 4 + (f - c) * 2.6;
            return <div key={c} style={{ position: 'absolute', left: px - ox + 3 - r, top: py - oy + 3 - r, width: 2 * r, height: 2 * r, borderRadius: '50%', border: `3px solid ${el.color2 || '#2b7cff'}`, opacity: 1 - (f - c) / 12 }} />;
          })}
          <svg style={{ ...st, transform: 'none' }} width={el.w} height={el.h} viewBox="0 0 24 24">
            <path d="M3 2 L3 19 L8 14.5 L11.5 22 L14.5 20.6 L11 13.4 L18 13 Z" fill={el.color || '#fff'} stroke="#000" strokeWidth="1.4" />
          </svg>
        </>
      ); break;
    }
    case 'list': {
      const rows: string[] = el.rows;
      body = (
        <div style={{ ...st, height: rows.length * el.rowH }}>
          {rows.map((r, i) => (
            <div key={i} style={{ height: el.rowH, display: 'flex', alignItems: 'center', padding: '0 16px', borderBottom: `1px solid ${el.lineColor || 'rgba(128,128,128,0.35)'}`, color: el.color, fontSize: el.fontSize || 20 }}>{r}</div>
          ))}
        </div>
      ); break;
    }
  }
  if (el.clip) {
    const [cx, cy, cw, ch] = el.clip;
    return (
      <div style={{ position: 'absolute', left: cx - ox, top: cy - oy, width: cw, height: ch, overflow: 'hidden' }}>
        <div style={{ position: 'absolute', left: -(cx - ox), top: -(cy - oy) }}>{body}</div>
      </div>
    );
  }
  return <>{body}</>;
};

const Chrome: React.FC<{ spec: any; f: number }> = ({ spec, f }) => {
  const c = spec.chrome; const W = spec.width;
  const cur = sceneIndexAt(spec, f);
  return (
    <div style={{ position: 'absolute', left: 0, top: 0, width: W, height: spec.area[1], background: c.bar, fontFamily: FONT }}>
      <div style={{ display: 'flex', gap: 8, position: 'absolute', left: 14, top: 12 }}>
        {['#ff5f56', '#ffbd2e', '#27c93f'].map((k) => <div key={k} style={{ width: 12, height: 12, borderRadius: '50%', background: k }} />)}
      </div>
      {spec.scenes.map((s: any, i: number) => (
        <div key={i} style={{ position: 'absolute', left: 100 + i * 170, top: 6, width: 160, height: 26, borderRadius: '8px 8px 0 0', background: i === cur ? c.tabOn : c.tabOff, color: c.text, fontSize: 14, lineHeight: '26px', textAlign: 'center', opacity: i === cur ? 1 : 0.6 }}>{s.tab}</div>
      ))}
      <div style={{ position: 'absolute', left: 90, top: 38, width: W - 180, height: 26, borderRadius: 13, background: c.url, color: c.text, fontSize: 14, lineHeight: '26px', paddingLeft: 14, boxSizing: 'border-box' }}>{spec.scenes[cur].url}</div>
    </div>
  );
};

export const Main: React.FC<any> = (spec) => {
  const f = useCurrentFrame();
  const W = spec.width, H = spec.height;
  const area = spec.area || [0, 0, W, H];
  const [ax, ay, aw, ah] = area;
  const [cx, cy, cs] = cameraAt(spec, f);
  const drift = spec.drift ? 6 * Math.sin(f / 90) : 0;
  const scenes = spec.scenes;
  const els = spec.elements;
  const persistent = els.filter((e: any) => e.scene == null);
  const phase = (s: any) => (s.bgB ? `linear-gradient(${125 + drift}deg, ${s.bg}, ${s.bgB})` : s.bg);

  return (
    <div style={{ position: 'absolute', inset: 0, background: spec.theme.bg, overflow: 'hidden' }}>
      {spec.chrome ? <Chrome spec={spec} f={f} /> : null}
      <div style={{ position: 'absolute', left: ax, top: ay, width: aw, height: ah, overflow: 'hidden' }}>
        <div style={{ position: 'absolute', inset: 0, transformOrigin: '50% 50%', transform: `translate(${cx}px, ${cy}px) scale(${cs})` }}>
          {scenes.map((s: any, i: number) => {
            const p = sceneProgress(s, f);
            const next = scenes[i + 1];
            if (p <= 0) return null;
            if (next && sceneProgress(next, f) >= 1) return null;
            const style: React.CSSProperties = { position: 'absolute', inset: 0, background: phase(s) };
            if (i > 0 && p < 1) {
              if (s.trans === 'fade') style.opacity = p;
              else if (s.trans === 'wipe') style.clipPath = `inset(0 ${(1 - p) * 100}% 0 0)`;
            }
            return (
              <div key={i} style={style}>
                <Deco s={s} W={aw} H={ah} />
                {els.filter((e: any) => e.scene === i).map((e: any) => (
                  <ElementView key={e.id} el={e} f={f} frames={spec.frames} ox={ax} oy={ay} />
                ))}
              </div>
            );
          })}
          {persistent.map((e: any) => (
            <ElementView key={e.id} el={e} f={f} frames={spec.frames} ox={ax} oy={ay} />
          ))}
        </div>
      </div>
    </div>
  );
};
