using System;

namespace TrenchHQ.Core.Wallets
{
    internal static class WalletActivityAgeRules
    {
        internal static string Format(long observedAtUnixMs, long nowUnixMs)
        {
            var elapsedMs = Math.Max(0d, (double)nowUnixMs - observedAtUnixMs);
            if (elapsedMs < 1_000)
            {
                return "now";
            }
            if (elapsedMs < 60_000)
            {
                return $"{(long)(elapsedMs / 1_000)}s";
            }
            if (elapsedMs < 3_600_000)
            {
                return $"{(long)(elapsedMs / 60_000)}m";
            }
            if (elapsedMs < 86_400_000)
            {
                return $"{(long)(elapsedMs / 3_600_000)}h";
            }
            return $"{(long)(elapsedMs / 86_400_000)}d";
        }
    }
}
