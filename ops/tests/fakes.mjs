// Stand-ins used only by ops/tests/Run-OpsTests.ps1. No dependencies (Node 18+).
//   node fakes.mjs a2s <port> <info|challenge|garbage>   UDP server answering A2S_INFO like a Reign of Kings server
//   node fakes.mjs tcp <port>                             TCP listener (the game's ping port)
//   node fakes.mjs webhook <port> <logfile> <ok|429once|404>  Discord-like webhook endpoint, logs each JSON body
//   node fakes.mjs file <port> <path>                     serves one file at any URL
//   node fakes.mjs touch <file> <everyMs> <forMs>         keeps rewriting a file (a server saving mid-backup)
// Each server prints "ready" on stdout once listening.
import dgram from 'node:dgram';
import net from 'node:net';
import http from 'node:http';
import fs from 'node:fs';

const [, , cmd, a, b, c] = process.argv;

function cstr(s) {
  return Buffer.concat([Buffer.from(s, 'utf8'), Buffer.from([0])]);
}

if (cmd === 'a2s') {
  const port = Number(a);
  const mode = b || 'info';
  const challenge = Buffer.from([0x11, 0x22, 0x33, 0x44]);
  const sock = dgram.createSocket('udp4');
  sock.on('message', (msg, rinfo) => {
    const isInfo = msg.length >= 25 && msg.readUInt32LE(0) === 0xffffffff && msg[4] === 0x54;
    if (!isInfo) return;
    if (mode === 'garbage') {
      sock.send(Buffer.from([0xff, 0xff, 0xff, 0xff, 0x99, 1, 2]), rinfo.port, rinfo.address);
      return;
    }
    const tail = msg.subarray(25);
    if (mode === 'challenge' && !(tail.length === 4 && tail.equals(challenge))) {
      sock.send(Buffer.concat([Buffer.from([0xff, 0xff, 0xff, 0xff, 0x41]), challenge]), rinfo.port, rinfo.address);
      return;
    }
    // What the RoK server reports (docs/join-and-scale.md 3.1): fixed name, max players 0.
    const body = Buffer.concat([
      Buffer.from([0xff, 0xff, 0xff, 0xff, 0x49, 17]),
      cstr('Another ROK Server'), cstr(''), cstr('rok'), cstr('ROK Server'),
      Buffer.from([0x00, 0x00]), // app id 0 (short)
      Buffer.from([7, 0, 0, 'd'.charCodeAt(0), 'w'.charCodeAt(0), 0, 1]),
      cstr('1.0.0.0')
    ]);
    sock.send(body, rinfo.port, rinfo.address);
  });
  sock.bind(port, '127.0.0.1', () => console.log('ready'));
} else if (cmd === 'tcp') {
  const srv = net.createServer((s) => s.end());
  srv.listen(Number(a), '127.0.0.1', () => console.log('ready'));
} else if (cmd === 'webhook') {
  const port = Number(a);
  const log = b;
  const mode = c || 'ok';
  let n = 0;
  const srv = http.createServer((req, res) => {
    const chunks = [];
    req.on('data', (d) => chunks.push(d));
    req.on('end', () => {
      n++;
      fs.appendFileSync(log, JSON.stringify({ n, method: req.method, url: req.url, ct: req.headers['content-type'], body: Buffer.concat(chunks).toString('utf8') }) + '\n');
      if (mode === '429once' && n === 1) {
        res.writeHead(429, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ message: 'You are being rate limited.', retry_after: 0.3, global: false }));
        return;
      }
      if (mode === '404') {
        res.writeHead(404, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ message: 'Unknown Webhook', code: 10015 }));
        return;
      }
      res.writeHead(204);
      res.end();
    });
  });
  srv.listen(port, '127.0.0.1', () => console.log('ready'));
} else if (cmd === 'file') {
  const data = fs.readFileSync(b);
  http.createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'application/zip', 'content-length': data.length });
    res.end(data);
  }).listen(Number(a), '127.0.0.1', () => console.log('ready'));
} else if (cmd === 'touch') {
  const every = Number(b);
  const until = Date.now() + Number(c);
  let i = 0;
  const t = setInterval(() => {
    fs.writeFileSync(a, 'save ' + i++ + ' ' + 'x'.repeat(1000 + (i % 7)));
    if (Date.now() > until) clearInterval(t);
  }, every);
  console.log('ready');
} else {
  console.error('unknown command');
  process.exit(2);
}
