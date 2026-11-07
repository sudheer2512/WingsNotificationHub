// Side rail for user-manual.html: current-section highlight, search, phone menu. No dependencies.
(() => {
  const rail = document.getElementById('rail'), scrim = document.getElementById('scrim'), menu = document.getElementById('menu');
  const q = document.getElementById('q'), results = document.getElementById('results'), tocNav = document.getElementById('toc');
  const heads = [...document.querySelectorAll('main h1[id], main h2[id], main h3[id]')].filter(h => !h.closest('.cover') && h.id !== 'contents' && h.id !== 'index');
  const links = new Map([...tocNav.querySelectorAll('a')].map(a => [a.getAttribute('href').slice(1), a]));

  // Chapter sub-lists appear under the current chapter.
  for (const li of tocNav.querySelectorAll('li.r2')) {
    const items = SUB[li.dataset.ch] ?? [];
    if (!items.length) continue;
    const ol = document.createElement('ol');
    ol.className = 'r3';
    for (const s of items) {
      const c = document.createElement('li'), a = document.createElement('a');
      a.href = '#' + s.id; a.textContent = s.t; c.append(a); ol.append(c); links.set(s.id, a);
    }
    li.append(ol);
  }

  function setCurrent(id) {
    tocNav.querySelectorAll('.on,.open').forEach(e => e.classList.remove('on', 'open'));
    const a = links.get(id);
    if (!a) return;
    a.classList.add('on');
    a.setAttribute('aria-current', 'true');
    a.closest('li.r2')?.classList.add('open');
    const r = a.getBoundingClientRect(), b = rail.getBoundingClientRect();
    if (r.top < b.top + 150 || r.bottom > b.bottom - 40) a.scrollIntoView({ block: 'center' });
  }
  let ticking = false;
  function onScroll() {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(() => {
      ticking = false;
      let cur = heads[0];
      for (const h of heads) { if (h.getBoundingClientRect().top <= 120) cur = h; else break; }
      if (cur) setCurrent(cur.id);
    });
  }
  addEventListener('scroll', onScroll, { passive: true });
  onScroll();

  // Search over every section: title first, then body text.
  const secs = heads.map((h, i) => {
    const stop = heads[i + 1];
    let text = '', n = h.nextSibling;
    const walker = document.createTreeWalker(document.querySelector('main'), NodeFilter.SHOW_TEXT);
    walker.currentNode = h;
    while ((n = walker.nextNode())) {
      if (h.contains(n)) continue;
      if (n.parentNode.closest('.index')) break;
      if (stop && (stop.compareDocumentPosition(n) & Node.DOCUMENT_POSITION_FOLLOWING || stop === n.parentNode || stop.contains(n))) break;
      text += ' ' + n.nodeValue;
    }
    return { id: h.id, title: h.textContent.replace(/^(Chapter \d+|Appendix [A-F]|\d+\.\d+|[A-F]\.\d+)/, '').trim(), text: text.replace(/\s+/g, ' ').trim() };
  });
  const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const mark = (s, terms) => esc(s).replace(new RegExp('(' + terms.map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|') + ')', 'ig'), '<mark>$1</mark>');
  function search(v) {
    const terms = v.toLowerCase().split(/\s+/).filter(t => t.length > 1);
    if (!terms.length) { results.hidden = true; tocNav.hidden = false; return; }
    const hits = [];
    for (const s of secs) {
      const t = s.title.toLowerCase(), b = s.text.toLowerCase();
      if (!terms.every(x => t.includes(x) || b.includes(x))) continue;
      const score = terms.reduce((n, x) => n + (t.includes(x) ? 10 : 0) + Math.min(b.split(x).length - 1, 5), 0);
      const at = Math.max(0, Math.min(...terms.map(x => { const i = b.indexOf(x); return i < 0 ? Infinity : i; })) - 40);
      hits.push({ s, score, snip: isFinite(at) ? (at ? '…' : '') + s.text.slice(at, at + 110) + '…' : '' });
    }
    hits.sort((a, b) => b.score - a.score);
    results.innerHTML = hits.length
      ? `<p class="count">${hits.length} section${hits.length === 1 ? '' : 's'}</p>` + hits.slice(0, 30).map(h => `<a href="#${h.s.id}"><b>${mark(h.s.title, terms)}</b><span>${mark(h.snip, terms)}</span></a>`).join('')
      : '<p class="count">Nothing found. Try fewer or simpler words.</p>';
    results.hidden = false; tocNav.hidden = true;
  }
  q.addEventListener('input', () => search(q.value));
  q.addEventListener('keydown', e => {
    if (e.key === 'Enter') { const first = results.querySelector('a'); if (first) { first.click(); location.hash = first.getAttribute('href'); } }
    if (e.key === 'Escape') { q.value = ''; search(''); }
  });

  // Phone menu.
  const open = on => { document.body.classList.toggle('rail-open', on); scrim.hidden = !on; menu.setAttribute('aria-expanded', String(on)); };
  menu.addEventListener('click', () => open(!document.body.classList.contains('rail-open')));
  scrim.addEventListener('click', () => open(false));
  rail.addEventListener('click', e => { if (e.target.closest('a')) open(false); });
  addEventListener('keydown', e => { if (e.key === 'Escape') open(false); });
})();
