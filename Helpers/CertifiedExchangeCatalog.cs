using System;

namespace TrenchHQ.Helpers
{
    internal sealed class CertifiedExchange(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
    }

    internal static class CertifiedExchangeCatalog
    {
        private static readonly CertifiedExchange[] Exchanges =
        [
            new("binance", "Binance"),
            new("bybit", "Bybit"),
            new("okx", "OKX"),
            new("gate", "Gate"),
            new("kucoin", "KuCoin"),
            new("bitget", "Bitget"),
            new("bingx", "BingX"),
            new("htx", "HTX"),
            new("mexc", "MEXC"),
            new("cryptocom", "Crypto.com"),
            new("coinex", "CoinEx"),
            new("hashkey", "HashKey Global"),
            new("woo", "WOO X")
        ];

        internal static CertifiedExchange[] GetAll() => Exchanges;

        internal static string GetDisplayName(string exchangeId)
        {
            foreach (var exchange in Exchanges)
            {
                if (string.Equals(exchange.Id, exchangeId, StringComparison.OrdinalIgnoreCase))
                {
                    return exchange.Name;
                }
            }

            return exchangeId;
        }

        internal static string? GetExchangeId(string exchangeNameOrId)
        {
            if (string.IsNullOrWhiteSpace(exchangeNameOrId))
            {
                return null;
            }

            foreach (var exchange in Exchanges)
            {
                if (string.Equals(exchange.Id, exchangeNameOrId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(exchange.Name, exchangeNameOrId, StringComparison.OrdinalIgnoreCase))
                {
                    return exchange.Id;
                }
            }

            return null;
        }
    }
}
