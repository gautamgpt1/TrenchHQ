const readline = require('node:readline');
const PROTOCOL_VERSION = 1;

const pendingTimers = new Set();
let subscriptionRequestCount = 0;

const stdin = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
});

stdin.on('line', (line) => {
  if (!line || !line.trim()) {
    return;
  }

  const message = JSON.parse(line);
  if (message.protocolVersion !== PROTOCOL_VERSION) {
    writeMessage({ id: message.id, type: 'error', scope: 'protocol', message: 'Unsupported protocol version.' });
    return;
  }

  if (message.type === 'setSubscriptions') {
    subscriptionRequestCount += 1;
    writeMessage({ id: message.id, type: 'ack' });

    const markets = Array.isArray(message.markets) ? message.markets : [];
    const timer = setTimeout(() => {
      pendingTimers.delete(timer);
      for (const market of markets) {
        const symbol = market.symbol;
        const venueId = market.venueId;
        const [base, quote] = symbol.split('/');
        writeMessage({
          type: 'priceUpdate',
          market: {
            id: `cex:${venueId}:spot:${symbol}`,
            symbol,
            kind: 'spot',
            baseAsset: {
              id: `cex:${venueId}:asset:${base}`,
              symbol: base,
              kind: 'venueAsset',
              chainId: null,
              address: null,
            },
            quoteAsset: {
              id: `cex:${venueId}:asset:${quote}`,
              symbol: quote,
              kind: 'venueAsset',
              chainId: null,
              address: null,
            },
            venue: { id: venueId, kind: 'centralizedExchange' },
          },
          source: {
            id: `fake:${venueId}`,
            providerId: 'fake',
            venueId,
            transport: 'stream',
          },
          price: { value: 100 + subscriptionRequestCount },
          timing: {
            sourceAt: null,
            receivedAt: new Date().toISOString(),
          },
        });
      }
    }, 750);
    pendingTimers.add(timer);
    return;
  }

  if (message.type === 'shutdown') {
    writeMessage({ id: message.id, type: 'ack' });
    setTimeout(() => process.exit(0), 25);
    return;
  }

  writeMessage({ id: message.id, type: 'ack' });
});

stdin.on('close', () => process.exit(0));

writeMessage({ type: 'ready' });

function writeMessage(message) {
  process.stdout.write(`${JSON.stringify({ ...message, protocolVersion: PROTOCOL_VERSION })}\n`);
}
