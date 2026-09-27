using System;

namespace TrenchHQ.Core.Markets
{
    internal static class MarketReconnectRules
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15)
        ];

        internal static TimeSpan GetDelay(int failedAttempt)
        {
            var index = Math.Clamp(failedAttempt, 0, Delays.Length - 1);
            return Delays[index];
        }
    }
}
