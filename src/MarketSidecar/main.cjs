const readline = require('node:readline');

const ccxtPackage = require('ccxt');
const ccxtStreaming = ccxtPackage.pro ?? ccxtPackage;
const PROTOCOL_VERSION = 1;

const exchangeOptions = {
  okx: { options: { defaultType: 'spot' } },
  bingx: { options: { defaultType: 'spot' } },
  cryptocom: { options: { defaultType: 'spot' } },
};

const requestHandlers = {
  getMarkets: handleGetMarkets,
  setSubscriptions: handleSetSubscriptions,
  ping: handlePing,
  shutdown: handleShutdown,
};

let streamGeneration = 0;
const activeStreamingExchanges = new Map();

const stdin = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
});

stdin.on('line', (line) => {
  void processLine(line);
});

stdin.on('close', () => {
  void shutdown(0);
});

process.on('SIGTERM', () => {
  void shutdown(0);
});

process.on('SIGINT', () => {
  void shutdown(0);
});

writeMessage({ type: 'ready' });

async function processLine(line) {
  if (!line || !line.trim()) {
    return;
  }

  let message;
  try {
    message = JSON.parse(line);
  } catch (error) {
    writeMessage({
      type: 'error',
      scope: 'protocol',
      message: error instanceof Error ? error.message : String(error),
    });
    return;
  }

  if (!message || typeof message !== 'object' || Array.isArray(message)) {
    writeMessage({ type: 'error', scope: 'protocol', message: 'Expected a protocol object.' });
    return;
  }
  if (message.protocolVersion !== PROTOCOL_VERSION) {
    writeMessage({
      id: message.id,
      type: 'error',
      scope: 'protocol',
      message: `Unsupported protocol version: ${String(message.protocolVersion ?? 'missing')}.`,
    });
    return;
  }

  const { id, type } = message;
  const handler = Object.hasOwn(requestHandlers, type) ? requestHandlers[type] : null;
  if (!handler) {
    writeMessage({
      id,
      type: 'error',
      scope: 'protocol',
      message: `Unknown message type: ${type}`,
    });
    return;
  }

  try {
    await handler(message);
  } catch (error) {
    writeMessage({
      id,
      type: 'error',
      scope: type,
      message: error instanceof Error ? error.message : String(error),
    });
  }
}

async function handlePing(message) {
  writeMessage({
    id: message.id,
    type: 'ack',
  });
}

async function handleShutdown(message) {
  writeMessage({
    id: message.id,
    type: 'ack',
  });

  await shutdown(0);
}

async function handleGetMarkets(message) {
  const exchangeId = normalizeText(message.venueId);
  if (!exchangeId) {
    writeMessage({
      id: message.id,
      type: 'error',
      scope: 'getMarkets',
      message: 'venueId is required.',
    });
    return;
  }

  const exchange = createExchange(exchangeId, false);
  if (!exchange) {
    writeMessage({
      id: message.id,
      type: 'error',
      scope: 'getMarkets',
      venueId: exchangeId,
      message: 'Exchange is not supported by the sidecar.',
    });
    return;
  }

  try {
    await loadMarketsWithRetry(exchange);
    const markets = buildSpotMarkets(exchange, exchangeId);
    writeMessage({
      id: message.id,
      type: 'markets',
      markets,
    });
  } finally {
    await safeClose(exchange);
  }
}

async function loadMarketsWithRetry(exchange) {
  for (let attempt = 0; ; attempt += 1) {
    try {
      await exchange.loadMarkets(attempt > 0);
      return;
    } catch (error) {
      const errorName = error?.constructor?.name;
      const isTransient = errorName === 'NetworkError'
        || errorName === 'RequestTimeout'
        || errorName === 'ExchangeNotAvailable';
      if (!isTransient || attempt >= 6) {
        throw error;
      }

      await delay(Math.min(250 * (2 ** attempt), 2000));
    }
  }
}

async function handleSetSubscriptions(message) {
  const markets = Array.isArray(message.markets) ? message.markets : [];
  streamGeneration += 1;
  const generation = streamGeneration;

  await closeAllStreamingExchanges();

  const grouped = new Map();
  for (const market of markets) {
    const exchangeId = normalizeText(market?.venueId);
    const symbol = normalizeText(market?.symbol);
    const kind = normalizeText(market?.kind);
    if (!exchangeId || !symbol || kind !== 'spot') {
      continue;
    }

    if (!grouped.has(exchangeId)) {
      grouped.set(exchangeId, new Set());
    }

    grouped.get(exchangeId).add(symbol);
  }

  writeMessage({
    id: message.id,
    type: 'ack',
  });

  for (const [exchangeId, symbolsSet] of grouped.entries()) {
    const symbols = Array.from(symbolsSet.values());
    void runExchangeStream(exchangeId, symbols, generation);
  }
}

async function runExchangeStream(exchangeId, symbols, generation) {
  let reconnectAttempt = 0;
  while (generation === streamGeneration) {
    const forcePolling = exchangeId === 'bingx';
    const exchange = createExchange(exchangeId, !forcePolling);
    if (!exchange) {
      writeMessage({
        type: 'error',
        scope: 'stream',
        venueId: exchangeId,
        message: 'Exchange is not supported by the sidecar.',
      });
      return;
    }

    activeStreamingExchanges.set(exchangeId, exchange);

    try {
      await exchange.loadMarkets();
      await seedPrices(exchange, exchangeId, symbols, generation);

      if (forcePolling) {
        await pollTickers(exchange, exchangeId, symbols, generation);
        return;
      }

      if (await tryWatchManyTickers(exchange, exchangeId, symbols, generation)) {
        return;
      }

      if (await tryWatchSingleTickers(exchange, exchangeId, symbols, generation)) {
        return;
      }

      await pollTickers(exchange, exchangeId, symbols, generation);
    } catch (error) {
      if (generation === streamGeneration) {
        writeReconnecting(exchangeId, 'stream', error);
      }
    } finally {
      const current = activeStreamingExchanges.get(exchangeId);
      if (current === exchange) {
        activeStreamingExchanges.delete(exchangeId);
      }

      await safeClose(exchange);
    }

    if (generation === streamGeneration) {
      writeReconnecting(exchangeId, 'stream', 'Market stream ended unexpectedly.');
      await delay(reconnectDelay(reconnectAttempt));
      reconnectAttempt += 1;
    }
  }
}

async function seedPrices(exchange, exchangeId, symbols, generation) {
  if (symbols.length === 0 || generation !== streamGeneration) {
    return;
  }

  try {
    if (symbols.length > 1 && typeof exchange.fetchTickers === 'function') {
      const tickers = await exchange.fetchTickers(symbols);
      emitTickers(exchangeId, tickers?.tickers ?? tickers, generation, 'seed');
      return;
    }
  } catch {
  }

  for (const symbol of symbols) {
    if (generation !== streamGeneration) {
      return;
    }

    try {
      const ticker = await exchange.fetchTicker(symbol);
      emitTicker(exchangeId, ticker, generation, 'seed');
    } catch {
    }
  }
}

async function tryWatchManyTickers(exchange, exchangeId, symbols, generation) {
  if (symbols.length === 0 || !supportsCapability(exchange, 'watchTickers') || typeof exchange.watchTickers !== 'function') {
    return false;
  }

  while (generation === streamGeneration) {
    try {
      const tickers = await exchange.watchTickers(symbols);
      if (emitTickers(exchangeId, tickers?.tickers ?? tickers, generation, 'stream') === 0) {
        return false;
      }
    } catch (error) {
      if (generation === streamGeneration) {
        writeReconnecting(exchangeId, 'watchTickers', error);
      }
      return false;
    }
  }

  return true;
}

async function tryWatchSingleTickers(exchange, exchangeId, symbols, generation) {
  if (symbols.length === 0 || !supportsCapability(exchange, 'watchTicker') || typeof exchange.watchTicker !== 'function') {
    return false;
  }

  const tasks = symbols.map((symbol) => watchSingleTicker(exchange, exchangeId, symbol, generation));
  const results = await Promise.all(tasks);
  return results.some(Boolean);
}

async function watchSingleTicker(exchange, exchangeId, symbol, generation) {
  let streamed = false;
  let reconnectAttempt = 0;

  while (generation === streamGeneration) {
    try {
      const ticker = await exchange.watchTicker(symbol);
      if (!emitTicker(exchangeId, ticker, generation, 'stream')) {
        await pollTicker(exchange, exchangeId, symbol, generation);
        return true;
      }

      streamed = true;
      reconnectAttempt = 0;
    } catch (error) {
      if (generation !== streamGeneration) {
        break;
      }

      writeReconnecting(exchangeId, 'watchTicker', error, symbol);
      if (!streamed) {
        await pollTicker(exchange, exchangeId, symbol, generation);
        return true;
      }

      await delay(reconnectDelay(reconnectAttempt));
      reconnectAttempt += 1;
    }
  }

  return streamed;
}

async function pollTicker(exchange, exchangeId, symbol, generation) {
  while (generation === streamGeneration) {
    try {
      const ticker = await exchange.fetchTicker(symbol);
      emitTicker(exchangeId, ticker, generation, 'poll');
    } catch (error) {
      if (generation === streamGeneration) {
        writeReconnecting(exchangeId, 'poll', error, symbol);
      }
    }

    await delay(5000);
  }
}

async function pollTickers(exchange, exchangeId, symbols, generation) {
  while (generation === streamGeneration) {
    try {
      if (symbols.length > 1 && typeof exchange.fetchTickers === 'function') {
        const tickers = await exchange.fetchTickers(symbols);
        emitTickers(exchangeId, tickers?.tickers ?? tickers, generation, 'poll');
      } else {
        for (const symbol of symbols) {
          const ticker = await exchange.fetchTicker(symbol);
          emitTicker(exchangeId, ticker, generation, 'poll');
        }
      }
    } catch (error) {
      if (generation === streamGeneration) {
        writeReconnecting(exchangeId, 'poll', error);
      }
    }

    await delay(5000);
  }
}

function emitTickers(exchangeId, tickers, generation, transport) {
  if (generation !== streamGeneration || !tickers) {
    return 0;
  }

  let emitted = 0;
  if (tickers instanceof Map) {
    for (const [, ticker] of tickers.entries()) {
      emitted += emitTicker(exchangeId, ticker, generation, transport) ? 1 : 0;
    }
    return emitted;
  }

  for (const ticker of Object.values(tickers)) {
    emitted += emitTicker(exchangeId, ticker, generation, transport) ? 1 : 0;
  }

  return emitted;
}

function emitTicker(exchangeId, ticker, generation, transport) {
  if (generation !== streamGeneration || !ticker) {
    return false;
  }

  const symbol = normalizeText(ticker.symbol);
  const last = toNumber(ticker.last);
  const market = buildMarketIdentity(exchangeId, symbol);
  if (!market || last == null || last <= 0) {
    return false;
  }

  writeMessage({
    type: 'priceUpdate',
    market,
    source: {
      id: `ccxt:${exchangeId}`,
      providerId: 'ccxt',
      venueId: exchangeId,
      transport,
    },
    price: {
      value: last,
    },
    timing: {
      sourceAt: toIsoTimestamp(ticker.timestamp),
      receivedAt: new Date().toISOString(),
    },
  });
  return true;
}

function buildSpotMarkets(exchange, exchangeId) {
  const markets = Object.values(exchange.markets ?? {});
  const normalized = [];

  for (const market of markets) {
    if (!market) {
      continue;
    }

    if (market.spot === true || normalizeText(market.type) === 'spot') {
      const symbol = normalizeText(market.symbol);
      const identity = buildMarketIdentity(exchangeId, symbol, market.base, market.quote);
      if (identity) {
        normalized.push(identity);
      }
    }
  }

  normalized.sort((left, right) => left.symbol.localeCompare(right.symbol, 'en', { sensitivity: 'base' }));

  const deduped = [];
  let previous = null;
  for (const market of normalized) {
    if (market.id !== previous) {
      deduped.push(market);
      previous = market.id;
    }
  }

  return deduped;
}

function buildMarketIdentity(exchangeId, symbol, baseValue, quoteValue) {
  const normalizedExchangeId = normalizeText(exchangeId).toLowerCase();
  const normalizedSymbol = normalizeText(symbol);
  const [symbolBase, symbolQuoteWithSettlement] = normalizedSymbol.split('/');
  const symbolQuote = normalizeText(symbolQuoteWithSettlement).split(':')[0];
  const base = normalizeText(baseValue) || normalizeText(symbolBase);
  const quote = normalizeText(quoteValue) || symbolQuote;
  if (!normalizedExchangeId || !normalizedSymbol || !base || !quote) {
    return null;
  }

  const baseSymbol = base.toUpperCase();
  const quoteSymbol = quote.toUpperCase();
  return {
    id: `cex:${normalizedExchangeId}:spot:${normalizedSymbol.toUpperCase()}`,
    symbol: normalizedSymbol,
    kind: 'spot',
    baseAsset: buildVenueAsset(normalizedExchangeId, baseSymbol),
    quoteAsset: buildVenueAsset(normalizedExchangeId, quoteSymbol),
    venue: {
      id: normalizedExchangeId,
      kind: 'centralizedExchange',
    },
  };
}

function buildVenueAsset(exchangeId, symbol) {
  return {
    id: `cex:${exchangeId}:asset:${symbol}`,
    symbol,
    kind: 'venueAsset',
    chainId: null,
    address: null,
  };
}

function supportsCapability(exchange, key) {
  const value = exchange?.has?.[key];
  return value === true || value === 'emulated';
}

function createExchange(exchangeId, streaming) {
  const exchangeClass = (streaming ? ccxtStreaming : ccxtPackage)[exchangeId];
  if (typeof exchangeClass !== 'function') {
    return null;
  }

  const options = {
    enableRateLimit: true,
    ...(exchangeOptions[exchangeId] ?? {}),
  };

  const exchange = new exchangeClass(options);
  if (exchangeId === 'bingx' && typeof exchange.fetchSpotMarkets === 'function') {
    exchange.fetchMarkets = exchange.fetchSpotMarkets.bind(exchange);
  }

  return exchange;
}

async function closeAllStreamingExchanges() {
  const exchanges = Array.from(activeStreamingExchanges.values());
  activeStreamingExchanges.clear();

  for (const exchange of exchanges) {
    await safeClose(exchange);
  }
}

async function safeClose(exchange) {
  if (!exchange || typeof exchange.close !== 'function') {
    return;
  }

  try {
    await exchange.close();
  } catch {
  }
}

function writeMessage(message) {
  process.stdout.write(`${JSON.stringify({ ...message, protocolVersion: PROTOCOL_VERSION })}\n`);
}

function normalizeText(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function toNumber(value) {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  return null;
}

function toIsoTimestamp(value) {
  const timestamp = toNumber(value);
  if (timestamp == null || timestamp <= 0) {
    return null;
  }

  const date = new Date(timestamp);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function delay(milliseconds) {
  return new Promise((resolve) => {
    setTimeout(resolve, milliseconds);
  });
}

function reconnectDelay(failedAttempt) {
  return Math.min(15000, 1000 * (2 ** Math.min(failedAttempt, 4)));
}

function writeReconnecting(exchangeId, scope, error, symbol) {
  writeMessage({
    type: 'reconnecting',
    scope,
    venueId: exchangeId,
    ...(symbol ? { symbol } : {}),
    message: error instanceof Error ? error.message : String(error),
  });
}

async function shutdown(code) {
  await closeAllStreamingExchanges();
  process.exit(code);
}
