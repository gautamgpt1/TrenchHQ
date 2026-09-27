const { test } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const path = require('node:path');

test('production sidecar rejects private operations, inherited handlers and malformed frames', async () => {
  const child = spawn(process.execPath, [path.join(__dirname, '../src/MarketSidecar/dist/sidecar.bundle.cjs')],
    { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
  async function next() {
    let timer;
    try { return JSON.parse((await Promise.race([lines.next(), new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('Sidecar response timed out')), 5000);
    })])).value); } finally { clearTimeout(timer); }
  }
  try {
    assert.equal((await next()).type, 'ready');
    for (const type of ['createOrder', 'withdraw', 'fetchBalance', 'constructor', '__proto__']) {
      child.stdin.write(JSON.stringify({ protocolVersion: 1, id: type, type }) + '\n');
      const reply = await next();
      assert.equal(reply.type, 'error'); assert.equal(reply.scope, 'protocol');
    }
    for (const frame of ['null', '[]', '"invalid"']) {
      child.stdin.write(frame + '\n'); assert.equal((await next()).type, 'error');
    }
    child.stdin.write('{"protocolVersion":1,"id":"ping","type":"ping"}\n');
    assert.equal((await next()).type, 'ack');
  } finally { child.stdin.end(); child.kill(); }
});
