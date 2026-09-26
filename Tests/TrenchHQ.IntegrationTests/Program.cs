using System.Collections.Concurrent;
using System.Diagnostics;
using TrenchHQ.Helpers;

var client = MarketSidecarClient.Current;
var observedStates = new ConcurrentQueue<MarketSidecarState>();
var priceCount = 0;

void OnStateChanged(object? sender, MarketSidecarStateChangedEventArgs args)
{
    observedStates.Enqueue(args.State);
}

void OnPriceUpdated(object? sender, MarketPriceUpdatedEventArgs args)
{
    if (string.Equals(args.Update.Market.Venue.Id, "test", StringComparison.OrdinalIgnoreCase)
        && string.Equals(args.Update.Market.Symbol, "BTC/USDT", StringComparison.OrdinalIgnoreCase))
    {
        Interlocked.Increment(ref priceCount);
    }
}

client.StateChanged += OnStateChanged;
client.PriceUpdated += OnPriceUpdated;

try
{
    await client.SetSubscriptionsAsync([]);
    AssertEqual(MarketSidecarState.Stopped, client.State, "An empty subscription set started the sidecar.");
    AssertEqual(0, GetTestSidecarIds().Length, "An empty subscription set launched a Node process.");

    await client.SetSubscriptionsAsync(
    [
        new MarketSidecarSubscription
        {
            VenueId = "test",
            Symbol = "BTC/USDT"
        }
    ]);

    await WaitUntilAsync(() => Volatile.Read(ref priceCount) == 1, TimeSpan.FromSeconds(5), "initial price");
    var processIds = new List<int> { GetSingleTestSidecarId() };
    await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => client.SetSubscriptionsAsync(
    [
        new MarketSidecarSubscription
        {
            VenueId = " TEST ",
            Symbol = "BTC/USDT"
        },
        new MarketSidecarSubscription
        {
            VenueId = "test",
            Symbol = "BTC/USDT"
        }
    ])));
    await Task.Delay(TimeSpan.FromSeconds(1));
    AssertEqual(1, Volatile.Read(ref priceCount),
        "Concurrent equivalent subscription sets restarted the market stream.");
    AssertEqual(processIds[0], GetSingleTestSidecarId(),
        "An equivalent subscription set replaced the sidecar process.");

    for (var crash = 0; crash < 5; crash++)
    {
        processIds.Add(await CrashAndVerifyRecoveryAsync(processIds[^1], expectedPriceCount: crash + 2));
    }

    AssertEqual(processIds.Count, processIds.Distinct().Count(),
        "Each recovery must use a replacement sidecar process.");
    Assert(observedStates.Count(state => state == MarketSidecarState.Reconnecting) >= 5,
        "Every forced crash must expose Reconnecting state.");

    await client.SetSubscriptionsAsync([]);
    using (var idleProcess = Process.GetProcessById(processIds[^1]))
    {
        idleProcess.Kill(entireProcessTree: true);
        await idleProcess.WaitForExitAsync();
    }
    await WaitUntilAsync(
        () => client.State == MarketSidecarState.Stopped,
        TimeSpan.FromSeconds(5),
        "idle sidecar stop state");
    await Task.Delay(TimeSpan.FromSeconds(2));
    AssertEqual(0, GetTestSidecarIds().Length,
        "A sidecar with no desired subscriptions restarted after a crash.");

    await client.StopAsync();
    await WaitUntilAsync(() => GetTestSidecarIds().Length == 0, TimeSpan.FromSeconds(5), "intentional sidecar stop");
    await Task.Delay(TimeSpan.FromSeconds(2));

    AssertEqual(MarketSidecarState.Stopped, client.State, "Intentional stop did not remain stopped.");
    AssertEqual(0, GetTestSidecarIds().Length, "Intentional stop unexpectedly restarted the sidecar.");
    AssertEqual(6, Volatile.Read(ref priceCount), "Recovery produced duplicate or missing price updates.");

    Console.WriteLine("PASS: sidecar starts lazily, ignores equivalent subscriptions, survives five crashes, restores once, and does not restart while idle or stopped.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: sidecar reconnect integration: {exception.Message}");
    return 1;
}
finally
{
    client.StateChanged -= OnStateChanged;
    client.PriceUpdated -= OnPriceUpdated;

    try
    {
        await client.StopAsync();
    }
    catch
    {
    }

    KillRemainingTestSidecars();
}

async Task<int> CrashAndVerifyRecoveryAsync(int processId, int expectedPriceCount)
{
    var stateCountBeforeCrash = observedStates.Count;
    var stopwatch = Stopwatch.StartNew();
    using (var process = Process.GetProcessById(processId))
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    await WaitUntilAsync(
        () => observedStates.Skip(stateCountBeforeCrash).Contains(MarketSidecarState.Reconnecting),
        TimeSpan.FromSeconds(5),
        "Reconnecting state");

    var replacementProcessId = 0;
    await WaitUntilAsync(() =>
    {
        replacementProcessId = GetTestSidecarIds().SingleOrDefault(id => id != processId);
        return replacementProcessId != 0;
    }, TimeSpan.FromSeconds(8), "replacement sidecar process");

    Assert(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800), "First reconnect attempt ignored its backoff delay.");
    await Task.Delay(TimeSpan.FromMilliseconds(250));
    AssertEqual(MarketSidecarState.Reconnecting, client.State, "Recovery reported Connected before a fresh price.");

    await WaitUntilAsync(
        () => Volatile.Read(ref priceCount) == expectedPriceCount,
        TimeSpan.FromSeconds(5),
        "restored subscription price");
    AssertEqual(MarketSidecarState.Connected, client.State, "Fresh price did not restore Connected state.");

    await Task.Delay(TimeSpan.FromSeconds(1));
    AssertEqual(expectedPriceCount, Volatile.Read(ref priceCount), "Subscription restoration produced duplicate prices.");
    return replacementProcessId;
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));
    }

    throw new TimeoutException($"Timed out waiting for {description}.");
}

static int GetSingleTestSidecarId()
{
    var processIds = GetTestSidecarIds();
    AssertEqual(1, processIds.Length, "Expected exactly one test sidecar process.");
    return processIds[0];
}

static int[] GetTestSidecarIds()
{
    var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SidecarRuntime", "node.exe"));
    var processIds = new List<int>();
    foreach (var process in Process.GetProcessesByName("node"))
    {
        using (process)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    processIds.Add(process.Id);
                }
            }
            catch
            {
            }
        }
    }

    return [.. processIds];
}

static void KillRemainingTestSidecars()
{
    foreach (var processId in GetTestSidecarIds())
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}
