'use strict';

// Connection Doctor UI, shared by both editions.
// - Realm Steward: the "Doctor" view (#doctor-root): checks for a chosen server, the live game or
//   server log with known errors highlighted, a paste box, and Copy report.
// - Realm (player): a "Can't join?" panel opened by any [data-doctor-open] button.
// Builds its own DOM; talks only to the bridge (window.realm.doctor.* or window.realm.doctor*).
(function () {
  const api = window.realm;
  if (!api) return;
  const steward = !!(api.doctor && api.doctor.run);
  const D = steward
    ? api.doctor
    : api.doctorRun
      ? { run: api.doctorRun, log: api.doctorLog, classify: api.doctorClassify, copyReport: api.doctorReport }
      : null;
  if (!D) return;

  function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }
  function icon(id, cls) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', cls || 'ico');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }
  function btn(label, iconId, cls) {
    const b = el('button', 'btn ' + (cls || ''));
    if (iconId) b.appendChild(icon(iconId));
    b.appendChild(el('span', null, label));
    return b;
  }
  function toast(msg, kind) {
    const box = document.getElementById('toasts');
    if (!box) return;
    const t = el('div', 'toast' + (kind ? ' ' + kind : ''), msg);
    box.appendChild(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 5000);
  }
  const MARK = { ok: 'i-check', warn: 'i-alert', bad: 'i-x', skip: 'i-skip', info: 'i-eye' };
  const WORD = { ok: 'Good', warn: 'Check', bad: 'Problem', skip: 'Skipped', info: 'Note' };

  // ---------------------------------------------------------------- shared pieces

  function renderVerdict(box, v) {
    box.replaceChildren();
    box.className = 'doc-verdict ' + (v ? v.status : 'idle');
    if (!v) {
      box.appendChild(el('p', 'doc-verdict-title', 'Press Run checks.'));
      box.appendChild(el('p', 'fine', 'Every step reads only: nothing on this PC, the router or the game is changed.'));
      return;
    }
    box.appendChild(icon(MARK[v.status] || 'i-eye', 'doc-verdict-ico'));
    const t = el('div', 'doc-verdict-text');
    t.appendChild(el('p', 'doc-verdict-title', v.status === 'ok' ? 'All clear' : v.status === 'bad' ? 'Found the problem' : 'Something to check'));
    t.appendChild(el('p', 'doc-verdict-cause', v.title));
    if (v.fix) {
      const f = el('p', 'doc-fix');
      f.appendChild(el('b', null, 'Fix: '));
      f.appendChild(document.createTextNode(v.fix));
      t.appendChild(f);
    }
    box.appendChild(t);
  }

  function renderSteps(list, steps) {
    list.replaceChildren();
    for (const s of steps) {
      const li = el('li', 'doc-step ' + s.status);
      const badge = el('span', 'doc-badge ' + s.status);
      badge.appendChild(icon(MARK[s.status] || 'i-eye'));
      badge.appendChild(el('span', null, WORD[s.status] || s.status));
      li.appendChild(badge);
      const body = el('div', 'doc-step-body');
      body.appendChild(el('p', 'doc-step-label', s.label));
      body.appendChild(el('p', 'doc-step-detail', s.detail));
      if (s.fix && (s.status === 'bad' || s.status === 'warn')) {
        const f = el('p', 'doc-fix');
        f.appendChild(el('b', null, 'Fix: '));
        f.appendChild(document.createTextNode(s.fix));
        body.appendChild(f);
      }
      li.appendChild(body);
      list.appendChild(li);
    }
  }

  function renderFindings(list, findings, empty) {
    list.replaceChildren();
    const shown = (findings || []).filter((f) => f.severity !== 'ok').slice(0, 8);
    if (!shown.length) {
      list.appendChild(el('li', 'empty', empty));
      return;
    }
    for (const f of shown) {
      const li = el('li', 'doc-finding ' + f.severity);
      const head = el('p', 'doc-finding-title');
      head.appendChild(el('span', 'doc-dot ' + f.severity));
      head.appendChild(document.createTextNode(f.title + (f.count > 1 ? `  x${f.count}` : '')));
      li.appendChild(head);
      li.appendChild(el('p', 'doc-finding-line mono', f.match));
      if (f.cause) li.appendChild(el('p', 'doc-finding-cause', f.cause));
      if (f.fix) {
        const fx = el('p', 'doc-fix');
        fx.appendChild(el('b', null, 'Fix: '));
        fx.appendChild(document.createTextNode(f.fix));
        li.appendChild(fx);
      }
      list.appendChild(li);
    }
  }

  function pasteBox(onResult) {
    const wrap = el('div', 'doc-paste');
    const ta = el('textarea', 'doc-paste-input');
    ta.rows = 3;
    ta.maxLength = 4000;
    ta.placeholder = 'Paste or type the message the game showed, e.g. "Unable to resolve host name. (127.0.0.1:7350)"';
    const go = btn('Explain', 'i-eye', 'small');
    const out = el('ul', 'doc-findings');
    go.addEventListener('click', async () => {
      if (!ta.value.trim()) return;
      try {
        const r = await D.classify(ta.value);
        renderFindings(out, r.findings, 'Realm does not know this message yet. Copy the report and send it to the server owner.');
        if (onResult) onResult(ta.value, r);
      } catch (e) {
        toast(e.message, 'bad');
      }
    });
    const row = el('div', 'row gap');
    row.appendChild(go);
    wrap.appendChild(ta);
    wrap.appendChild(row);
    wrap.appendChild(out);
    return { wrap, ta };
  }

  // ---------------------------------------------------------------- steward view

  function stewardView() {
    const root = document.getElementById('doctor-root');
    if (!root) return null;
    let selId = 's1';
    let lastRun = null;
    let logIndex = null;
    let logOffset = null;
    let pollTimer = null;
    let onlyProblems = false;
    let running = false;

    root.replaceChildren();
    const head = el('header', 'view-head');
    const titles = el('div');
    titles.appendChild(el('p', 'eyebrow', 'Why can\'t they join?'));
    titles.appendChild(el('h1', 'view-title', 'Connection Doctor'));
    head.appendChild(titles);
    const pick = el('div', 'fleet-pick');
    pick.setAttribute('role', 'tablist');
    pick.setAttribute('aria-label', 'Server');
    head.appendChild(pick);
    root.appendChild(head);

    const grid = el('div', 'doc-grid');
    const left = el('div', 'doc-col');
    const right = el('div', 'doc-col');
    grid.appendChild(left);
    grid.appendChild(right);
    root.appendChild(grid);

    const vCard = el('article', 'card doc-verdict-card');
    const verdictBox = el('div', 'doc-verdict idle');
    vCard.appendChild(verdictBox);
    const actions = el('div', 'row gap');
    const runBtn = btn('Run checks', 'i-eye', 'primary small');
    runBtn.id = 'doc-run';
    const copyBtn = btn('Copy report', 'i-copy', 'small');
    copyBtn.id = 'doc-copy';
    copyBtn.disabled = true;
    actions.appendChild(runBtn);
    actions.appendChild(copyBtn);
    actions.appendChild(el('span', 'fine', 'The report hides your public IP, Steam IDs, passwords and Windows user name.'));
    vCard.appendChild(actions);
    left.appendChild(vCard);

    const sCard = el('article', 'card');
    sCard.appendChild(el('h2', 'card-title', 'Checks'));
    const steps = el('ol', 'doc-steps');
    steps.appendChild(el('li', 'empty', 'No checks run yet.'));
    sCard.appendChild(steps);
    left.appendChild(sCard);

    const pCard = el('article', 'card');
    pCard.appendChild(el('h2', 'card-title', 'What did the game say?'));
    let pasted = '';
    const paste = pasteBox((text) => (pasted = text));
    pCard.appendChild(paste.wrap);
    left.appendChild(pCard);

    const lCard = el('article', 'card doc-log-card');
    const lHead = el('header', 'card-head');
    lHead.appendChild(el('h2', null, 'Live log'));
    const sel = el('select', 'doc-log-pick');
    sel.setAttribute('aria-label', 'Log file');
    lHead.appendChild(sel);
    lCard.appendChild(lHead);
    const lTools = el('div', 'row gap doc-log-tools');
    const onlyLbl = el('label', 'check');
    const onlyBox = el('input');
    onlyBox.type = 'checkbox';
    onlyLbl.appendChild(onlyBox);
    onlyLbl.appendChild(el('span', null, 'Only known messages'));
    lTools.appendChild(onlyLbl);
    const where = el('span', 'fine mono doc-log-path');
    lTools.appendChild(where);
    lCard.appendChild(lTools);
    const findings = el('ul', 'doc-findings');
    findings.appendChild(el('li', 'empty', 'Run the checks to find the logs.'));
    lCard.appendChild(findings);
    const pre = el('pre', 'console doc-console');
    pre.appendChild(el('span', 'placeholder', 'The newest game log (on this PC) and the server logs appear here, with known problems highlighted.'));
    lCard.appendChild(pre);
    right.appendChild(lCard);

    function renderPick(list) {
      pick.replaceChildren();
      for (const s of list) {
        const b = el('button', s.id === selId ? 'active' : '', `${s.id.slice(1)} · ${s.name}`);
        b.setAttribute('role', 'tab');
        b.addEventListener('click', () => {
          selId = s.id;
          renderPick(list);
          run();
        });
        pick.appendChild(b);
      }
    }

    function appendLog(lines, reset) {
      if (reset) pre.replaceChildren();
      const ph = pre.querySelector('.placeholder');
      if (ph) ph.remove();
      const atBottom = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 30;
      for (const l of lines) {
        if (onlyProblems && !(l.finding && l.finding.severity !== 'ok')) continue;
        const span = el('span', 'ln' + (l.finding ? ' hit ' + l.finding.severity : l.level === 'error' ? ' err' : l.level === 'warning' ? ' wrn' : ''), l.text);
        pre.appendChild(span);
      }
      while (pre.childElementCount > 2500) pre.firstChild.remove();
      if (atBottom || reset) pre.scrollTop = pre.scrollHeight;
    }

    async function pollLog(reset) {
      if (logIndex == null) return;
      try {
        const r = await D.log({ index: logIndex, offset: reset ? null : logOffset });
        logOffset = r.offset;
        appendLog(r.lines, reset || r.reset);
        if (reset) renderFindings(findings, r.findings, 'No known problem in the last part of this log.');
        else if (r.findings.some((f) => f.severity === 'bad' || f.severity === 'warn')) {
          renderFindings(findings, r.findings, '');
          findings.classList.add('flash');
          setTimeout(() => findings.classList.remove('flash'), 1200);
        }
      } catch {
        /* file rotated or gone: the next run picks it up */
      }
    }

    function startPoll() {
      clearInterval(pollTimer);
      pollTimer = setInterval(() => {
        if (document.body.dataset.view === 'doctor') pollLog(false);
      }, 1500);
    }

    function renderLogs(logs) {
      sel.replaceChildren();
      if (!logs.length) {
        sel.appendChild(el('option', null, 'No logs found yet'));
        sel.disabled = true;
        logIndex = null;
        where.textContent = '';
        pre.replaceChildren(el('span', 'placeholder', 'No log file yet. Start the server, or start the game once from Steam, then run the checks again.'));
        renderFindings(findings, [], 'No logs to read yet.');
        return;
      }
      sel.disabled = false;
      for (const l of logs) {
        const o = el('option', null, `${l.kind === 'client' ? 'Your game' : 'Server'}: ${l.label}`);
        o.value = String(l.index);
        sel.appendChild(o);
      }
      const keep = logs.find((l) => l.index === logIndex);
      logIndex = keep ? keep.index : logs[0].index;
      sel.value = String(logIndex);
      showLogPath(logs);
      pollLog(true);
      startPoll();
    }

    function showLogPath(logs) {
      const l = logs.find((x) => x.index === logIndex);
      where.textContent = l ? l.path : '';
      where.title = l ? l.evidence : '';
    }

    sel.addEventListener('change', () => {
      logIndex = Number(sel.value);
      logOffset = null;
      if (lastRun) showLogPath(lastRun.logs);
      pollLog(true);
    });
    onlyBox.addEventListener('change', () => {
      onlyProblems = onlyBox.checked;
      pollLog(true);
    });

    async function run() {
      if (running) return;
      running = true;
      runBtn.disabled = true;
      steps.replaceChildren(el('li', 'empty', 'Checking... (sockets, Steam, logs; about 5 seconds)'));
      try {
        lastRun = await D.run(selId);
        renderVerdict(verdictBox, lastRun.verdict);
        renderSteps(steps, lastRun.steps);
        renderLogs(lastRun.logs);
        copyBtn.disabled = false;
      } catch (e) {
        steps.replaceChildren(el('li', 'empty', e.message));
      } finally {
        running = false;
        runBtn.disabled = false;
      }
    }

    runBtn.addEventListener('click', run);
    copyBtn.addEventListener('click', async () => {
      try {
        const r = await D.copyReport({ pasted, logIndex });
        toast(`Report copied (${r.chars} characters). Paste it in Discord or a message to whoever helps you.`, 'ok');
      } catch (e) {
        toast(e.message, 'bad');
      }
    });

    let first = true;
    return {
      async show() {
        try {
          const list = api.fleet ? await api.fleet.list() : [{ id: 's1', name: 'Server 1' }];
          if (!list.some((s) => s.id === selId)) selId = list[0] ? list[0].id : 's1';
          renderPick(list);
        } catch {
          /* fleet unavailable: keep s1 */
        }
        if (first) {
          first = false;
          run();
        } else startPoll();
      }
    };
  }

  // ---------------------------------------------------------------- player panel

  function playerPanel() {
    let modal = null;
    let pasted = '';
    let ran = false;

    function build() {
      modal = el('div', 'modal doc-modal');
      modal.hidden = true;
      const card = el('div', 'modal-card doc-card');
      card.setAttribute('role', 'dialog');
      card.setAttribute('aria-modal', 'true');
      card.setAttribute('aria-label', "Can't join?");
      const head = el('div', 'row doc-card-head');
      const ttl = el('div', 'grow');
      ttl.appendChild(el('p', 'eyebrow', 'Connection Doctor'));
      ttl.appendChild(el('h2', null, "Can't join?"));
      head.appendChild(ttl);
      const close = el('button', 'win-btn doc-close');
      close.setAttribute('aria-label', 'Close');
      close.appendChild(icon('i-x'));
      close.addEventListener('click', () => (modal.hidden = true));
      head.appendChild(close);
      card.appendChild(head);
      const verdict = el('div', 'doc-verdict idle');
      card.appendChild(verdict);
      const actions = el('div', 'row gap');
      const runBtn = btn('Check again', 'i-restart', 'small');
      const copyBtn = btn('Copy report', 'i-copy', 'small primary');
      copyBtn.disabled = true;
      actions.appendChild(runBtn);
      actions.appendChild(copyBtn);
      actions.appendChild(el('span', 'fine', 'Hides your IP, Steam ID and Windows user name.'));
      card.appendChild(actions);
      const scroll = el('div', 'doc-card-scroll');
      const steps = el('ol', 'doc-steps');
      scroll.appendChild(steps);
      scroll.appendChild(el('h3', 'doc-sub', 'Known messages in your game log'));
      const findings = el('ul', 'doc-findings');
      scroll.appendChild(findings);
      scroll.appendChild(el('h3', 'doc-sub', 'What did the game say?'));
      const paste = pasteBox((text) => (pasted = text));
      scroll.appendChild(paste.wrap);
      card.appendChild(scroll);
      modal.appendChild(card);
      document.body.appendChild(modal);
      modal.addEventListener('click', (e) => {
        if (e.target === modal) modal.hidden = true;
      });
      document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && modal && !modal.hidden) modal.hidden = true;
      });

      async function run() {
        runBtn.disabled = true;
        steps.replaceChildren(el('li', 'empty', 'Checking your game, Steam and the servers...'));
        try {
          const r = await D.run(null);
          renderVerdict(verdict, r.verdict);
          renderSteps(steps, r.steps);
          renderFindings(findings, r.findings, r.logs && r.logs.length ? 'No known problem in your game log.' : 'No game log found yet. It appears after the game has been started once.');
          copyBtn.disabled = false;
        } catch (e) {
          steps.replaceChildren(el('li', 'empty', e.message));
        } finally {
          runBtn.disabled = false;
        }
      }
      runBtn.addEventListener('click', run);
      copyBtn.addEventListener('click', async () => {
        try {
          const r = await D.copyReport({ pasted });
          toast(`Report copied (${r.chars} characters). Paste it to the server owner or in Discord.`, 'ok');
        } catch (e) {
          toast(e.message, 'bad');
        }
      });
      return run;
    }

    let run = null;
    return {
      open() {
        if (!modal) run = build();
        modal.hidden = false;
        if (!ran) {
          ran = true;
          run();
        }
      }
    };
  }

  // ---------------------------------------------------------------- wiring

  if (steward) {
    const view = stewardView();
    window.RealmDoctor = { show: () => view && view.show() };
  } else {
    const panel = playerPanel();
    window.RealmDoctor = { open: () => panel.open() };
    document.addEventListener('click', (e) => {
      if (e.target.closest('[data-doctor-open]')) panel.open();
    });
  }
})();
