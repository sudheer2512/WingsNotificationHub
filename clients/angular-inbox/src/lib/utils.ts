/** Framework-free helpers (deep links, theming, relative time). No imports so they can be unit-tested with plain Node. */

export type DeepLinkTarget =
  | { kind: 'internal'; path: string }
  | { kind: 'external'; href: string }
  | { kind: 'blocked'; reason: string };

/**
 * Classifies a deep link from a notification. Relative paths ("/sales/orders/42") and same-origin URLs become
 * router navigations; other http(s) URLs open in a new tab; everything else (javascript:, data:, custom schemes,
 * protocol-relative "//host") is blocked.
 */
export function resolveDeepLink(url: string | null | undefined, origin?: string): DeepLinkTarget {
  const u = (url ?? '').trim();
  if (!u || u === '#') return { kind: 'blocked', reason: 'empty' };
  if (u.startsWith('/') && !u.startsWith('//') && !u.includes('\\')) return { kind: 'internal', path: u };
  let parsed: URL;
  try {
    parsed = new URL(u);
  } catch {
    return { kind: 'blocked', reason: 'invalid' };
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return { kind: 'blocked', reason: `scheme ${parsed.protocol}` };
  if (origin && parsed.origin === origin) return { kind: 'internal', path: `${parsed.pathname}${parsed.search}${parsed.hash}` };
  return { kind: 'external', href: parsed.href };
}

/** Only http(s) image URLs are rendered. */
export function safeImageUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    const p = new URL(url);
    return p.protocol === 'https:' || p.protocol === 'http:' ? p.href : null;
  } catch {
    return null;
  }
}

const HEX = /^#(?:[0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})$/i;
const FUNC = /^(?:rgb|rgba|hsl|hsla)\(\s*[\d.\s,%/+-]+\)$/i;

/** Accepts #rgb/#rrggbb(aa) and rgb()/hsl() colours from branding; anything else is ignored. */
export function safeColor(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const v = value.trim();
  return HEX.test(v) || FUNC.test(v) ? v : null;
}

/** Black or white, whichever reads better on the given hex colour (WCAG relative luminance). */
export function contrastText(color: string): string {
  if (!HEX.test(color)) return '#ffffff';
  let hex = color.slice(1);
  if (hex.length <= 4) hex = [...hex].map((c) => c + c).join('');
  const [r, g, b] = [0, 2, 4].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return luminance > 0.179 ? '#1f1f1f' : '#ffffff';
}

export interface InboxTheme {
  primary: string;
  onPrimary: string;
  accent: string;
}

export const DEFAULT_PRIMARY = '#1565c0';

/** White-label theme from the hub's branding object ({ primaryColor, accentColor, … }). */
export function inboxTheme(branding: { primaryColor?: unknown; accentColor?: unknown } | null | undefined): InboxTheme {
  const primary = safeColor(branding?.primaryColor) ?? DEFAULT_PRIMARY;
  return { primary, onPrimary: contrastText(primary), accent: safeColor(branding?.accentColor) ?? primary };
}

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 31_536_000], ['month', 2_592_000], ['week', 604_800], ['day', 86_400], ['hour', 3_600], ['minute', 60],
];

/** "just now", "5 minutes ago", "yesterday" … in the given locale. */
export function relativeTime(iso: string | null | undefined, now: number = Date.now(), locale?: string): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const seconds = Math.round((t - now) / 1000);
  const abs = Math.abs(seconds);
  const fmt = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  if (abs < 45) return fmt.format(0, 'second');
  for (const [unit, size] of UNITS) {
    if (abs >= size) return fmt.format(Math.round(seconds / size), unit);
  }
  return fmt.format(Math.round(seconds / 60), 'minute');
}

/** "99+" style badge text; empty when there is nothing unread. */
export function badgeText(count: number, max = 99): string {
  return count <= 0 ? '' : count > max ? `${max}+` : String(count);
}
