using System;

namespace TrenchHQ.Core.Providers;

// Acquisition reports usage through this boundary; accounting and persistence
// belong to infrastructure, not to the saved provider model.
internal interface IOnChainUsageScope
{
    void QuotaExceeded(TimeSpan? retryAfter = null);
    void EnsureAvailable();
    void Rpc(string method, bool archive = false);
    void Grpc(int bytes);
    void WebSocket(byte[] message);
}
