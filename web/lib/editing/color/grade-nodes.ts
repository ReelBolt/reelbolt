/**
 * Colour depth (pro UI wave 4): the pure helpers behind the Colour page's node list and serial graph —
 * rename, reorder, duplicate and reset a correction, the transient solo stack, and the chain a clip is
 * graded through, in the order the render applies it.
 *
 * Nothing here adds a document field or an op: every write helper returns the WHOLE stack, which the
 * page writes with the one `setProperty grade` (or `setColorGroup`) it already uses, so each action is
 * one undo step. Solo never reaches the document at all: {@link soloStack} feeds the transient grade
 * preview only.
 */

import type { AdjustmentClip, ColorGroup, EditTimeline, GradeNode, GradeStack, MediaClip } from '../types';
import { MAX_GRADE_NODES } from '../engine/ops';
import { colorGroupOf, hasCurves, isIdentityPrimaries } from './grade';

/** The longest name a correction can be given here. The server validator does not limit node names; this keeps a row readable. */
export const NODE_NAME_MAX = 80;

/**
 * A typed name made safe to store: control characters dropped, inner whitespace collapsed, trimmed and cut
 * to {@link NODE_NAME_MAX}. `null` when nothing is left (the caller then keeps the old name).
 */
export function cleanNodeName(raw: string): string | null {
  let out = '';
  for (const ch of raw) {
    const code = ch.codePointAt(0) ?? 0;
    out += code < 0x20 || (code >= 0x7f && code <= 0x9f) ? ' ' : ch;
  }
  const name = out.replace(/\s+/g, ' ').trim();
  if (name.length === 0) return null;
  return Array.from(name).slice(0, NODE_NAME_MAX).join('').trim();
}

const nodesOf = (stack: GradeStack | null | undefined): GradeNode[] => stack?.nodes ?? [];
const withNodes = (stack: GradeStack | null | undefined, nodes: GradeNode[]): GradeStack => ({ enabled: stack?.enabled ?? true, nodes });

/** The stack with one node renamed; `null` when the name is empty, unchanged, or the node is not in the stack. */
export function renameNode(stack: GradeStack | null | undefined, nodeId: string, rawName: string): GradeStack | null {
  const name = cleanNodeName(rawName);
  const nodes = nodesOf(stack);
  const node = nodes.find((n) => n.id === nodeId);
  if (!name || !node || node.name === name) return null;
  return withNodes(stack, nodes.map((n) => (n.id === nodeId ? { ...n, name } : n)));
}

/** The stack with the node at `from` moved to `to` (both clamped); `null` when nothing would move. */
export function reorderNodes(stack: GradeStack | null | undefined, from: number, to: number): GradeStack | null {
  const nodes = nodesOf(stack);
  if (from < 0 || from >= nodes.length) return null;
  const target = Math.min(nodes.length - 1, Math.max(0, to));
  if (target === from) return null;
  const next = nodes.slice();
  const [moved] = next.splice(from, 1);
  next.splice(target, 0, moved);
  return withNodes(stack, next);
}

/** The stack with one node moved up (`-1`) or down (`+1`); `null` at the end of the chain. */
export function moveNode(stack: GradeStack | null | undefined, nodeId: string, delta: -1 | 1): GradeStack | null {
  const index = nodesOf(stack).findIndex((n) => n.id === nodeId);
  return index < 0 ? null : reorderNodes(stack, index, index + delta);
}

/** True while the stack can take one more correction (the validator allows {@link MAX_GRADE_NODES}). */
export const canAddNode = (stack: GradeStack | null | undefined): boolean => nodesOf(stack).length < MAX_GRADE_NODES;

/**
 * The stack with a copy of one node placed right after it, under a new id and the given name. `null` when
 * the node is not in the stack or the stack is full.
 */
export function duplicateNode(stack: GradeStack | null | undefined, nodeId: string, newId: string, name: string): GradeStack | null {
  const nodes = nodesOf(stack);
  const index = nodes.findIndex((n) => n.id === nodeId);
  if (index < 0 || !canAddNode(stack)) return null;
  const copy: GradeNode = { ...structuredClone(nodes[index]), id: newId, name: cleanNodeName(name) ?? nodes[index].name };
  const next = nodes.slice();
  next.splice(index + 1, 0, copy);
  return withNodes(stack, next);
}

/**
 * The stored name of a copy. Names in a timeline are shared data and stay language-neutral: a default name
 * ("Correction 2") becomes the next default name, which every reader sees in their own language; a typed
 * name gets a number ("Skin" becomes "Skin 2", "Skin 2" becomes "Skin 3").
 */
export function duplicateNodeName(name: string | undefined, nodeCount: number): string {
  if (!name || /^Correction \d+$/.test(name)) return `Correction ${nodeCount + 1}`;
  const numbered = /^(.*\S) (\d{1,6})$/.exec(name);
  const suffix = numbered ? ` ${Number(numbered[2]) + 1}` : ' 2';
  const base = numbered ? numbered[1] : name;
  return `${Array.from(base).slice(0, NODE_NAME_MAX - suffix.length).join('').trimEnd()}${suffix}`;
}

/** What a correction carries, for the graph's glyphs and the "nothing to reset" check. */
export interface NodeCarries {
  primaries: boolean;
  curves: boolean;
  lut: boolean;
  qualifier: boolean;
  window: boolean;
}

export function nodeCarries(node: GradeNode): NodeCarries {
  return { primaries: !isIdentityPrimaries(node.primaries), curves: hasCurves(node), lut: !!node.lut, qualifier: !!node.qualifier, window: !!node.window };
}

/** True when the node changes nothing: no primaries, curves, LUT, qualifier or window. */
export function isIdentityNode(node: GradeNode): boolean {
  const c = nodeCarries(node);
  return !c.primaries && !c.curves && !c.lut && !c.qualifier && !c.window;
}

/** The stack with one node back to identity. Its id, name and on/off state are kept; `null` when there is nothing to reset. */
export function resetNode(stack: GradeStack | null | undefined, nodeId: string): GradeStack | null {
  const nodes = nodesOf(stack);
  const node = nodes.find((n) => n.id === nodeId);
  if (!node || isIdentityNode(node)) return null;
  const reset: GradeNode = { id: node.id, ...(node.name !== undefined ? { name: node.name } : {}), enabled: node.enabled ?? true, primaries: {}, curves: null, lut: null };
  return withNodes(stack, nodes.map((n) => (n.id === nodeId ? reset : n)));
}

/**
 * The stack as solo shows it: switched on, with ONLY the soloed node enabled. View state — this is handed to
 * the transient grade preview and is never written. `null` when the node is not in the stack.
 */
export function soloStack(stack: GradeStack | null | undefined, nodeId: string): GradeStack | null {
  const nodes = nodesOf(stack);
  if (!nodes.some((n) => n.id === nodeId)) return null;
  return { enabled: true, nodes: nodes.map((n) => ({ ...n, enabled: n.id === nodeId })) };
}

/** A stack switched off for the solo preview (the other stack of the chain while one node is soloed). */
export const mutedStack = (stack: GradeStack): GradeStack => ({ ...stack, enabled: false });

/** Which stack a node of the chain belongs to. */
export type ChainOwner = { kind: 'group'; id: string; name: string } | { kind: 'clip'; id: string; name: string };

export interface ChainNode {
  /** Unique in the chain (`group:<id>:<node>` / `clip:<id>:<node>`): a group and a clip may reuse a node id. */
  key: string;
  owner: ChainOwner;
  node: GradeNode;
  /** 0-based position inside its own stack. */
  index: number;
  /** True when this node changes the picture: its stack is on and the node is on. */
  enabled: boolean;
  carries: NodeCarries;
}

export interface ChainSegment {
  owner: ChainOwner;
  /** False when the whole stack is bypassed. */
  stackEnabled: boolean;
  nodes: ChainNode[];
}

const segmentOf = (owner: ChainOwner, stack: GradeStack | null | undefined): ChainSegment => {
  const stackEnabled = stack?.enabled !== false;
  return {
    owner,
    stackEnabled,
    nodes: nodesOf(stack).map((node, index) => ({ key: `${owner.kind}:${owner.id}:${node.id}`, owner, node, index, enabled: stackEnabled && node.enabled !== false, carries: nodeCarries(node) })),
  };
};

/**
 * The serial chain the Colour page draws, in application order (the order of `effectiveGrade`): a media
 * clip's colour group stack first, then the clip's own. An adjustment clip has only its own stack. With a
 * colour group as the target and no member clip selected, the chain is that group's stack alone.
 */
export function gradeChain(doc: EditTimeline, clip: MediaClip | AdjustmentClip | null, targetGroup: ColorGroup | null): ChainSegment[] {
  const clipGroup = clip && clip.type === 'mediaClip' ? colorGroupOf(doc, clip.id) : null;
  if (targetGroup && (!clip || clipGroup?.id !== targetGroup.id)) {
    return [segmentOf({ kind: 'group', id: targetGroup.id, name: targetGroup.name }, targetGroup.grade)];
  }
  if (!clip) return [];
  const segments: ChainSegment[] = [];
  if (clipGroup) segments.push(segmentOf({ kind: 'group', id: clipGroup.id, name: clipGroup.name }, clipGroup.grade));
  segments.push(segmentOf({ kind: 'clip', id: clip.id, name: clip.name }, clip.grade));
  return segments;
}

/** Every node of the chain in order (the graph's keyboard order). */
export const chainNodes = (segments: readonly ChainSegment[]): ChainNode[] => segments.flatMap((s) => s.nodes);
