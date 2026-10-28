/** Client-side helpers mirroring the hub's template engine (Core/Templates/TemplateEngine.cs) for instant feedback. */

export const FILTERS: { name: string; arg?: string; example: string; text: string }[] = [
  { name: 'upper', example: '{{CustomerName | upper}}', text: 'UPPER CASE' },
  { name: 'lower', example: '{{Email | lower}}', text: 'lower case' },
  { name: 'title', example: '{{CustomerName | title}}', text: 'Title Case' },
  { name: 'trim', example: '{{Comment | trim}}', text: 'Remove surrounding spaces' },
  { name: 'default', arg: 'text', example: '{{Nickname | default:Customer}}', text: 'Fallback when empty' },
  { name: 'truncate', arg: 'n', example: '{{Comment | truncate:80}}', text: 'Cut to n characters (…)' },
  { name: 'number', arg: 'decimals', example: '{{Quantity | number:2}}', text: 'Grouped number' },
  { name: 'currency', arg: 'code', example: '{{InvoiceAmount | currency:INR}}', text: 'Currency amount' },
  { name: 'date', arg: 'format', example: '{{DueDate | date:dd-MMM-yyyy}}', text: '.NET date format' },
  { name: 'url', example: '{{PaymentLink | url}}', text: 'Safe http(s) link only' },
];

export const BLOCKS: { example: string; text: string }[] = [
  { example: '{{#if Discount}}Save {{Discount}}{{else}}No offer{{/if}}', text: 'Conditional section' },
  { example: '{{#unless Paid}}Payment pending{{/unless}}', text: 'Inverse conditional' },
  { example: '{{#each Items}}{{@index}}. {{this.Name}}{{/each}}', text: 'Repeat for each list item' },
  { example: '{{! internal note }}', text: 'Comment (not rendered)' },
];

/** Variables the hub always supplies at render time (TemplateRendering.Build); never declared per template. */
export const BUILT_IN_VARIABLES = ['CompanyName', 'TenantName', 'RecipientName', 'UnsubscribeUrl', 'CurrentDate'];
const BUILT_IN = new Set(BUILT_IN_VARIABLES.map(v => v.toLowerCase()));
export function isBuiltIn(name: string) { return BUILT_IN.has(name.toLowerCase()); }

const TAG = /\{\{\s*([^}]*?)\s*\}\}/g;
const PATH = /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/;

/**
 * Root variable names referenced by a template, in first-use order. Like the server, names inside #each blocks are
 * item-relative and are not reported; comments and block closers are ignored.
 */
export function extractVariables(...templates: (string | null | undefined)[]): string[] {
  const seen = new Map<string, string>();
  for (const t of templates) {
    if (!t) continue;
    let eachDepth = 0;
    for (const m of t.matchAll(TAG)) {
      const inner = m[1].trim();
      if (!inner || inner.startsWith('!')) continue;
      if (inner.startsWith('/')) { if (inner.slice(1).trim() === 'each') eachDepth = Math.max(0, eachDepth - 1); continue; }
      if (inner === 'else') continue;
      let path: string;
      if (inner.startsWith('#')) {
        const [kw, ...rest] = inner.slice(1).trim().split(/\s+/);
        path = rest.join(' ').split('|')[0].trim();
        if (kw === 'each') { add(path); eachDepth++; continue; }
      } else path = inner.split('|')[0].trim();
      add(path);
    }
    function add(p: string) {
      if (eachDepth > 0 || !p || p === '@index' || p.startsWith('this') || !PATH.test(p)) return;
      const root = p.split('.')[0];
      if (!seen.has(root.toLowerCase())) seen.set(root.toLowerCase(), root);
    }
  }
  return [...seen.values()];
}

// GSM 03.38 basic set and extension table (extension characters take two septets).
const GSM_BASIC = '@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !"#¤%&\'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà';
const GSM_EXT = '^{}\\[~]|€\f';

export interface SmsInfo { encoding: 'GSM-7' | 'UCS-2'; characters: number; segments: number; perSegment: number; }

/** Estimates SMS encoding and segment count (GSM-7: 160/153 septets; UCS-2: 70/67 UTF-16 units). */
export function smsSegments(text: string): SmsInfo {
  let septets = 0, gsm = true;
  for (const ch of text) {
    if (GSM_BASIC.includes(ch)) septets += 1;
    else if (GSM_EXT.includes(ch)) septets += 2;
    else { gsm = false; break; }
  }
  if (gsm) {
    const segments = septets === 0 ? 0 : septets <= 160 ? 1 : Math.ceil(septets / 153);
    return { encoding: 'GSM-7', characters: septets, segments, perSegment: segments > 1 ? 153 : 160 };
  }
  const units = text.length;
  const segments = units === 0 ? 0 : units <= 70 ? 1 : Math.ceil(units / 67);
  return { encoding: 'UCS-2', characters: units, segments, perSegment: segments > 1 ? 67 : 70 };
}

/** Sample variables object built from declared sample values (numbers stay numbers). */
export function sampleData(vars: { name: string; sampleValue?: string | null }[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const v of vars) {
    const s = v.sampleValue;
    if (s === null || s === undefined) continue;
    out[v.name] = /^-?\d+(\.\d+)?$/.test(s) ? Number(s) : s;
  }
  return out;
}
