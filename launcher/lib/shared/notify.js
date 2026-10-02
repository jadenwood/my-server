'use strict';

// Chronicle events -> tray notifications (player edition). Pure mapping, plain text only.
// Event types come from the Chronicle contract (README "data files"); names in events are public
// player names only.

const NOTIFY = {
  coronation: 'A new king',
  abdication: 'The throne is empty',
  claim_declared: 'A claim on the crown',
  rebellion_started: 'Rebellion!',
  rebellion_ended: 'The rebellion is over'
};

function clean(s, max) {
  return String(s == null ? '' : s).replace(/[\u0000-\u001f\u007f]/g, ' ').trim().slice(0, max);
}

// Returns { title, body } or null when the event should not raise a notification.
function eventToNotification(event, serverName) {
  if (!event || typeof event !== 'object' || !Object.prototype.hasOwnProperty.call(NOTIFY, event.type)) return null;
  const where = clean(serverName, 40);
  return {
    title: clean(`${where ? where + ': ' : ''}${NOTIFY[event.type]}`, 80),
    body: clean([event.title, event.detail].filter(Boolean).join('. '), 200)
  };
}

// Picks the events newer than lastId. The first poll (lastId null) only sets the baseline, so old
// history never floods the tray.
function freshEvents(events, lastId) {
  const list = (Array.isArray(events) ? events : []).filter((e) => e && Number.isInteger(e.id));
  const maxId = list.reduce((m, e) => Math.max(m, e.id), Number.isInteger(lastId) ? lastId : 0);
  if (lastId == null) return { events: [], lastId: maxId };
  return { events: list.filter((e) => e.id > lastId).sort((a, b) => a.id - b.id).slice(-5), lastId: maxId };
}

module.exports = { NOTIFY, eventToNotification, freshEvents };
