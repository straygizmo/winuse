(interactiveOnly) => {
  const INTERACTIVE = 'a[href], button, input:not([type=hidden]), select, textarea, [role=button], [role=link], [role=textbox], [role=checkbox], [role=radio], [role=combobox], [role=menuitem], [role=tab], [role=option], [role=switch], [contenteditable=true], [tabindex]:not([tabindex="-1"])';
  const TEXT_TAGS = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'li', 'td', 'th', 'label', 'span', 'div']);
  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const isVisible = (el) => {
    if (el.getClientRects().length === 0) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none';
  };

  const roleOf = (el) => {
    const explicit = el.getAttribute('role');
    if (explicit) return explicit.trim().split(/\s+/)[0];
    const tag = el.tagName.toLowerCase();
    if (tag === 'a') return 'link';
    if (tag === 'button') return 'button';
    if (tag === 'select') return 'combobox';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'input') {
      const t = (el.getAttribute('type') || 'text').toLowerCase();
      if (t === 'checkbox') return 'checkbox';
      if (t === 'radio') return 'radio';
      if (t === 'button' || t === 'submit' || t === 'reset' || t === 'image') return 'button';
      return 'textbox';
    }
    if (el.isContentEditable) return 'textbox';
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'li') return 'listitem';
    if (tag === 'td' || tag === 'th') return 'cell';
    return TEXT_TAGS.has(tag) ? 'text' : 'generic';
  };

  const nameOf = (el) => {
    const aria = norm(el.getAttribute('aria-label'));
    if (aria) return aria;
    const by = el.getAttribute('aria-labelledby');
    if (by) {
      const t = norm(by.split(/\s+/).map((id) => { const e = document.getElementById(id); return e ? e.textContent : ''; }).join(' '));
      if (t) return t;
    }
    if (el.labels && el.labels.length) {
      const t = norm(Array.from(el.labels).map((l) => l.textContent).join(' '));
      if (t) return t;
    }
    const ph = norm(el.getAttribute('placeholder'));
    if (ph) return ph;
    const title = norm(el.getAttribute('title'));
    if (title) return title;
    const tc = norm(el.textContent);
    if (tc) return tc.length > 80 ? tc.substring(0, 80) : tc;
    const alt = norm(el.getAttribute('alt'));
    return alt || null;
  };

  const directText = (el) => norm(Array.from(el.childNodes).filter((n) => n.nodeType === 3).map((n) => n.textContent).join(' '));

  const quote = (s) => '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';

  const cssPath = (el) => {
    const parts = [];
    let cur = el;
    while (cur && cur.nodeType === 1 && cur !== document.body) {
      const parent = cur.parentElement;
      const idx = parent ? Array.prototype.indexOf.call(parent.children, cur) + 1 : 1;
      parts.unshift(cur.tagName.toLowerCase() + ':nth-child(' + idx + ')');
      cur = parent;
    }
    parts.unshift('body');
    return parts.join(' > ');
  };

  // Candidates used to decide whether a role+name selector is unique (Playwright matches names as
  // case-insensitive substrings).
  const candidates = Array.from(document.querySelectorAll(INTERACTIVE)).filter(isVisible)
    .map((el) => ({ el, role: roleOf(el), name: (nameOf(el) || '').toLowerCase() }));

  const selectorOf = (el, role, name, allowRole) => {
    const tid = el.getAttribute('data-testid');
    if (tid) return '[data-testid=' + quote(tid) + ']';
    if (el.id && /^[A-Za-z][\w-]*$/.test(el.id)) return '#' + el.id;
    if (allowRole && name) {
      const lname = name.toLowerCase();
      const count = candidates.filter((c) => c.role === role && c.name.includes(lname)).length;
      if (count === 1) return 'role=' + role + '[name=' + quote(name) + ']';
    }
    return cssPath(el);
  };

  const set = new Set(Array.from(document.querySelectorAll(INTERACTIVE)).filter(isVisible));
  const all = Array.from(set);
  const interactiveSet = new Set(all);
  if (!interactiveOnly) {
    for (const el of document.querySelectorAll('*')) {
      if (interactiveSet.has(el)) continue;
      if (!TEXT_TAGS.has(el.tagName.toLowerCase())) continue;
      if (!isVisible(el) || !directText(el)) continue;
      all.push(el);
    }
  }

  const dpr = window.devicePixelRatio || 1;
  // Screen physical pixels. WebView2 hosts may add a title/frame offset, so these can be skewed;
  // DOM actions go through Playwright and never use coordinates.
  const sx = window.screenX, sy = window.screenY;

  return all.map((el) => {
    const interactive = interactiveSet.has(el);
    const tag = el.tagName.toLowerCase();
    const role = roleOf(el);
    const name = interactive ? nameOf(el) : (directText(el).substring(0, 80) || null);
    const isField = tag === 'input' || tag === 'textarea' || tag === 'select';
    const type = (el.getAttribute('type') || '').toLowerCase();
    const value = isField && type !== 'password' ? String(el.value) : null;
    let checked = null;
    if (role === 'checkbox' || role === 'radio' || role === 'switch') {
      const ac = el.getAttribute('aria-checked');
      if (ac === 'true') checked = true;
      else if (ac === 'false') checked = false;
      else if (typeof el.checked === 'boolean') checked = el.checked;
    }
    const r = el.getBoundingClientRect();
    return {
      role,
      name,
      tag,
      value,
      enabled: !el.disabled && el.getAttribute('aria-disabled') !== 'true',
      focused: el === document.activeElement,
      checked,
      rect: { x: sx + r.x * dpr, y: sy + r.y * dpr, w: r.width * dpr, h: r.height * dpr },
      selector: selectorOf(el, role, name, interactive),
    };
  });
}
