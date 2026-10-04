// The arrival site (docs/arrival-design.md section 4): the Gatehouse of the Unwritten, the processional between the six
// house monuments and their pledge stones, the Hearth ring with the ember band, the Herald's Pillar and the wayboard,
// with the cells, points, boxes, zones and sign spots RealmArrival and the run-sheet use. Written to
// art/sculptures/sites/arrival.json by: node art/tools/sculptor/cli.mjs build. Geometry lives in ../lib/arrival-site.mjs.
import { standFor, turnedSize, CELL_M, FACING_WORD } from '../../../tools/sculptor/site.mjs';
import { GH, AVENUE, HEARTH, BAND, PILLAR, WAYBOARD, THRONE, HOUSE_SLOTS, PREVIEW_DRAW } from '../lib/arrival-site.mjs';
import { HOUSE_KEYS, pal, house } from '../lib/kit.mjs';
import { BAYS } from '../wayboard.mjs';

const MONUMENT_TURN = { left: 3, right: 1 };      // fronts to the road: the left row faces +x, the right row -x
const STONE_TURN = 0;

// Preview cameras (cells; eye height 1.6 m above the floor a player stands on). See art/tools/sculptor/site-render.mjs.
const EYE = 1 + 1.6 / 1.2;
const A2 = GH.stones.find((s) => s[0] === 'A2');
const PREVIEWS = [
  { name: 'reveal-closed', title: 'From arrival stone A2: the gate closed', state: { gate: 'closed', band: 'rest' }, fog: 340,
    cam: { kind: 'persp', eye: [A2[1] + 0.5, EYE, A2[2] + 0.5], yaw: 0, pitch: -6, fov: 68 } },
  { name: 'reveal-open', title: 'From arrival stone A2: the gate down, the ember band flared', state: { gate: 'open', band: 'flare' }, fog: 340,
    cam: { kind: 'persp', eye: [A2[1] + 0.5, EYE, A2[2] + 0.5], yaw: 0, pitch: -6, fov: 68 } },
  { name: 'threshold', title: 'On the gold line, the gate open', state: { gate: 'open', band: 'flare' }, fog: 340,
    cam: { kind: 'persp', eye: [0.5, EYE, GH.goldLine.z + 0.5], yaw: 0, pitch: -4, fov: 64 } },
  { name: 'court', title: 'The court from the gate: the six stones, the back wall for the signs', state: { gate: 'closed', band: 'rest' }, fog: 0,
    cam: { kind: 'persp', eye: [0.5, EYE + 1, GH.goldLine.z + 0.5], yaw: 180, pitch: 12, fov: 74 } },
  { name: 'avenue', title: 'Between the second pair', state: { gate: 'open', band: 'flare' }, fog: 340,
    cam: { kind: 'persp', eye: [0.5, EYE, AVENUE.pairs[1] + 10.5], yaw: 0, pitch: -6, fov: 70 } },
  { name: 'hearth', title: 'At the ring edge: the fire, the pillar, the wayboard and the throne beyond', state: { gate: 'open', band: 'flare' }, fog: 400,
    cam: { kind: 'persp', eye: [3.5, EYE, HEARTH.z - HEARTH.half - 6], yaw: -4, pitch: -3, fov: 70 } },
  { name: 'aerial', title: 'The site from above the Gatehouse', state: { gate: 'closed', band: 'rest' }, width: 1600, height: 900,
    cam: { kind: 'ortho', yaw: 152, pitch: 30, centre: [0, 6, 104], scale: 5.2 } },
  { name: 'aerial-gate', title: 'The Gatehouse of the Unwritten', state: { gate: 'closed', band: 'rest' },
    cam: { kind: 'ortho', yaw: 205, pitch: 34, centre: [0, 4, 22], scale: 13 } },
  { name: 'side', title: 'Side elevation: gate, banners, fire, throne', state: { gate: 'open', band: 'rest' }, width: 1600, height: 500,
    cam: { kind: 'ortho', yaw: -90, pitch: 0, centre: [0, 12, 118], scale: 5.6 } },
];

export default {
  id: 'arrival',
  name: 'The Gatehouse of the Unwritten and the Hearth road',
  description: 'Where every newcomer arrives: the walled Gatehouse, the processional between the six house monuments, and the Hearth fire with its ember band, the Herald\'s Pillar and the wayboard. One axis from the gate to the fire to the Old Throne.',
  previews: PREVIEWS,
  build(sculptures) {
    const pieces = [];
    let order = 0;
    const size = (id, turn) => turnedSize(sculptures.get(id), turn);
    const add = (p) => {
      const sz = p.size || size(p.sculpture, p.turn);
      const out = { key: p.key, sculpture: p.sculpture, by: p.by };
      if (p.slot) out.slot = p.slot;
      out.turn = p.turn;
      out.at = p.at;
      out.size = sz;
      if (p.by === 'sculptor') {
        out.order = p.order;
        const st = standFor(p.at, sz, p.turn);
        out.stand = { cell: st.cell, facing: FACING_WORD[st.facing], command: `/sculpt place ${p.sculpture} ${p.turn}` };
      }
      if (p.optional) out.optional = true;
      if (p.note) out.note = p.note;
      pieces.push(out);
    };

    add({ key: 'gatehouse', sculpture: 'gatehouse-unwritten', by: 'sculptor', order: ++order, turn: 2, at: [GH.x0, 0, GH.z0],
      note: 'The walled court. Its floor carries the six arrival stones and the gold line; the gate gap is left open for the portcullis.' });
    add({ key: 'portcullis', sculpture: 'gatehouse-portcullis', by: 'plugin', turn: 2, at: [GH.gate.x0, GH.gate.y0, GH.gate.z],
      note: 'Never placed with /sculpt: RealmArrival builds these cells with /arrival admin gate build (cells.gate) and takes them down row by row.' });
    add({ key: 'processional-a', sculpture: 'processional-a', by: 'sculptor', order: ++order, turn: 2, at: [-AVENUE.half, 0, AVENUE.a.z0], optional: true,
      note: 'Skip both halves if the ground is not flat to within one block over 45 m.' });
    const monumentSize = (turn) => {
      const all = HOUSE_KEYS.map((h) => size(`house-${h}`, turn));
      return [all[0][0], Math.max(...all.map((s) => s[1])), all[0][2]];
    };
    const pair = (n, z0) => {
      for (const side of ['left', 'right']) {
        const slot = `p${n}-${side}`, turn = MONUMENT_TURN[side];
        const ms = monumentSize(turn);
        const mx = side === 'left' ? -AVENUE.monumentFront - ms[0] + 1 : AVENUE.monumentFront;
        add({ key: `${slot}-monument`, sculpture: 'house-{house}', slot, by: 'sculptor', order: ++order, turn, at: [mx, 0, z0], size: ms });
        const sx = (side === 'left' ? -AVENUE.pledgeX : AVENUE.pledgeX) - 1;
        add({ key: `${slot}-stone`, sculpture: 'pledge-stone-{house}', slot, by: 'sculptor', order: ++order, turn: STONE_TURN, at: [sx, 0, z0 + AVENUE.pledgeDz - 1], size: [3, 2, 3] });
      }
    };
    pair(1, AVENUE.pairs[0]);
    add({ key: 'processional-b', sculpture: 'processional-b', by: 'sculptor', order: ++order, turn: 2, at: [-AVENUE.half, 0, AVENUE.b.z0], optional: true });
    pair(2, AVENUE.pairs[1]);
    pair(3, AVENUE.pairs[2]);
    add({ key: 'pillar', sculpture: 'heralds-pillar', by: 'sculptor', order: ++order, turn: 0, at: [PILLAR.x0, 0, PILLAR.z0],
      note: 'Placed before the ring so its stand spot is on bare ground. Off the axis, so it never hides the throne from the gate. The waystone the-hearth is marked with it (/travel admin mark).' });
    add({ key: 'hearth-ring', sculpture: 'hearth-ring', by: 'sculptor', order: ++order, turn: 2, at: [HEARTH.x - HEARTH.half, 0, HEARTH.z - HEARTH.half],
      note: 'Centred on the fire. The staff fire pit goes in the open middle; the ember band sits on the dais (cells.emberBand).' });
    add({ key: 'wayboard', sculpture: 'wayboard', by: 'sculptor', order: ++order, turn: 0, at: [WAYBOARD.x0, 0, WAYBOARD.z0] });
    add({ key: 'old-throne', sculpture: 'old-throne', by: 'context', turn: 0, at: [THRONE.x0, THRONE.lift, THRONE.z0],
      note: 'Context for previews only: the Old Throne (L01) on its hill, wherever it really stands. The site is laid out so gate, fire and throne line up.' });

    // Plugin-owned and plugin-read cells.
    const { gate } = GH;
    const rows = [];
    for (let y = gate.y1; y >= gate.y0; y--) { const r = []; for (let x = gate.x0; x <= gate.x1; x++) r.push([x, y, gate.z]); rows.push(r); }
    const bandY = HEARTH.bandY;
    const cells = {
      gate: { piece: 'portcullis', owner: 'RealmArrival', command: '/arrival admin gate build', material: 'reinforced', colour: pal('Iron 900'),
        note: 'Rows top first, each left to right as seen from the court. Opening removes one row every 0.4 s (material 0, collectPreviousCube false); closing puts them back bottom first.',
        rows },
      emberBand: { owner: 'RealmArrival', command: '/arrival admin beacon build 5', material: 'clay', rest: pal('Ember deep'), flare: pal('Ember hot'), restsOn: 'hearth-ring',
        centre: [HEARTH.x, 0, HEARTH.z], radius: 5,
        rule: 'hearth centre + (dx, 2, dz) for max(|dx|,|dz|) <= 5 and |dx|+|dz| <= 6, but not max <= 4 and |dx|+|dz| <= 5: an octagon of 24 cells',
        cells: BAND.map(([dx, dz]) => [HEARTH.x + dx, bandY, HEARTH.z + dz]) },
      goldLine: { piece: 'gatehouse', note: 'The 5 x 1 inlay in the floor (clay, Ember hot). Z1 is centred over it.', cells: range(GH.goldLine.x0, GH.goldLine.x1).map((x) => [x, 0, GH.goldLine.z]) },
      stoneFloors: { piece: 'gatehouse', note: 'The inlay under each arrival stone, A1 to A6. The self-check (GetCubeInfoAtLocal) closes the arrival if one is missing.',
        cells: GH.stones.map(([, x, z]) => [x, 0, z]) },
    };

    // Points: the cell a player's feet are in. World position = ((x + 0.5) * 1.2, y * 1.2, (z + 0.5) * 1.2) in the site frame.
    const points = {};
    for (const [name, x, z] of GH.stones) points[name] = { kind: 'stone', cell: [x, 1, z], note: name === 'A1' ? 'Arrival stones: teleport to the cell + 0.5 m up. A1-A3 are the row nearer the gate.' : undefined };
    points.threshold = { kind: 'trigger', cell: [0, 1, GH.goldLine.z], note: 'Over the middle of the gold line (/arrival admin threshold set).' };
    points.gateSet = { kind: 'gate', cell: [gate.x0, gate.y0, gate.z], facing: '+z', note: '/arrival admin gate set 5 6: stand in the bottom-left cell of the empty opening (before gate build), facing out. It is the first cell of the gate\'s bottom row.' };
    points.E = { kind: 'eject', cell: [0, 1, 26], note: 'On the processional 4 m outside the gate (/arrival admin eject set).' };
    for (let n = 1; n <= 3; n++) for (const side of ['left', 'right']) {
      const slot = `p${n}-${side}`;
      points[`banner.${slot}`] = { kind: 'banner', slot, cell: [side === 'left' ? -AVENUE.pledgeX : AVENUE.pledgeX, 2, AVENUE.pairs[n - 1] + AVENUE.pledgeDz], note: slot === 'p1-left' ? 'On the raised centre of each pledge stone (/arrival admin banner set <house>, with the house drawn for that slot).' : undefined };
    }
    points.hearth = { kind: 'fire', cell: [HEARTH.x, 0, HEARTH.z], note: 'The Hearth centre (/arrival admin hearth set), in the middle of the open pit before the fire pit is built. Must be within 3 m of the RealmQuests place the_hearth and the RealmLaws zone Hearth.' };
    points.M1 = { kind: 'mercy', cell: [HEARTH.x - 6, 2, HEARTH.z], note: 'Mercy stones on the dais rim, outside the ember band (/arrival admin mercy add).' };
    points.M2 = { kind: 'mercy', cell: [HEARTH.x + 6, 2, HEARTH.z] };
    points.M3 = { kind: 'mercy', cell: [HEARTH.x, 2, HEARTH.z - 6] };
    points.wayboard = { kind: 'trigger', cell: [0, 0, WAYBOARD.z0 - 2], note: 'In front of the wayboard (/arrival admin wayboard set).' };
    points.pilgrimLedge = { kind: 'exit', cell: [GH.pilgrim.ledge.x, GH.pilgrim.ledge.y + 1, GH.pilgrim.ledge.z0], note: 'The Pilgrim\'s ledge: up the two steps in the left tower; the drop pad is below it outside.' };
    for (const k of Object.keys(points)) if (points[k].note === undefined) delete points[k].note;

    const boxes = {
      Z0: { name: 'hall box', min: [GH.x0, 0, GH.z0], max: [GH.x1, GH.towerTop, GH.z1], note: 'The whole Gatehouse footprint, walls and towers included (sanctuary, eviction).' },
      Z0b: { name: 'drop pad', min: [GH.x0 - 3, 0, GH.towerZ0 - 1], max: [GH.x0 - 1, 3, GH.z1 + 1], note: 'Outside the left wall under the Pilgrim\'s ledge.' },
    };
    const zones = [
      { key: 'Z0', name: 'hall box', box: 'Z0' },
      { key: 'Z0b', name: 'drop pad', box: 'Z0b' },
      { key: 'E', name: 'eject point', point: 'E' },
      { key: 'Z1', name: 'gold line', point: 'threshold', radiusM: 2.5 },
      { key: 'Z2', name: 'banners', point: 'banner.*', radiusM: 9 },
      { key: 'Z2p', name: 'pledge stones', point: 'banner.*', radiusM: 1.5, dwellSeconds: 3 },
      { key: 'Z3q', name: 'Hearth quiet ring', point: 'hearth', radiusM: 40 },
      { key: 'Z3', name: 'Hearth stone', point: 'hearth', radiusM: 12 },
      { key: 'Z4', name: 'wayboard', point: 'wayboard', radiusM: 8 },
      { key: 'Z5', name: 'Hearth outer', point: 'hearth', radiusM: 70 },
      { key: 'corridor', name: 'route corridor', axis: [[0, 0, GH.z0], [0, 0, WAYBOARD.z0 + 1]], halfWidthM: 20, note: 'Plus the hall box and Z5.' },
    ];

    // Sign spots (4.3): the air cell the staff sign stands in, and which way its face looks.
    const bay = (i) => WAYBOARD.x0 + Math.round((BAYS[i][0] + BAYS[i][1]) / 2);
    const signs = [
      { key: 'G1', cell: [0, 2, GH.z0 + 2], faces: '+z', binding: 'art the-crossing', note: 'Back wall, behind the stones.' },
      { key: 'G2', cell: [-4, 2, GH.z0 + 2], faces: '+z', binding: 'board chronicle', note: 'Back wall: the Chronicle Wall.' },
      { key: 'G3', cell: [4, 2, GH.z0 + 2], faces: '+z', binding: 'notice', text: 'The Unwritten | You have no page in the Chronicle yet. Walk through the gate, and the Chronicle opens.' },
      { key: 'G4', cell: [-4, 2, GH.z1 - 2], faces: '-z', binding: 'art poster-welcome', note: 'Inner face of the front wall, left of the gate.' },
      ...HOUSE_SLOTS.map((slot, i) => {
        const n = Math.floor(i / 2), left = slot.endsWith('left');
        return { key: `P${i + 1}`, slot, cell: [left ? -AVENUE.pledgeX - 1 : AVENUE.pledgeX + 1, 0, AVENUE.pairs[n] + AVENUE.pledgeDz - 3], faces: left ? '+x' : '-x', binding: 'art crest-{house}', note: i === 0 ? 'A post beside each pledge stone, on its gate side.' : undefined };
      }),
      { key: 'W1', cell: [bay(0), 1, WAYBOARD.z0], faces: '-z', binding: 'notice', text: 'Three Roads | Crown Market: coin and contracts. Listing Field: the Ring. The seats: the houses. /road shows the way.' },
      { key: 'W2', cell: [bay(1), 1, WAYBOARD.z0], faces: '-z', binding: 'board proclamation' },
      { key: 'W3', cell: [bay(2), 1, WAYBOARD.z0], faces: '-z', binding: 'board event' },
      { key: 'W4', cell: [bay(3), 1, WAYBOARD.z0], faces: '-z', binding: 'board standings' },
      { key: 'W5', cell: [bay(4), 1, WAYBOARD.z0], faces: '-z', binding: 'notice', text: 'Other Banners | Houses beyond the six recruit too. /house list names them; /raven <house> <letter> asks.' },
      { key: 'H1', cell: [4, 0, HEARTH.z - HEARTH.half - 2], faces: '-z', binding: 'notice', text: 'The Hearth | Raise a crest before you build. Log off behind walls. Nights are dark: carry a torch.', note: 'Facing the avenue end, beside the path.' },
    ].map((s) => { if (s.note === undefined) delete s.note; return s; });

    return {
      format: 'realm-site/1',
      id: 'arrival',
      name: this.name,
      description: this.description,
      source: 'art/sculptures/src/sites/arrival.mjs',
      design: 'docs/arrival-design.md',
      cellMetres: CELL_M,
      frame: {
        origin: 'cell [0, 0, 0] is the middle of the Gatehouse back wall\'s outer face, in the floor layer',
        x: 'across the axis; +x is on the right of someone walking from the gate to the fire',
        y: 'up; every piece\'s bottom layer is y 0 (the cell an admin\'s feet are in on bare ground)',
        z: 'along the axis: back wall (0), gate (21-22), avenue, fire (107), wayboard (122), toward the Old Throne',
      },
      lot: {
        rule: 'Which house stands in which slot is drawn by lot in public at every build (after every wipe). The two houses of a pair face each other across the road.',
        slots: HOUSE_SLOTS,
        houses: HOUSE_KEYS,
        preview: PREVIEW_DRAW,
      },
      pieces,
      cells,
      points,
      boxes,
      zones,
      signs,
      terrain: [
        { kind: 'mound', previewOnly: true, centre: [0, THRONE.z0 + 8], radius: 34, top: 12, height: THRONE.lift, colour: house('merrin', 'fieldLight'), note: 'A stand-in for the throne hill in previews.' },
        { kind: 'fire', previewOnly: true, centre: [HEARTH.x, HEARTH.z], colour: pal('Ember hot'), ember: pal('Afterglow'), note: 'A stand-in for the staff-built fire pit in previews.' },
      ],
    };
  },
};

function range(a, b) { const out = []; for (let i = a; i <= b; i++) out.push(i); return out; }
