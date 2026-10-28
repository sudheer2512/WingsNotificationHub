import { ConditionNode } from '../core/models';

/**
 * Editor model for the nested AND/OR condition builder and its conversion to/from the API's ConditionNode
 * (src/Wings.NotificationHub.Core/Rules/RuleModels.cs): group {op, children}, leaf {field, comparison, value}.
 */
export interface EdLeaf { kind: 'leaf'; id: number; field: string; comparison: string; value: string; }
export interface EdGroup { kind: 'group'; id: number; op: 'and' | 'or'; children: EdNode[]; }
export type EdNode = EdLeaf | EdGroup;

export const COMPARISONS: { value: string; label: string; symbol: string }[] = [
  { value: 'eq', label: 'equals', symbol: '=' }, { value: 'ne', label: 'not equal', symbol: '≠' },
  { value: 'gt', label: 'greater than', symbol: '>' }, { value: 'gte', label: 'greater or equal', symbol: '≥' },
  { value: 'lt', label: 'less than', symbol: '<' }, { value: 'lte', label: 'less or equal', symbol: '≤' },
  { value: 'contains', label: 'contains', symbol: 'contains' }, { value: 'notcontains', label: 'does not contain', symbol: 'not contains' },
  { value: 'startswith', label: 'starts with', symbol: 'starts with' }, { value: 'endswith', label: 'ends with', symbol: 'ends with' },
  { value: 'in', label: 'is one of', symbol: 'in' }, { value: 'notin', label: 'is none of', symbol: 'not in' },
  { value: 'exists', label: 'exists', symbol: 'exists' }, { value: 'notexists', label: 'does not exist', symbol: 'does not exist' },
  { value: 'empty', label: 'is empty', symbol: 'is empty' }, { value: 'notempty', label: 'is not empty', symbol: 'is not empty' },
];
export const NO_VALUE = new Set(['exists', 'notexists', 'empty', 'notempty']);
export const LIST_VALUE = new Set(['in', 'notin']);
const KNOWN = new Set(COMPARISONS.map(c => c.value));
const MAX_DEPTH = 10;

let seq = 0;
const nextId = () => ++seq;

export function newLeaf(field = '', comparison = 'eq', value = ''): EdLeaf { return { kind: 'leaf', id: nextId(), field, comparison, value }; }
export function newGroup(op: 'and' | 'or' = 'and', children: EdNode[] = []): EdGroup { return { kind: 'group', id: nextId(), op, children }; }

function isGroupNode(n: ConditionNode): boolean {
  return n.op !== null && n.op !== undefined && n.op !== '';
}

/** Parses the text typed in the value box into the JSON value the evaluator compares against. */
export function parseValue(text: string, comparison: string): unknown {
  const cmp = (comparison || 'eq').toLowerCase();
  if (NO_VALUE.has(cmp)) return undefined;
  if (LIST_VALUE.has(cmp)) return splitList(text).map(parseScalar);
  return parseScalar(text.trim());
}

function splitList(text: string): string[] {
  // Commas separate items; a quoted item may contain commas.
  const out: string[] = [];
  const re = /\s*("(?:[^"\\]|\\.)*"|[^,]*)\s*(?:,|$)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m[0] === '') break;
    const item = m[1].trim();
    if (item) out.push(item);
    if (re.lastIndex >= text.length) break;
  }
  return out;
}

function parseScalar(s: string): unknown {
  if (/^"(?:[^"\\]|\\.)*"$/.test(s)) { try { return JSON.parse(s); } catch { return s.slice(1, -1); } }
  if (/^-?\d+(\.\d+)?$/.test(s)) return Number(s);
  if (s === 'true') return true;
  if (s === 'false') return false;
  if (s === 'null') return null;
  return s;
}

/** Inverse of {@link parseValue}: strings that would re-parse as numbers/booleans are quoted so their type survives a round trip. */
export function formatValue(value: unknown): string {
  if (value === undefined) return '';
  if (Array.isArray(value)) return value.map(formatScalar).join(', ');
  return formatScalar(value);
}

function formatScalar(v: unknown): string {
  if (v === null) return 'null';
  if (typeof v === 'string') {
    const ambiguous = /^-?\d+(\.\d+)?$/.test(v) || v === 'true' || v === 'false' || v === 'null' || v.includes(',') || /^\s|\s$/.test(v) || /^".*"$/.test(v);
    return ambiguous ? JSON.stringify(v) : v;
  }
  if (typeof v === 'number' || typeof v === 'boolean') return String(v);
  return JSON.stringify(v);
}

/** API tree → editor tree. The root is always a group (a lone leaf is wrapped in an AND group). */
export function fromApi(node: ConditionNode | null | undefined): EdGroup {
  if (!node) return newGroup('and');
  const ed = convertIn(node);
  return ed.kind === 'group' ? ed : newGroup('and', [ed]);
}

function convertIn(n: ConditionNode): EdNode {
  if (isGroupNode(n)) {
    const op = String(n.op).toLowerCase() === 'or' ? 'or' : 'and';
    return newGroup(op, (n.children ?? []).map(convertIn));
  }
  const cmp = (n.comparison ?? 'eq').toLowerCase();
  return newLeaf(n.field ?? '', cmp, formatValue(n.value));
}

/**
 * Editor tree → API tree. Leaves without a field and empty groups are dropped; a group left with a single child is
 * replaced by that child (logically identical). Returns null when nothing remains (= "always matches").
 */
export function toApi(group: EdGroup): ConditionNode | null {
  return convertOut(group);
}

function convertOut(n: EdNode): ConditionNode | null {
  if (n.kind === 'leaf') {
    const field = n.field.trim();
    if (!field) return null;
    const out: ConditionNode = { field, comparison: n.comparison || 'eq' };
    const value = parseValue(n.value, n.comparison);
    if (value !== undefined) out.value = value;
    return out;
  }
  const children = n.children.map(convertOut).filter((c): c is ConditionNode => c !== null);
  if (children.length === 0) return null;
  if (children.length === 1) return children[0];
  return { op: n.op, children };
}

/** Validation mirroring ConditionEvaluator.Validate plus UI-only checks (list comparisons need at least one value). */
export function validateTree(group: EdGroup): string[] {
  const errors: string[] = [];
  const walk = (n: EdNode, depth: number) => {
    if (depth > MAX_DEPTH) { errors.push(`Conditions are nested too deeply (max ${MAX_DEPTH}).`); return; }
    if (n.kind === 'group') { n.children.forEach(c => walk(c, depth + 1)); return; }
    if (!n.field.trim()) { if (n.value.trim()) errors.push('A condition has a value but no field.'); return; }
    if (!KNOWN.has(n.comparison)) errors.push(`Unknown comparison '${n.comparison}'.`);
    if (LIST_VALUE.has(n.comparison) && splitList(n.value).length === 0) errors.push(`"${n.field}" needs at least one value (comma-separated).`);
    if (!NO_VALUE.has(n.comparison) && !LIST_VALUE.has(n.comparison) && n.value.trim() === '' && n.comparison !== 'eq' && n.comparison !== 'ne')
      errors.push(`"${n.field}" needs a value.`);
  };
  walk(group, 0);
  return errors;
}

/** Human-readable summary, e.g. "payload.days ≥ 7 AND (payload.amount > 10000 OR payload.tier = gold)". */
export function describe(node: ConditionNode | null | undefined): string {
  if (!node) return 'Always (no conditions)';
  return describeNode(node, true);
}

function describeNode(n: ConditionNode, root: boolean): string {
  if (isGroupNode(n)) {
    const parts = (n.children ?? []).map(c => describeNode(c, false));
    if (parts.length === 0) return 'Always';
    const s = parts.join(` ${String(n.op).toUpperCase()} `);
    return root || parts.length === 1 ? s : `(${s})`;
  }
  const c = COMPARISONS.find(x => x.value === (n.comparison ?? 'eq').toLowerCase());
  const sym = c?.symbol ?? n.comparison ?? '=';
  return NO_VALUE.has(c?.value ?? '') ? `${n.field} ${sym}` : `${n.field} ${sym} ${Array.isArray(n.value) ? '[' + formatValue(n.value) + ']' : formatValue(n.value)}`;
}

/** Collects dotted paths of a sample JSON payload ("payload.customer.tier") for field suggestions. */
export function payloadPaths(obj: unknown, prefix = 'payload', depth = 0, out: string[] = []): string[] {
  if (depth > 5 || obj === null || typeof obj !== 'object' || Array.isArray(obj)) return out;
  for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
    const p = `${prefix}.${k}`;
    out.push(p);
    if (v && typeof v === 'object' && !Array.isArray(v)) payloadPaths(v, p, depth + 1, out);
  }
  return out;
}
