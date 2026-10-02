// A small, safe Markdown renderer for docs/community/*.md. It covers what those files use:
// headings (with GitHub-style anchors), paragraphs, bold/italic, inline code, fenced code,
// links, bullet and numbered lists (nested by indentation), blockquotes, tables and rules.
// Raw HTML is never passed through: every character of source text is escaped.

import { esc, anchor } from './util.mjs';

const unesc = (s) => s.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');

// linkFor(href) -> href to use, or null to render the link text without a link.
export function inline(text, linkFor = defaultLink) {
  const codes = [];
  let s = String(text).replace(/`([^`]+)`/g, (_, c) => {
    codes.push(`<code>${esc(c)}</code>`);
    return `\u0000${codes.length - 1}\u0000`;
  });
  s = esc(s);
  s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (_, alt) => alt); // images: alt text only
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, href) => {
    const target = linkFor(unesc(href));
    if (!target) return label;
    const ext = /^https?:/i.test(target);
    return `<a href="${esc(target)}"${ext ? ' rel="noopener" target="_blank"' : ''}>${label}</a>`;
  });
  s = s.replace(/\*\*([^*]+?)\*\*/g, '<strong>$1</strong>');
  s = s.replace(/__([^_]+?)__/g, '<strong>$1</strong>');
  s = s.replace(/(^|[^*\w])\*(?!\s)([^*]+?)\*(?!\w)/g, '$1<em>$2</em>');
  s = s.replace(/(^|[^_\w])_(?!\s)([^_]+?)_(?!\w)/g, '$1<em>$2</em>');
  return s.replace(/\u0000(\d+)\u0000/g, (_, i) => codes[Number(i)]);
}

function defaultLink(href) {
  return /^https:\/\//i.test(href) || href.startsWith('#') ? href : null;
}

const isTableSep = (l) => /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(l);
// Splits a table row on "|", except escaped "\|" and pipes inside `code spans`.
function splitRow(l) {
  const src = l.trim().replace(/^\|/, '').replace(/\|$/, '');
  const cells = [];
  let cur = '';
  let inCode = false;
  for (let i = 0; i < src.length; i++) {
    const ch = src[i];
    if (ch === '\\' && src[i + 1] === '|') { cur += '|'; i++; continue; }
    if (ch === '`') inCode = !inCode;
    if (ch === '|' && !inCode) { cells.push(cur.trim()); cur = ''; continue; }
    cur += ch;
  }
  cells.push(cur.trim());
  return cells;
}
const LIST_RE = /^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$/;

// Returns { html, headings: [{level, text, id}] }.
export function render(md, { linkFor = defaultLink, idPrefix = '' } = {}) {
  const lines = String(md || '').replace(/\r\n?/g, '\n').split('\n');
  const headings = [];
  const ids = new Map();
  const uniqueId = (text) => {
    const base = idPrefix + (anchor(text) || 'section');
    const n = ids.get(base) || 0;
    ids.set(base, n + 1);
    return n ? `${base}-${n}` : base;
  };
  const html = blocks(lines, { linkFor, headings, uniqueId });
  return { html, headings };
}

function blocks(lines, ctx) {
  const out = [];
  let i = 0;
  const para = [];
  const flush = () => {
    if (para.length) out.push(`<p>${inline(para.join(' '), ctx.linkFor)}</p>`);
    para.length = 0;
  };
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { flush(); i++; continue; }

    const fence = line.match(/^\s*(```|~~~)\s*([\w-]*)\s*$/);
    if (fence) {
      flush();
      const body = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith(fence[1])) body.push(lines[i++]);
      i++;
      out.push(`<pre class="code"><code>${esc(body.join('\n'))}</code></pre>`);
      continue;
    }

    const h = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/);
    if (h) {
      flush();
      const level = h[1].length;
      const id = ctx.uniqueId(h[2].replace(/[`*_]/g, ''));
      ctx.headings.push({ level, text: h[2].replace(/[`*_]/g, ''), id });
      out.push(`<h${level} id="${esc(id)}">${inline(h[2], ctx.linkFor)}</h${level}>`);
      i++;
      continue;
    }

    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { flush(); out.push('<hr>'); i++; continue; }

    if (/^\s*>/.test(line)) {
      flush();
      const quoted = [];
      while (i < lines.length && /^\s*>/.test(lines[i])) quoted.push(lines[i++].replace(/^\s*>\s?/, ''));
      out.push(`<blockquote>${blocks(quoted, ctx)}</blockquote>`);
      continue;
    }

    if (line.includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1])) {
      flush();
      const head = splitRow(line);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].includes('|') && lines[i].trim()) rows.push(splitRow(lines[i++]));
      const th = head.map((c) => `<th>${inline(c, ctx.linkFor)}</th>`).join('');
      const tb = rows.map((r) => `<tr>${head.map((_, k) => `<td>${inline(r[k] || '', ctx.linkFor)}</td>`).join('')}</tr>`).join('');
      out.push(`<div class="table-wrap"><table><thead><tr>${th}</tr></thead><tbody>${tb}</tbody></table></div>`);
      continue;
    }

    const li = line.match(LIST_RE);
    if (li) {
      flush();
      const baseIndent = li[1].length;
      const ordered = /\d/.test(li[2]);
      const items = [];
      while (i < lines.length) {
        const m = lines[i].match(LIST_RE);
        if (m && m[1].length === baseIndent) {
          items.push([m[3]]);
          i++;
        } else if (lines[i].trim() && (/^\s/.test(lines[i]) && (lines[i].match(/^\s*/)[0].length > baseIndent))) {
          items[items.length - 1].push(lines[i].slice(Math.min(lines[i].match(/^\s*/)[0].length, baseIndent + 2)));
          i++;
        } else if (!lines[i].trim() && i + 1 < lines.length && (lines[i + 1].match(LIST_RE) || /^\s{2,}\S/.test(lines[i + 1])) && (lines[i + 1].match(/^\s*/)[0].length >= baseIndent)) {
          const nm = lines[i + 1].match(LIST_RE);
          if (nm && nm[1].length === baseIndent && /\d/.test(nm[2]) !== ordered) break;
          i++;
        } else break;
      }
      const tag = ordered ? 'ol' : 'ul';
      out.push(`<${tag}>${items.map((it) => {
        const [first, ...rest] = it;
        const nested = rest.length ? blocks(rest, ctx) : '';
        return `<li>${inline(first, ctx.linkFor)}${nested}</li>`;
      }).join('')}</${tag}>`);
      continue;
    }

    para.push(line.trim());
    i++;
  }
  flush();
  return out.join('\n');
}
