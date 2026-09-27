using System;

namespace TrenchHQ.Core.Providers
{
    internal enum OnChainProviderFailureKind
    {
        Authentication,
        RateLimited,
        Transport
    }

    internal sealed class OnChainProviderResponseException(OnChainProviderFailureKind kind)
        : InvalidOperationException("The provider rejected a stream request.")
    {
        internal OnChainProviderFailureKind Kind { get; } = kind;
    }

    internal enum OnChainProviderFailoverOutcome
    {
        Ignored,
        Switched,
        Unavailable
    }

    internal sealed class OnChainProviderFailureEventArgs(
        string configurationId,
        OnChainProviderFailureKind kind) : EventArgs
    {
        internal string ConfigurationId { get; } = configurationId;
        internal OnChainProviderFailureKind Kind { get; } = kind;
    }

    internal sealed class OnChainProviderFailureCounter
    {
        private OnChainProviderFailureKind? _kind;
        private DateTimeOffset? _healthySince;
        internal int Count { get; private set; }

        internal void Reset()
        {
            Count = 0;
            _kind = null;
            _healthySince = null;
        }

        internal void RecordSuccess(DateTimeOffset now)
        {
            _healthySince ??= now;
            if (now - _healthySince >= TimeSpan.FromSeconds(30))
            {
                Count = 0;
                _kind = null;
            }
        }

        internal bool Record(OnChainProviderFailureKind kind)
        {
            _healthySince = null;
            Count = _kind == kind ? Math.Min(Count + 1, 30) : 1;
            _kind = kind;
            // Notify again after an ignored failure (for example, a machine-wide outage).
            return Count >= (kind == OnChainProviderFailureKind.Authentication ? 1
                : kind == OnChainProviderFailureKind.RateLimited ? 2 : 3);
        }
    }

    internal static class OnChainProviderFailoverRules
    {
        internal static bool ShouldAdvanceRoute(
            OnChainProviderFailureKind kind,
            bool hasInternetAccess) =>
            kind != OnChainProviderFailureKind.Transport || hasInternetAccess;

    }
}
