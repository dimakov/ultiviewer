// Preview shell: the app posts rendered HTML here; this script swaps it in, highlights code, renders math
// and diagrams, keeps scrolling in step with the editor and reports clicks back to the app.
(() => {
  'use strict';

  const APP = 'https://app.ultiviewer.example/';
  const content = document.getElementById('content');
  const header = document.getElementById('page-header');
  const titleEl = document.getElementById('page-title');
  const metaEl = document.getElementById('page-meta');
  const baseEl = document.querySelector('base');
  const post = (message) => window.chrome.webview.postMessage(message);

  let blocks = [];
  let theme = 'light';
  let programmaticUntil = 0;
  let mermaidReady = null;
  const mermaidCache = new Map();

  // ---- Rendering ----

  function render(msg) {
    if (msg.baseUrl) baseEl.href = msg.baseUrl;
    const keepY = window.scrollY;
    content.innerHTML = msg.html;

    const first = content.firstElementChild;
    const startsWithTitle = first && first.tagName === 'H1';
    titleEl.textContent = msg.title || '';
    titleEl.hidden = startsWithTitle || !msg.title;
    metaEl.textContent = msg.meta || '';
    header.hidden = !msg.title && !msg.meta;

    enhance();
    collectBlocks();
    if (typeof msg.line === 'number') scrollToLine(msg.line);
    else setScroll(keepY);
  }

  function enhance() {
    for (const box of content.querySelectorAll('li.task-list-item > input[type=checkbox], input.task-list-item-checkbox')) {
      box.disabled = false;
      box.title = 'Toggle task';
    }

    if (window.hljs) {
      for (const code of content.querySelectorAll('pre > code')) {
        if (code.closest('.mermaid')) continue;
        try { window.hljs.highlightElement(code); } catch { /* unknown language */ }
      }
    }

    if (window.renderMathInElement) {
      for (const el of content.querySelectorAll('.math')) {
        try {
          window.renderMathInElement(el, {
            delimiters: [
              { left: '\\[', right: '\\]', display: true },
              { left: '\\(', right: '\\)', display: false },
            ],
            throwOnError: false,
          });
        } catch { /* leave the source visible */ }
      }
    }

    for (const img of content.querySelectorAll('img')) {
      img.loading = 'lazy';
      img.addEventListener('load', collectBlocks, { once: true });
    }

    const diagrams = content.querySelectorAll('.mermaid');
    if (diagrams.length) renderDiagrams(diagrams);
  }

  function loadMermaid() {
    mermaidReady ??= new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = APP + 'vendor/mermaid/mermaid.min.js';
      script.onload = () => {
        window.mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: mermaidTheme() });
        resolve();
      };
      script.onerror = reject;
      document.head.appendChild(script);
    });
    return mermaidReady;
  }

  const mermaidTheme = () => (theme === 'dark' ? 'dark' : 'default');

  let diagramCounter = 0;
  async function renderDiagrams(nodes) {
    try { await loadMermaid(); } catch { return; }
    for (const node of nodes) {
      const source = node.textContent;
      const key = theme + '\u0000' + source;
      if (mermaidCache.has(key)) {
        node.innerHTML = mermaidCache.get(key);
        continue;
      }
      try {
        const { svg } = await window.mermaid.render('uv-mermaid-' + (++diagramCounter), source);
        mermaidCache.set(key, svg);
        if (node.isConnected) node.innerHTML = svg;
      } catch (error) {
        node.classList.add('mermaid-error');
        node.textContent = 'Diagram error: ' + (error && error.message ? error.message : error);
      }
    }
    collectBlocks();
  }

  function setTheme(next) {
    theme = next;
    document.documentElement.dataset.theme = next;
    document.getElementById('hljs-light').disabled = next === 'dark';
    document.getElementById('hljs-dark').disabled = next !== 'dark';
    if (window.mermaid) {
      window.mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: mermaidTheme() });
      const diagrams = content.querySelectorAll('.mermaid');
      if (diagrams.length) renderDiagrams(diagrams);
    }
  }

  // ---- Scroll sync (lines are 1-based, fractional) ----

  function collectBlocks() {
    blocks = Array.from(content.querySelectorAll('[data-line]'), (el) => ({ line: Number(el.dataset.line), el }))
      .filter((b) => b.el.getClientRects().length > 0);
  }

  const topOf = (el) => el.getBoundingClientRect().top + window.scrollY;

  function setScroll(y) {
    programmaticUntil = performance.now() + 120;
    window.scrollTo(0, Math.max(0, y));
  }

  function scrollToLine(line) {
    if (!blocks.length || line <= 1) {
      setScroll(0);
      return;
    }
    let before = null;
    let after = null;
    for (const b of blocks) {
      if (b.line <= line) before = b;
      else { after = b; break; }
    }
    if (!before) { setScroll(0); return; }
    const y0 = topOf(before.el);
    let y = y0;
    if (after && after.line > before.line) {
      y = y0 + ((line - before.line) / (after.line - before.line)) * (topOf(after.el) - y0);
    }
    setScroll(y - 12);
  }

  function lineAtTop() {
    const y = window.scrollY + 12;
    let before = null;
    let after = null;
    for (const b of blocks) {
      const t = topOf(b.el);
      if (t <= y) before = { line: b.line, t };
      else { after = { line: b.line, t }; break; }
    }
    if (!before) return 1;
    if (!after || after.t === before.t) return before.line;
    return before.line + ((y - before.t) / (after.t - before.t)) * (after.line - before.line);
  }

  let scrollQueued = false;
  window.addEventListener('scroll', () => {
    if (performance.now() < programmaticUntil || scrollQueued) return;
    scrollQueued = true;
    requestAnimationFrame(() => {
      scrollQueued = false;
      post({ type: 'scrolled', line: lineAtTop() });
    });
  }, { passive: true });

  window.addEventListener('resize', collectBlocks);

  // ---- Clicks ----

  document.addEventListener('click', (event) => {
    const box = event.target.closest('input[type=checkbox]');
    if (box && content.contains(box)) {
      const holder = box.closest('[data-line]');
      if (holder) post({ type: 'toggleTask', line: Number(holder.dataset.line) });
      return;
    }

    const link = event.target.closest('a[href]');
    if (!link) return;
    event.preventDefault();
    const href = link.getAttribute('href');
    if (href.startsWith('#')) {
      const target = document.getElementById(decodeURIComponent(href.slice(1)));
      if (target) {
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        target.classList.add('flash');
        setTimeout(() => target.classList.remove('flash'), 1300);
      }
      return;
    }
    post({ type: 'link', href });
  });

  document.addEventListener('dblclick', (event) => {
    const holder = event.target.closest('[data-line]');
    if (holder && content.contains(holder)) post({ type: 'dblclick', line: Number(holder.dataset.line) });
  });

  // Ctrl+wheel and keyboard shortcuts stay with the app.
  document.addEventListener('keydown', (event) => {
    if (event.key === 'F5' || (event.ctrlKey && ['r', 'p', 'f', 'g'].includes(event.key.toLowerCase()))) {
      event.preventDefault();
    }
  });

  // ---- Messages from the app ----

  window.chrome.webview.addEventListener('message', (event) => {
    const msg = event.data;
    switch (msg.type) {
      case 'render': render(msg); break;
      case 'scroll': scrollToLine(msg.line); break;
      case 'theme': setTheme(msg.theme); break;
    }
  });

  post({ type: 'ready' });
})();
