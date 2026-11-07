#!/usr/bin/env node
// Builds user-manual.html (self-contained) and user-manual.pdf from src/.
// Chrome does the PDF layout. Contents, cross-reference and index page numbers come from a first pass:
// render, read each heading's page from the PDF's named destinations, inject, render again.
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';

const HERE = dirname(fileURLToPath(import.meta.url));
const SRC = join(HERE, 'src');
const IMAGES = join(HERE, 'images');
const TITLE = 'Wings Notification Hub User Manual';
const AUTHOR = 'Wings Notification Hub';
const SUBJECT = 'Administration console guide for administrators, designers, approvers, campaign managers, operators and auditors';

// ─── Icons (Material Icons paths, Apache 2.0) ───────────────────────────────────────────────────────
const ICON = {
  check_circle: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z',
  schedule: 'M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z',
  error: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z',
  info: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z',
  event: 'M17 12h-5v5h5v-5zM16 1v2H8V1H6v2H5c-1.11 0-1.99.9-1.99 2L3 19c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2h-1V1h-2zm3 18H5V8h14v11z',
  remove: 'M7 11v2h10v-2H7zm5-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z',
  tip: 'M9 21c0 .55.45 1 1 1h4c.55 0 1-.45 1-1v-1H9v1zm3-19C8.14 2 5 5.14 5 9c0 2.38 1.19 4.47 3 5.74V17c0 .55.45 1 1 1h6c.55 0 1-.45 1-1v-2.26c1.81-1.27 3-3.36 3-5.74 0-3.86-3.14-7-7-7z',
  warn: 'M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z',
  yes: 'M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z',
  bolt: 'M7 2v11h3v9l7-12h-4l4-8z',
  lock: 'M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zM9 6c0-1.66 1.34-3 3-3s3 1.34 3 3v2H9V6z',
  lock_open: 'M12 17c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm6-9h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6h1.9c0-1.71 1.39-3.1 3.1-3.1s3.1 1.39 3.1 3.1v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2z',
  power: 'M16.01 7 16 3h-2v4h-4V3H8v4h-.01C7 6.99 6 7.99 6 8.99v5.49L9.5 18v3h5v-3l3.5-3.51v-5.5c0-1-1-2-1.99-1.99z',
  power_off: 'M18 14.49V9c0-1-1.01-2.01-2-2V3h-2v4h-4V3H8v2.48l9.51 9.5.49-.49zm-1.76 1.77L7.2 7.2l-.01.01L3.98 4 2.71 5.25l3.36 3.36c-.04.13-.07.26-.07.39v5.48L9.5 18v3h5v-3l.48-.48L19.45 22l1.26-1.28-4.47-4.46z',
};
const svg = (name, cls = '') => `<svg class="ic ${cls}" viewBox="0 0 24 24" aria-hidden="true"><path d="${ICON[name]}"/></svg>`;

// Same tone and icon as the console's own status chips (web/src/app/shared/ui.ts).
const TONE_ICON = { good: 'check_circle', warn: 'schedule', bad: 'error', info: 'info', maint: 'event', off: 'remove' };
const STATUS = {
  Delivered: 'good', Read: 'good', Sent: 'good', 'Provider accepted': 'good', Submitted: 'info', Processing: 'info', Queued: 'warn', Scheduled: 'maint', 'Retry pending': 'warn',
  Failed: 'bad', Expired: 'bad', Unknown: 'bad', Suppressed: 'off', Cancelled: 'off', Draft: 'off', Accepted: 'info', Completed: 'good', 'Pending approval': 'warn', Rejected: 'bad',
  'In review': 'warn', Approved: 'info', Active: 'good', Retired: 'off', Running: 'info', Paused: 'warn', Pending: 'warn', Healthy: 'good', Degraded: 'warn', Unhealthy: 'bad',
  'Circuit open': 'bad', Suspended: 'bad', Routed: 'good', 'No subscriber': 'warn', Granted: 'good', Withdrawn: 'off', Enabled: 'good', Disabled: 'off', Revoked: 'bad',
  Skipped: 'off', Warning: 'warn', Error: 'bad', Critical: 'bad', Info: 'info', Closed: 'good', Matched: 'good', 'No match': 'off',
};
const chip = (text, tone, icon) => { const t = tone ?? STATUS[text] ?? 'off'; return `<span class="st" data-t="${t}">${svg(icon ?? TONE_ICON[t])}${text}</span>`; };

// ─── Small helpers ──────────────────────────────────────────────────────────────────────────────────
const attrs = s => Object.fromEntries([...s.matchAll(/([\w-]+)="([^"]*)"/g)].map(m => [m[1], m[2]]));
const strip = s => s.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();

function jpegSize(path) {
  const b = readFileSync(path);
  for (let i = 2; i < b.length;) {
    if (b[i] !== 0xff) { i++; continue; }
    const m = b[i + 1];
    if (m >= 0xc0 && m <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(m)) return { h: b.readUInt16BE(i + 5), w: b.readUInt16BE(i + 7) };
    i += 2 + b.readUInt16BE(i + 2);
  }
  throw new Error('No JPEG size in ' + path);
}

// ─── Source → expanded body, heading list, figures, index ───────────────────────────────────────────
function expand() {
  const parts = readdirSync(join(SRC, 'parts')).filter(f => f.endsWith('.html')).sort();
  let html = parts.map(f => readFileSync(join(SRC, 'parts', f), 'utf8')).join('\n');

  // Headings: numbering, table of contents, index terms.
  const toc = [], index = new Map(), ids = new Set();
  let ch = 0, sec = 0, partNo = 0, appx = 0, kicker = '';
  html = html.replace(/<h([1-4])\s+([^>]*)>([\s\S]*?)<\/h\1>/g, (m, lv, a, text) => {
    const at = attrs(a), level = +lv, id = at.id;
    if (!id) { if (level < 4) throw new Error('Heading without id: ' + text); return m; }
    if (ids.has(id)) throw new Error('Duplicate id ' + id);
    ids.add(id);
    let num = '', label = '';
    if (level === 1) { if ('part' in at) { partNo = +at.part; num = `Part ${partNo}`; } else { num = ''; partNo = id === 'appendices' ? 5 : partNo; } sec = 0; }
    if (level === 2) { if ('appendix' in at) { appx++; num = String.fromCharCode(64 + appx); label = `Appendix ${num}`; } else { ch++; num = String(ch); label = `Chapter ${num}`; } sec = 0; kicker = num; }
    if (level === 3) { sec++; num = `${kicker}.${sec}`; label = num; }
    if (level < 4) toc.push({ level, id, num, label, title: strip(text), part: partNo });
    for (const term of (at['data-ix'] ?? '').split('|').map(s => s.trim()).filter(Boolean)) {
      if (!index.has(term)) index.set(term, []);
      index.get(term).push({ id, title: strip(text) });
    }
    const cls = at.class ? ` class="${at.class}"` : '';
    if (level === 2) return `<h2 id="${id}"${cls}><span class="kick">${label}</span>${text}</h2>`;
    if (level === 3) return `<h3 id="${id}"${cls}><span class="num">${num}</span>${text}</h3>`;
    return `<h${level} id="${id}"${cls}>${text}</h${level}>`;
  });

  // Figures, numbered in document order.
  let figNo = 0; const figures = [];
  html = html.replace(/<fig\s+([\s\S]*?)\/>/g, (m, a) => {
    const at = attrs(a); figNo++;
    const path = join(IMAGES, at.src);
    if (!existsSync(path)) throw new Error('Missing image ' + at.src);
    const { w, h } = jpegSize(path);
    figures.push(at.src);
    const pins = (at.pins ?? '').trim().split(/\s+/).filter(Boolean).map(p => { const [n, xy] = p.split('@'); const [x, y] = xy.split(','); return `<span class="pin" style="left:${x}%;top:${y}%">${n}</span>`; }).join('');
    const style = at.w ? ` style="--w:${at.w}%"` : '';
    return `<figure${at.id ? ` id="${at.id}"` : ''}${style}${at.phone ? ' class="phone"' : ''}><div class="frame"><img data-img="${at.src}" width="${w}" height="${h}" alt="${at.alt ?? strip(at.cap)}" decoding="async">${pins}</div><figcaption><b>Figure ${figNo}:</b> ${at.cap}</figcaption></figure>`;
  });

  // Status chips, callouts, yes/no marks, cross-references.
  html = html.replace(/<st(?:\s+([^>]*))?>([\s\S]*?)<\/st>/g, (m, a, text) => { const at = attrs(a ?? ''); return chip(text, at.tone, at.icon); });
  const LABEL = { tip: ['Tip', 'tip'], note: ['Note', 'info'], warn: ['Warning', 'warn'] };
  html = html.replace(/<aside class="(tip|note|warn)">/g, (m, k) => `<aside class="callout ${k}">${svg(LABEL[k][1], 'co')}<b class="co-l">${LABEL[k][0]}</b>`);
  html = html.replace(/<y\/>/g, `<span class="yn y">${svg('yes')}Yes</span>`).replace(/<n\/>/g, '<span class="yn n">No</span>');
  html = html.replace(/<ic name="(\w+)"\/>/g, (m, n) => svg(n));
  const byId = new Map(toc.map(t => [t.id, t]));
  html = html.replace(/<x to="([\w-]+)"\/>/g, (m, id) => {
    const t = byId.get(id);
    if (!t) throw new Error('Bad cross-reference ' + id);
    const name = t.level === 2 ? (t.label.startsWith('Appendix') ? t.label : `Chapter ${t.num}`) : `section ${t.num}`;
    return `<a class="xref" href="#${id}">${name}<span class="xp"> on page <span class="pg" data-to="${id}">000</span></span></a>`;
  });
  toc.push({ level: 1, id: 'index', num: '', label: '', title: 'Index', part: 5 });
  return { html, toc, index, figures };
}

// ─── Page furniture ─────────────────────────────────────────────────────────────────────────────────
const BRAND = readFileSync(join(HERE, 'src', 'brand.svg'), 'utf8');

function cover() {
  return `<section class="cover" aria-label="Cover">
    <div class="cover-glow"></div>
    <div class="cover-brand">${BRAND}<span>Administration console</span></div>
    <div class="cover-main">
      <div class="cover-title">Wings Notification Hub</div>
      <div class="cover-kick">User manual</div>
      <p class="cover-sub">For administrators, template designers and approvers, campaign managers, operators and auditors</p>
    </div>
    <ul class="cover-ch" aria-hidden="true"><li>Email</li><li>SMS</li><li>WhatsApp</li><li>In-app</li><li>Push</li><li>Web push</li><li>Webhooks</li></ul>
    <div class="cover-foot">Version 1.0 · November 2026</div>
  </section>`;
}

function tocHtml(toc, withPages) {
  const pg = id => withPages ? `<span class="pg" data-to="${id}">000</span>` : '';
  const rows = toc.filter(t => t.id !== 'contents').map(t => {
    if (t.level === 1) return `<li class="t1"><a href="#${t.id}">${t.num ? `<span class="n">${t.num}</span>` : ''}<span class="t">${t.title}</span></a>${pg(t.id)}</li>`;
    if (t.level === 2) return `<li class="t2"><a href="#${t.id}"><span class="n">${t.num}</span><span class="t">${t.title}</span></a>${pg(t.id)}</li>`;
    return `<li class="t3"><a href="#${t.id}"><span class="n">${t.num}</span><span class="t">${t.title}</span></a>${pg(t.id)}</li>`;
  }).join('');
  return `<section class="toc-page" id="contents-sec"><h1 id="contents" class="front-h">Contents</h1><ol class="toc">${rows}</ol></section>`;
}

function indexHtml(index, toc, withPages) {
  const terms = [...index.keys()].sort((a, b) => a.localeCompare(b, 'en', { sensitivity: 'base' }));
  let letter = '', out = '';
  for (const term of terms) {
    const L = term[0].toUpperCase();
    if (L !== letter) { if (letter) out += '</ul></div>'; letter = L; out += `<div class="ix-group"><div class="ix-l">${L}</div><ul>`; }
    const seen = new Set();
    const refs = index.get(term).filter(r => !seen.has(r.id) && seen.add(r.id)).map(r => withPages ? `<a href="#${r.id}"><span class="pg" data-to="${r.id}">000</span></a>` : `<a href="#${r.id}">${r.title}</a>`).join(', ');
    out += `<li><span class="ix-t">${term}</span> ${refs}</li>`;
  }
  return `<section class="index" data-sec="index"><h1 id="index" class="front-h">Index</h1><div class="ix-cols">${out}</ul></div></div></section>`;
}

const fontFace = () => {
  const f = n => `data:font/woff2;base64,${readFileSync(join(SRC, 'fonts', n)).toString('base64')}`;
  return `@font-face{font-family:'IBM Plex Sans';font-style:normal;font-display:swap;font-weight:100 700;src:url(${f('ibm-plex-sans-latin-ext-wght-normal.woff2')}) format('woff2');unicode-range:U+0100-02BA,U+02BD-02C5,U+02C7-02CC,U+02CE-02D7,U+02DD-02FF,U+0304,U+0308,U+0329,U+1D00-1DBF,U+1E00-1E9F,U+1EF2-1EFF,U+2020,U+20A0-20AB,U+20AD-20C0,U+2113,U+2C60-2C7F,U+A720-A7FF}
@font-face{font-family:'IBM Plex Sans';font-style:normal;font-display:swap;font-weight:100 700;src:url(${f('ibm-plex-sans-latin-wght-normal.woff2')}) format('woff2');unicode-range:U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD}`;
};

// Fills the chapter lists on the part openers (PDF and HTML).
function partLists(html, toc) {
  return html.replace(/<ul class="part-list" data-part="(\d)"><\/ul>/g, (m, p) => `<ul class="part-list">${toc.filter(t => t.level === 2 && t.part === +p).map(t => `<li><a href="#${t.id}"><span>${t.num}</span>${t.title}</a><span class="pg" data-to="${t.id}">000</span></li>`).join('')}</ul>`);
}

function pdfDocument({ html, toc, index }, pages) {
  const body = partLists(html, toc).replace(/<img data-img="([^"]+)"/g, (m, f) => `<img src="${pathToFileURL(join(IMAGES, f)).href}"`);
  const front = body.indexOf('<!--contents-->');
  const page = `<!DOCTYPE html><html lang="en" class="pdf"><head><meta charset="utf-8"><title>${TITLE}</title><style>${fontFace()}\n${readFileSync(join(SRC, 'manual.css'), 'utf8')}</style></head><body>
${cover()}${body.slice(0, front)}${tocHtml(toc, true)}${body.slice(front)}${indexHtml(index, toc, true)}</body></html>`;
  return page.replace(/<span class="pg" data-to="([\w-]+)">000<\/span>/g, (m, id) => `<span class="pg">${pages?.get(id) ?? '000'}</span>`);
}

function htmlDocument({ html, toc, index, figures }) {
  const uri = f => `data:image/jpeg;base64,${readFileSync(join(IMAGES, f)).toString('base64')}`;
  const cache = new Map(figures.map(f => [f, uri(f)]));
  const body = partLists(html, toc).replace(/<img data-img="([^"]+)"/g, (m, f) => `<img loading="lazy" src="${cache.get(f)}"`).replace(/<span class="xp">[\s\S]*?<\/span><\/span>/g, '').replace(/<span class="pg" data-to="[\w-]+">000<\/span>/g, '');
  const rail = toc.filter(t => t.level < 3 && t.id !== 'contents').map(t => t.level === 1
    ? `<li class="r1"><a href="#${t.id}">${t.num ? `<span>${t.num}</span>` : ''}${t.title}</a></li>`
    : `<li class="r2" data-ch="${t.id}"><a href="#${t.id}"><span>${t.num}</span>${t.title}</a></li>`).join('');
  const sub = {}; let cur = null;
  for (const t of toc) { if (t.level === 2) { cur = t.id; sub[cur] = []; } else if (t.level === 3 && cur) sub[cur].push({ id: t.id, n: t.num, t: t.title }); }
  const front = body.indexOf('<!--contents-->');
  return `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${TITLE}</title><meta name="description" content="${SUBJECT}"><meta name="author" content="${AUTHOR}">
<link rel="icon" href="data:image/svg+xml;base64,${Buffer.from(BRAND).toString('base64')}">
<style>${fontFace()}\n${readFileSync(join(SRC, 'manual.css'), 'utf8')}</style></head>
<body>
<header id="topbar"><button id="menu" aria-label="Open contents" aria-expanded="false" aria-controls="rail"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 18h18v-2H3v2zm0-5h18v-2H3v2zm0-7v2h18V6H3z"/></svg></button><span class="tb-brand">${BRAND}<span><b>Wings</b> Notification Hub</span></span></header>
<aside id="rail" aria-label="Contents">
  <a class="brand" href="#top">${BRAND}<span><b>Wings</b> Notification Hub<small>User manual</small></span></a>
  <div class="search"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z"/></svg><input id="q" type="search" placeholder="Search the manual" aria-label="Search the manual" autocomplete="off"></div>
  <div id="results" hidden></div>
  <nav id="toc"><ol>${rail}</ol></nav>
</aside>
<div id="scrim" hidden></div>
<main id="top">${cover()}${body.slice(0, front)}${tocHtml(toc, false)}${body.slice(front)}${indexHtml(index, toc, false)}</main>
<script>const SUB=${JSON.stringify(sub)};
${readFileSync(join(SRC, 'rail.js'), 'utf8')}</script>
</body></html>`;
}

// ─── PDF internals: page of each named destination, and the Info dictionary ─────────────────────────
function readPdf(buf) {
  const s = buf.toString('latin1');
  const sx = s.lastIndexOf('startxref');
  let at = parseInt(s.slice(sx + 9, sx + 30));
  const offsets = new Map();
  const lines = s.slice(at).split('\n'); let i = 1;                      // Skia writes one classic xref table
  while (/^\d+ \d+$/.test(lines[i])) { let [start, n] = lines[i++].split(' ').map(Number); for (let k = 0; k < n; k++, i++) { const [off, , f] = lines[i].trim().split(' '); if (f === 'n') offsets.set(start + k, +off); } }
  const obj = n => { const o = offsets.get(n); return s.slice(o, s.indexOf('endobj', o)); };
  const trailer = s.slice(s.lastIndexOf('trailer'), sx);
  return { s, obj, trailer, sx, prev: at };
}

function pageMap(buf) {
  const { obj, trailer } = readPdf(buf);
  const root = obj(+trailer.match(/\/Root (\d+) 0 R/)[1]);
  const order = new Map();
  const walk = n => { const o = obj(n); if (/\/Type \/Pages/.test(o)) for (const k of o.match(/\/Kids \[([^\]]*)\]/)[1].matchAll(/(\d+) 0 R/g)) walk(+k[1]); else order.set(n, order.size + 1); };
  walk(+root.match(/\/Pages (\d+) 0 R/)[1]);
  const pages = new Map();
  for (const m of obj(+root.match(/\/Dests (\d+) 0 R/)[1]).matchAll(/\/([^\s\[\]\/<>]+)\s*\[(\d+) 0 R/g)) {
    pages.set(m[1].replace(/#([0-9a-fA-F]{2})/g, (x, h) => String.fromCharCode(parseInt(h, 16))), order.get(+m[2]));
  }
  return { pages, count: order.size };
}

// Chrome writes only Title, so metadata is added as a standard incremental update: no PDF library needed.
function withMetadata(buf) {
  const { obj, trailer, prev } = readPdf(buf);
  const size = trailer.match(/\/Size (\d+)/)[1], root = trailer.match(/\/Root (\d+) 0 R/)[1], info = +trailer.match(/\/Info (\d+) 0 R/)[1];
  const old = obj(info);
  const keep = k => old.match(new RegExp(`/${k} (\\([^)]*\\))`))?.[1] ?? '()';
  const dict = `${info} 0 obj\n<</Title (${TITLE})\n/Author (${AUTHOR})\n/Subject (${SUBJECT})\n/Keywords (Wings, notifications, user manual)\n/Creator (docs/manual/build.mjs)\n/Producer ${keep('Producer')}\n/CreationDate ${keep('CreationDate')}\n/ModDate ${keep('ModDate')}>>\nendobj\n`;
  const off = buf.length + 1;
  const head = '\n' + dict;
  const xrefAt = buf.length + head.length;
  const tail = `xref\n${info} 1\n${String(off).padStart(10, '0')} 00000 n \ntrailer\n<</Size ${size}\n/Root ${root} 0 R\n/Info ${info} 0 R\n/Prev ${prev}>>\nstartxref\n${xrefAt}\n%%EOF\n`;
  return Buffer.concat([buf, Buffer.from(head + tail, 'latin1')]);
}

// ─── Main ───────────────────────────────────────────────────────────────────────────────────────────
const doc = expand();
const missing = readdirSync(IMAGES).filter(f => f.endsWith('.jpg') && !doc.figures.includes(f));
if (missing.length) console.log(`Unused images: ${missing.join(', ')}`);

writeFileSync(join(HERE, 'user-manual.html'), htmlDocument(doc));
console.log(`user-manual.html  ${(statSync(join(HERE, 'user-manual.html')).size / 1048576).toFixed(1)} MB`);

const launch = process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : { channel: 'chrome' };
const browser = await chromium.launch(launch);
const tmp = mkdtempSync(join(tmpdir(), 'wnh-manual-'));
try {
  const page = await browser.newPage();
  const render = async pages => {
    const file = join(tmp, 'manual.html');
    writeFileSync(file, pdfDocument(doc, pages));
    await page.goto(pathToFileURL(file).href, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts.ready);
    await page.waitForFunction(() => [...document.images].every(i => i.complete));
    return page.pdf({ printBackground: true, preferCSSPageSize: true, outline: true, tagged: true });
  };
  let pdf = await render(null), map = pageMap(pdf);
  for (let pass = 2; pass <= 4; pass++) {
    pdf = await render(map.pages);
    const next = pageMap(pdf);
    const same = next.count === map.count && [...next.pages].every(([k, v]) => map.pages.get(k) === v);
    map = next;
    if (same) break;
    if (pass === 4) throw new Error('Page numbers did not settle');
  }
  const absent = doc.toc.filter(t => !map.pages.has(t.id)).map(t => t.id);
  if (absent.length) throw new Error('Headings without a PDF destination: ' + absent.join(', '));
  writeFileSync(join(HERE, 'user-manual.pdf'), withMetadata(pdf));
  writeFileSync(join(tmp, 'pages.json'), JSON.stringify([...map.pages]));
  console.log(`user-manual.pdf   ${map.count} pages, ${(statSync(join(HERE, 'user-manual.pdf')).size / 1048576).toFixed(1)} MB, ${doc.figures.length} figures`);
  if (process.env.PAGE_MAP) writeFileSync(process.env.PAGE_MAP, JSON.stringify({ toc: doc.toc.map(t => ({ ...t, page: map.pages.get(t.id) })) }, null, 1));
} finally {
  await browser.close();
  rmSync(tmp, { recursive: true, force: true });
}
