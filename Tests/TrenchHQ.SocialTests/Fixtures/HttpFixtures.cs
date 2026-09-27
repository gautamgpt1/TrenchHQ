using TrenchHQ.Core.Social;
using TrenchHQ.Infrastructure.Social;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TrenchHQ.SocialTests;

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
}

sealed class WaitingStream : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
