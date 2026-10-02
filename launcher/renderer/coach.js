'use strict';

// The coach card: plain text from its own query string, nothing else.
(function () {
  const q = new URLSearchParams(location.search);
  const mode = q.get('mode') === 'classic' ? 'classic' : 'quick';
  const clip = (s, n) => String(s || '').slice(0, n);
  const name = clip(q.get('name'), 64);
  const host = clip(q.get('host'), 253);
  const port = clip(q.get('port'), 5);
  const $ = (id) => document.getElementById(id);

  $('host').textContent = host;
  $('port').textContent = port;
  const steps = $('steps');
  const add = (html) => {
    const li = document.createElement('li');
    for (const part of html) {
      if (typeof part === 'string') li.appendChild(document.createTextNode(part));
      else {
        const b = document.createElement('b');
        b.textContent = part.b;
        li.appendChild(b);
      }
    }
    steps.appendChild(li);
  };

  if (mode === 'quick') {
    $('title').textContent = 'Joining ' + name;
    $('lead').textContent = 'Steam is opening Reign of Kings and will connect by itself.';
    add(['If Steam asks about launch options, choose ', { b: 'OK' }, '.']);
    add(['If the game stops at its main menu instead, open ', { b: 'Direct Connect' }, ', click the address box and press ', { b: 'Ctrl+V' }, ' (already copied), then set the port below.']);
    $('fine').textContent = 'This card closes by itself in a few minutes.';
  } else {
    $('title').textContent = 'Join ' + name;
    $('lead').textContent = 'Steam is opening Reign of Kings. Then:';
    add(['In the main menu, open the server list and choose ', { b: 'Direct Connect' }, '.']);
    add(['Click the address box and press ', { b: 'Ctrl+V' }, '. The address is already copied.']);
    add(['Type the port below into the port box, then press ', { b: 'Join' }, '.']);
    $('fine').textContent = 'Menu names come from the game and may differ slightly.';
  }
  $('close').addEventListener('click', () => window.close());
})();
