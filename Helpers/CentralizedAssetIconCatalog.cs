using System;
using System.IO;

namespace TrenchHQ.Helpers
{
    internal static class CentralizedAssetIconCatalog
    {
        internal static string? GetBaseAssetSymbol(string? marketSymbol)
        {
            if (string.IsNullOrWhiteSpace(marketSymbol))
            {
                return null;
            }

            var separator = marketSymbol.IndexOf('/');
            if (separator <= 0)
            {
                return null;
            }

            var symbol = marketSymbol[..separator].Trim().ToLowerInvariant();
            if (symbol.Length == 0 || symbol.IndexOfAny(['\\', '/', '.', ':']) >= 0)
            {
                return null;
            }

            return symbol switch
            {
                "xbt" => "btc",
                "xdg" => "doge",
                _ => symbol
            };
        }

        internal static string? GetIconUri(string? marketSymbol, string? baseDirectory = null)
        {
            var symbol = GetBaseAssetSymbol(marketSymbol);
            if (symbol == null)
            {
                return null;
            }

            var root = baseDirectory ?? AppContext.BaseDirectory;
            var iconPath = Path.Combine(root, "Assets", "Crypto", $"{symbol}.png");
            return File.Exists(iconPath)
                ? $"ms-appx:///Assets/Crypto/{Uri.EscapeDataString(symbol)}.png"
                : null;
        }
    }
}
