using System.Diagnostics;
using System.Reflection;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Infrastructure.OnChain;
using Xunit;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainIntegrationTests;

public partial class OnChainTests
{
    [Fact]
    public async Task CancelledPartialFrameClosesWorkerPipeAndAllowsRestart()
    {
        var baseline = GetEngineProcessIds();
        var client = new OnChainEngineClient(EnginePath);
        try
        {
            await client.SetRecoveryStateAsync(OnChainRecoveryState.Live);
            using var worker = Process.GetProcessById(GetSingleNewEngineProcessId(baseline));
            using var cancellation = new CancellationTokenSource();
            var inputField = typeof(OnChainEngineClient).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var input = (Stream)inputField.GetValue(client)!;
            using var interrupted = new CancelAfterHeaderStream(input, cancellation);
            inputField.SetValue(client, interrupted);

            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.SetRecoveryStateAsync(OnChainRecoveryState.Live, cancellation.Token));
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            // A new request must receive its own reply, not become the missing body of the old frame.
            await client.SetRecoveryStateAsync(OnChainRecoveryState.Live).WaitAsync(TimeSpan.FromSeconds(5));
            Xunit.Assert.Equal(OnChainEngineState.Ready, client.State);
        }
        finally
        {
            await client.StopAsync();
        }
        await WaitUntilAsync(() => !GetEngineProcessIds().Except(baseline).Any(),
            TimeSpan.FromSeconds(5), "interrupted-frame worker cleanup");
    }

    private sealed class CancelAfterHeaderStream(Stream inner, CancellationTokenSource cancellation) : Stream
    {
        private bool firstWrite = true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await inner.WriteAsync(buffer, token);
            if (firstWrite)
            {
                firstWrite = false;
                cancellation.Cancel();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
